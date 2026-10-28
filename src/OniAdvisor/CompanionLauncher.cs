using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;

namespace OniAdvisor
{
    /// <summary>
    /// Starts the companion app in the background when the game loads the mod, so the player never has to.
    /// Its location comes from companion-path.txt next to the mod (written by the mod's Deploy build). It runs
    /// windowless (the WinExe apphost, not dotnet.exe, so no console) with "--background" (window hidden, tray
    /// icon only) and "--game-pid" (it quits when the game does). Turned off by "AutoStart": false in
    /// advisor/companion.json. A second companion exits by itself, so a manually started one is fine.
    /// UNTESTED IN GAME.
    /// </summary>
    internal static class CompanionLauncher
    {
        public static void EnsureRunning(string modDir)
        {
            try
            {
                if (!AutoStart()) { Log.Info("companion auto-start is off"); return; }
                if (Process.GetProcessesByName("OniAdvisorCompanion").Length > 0) { Log.Info("companion already running"); return; }
                var pathFile = Path.Combine(modDir, "companion-path.txt");
                if (!File.Exists(pathFile)) { Log.Warn("no companion-path.txt; start the companion by hand"); return; }
                var exe = File.ReadAllText(pathFile).Trim();
                if (!File.Exists(exe)) { Log.Warn("companion not found at " + exe); return; }

                var psi = new ProcessStartInfo(exe, "--background --game-pid " + Process.GetCurrentProcess().Id)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                };
                // .NET is installed per user (%USERPROFILE%\.dotnet), which the apphost only finds through DOTNET_ROOT.
                var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
                if (Directory.Exists(dotnet)) psi.EnvironmentVariables["DOTNET_ROOT"] = dotnet;
                Process.Start(psi);
                Log.Info("started the companion: " + exe);
            }
            catch (Exception e) { Log.Warn("could not start the companion: " + e.Message); }
        }

        static bool AutoStart()
        {
            try
            {
                var file = Path.Combine(Path.GetDirectoryName(Paths.LiveDir), "companion.json");
                if (File.Exists(file)) return (bool?)JObject.Parse(File.ReadAllText(file))["AutoStart"] ?? true;
            }
            catch { }
            return true;
        }
    }
}
