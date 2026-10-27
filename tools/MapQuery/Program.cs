// Reads advisor/live/map/map.bin written by the OniAdvisor mod and answers small questions about it,
// so the advisor never has to load the whole map.
//
//   MapQuery [--file path] summary
//   MapQuery [--file path] region <x0> <y0> <x1> <y1> [elem|gas|temp|germs|age|build]
//   MapQuery [--file path] cell <x> <y>
//   MapQuery [--file path] find <element name substring>
//
// Coordinates are game tile coordinates (y grows upward). Rows print top (high y) to bottom.
using System.IO.Compression;
using System.Text;

string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    @"OneDrive\Documents\Klei\OxygenNotIncluded\advisor\live\map\map.bin");
var a = args.ToList();
int fi = a.IndexOf("--file");
if (fi >= 0) { file = a[fi + 1]; a.RemoveRange(fi, 2); }
if (a.Count == 0) { Console.WriteLine("commands: summary | region x0 y0 x1 y1 [elem|gas|temp|germs|age|build] | cell x y | find <element> | selftest"); return 1; }
if (a[0] == "selftest") { file = SelfTest.Write(); Console.WriteLine("synthetic map written to " + file); }

// "live" asks the running game (fresh data, memory mode respected) instead of reading map.bin:
//   live region x0 y0 x1 y1 [terrain|gas|temp] | live cell x y | live buildings x0 y0 x1 y1 | live find <what> [x y] | live view
if (a[0] == "live")
{
    var liveDir = Path.GetDirectoryName(Path.GetDirectoryName(file))!; // .../advisor/live/map/map.bin -> .../advisor/live
    var qdir = Path.Combine(liveDir, "queries");
    Directory.CreateDirectory(qdir);
    var q = new System.Text.Json.Nodes.JsonObject { ["kind"] = a.Count > 1 ? a[1] : "view" };
    int N(int i) => a.Count > i && int.TryParse(a[i], out var v) ? v : 0;
    switch (q["kind"]!.ToString())
    {
        case "region": q["x0"] = N(2); q["y0"] = N(3); q["x1"] = N(4); q["y1"] = N(5); q["layer"] = a.Count > 6 ? a[6] : "terrain"; break;
        case "cell": q["x"] = N(2); q["y"] = N(3); break;
        case "buildings": q["x0"] = N(2); q["y0"] = N(3); q["x1"] = N(4); q["y1"] = N(5); break;
        case "find": q["what"] = a.Count > 2 ? a[2] : ""; if (a.Count > 4) { q["x"] = N(3); q["y"] = N(4); } break;
    }
    var id = Guid.NewGuid().ToString("N");
    var req = Path.Combine(qdir, $"req-{id}.json");
    File.WriteAllText(req + ".tmp", q.ToJsonString());
    File.Move(req + ".tmp", req);
    var res = Path.Combine(qdir, $"res-{id}.txt");
    for (int i = 0; i < 50; i++) // up to 5 s
    {
        if (File.Exists(res))
        {
            Thread.Sleep(30);
            Console.WriteLine(File.ReadAllText(res));
            try { File.Delete(res); } catch { }
            return 0;
        }
        Thread.Sleep(100);
    }
    try { File.Delete(req); } catch { }
    Console.WriteLine("no answer from the game (is ONI running with the OniAdvisor mod and a colony loaded?)");
    return 2;
}

var map = MapFile.Load(file);
switch (a[0])
{
    case "summary": map.Summary(); break;
    case "region": map.Region(int.Parse(a[1]), int.Parse(a[2]), int.Parse(a[3]), int.Parse(a[4]), a.Count > 5 ? a[5] : "elem"); break;
    case "cell": map.Cell(int.Parse(a[1]), int.Parse(a[2])); break;
    case "find": map.Find(string.Join(" ", a.Skip(1))); break;
    case "selftest":
        map.Summary();
        map.Region(10, 20, 29, 27, "elem");
        map.Region(10, 20, 17, 27, "gas");
        map.Region(10, 20, 17, 23, "temp");
        map.Region(10, 20, 29, 27, "build");
        map.Cell(12, 21);
        map.Cell(25, 25);
        map.Find("coal");
        break;
    default: Console.WriteLine("unknown command"); return 1;
}
return 0;

struct Tile
{
    public byte Flags; public ushort Elem; public float ElemAge;
    public ushort GasElem; public float GasMass; public float GasAge;
    public float TempC; public float TempAge;
    public byte GermIdx; public int GermCount; public float GermAge;
    public short Building;
    public bool Revealed => (Flags & 1) != 0;
    public bool KnowElem => (Flags & 2) != 0;
    public bool KnowGas => (Flags & 4) != 0;
    public bool KnowTemp => (Flags & 8) != 0;
    public bool KnowGerms => (Flags & 16) != 0;
}

sealed class MapFile
{
    public int Cycle, Ox, Oy, W, H; public float Time; public string Mode = "";
    public List<(string name, char state)> Elements = new();
    public List<string> Diseases = new(), Buildings = new();
    public Tile[] Tiles = Array.Empty<Tile>();
    public List<(int x, int y, string name)> Dupes = new();

