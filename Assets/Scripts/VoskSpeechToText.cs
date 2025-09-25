using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Ionic.Zip;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Networking;
using Vosk;
using Cysharp.Threading.Tasks;

public class VoskSpeechToText : MonoBehaviour
{
    [Tooltip("Location of the model, relative to the Streaming Assets folder.")]
    public string ModelPath = "vosk-model-small-ru-0.22.zip";

    [Tooltip("The source of the microphone input.")]
    public VoiceProcessor VoiceProcessor;

    [Tooltip("The Max number of alternatives that will be processed.")]
    public int MaxAlternatives = 3;

    [Tooltip("How long should we record before restarting?")]
    public float MaxRecordLength = 5;

    [Tooltip("Should the recognizer start when the application is launched?")]
    public bool AutoStart = true;

    [Tooltip("The phrases that will be detected. If left empty, all words will be detected.")]
    public List<string> KeyPhrases = new List<string>();

    private Model _model;
    private VoskRecognizer _recognizer;
    private bool _recognizerReady;

    private readonly List<short> _buffer = new List<short>();
    public Action<string> OnStatusUpdated;
    public Action<string> OnTranscriptionResult;

    private string _decompressedModelPath;
    private string _grammar = "";

    private bool _isDecompressing;
    private bool _isInitializing;
    private bool _didInit;
    private bool _running;

    private readonly ConcurrentQueue<short[]> _threadedBufferQueue = new ConcurrentQueue<short[]>();
    private readonly ConcurrentQueue<string> _threadedResultQueue = new ConcurrentQueue<string>();

    static readonly ProfilerMarker voskRecognizerCreateMarker = new ProfilerMarker("VoskRecognizer.Create");
    static readonly ProfilerMarker voskRecognizerReadMarker = new ProfilerMarker("VoskRecognizer.AcceptWaveform");

    void Start()
    {
        if (AutoStart)
        {
            StartVoskStt().Forget();
        }
    }

    public async UniTaskVoid StartVoskStt(List<string> keyPhrases = null, string modelPath = default, bool startMicrophone = false, int maxAlternatives = 3)
    {
        if (_isInitializing)
        {
            Debug.LogError("Initializing in progress!");
            return;
        }
        if (_didInit)
        {
            Debug.LogError("Vosk has already been initialized!");
            return;
        }

        if (!string.IsNullOrEmpty(modelPath))
        {
            ModelPath = modelPath;
        }

        if (keyPhrases != null)
        {
            KeyPhrases = keyPhrases;
        }

        MaxAlternatives = maxAlternatives;
        await DoStartVoskStt(startMicrophone);
    }

    private async UniTask DoStartVoskStt(bool startMicrophone)
    {
        _isInitializing = true;
        await WaitForMicrophoneInput();
        await Decompress();

        OnStatusUpdated?.Invoke("Loading Model from: " + _decompressedModelPath);
        _model = new Model(_decompressedModelPath);

        await UniTask.Yield();

        OnStatusUpdated?.Invoke("Initialized");
        VoiceProcessor.OnFrameCaptured += VoiceProcessorOnOnFrameCaptured;
        VoiceProcessor.OnRecordingStop += VoiceProcessorOnOnRecordingStop;

        if (startMicrophone)
            VoiceProcessor.StartRecording();

        _isInitializing = false;
        _didInit = true;

        ToggleRecording();
    }

    private void UpdateGrammar()
    {
        if (KeyPhrases.Count == 0)
        {
            _grammar = "";
            return;
        }

        JSONArray keywords = new JSONArray();
        foreach (string keyphrase in KeyPhrases)
        {
            keywords.Add(new JSONString(keyphrase.ToLower()));
        }

        keywords.Add(new JSONString("[unk]"));
        _grammar = keywords.ToString();
    }

