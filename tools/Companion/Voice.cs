using System.Speech.Synthesis;

namespace OniAdvisor.Companion;

/// <summary>
/// Reads replies aloud: with a natural Kokoro voice when its model is installed (KokoroVoice), otherwise with
/// Windows' built-in voices. (Speech input lives in SpeechInput.)
/// </summary>
public sealed class Voice : IDisposable
{
    SpeechSynthesizer? tts;
    KokoroVoice? kokoro;

    public string? Error { get; private set; }
    public string Engine => UseKokoro ? "Kokoro (" + kokoro!.Description + ")" : "Windows voice";

    /// <summary>Picks the engine from the settings and loads Kokoro in the background.</summary>
    public void Configure(Settings settings)
    {
        if (settings.TtsEngine == "windows") return;
        kokoro = KokoroVoice.TryCreate(settings.TtsModelDir, settings.TtsSpeaker, settings.TtsSpeed);
        if (kokoro != null) _ = Task.Run(kokoro.Load);
    }

    bool UseKokoro => kokoro != null && kokoro.Error == null;

    public void Speak(string text)
    {
        if (UseKokoro) { _ = kokoro!.SpeakAsync(Speakable(text)); return; }
        try
        {
            tts ??= new SpeechSynthesizer();
            tts.SpeakAsyncCancelAll();
            tts.SpeakAsync(Speakable(text));
        }
        catch (Exception e) { Error = "speech output unavailable: " + e.Message; }
    }

    /// <summary>Speaks and completes when finished (or cancelled). Returns false if speech is unavailable.</summary>
    public Task<bool> SpeakAndWaitAsync(string text)
    {
        if (UseKokoro) return kokoro!.SpeakAsync(Speakable(text, maxSentences: 4));
        var tcs = new TaskCompletionSource<bool>();
        try
        {
            tts ??= new SpeechSynthesizer();
            tts.SpeakAsyncCancelAll();
            var prompt = new Prompt(Speakable(text, maxSentences: 4));
            void Done(object? s, SpeakCompletedEventArgs e)
            {
                if (e.Prompt != prompt) return;
                tts!.SpeakCompleted -= Done;
                tcs.TrySetResult(!e.Cancelled);
            }
            tts.SpeakCompleted += Done;
            tts.SpeakAsync(prompt);
        }
        catch (Exception e) { Error = "speech output unavailable: " + e.Message; tcs.TrySetResult(false); }
        return tcs.Task;
    }

    public void StopSpeaking()
    {
        kokoro?.Stop();
        try { tts?.SpeakAsyncCancelAll(); } catch { }
    }

    /// <summary>
    /// What is worth hearing: no markdown, no coordinates ("(130, 200)", "x120-150", "y 201"), and at most
    /// the first few sentences - anything longer belongs on screen, not in your ears.
    /// </summary>
    public static string Speakable(string s, int maxSentences = 3)
    {
        static string R(string input, string pattern, string replacement) => System.Text.RegularExpressions.Regex.Replace(input, pattern, replacement);
        s = R(s, @"```[\s\S]*?```", " ");                                   // code blocks
        s = R(s, @"\(?\s*\b\d{1,3}\s*,\s*\d{1,3}\b\s*\)?", " ");            // (130, 200)
        s = R(s, @"\b[xy]\s?=?\s?\d{1,3}(\s?[-–]\s?\d{1,3})?\b", " ");      // x120-150, y 201
        s = R(s, @"\b(at|around|near)\s+(?=[,.;)]|\s*$)", " ");              // dangling "at" after removal
        s = R(s, @"[*_`#>|]", "");
        s = R(s, @"\s+", " ").Trim();
        var sentences = System.Text.RegularExpressions.Regex.Split(s, @"(?<=[.!?])\s+");
        return string.Join(" ", sentences.Take(maxSentences));
    }

    public void Dispose()
    {
        kokoro?.Dispose();
        try { tts?.Dispose(); } catch { }
    }
}
