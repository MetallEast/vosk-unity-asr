using System;
using Cysharp.Threading.Tasks;
using UnityEngine;

/// <summary>
/// Class that records audio and delivers frames for real-time audio processing
/// </summary>
public class VoiceProcessor : MonoBehaviour
{
    private const int SampleRate = 16000;
    private const int FrameLength = 512;

    /// <summary>
    /// Indicates whether microphone is capturing or not
    /// </summary>
    public bool IsRecording
        =>_audioClip != null && Microphone.IsRecording(CurrentDeviceName);

#if UNITY_EDITOR
    [SerializeField] private int MicrophoneIndex = 0;
#endif

    /// <summary>
    /// Event where frames of audio are delivered
    /// </summary>
    public event Action<short[]> OnFrameCaptured;

    /// <summary>
    /// Event when audio capture thread stops
    /// </summary>
    public event Action OnRecordingStop;

    /// <summary>
    /// Event when audio capture thread starts
    /// </summary>
    public event Action OnRecordingStart;

    /// <summary>
    /// Index of selected audio recording device
    /// </summary>
    public int CurrentDeviceIndex { get; private set; } = -1;

    /// <summary>
    /// Name of selected audio recording device
    /// </summary>
    public string CurrentDeviceName { get; private set; } = string.Empty;

    [Header("Voice Detection Settings")]
    [SerializeField, Tooltip("The minimum volume to detect voice input for"), Range(0.0f, 1.0f)]
    private float _minimumSpeakingSampleValue = 0.05f;

    [SerializeField, Tooltip("Time in seconds of detected silence before voice request is sent")]
    private float _silenceTimer = 1.0f;

    [SerializeField, Tooltip("Auto detect speech using the volume threshold.")]
    private bool _autoDetect;

    private float _timeAtSilenceBegan;
    private bool _audioDetected;
    private bool _didDetect;
    private bool _transmit;

    private AudioClip _audioClip;
    private event Action RestartRecording;

    /// <summary>
    /// Updates list of available audio devices
    /// </summary>
    public void UpdateDevices()
    {
#if UNITY_ANDROID || UNITY_IOS
        if (Microphone.devices.Length > 0)
        {
            CurrentDeviceName = Microphone.devices[0];
            CurrentDeviceIndex = 0;
        }
#elif UNITY_EDITOR
        if (Microphone.devices.Length > 0)
        {
            CurrentDeviceName = Microphone.devices[MicrophoneIndex];
            CurrentDeviceIndex = MicrophoneIndex;
        }
#endif
    }

    /// <summary>
    /// Start recording audio
    /// </summary>
    public async UniTask StartRecording()
    {
        if (IsRecording)
        {
            return;
        }

        _audioClip = Microphone.Start(CurrentDeviceName, true, 1, SampleRate);

        OnRecordingStart?.Invoke();

        await RecordDataAsync();
    }

    /// <summary>
    /// Stops recording audio
    /// </summary>
    public void StopRecording()
    {
        if (!IsRecording)
        {
            return;
        }

        Microphone.End(CurrentDeviceName);
        Destroy(_audioClip);

        _audioClip = null;
        _didDetect = false;
        _timeAtSilenceBegan = 0;
        _audioDetected = false;
        _transmit = false;
    }

    /// <summary>
    /// Loop for buffering incoming audio data and delivering frames
    /// </summary>
    private async UniTask RecordDataAsync()
    {
        float[] sampleBuffer = new float[FrameLength];
        int startReadPos = 0;

        while (IsRecording)
        {
            int curClipPos = Microphone.GetPosition(CurrentDeviceName);
            if (curClipPos < startReadPos)
                curClipPos += _audioClip.samples;

            int samplesAvailable = curClipPos - startReadPos;
            if (samplesAvailable < FrameLength)
            {
                await UniTask.Yield();
                continue;
            }

            int endReadPos = startReadPos + FrameLength;
            if (endReadPos > _audioClip.samples)
            {
                // fragmented read (wraps around to beginning of clip)
                // read bit at end of clip
                int numSamplesClipEnd = _audioClip.samples - startReadPos;
                float[] endClipSamples = new float[numSamplesClipEnd];
                _audioClip.GetData(endClipSamples, startReadPos);

                // read bit at start of clip
                int numSamplesClipStart = endReadPos - _audioClip.samples;
                float[] startClipSamples = new float[numSamplesClipStart];
                _audioClip.GetData(startClipSamples, 0);

                // combine to form full frame
                Buffer.BlockCopy(endClipSamples, 0, sampleBuffer, 0, numSamplesClipEnd);
                Buffer.BlockCopy(startClipSamples, 0, sampleBuffer, numSamplesClipEnd, numSamplesClipStart);
            }
            else
            {
                _audioClip.GetData(sampleBuffer, startReadPos);
            }

            startReadPos = endReadPos % _audioClip.samples;

            if (!_autoDetect)
            {
                _transmit = _audioDetected = true;
            }
            else
            {
                float maxVolume = 0.0f;

                for (int i = 0; i < sampleBuffer.Length; i++)
                {
                    if (sampleBuffer[i] > maxVolume)
                    {
                        maxVolume = sampleBuffer[i];
                    }
                }

                if (maxVolume >= _minimumSpeakingSampleValue)
                {
                    _transmit = _audioDetected = true;
                    _timeAtSilenceBegan = Time.time;
                }
                else
                {
                    _transmit = false;

                    if (_audioDetected && Time.time - _timeAtSilenceBegan > _silenceTimer)
                    {
                        _audioDetected = false;
                    }
                }
            }

            if (_audioDetected)
            {
                _didDetect = true;
                // converts to 16-bit int samples
                var pcmBuffer = new short[sampleBuffer.Length];
                for (var i = 0; i < FrameLength; i++)
                {
                    pcmBuffer[i] = (short)Math.Floor(sampleBuffer[i] * short.MaxValue);
                }

                // raise buffer event
                if (_transmit)
                {
                    //var processedBuffer = ApplyNoiseGate(pcmBuffer);

                    OnFrameCaptured?.Invoke(pcmBuffer);
                }
            }
            else
            {
                if (_didDetect)
                {
                    Debug.Log("Stop recording");
                    OnRecordingStop?.Invoke();
                    _didDetect = false;
                }
            }
        }

        Debug.Log("Stop recording");
        OnRecordingStop?.Invoke();
        RestartRecording?.Invoke();
    }

    private short[] ApplyNoiseGate(short[] input, float threshold = 0.005f)
    {
        var output = new short[input.Length];

        for (var i = 0; i < input.Length; i++)
        {
            var normalized = input[i] / (float)short.MaxValue;

            output[i] = Mathf.Abs(normalized) > threshold ? input[i] : (short)0;
        }

        return output;
    }
}