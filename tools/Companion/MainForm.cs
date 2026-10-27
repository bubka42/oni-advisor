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
    readonly LiveClaude live;
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
        claude = new ClaudeRunner(settings, advisorDir, dotnetDir);
        speech = new SpeechInput(settings.WhisperModel, settings.MicDevice);
        live = new LiveClaude(settings, advisorDir, dotnetDir);
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
        timer.Start();

        WriteStatus("idle", null);
        _ = Task.Run(live.WarmUp); // start the live advisor now: start-up is the slow part
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

    // ------------------------------------------------------------------ talking to Claude

    void Enqueue(string trigger, string? text, JsonObject? req = null)
    {
        if (trigger == "autosave" && queue.Any(q => q.trigger == "autosave")) return; // one briefing is enough
        if (text != null && trigger == "briefing") Append("", "Preparing a briefing on: " + text, Color.Gray);
        else if (text != null) Append("You", text, Color.LightSkyBlue);
        else Append("", trigger switch
        {
            "autosave" => "New cycle — asking for a briefing…",
            "pod" => "Printing Pod — asking for a pick…",
            "plan" => "Your sketch — asking for a review…",
            "hotkey" => "Look request — asking the advisor…",
            "briefing" => "Asking for a briefing…",
            _ => "Asking…",
        }, Color.Gray);
        if (trigger is "autosave" or "briefing")
        {
            // Briefings: full model and workflow, one at a time, in the background.
            queue.Enqueue((trigger, text, req));
            if (!claude.Busy) _ = Pump();
        }
        else _ = AskLive(trigger, text, req); // live questions never wait behind a briefing
    }

    readonly SemaphoreSlim liveLock = new(1, 1);

    /// <summary>Live mode: the persistent fast channel, with the colony summary in the prompt.</summary>
    async Task AskLive(string trigger, string? text, JsonObject? req)
    {
        await liveLock.WaitAsync();
        try
        {
            status.Text = "advisor is thinking…";
            WriteStatus("thinking", null);
            cts = new CancellationTokenSource();
            // Fetch what the question most likely needs first: the fast model answers from what is in the prompt.
            string view = ReadView();
            string facts = view.StartsWith("{") ? await LiveFacts.FetchAsync(liveDir, view, text) : "";
            string? shot = trigger is "hotkey" or "plan" ? (string?)req?["screenshot"] : null;
            var reply = await live.AskAsync(BuildPrompt(trigger, text, req, view, facts), cts.Token, shot != null && File.Exists(shot) ? shot : null);
            status.Text = reply.Seconds is double s ? $"ready (last answer {s:0.0} s)" : "ready";
            if (reply.Ok)
            {
                // "[[briefing: topic]]" = no honest short answer: hand the question to briefing mode.
                var m = global::System.Text.RegularExpressions.Regex.Match(reply.Text, @"\[\[\s*briefing\s*:\s*(.*?)\]\]", global::System.Text.RegularExpressions.RegexOptions.Singleline);
                string shown = m.Success ? reply.Text.Remove(m.Index, m.Length).Trim() : reply.Text;
                Append("Advisor", shown, Color.PaleGreen);
                if (settings.Speak) voice.Speak(shown);
                LogAnswer(text ?? $"({trigger})", shown);
                convo.Add(trigger switch { "hotkey" => "look", "pod" => "pod", "plan" => "plan", _ => "live" }, (int?)req?["cycle"] ?? ReadCycle(),
                          text ?? trigger switch { "hotkey" => "(pressed 'look at this')", "pod" => "(Printing Pod open)", "plan" => "(sent a sketch)", _ => "" }, shown);
                WriteStatus("replied", shown);
                if (m.Success)
                {
                    string topic = m.Groups[1].Value.Trim();
                    Enqueue("briefing", $"{topic} (the player asked: \"{text ?? trigger}\")");
                }
            }
            else { System("Advisor error: " + reply.Error); WriteStatus("error", reply.Error); }
        }
        finally { liveLock.Release(); }
    }

    async Task Pump()
    {
        while (queue.Count > 0)
        {
            var (trigger, text, req) = queue.Dequeue();
            status.Text = "preparing a briefing…";
            WriteStatus("thinking", null);
            var briefingFile = Path.Combine(liveDir, "briefing", "briefing.json");
            var before = File.Exists(briefingFile) ? File.GetLastWriteTimeUtc(briefingFile) : DateTime.MinValue;
            var reply = await claude.AskAsync(BuildPrompt(trigger, text, req), CancellationToken.None);
            try
            {
                // Log the briefing itself, so live mode knows what it said.
                if (File.Exists(briefingFile) && File.GetLastWriteTimeUtc(briefingFile) > before)
                    convo.Add("briefing", ReadCycle(), text ?? (trigger == "autosave" ? "new cycle" : null), ConversationLog.Summarize(File.ReadAllText(briefingFile)));
            }
            catch { }
            status.Text = "ready";
            if (reply.Ok)
            {
                Append("Advisor", reply.Text, Color.PaleGreen); // briefings are read step by step in the game
                WriteStatus("replied", reply.Text);
            }
            else { System("Advisor error: " + reply.Error); WriteStatus("error", reply.Error); }
        }
    }

    string BuildPrompt(string trigger, string? text, JsonObject? req, string? view = null, string? facts = null)
    {
        var sb = new System.Text.StringBuilder();
        int cycle = (int?)req?["cycle"] ?? ReadCycle();
        bool briefing = trigger is "autosave" or "briefing";
        sb.AppendLine($"[mode: {(briefing ? "briefing" : "live")}] [trigger: {trigger}] [cycle {cycle}] [{DateTime.Now:HH:mm}]");
        sb.AppendLine("Data: live/state.json (fresh). Map images: live/map/. MapQuery: dotnet \"" + MapQueryDll() + "\" <summary|region|cell|find> ...");
        if ((string?)req?["screenshot"] is { } shot) sb.AppendLine("Screenshot: " + RelativeToAdvisor(shot));
        switch (trigger)
        {
            case "autosave":
            case "briefing":
                if (trigger == "briefing" && text != null)
                    sb.AppendLine("Write a FOCUSED briefing to live/briefing/briefing.json (2-5 steps; see CLAUDE.md) answering this topic in depth: " + text + ". Use the player's view (live/view.json) and live queries for the places involved; draw marks and, if a layout is involved, a project. Reply with ONE sentence saying what the briefing covers.");
                else
                    sb.AppendLine("Write the cycle briefing to live/briefing/briefing.json (2-5 steps, Now -> Next -> Long term; see CLAUDE.md). Compare with the previous live/history snapshot if useful. Update the colony plan file if goals changed. Reply with ONE sentence saying what the briefing covers.");
                break;
            case "pod":
                sb.AppendLine("The Printing Pod selection is open: recommend one option from live/pod.json. One or two spoken sentences. If pod.json is old or missing, say so.");
                break;
            case "plan":
                sb.AppendLine("Review the player's sketch in live/plan/player-marks.json (they refer to marks as #1, #2, ...). One or two spoken sentences; draw corrections as advisor marks.");
                break;
            case "hotkey":
                sb.AppendLine("The player pressed 'look at this' in the game. Look at the screenshot and state; say the single most important thing in one or two spoken sentences.");
                break;
        }
        if (!briefing)
        {
            sb.AppendLine("Live mode: at most two short spoken sentences, no coordinates (draw a mark and say 'see the mark').");
            sb.AppendLine("Answer from the facts below (view, nearby buildings and gas, colony summary, recent conversation) and the ONI numbers in CLAUDE.md. \"Here\"/\"this\" = the cursor tile or the selection. If the facts don't cover the question, run ONE query rather than guess: dotnet \"" + MapQueryDll() + "\" live region|cell|buildings|find ... Stay consistent with the recent conversation, including briefings: build on it, and if you now think something said there was wrong, say so and why.");
            if ((string?)req?["screenshot"] is not null && trigger is "hotkey" or "plan") sb.AppendLine("The screenshot attached is what the player sees right now.");
            sb.AppendLine("Player's view: " + (view ?? ReadView()));
            if (!string.IsNullOrWhiteSpace(facts)) sb.AppendLine("Fetched from the game just now:\n" + facts.TrimEnd());
            sb.AppendLine("Colony summary: " + ColonyDigest.Build(liveDir));
        }
        string recent = convo.Recent(briefing ? 14 : 8, briefing ? 6000 : 3500);
        if (recent.Length > 0) sb.AppendLine("Recent conversation with the player (live answers and briefings, oldest first):\n" + recent);
        if (text != null && !briefing) sb.AppendLine("Player: " + text);
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
        live.Dispose();
        voice.Dispose();
        speech.Dispose();
        try { File.Delete(Path.Combine(liveDir, "advisor-status.json")); } catch { } // the game then shows "companion not running"
        settings.Save();
        base.OnFormClosed(e);
    }
}
