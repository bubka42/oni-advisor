using System.IO;
using Newtonsoft.Json.Linq;

namespace OniAdvisor
{
    /// <summary>
    /// The display preferences the mod itself needs from advisor/companion.json (set in the mod options):
    /// re-read at most once a second, when the file changes.
    /// </summary>
    internal static class Prefs
    {
        static System.DateTime fileTime;
        static float checkedAt = -10f;
        static JObject json = new JObject();

        static JObject Json
        {
            get
            {
                float now = UnityEngine.Time.unscaledTime;
                if (now - checkedAt < 1f) return json;
                checkedAt = now;
                try
                {
                    var file = Path.Combine(Path.GetDirectoryName(Paths.LiveDir), "companion.json");
                    if (!File.Exists(file)) return json;
                    var t = File.GetLastWriteTimeUtc(file);
                    if (t != fileTime) { json = JObject.Parse(File.ReadAllText(file)); fileTime = t; }
                }
                catch { }
                return json;
            }
        }

        /// <summary>The advisor's marks look hand-drawn ("sketch", default) or geometric ("clean").</summary>
        public static bool SketchMarks => ((string)Json["MarkStyle"] ?? "sketch") != "clean";

        /// <summary>Briefing steps grey out everything but the area they are about.</summary>
        public static bool Spotlight => (bool?)Json["Spotlight"] ?? true;
    }
}
