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

    /// <summary>A Kokoro voice picked in the game's mod options. False when Kokoro isn't in use.</summary>
    public bool SetSpeaker(int id)
    {
        if (!UseKokoro) return false;
        kokoro!.Speaker = id;
        return true;
    }

    public void SetSpeed(float speed)
    {
        if (kokoro != null) kokoro.Speed = speed;
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
        if (UseKokoro) return kokoro!.SpeakAsync(Speakable(text));
        var tcs = new TaskCompletionSource<bool>();
        try
        {
            tts ??= new SpeechSynthesizer();
            tts.SpeakAsyncCancelAll();
            var prompt = new Prompt(Speakable(text));
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
    /// What is worth hearing: no markdown, no coordinates ("(130, 200)", "x120-150", "y 201"), units as words
    /// ("1000 W" is "1000 watts", not "W"), and optionally only the first few sentences.
    /// </summary>
    public static string Speakable(string s, int maxSentences = int.MaxValue)
    {
        static string R(string input, string pattern, string replacement) => System.Text.RegularExpressions.Regex.Replace(input, pattern, replacement);
        s = R(s, @"```[\s\S]*?```", " ");                                   // code blocks
        s = R(s, @"\(?\s*\b\d{1,3}\s*,\s*\d{1,3}\b\s*\)?", " ");            // (130, 200)
        s = R(s, @"\b[xy]\s?=?\s?\d{1,3}(\s?[-–]\s?\d{1,3})?\b", " ");      // x120-150, y 201
        s = R(s, @"\b(at|around|near)\s+(?=[,.;)]|\s*$)", " ");              // dangling "at" after removal
        s = R(s, @"[*_`#>|]", "");
        s = Units(s);
        s = R(s, @"\s+", " ").Trim();
        var sentences = System.Text.RegularExpressions.Regex.Split(s, @"(?<=[.!?])\s+");
        return string.Join(" ", sentences.Take(maxSentences));
    }

    // Units the game uses, longest first; each only right after a number ("112 g/s", "1.2kW", "30 °C").
    static readonly (string unit, string one, string many)[] UnitWords =
    {
        ("kDTU/s", "thousand DTU per second", "thousand DTU per second"), ("DTU/s", "DTU per second", "DTU per second"),
        ("kg/s", "kilogram per second", "kilograms per second"), ("mg/s", "milligram per second", "milligrams per second"),
        ("g/s", "gram per second", "grams per second"), ("kg/cycle", "kilogram per cycle", "kilograms per cycle"),
        ("g/cycle", "gram per cycle", "grams per cycle"), ("kcal/cycle", "kilocalorie per cycle", "kilocalories per cycle"),
        ("kcal", "kilocalorie", "kilocalories"), ("kJ", "kilojoule", "kilojoules"), ("kW", "kilowatt", "kilowatts"),
        ("W", "watt", "watts"), ("kg", "kilogram", "kilograms"), ("mg", "milligram", "milligrams"), ("g", "gram", "grams"),
        ("t", "tonne", "tonnes"), ("°C", "degree", "degrees"), ("°F", "degree Fahrenheit", "degrees Fahrenheit"),
        ("K", "kelvin", "kelvin"), ("%", "percent", "percent"), ("m", "metre", "metres"),
    };

    /// <summary>"1000 W" -> "1000 watts", "112 g/s" -> "112 grams per second", "~3" -> "about 3", "a → b" -> "a to b".</summary>
    internal static string Units(string s)
    {
        foreach (var (unit, one, many) in UnitWords)
            s = System.Text.RegularExpressions.Regex.Replace(s,
                @"(?<n>\d[\d,]*(?:\.\d+)?)\s?" + System.Text.RegularExpressions.Regex.Escape(unit) + @"(?![\p{L}\d/])",
                m => m.Groups["n"].Value + " " + (m.Groups["n"].Value == "1" ? one : many));
        s = System.Text.RegularExpressions.Regex.Replace(s, @"~\s?(?=\d)", "about ");
        s = s.Replace("→", " to ").Replace("≈", " about ").Replace("±", " plus or minus ");
        return s;
    }

    public void Dispose()
    {
        kokoro?.Dispose();
        try { tts?.Dispose(); } catch { }
    }
}
