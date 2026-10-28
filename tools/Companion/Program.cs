namespace OniAdvisor.Companion;

static class Program
{
    /// <summary>
    /// OniAdvisorCompanion [advisorDir] [--background] [--game-pid N]
    /// advisorDir defaults to Documents\Klei\OxygenNotIncluded\advisor (OneDrive-redirected Documents first).
    /// --background: started by the mod; the window starts hidden (tray icon; Ctrl+Shift+C in the game shows it).
    /// --game-pid: quit when that game process ends.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool background = args.Contains("--background");
        int gamePid = 0;
        int pidAt = Array.IndexOf(args, "--game-pid");
        if (pidAt >= 0 && pidAt + 1 < args.Length) int.TryParse(args[pidAt + 1], out gamePid);
        var positional = args.Where((a, i) => !a.StartsWith("--") && !(i > 0 && args[i - 1] == "--game-pid")).ToList();

        // One companion at a time (two would both answer every request from the game).
        using var single = new Mutex(true, "OniAdvisorCompanion-single-instance", out bool first);
        if (!first)
        {
            if (!background) MessageBox.Show("The ONI Advisor companion is already running (Ctrl+Shift+C in the game shows it).", "ONI Advisor");
            return;
        }

        string advisorDir = positional.Count > 0 ? positional[0] : DefaultAdvisorDir();
        Directory.CreateDirectory(advisorDir);

        string repoRoot = FindRepoRoot(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
        string dotnetDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") is { } d && File.Exists(Path.Combine(d, "dotnet.exe"))
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");

        Application.Run(new MainForm(advisorDir, repoRoot, dotnetDir, background, gamePid));
    }

    static string DefaultAdvisorDir()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var docs in new[] { Path.Combine(home, "OneDrive", "Documents"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
        {
            var oni = Path.Combine(docs, "Klei", "OxygenNotIncluded");
            if (Directory.Exists(oni)) return Path.Combine(oni, "advisor");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Klei", "OxygenNotIncluded", "advisor");
    }

    static string? FindRepoRoot(string start)
    {
        var dir = new DirectoryInfo(start);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "advisor-workspace"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