    public static MapFile Load(string path)
    {
        var m = new MapFile();
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var r = new BinaryReader(gz);
        if (r.ReadString() != "OAMB") throw new InvalidDataException("not a map.bin");
        int ver = r.ReadInt32(); if (ver != 1) throw new InvalidDataException("unsupported version " + ver);
        m.Cycle = r.ReadInt32(); m.Time = r.ReadSingle(); m.Mode = r.ReadString();
        m.Ox = r.ReadInt32(); m.Oy = r.ReadInt32(); m.W = r.ReadInt32(); m.H = r.ReadInt32();
        int ne = r.ReadInt32(); for (int i = 0; i < ne; i++) m.Elements.Add((r.ReadString(), r.ReadChar()));
        int nd = r.ReadInt32(); for (int i = 0; i < nd; i++) m.Diseases.Add(r.ReadString());
        int nb = r.ReadInt32(); for (int i = 0; i < nb; i++) m.Buildings.Add(r.ReadString());
        m.Tiles = new Tile[m.W * m.H];
        for (int i = 0; i < m.Tiles.Length; i++)
        {
            m.Tiles[i] = new Tile
            {
                Flags = r.ReadByte(), Elem = r.ReadUInt16(), ElemAge = r.ReadSingle(),
                GasElem = r.ReadUInt16(), GasMass = r.ReadSingle(), GasAge = r.ReadSingle(),
                TempC = r.ReadSingle(), TempAge = r.ReadSingle(),
                GermIdx = r.ReadByte(), GermCount = r.ReadInt32(), GermAge = r.ReadSingle(),
                Building = r.ReadInt16(),
            };
        }
        int nm = r.ReadInt32(); for (int i = 0; i < nm; i++) m.Dupes.Add((r.ReadInt32(), r.ReadInt32(), r.ReadString()));
        return m;
    }

    bool InMap(int x, int y) => x >= Ox && y >= Oy && x < Ox + W && y < Oy + H;
    ref Tile At(int x, int y) => ref Tiles[(y - Oy) * W + (x - Ox)];
    string ElemName(ushort i) => i < Elements.Count ? Elements[i].name : "?";
    char ElemState(ushort i) => i < Elements.Count ? Elements[i].state : '?';

    public void Summary()
    {
        Console.WriteLine($"cycle {Cycle}, mode {Mode}, revealed bounds x{Ox}-{Ox + W - 1} y{Oy}-{Oy + H - 1}");
        int rev = Tiles.Count(t => t.Revealed);
        Console.WriteLine($"revealed tiles {rev}; known: elem {Tiles.Count(t => t.KnowElem)}, gas {Tiles.Count(t => t.KnowGas)}, temp {Tiles.Count(t => t.KnowTemp)}, germs {Tiles.Count(t => t.KnowGerms)}");
        var byEl = Tiles.Where(t => t.KnowElem).GroupBy(t => t.Elem).Select(g => (ElemName(g.Key), ElemState(g.Key), g.Count()))
            .OrderByDescending(x => x.Item3).Take(25);
        Console.WriteLine("most common known elements (tiles):");
        foreach (var (n, s, c) in byEl) Console.WriteLine($"  {n} [{s}] {c}");
        Console.WriteLine("duplicants: " + string.Join(", ", Dupes.Select(d => $"{d.name} ({d.x},{d.y})")));
    }

    public void Cell(int x, int y)
    {
        if (!InMap(x, y)) { Console.WriteLine("outside revealed bounds"); return; }
        var t = At(x, y);
        if (!t.Revealed) { Console.WriteLine("unrevealed"); return; }
        Console.WriteLine($"({x},{y})");
        Console.WriteLine(t.KnowElem ? $"  element: {ElemName(t.Elem)} [{ElemState(t.Elem)}], seen {t.ElemAge:0.0} cycles ago" : "  element: never seen");
        Console.WriteLine(t.KnowGas ? $"  oxygen view: {ElemName(t.GasElem)} {t.GasMass * 1000:0} g, seen {t.GasAge:0.0} cycles ago" : "  oxygen view: never seen");
        Console.WriteLine(t.KnowTemp ? $"  temperature: {t.TempC:0.0} C, seen {t.TempAge:0.0} cycles ago" : "  temperature: never seen");
        if (t.KnowGerms)
            Console.WriteLine(t.GermIdx == 255 || t.GermCount <= 0 ? $"  germs: none, seen {t.GermAge:0.0} cycles ago"
                : $"  germs: {t.GermCount} {Dis(t.GermIdx)}, seen {t.GermAge:0.0} cycles ago");
        else Console.WriteLine("  germs: never seen");
        if (t.Building >= 0) Console.WriteLine($"  building: {Buildings[t.Building]}");
        foreach (var d in Dupes.Where(d => d.x == x && d.y == y)) Console.WriteLine($"  duplicant: {d.name}");
    }

    string Dis(byte i) => i < Diseases.Count ? Diseases[i] : "germs";

