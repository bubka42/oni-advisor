using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Newtonsoft.Json.Linq;

namespace OniAdvisor
{
    /// <summary>Reads the Printing Pod options from the open selection screen. The options only exist while it is open.</summary>
    internal static class PodReader
    {
        public static bool IsOpen()
        {
            var s = ImmigrantScreen.instance;
            return s != null && s.gameObject.activeInHierarchy;
        }

        public static JObject Read()
        {
            var screen = ImmigrantScreen.instance;
            var containers = Traverse.Create(screen).Field("containers").GetValue<List<ITelepadDeliverableContainer>>();
            var options = new JArray();
            if (containers != null)
            {
                foreach (var c in containers)
                {
                    if (c is CharacterContainer cc && cc.Stats != null) options.Add(Dupe(cc.Stats));
                    else if (c is CarePackageContainer cp && cp.Info != null) options.Add(Package(cp.Info));
                }
            }
            return new JObject
            {
                ["cycle"] = GameClock.Instance.GetCycle() + 1,
                ["options"] = options,
            };
        }

        static JObject Dupe(MinionStartingStats s)
        {
            var o = new JObject
            {
                ["kind"] = "duplicant",
                ["name"] = s.Name,
                ["interests"] = new JArray(s.skillAptitudes.Where(kv => kv.Value > 0f).Select(kv => Strip(kv.Key.Name))),
                ["traits"] = new JArray(s.Traits.Where(t => t != null).Select(t => (t.PositiveTrait ? "+" : "-") + Strip(t.Name))),
                ["stressReaction"] = Strip(s.stressTrait?.Name),
                ["overjoyedResponse"] = Strip(s.joyTrait?.Name),
            };
            var levels = new JObject();
            foreach (var kv in s.StartingLevels.Where(kv => kv.Value != 0)) levels[kv.Key] = kv.Value;
            o["attributeLevels"] = levels;
            return o;
        }

        static string Strip(string s) => string.IsNullOrEmpty(s) ? s : STRINGS.UI.StripLinkFormatting(s).Trim();

        static JObject Package(CarePackageInfo info) => new JObject
        {
            ["kind"] = "care package",
            ["item"] = TagManager.GetProperName(new Tag(info.id), true),
            ["quantity"] = info.quantity,
        };
    }
}
