using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OniAdvisor.Companion;

/// <summary>
/// Small always-on-top window next to the game: chat with the advisor, quick actions, approvals for
/// delegated commands, optional speech. Reacts to the mod's request files (autosave, pod, plan, hotkey).
/// UNTESTED with a live game.
/// </summary>
public sealed class MainForm : Form
{
    readonly string advisorDir, liveDir, repoRoot, dotnetDir;
    readonly Settings settings;
    readonly ClaudeRunner claude;
    readonly Voice voice = new();
    readonly Queue<(string trigger, string? text, JsonObject? req)> queue = new();
    readonly HashSet<string> seenRequests = new();
    FileSystemWatcher? requestWatcher;
    CancellationTokenSource? cts;
    readonly SpeechInput speech;

    readonly RichTextBox log = new() { ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(24, 26, 30), ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 10f) };
    readonly TextBox input = new() { Multiline = true, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10f), AcceptsReturn = false };
    readonly Label status = new() { Dock = DockStyle.Bottom, AutoSize = false, Height = 22, ForeColor = Color.Gray, Text = "ready", TextAlign = ContentAlignment.MiddleLeft };
    readonly ListBox pendingList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly Panel pendingPanel = new() { Dock = DockStyle.Top, Height = 110, Visible = false };
    readonly CheckBox topMostBox = new() { Text = "On top", AutoSize = true };
    readonly CheckBox speakBox = new() { Text = "Speak", AutoSize = true };
    readonly Button micButton = new() { Text = "🎤 Hold", Width = 70 };

    public MainForm(string advisorDir, string repoRoot, string dotnetDir)
    {
        this.advisorDir = advisorDir;
        this.repoRoot = repoRoot;
        this.dotnetDir = dotnetDir;
        liveDir = Path.Combine(advisorDir, "live");
        Directory.CreateDirectory(liveDir);
        settings = Settings.Load(Path.Combine(advisorDir, "companion.json"));
        claude = new ClaudeRunner(settings, advisorDir, dotnetDir);
        speech = new SpeechInput(settings.WhisperModel, settings.MicDevice);

        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "ONI Advisor";
        ClientSize = new Size(440, 680);
        MinimumSize = new Size(320, 400);
        StartPosition = FormStartPosition.Manual;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1600, 900);
        Location = new Point(wa.Right - Width - 10, wa.Top + 40);
        TopMost = settings.TopMost;
        BackColor = Color.FromArgb(32, 34, 40);
        ForeColor = Color.Gainsboro;

        BuildUi();
        ResumeLayout(false);
        PerformLayout();
        // After scaling: fit the window on this screen and never start minimized.
        WindowState = FormWindowState.Normal;
        if (Height > wa.Height - 60) Height = Math.Max(MinimumSize.Height, wa.Height - 60);
        Location = new Point(wa.Right - Width - 10, wa.Top + 30);
        EnsureWorkspace();
        StartWatching();
        RefreshPending();

        var timer = new System.Windows.Forms.Timer { Interval = 1500 };
        timer.Tick += (_, _) => RefreshPending();
        timer.Start();

        WriteStatus("idle", null);
        System("Ready. In the game: Ctrl+Shift+A = look at this, hold Ctrl+Shift+V = talk to me, Ctrl+Shift+C = show/hide this window, Ctrl+Shift+P = sketch a plan.");
        if (!File.Exists(SpeechInput.DefaultModelPath) && string.IsNullOrWhiteSpace(settings.WhisperModel))
            System("Speech model not found (" + SpeechInput.DefaultModelPath + "); voice input is off.");
        if (ClaudeRunner.FindClaude(settings.ClaudePath) == null)
            System("Claude Code was not found. Set \"ClaudePath\" in advisor/companion.json.");
    }

    // ------------------------------------------------------------------ UI

    void BuildUi()
    {
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4), WrapContents = true };
        topMostBox.Checked = settings.TopMost;
        topMostBox.CheckedChanged += (_, _) => { TopMost = settings.TopMost = topMostBox.Checked; settings.Save(); };
        speakBox.Checked = settings.Speak;
        speakBox.CheckedChanged += (_, _) => { settings.Speak = speakBox.Checked; settings.Save(); if (!speakBox.Checked) voice.StopSpeaking(); };
        var settingsBtn = SmallButton("Settings", (_, _) => OpenSettings());
        var newBtn = SmallButton("New chat", (_, _) =>
        {
            if (MessageBox.Show(this, "Start a new conversation? The advisor keeps its colony plan file, but forgets this chat.", "ONI Advisor", MessageBoxButtons.OKCancel) == DialogResult.OK)
            { settings.SessionId = null; settings.Save(); System("New conversation started."); }
        });
        var stopBtn = SmallButton("Stop", (_, _) => { cts?.Cancel(); claude.Cancel(); voice.StopSpeaking(); });
        var testVoiceBtn = SmallButton("Test voice", (_, _) =>
        {
            voice.Speak("Voice test. Breathability is ninety six percent, and Meep needs a skill point.");
            if (voice.Error != null) System(voice.Error);
            else System("Speaking a test sentence. Then hold the mic button, say something, and check the text that appears.");
        });
        top.Controls.AddRange(new Control[] { topMostBox, speakBox, settingsBtn, newBtn, stopBtn, testVoiceBtn });

        // Approvals for delegated commands ("ask" mode).
        var pendingButtons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 90, FlowDirection = FlowDirection.TopDown };
        pendingButtons.Controls.Add(SmallButton("Approve", (_, _) => Decide(true), 84));
        pendingButtons.Controls.Add(SmallButton("Decline", (_, _) => Decide(false), 84));
        var pendingLabel = new Label { Text = "Waiting for your approval:", Dock = DockStyle.Top, Height = 18, ForeColor = Color.Khaki };
        pendingPanel.Controls.Add(pendingList);
        pendingPanel.Controls.Add(pendingButtons);
        pendingPanel.Controls.Add(pendingLabel);

        var quick = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(2), WrapContents = true };
        quick.Controls.Add(SmallButton("Briefing", (_, _) => Enqueue("briefing", null)));
        quick.Controls.Add(SmallButton("Pod pick", (_, _) => Enqueue("pod", null)));
        quick.Controls.Add(SmallButton("What's wrong?", (_, _) => Enqueue("ask", "What is going wrong right now, and what should I fix first?")));
        quick.Controls.Add(SmallButton("Review marks", (_, _) => Enqueue("plan", TakeInput())));

        var inputRow = new Panel { Dock = DockStyle.Bottom, Height = 64 };
        var send = new Button { Text = "Send", Dock = DockStyle.Right, Width = 60 };
        send.Click += (_, _) => SendTyped();
        micButton.Dock = DockStyle.Right;
        // Hold-to-talk inside the window: the text lands in the box to check before sending.
        micButton.MouseDown += (_, _) => StartListening();
        micButton.MouseUp += async (_, _) =>
        {
            var said = await StopListeningAsync();
            if (said.Length == 0) return;
            input.Text = (input.Text + " " + said).Trim();
            if (settings.VoiceAutoSend) SendTyped();
            else { input.Focus(); input.SelectionStart = input.TextLength; status.Text = "check the text, then press Enter"; }
        };
        input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && !e.Shift) { e.SuppressKeyPress = true; SendTyped(); }
        };
        inputRow.Controls.Add(input);
        inputRow.Controls.Add(micButton);
        inputRow.Controls.Add(send);

        Controls.Add(log);
        Controls.Add(pendingPanel);
        Controls.Add(top);
        Controls.Add(quick);
        Controls.Add(inputRow);
        Controls.Add(status);
    }

    static Button SmallButton(string text, EventHandler onClick, int width = 0)
    {
        var b = new Button { Text = text, AutoSize = width == 0, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4, 2, 4, 2), FlatStyle = FlatStyle.Flat, ForeColor = Color.Gainsboro, Margin = new Padding(2) };
        if (width > 0) b.Width = width;
        b.Click += onClick;
        return b;
    }

    string? TakeInput()
    {
        var t = input.Text.Trim();
        input.Clear();
        return t.Length > 0 ? t : null;
    }

    void SendTyped()
    {
        var t = TakeInput();
        if (t != null) Enqueue("ask", t);
    }

    void OpenSettings()
    {
        settings.Save();
        foreach (var f in new[] { settings.FilePath, Path.Combine(advisorDir, "config.json") })
            if (File.Exists(f)) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{f}\"") { UseShellExecute = true });
    }

    void Append(string who, string text, Color color)
    {
        if (InvokeRequired) { BeginInvoke(() => Append(who, text, color)); return; }
        log.SelectionStart = log.TextLength;
        log.SelectionColor = color;
        log.SelectionFont = new Font(log.Font, FontStyle.Bold);
        log.AppendText(who.Length > 0 ? who + "  " : "");
        log.SelectionFont = log.Font;
        log.SelectionColor = color == Color.Gray ? Color.Gray : Color.Gainsboro;
        log.AppendText(text.Trim() + Environment.NewLine + Environment.NewLine);
        log.ScrollToCaret();
    }

    void System(string text) => Append("", text, Color.Gray);

    // ------------------------------------------------------------------ workspace

    /// <summary>Copies CLAUDE.md (advisor instructions) into the advisor folder when it is missing or older.</summary>
    void EnsureWorkspace()
    {
        try
        {
            var src = Path.Combine(repoRoot, "advisor-workspace", "CLAUDE.md");
            var dst = Path.Combine(advisorDir, "CLAUDE.md");
            if (File.Exists(src) && (!File.Exists(dst) || File.GetLastWriteTimeUtc(src) > File.GetLastWriteTimeUtc(dst)))
            {
                File.Copy(src, dst, overwrite: true);
                System("Updated advisor instructions (CLAUDE.md).");
            }
        }
        catch (Exception e) { System("Could not copy CLAUDE.md: " + e.Message); }
    }

    string MapQueryDll() =>
        !string.IsNullOrWhiteSpace(settings.MapQueryDll) ? settings.MapQueryDll!
            : Path.Combine(repoRoot, "tools", "MapQuery", "bin", "Release", "net8.0", "MapQuery.dll");

    // ------------------------------------------------------------------ requests from the mod

    void StartWatching()
    {
        var dir = Path.Combine(liveDir, "requests");
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.GetFiles(dir, "*.json")) seenRequests.Add(Path.GetFileName(f)); // ignore old ones
        requestWatcher = new FileSystemWatcher(dir, "*.json") { EnableRaisingEvents = true };
        requestWatcher.Created += (_, e) => BeginInvoke(() => OnRequest(e.FullPath));
        requestWatcher.Renamed += (_, e) => BeginInvoke(() => OnRequest(e.FullPath));
    }

    async void OnRequest(string path)
    {
        if (!seenRequests.Add(Path.GetFileName(path))) return;
        JsonObject? req = null;
        for (int i = 0; i < 10 && req == null; i++)
        {
            try { req = JsonNode.Parse(await File.ReadAllTextAsync(path))?.AsObject(); }
            catch (IOException) { await Task.Delay(200); }
            catch { break; }
        }
        if (req == null) return;
        string trigger = (string?)req["trigger"] ?? "";
        switch (trigger)
        {
            case "autosave" when settings.AutoBriefOnAutosave: Enqueue("autosave", null, req); break;
            case "pod" when settings.AutoPodPick: Enqueue("pod", null, req); break;
            case "plan" when settings.AutoReviewPlan: Enqueue("plan", TakeInput(), req); break;
            case "hotkey" when settings.AutoLookOnHotkey: Enqueue("hotkey", TakeInput(), req); break;
            case "voice-start": StartListening(); break;
            case "voice-stop": await VoiceFromGameAsync(); break;
            case "companion": ToggleVisible(); break;
            default: System($"({trigger} from the game; auto-reply is off)"); break;
        }
    }

    // ------------------------------------------------------------------ voice input (Whisper)

    void StartListening()
    {
        voice.StopSpeaking(); // don't record the advisor's own voice
        if (speech.Start(out var mic)) { status.Text = "listening… (" + mic + ")"; WriteStatus("listening", null); }
        else { System(speech.Error ?? "microphone unavailable"); WriteStatus("error", speech.Error); }
    }

    async Task<string> StopListeningAsync()
    {
        if (!speech.Recording) return "";
        var audio = speech.Stop();
        status.Text = "transcribing…";
        WriteStatus("transcribing", null);
        var text = await Task.Run(() => speech.TranscribeAsync(audio, VocabularyPrompt(), CancellationToken.None));
        status.Text = "ready";
        if (speech.Error != null) { System(speech.Error); WriteStatus("error", speech.Error); return ""; }
        if (text.Length == 0) { status.Text = "didn't catch anything"; WriteStatus("idle", null); }
        return text;
    }

    /// <summary>Hold Ctrl+Shift+V in the game: the game keeps focus; what was heard is shown in the game and sent.</summary>
    async Task VoiceFromGameAsync()
    {
        var said = await StopListeningAsync();
        if (said.Length == 0) return;
        WriteStatus("heard", said);
        await Task.Delay(1300); // the game checks the status about once a second; let it show what was heard
        Enqueue("ask", said);
    }

    /// <summary>Primes Whisper with this colony's words: duplicant names, building names, common ONI terms.</summary>
    string VocabularyPrompt()
    {
        var words = new List<string> { "Oxygen Not Included", "duplicant", "electrolyzer", "hydrogen", "carbon dioxide", "polluted water",
            "Printing Pod", "Mealwood", "Bristle Blossom", "Hatch", "Pacu", "algae", "Oxylite", "Lavatory", "Water Sieve" };
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(liveDir, "state.json")));
            var root = doc.RootElement;
            if (root.TryGetProperty("dupes", out var dupes) && dupes.ValueKind == JsonValueKind.Array)
                foreach (var d in dupes.EnumerateArray())
                    if (d.TryGetProperty("name", out var n) && n.GetString() is { } s) words.Add(s);
            if (root.TryGetProperty("buildings", out var b) && b.TryGetProperty("counts", out var counts))
                foreach (var p in counts.EnumerateObject()) words.Add(p.Name);
        }
        catch { }
        return "Talking about my Oxygen Not Included colony: " + string.Join(", ", words.Distinct().Take(120)) + ".";
    }

    /// <summary>Ctrl+Shift+C in the game: show this window without taking focus, or hide it again.</summary>
    void ToggleVisible()
    {
        // Show/hide with native calls only: restoring a *minimized* window makes Windows give it focus,
        // so the window is hidden rather than minimized, and shown without activation.
        if (!NativeFocus.OnScreen(Handle))
        {
            TopMost = true;                     // visible over the game while shown
            NativeFocus.ShowNoActivate(Handle); // the game keeps the keyboard, so Ctrl+Shift+C hides it again
            log.ScrollToCaret();
        }
        else NativeFocus.Hide(Handle);
    }

    // ------------------------------------------------------------------ talking to Claude

    void Enqueue(string trigger, string? text, JsonObject? req = null)
    {
        if (trigger == "autosave" && queue.Any(q => q.trigger == "autosave")) return; // one briefing is enough
        if (text != null) Append("You", text, Color.LightSkyBlue);
        else Append("", trigger switch
        {
            "autosave" => "New cycle — asking for a briefing…",
            "pod" => "Printing Pod — asking for a pick…",
            "plan" => "Your sketch — asking for a review…",
            "hotkey" => "Look request — asking the advisor…",
            "briefing" => "Asking for a briefing…",
            _ => "Asking…",
        }, Color.Gray);
        queue.Enqueue((trigger, text, req));
        if (!claude.Busy) _ = Pump();
    }

    async Task Pump()
    {
        while (queue.Count > 0)
        {
            var (trigger, text, req) = queue.Dequeue();
            status.Text = "advisor is thinking…";
            WriteStatus("thinking", null);
            cts = new CancellationTokenSource();
            var reply = await claude.AskAsync(BuildPrompt(trigger, text, req), cts.Token);
            status.Text = "ready";
            if (reply.Ok)
            {
                Append("Advisor", reply.Text, Color.PaleGreen);
                if (settings.Speak) voice.Speak(reply.Text);
                WriteStatus("replied", reply.Text);
            }
            else { System("Advisor error: " + reply.Error); WriteStatus("error", reply.Error); }
        }
    }

    string BuildPrompt(string trigger, string? text, JsonObject? req)
    {
        var sb = new System.Text.StringBuilder();
        int cycle = (int?)req?["cycle"] ?? ReadCycle();
        sb.AppendLine($"[trigger: {trigger}] [cycle {cycle}] [{DateTime.Now:HH:mm}]");
        sb.AppendLine("Data: live/state.json (fresh). Map images: live/map/. MapQuery: dotnet \"" + MapQueryDll() + "\" <summary|region|cell|find> ...");
        if ((string?)req?["screenshot"] is { } shot) sb.AppendLine("Screenshot: " + RelativeToAdvisor(shot));
        switch (trigger)
        {
            case "autosave":
            case "briefing":
                sb.AppendLine("Give the cycle briefing (Now / Next / Long term). Compare with the previous live/history snapshot if useful. Update the colony plan file if goals changed.");
                break;
            case "pod":
                sb.AppendLine("The Printing Pod selection is open: recommend one option from live/pod.json (one line per option). If pod.json is old or missing, say so.");
                break;
            case "plan":
                sb.AppendLine("Review the player's sketch in live/plan/player-marks.json (they refer to marks as #1, #2, ...). Answer their question; draw corrections or suggestions as advisor marks when a location matters.");
                break;
            case "hotkey":
                sb.AppendLine("The player pressed the 'look at this' key in the game. Look at the screenshot and the state, and say what matters most right now.");
                break;
        }
        if (text != null) sb.AppendLine("Player: " + text);
        return sb.ToString();
    }

    int ReadCycle()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(liveDir, "state.json")));
            return doc.RootElement.GetProperty("meta").GetProperty("cycle").GetInt32();
        }
        catch { return 0; }
    }

    string RelativeToAdvisor(string path)
    {
        try { return Path.GetRelativePath(advisorDir, path).Replace('\\', '/'); } catch { return path; }
    }

    /// <summary>Tells the game mod what the advisor is doing; the mod shows it in its status badge.</summary>
    void WriteStatus(string state, string? text)
    {
        try
        {
            var path = Path.Combine(liveDir, "advisor-status.json");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new { state, text, utc = DateTime.UtcNow.ToString("o") }));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    // ------------------------------------------------------------------ approvals

    sealed record Pending(string Id, string Summary)
    {
        public override string ToString() => Summary;
    }

    void RefreshPending()
    {
        var dir = Path.Combine(liveDir, "commands", "pending");
        var items = new List<Pending>();
        try
        {
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
                {
                    try
                    {
                        var o = JsonNode.Parse(File.ReadAllText(f))?.AsObject();
                        items.Add(new Pending(Path.GetFileNameWithoutExtension(f), (string?)o?["summary"] ?? (string?)o?["action"] ?? "command"));
                    }
                    catch { }
                }
        }
        catch { }
        var current = pendingList.Items.Cast<Pending>().Select(p => p.Id).ToList();
        if (!current.SequenceEqual(items.Select(p => p.Id)))
        {
            pendingList.Items.Clear();
            foreach (var p in items) pendingList.Items.Add(p);
            if (items.Count > 0) pendingList.SelectedIndex = 0;
            if (items.Count > current.Count) System($"{items.Count} command(s) waiting for your approval.");
        }
        pendingPanel.Visible = items.Count > 0;
    }

    void Decide(bool approve)
    {
        if (pendingList.SelectedItem is not Pending p) return;
        var dir = Path.Combine(liveDir, "commands", "decisions");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, p.Id + ".json"), JsonSerializer.Serialize(new { approve }));
        System($"{(approve ? "Approved" : "Declined")}: {p.Summary}");
        pendingList.Items.Remove(p);
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        requestWatcher?.Dispose();
        voice.Dispose();
        speech.Dispose();
        try { File.Delete(Path.Combine(liveDir, "advisor-status.json")); } catch { } // the game then shows "companion not running"
        settings.Save();
        base.OnFormClosed(e);
    }
}