    private async UniTask Decompress()
    {
        if (!Path.HasExtension(ModelPath)
            || Directory.Exists(Path.Combine(Application.persistentDataPath, Path.GetFileNameWithoutExtension(ModelPath))))
        {
            OnStatusUpdated?.Invoke("Using existing decompressed model.");
            _decompressedModelPath = Path.Combine(Application.persistentDataPath, Path.GetFileNameWithoutExtension(ModelPath));
            Debug.Log(_decompressedModelPath);
            return;
        }

        OnStatusUpdated?.Invoke("Decompressing model...");
        string dataPath = Path.Combine(Application.streamingAssetsPath, ModelPath);

        Stream dataStream;
        if (dataPath.Contains("://"))
        {
            UnityWebRequest www = UnityWebRequest.Get(dataPath);
            await www.SendWebRequest();
            dataStream = new MemoryStream(www.downloadHandler.data);
        }
        else
        {
            dataStream = File.OpenRead(dataPath);
        }

        var zipFile = ZipFile.Read(dataStream);
        zipFile.ExtractProgress += ZipFileOnExtractProgress;
        OnStatusUpdated?.Invoke("Reading Zip file");
        zipFile.ExtractAll(Application.persistentDataPath);

        await UniTask.WaitUntil(() => _isDecompressing);

        _decompressedModelPath = Path.Combine(Application.persistentDataPath, Path.GetFileNameWithoutExtension(ModelPath));
        OnStatusUpdated?.Invoke("Decompressing complete!");

        await UniTask.Delay(1000);
        zipFile.Dispose();
    }

    private void ZipFileOnExtractProgress(object sender, ExtractProgressEventArgs e)
    {
        if (e.EventType == ZipProgressEventType.Extracting_AfterExtractAll)
        {
            _isDecompressing = true;
            _decompressedModelPath = e.ExtractLocation;
        }
    }

    private async UniTask WaitForMicrophoneInput()
    {
        await UniTask.WaitUntil(() => Microphone.devices.Length > 0);
    }

    public void ToggleRecording()
    {
        Debug.Log("Toggle Recording");
        if (!VoiceProcessor.IsRecording)
        {
            Debug.Log("Start Recording");
            _running = true;
            VoiceProcessor.StartRecording();
            ThreadedWork().Forget();
        }
        else
        {
            Debug.Log("Stop Recording");
            _running = false;
            VoiceProcessor.StopRecording();
        }
    }

    void Update()
    {
        if (_threadedResultQueue.TryDequeue(out string voiceResult))
        {
            OnTranscriptionResult?.Invoke(voiceResult);
        }
    }

    private void VoiceProcessorOnOnFrameCaptured(short[] samples)
    {
        _threadedBufferQueue.Enqueue(samples);
    }

    private void VoiceProcessorOnOnRecordingStop()
    {
        Debug.Log("Stopped");
    }

    private async UniTaskVoid ThreadedWork()
    {
        voskRecognizerCreateMarker.Begin();
        if (!_recognizerReady)
        {
            UpdateGrammar();
            if (string.IsNullOrEmpty(_grammar))
            {
                _recognizer = new VoskRecognizer(_model, 16000.0f);
            }
            else
            {
                _recognizer = new VoskRecognizer(_model, 16000.0f, _grammar);
            }

            _recognizer.SetMaxAlternatives(MaxAlternatives);
            _recognizerReady = true;
            Debug.Log("Recognizer ready");
        }
        voskRecognizerCreateMarker.End();

        voskRecognizerReadMarker.Begin();
        while (_running)
        {
            if (_threadedBufferQueue.TryDequeue(out short[] voiceResult))
            {
                if (_recognizer.AcceptWaveform(voiceResult, voiceResult.Length))
                {
                    var result = _recognizer.Result();
                    _threadedResultQueue.Enqueue(result);
                }
            }
            else
            {
                await UniTask.Delay(100);
            }
        }
        voskRecognizerReadMarker.End();
    }
}
