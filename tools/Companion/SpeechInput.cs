using System.Text;
using NAudio.Wave;
using Whisper.net;

namespace OniAdvisor.Companion;

/// <summary>
/// Push-to-talk speech recognition: records from the microphone while the key is held, then transcribes
/// locally with Whisper (ggml-small.en, stored outside OneDrive in %USERPROFILE%\.oni-advisor\models).
/// The prompt primes Whisper with colony vocabulary (duplicant names, buildings) so game words come out right.
/// </summary>
public sealed class SpeechInput : IDisposable
{
    public static string DefaultModelPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".oni-advisor", "models", "ggml-small.en.bin");

    readonly string modelPath;
    readonly string micHint;
    WhisperFactory? factory;
    WaveInEvent? wave;
    readonly List<float> samples = new();

    public string? Error { get; private set; }
    public bool Recording => wave != null;

    public SpeechInput(string? modelPath, string? micHint)
    {
        this.modelPath = string.IsNullOrWhiteSpace(modelPath) ? DefaultModelPath : modelPath!;
        this.micHint = string.IsNullOrWhiteSpace(micHint) ? "Microphone Array" : micHint!;
    }

    /// <summary>Picks the microphone whose name contains the hint (default "Microphone Array"), else the first one.</summary>
    int PickDevice(out string name)
    {
        name = "";
        int count = WaveInEvent.DeviceCount;
        for (int i = 0; i < count; i++)
        {
            var n = WaveInEvent.GetCapabilities(i).ProductName;
            if (n.Contains(micHint, StringComparison.OrdinalIgnoreCase)) { name = n; return i; }
        }
        if (count > 0) { name = WaveInEvent.GetCapabilities(0).ProductName; return 0; }
        return -1;
    }

    public bool Start(out string micName)
    {
        micName = "";
        Error = null;
        if (wave != null) return true;
        try
        {
            int dev = PickDevice(out micName);
            if (dev < 0) { Error = "no microphone found"; return false; }
            lock (samples) samples.Clear();
            wave = new WaveInEvent { DeviceNumber = dev, WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 50 };
            wave.DataAvailable += (_, e) =>
            {
                lock (samples)
                    for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
                        samples.Add(BitConverter.ToInt16(e.Buffer, i) / 32768f);
            };
            wave.StartRecording();
            return true;
        }
        catch (Exception e) { Error = "microphone error: " + e.Message; StopDevice(); return false; }
    }

    /// <summary>Stops recording and returns the audio (16 kHz mono floats).</summary>
    public float[] Stop()
    {
        StopDevice();
        lock (samples) return samples.ToArray();
    }

    void StopDevice()
    {
        try { wave?.StopRecording(); } catch { }
        try { wave?.Dispose(); } catch { }
        wave = null;
    }

    public async Task<string> TranscribeAsync(float[] audio, string vocabularyPrompt, CancellationToken ct)
    {
        Error = null;
        if (audio.Length < 16000 / 4) return ""; // under a quarter second: nothing said
        if (!File.Exists(modelPath)) { Error = "speech model not found: " + modelPath; return ""; }
        try
        {
            factory ??= WhisperFactory.FromPath(modelPath);
            // Whisper wants at least one second of audio; pad short clips with silence.
            if (audio.Length < 16000) Array.Resize(ref audio, 16000);
            await using var processor = factory.CreateBuilder()
                .WithLanguage("en")
                .WithPrompt(vocabularyPrompt)
                .WithThreads(Math.Max(2, Environment.ProcessorCount / 2))
                .Build();
            var sb = new StringBuilder();
            await foreach (var seg in processor.ProcessAsync(audio, ct)) sb.Append(seg.Text);
            return Clean(sb.ToString());
        }
        catch (Exception e) { Error = "speech recognition failed: " + e.Message; return ""; }
    }

    /// <summary>Drops Whisper's non-speech markers like [BLANK_AUDIO] or (music).</summary>
    static string Clean(string s)
    {
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\[[^\]]*\]|\([^)]*\)", " ");
        return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
    }

    public void Dispose()
    {
        StopDevice();
        factory?.Dispose();
    }
}
