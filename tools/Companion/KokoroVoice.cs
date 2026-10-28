using System.Runtime.InteropServices;
using NAudio.Wave;
using SherpaOnnx;

namespace OniAdvisor.Companion;

/// <summary>
/// Natural-sounding speech with Kokoro (82M parameters, Apache 2.0) through sherpa-onnx, entirely offline.
/// Audio is played while it is still being generated (sentence by sentence), so speech starts quickly.
/// Model folders live in %USERPROFILE%\.oni-advisor\models\ (from the sherpa-onnx "tts-models" releases):
///   kokoro-multi-lang-v1_0  - 53 voices, the best English ones (af_heart, am_michael, bf_emma ...); preferred
///   kokoro-en-v0_19         - 11 English voices
///   kokoro-int8-multi-lang-v1_1 - mostly Chinese voices; English: af_maple, af_sol, bf_vale (ids 0-2)
/// </summary>
public sealed class KokoroVoice : IDisposable
{
    static readonly string ModelsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".oni-advisor", "models");
    static readonly string[] Preferred = { "kokoro-multi-lang-v1_0", "kokoro-en-v0_19", "kokoro-int8-multi-lang-v1_1", "kokoro-multi-lang-v1_1" };

    readonly string dir;
    int speaker;
    readonly float speed;
    OfflineTts? tts;
    WaveOutEvent? output;
    BufferedWaveProvider? buffer;
    int generation; // bumps on every Speak/Stop, so an old generation stops feeding audio
    readonly object gate = new();
    public string? Error { get; private set; }

    KokoroVoice(string dir, int speaker, float speed)
    {
        this.dir = dir;
        this.speaker = speaker;
        this.speed = speed;
    }

    /// <summary>The installed model to use (or the one named in settings), or null when there is none.</summary>
    public static KokoroVoice? TryCreate(string? modelDir, int? speaker, float speed)
    {
        string? dir = !string.IsNullOrWhiteSpace(modelDir) ? modelDir
            : Preferred.Select(n => Path.Combine(ModelsDir, n)).FirstOrDefault(Directory.Exists);
        if (dir == null || !Directory.Exists(dir)) return null;
        return new KokoroVoice(dir, speaker ?? DefaultSpeaker(dir), speed <= 0 ? 1f : speed);
    }

    /// <summary>A good English voice per model: af_heart in v1.0, af (Bella-like) in v0.19, af_maple in v1.1.</summary>
    static int DefaultSpeaker(string dir) => Path.GetFileName(dir).Contains("v1_0") ? 3 : 0;

    public string Description => $"{Path.GetFileName(dir)}, voice {speaker}";

    /// <summary>Switches voice without reloading the model (all voices share it); applies from the next sentence.</summary>
    public int Speaker { get => speaker; set => speaker = value; }

    /// <summary>Loads the model (a few seconds); called in the background at start-up.</summary>
    public bool Load()
    {
        lock (gate)
        {
            if (tts != null) return true;
            try
            {
                var config = new OfflineTtsConfig();
                string F(string name) => Path.Combine(dir, name);
                config.Model.Kokoro.Model = File.Exists(F("model.onnx")) ? F("model.onnx") : F("model.int8.onnx");
                config.Model.Kokoro.Voices = F("voices.bin");
                config.Model.Kokoro.Tokens = F("tokens.txt");
                if (Directory.Exists(F("espeak-ng-data"))) config.Model.Kokoro.DataDir = F("espeak-ng-data");
                var lexicons = new[] { "lexicon-us-en.txt", "lexicon-zh.txt" }.Select(F).Where(File.Exists).ToList();
                if (lexicons.Count > 0) config.Model.Kokoro.Lexicon = string.Join(",", lexicons);
                if (Directory.Exists(F("dict"))) config.Model.Kokoro.DictDir = F("dict");
                config.Model.Kokoro.LengthScale = 1f;
                config.Model.NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
                config.Model.Provider = "cpu";
                config.MaxNumSentences = 1; // hand audio over sentence by sentence
                tts = new OfflineTts(config);
                buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(tts.SampleRate, 1))
                {
                    BufferDuration = TimeSpan.FromMinutes(3), DiscardOnBufferOverflow = true, ReadFully = true,
                };
                output = new WaveOutEvent { DesiredLatency = 150 };
                output.Init(buffer);
                output.Play();
                return true;
            }
            catch (Exception e) { Error = "Kokoro voice unavailable: " + e.Message; tts = null; return false; }
        }
    }

    /// <summary>Speaks the text; completes when it has been spoken, or false when stopped or unavailable.</summary>
    public Task<bool> SpeakAsync(string text)
    {
        int my;
        lock (gate) { my = ++generation; buffer?.ClearBuffer(); }
        return Task.Run(() =>
        {
            if (!Load() || tts == null || buffer == null) return false;
            try
            {
                foreach (var piece in Pieces(text))
                {
                    if (my != generation) return false;
                    tts.GenerateWithCallback(piece, speed, speaker, (IntPtr samples, int n) =>
                    {
                        if (my != generation) return 0; // stopped or replaced: stop generating
                        var data = new float[n];
                        Marshal.Copy(samples, data, 0, n);
                        var bytes = new byte[n * 4];
                        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
                        buffer.AddSamples(bytes, 0, bytes.Length);
                        return 1;
                    });
                }
                // Wait for the buffer to play out.
                while (my == generation && buffer.BufferedDuration > TimeSpan.Zero) Thread.Sleep(50);
                return my == generation;
            }
            catch (Exception e) { Error = "Kokoro voice failed: " + e.Message; return false; }
        });
    }

    /// <summary>
    /// Sentences, with long ones split at commas/semicolons: the first short piece starts playing (~1 s) while
    /// the rest is generated (generation runs faster than speech, so playback doesn't stall).
    /// </summary>
    static IEnumerable<string> Pieces(string text)
    {
        foreach (var sentence in System.Text.RegularExpressions.Regex.Split(text.Trim(), @"(?<=[.!?])\s+"))
        {
            if (sentence.Split(' ').Length <= 12) { if (sentence.Length > 0) yield return sentence; continue; }
            var parts = System.Text.RegularExpressions.Regex.Split(sentence, @"(?<=[,;:])\s+");
            string acc = "";
            foreach (var p in parts)
            {
                acc = acc.Length == 0 ? p : acc + " " + p;
                if (acc.Split(' ').Length >= 5) { yield return acc; acc = ""; }
            }
            if (acc.Length > 0) yield return acc;
        }
    }

    public void Stop()
    {
        lock (gate) { generation++; buffer?.ClearBuffer(); }
    }

    public void Dispose()
    {
        Stop();
        try { output?.Dispose(); } catch { }
        try { tts?.Dispose(); } catch { }
    }
}
