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
    readonly Voice voice = new();
    readonly Queue<(string trigger, string? text, JsonObject? req)> queue = new();
    readonly HashSet<string> seenRequests = new();
    FileSystemWatcher? requestWatcher;
    CancellationTokenSource? cts;
    readonly SpeechInput speech;
    readonly AdvisorSession advisor;
    readonly GameWatch watch;
    readonly ChatLog chat;
    readonly ConversationLog convo;

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
        speech = new SpeechInput(settings.WhisperModel, settings.MicDevice);
        voice.Configure(settings);
        advisor = new AdvisorSession(settings, advisorDir, dotnetDir);
        watch = new GameWatch(liveDir);
        chat = new ChatLog(liveDir);
        HookAdvisorEvents();
        convo = new ConversationLog(liveDir);

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
        var follow = new System.Windows.Forms.Timer { Interval = 10000 };
        follow.Tick += (_, _) => FollowGame();
        follow.Start();
        timer.Start();

        WriteStatus("idle", null);
        _ = Task.Run(advisor.WarmUp); // start the advisor now: start-up is the slow part
        System("Ready. In the game: hold Ctrl+Shift+V = talk to me, Ctrl+Shift+A = look at this, Ctrl+Shift+B = briefing, Ctrl+Shift+Q = voice on/off, Ctrl+Shift+C = show/hide this window, Ctrl+Shift+P = sketch a plan.");
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
        speakBox.CheckedChanged += (_, _) => { settings.Speak = settings.SpeakBriefings = speakBox.Checked; settings.Save(); if (!speakBox.Checked) voice.StopSpeaking(); };
        var settingsBtn = SmallButton("Settings", (_, _) => OpenSettings());
        var newBtn = SmallButton("New chat", (_, _) =>
        {
            if (MessageBox.Show(this, "Start a new conversation? The advisor keeps its colony plan file, but forgets this chat.", "ONI Advisor", MessageBoxButtons.OKCancel) == DialogResult.OK)
            { advisor.NewConversation(); System("New conversation started."); }
        });
        var stopBtn = SmallButton("Stop", (_, _) => { cts?.Cancel(); cts = null; voice.StopSpeaking(); });
        var testVoiceBtn = SmallButton("Test voice", (_, _) =>
        {
            voice.Speak("Voice test. Breathability is ninety six percent, and Meep needs a skill point.");
            if (voice.Error != null) System(voice.Error);
            else System($"Speaking a test sentence ({voice.Engine}). Then hold the mic button, say something, and check the text that appears.");
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

    FileSystemWatcher? playbackWatcher;
    string lastPlayback = "";

    void StartWatching()
    {
        var bdir = Path.Combine(liveDir, "briefing");
        Directory.CreateDirectory(bdir);
        playbackWatcher = new FileSystemWatcher(bdir, "playback.json") { EnableRaisingEvents = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
        playbackWatcher.Changed += (_, _) => BeginInvoke(() => OnPlayback());
        playbackWatcher.Created += (_, _) => BeginInvoke(() => OnPlayback());
        playbackWatcher.Renamed += (_, _) => BeginInvoke(() => OnPlayback());

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
            case "briefing": Enqueue("briefing", null, req); break;
            case "chat" when (string?)req["text"] is { Length: > 0 } typed: Enqueue("chat", typed, req); break;
            case "new-chat": advisor.NewConversation(); chat.Add("note", "New conversation started.", ReadCycle()); break;
            case "stop": cts?.Cancel(); cts = null; voice.StopSpeaking(); break;
            case "speak-toggle": ToggleVoice(); break;
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
        Enqueue("ask", said, new JsonObject { ["via"] = "voice" });
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

    /// <summary>Ctrl+Shift+Q in the game: the advisor's voice on/off (live answers and briefings).</summary>
    void ToggleVoice()
    {
        bool on = !(settings.Speak || settings.SpeakBriefings);
        settings.Speak = settings.SpeakBriefings = on;
        settings.Save();
        speakBox.Checked = on;
        if (!on) voice.StopSpeaking();
        WriteStatus("notice", on ? "Advisor voice: on" : "Advisor voice: off");
        System(on ? "Voice on." : "Voice off.");
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

    /// <summary>The game is playing a briefing step: read its "say" aloud, then report the step as done so the
    /// game moves on. Step -1 means the briefing was closed.</summary>
    async void OnPlayback()
    {
        JsonObject? p = null;
        for (int i = 0; i < 5 && p == null; i++)
        {
            try { p = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(liveDir, "briefing", "playback.json")))?.AsObject(); }
            catch (IOException) { await Task.Delay(100); }
            catch { return; }
        }
        if (p == null) return;
        string id = (string?)p["id"] ?? "";
        int step = (int?)p["step"] ?? -1;
        string key = id + "#" + step + "#" + (string?)p["utc"];
        if (key == lastPlayback) return; // FileSystemWatcher often fires twice
        lastPlayback = key;

        if (step < 0) { voice.StopSpeaking(); return; }
        string say = (string?)p["say"] ?? "";
        bool spoke = settings.SpeakBriefings && say.Length > 0 && await voice.SpeakAndWaitAsync(say);
        if (!spoke && say.Length > 0)
            await Task.Delay(Math.Clamp(say.Split(' ').Length * 330, 2500, 12000)); // reading time when not spoken
        if (lastPlayback != key) return; // the player moved to another step meanwhile
        try
        {
            File.WriteAllText(Path.Combine(liveDir, "briefing", "playback-done.json"),
                JsonSerializer.Serialize(new { id, step, utc = DateTime.UtcNow.ToString("o") }));
        }
        catch { }
    }

    /// <summary>Keeps the last answers for the in-game panel ("Recent answers").</summary>
    void LogAnswer(string? question, string answer)
    {
        try
        {
            var path = Path.Combine(liveDir, "advisor-log.json");
            var arr = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsArray() ?? new JsonArray() : new JsonArray();
            arr.Add(new JsonObject { ["q"] = question, ["a"] = answer, ["utc"] = DateTime.UtcNow.ToString("o") });
            while (arr.Count > 20) arr.RemoveAt(0);
            File.WriteAllText(path, arr.ToJsonString());
        }
        catch { }
    }

    // ------------------------------------------------------------------ talking to the advisor

    DateTime lastPlayerUtc = DateTime.MinValue, lastProactiveUtc = DateTime.MinValue;
    bool autosavePending;
    string currentActivity = "";

    void HookAdvisorEvents()
    {
        advisor.TextChanged += t => chat.Stream(t.Length > 0 ? "writing" : "thinking", t, currentActivity);
        advisor.Activity += a =>
        {
            currentActivity = a;
            chat.Add("activity", a, ReadCycle());
            chat.Stream("thinking", "", a);
            BeginInvoke(() => { status.Text = a + "…"; Append("", "· " + a, Color.DimGray); });
            WriteStatus("thinking", a + "…");
        };
        advisor.Thought += t => { chat.Add("thought", t, ReadCycle()); BeginInvoke(() => Append("", t, Color.Gray)); };
    }

    /// <summary>Every question, request and game update goes to the one advisor conversation, one turn at a time.</summary>
    void Enqueue(string trigger, string? text, JsonObject? req = null)
    {
        if (trigger == "autosave") { if (autosavePending) return; autosavePending = true; }
        if (text != null && trigger == "briefing") Append("", "Preparing a briefing on: " + text, Color.Gray);
        else if (text != null) Append("You", text, Color.LightSkyBlue);
        else if (trigger != "update") Append("", trigger switch
        {
            "autosave" => "New cycle — asking for a briefing…",
            "pod" => "Printing Pod — asking for a pick…",
            "plan" => "Your sketch — asking for a review…",
            "hotkey" => "Look request — asking the advisor…",
            "briefing" => "Asking for a briefing…",
            _ => "Asking…",
        }, Color.Gray);
        _ = Ask(trigger, text, req);
    }

    async Task Ask(string trigger, string? text, JsonObject? req)
    {
        int cycle = (int?)req?["cycle"] ?? ReadCycle();
        if (trigger != "update")
        {
            lastPlayerUtc = DateTime.UtcNow;
            if (text != null && trigger is "ask" or "chat") chat.Add("you", text, cycle);
            else chat.Add("note", trigger switch
            {
                "autosave" => $"Cycle {cycle}: preparing the cycle briefing",
                "pod" => "Printing Pod open: asking for a pick",
                "plan" => "Sketch sent" + (text != null ? ": " + text : ""),
                "hotkey" => "Look at this" + (text != null ? ": " + text : ""),
                "briefing" => "Preparing a briefing" + (text != null ? " on " + text : ""),
                _ => trigger,
            }, cycle);
        }
        if (advisor.Busy && trigger != "update") { chat.Stream("thinking", "", "finishing the previous answer first"); WriteStatus("thinking", "finishing the previous answer…"); }

        string view = ReadView();
        string facts = view.StartsWith("{") && trigger != "autosave" ? await LiveFacts.FetchAsync(liveDir, view, text) : "";
        string? shot = trigger is "hotkey" or "plan" ? (string?)req?["screenshot"] : null;
        var briefingFile = Path.Combine(liveDir, "briefing", "briefing.json");
        var before = File.Exists(briefingFile) ? File.GetLastWriteTimeUtc(briefingFile) : DateTime.MinValue;

        cts ??= new CancellationTokenSource();
        var reply = await advisor.AskAsync(BuildPrompt(trigger, text, req, view, facts), cts.Token, shot != null && File.Exists(shot) ? shot : null);
        if (trigger == "autosave") autosavePending = false;
        currentActivity = "";
        status.Text = reply.Seconds is double s ? $"ready (last answer {s:0} s)" : "ready";

        // A briefing written during this turn: archive it and tell the player.
        try
        {
            if (File.Exists(briefingFile) && File.GetLastWriteTimeUtc(briefingFile) > before)
            {
                var json = File.ReadAllText(briefingFile);
                string title = Briefings.Archive(liveDir, json);
                convo.Add("briefing", cycle, text ?? (trigger == "autosave" ? "new cycle" : null), ConversationLog.Summarize(json));
                chat.Add("note", $"Briefing ready: {title} — open it with Ctrl+Shift+B", cycle);
            }
        }
        catch { }

        if (!reply.Ok)
        {
            chat.Stream("idle");
            if (trigger == "update") return; // unasked: stay quiet about failures
            System("Advisor error: " + reply.Error);
            chat.Add("note", "Advisor error: " + reply.Error, cycle);
            WriteStatus("error", reply.Error);
            return;
        }
        string answer = reply.Text;
        if (trigger == "update" && (answer.Length == 0 || answer.Contains("(silent)", StringComparison.OrdinalIgnoreCase)))
        {
            chat.Stream("idle");
            WriteStatus("idle", null);
            return;
        }
        chat.Add("advisor", answer, cycle);
        chat.Stream("idle");
        Append("Advisor", answer, Color.PaleGreen);
        string spoken = Spoken(answer);
        if (settings.Speak && trigger is not "autosave" and not "briefing") voice.Speak(spoken);
        LogAnswer(text ?? $"({trigger})", answer);
        convo.Add(trigger switch { "hotkey" => "look", "pod" => "pod", "plan" => "plan", "update" => "unasked", _ => "live" }, cycle,
                  text ?? trigger switch { "hotkey" => "(pressed 'look at this')", "pod" => "(Printing Pod open)", "plan" => "(sent a sketch)", "update" => "(nothing asked)", _ => "" }, answer);
        WriteStatus("replied", spoken);
    }

    /// <summary>What to read aloud: a short answer whole; a long one's first two sentences and a pointer to the panel.</summary>
    static string Spoken(string answer)
    {
        var plain = global::System.Text.RegularExpressions.Regex.Replace(answer, @"[*_`#>]|\[[^\]]*\]\([^)]*\)", "");
        plain = global::System.Text.RegularExpressions.Regex.Replace(plain, @"^\s*[-•]\s*", "", global::System.Text.RegularExpressions.RegexOptions.Multiline).Trim();
        if (plain.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 45) return plain;
        var sentences = global::System.Text.RegularExpressions.Regex.Split(plain, @"(?<=[.!?])\s+");
        return string.Join(" ", sentences.Take(2)) + " The rest is in the advisor panel.";
    }

    /// <summary>Every ~10 s: follow the game, and let the advisor speak up when something happened.</summary>
    void FollowGame()
    {
        bool fresh = watch.Poll();
        if (!fresh || settings.Proactive == "never" || advisor.Busy || !watch.HasEvents) return;
        var now = DateTime.UtcNow;
        if ((now - lastPlayerUtc).TotalSeconds < 45 || (now - lastProactiveUtc).TotalSeconds < settings.ProactiveMinSeconds) return;
        if (settings.Proactive == "serious" && !watch.HasSerious) return;
        if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(Path.Combine(liveDir, "view.json"))).TotalSeconds > 30) return; // game not running
        lastProactiveUtc = now;
        Enqueue("update", null);
    }

    string BuildPrompt(string trigger, string? text, JsonObject? req, string view, string facts)
    {
        var sb = new global::System.Text.StringBuilder();
        int cycle = (int?)req?["cycle"] ?? ReadCycle();
        string via = (string?)req?["via"] ?? (trigger == "chat" ? "panel" : "companion");
        sb.AppendLine($"[trigger: {trigger}] [via: {via}] [cycle {cycle}] [{DateTime.Now:HH:mm}]");
        if (advisor.FreshConversation)
        {
            string recent = convo.Recent(20, 8000);
            if (recent.Length > 0) sb.AppendLine("(New conversation. What you and the player said earlier, oldest first:)\n" + recent);
        }
        string updates = watch.Take();
        if (updates.Length > 0) sb.AppendLine("Since your last message, in the game:\n" + updates);
        sb.AppendLine("Player's view: " + view);
        if (!string.IsNullOrWhiteSpace(facts)) sb.AppendLine("Fetched from the game just now:\n" + facts.TrimEnd());
        sb.AppendLine("Colony summary: " + ColonyDigest.Build(liveDir));
        sb.AppendLine("Tools: MapQuery = dotnet \"" + MapQueryDll() + "\" (live region|cell|buildings|pipes|ports|rates|find ...). Files: live/state.json, live/map/, live/screenshots/.");
        if ((string?)req?["screenshot"] is { } shotPath)
            sb.AppendLine(trigger is "hotkey" or "plan" ? "The attached screenshot is what the player sees right now." : "Screenshot: " + RelativeToAdvisor(shotPath));
        switch (trigger)
        {
            case "autosave":
                sb.AppendLine("A new cycle began (autosave). Write the cycle briefing to live/briefing/briefing.json — as long as the situation needs (see CLAUDE.md), with plenty of marks. Then reply with one sentence on what it covers.");
                break;
            case "briefing":
                sb.AppendLine(text != null
                    ? "Write a briefing on: " + text + " — to live/briefing/briefing.json, as many steps as it needs, with plenty of marks and a project if a layout is involved. Then reply with one sentence on what it covers."
                    : "The player asked for a briefing now. Write it to live/briefing/briefing.json — as long as the situation needs, with plenty of marks. Then reply with one sentence on what it covers.");
                break;
            case "pod":
                sb.AppendLine("The Printing Pod selection is open: recommend one option from live/pod.json, briefly saying why.");
                break;
            case "plan":
                sb.AppendLine("Review the player's sketch in live/plan/player-marks.json (they call marks #1, #2, ...); draw corrections as advisor marks.");
                break;
            case "hotkey":
                sb.AppendLine("The player pressed 'look at this'. Say what matters most about what they are looking at.");
                break;
            case "update":
                sb.AppendLine("Nothing was asked: this is a game update. If something above is worth telling the player now — a risk, a mistake, a consequence of what they just built, a good next step — say it briefly (it is read aloud), with marks if a place matters. Don't repeat what you already told them. Otherwise reply exactly: (silent)");
                break;
        }
        if (text != null && trigger is "ask" or "chat") sb.AppendLine($"Player ({(via == "voice" ? "spoken" : "typed")}): {text}");
        else if (text != null && trigger is "plan" or "hotkey") sb.AppendLine("Player adds: " + text);
        return sb.ToString();
    }

    /// <summary>live/view.json from the mod: visible area, overlay, cursor tile, selection, map around the cursor.</summary>
    string ReadView()
    {
        try
        {
            var path = Path.Combine(liveDir, "view.json");
            if (!File.Exists(path) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds > 30) return "(not available: game not running or mod outdated)";
            return File.ReadAllText(path);
        }
        catch { return "(not available)"; }
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
        playbackWatcher?.Dispose();
        advisor.Dispose();
        voice.Dispose();
        speech.Dispose();
        try { File.Delete(Path.Combine(liveDir, "advisor-status.json")); } catch { } // the game then shows "companion not running"
        settings.Save();
        base.OnFormClosed(e);
    }
}
