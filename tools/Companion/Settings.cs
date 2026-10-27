using System.Text.Json;
using System.Text.Json.Serialization;

namespace OniAdvisor.Companion;

/// <summary>Companion settings, stored in advisor/companion.json next to the mod's config.json.</summary>
public sealed class Settings
{
    public string? SessionId { get; set; }
    public string? ClaudePath { get; set; }          // empty = auto-detect
    public string? Model { get; set; }               // empty = Claude Code default
    public string? MapQueryDll { get; set; }         // empty = auto-detect next to this app's repo
    public bool AutoBriefOnAutosave { get; set; } = true;
    public bool AutoPodPick { get; set; } = true;
    public bool AutoReviewPlan { get; set; } = true;
    public bool AutoLookOnHotkey { get; set; } = true;
    public bool Speak { get; set; } = false;          // read live answers aloud
    public bool SpeakBriefings { get; set; } = true;  // read briefing steps aloud (the game waits for each)
    public bool VoiceAutoSend { get; set; } = false;
    public string? WhisperModel { get; set; }   // empty = %USERPROFILE%\.oni-advisor\models\ggml-small.en.bin
    public string? MicDevice { get; set; }      // part of the microphone's name; empty = "Microphone Array"   // false: dictated text goes into the input box to check first
    public string? LiveModel { get; set; } = "sonnet";  // live answers: sonnet (~2.5 s) or haiku (~1.8 s)
    public string? LiveEffort { get; set; } = "low";
    public bool TopMost { get; set; } = true;
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

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json)); } catch { }
    }
}
