using TMPro;
using UnityEngine;

public class VoskResultText : MonoBehaviour 
{
    public VoskSpeechToText VoskSpeechToText;
    public TMP_Text ResultText;

    private void Awake()
    {
        VoskSpeechToText.OnTranscriptionResult += OnTranscriptionResult;
    }

    private void OnTranscriptionResult(string obj)
    {
        Debug.Log(obj);
        var result = new RecognitionResult(obj);
        for (int i = 0; i < result.Phrases.Length; i++)
        {
            if (i > 0)
            {
                ResultText.text += ", ";
            }

            ResultText.text += result.Phrases[i].Text;
        }
    	ResultText.text += "\n";
    }
}