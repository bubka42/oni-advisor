using System;
using System.IO;
using System.Text;

namespace OniAdvisor
{
    /// <summary>
    /// Output layout, under &lt;Klei/OxygenNotIncluded&gt;/advisor/live:
    ///   state.json            latest snapshot (any trigger)
    ///   pod.json              latest Printing Pod options (written when the selection screen is open)
    ///   history/cycle-NNNN.json   snapshot taken at each autosave
    ///   screenshots/*.png     autosave and hotkey screenshots
    ///   requests/*.json       one small file per event the advisor should react to
    /// </summary>
    internal static class Paths
    {
        static string root;

        public static string LiveDir
        {
            get
            {
                if (root == null)
                {
                    string kleiRoot;
                    try { kleiRoot = Path.GetDirectoryName(KMod.Manager.GetDirectory().TrimEnd('/', '\\')); }
                    catch { kleiRoot = Util.RootFolder(); }
                    root = Path.Combine(Path.Combine(kleiRoot, "advisor"), "live");
                }
                return root;
            }
        }

        public static string Sub(string name)
        {
            var dir = Path.Combine(LiveDir, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>Writes via a temp file so readers never see a half-written file.</summary>
        public static void WriteAtomic(string path, string contents)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, contents, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static string Stamp() => System.DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
    }
}
