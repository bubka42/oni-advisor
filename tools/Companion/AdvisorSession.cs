using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace OniAdvisor.Companion;

/// <summary>
/// The advisor: ONE long-running Claude Code conversation (stream-json in and out) for everything - questions,
/// look requests, sketch reviews, briefings, game updates. Like a Claude Code chat: it keeps its memory across
/// turns (and across restarts, by resuming the same session id), uses tools when it needs to look, and streams
/// its text. Events report the text as it is written and the tools it uses, for the chat panels.
/// </summary>
public sealed class AdvisorSession : IDisposable
{
    readonly Settings settings;
    readonly string workDir, dotnetDir;
    Process? proc;
    readonly SemaphoreSlim turnLock = new(1, 1);
    TaskCompletionSource<string>? pending;
    readonly object gate = new();
    int sent, results;
    public string? LastError { get; private set; }
    public bool Busy => turnLock.CurrentCount == 0;
    /// <summary>True when the current process started a brand-new conversation (nothing to remember yet).</summary>
    public bool FreshConversation { get; private set; }

    /// <summary>Text of the answer as it is being written (accumulated for the current text block).</summary>
    public event Action<string>? TextChanged;
    /// <summary>The advisor started using a tool: a short human description ("checking pipes near the generator").</summary>
    public event Action<string>? Activity;
    /// <summary>Text written before a tool call ("Let me check the pipes.") - shown as a thought, not the answer.</summary>
    public event Action<string>? Thought;

    StringBuilder block = new();

    public AdvisorSession(Settings settings, string workDir, string dotnetDir)
    {
        this.settings = settings;
        this.workDir = workDir;
        this.dotnetDir = dotnetDir;
    }

    public void WarmUp()
    {
        try { EnsureProcess(); } catch (Exception e) { LastError = e.Message; }
    }

    void EnsureProcess()
    {
        if (proc is { HasExited: false }) return;
        Stop();
        var exe = ClaudeRunner.FindClaude(settings.ClaudePath) ?? throw new InvalidOperationException("Claude Code (claude.exe) not found");
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        var args = new List<string> { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
                                      "--permission-mode", "dontAsk",
                                      "--allowedTools", "Read(./**) Glob Grep Write(./**) Edit(./**) Bash(dotnet *)",
                                      "--model", string.IsNullOrWhiteSpace(settings.Model) ? "sonnet" : settings.Model!,
                                      "--effort", string.IsNullOrWhiteSpace(settings.Effort) ? "medium" : settings.Effort! };
        // Resume the conversation if there is one, so a restart doesn't make the advisor forget.
        FreshConversation = string.IsNullOrWhiteSpace(settings.SessionId) || !SessionExists(settings.SessionId!);
        if (FreshConversation)
        {
            settings.SessionId = Guid.NewGuid().ToString();
            settings.Save();
            args.AddRange(new[] { "--session-id", settings.SessionId! });
        }
        else args.AddRange(new[] { "--resume", settings.SessionId! });
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = dotnetDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start claude");
        sent = results = 0;
        var p = proc;
        _ = Task.Run(() => ReadLoop(p));
        _ = Task.Run(() => { try { p.StandardError.ReadToEnd(); } catch { } });
    }

