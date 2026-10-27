using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OniAdvisor.Companion;

/// <summary>
/// Live mode: one long-running Claude Code process (stream-json in and out) so a question costs only the
/// model's answer time, not Claude Code's ~2-7 s start-up. A fast model at low effort by default. The answer
/// is taken from the first assistant message that is pure text - the companion does not wait for Claude
/// Code's post-turn bookkeeping. A fresh live session is started every ~25 questions to keep it small.
/// </summary>
public sealed class LiveClaude : IDisposable
{
    readonly Settings settings;
    readonly string workDir, dotnetDir;
    Process? proc;
    volatile int assistantSeq;
    int turns, sent, results; // results = turns finished, so a late event from an earlier turn never answers this one
    TaskCompletionSource<string>? pending;
    readonly object gate = new();
    public string? LastError { get; private set; }

    public LiveClaude(Settings settings, string workDir, string dotnetDir)
    {
        this.settings = settings;
        this.workDir = workDir;
        this.dotnetDir = dotnetDir;
    }

    /// <summary>Starts the process ahead of the first question (start-up is the slow part).</summary>
    public void WarmUp()
    {
        try { EnsureProcess(); } catch (Exception e) { LastError = e.Message; }
    }

    void EnsureProcess()
    {
        if (proc is { HasExited: false } && turns < 25) return;
        Stop();
        var exe = ClaudeRunner.FindClaude(settings.ClaudePath) ?? throw new InvalidOperationException("Claude Code (claude.exe) not found");
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                                  "--permission-mode", "dontAsk",
                                  "--allowedTools", "Read(./**) Glob Grep Write(./live/plan/**) Bash(dotnet *)",
                                  "--model", string.IsNullOrWhiteSpace(settings.LiveModel) ? "sonnet" : settings.LiveModel!,
                                  "--effort", string.IsNullOrWhiteSpace(settings.LiveEffort) ? "low" : settings.LiveEffort!,
                                  "--session-id", Guid.NewGuid().ToString() })
            psi.ArgumentList.Add(a);
        psi.Environment["PATH"] = dotnetDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start claude");
        turns = 0; sent = 0; results = 0;
        var p = proc;
        _ = Task.Run(() => ReadLoop(p));
        _ = Task.Run(() => { try { p.StandardError.ReadToEnd(); } catch { } });
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
                string type = (string?)j["type"] ?? "";
                if (type == "assistant")
                {
                    // The answer = a text-only assistant message that nothing follows within a short grace period.
                    // Claude Code streams each content block as its own message without a stop reason, so a
                    // "Let me check…" line is followed by its tool call; any later message cancels the candidate.
                    var content = j["message"]?["content"]?.AsArray();
                    if (content == null || results != sent - 1) continue;
                    int seq = ++assistantSeq;
                    bool toolUse = content.Any(c => (string?)c?["type"] == "tool_use");
                    var text = string.Concat(content.Where(c => (string?)c?["type"] == "text").Select(c => (string?)c?["text"])).Trim();
                    if (!toolUse && text.Length > 0)
                        _ = Task.Delay(600).ContinueWith(_ => { if (assistantSeq == seq) Complete(text); });
                }
                else if (type == "result")
                {
                    ++assistantSeq; // the turn is over: the result text wins over a pending candidate
                    if (++results != sent) continue;
                    if ((bool?)j["is_error"] == true) Complete(null, (string?)j["result"] ?? "error");
                    else Complete((string?)j["result"] ?? "");
                }
            }
        }
        catch { }
        Complete(null, "the live advisor process stopped");
    }

    void Complete(string? text, string? error = null)
    {
        TaskCompletionSource<string>? tcs;
        lock (gate) { tcs = pending; pending = null; }
        if (tcs == null) return;
        if (text != null) tcs.TrySetResult(text);
        else { LastError = error; tcs.TrySetException(new Exception(error)); }
    }

    /// <param name="imagePath">a screenshot shown to the model directly (left to itself, it never opened them)</param>
    public async Task<ClaudeRunner.Reply> AskAsync(string prompt, CancellationToken ct, string? imagePath = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            EnsureProcess();
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) { pending = tcs; sent++; }
            JsonNode content = prompt;
            if (imagePath != null && Jpeg(imagePath) is { } jpeg)
                content = new JsonArray(
                    new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = Convert.ToBase64String(jpeg) } },
                    new JsonObject { ["type"] = "text", ["text"] = prompt });
            var msg = new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = content } };
            await proc!.StandardInput.WriteLineAsync(msg.ToJsonString());
            await proc.StandardInput.FlushAsync();
            turns++;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            using (timeout.Token.Register(() => tcs.TrySetCanceled()))
            {
                var text = await tcs.Task;
                if (text.Contains("Not logged in", StringComparison.OrdinalIgnoreCase))
                    return new ClaudeRunner.Reply(false, "", null, "Claude Code is not logged in for command-line use; run claude once and type /login.");
                return new ClaudeRunner.Reply(true, text, null, null) { Seconds = sw.Elapsed.TotalSeconds };
            }
        }
        catch (TaskCanceledException) { Stop(); return new ClaudeRunner.Reply(false, "", null, ct.IsCancellationRequested ? "cancelled" : "timed out"); }
        catch (Exception e) { return new ClaudeRunner.Reply(false, "", null, e.Message); }
    }

    /// <summary>The screenshot scaled to at most 1568 px on the long side, as JPEG (the model downsizes to that anyway).</summary>
    static byte[]? Jpeg(string path)
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

    public void Stop()
    {
        try { if (proc is { HasExited: false }) { proc.StandardInput.Close(); if (!proc.WaitForExit(1500)) proc.Kill(entireProcessTree: true); } } catch { }
        proc = null;
    }

    public void Dispose() => Stop();
}
