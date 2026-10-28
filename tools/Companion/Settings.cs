using System.Text.Json;
using System.Text.Json.Serialization;

namespace OniAdvisor.Companion;

/// <summary>Companion settings, stored in advisor/companion.json next to the mod's config.json.</summary>
public sealed class Settings
{
    public string? SessionId { get; set; }
    public string? ClaudePath { get; set; }          // empty = auto-detect
    public string? Model { get; set; } = "sonnet";   // the advisor's model: sonnet (answers in ~5-15 s) or opus (deeper, slower)
    public string? Effort { get; set; } = "medium";  // low | medium | high
    public string Proactive { get; set; } = "freely"; // never | serious | freely: may the advisor speak up unasked?
    public int ProactiveMinSeconds { get; set; } = 120; // at least this long between unasked comments
    public int PlanReviewMinutes { get; set; } = 12;    // when calm, look ahead and advance the long-term plan this often
    public string? MapQueryDll { get; set; }         // empty = auto-detect next to this app's repo
    public bool AutoBriefOnAutosave { get; set; } = true;
    public bool AutoPodPick { get; set; } = true;
    public bool AutoReviewPlan { get; set; } = true;
    public bool AutoLookOnHotkey { get; set; } = true;
    public bool Speak { get; set; } = false;          // read live answers aloud
    public bool SpeakBriefings { get; set; } = true;  // read briefing steps aloud (the game waits for each)
    public bool VoiceAutoSend { get; set; } = false;
    public string TtsEngine { get; set; } = "kokoro";  // kokoro (natural, offline; falls back to windows) | windows
    public string? TtsModelDir { get; set; }           // empty = best installed model under %USERPROFILE%\.oni-advisor\models
    public int? TtsSpeaker { get; set; }               // Kokoro voice id; empty = a good English voice for the model
    public float TtsSpeed { get; set; } = 1.0f;
    public string? WhisperModel { get; set; }   // empty = %USERPROFILE%\.oni-advisor\models\ggml-small.en.bin
    public string? MicDevice { get; set; }      // part of the microphone's name; empty = "Microphone Array"   // false: dictated text goes into the input box to check first
    public bool TopMost { get; set; } = true;
    public bool AutoStart { get; set; } = true;       // the mod starts this app in the background with the game
    public int TimeoutSeconds { get; set; } = 300;

    [JsonIgnore] public string FilePath { get; private set; } = "";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load(string path)
    {
        Settings s;
        try { s = File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new() : new(); }
        catch { s = new(); }
        s.FilePath = path;
        return s;
    }

    /// <summary>Re-reads the file into this instance (others hold a reference to it): the game's mod options edit it.</summary>
    public void Reload()
    {
        var fresh = Load(FilePath);
        foreach (var p in typeof(Settings).GetProperties())
            if (p.CanWrite && p.GetCustomAttributes(typeof(JsonIgnoreAttribute), false).Length == 0) p.SetValue(this, p.GetValue(fresh));
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); } catch { }
    }
}
