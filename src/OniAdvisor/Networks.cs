using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace OniAdvisor
{
    /// <summary>
    /// Pipes, wires and conveyor rails for the advisor: their layout, what flows in them, which building ports
    /// they reach, and what each building converts. Like buildings, the player's own networks are drawn live
    /// (on revealed tiles only); pipe contents are what the ventilation/plumbing overlays show.
    /// </summary>
    internal static class Networks
    {
        sealed class Layer
        {
            public string Name;
            public ObjectLayer Objects, Bridges;
            public Func<int, UtilityConnections> Connections;
            public Func<int, UtilityNetwork> Network;
            public ConduitFlow Flow; // null for wires and rails
            public float FullMass;   // one pipe packet
        }

        static Layer Get(string name)
        {
            var g = Game.Instance;
            switch (name)
            {
                case "gas": return new Layer { Name = "gas", Objects = ObjectLayer.GasConduit, Bridges = ObjectLayer.GasConduitConnection, Connections = c => g.gasConduitSystem.GetConnections(c, true), Network = c => g.gasConduitSystem.GetNetworkForCell(c), Flow = g.gasConduitFlow, FullMass = 1f };
                case "liquid": return new Layer { Name = "liquid", Objects = ObjectLayer.LiquidConduit, Bridges = ObjectLayer.LiquidConduitConnection, Connections = c => g.liquidConduitSystem.GetConnections(c, true), Network = c => g.liquidConduitSystem.GetNetworkForCell(c), Flow = g.liquidConduitFlow, FullMass = 10f };
                case "power": return new Layer { Name = "power", Objects = ObjectLayer.Wire, Bridges = ObjectLayer.WireConnectors, Connections = c => g.electricalConduitSystem.GetConnections(c, true), Network = c => g.electricalConduitSystem.GetNetworkForCell(c) };
                case "rail": return new Layer { Name = "rail", Objects = ObjectLayer.SolidConduit, Bridges = ObjectLayer.SolidConduitConnection, Connections = c => g.solidConduitSystem.GetConnections(c, true), Network = c => g.solidConduitSystem.GetNetworkForCell(c) };
                default: return null;
            }
        }

        static Layer For(ConduitType t) => t == ConduitType.Gas ? Get("gas") : t == ConduitType.Liquid ? Get("liquid") : t == ConduitType.Solid ? Get("rail") : null;

        /// <summary>Box-drawing glyph for a segment's connections (up = towards higher y = the row above).</summary>
        static char Glyph(UtilityConnections c)
        {
            bool l = (c & UtilityConnections.Left) != 0, r = (c & UtilityConnections.Right) != 0,
                 u = (c & UtilityConnections.Up) != 0, d = (c & UtilityConnections.Down) != 0;
            int k = (l ? 1 : 0) | (r ? 2 : 0) | (u ? 4 : 0) | (d ? 8 : 0);
            return "■╴╶─╵┘└┴╷┐┌┬│┤├┼"[k];
        }

        /// <summary>
        /// A network layer as a text grid. gas/liquid: 3 chars per tile = segment glyph, element letter, fill digit
        /// (0-9 of a full packet: 1 kg gas, 10 kg liquid); power: glyph + circuit letter; rail: glyph.
        /// '=' marks a bridge, '·' no network, '~' unrevealed.
        /// </summary>
        public static string Map(int x0, int y0, int x1, int y1, string layerName, int maxTiles = 1600)
        {
            var layer = Get(layerName);
            if (layer == null) return "unknown layer; use gas, liquid, power or rail";
            if (x1 < x0) { int t = x0; x0 = x1; x1 = t; }
            if (y1 < y0) { int t = y0; y0 = y1; y1 = t; }
            x0 = Mathf.Clamp(x0, 0, Grid.WidthInCells - 1); x1 = Mathf.Clamp(x1, 0, Grid.WidthInCells - 1);
            y0 = Mathf.Clamp(y0, 0, Grid.HeightInCells - 1); y1 = Mathf.Clamp(y1, 0, Grid.HeightInCells - 1);
            if ((x1 - x0 + 1) * (y1 - y0 + 1) > maxTiles) return $"(region too large: max {maxTiles} tiles)";

            var legend = new Dictionary<string, char>();
            var circuits = new Dictionary<ushort, char>();
            int w = layer.Flow != null ? 3 : layerName == "power" ? 2 : 1;
            var sb = new StringBuilder("     ");
            for (int x = x0; x <= x1; x++) sb.Append(x % 10 == 0 ? (x % 1000).ToString().PadRight(w) : new string(' ', w));
            sb.AppendLine();
            int segments = 0;
            for (int y = y1; y >= y0; y--)
            {
                sb.Append(y.ToString().PadLeft(4)).Append(' ');
                for (int x = x0; x <= x1; x++)
                {
                    int cell = Grid.XYToCell(x, y);
                    if (!MapMemory.Revealed(cell)) { sb.Append(new string('~', w)); continue; }
                    bool pipe = Grid.Objects[cell, (int)layer.Objects] != null, bridge = Grid.Objects[cell, (int)layer.Bridges] != null;
                    if (!pipe && !bridge) { sb.Append('·').Append(' ', w - 1); continue; }
                    segments++;
                    sb.Append(pipe ? Glyph(layer.Connections(cell)) : '=');
                    if (layer.Flow != null)
                    {
                        var c = layer.Flow.GetContents(cell);
                        if (c.mass <= 0f || c.element == SimHashes.Vacuum) sb.Append("  ");
                        else
                        {
                            string n = ElementName(c.element);
                            if (!legend.TryGetValue(n, out char s)) { s = legend.Count < 26 ? (char)('a' + legend.Count) : '*'; legend[n] = s; }
                            sb.Append(s).Append((char)('0' + Mathf.Clamp(Mathf.RoundToInt(c.mass / layer.FullMass * 9f), c.mass > 0 ? 1 : 0, 9)));
                        }
                    }
                    else if (layerName == "power")
                    {
                        ushort id = Game.Instance.circuitManager.GetCircuitID(cell);
                        if (id == CircuitManager.INVALID_ID) sb.Append('?');
                        else
                        {
                            if (!circuits.TryGetValue(id, out char s)) { s = circuits.Count < 26 ? (char)('A' + circuits.Count) : '*'; circuits[id] = s; }
                            sb.Append(s);
                        }
                    }
                }
                sb.AppendLine();
            }
            if (segments == 0) return $"no {layerName} network in x{x0}-{x1} y{y0}-{y1}";
            if (legend.Count > 0) sb.Append("contents: ").Append(string.Join(", ", legend.Select(kv => $"{kv.Value}={kv.Key}")))
                                  .Append($"; digit = fill, 9 = full packet ({layer.FullMass} kg); blank = empty pipe. ");
            if (circuits.Count > 0) sb.Append("circuits: ").Append(string.Join(", ", circuits.Select(kv => $"{kv.Value}=circuit {kv.Key}"))).Append(". ");
            sb.Append("= bridge, · none, ~ unrevealed. Building ports: use the ports query.");
            return sb.ToString();
        }

        /// <summary>Every utility port of the buildings in a region, and whether a pipe/wire reaches it.</summary>
        public static string Ports(int x0, int y0, int x1, int y1)
        {
            var lines = new List<string>();
            foreach (var bc in Components.BuildingCompletes.Items)
            {
                if (bc == null || IsNetworkPiece(bc)) continue;
                var xy = Grid.CellToXY(Grid.PosToCell(bc));
                if (xy.x < Math.Min(x0, x1) || xy.x > Math.Max(x0, x1) || xy.y < Math.Min(y0, y1) || xy.y > Math.Max(y0, y1)) continue;
                if (!MapMemory.Revealed(Grid.PosToCell(bc))) continue;
                var ports = PortList(bc);
                if (ports.Count == 0) continue;
                lines.Add($"{MapText.Strip(bc.GetProperName())} @{xy.x},{xy.y}: " + string.Join("; ", ports));
                if (lines.Count >= 40) { lines.Add("…"); break; }
            }
            return lines.Count == 0 ? "no buildings with pipe or power ports there" : string.Join("\n", lines);
        }

        /// <summary>Pipes, wires, rails and ports are buildings too; leave them out of building lists.</summary>
        public static bool IsNetworkPiece(BuildingComplete bc)
        {
            var def = bc.GetComponent<Building>()?.Def;
            return def != null && (def.isUtility || def.IsFoundation);
        }

        static List<string> PortList(BuildingComplete bc)
        {
            var list = new List<string>();
            var b = bc.GetComponent<Building>();
            if (b == null || b.Def == null) return list;
            var seen = new HashSet<string>();
            void Conduit(string what, ConduitType type, int cell)
            {
                var layer = For(type);
                if (layer == null || !Grid.IsValidCell(cell) || !seen.Add(type + "@" + cell)) return;
                var p = Grid.CellToXY(cell);
                string s = $"{layer.Name} {what} @{p.x},{p.y} ";
                if (Grid.Objects[cell, (int)layer.Objects] == null && Grid.Objects[cell, (int)layer.Bridges] == null) s += "NOT CONNECTED (no pipe)";
                else
                {
                    var net = layer.Network(cell);
                    s += net != null ? $"net {net.id}" : "pipe";
                    if (layer.Flow != null)
                    {
                        var c = layer.Flow.GetContents(cell);
                        s += c.mass > 0f && c.element != SimHashes.Vacuum ? $", {Mass(c.mass)} {ElementName(c.element)}" : ", no packet at this instant";
                    }
                }
                list.Add(s);
            }
            if (b.Def.InputConduitType != ConduitType.None) Conduit("in", b.Def.InputConduitType, b.GetUtilityInputCell());
            if (b.Def.OutputConduitType != ConduitType.None) Conduit("out", b.Def.OutputConduitType, b.GetUtilityOutputCell());
            int origin = Grid.PosToCell(bc);
            foreach (var si in bc.GetComponents<ConduitSecondaryInput>())
                if (si?.portInfo != null) Conduit("in 2", si.portInfo.conduitType, Grid.OffsetCell(origin, b.GetRotatedOffset(si.portInfo.offset)));
            var filter = bc.GetComponent<ElementFilter>();
            if (filter != null && filter.portInfo != null) Conduit("filtered out", filter.portInfo.conduitType, filter.GetFilteredCell());
            foreach (var so in bc.GetComponents<ConduitSecondaryOutput>())
                if (so?.portInfo != null) Conduit("out 2", so.portInfo.conduitType, Grid.OffsetCell(origin, b.GetRotatedOffset(so.portInfo.offset)));

            var cm = Game.Instance.circuitManager;
            void Power(string what, int cell)
            {
                if (!Grid.IsValidCell(cell)) return;
                var p = Grid.CellToXY(cell);
                ushort id = cm.GetCircuitID(cell);
                list.Add($"power {what} @{p.x},{p.y} " + (id == CircuitManager.INVALID_ID ? "NOT CONNECTED (no wire)" : $"circuit {id}"));
            }
            if (b.Def.RequiresPowerInput) Power("in", b.GetPowerInputCell());
            if (b.Def.RequiresPowerOutput) Power("out", b.GetPowerOutputCell());
            return list;
        }

        /// <summary>What lies on the network layers of one tile, for the cell query.</summary>
        public static string AtCell(int cell)
        {
            var parts = new List<string>();
            foreach (var name in new[] { "gas", "liquid", "power", "rail" })
            {
                var layer = Get(name);
                bool pipe = Grid.Objects[cell, (int)layer.Objects] != null, bridge = Grid.Objects[cell, (int)layer.Bridges] != null;
                if (!pipe && !bridge) continue;
                string s = name == "power" ? (bridge && !pipe ? "power bridge" : "wire") : name == "rail" ? "conveyor rail" : $"{name} {(bridge && !pipe ? "bridge" : "pipe")}";
                if (layer.Flow != null)
                {
                    var c = layer.Flow.GetContents(cell);
                    s += c.mass > 0f && c.element != SimHashes.Vacuum ? $" ({Mass(c.mass)} {ElementName(c.element)})" : " (empty)";
                }
                if (name == "power") { ushort id = Game.Instance.circuitManager.GetCircuitID(cell); if (id != CircuitManager.INVALID_ID) s += $" (circuit {id})"; }
                parts.Add(s);
            }
            return string.Join(", ", parts);
        }

        // ---------------------------------------------------------------- rates

        /// <summary>What a building converts per second, with "running"/"idle" — e.g. "1000 g/s Water → 888 g/s Oxygen + 112 g/s Hydrogen".</summary>
        public static string Rates(GameObject go)
        {
            var parts = new List<string>();
            var pump = go.GetComponent<Pump>();
            if (pump != null) parts.Add(PumpRate(go, pump));
            else foreach (var (ins, outs) in Conversions(go))
                parts.Add(string.Join(" + ", ins.Select(i => $"{Rate(i.kg)} {i.name}")) + " → " + (outs.Count > 0 ? string.Join(" + ", outs.Select(o => $"{Rate(o.kg)} {o.name}")) : "nothing"));
            var gen = go.GetComponent<Generator>();
            if (gen != null) parts.Add($"makes {Mathf.RoundToInt(gen.WattageRating)} W" + (gen.IsProducingPower() ? "" : " (not now)"));
            var ec = go.GetComponent<EnergyConsumer>();
            if (ec != null && ec.WattsNeededWhenActive > 0f) parts.Add($"uses {Mathf.RoundToInt(ec.WattsNeededWhenActive)} W");
            if (parts.Count == 0) return "";
            var op = go.GetComponent<Operational>();
            return string.Join("; ", parts) + (op == null ? "" : op.IsActive ? "; running" : "; idle");
        }

        /// <summary>
        /// A pump moves whatever is at its intake into its pipe; it converts nothing. (Its consumer is set to
        /// "Vacuum" = any element of its state, which once read as "pumping nothing".) Reported from the pump's
        /// own view: whether it has anything to pump, what it last took, and what is at the intake now.
        /// </summary>
        static string PumpRate(GameObject go, Pump pump)
        {
            var consumer = go.GetComponent<ElementConsumer>();
            var t = HarmonyLib.Traverse.Create(pump);
            bool pumpable = t.Field("pumpable").GetValue<bool>();
            var last = t.Field("lastElementConsumed").GetValue<SimHashes>();
            string state = consumer != null && consumer.configuration == ElementConsumer.Configuration.AllGas ? "gas" : "liquid";
            string what = consumer != null && consumer.configuration == ElementConsumer.Configuration.Element ? ElementName(consumer.elementToConsume) : "any " + state;
            float rate = consumer != null ? consumer.consumptionRate : 0f;
            string intake = "";
            if (consumer != null)
            {
                int cell = Grid.PosToCell(go.transform.GetPosition() + consumer.sampleCellOffset);
                if (Grid.IsValidCell(cell)) intake = $"; at its intake now: {ElementName(Grid.Element[cell].id)} {Grid.Mass[cell]:0.#} kg";
            }
            return $"pumps up to {Rate(rate)} of {what} from its intake into its {state} pipe (no conversion)" +
                   (pumpable ? (last != SimHashes.Vacuum && last != 0 ? $"; pumping {ElementName(last)}" : "; has something to pump") : $"; NOTHING TO PUMP: no {state} at its intake") + intake;
        }

        static IEnumerable<(List<(string name, float kg)> ins, List<(string name, float kg)> outs)> Conversions(GameObject go)
        {
            foreach (var conv in go.GetComponents<ElementConverter>())
            {
                if (conv == null || conv.consumedElements == null) continue;
                yield return (conv.consumedElements.Select(c => (MapText.Strip(c.Name), c.MassConsumptionRate)).ToList(),
                              (conv.outputElements ?? new ElementConverter.OutputElement[0]).Select(o => (ElementName(o.elementHash), o.massGenerationRate)).ToList());
            }
            var eg = go.GetComponent<EnergyGenerator>();
            if (eg != null && eg.formula.inputs != null)
                yield return (eg.formula.inputs.Select(i => (TagName(i.tag), i.consumptionRate)).ToList(),
                              (eg.formula.outputs ?? new EnergyGenerator.OutputItem[0]).Select(o => (ElementName(o.element), o.creationRate)).ToList());
            foreach (var ec in go.GetComponents<ElementConsumer>())
                if (ec != null && ec.consumptionRate > 0f && go.GetComponent<ElementConverter>() == null)
                    yield return (new List<(string, float)> { (ElementName(ec.elementToConsume), ec.consumptionRate) }, new List<(string, float)>());
        }

        /// <summary>
        /// Colony totals in g/s: what running buildings produce and consume now, the most they could at full
        /// operation, and the duplicants' breathing. Answers "am I making more hydrogen than I burn?".
        /// </summary>
        public static JObject ColonyRates()
        {
            var now = new Dictionary<string, float[]>(); // name -> [produced now, consumed now, produced max, consumed max]
            void Add(string name, int slot, float kg)
            {
                if (!now.TryGetValue(name, out var v)) now[name] = v = new float[4];
                v[slot] += kg * 1000f;
            }
            foreach (var bc in Components.BuildingCompletes.Items)
            {
                if (bc == null) continue;
                var op = bc.GetComponent<Operational>();
                bool running = op != null && op.IsActive;
                foreach (var (ins, outs) in Conversions(bc.gameObject))
                {
                    foreach (var i in ins) { Add(i.name, 3, i.kg); if (running) Add(i.name, 1, i.kg); }
                    foreach (var o in outs) { Add(o.name, 2, o.kg); if (running) Add(o.name, 0, o.kg); }
                }
            }
            int dupes = Components.LiveMinionIdentities.Count;
            const float Breath = 0.1f, Exhale = 0.002f; // kg/s per duplicant (base game)
            Add("Oxygen", 1, dupes * Breath); Add("Oxygen", 3, dupes * Breath);
            Add("Carbon Dioxide", 0, dupes * Exhale); Add("Carbon Dioxide", 2, dupes * Exhale);

            var o2 = new JObject();
            foreach (var kv in now.OrderByDescending(kv => kv.Value[2] + kv.Value[3]))
            {
                var v = kv.Value;
                o2[kv.Key] = $"now +{Mathf.RoundToInt(v[0])}/-{Mathf.RoundToInt(v[1])} g/s (net {Signed(v[0] - v[1])}); at full operation +{Mathf.RoundToInt(v[2])}/-{Mathf.RoundToInt(v[3])}";
            }
            return new JObject { ["note"] = $"includes {dupes} duplicants breathing 100 g/s oxygen and exhaling 2 g/s CO2 each; buildings only while running", ["byElement"] = o2 };
        }

        static string Signed(float g) => (g >= 0 ? "+" : "") + Mathf.RoundToInt(g);
        static string Rate(float kgPerS) => kgPerS >= 1f ? $"{kgPerS:0.##} kg/s" : $"{Mathf.RoundToInt(kgPerS * 1000f)} g/s";
        static string Mass(float kg) => kg >= 1f ? $"{kg:0.#} kg" : $"{Mathf.RoundToInt(kg * 1000f)} g";

        static string ElementName(SimHashes h)
        {
            var e = ElementLoader.FindElementByHash(h);
            return e != null ? MapText.Name(e) : h.ToString();
        }

        static string TagName(Tag t)
        {
            var e = ElementLoader.GetElement(t);
            return e != null ? MapText.Name(e) : MapText.Strip(t.ProperName());
        }
    }
}
