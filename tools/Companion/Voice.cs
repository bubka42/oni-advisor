using System.Speech.Synthesis;

namespace OniAdvisor.Companion;

/// <summary>Reads replies aloud with Windows' built-in voices. (Speech input lives in SpeechInput.)</summary>
public sealed class Voice : IDisposable
{
    SpeechSynthesizer? tts;

    public string? Error { get; private set; }

    public void Speak(string text)
    {
        try
        {
            tts ??= new SpeechSynthesizer();
            tts.SpeakAsyncCancelAll();
            tts.SpeakAsync(StripMarkdown(text));
        }
        catch (Exception e) { Error = "speech output unavailable: " + e.Message; }
    }

    public void StopSpeaking()
    {
        try { tts?.SpeakAsyncCancelAll(); } catch { }
    }

    static string StripMarkdown(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"[*_`#>|]", "").Replace("  ", " ");

    public void Dispose()
    {
        try { tts?.Dispose(); } catch { }
    }
}
