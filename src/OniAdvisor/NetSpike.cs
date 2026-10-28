using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OniAdvisor
{
    /// <summary>
    /// TEMPORARY feasibility test: can the mod talk to model APIs from inside the game? Runs once, a few seconds
    /// after a colony loads, only when advisor/netspike.json exists (renamed to netspike-done.json afterwards).
    /// Checks, all off the main thread:
    ///   1. HTTPS to the real APIs without a key (a 401/403 proves TLS and HTTP work on the game's Mono);
    ///   2. streaming from tools/LlmMock (Anthropic-style server-sent events) with a ~1.5 MB request body,
    ///      as a screenshot would be;
    ///   3. cancelling a stream halfway;
    ///   4. only if the ANTHROPIC_API_KEY environment variable is set: one real, tiny streamed call.
    /// Meanwhile it measures frame times, to see whether any of it makes the game stutter.
    /// Results: advisor/live/netspike-result.json.
    /// </summary>
    internal static class NetSpike
    {
        static string ConfigFile => Path.Combine(Path.GetDirectoryName(Paths.LiveDir), "netspike.json");

        static bool scheduled, running;
        static float startAt;
        static readonly object gate = new object();
        static readonly List<float> baseline = new List<float>(), during = new List<float>();
        static string toast;

        /// <summary>Every frame, from AdvisorRunner.</summary>
        public static void Frame(float now, float dt)
        {
            if (!scheduled)
            {
                scheduled = true;
                if (!File.Exists(ConfigFile)) { startAt = float.MaxValue; return; }
                startAt = now + 15f; // let the colony settle; the 15 s before are the frame-time baseline
                Log.Info("network spike scheduled");
            }
            lock (gate)
            {
                if (running) during.Add(dt);
                else if (startAt != float.MaxValue && now < startAt && now > startAt - 10f) baseline.Add(dt);
                if (toast != null) { AdvisorHud.Show(toast, 8f); toast = null; }
            }
            if (now >= startAt && startAt != float.MaxValue)
            {
                startAt = float.MaxValue;
                JObject config;
                try { config = JObject.Parse(File.ReadAllText(ConfigFile)); } catch { config = new JObject(); }
                running = true;
                AdvisorHud.Show("Advisor network test running…", 4f);
                Task.Run(() => Run(config));
            }
        }

        static async Task Run(JObject config)
        {
            JObject result;
            try { result = await NetChecks.RunAll(config); }
            catch (Exception e) { result = new JObject { ["error"] = e.ToString() }; }

            lock (gate)
            {
                running = false;
                result["frames"] = new JObject { ["baseline"] = Stats(baseline), ["duringTests"] = Stats(during) };
            }
            try
            {
                Paths.WriteAtomic(Path.Combine(Paths.LiveDir, "netspike-result.json"), result.ToString(Formatting.Indented));
                File.Move(ConfigFile, Path.Combine(Path.GetDirectoryName(ConfigFile), "netspike-done.json"));
            }
            catch (Exception e) { Log.Warn("network spike: could not save results: " + e.Message); }
            Log.Info("network spike finished");
            lock (gate) toast = "Advisor network test finished — results saved";
        }

        static JObject Stats(List<float> dts)
        {
            if (dts.Count == 0) return new JObject { ["frames"] = 0 };
            var ms = dts.Select(d => d * 1000f).OrderBy(x => x).ToList();
            return new JObject
            {
                ["frames"] = ms.Count,
                ["meanMs"] = Math.Round(ms.Average(), 1),
                ["p95Ms"] = Math.Round(ms[(int)(ms.Count * 0.95)], 1),
                ["maxMs"] = Math.Round(ms.Last(), 1),
                ["over50ms"] = ms.Count(x => x > 50),
            };
        }

    }
}
