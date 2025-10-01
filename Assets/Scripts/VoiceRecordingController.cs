using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Android;

[Serializable]
public class LanguageModelPair
{
    [field: SerializeField] public SystemLanguage LanguageCode { get; private set; }
    [field: SerializeField] public string ModelName { get; private set; }
}

public class VoiceRecordingController : MonoBehaviour
{
    public static VoiceRecordingController Instantce { get; private set; }

    public event Action<bool> RecordingStateChanged;

    public bool IsInitialized { get; private set; }

    [SerializeField] private VoskSpeechToText _voskSpeechToText;
    [SerializeField] private VoiceProcessor _voiceProcessor;
    [SerializeField] private SystemLanguage _defaultLanguage = SystemLanguage.English;
    [SerializeField] private LanguageModelPair[] _voiceModelsMap;

    private TMP_Text _currentTextTarget;
    private bool _initializationInProgress;

    private void Awake()
    {
        if (Instantce == null)
        {
            Instantce = this;

            _voskSpeechToText.OnTranscriptionResult += OnTranscriptionResult;
            _voiceProcessor.OnRecordingStart += OnRecordingStart;
            _voiceProcessor.OnRecordingStop += OnRecordingStop;
        }
        else
        {
            Destroy(this);
            Debug.LogWarning("Duplicate VoiceRecordingController destroyed");
        }
    }

    private void OnDestroy()
    {
        _voskSpeechToText.OnTranscriptionResult -= OnTranscriptionResult;
        _voiceProcessor.OnRecordingStart -= OnRecordingStart;
        _voiceProcessor.OnRecordingStop -= OnRecordingStop;
    }

    public async UniTask<bool> TryInitialize()
    {
        Debug.Log("TryInitialize started");
        try
        {
#if UNITY_ANDROID || UNITY_IOS
            Debug.Log($"Microphone permission: {Permission.HasUserAuthorizedPermission(Permission.Microphone)}");
            if (!Permission.HasUserAuthorizedPermission(Permission.Microphone))
            {
                Debug.Log("Microphone permission requested");
                Permission.RequestUserPermission(Permission.Microphone);

                return false;
            }
#endif
            if (IsInitialized || _initializationInProgress)
            {
                Debug.LogWarning($"Initialization skipped. IsInitialized: {IsInitialized}, InitializationInProgress: {_initializationInProgress}");
                return false;
            }

            IsInitialized = false;
            _initializationInProgress = true;

            if (!await TryValidateDevices())
            {
                return false;
            }

            if (!TryGetVoiceModelName(out var modelName))
            {
                Debug.LogError("Cannot determine model name");
                return false;
            }

            if (!await _voskSpeechToText.Initialize(modelName))
            {
                return false;
            }

            IsInitialized = true;

            Debug.Log("Initialization complete");

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Exception during voice recording initialization: {ex.Message}");
            return false;
        }
        finally
        {
            _initializationInProgress = false;
            Debug.Log($"TryInitialize finished, IsInitialized: {IsInitialized}");
        }
    }

    public bool TryToggleRecording(TMP_Text targetText, out bool newState)
    {
        newState = default;

        if (!IsInitialized)
        {
            TryInitialize().Forget();
            return false;
        }

        if (_initializationInProgress)
        {
            Debug.LogWarning("Cannot toggle recording: initialization in progress");
            return false;
        }

        _currentTextTarget = targetText;

        _voskSpeechToText.ToggleRecording();

        newState = _voiceProcessor.IsRecording;

        return true;
    }

    public void StopRecordingIfRunning()
    {
        _voiceProcessor.StopRecording();
    }

    private async UniTask<bool> TryValidateDevices()
    {
        if (_voiceProcessor.CurrentDeviceIndex > -1)
        {
            Debug.Log($"Valid device found: {_voiceProcessor.CurrentDeviceIndex}");
            return true;
        }

        var attempts = 0;

        while (_voiceProcessor.CurrentDeviceIndex == -1 || attempts < 3)
        {
            _voiceProcessor.UpdateDevices();

            await UniTask.Delay(500);

            if (_voiceProcessor.CurrentDeviceIndex > -1)
            {
                Debug.LogWarning($"Valid device found, CurrentDeviceIndex: {_voiceProcessor.CurrentDeviceIndex}");
                return true;
            }

            attempts++;
            Debug.LogWarning($"No valid device, attempt {attempts + 1}/3");

        }

        Debug.LogError("Failed to find valid device after 3 attempts");
        return false;
    }

    private bool TryGetVoiceModelName(out string modelName)
    {
        var systemLanguage = Application.systemLanguage;

        modelName = _voiceModelsMap.FirstOrDefault(m => m.LanguageCode == systemLanguage).ModelName;

        if (modelName == null)
        {
            modelName = _voiceModelsMap.FirstOrDefault(m => m.LanguageCode == _defaultLanguage).ModelName;

            if (modelName == null)
            {
                Debug.LogError("Cannot determine model name");
                return false;
            }
        }

        return true;
    }

    private void OnTranscriptionResult(string obj)
    {
        Debug.Log($"OnTranscriptionResult: {obj}");
        if (_currentTextTarget == null)
        {
            Debug.LogWarning("No valid target input field, stopping recording");
            StopRecordingIfRunning();
            return;
        }

        var result = new RecognitionResult(obj);

        if (result.Phrases.Length > 0)
        {
            var mostRelevant = result.Phrases.OrderByDescending(p => p.Confidence)
                .FirstOrDefault();

            _currentTextTarget.text += $"{mostRelevant.Text} ";
        }
    }

    private void OnRecordingStart()
    {
        Debug.Log("Start recording");
        RecordingStateChanged?.Invoke(true);
    }

    private void OnRecordingStop()
    {
        Debug.Log("Stop recording");
        RecordingStateChanged?.Invoke(false);
    }
}