    public void Region(int x0, int y0, int x1, int y1, string layer)
    {
        if (x1 < x0) (x0, x1) = (x1, x0);
        if (y1 < y0) (y0, y1) = (y1, y0);
        if ((x1 - x0 + 1) * (y1 - y0 + 1) > 6000) { Console.WriteLine("region too large (max 6000 tiles); narrow it"); return; }

        var legend = new Dictionary<string, char>();
        const string symbols = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789@$%&*=+";
        char Sym(string key)
        {
            if (!legend.TryGetValue(key, out var c)) { c = legend.Count < symbols.Length ? symbols[legend.Count] : '?'; legend[key] = c; }
            return c;
        }

        bool wide = layer is "gas" or "temp" or "age";
        var sb = new StringBuilder();
        sb.Append(wide ? "      " : "     ");
        for (int x = x0; x <= x1; x++) sb.Append(wide ? (x % 10 == 0 ? $"{x % 1000,4}" : "    ") : (x % 10 == 0 ? '|' : ' '));
        sb.AppendLine();
        for (int y = y1; y >= y0; y--)
        {
            sb.Append($"{y,4} ");
            for (int x = x0; x <= x1; x++)
            {
                string cellText;
                if (!InMap(x, y) || !At(x, y).Revealed) cellText = wide ? "   ~" : "~";
                else
                {
                    var t = At(x, y);
                    cellText = layer switch
                    {
                        "gas" => !t.KnowGas ? "   ." : ElemState(t.GasElem) == 'G' ? $"{Sym(ElemName(t.GasElem))}{Math.Min(999, t.GasMass * 1000 / 10),3:0}"
                                 : ElemState(t.GasElem) == 'V' ? "   _" : $"   {Sym(ElemName(t.GasElem))}",
                        "temp" => !t.KnowTemp ? "   ." : $"{Math.Clamp(t.TempC, -99, 999),4:0}",
                        "age" => !t.KnowElem ? "   ." : $"{Math.Min(999, t.ElemAge),4:0}",
                        "germs" => !t.KnowGerms ? "." : t.GermIdx == 255 || t.GermCount <= 0 ? "-" : Sym(Dis(t.GermIdx)).ToString(),
                        "build" => t.Building >= 0 ? Sym(Buildings[t.Building]).ToString() : !t.KnowElem ? "." : ElemState(t.Elem) == 'S' ? "#" : " ",
                        _ => !t.KnowElem ? "." : Dupes.Any(d => d.x == x && d.y == y) ? "!" : Sym(ElemName(t.Elem)).ToString(),
                    };
                }
                sb.Append(cellText);
            }
            sb.AppendLine();
        }
        Console.Write(sb.ToString());
        Console.WriteLine(layer switch
        {
            "gas" => "gas: symbol + mass in units of 10 g (e.g. a180 = 1800 g); '_' vacuum; letter alone = liquid/solid",
            "temp" => "temperature in C",
            "age" => "cycles since last seen",
            "germs" => "'-' no germs",
            "build" => "'#' solid tile, ' ' open",
            _ => "'!' duplicant",
        } + "; '~' unrevealed; '.' never seen in this view");
        foreach (var kv in legend) Console.WriteLine($"  {kv.Value} = {kv.Key}");
    }

    public void Find(string query)
    {
        var hits = new bool[Tiles.Length];
        for (int i = 0; i < Tiles.Length; i++)
            hits[i] = Tiles[i].KnowElem && ElemName(Tiles[i].Elem).Contains(query, StringComparison.OrdinalIgnoreCase);
        var seen = new bool[Tiles.Length];
        var groups = new List<(int count, int x0, int y0, int x1, int y1, string name)>();
        for (int i = 0; i < Tiles.Length; i++)
        {
            if (!hits[i] || seen[i]) continue;
            var q = new Queue<int>(); q.Enqueue(i); seen[i] = true;
            int c = 0, gx0 = int.MaxValue, gy0 = int.MaxValue, gx1 = int.MinValue, gy1 = int.MinValue;
            while (q.Count > 0)
            {
                int k = q.Dequeue(); c++;
                int x = k % W, y = k / W;
                gx0 = Math.Min(gx0, x); gx1 = Math.Max(gx1, x); gy0 = Math.Min(gy0, y); gy1 = Math.Max(gy1, y);
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                    int n = ny * W + nx;
                    if (hits[n] && !seen[n]) { seen[n] = true; q.Enqueue(n); }
                }
            }
            groups.Add((c, gx0 + Ox, gy0 + Oy, gx1 + Ox, gy1 + Oy, ElemName(Tiles[i].Elem)));
        }
        if (groups.Count == 0) { Console.WriteLine($"no known tiles matching '{query}'"); return; }
        Console.WriteLine($"{groups.Sum(g => g.count)} known tiles matching '{query}' in {groups.Count} deposits (largest first):");
        foreach (var g in groups.OrderByDescending(g => g.count).Take(30))
            Console.WriteLine($"  {g.name}: {g.count} tiles, x{g.x0}-{g.x1} y{g.y0}-{g.y1}");
    }
}
