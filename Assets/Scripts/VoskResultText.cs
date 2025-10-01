using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class VoskResultText : MonoBehaviour 
{
    [SerializeField] private Button _voiceButton;
    [SerializeField] private TMP_Text _text;

    private void OnEnable()
    {
        _voiceButton.onClick.AddListener(OnClicked);
    }

    private void OnDisable()
    {
        _voiceButton.onClick.RemoveListener(OnClicked);
    }

    private void OnClicked()
    {
        if (VoiceRecordingController.Instantce.TryToggleRecording(_text, out bool newState))
        {
            // NOTE: Best place for voice button idle/active effects
        }
    }
}