using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace OniAdvisor
{
    /// <summary>
    /// What the advisor is allowed to know about each tile, per layer, with the game time it was
    /// last observed (-1 = never).
    ///
    /// Memory mode: a layer is updated only for tiles on screen, and only while the matching view
    /// is open - terrain (element) in any view, gas mass in the Oxygen overlay, temperature in the
    /// Temperature overlay, germs in the Germ overlay. Persisted per colony between sessions.
    ///
    /// Live mode: every layer reads the current game state of revealed tiles.
    ///
    /// Unrevealed tiles are never recorded or read in either mode.
    /// </summary>
    internal sealed class MapMemory
    {
        public static MapMemory Current { get; private set; }

        const int FileVersion = 1;
        const float TickSeconds = 0.5f;

        public readonly int W, H, N;
        public readonly bool Live;
        readonly string colony;

        public readonly ushort[] Elem;
        public readonly float[] ElemSeen;
        public readonly ushort[] GasElem;
        public readonly float[] GasMass;
        public readonly float[] GasSeen;
        public readonly float[] Temp;
        public readonly float[] TempSeen;
        public readonly byte[] GermIdx;
        public readonly int[] GermCount;
        public readonly float[] GermSeen;

        float nextTick;

        MapMemory(int w, int h, bool live, string colony)
        {
            W = w; H = h; N = w * h; Live = live; this.colony = colony;
            Elem = new ushort[N]; ElemSeen = Filled(N);
            GasElem = new ushort[N]; GasMass = new float[N]; GasSeen = Filled(N);
            Temp = new float[N]; TempSeen = Filled(N);
            GermIdx = Enumerable.Repeat(byte.MaxValue, N).ToArray(); GermCount = new int[N]; GermSeen = Filled(N);
        }

        static float[] Filled(int n) => Enumerable.Repeat(-1f, n).ToArray();

        public static void Init()
        {
            Config.Load();
            string name = SaveGame.Instance != null ? SaveGame.Instance.BaseName : "colony";
            Current = new MapMemory(Grid.WidthInCells, Grid.HeightInCells, !Config.MemoryMode, name);
            if (!Current.Live) Current.LoadFromDisk();
        }

        public static void Shutdown()
        {
            if (Current != null && !Current.Live) Current.SaveToDisk();
            Current = null;
        }

        static float Now => GameClock.Instance != null ? GameClock.Instance.GetTime() : 0f;

        // ------------------------------------------------------------------ observation

        public void Tick(float unscaledTime)
        {
            if (Live || unscaledTime < nextTick) return;
            nextTick = unscaledTime + TickSeconds;

            var area = GridVisibleArea.GetVisibleArea();
            int x0 = Math.Max(0, area.Min.x), y0 = Math.Max(0, area.Min.y);
            int x1 = Math.Min(W - 1, area.Max.x), y1 = Math.Min(H - 1, area.Max.y);
            if (x1 < x0 || y1 < y0) return;

            var mode = OverlayScreen.Instance != null ? OverlayScreen.Instance.GetMode() : OverlayModes.None.ID;
            bool oxygen = mode == OverlayModes.Oxygen.ID;
            bool temperature = mode == OverlayModes.Temperature.ID;
            bool germs = mode == OverlayModes.Disease.ID;
            float t = Now;

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    int cell = Grid.XYToCell(x, y);
                    if (!Grid.IsValidCell(cell) || !Grid.IsVisible(cell)) continue;
                    ushort e = Grid.ElementIdx[cell];
                    Elem[cell] = e; ElemSeen[cell] = t;
                    if (oxygen) { GasElem[cell] = e; GasMass[cell] = Grid.Mass[cell]; GasSeen[cell] = t; }
                    if (temperature) { Temp[cell] = Grid.Temperature[cell]; TempSeen[cell] = t; }
                    if (germs) { GermIdx[cell] = Grid.DiseaseIdx[cell]; GermCount[cell] = Grid.DiseaseCount[cell]; GermSeen[cell] = t; }
                }
            }
        }

        // ------------------------------------------------------------------ reads (mode-aware)

        public static bool Revealed(int cell) => Grid.IsValidCell(cell) && Grid.IsVisible(cell);

        /// <summary>Terrain element; false if the advisor may not know it.</summary>
        public bool TryElement(int cell, out Element el, out float seen)
        {
            el = null; seen = -1f;
            if (!Revealed(cell)) return false;
            if (Live) { el = Grid.Element[cell]; seen = Now; return true; }
            if (ElemSeen[cell] < 0f) return false;
            el = ElementLoader.elements[Elem[cell]]; seen = ElemSeen[cell];
            return true;
        }

        public bool TryGas(int cell, out Element el, out float mass, out float seen)
        {
            el = null; mass = 0f; seen = -1f;
            if (!Revealed(cell)) return false;
            if (Live) { el = Grid.Element[cell]; mass = Grid.Mass[cell]; seen = Now; return true; }
            if (GasSeen[cell] < 0f) return false;
            el = ElementLoader.elements[GasElem[cell]]; mass = GasMass[cell]; seen = GasSeen[cell];
            return true;
        }

        public bool TryTemp(int cell, out float kelvin, out float seen)
        {
            kelvin = 0f; seen = -1f;
            if (!Revealed(cell)) return false;
            if (Live) { kelvin = Grid.Temperature[cell]; seen = Now; return true; }
            if (TempSeen[cell] < 0f) return false;
            kelvin = Temp[cell]; seen = TempSeen[cell];
            return true;
        }

        public bool TryGerms(int cell, out byte idx, out int count, out float seen)
        {
            idx = byte.MaxValue; count = 0; seen = -1f;
            if (!Revealed(cell)) return false;
            if (Live) { idx = Grid.DiseaseIdx[cell]; count = Grid.DiseaseCount[cell]; seen = Now; return true; }
            if (GermSeen[cell] < 0f) return false;
            idx = GermIdx[cell]; count = GermCount[cell]; seen = GermSeen[cell];
            return true;
        }

        public static float AgeCycles(float seen) => seen < 0f ? -1f : Math.Max(0f, (Now - seen) / 600f);

        // ------------------------------------------------------------------ persistence

        string FilePath()
        {
            var safe = new string(colony.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
            return Path.Combine(Paths.Sub(Path.Combine("..", "memory")), safe + ".bin");
        }

        public void SaveToDisk()
        {
            try
            {
                var path = FilePath();
                var tmp = path + ".tmp";
                using (var fs = File.Create(tmp))
                using (var gz = new GZipStream(fs, CompressionMode.Compress))
                using (var w = new BinaryWriter(gz))
                {
                    w.Write("OAMM"); w.Write(FileVersion); w.Write(W); w.Write(H); w.Write(Now);
                    for (int i = 0; i < N; i++)
                    {
                        w.Write(Elem[i]); w.Write(ElemSeen[i]);
                        w.Write(GasElem[i]); w.Write(GasMass[i]); w.Write(GasSeen[i]);
                        w.Write(Temp[i]); w.Write(TempSeen[i]);
                        w.Write(GermIdx[i]); w.Write(GermCount[i]); w.Write(GermSeen[i]);
                    }
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e) { Log.Warn("memory save failed: " + e.Message); }
        }

        void LoadFromDisk()
        {
            var path = FilePath();
            if (!File.Exists(path)) return;
            try
            {
                using (var fs = File.OpenRead(path))
                using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                using (var r = new BinaryReader(gz))
                {
                    if (r.ReadString() != "OAMM" || r.ReadInt32() != FileVersion) return;
                    if (r.ReadInt32() != W || r.ReadInt32() != H) { Log.Warn("memory file is for a different map size; ignored"); return; }
                    r.ReadSingle();
                    // An older save may have been loaded: forget anything "seen" after the current game time.
                    float now = Now;
                    for (int i = 0; i < N; i++)
                    {
                        Elem[i] = r.ReadUInt16(); ElemSeen[i] = Keep(r.ReadSingle(), now);
                        GasElem[i] = r.ReadUInt16(); GasMass[i] = r.ReadSingle(); GasSeen[i] = Keep(r.ReadSingle(), now);
                        Temp[i] = r.ReadSingle(); TempSeen[i] = Keep(r.ReadSingle(), now);
                        GermIdx[i] = r.ReadByte(); GermCount[i] = r.ReadInt32(); GermSeen[i] = Keep(r.ReadSingle(), now);
                    }
                }
                Log.Info("map memory loaded for " + colony);
            }
            catch (Exception e) { Log.Warn("memory load failed: " + e.Message); }
        }

        static float Keep(float seen, float now) => seen > now + 1f ? -1f : seen;
    }
}
