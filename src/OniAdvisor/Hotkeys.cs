using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace OniAdvisor
{
    /// <summary>
    /// The advisor's hotkeys, set in the mod options (companion.json: "HotkeyPreset" standard | simple | custom,
    /// and "Hotkeys" {action: "Ctrl+Shift+T", ...} for custom). Standard: Ctrl+Shift+letter, out of the game's
    /// way. Simple: single keys, which take those keys from the game (e.g. G is no longer Dig). Custom: the
    /// standard set with the player's changes. While a key bound here is pressed, the game doesn't see it.
    /// </summary>
    internal static class Hotkeys
    {
        internal sealed class Binding
        {
            public bool Ctrl, Shift, Alt;
            public KeyCode Key = KeyCode.None;
            public override string ToString() => Key == KeyCode.None ? "none" :
                (Ctrl ? "Ctrl+" : "") + (Alt ? "Alt+" : "") + (Shift ? "Shift+" : "") + KeyName(Key);
        }

        /// <summary>Action ids, with what they do (for the options).</summary>
        internal static readonly (string id, string label)[] Actions =
        {
            ("panel", "Advisor panel"), ("briefing", "Briefing"), ("talk", "Talk (hold)"), ("look", "Look at this"),
            ("plan", "Planning mode"), ("guide", "Guided planning"), ("overlay", "Advisor overlay"),
            ("projects", "Project focus"), ("voice", "Voice on/off"), ("companion", "Companion window"),
            ("clearMarks", "Clear my marks (planning)"),
        };

        static readonly Dictionary<string, string> Standard = new Dictionary<string, string>
        {
            ["panel"] = "Ctrl+Shift+T", ["briefing"] = "Ctrl+Shift+B", ["talk"] = "Ctrl+Shift+V", ["look"] = "Ctrl+Shift+A",
            ["plan"] = "Ctrl+Shift+P", ["guide"] = "Ctrl+Shift+G", ["overlay"] = "Ctrl+Shift+M", ["projects"] = "Ctrl+Shift+N",
            ["voice"] = "Ctrl+Shift+Q", ["companion"] = "Ctrl+Shift+C", ["clearMarks"] = "Ctrl+Shift+X",
        };

        static readonly Dictionary<string, string> Simple = new Dictionary<string, string>
        {
            ["panel"] = "T", ["briefing"] = "B", ["talk"] = "V", ["look"] = "L",
            ["plan"] = "P", ["guide"] = "G", ["overlay"] = "M", ["projects"] = "N",
            ["voice"] = "Q", ["companion"] = "Ctrl+Shift+C", ["clearMarks"] = "X",
        };

        static Dictionary<string, Binding> current;
        static string currentKey;

        /// <summary>The bindings in force (re-read when the options change).</summary>
        static Dictionary<string, Binding> Current
        {
            get
            {
                string preset = Prefs.HotkeyPreset;
                var custom = Prefs.CustomHotkeys;
                string key = preset + "|" + (custom?.ToString(Newtonsoft.Json.Formatting.None) ?? "");
                if (current != null && key == currentKey) return current;
                currentKey = key;
                var src = preset == "simple" ? Simple : Standard;
                current = new Dictionary<string, Binding>();
                foreach (var kv in src) current[kv.Key] = Parse(kv.Value);
                if (preset == "custom" && custom != null)
                    foreach (var p in custom.Properties()) if (current.ContainsKey(p.Name)) current[p.Name] = Parse((string)p.Value);
                return current;
            }
        }

        public static Binding Get(string action) => Current.TryGetValue(action, out var b) ? b : new Binding();

        /// <summary>The preset's bindings as text (for the options).</summary>
        public static string Describe(string action) => Get(action).ToString();

        static bool Mods(Binding b)
        {
            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            return ctrl == b.Ctrl && shift == b.Shift && alt == b.Alt;
        }

        public static bool Down(string action)
        {
            if (GameTextFocused) return false;
            var b = Get(action);
            return b.Key != KeyCode.None && Input.GetKeyDown(b.Key) && Mods(b);
        }

        /// <summary>For push-to-talk: the key is held (modifiers may already be released).</summary>
        public static bool Held(string action)
        {
            var b = Get(action);
            return b.Key != KeyCode.None && Input.GetKey(b.Key);
        }

        /// <summary>A text box of the game (renaming a duplicant, the build search…) has focus: letters are for it, not for us.</summary>
        public static bool GameTextFocused
        {
            get
            {
                var go = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
                if (go == null) return false;
                var tmp = go.GetComponent<TMPro.TMP_InputField>();
                if (tmp != null && tmp.isFocused) return true;
                var ui = go.GetComponent<UnityEngine.UI.InputField>();
                return ui != null && ui.isFocused;
            }
        }

        /// <summary>Whether a key event the game is about to handle is one of ours this frame (then the game doesn't get it).</summary>
        public static bool IsOursNow()
        {
            if (GameTextFocused) return false;
            foreach (var b in Current.Values)
                if (b.Key != KeyCode.None && (Input.GetKeyDown(b.Key) || Input.GetKeyUp(b.Key)) && Mods(b)) return true;
            return false;
        }

        public static Binding Parse(string text)
        {
            var b = new Binding();
            if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return b;
            foreach (var part in text.Split('+').Select(p => p.Trim()))
            {
                switch (part.ToLowerInvariant())
                {
                    case "ctrl": case "control": b.Ctrl = true; break;
                    case "shift": b.Shift = true; break;
                    case "alt": b.Alt = true; break;
                    default:
                        if (part.Length == 1 && char.IsDigit(part[0])) b.Key = KeyCode.Alpha0 + (part[0] - '0');
                        else if (Enum.TryParse(part, true, out KeyCode k)) b.Key = k;
                        break;
                }
            }
            return b;
        }

        static string KeyName(KeyCode k) => k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9 ? ((int)(k - KeyCode.Alpha0)).ToString() : k.ToString();

        /// <summary>The key being pressed right now with its modifiers (the options' "press a key"), or null.</summary>
        public static string Capture()
        {
            foreach (KeyCode k in Enum.GetValues(typeof(KeyCode)))
            {
                if (k == KeyCode.None || k >= KeyCode.Mouse0 || IsModifier(k) || !Input.GetKeyDown(k)) continue;
                var b = new Binding
                {
                    Key = k,
                    Ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl),
                    Shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift),
                    Alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt),
                };
                return b.ToString();
            }
            return null;
        }

        static bool IsModifier(KeyCode k) => k == KeyCode.LeftControl || k == KeyCode.RightControl || k == KeyCode.LeftShift ||
            k == KeyCode.RightShift || k == KeyCode.LeftAlt || k == KeyCode.RightAlt || k == KeyCode.LeftCommand || k == KeyCode.RightCommand;

        /// <summary>The custom set to start editing from: the preset in force, so switching to custom changes nothing yet.</summary>
        public static JObject CurrentAsJson()
        {
            var o = new JObject();
            foreach (var (id, _) in Actions) o[id] = Describe(id);
            return o;
        }
    }
}
