using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Cysharp.Threading.Tasks;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using Vosk;

public class VoskSpeechToText : MonoBehaviour
{
    private static readonly ProfilerMarker voskRecognizerCreateMarker = new("VoskRecognizer.Create");
    private static readonly ProfilerMarker voskRecognizerReadMarker = new("VoskRecognizer.AcceptWaveform");

    public Action<string> OnTranscriptionResult;

    [Tooltip("The source of the microphone input.")]
    [SerializeField] private VoiceProcessor _voiceProcessor;

    [Tooltip("The Max number of alternatives that will be processed.")]
    [SerializeField] private int _maxAlternatives = 3;

    [Tooltip("How long should we record before restarting?")]
    [SerializeField] private float _maxRecordLength = 5;

    [Tooltip("The phrases that will be detected. If left empty, all words will be detected.")]
    [SerializeField] private List<string> _keyPhrases;

    private Model _model;
    private VoskRecognizer _recognizer;

    private string _modelName;
    private string _decompressedModelPath;
    private string _grammar = string.Empty;

    private bool _isInitializing;
    private bool _didInit;
    private bool _running;
    private bool _recognizerReady;

    private readonly ConcurrentQueue<short[]> _threadedBufferQueue = new();
    private readonly ConcurrentQueue<string> _threadedResultQueue = new();

    private void Update()
    {
        if (_threadedResultQueue.TryDequeue(out string voiceResult))
        {
            OnTranscriptionResult?.Invoke(voiceResult);
        }
    }

    public async UniTask<bool> Initialize(string modelName, List<string> keyPhrases = null)
    {
        if (_isInitializing || _didInit)
        {
            Debug.LogError("Initializing in progress or already initialized");
            return false;
        }

        _isInitializing = true;

        _modelName = modelName;
        _keyPhrases = keyPhrases;
        _decompressedModelPath = Path.Combine(Application.persistentDataPath, _modelName);

        await LoadModel();

        Debug.Log("Loading Model from: " + _decompressedModelPath);
        _model = new Model(_decompressedModelPath);

        await UniTask.Yield();

        _voiceProcessor.OnFrameCaptured += VoiceProcessorOnOnFrameCaptured;
        _voiceProcessor.OnRecordingStop += VoiceProcessorOnOnRecordingStop;

        _isInitializing = false;
        _didInit = true;

        Debug.Log("Initialized");

        return true;
    }

    private async UniTask<bool> LoadModel()
    {
        if (Directory.Exists(_decompressedModelPath))
        {
            // TODO: validate inner files (external files manipulations etc.)
            Debug.Log($"Using existing decompressed model: {_decompressedModelPath}");
            return true;
        }

        // NOTE: best place to show download suggestion window before going forward

        return await DownloadModel();
    }

    private async UniTask<bool> DownloadModel()
    {
        Debug.Log($"Downloading model from Addressables {_modelName}");

        AsyncOperationHandle<TextAsset> handle = new();

        try
        {
            handle = Addressables.LoadAssetAsync<TextAsset>(_modelName);

            await handle.ToUniTask();

            if (handle.Status != AsyncOperationStatus.Succeeded)
            {
                Debug.LogError("Failed to load model from Addressables.");
                return false;
            }

            var zipData = handle.Result.bytes;
            var tempZipPath = Path.Combine(Application.temporaryCachePath, "temp.zip");

            File.WriteAllBytes(tempZipPath, zipData);

            Debug.Log("Extracting zip file...");
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZipPath, Application.persistentDataPath, true);

            Debug.Log($"Decompression complete: {_decompressedModelPath}");

            if (File.Exists(tempZipPath))
            {
                File.Delete(tempZipPath);
                Debug.Log($"Deleted temp zip file: {tempZipPath}");
            }

            Debug.Log("Loading Model from: " + _decompressedModelPath);
            Vosk.Vosk.SetLogLevel(0);

            _model = new Model(_decompressedModelPath);
            Debug.Log($"Model loaded: {_model != null}");

            UpdateGrammar();

            _recognizer = string.IsNullOrEmpty(_grammar)
                ? new (_model, 16000.0f)
                : new (_model, 16000.0f, _grammar);

            if (_recognizer == null)
            {
                Debug.LogError("Failed to create initial VoskRecognizer!");
                return false;
            }

            _recognizer.SetMaxAlternatives(_maxAlternatives);
            _recognizer.SetWords(true);
            _recognizerReady = true;
            Debug.Log("Initial VoskRecognizer created and ready");

            _voiceProcessor.OnFrameCaptured += VoiceProcessorOnOnFrameCaptured;
            _voiceProcessor.OnRecordingStop += VoiceProcessorOnOnRecordingStop;

            _didInit = true;
            Debug.Log("Vosk initialization complete");

            return true;
        }
        catch (Exception exc)
        {
            Debug.LogError($"DownloadModel failed: {exc.Message}");
            return false;
        }
        finally
        {
            _isInitializing = false;

            Addressables.Release(handle);
            Debug.Log("Addressables handle released");
        }
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

            _recognizer.SetMaxAlternatives(_maxAlternatives);
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

    private void UpdateGrammar()
    {
        if (_keyPhrases == null || _keyPhrases.Count == 0)
        {
            _grammar = string.Empty;

            return;
        }

        var keywords = new JSONArray();

        foreach (var keyphrase in _keyPhrases)
        {
            keywords.Add(new JSONString(keyphrase.ToLower()));
        }

        keywords.Add(new JSONString("[unk]"));
        _grammar = keywords.ToString();
    }

    public void ToggleRecording()
    {
        Debug.Log("Toggle Recording");
        if (!_voiceProcessor.IsRecording)
        {
            Debug.Log("Start Recording");
            _running = true;
            _voiceProcessor.StartRecording().Forget();
            ThreadedWork().Forget();
        }
        else
        {
            Debug.Log("Stop Recording");
            _running = false;
            _voiceProcessor.StopRecording();
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
}