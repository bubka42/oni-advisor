using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace OniAdvisor.Companion;

/// <summary>
/// Runs Claude Code headlessly (`claude -p --output-format json`) in the advisor folder, continuing one
/// conversation via --session-id / --resume. The prompt goes in on stdin. Tool access is limited to
/// reading and writing inside the advisor folder plus running `dotnet` (for MapQuery).
/// UNTESTED with a live game.
/// </summary>
public sealed class ClaudeRunner
{
    readonly Settings settings;
    readonly string workDir;
    readonly string dotnetDir;
    Process? current;

    public ClaudeRunner(Settings settings, string workDir, string dotnetDir)
    {
        this.settings = settings;
        this.workDir = workDir;
        this.dotnetDir = dotnetDir;
    }

    public bool Busy => current != null;

    public sealed record Reply(bool Ok, string Text, double? CostUsd, string? Error) { public double? Seconds { get; init; } }

    public static string? FindClaude(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var p = Path.Combine(dir, "claude.exe");
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        // The Claude desktop app ships Claude Code as ...\claude-code\<version>\<hash>\claude.exe. Where that folder
        // lives depends on the app version and on whether you look from inside the app's package container.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = new List<string> { Path.Combine(roaming, "Claude", "claude-code") };
        try { roots.AddRange(Directory.GetDirectories(Path.Combine(local, "Claude-Data")).Select(d => Path.Combine(d, "claude-code"))); } catch { }
        try { roots.AddRange(Directory.GetDirectories(Path.Combine(local, "Packages"), "Claude_*").Select(d => Path.Combine(d, "LocalCache", "Roaming", "Claude", "claude-code"))); } catch { }

        var found = new List<string>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            try { found.AddRange(Directory.EnumerateFiles(root, "claude.exe", SearchOption.AllDirectories)); } catch { }
        }
        // Newest version folder first (claude-code\<version>\<hash>\claude.exe).
        return found
            .OrderByDescending(f => Version.TryParse(new DirectoryInfo(Path.GetDirectoryName(f)!).Parent?.Name, out var v) ? v : new Version(0, 0))
            .ThenByDescending(f => File.GetLastWriteTimeUtc(f))
            .FirstOrDefault();
    }

    public async Task<Reply> AskAsync(string prompt, CancellationToken ct)
    {
        var exe = FindClaude(settings.ClaudePath);
        if (exe == null) return new Reply(false, "", null, "Claude Code (claude.exe) not found. Set ClaudePath in advisor/companion.json.");

        bool resume = !string.IsNullOrEmpty(settings.SessionId);
        if (!resume) settings.SessionId = Guid.NewGuid().ToString();

        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("json");
        psi.ArgumentList.Add(resume ? "--resume" : "--session-id");
        psi.ArgumentList.Add(settings.SessionId!);
        psi.ArgumentList.Add("--permission-mode");
        psi.ArgumentList.Add("dontAsk");
        psi.ArgumentList.Add("--allowedTools");
        psi.ArgumentList.Add("Read(./**) Glob Grep Write(./**) Edit(./**) Bash(dotnet *)");
        if (!string.IsNullOrWhiteSpace(settings.Model)) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(settings.Model!); }
        psi.Environment["PATH"] = dotnetDir + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["DOTNET_NOLOGO"] = "1";

        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start claude");
            current = proc;
            await proc.StandardInput.WriteAsync(prompt);
            proc.StandardInput.Close();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, settings.TimeoutSeconds)));
            try { await proc.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new Reply(false, "", null, ct.IsCancellationRequested ? "cancelled" : "timed out");
            }
            string stdout = await stdoutTask, stderr = await stderrTask;
            return Parse(stdout, stderr, proc.ExitCode);
        }
        catch (Exception e) { return new Reply(false, "", null, e.Message); }
        finally { current = null; }
    }

    public void Cancel()
    {
        try { current?.Kill(entireProcessTree: true); } catch { }
    }

    Reply Parse(string stdout, string stderr, int exitCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            string text = root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
            if (root.TryGetProperty("session_id", out var sid) && sid.GetString() is { } s && s.Length > 0)
                settings.SessionId = s;
            settings.Save();
            double? cost = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;
            bool isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            if (isError && text.Contains("Not logged in", StringComparison.OrdinalIgnoreCase))
            {
                settings.SessionId = null; // nothing useful was recorded
                settings.Save();
                return new Reply(false, "", null,
                    "Claude Code is not logged in for command-line use (the desktop app keeps its own login). " +
                    "One time only: open a terminal, run \"" + (FindClaude(settings.ClaudePath) ?? "claude") + "\", type /login and follow the browser prompt.");
            }
            return new Reply(!isError, text, cost, isError ? text : null);
        }
        catch
        {
            var err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            if (exitCode != 0 && err.Contains("session", StringComparison.OrdinalIgnoreCase))
                settings.SessionId = null; // e.g. the session file is gone; start fresh next time
            settings.Save();
            return new Reply(false, "", null, $"claude exited {exitCode}: {Trim(err)}");
        }
    }

    static string Trim(string s) => s.Length > 600 ? s[..600] + "…" : s.Trim();
}
