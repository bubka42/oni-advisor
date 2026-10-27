namespace OniAdvisor.Companion;

static class Program
{
    /// <summary>
    /// OniAdvisorCompanion [advisorDir]
    /// advisorDir defaults to Documents\Klei\OxygenNotIncluded\advisor (OneDrive-redirected Documents first).
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // One companion at a time (two would both answer every request from the game).
        using var single = new Mutex(true, "OniAdvisorCompanion-single-instance", out bool first);
        if (!first)
        {
            MessageBox.Show("The ONI Advisor companion is already running (Ctrl+Shift+C in the game shows it).", "ONI Advisor");
            return;
        }

        string advisorDir = args.Length > 0 ? args[0] : DefaultAdvisorDir();
        Directory.CreateDirectory(advisorDir);

        string repoRoot = FindRepoRoot(AppContext.BaseDirectory) ?? AppContext.BaseDirectory;
        string dotnetDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") is { } d && File.Exists(Path.Combine(d, "dotnet.exe"))
            ? d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");

        Application.Run(new MainForm(advisorDir, repoRoot, dotnetDir));
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