    /// <summary>Claude Code keeps each conversation as a .jsonl under ~/.claude/projects/&lt;encoded working dir&gt;/.</summary>
    bool SessionExists(string id)
    {
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
            return Directory.Exists(root) && Directory.EnumerateFiles(root, id + ".jsonl", SearchOption.AllDirectories).Any();
        }
        catch { return false; }
    }

    async Task ReadLoop(Process p)
    {
        try
        {
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
            {
                JsonObject? j;
                try { j = JsonNode.Parse(line)?.AsObject(); } catch { continue; }
                if (j == null) continue;
                switch ((string?)j["type"])
                {
                    case "stream_event":
                        var ev = j["event"];
                        string? evType = (string?)ev?["type"];
                        if (evType == "content_block_start")
                        {
                            // Text followed by a tool call was thinking aloud ("Let me check the pipes."), not the answer.
                            if ((string?)ev?["content_block"]?["type"] == "tool_use" && block.Length > 0)
                            {
                                Thought?.Invoke(block.ToString().Trim());
                                TextChanged?.Invoke("");
                            }
                            block.Clear();
                        }
                        else if (evType == "content_block_delta" && (string?)ev?["delta"]?["type"] == "text_delta")
                        {
                            block.Append((string?)ev?["delta"]?["text"]);
                            TextChanged?.Invoke(block.ToString());
                        }
                        break;
                    case "assistant":
                        foreach (var c in j["message"]?["content"]?.AsArray() ?? new JsonArray())
                            if ((string?)c?["type"] == "tool_use")
                                Activity?.Invoke(Describe((string?)c?["name"] ?? "", c?["input"]));
                        break;
                    case "result":
                        if (++results != sent) break; // a late result from a turn that was given up on
                        if ((bool?)j["is_error"] == true) Complete(null, (string?)j["result"] ?? "error");
                        else Complete((string?)j["result"] ?? "");
                        break;
                }
            }
        }
        catch { }
        Complete(null, "the advisor process stopped");
    }

    /// <summary>A tool call in a few words for the player.</summary>
    static string Describe(string tool, JsonNode? input)
    {
        string S(string k) => (string?)input?[k] ?? "";
        switch (tool)
        {
            case "Bash":
                var cmd = S("command");
                var m = global::System.Text.RegularExpressions.Regex.Match(cmd, @"MapQuery\.dll""?\s+(live\s+)?(\w+)(.*)");
                if (!m.Success) return "running a command";
                string what = m.Groups[2].Value, rest = m.Groups[3].Value.Trim();
                return what switch
                {
                    "pipes" => "checking the " + (rest.Split(' ').Length > 4 ? rest.Split(' ')[4] : "gas") + " network",
                    "ports" => "checking which ports are connected",
                    "buildings" => "looking at the buildings there",
                    "region" => "looking at the map" + (rest.EndsWith("gas") ? " (gases)" : rest.EndsWith("temp") ? " (temperatures)" : ""),
                    "cell" => "checking a tile",
                    "find" => "searching for " + rest.Split(' ')[0],
                    "rates" => "checking production and consumption",
                    "summary" => "reading the map summary",
                    _ => "querying the game",
                };
            case "Read":
                var f = Path.GetFileName(S("file_path"));
                return f.EndsWith(".png") || f.EndsWith(".jpg") ? "looking at the screenshot" : f == "state.json" ? "reading the colony state" : "reading " + f;
            case "Write":
            case "Edit":
                var w = Path.GetFileName(S("file_path"));
                return w switch
                {
                    "advisor-marks.json" => "drawing on your map",
                    "briefing.json" => "writing the briefing",
                    "projects.json" => "drafting a project",
                    _ when w.EndsWith("-plan.md") => "updating the colony plan",
                    _ => "writing " + w,
                };
            case "Grep": return "searching the colony data";
            case "Glob": return "looking for files";
            default: return tool;
        }
    }

    void Complete(string? text, string? error = null)
    {
        TaskCompletionSource<string>? tcs;
        lock (gate) { tcs = pending; pending = null; }
        if (tcs == null) return;
        if (text != null) tcs.TrySetResult(text);
        else { LastError = error; tcs.TrySetException(new Exception(error)); }
    }

    /// <param name="imagePath">a screenshot shown to the model directly</param>
    public async Task<ClaudeRunner.Reply> AskAsync(string prompt, CancellationToken ct, string? imagePath = null, int timeoutSeconds = 300)
    {
        await turnLock.WaitAsync(ct);
        var sw = Stopwatch.StartNew();
        try
        {
            EnsureProcess();
            block.Clear();
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) { pending = tcs; sent++; }
            JsonNode content = prompt;
            if (imagePath != null && Images.Jpeg(imagePath) is { } jpeg)
                content = new JsonArray(
                    new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = Convert.ToBase64String(jpeg) } },
                    new JsonObject { ["type"] = "text", ["text"] = prompt });
            var msg = new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = content } };
            await proc!.StandardInput.WriteLineAsync(msg.ToJsonString());
            await proc.StandardInput.FlushAsync();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using (timeout.Token.Register(() => tcs.TrySetCanceled()))
            {
                var text = await tcs.Task;
                if (text.Contains("Not logged in", StringComparison.OrdinalIgnoreCase))
                    return new ClaudeRunner.Reply(false, "", null, "Claude Code is not logged in for command-line use; run claude once and type /login.");
                FreshConversation = false;
                return new ClaudeRunner.Reply(true, text.Trim(), null, null) { Seconds = sw.Elapsed.TotalSeconds };
            }
        }
        catch (TaskCanceledException) { Stop(); return new ClaudeRunner.Reply(false, "", null, ct.IsCancellationRequested ? "stopped" : "timed out"); }
        catch (Exception e) { return new ClaudeRunner.Reply(false, "", null, e.Message); }
        finally { turnLock.Release(); }
    }

    /// <summary>Forget the conversation: the next turn starts a new one.</summary>
    public void NewConversation()
    {
        Stop();
        settings.SessionId = null;
        settings.Save();
    }

    public void Stop()
    {
        try { if (proc is { HasExited: false }) { proc.StandardInput.Close(); if (!proc.WaitForExit(1500)) proc.Kill(entireProcessTree: true); } } catch { }
        proc = null;
    }

    public void Dispose() => Stop();
}

static class Images
{
    /// <summary>The screenshot scaled to at most 1568 px on the long side, as JPEG (the model downsizes to that anyway).</summary>
    public static byte[]? Jpeg(string path)
    {
        try
        {
            using var src = Image.FromFile(path);
            double scale = Math.Min(1.0, 1568.0 / Math.Max(src.Width, src.Height));
            using var bmp = new Bitmap(src, (int)(src.Width * scale), (int)(src.Height * scale));
            using var ms = new MemoryStream();
            var codec = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            using var ps = new System.Drawing.Imaging.EncoderParameters(1);
            ps.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
            bmp.Save(ms, codec, ps);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
