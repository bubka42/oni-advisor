using System.IO.Compression;

/// <summary>
/// Writes a small synthetic map.bin in exactly the layout MapExport.WriteBinary produces in the mod,
/// so the reader can be exercised without the game. Keep the two in sync.
/// </summary>
static class SelfTest
{
    public static string Write()
    {
        string path = Path.Combine(Path.GetTempPath(), "oni-advisor-selftest-map.bin");
        int ox = 10, oy = 20, w = 20, h = 8;
        string[] els = { "Vacuum", "Oxygen", "Carbon Dioxide", "Water", "Sandstone", "Coal", "Hydrogen" };
        char[] states = { 'V', 'G', 'G', 'L', 'S', 'S', 'G' };
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionMode.Compress);
        using var wr = new BinaryWriter(gz);
        wr.Write("OAMB"); wr.Write(1);
        wr.Write(19); wr.Write(11400f); wr.Write("memory");
        wr.Write(ox); wr.Write(oy); wr.Write(w); wr.Write(h);
        wr.Write(els.Length);
        for (int i = 0; i < els.Length; i++) { wr.Write(els[i]); wr.Write(states[i]); }
        wr.Write(2); wr.Write("Food Poisoning"); wr.Write("Slimelung");
        wr.Write(2); wr.Write("Electrolyzer"); wr.Write("Gas Pump");
        for (int ty = 0; ty < h; ty++)
            for (int tx = 0; tx < w; tx++)
            {
                int x = ox + tx, y = oy + ty;
                bool revealed = tx < 18;
                ushort el = ty == 0 ? (ushort)4 : ty <= 2 && tx < 6 ? (ushort)3 : tx > 13 && ty > 3 ? (ushort)5 : ty >= 6 ? (ushort)6 : ty >= 4 ? (ushort)1 : (ushort)2;
                bool knowGas = tx < 8;
                byte flags = (byte)((revealed ? 1 : 0) | (revealed ? 2 : 0) | (revealed && knowGas ? 4 : 0) | (revealed && tx < 6 ? 8 : 0) | (revealed && tx < 4 ? 16 : 0));
                wr.Write(flags);
                wr.Write(el); wr.Write(revealed ? tx * 0.5f : -1f);
                wr.Write(el); wr.Write(states[el] == 'G' ? 0.6f + tx * 0.1f : 1000f); wr.Write(knowGas ? 1.5f : -1f);
                wr.Write(21.5f + ty); wr.Write(tx < 6 ? 0.2f : -1f);
                wr.Write(tx == 1 ? (byte)0 : (byte)255); wr.Write(tx == 1 ? 1200 : 0); wr.Write(tx < 4 ? 3f : -1f);
                wr.Write(x == 12 && y == 21 ? (short)0 : x == 13 && y == 21 ? (short)0 : x == 16 && y == 26 ? (short)1 : (short)-1);
            }
        wr.Write(1); wr.Write(12); wr.Write(22); wr.Write("Abe");
        return path;
    }
}
