using System;
using System.Collections;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace OniAdvisor
{
    /// <summary>
    /// Lives on the Game object and drives everything:
    ///   - state.json every 60 s; snapshot + screenshot + maps at autosave and on Ctrl+Shift+A
    ///   - map memory recording (every frame, throttled inside MapMemory)
    ///   - Printing Pod options when the selection screen opens
    ///   - planning mode (Ctrl+Shift+P), overlay cycling (Ctrl+Shift+M), project focus (Ctrl+Shift+N),
    ///     clear my marks (Ctrl+Shift+X)
    ///   - polling the advisor's files (marks, projects, commands, config) once a second
    ///   - standing rules every 5 s
    /// Events the advisor should react to drop a file into requests/.
    /// </summary>
    public sealed class AdvisorRunner : MonoBehaviour
    {
        public static AdvisorRunner Instance { get; private set; }

        const float PeriodicSeconds = 60f;
        const float MemorySaveSeconds = 300f;
        const float PollSeconds = 1f;
        const float RuleSeconds = 5f;
        const float PodReadDelaySeconds = 1.5f; // containers are generated a moment after the screen opens

        float nextPeriodic, nextMemorySave, nextPoll, nextRules;
        bool started;
        bool podOpen;
        float podOpenedAt;
        bool podDumped;

        void Awake() => Instance = this;

        void OnDestroy()
        {
            try { if (PlanMode.Active) PlanMode.Exit(send: false); } catch { }
            try { MapMemory.Shutdown(); } catch (Exception e) { Log.Warn("memory shutdown failed: " + e.Message); }
            try { PlanMarks.DestroyAll(); ProjectStore.Clear(); MarkRenderer.DestroyAll(); Overlays.Reset(); Config.Unfix(); AdvisorHud.Destroy(); }
            catch (Exception e) { Log.Warn("cleanup failed: " + e.Message); }
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            float now = Time.unscaledTime;

            if (!started && GameClock.Instance != null && Grid.CellCount > 0)
            {
                try
                {
                    MapMemory.Init(); // also loads config.json
                    PlanMarks.LoadPlayer();
                    Overlays.Apply();
                    nextMemorySave = now + MemorySaveSeconds;
                    started = true;
                }
                catch (Exception e) { Log.Warn("start-up failed: " + e); enabled = false; return; }
            }
            if (!started) return;

            Guard("memory tick", () => MapMemory.Current?.Tick(now));
            Guard("hud", AdvisorHud.Tick);
            Guard("overlay", () => Overlays.Tick(now));

            if (now >= nextMemorySave)
            {
                nextMemorySave = now + MemorySaveSeconds;
                Guard("memory save", () => { if (MapMemory.Current != null && !MapMemory.Current.Live) MapMemory.Current.SaveToDisk(); });
            }

            if (now >= nextPeriodic)
            {
                nextPeriodic = now + PeriodicSeconds;
                Export("periodic", screenshot: false, request: false);
            }

            if (now >= nextPoll)
            {
                nextPoll = now + PollSeconds;
                Guard("config", Config.ReloadIfChanged);
                Guard("advisor marks", PlanMarks.PollAdvisorFile);
                Guard("projects", ProjectStore.Poll);
                Guard("commands", Commands.Poll);
                Guard("advisor status", AdvisorHud.PollStatus);
            }

            if (now >= nextRules)
            {
                nextRules = now + RuleSeconds;
                Guard("rules", Commands.RunRules);
            }

            Hotkeys();
            Guard("planning fallback", PlanMode.FallbackUpdate);
            WatchPod();
        }

        bool talking;

        void Hotkeys()
        {
            // Push-to-talk: recording runs while V is held (Ctrl/Shift may be let go first).
            if (talking && !Input.GetKey(KeyCode.V))
            {
                talking = false;
                WriteRequest("voice-stop", null);
            }

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!ctrl || !shift) return;
            if (Input.GetKeyDown(KeyCode.A)) { AdvisorHud.Show("Sent to the advisor: snapshot, screenshot and maps"); Export("hotkey", screenshot: true, request: true); }
            if (Input.GetKeyDown(KeyCode.V) && !talking) { talking = true; WriteRequest("voice-start", null); }
            if (Input.GetKeyDown(KeyCode.C)) WriteRequest("companion", null);
            if (Input.GetKeyDown(KeyCode.P)) Guard("planning toggle", PlanMode.Toggle);
            if (Input.GetKeyDown(KeyCode.M)) Guard("overlay cycle", () => { Overlays.Cycle(); AdvisorHud.Show("Advisor overlay: " + Overlays.Describe(), 2.5f); });
            if (Input.GetKeyDown(KeyCode.N)) Guard("project focus", () => AdvisorHud.Show("Projects: " + ProjectStore.CycleFocus(), 2.5f));
            if (Input.GetKeyDown(KeyCode.X) && PlanMode.Active) Guard("clear marks", () => { PlanMarks.ClearPlayer(); AdvisorHud.Show("Your marks cleared", 2f); });
        }

        static void Guard(string what, System.Action a)
        {
            try { a(); }
            catch (Exception e) { Log.Warn($"{what} failed: {e.Message}"); }
        }

        public void OnAutosave()
        {
            AdvisorHud.Show($"Cycle {GameClock.Instance.GetCycle() + 1}: snapshot sent to the advisor", 3f);
            Export("autosave", screenshot: true, request: true, history: true);
        }

        /// <summary>Fresh snapshot + maps + screenshot, then a request file (used by planning mode).</summary>
        public void RequestWithScreenshot(string trigger)
        {
            Export(trigger, screenshot: true, request: true);
        }

        void WatchPod()
        {
            bool open;
            try { open = PodReader.IsOpen(); } catch { open = false; }
            if (open && !podOpen) { podOpenedAt = Time.unscaledTime; podDumped = false; }
            podOpen = open;
            if (!open || podDumped || Time.unscaledTime - podOpenedAt < PodReadDelaySeconds) return;

            podDumped = true;
            try
            {
                var pod = PodReader.Read();
                Paths.WriteAtomic(Path.Combine(Paths.LiveDir, "pod.json"), pod.ToString(Formatting.Indented));
                WriteRequest("pod", null);
                AdvisorHud.Show("Printing Pod options sent to the advisor");
            }
            catch (Exception e) { Log.Warn("pod read failed: " + e); }
        }

        void Export(string trigger, bool screenshot, bool request, bool history = false)
        {
            try
            {
                var snap = Snapshot.Build(trigger);
                string json = snap.ToString(Formatting.Indented);
                Paths.WriteAtomic(Path.Combine(Paths.LiveDir, "state.json"), json);
                int cycle = GameClock.Instance.GetCycle() + 1;
                if (history)
                    Paths.WriteAtomic(Path.Combine(Paths.Sub("history"), $"cycle-{cycle:D4}.json"), json);

                if (screenshot) // autosave, hotkey and plan also refresh the map images
                {
                    try
                    {
                        MapExport.WriteAll(cycle);
                        if (MapMemory.Current != null && !MapMemory.Current.Live && trigger == "autosave")
                            MapMemory.Current.SaveToDisk();
                    }
                    catch (Exception e) { Log.Warn("map export failed: " + e); }
                }

                string shot = null;
                if (screenshot)
                {
                    string name = trigger == "autosave"
                        ? $"autosave-cycle-{cycle:D4}.png"
                        : $"{trigger}-c{cycle:D4}-{Paths.Stamp()}.png";
                    shot = Path.Combine(Paths.Sub("screenshots"), name);
                }

                if (shot != null) StartCoroutine(CaptureThenRequest(shot, trigger, request));
                else if (request) WriteRequest(trigger, null);
            }
            catch (Exception e) { Log.Warn($"export ({trigger}) failed: {e}"); }
        }

        IEnumerator CaptureThenRequest(string path, string trigger, bool request)
        {
            ScreenCapture.CaptureScreenshot(path);
            // The file is written at the end of a later frame; wait for it (max ~5 s real time).
            float deadline = Time.unscaledTime + 5f;
            while (!File.Exists(path) && Time.unscaledTime < deadline)
                yield return null;
            yield return new WaitForSecondsRealtime(0.3f);
            if (request) WriteRequest(trigger, File.Exists(path) ? path : null);
        }

        static void WriteRequest(string trigger, string screenshot)
        {
            try
            {
                var req = new JObject
                {
                    ["trigger"] = trigger,
                    ["cycle"] = GameClock.Instance.GetCycle() + 1,
                    ["state"] = Path.Combine(Paths.LiveDir, "state.json"),
                    ["screenshot"] = screenshot,
                    ["pod"] = trigger == "pod" ? Path.Combine(Paths.LiveDir, "pod.json") : null,
                    ["marks"] = trigger == "plan" ? Path.Combine(Paths.LiveDir, "plan", "player-marks.json") : null,
                    ["maps"] = screenshot != null ? Path.Combine(Paths.LiveDir, "map") : null,
                    ["createdUtc"] = System.DateTime.UtcNow.ToString("o"),
                };
                Paths.WriteAtomic(Path.Combine(Paths.Sub("requests"), $"{Paths.Stamp()}-{trigger}.json"), req.ToString(Formatting.Indented));
            }
            catch (Exception e) { Log.Warn("request write failed: " + e); }
        }
    }
}
