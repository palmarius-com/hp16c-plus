using System.Text;
using System.Xml.Linq;

namespace Nut.Core;

/// <summary>
/// Voyager calculator (10C/11C/12C/15C/16C) around the Nut CPU, headless.
/// Port of nut/voyager.py. Chip map, keyboard and LCD segment map come from
/// Nonpareil's .ncd.tmpl files; the R2D2 display chip follows Nonpareil's
/// src/voyager_lcd.c.
/// </summary>
public class Voyager
{
    public const string Segments = "abcdefghij";

    static readonly Dictionary<int, char> SevenSeg = new()
    {
        [0x00] = ' ', [0x3F] = '0', [0x06] = '1', [0x5B] = '2', [0x4F] = '3', [0x66] = '4',
        [0x6D] = '5', [0x7D] = '6', [0x07] = '7', [0x27] = '7', [0x7F] = '8', [0x6F] = '9',
        [0x67] = '9', [0x77] = 'A', [0x7C] = 'b', [0x39] = 'C', [0x58] = 'c', [0x5E] = 'd',
        [0x79] = 'E', [0x71] = 'F', [0x50] = 'r', [0x5C] = 'o', [0x74] = 'h', [0x76] = 'H',
        [0x73] = 'P', [0x38] = 'L', [0x3E] = 'U', [0x1C] = 'u', [0x54] = 'n', [0x40] = '-',
        [0x6E] = 'y', [0x04] = 'i', [0x10] = 'i', [0x30] = 'I', [0x08] = '_', [0x3D] = 'G',
        [0x31] = 'r', [0x37] = 'N', [0x78] = 't',
    };

    public sealed record Chip(string Type, int Index, List<(string Space, int Base, int Size)> Mems);

    public sealed record Ncd(
        List<Chip> Chips,
        Dictionary<(int Digit, char Seg), (int Reg, int Bit)> Lcd,
        Dictionary<int, int> Keys,
        List<string> ObjFiles)
    {
        /// <summary>Key legends from the comments in the chip file: user keycode
        /// -> [unshifted, f-shifted, g-shifted] (as many as the model has).</summary>
        public Dictionary<int, string[]> Legends { get; init; } = [];
        public string Model { get; init; } = "";
    }

    public static Ncd ParseNcd(string path) => ParseNcdText(File.ReadAllText(path));

    /// <summary>Chip file text -> chips, LCD and key maps, and the key legends.
    /// Legends sit in comments aligned to fixed columns, named by a header
    /// comment ("unshifted  f-shifted  g-shifted"); a legend may have spaces.</summary>
    public static Ncd ParseNcdText(string text)
    {
        var ncd = ParseNcd(XDocument.Parse(text));
        var legends = new Dictionary<int, string[]>();
        int[] cols = [];
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            int u = line.IndexOf("unshifted", StringComparison.Ordinal);
            if (u >= 0 && line.TrimStart().StartsWith("<!--"))
            {
                var heads = new List<int>();
                for (int i = u; i < line.Length; i++)
                    if (line[i] != ' ' && (i == 0 || line[i - 1] == ' ') && line[i] != '-') heads.Add(i);
                cols = [.. heads];
                continue;
            }
            var m = System.Text.RegularExpressions.Regex.Match(line, @"user_keycode=""(\d+)""");
            int c0 = line.IndexOf("<!--", StringComparison.Ordinal);
            if (!m.Success || c0 < 0 || cols.Length == 0) continue;
            int end = line.IndexOf("-->", c0, StringComparison.Ordinal);
            var body = end < 0 ? line : line[..end];
            var l = new string[cols.Length];
            for (int k = 0; k < cols.Length; k++)
            {
                int a = cols[k], b = k + 1 < cols.Length ? cols[k + 1] : body.Length;
                l[k] = a < body.Length ? body[a..Math.Min(b, body.Length)].Trim() : "";
            }
            legends[int.Parse(m.Groups[1].Value)] = l;
        }
        return ncd with { Legends = legends };
    }

    /// <summary>Chip definition shipped with the library (Nonpareil, GPLv3):
    /// "16c" or "15c-192".</summary>
    public static Ncd EmbeddedNcd(string model)
    {
        using var s = typeof(Voyager).Assembly.GetManifestResourceStream($"Nonpareil.{model}.ncd.tmpl")
            ?? throw new ArgumentException($"no shipped chip definition '{model}'");
        return ParseNcdText(new StreamReader(s).ReadToEnd());
    }

    public static Ncd ParseNcd(XDocument doc)
    {
        var root = doc.Root!;
        var chips = root.Elements("chip").Select(chip => new Chip(
            (string?)chip.Attribute("type") ?? "",
            int.Parse((string?)chip.Attribute("index") ?? "0"),
            chip.Elements("memory").Select(m => (
                (string?)m.Attribute("addr_space") ?? "",
                Convert.ToInt32((string)m.Attribute("base_addr")!, 16),
                Convert.ToInt32((string)m.Attribute("size")!, 16))).ToList())).ToList();
        var lcd = new Dictionary<(int, char), (int, int)>();
        foreach (var digit in root.Descendants("digit"))
        {
            int d = int.Parse((string)digit.Attribute("digit")!);
            foreach (var seg in digit.Elements("segment"))
            {
                int reg = int.Parse((string)seg.Attribute("reg")!);
                int bit = int.Parse((string)seg.Attribute("bit")!);
                if (reg >= 0) lcd[(d, ((string)seg.Attribute("segment")!)[0])] = (reg, bit);
            }
        }
        var keys = new Dictionary<int, int>();
        var legends = new Dictionary<int, string[]>();
        foreach (var key in root.Descendants("key"))
        {
            int code = int.Parse((string)key.Attribute("user_keycode")!);
            keys[code] = Convert.ToInt32((string)key.Attribute("hw_keycode")!, 16);
        }
        var objs = root.Elements("obj_file").Select(o => o.Value.Trim()).ToList();
        return new Ncd(chips, lcd, keys, objs)
        {
            Legends = legends,
            Model = (string?)root.Attribute("model") ?? "",
        };
    }

    public NutCpu Cpu { get; }
    public Dictionary<(int Digit, char Seg), (int Reg, int Bit)> Lcd { get; }
    public Dictionary<int, int> Keys { get; }
    public Dictionary<int, string[]> Legends { get; }
    public string Model { get; }
    public bool DisplayEnable { get; private set; }
    public bool Blink { get; private set; }

    /// <param name="ncdPath">the calculator's .ncd.tmpl (keys, LCD map, ROM file names)</param>
    /// <param name="romDir">directory of the ROM text files named in the ncd</param>
    /// <param name="chipNcdPath">take the chip map (ROM/RAM) from another calculator,
    /// e.g. the 16C ROM on the 15C-192 chips for the 16C+</param>
    /// <param name="patches">ROM text files loaded on top</param>
    public Voyager(string ncdPath, string romDir, IEnumerable<string>? romFiles = null,
                   IEnumerable<string>? patches = null, string? chipNcdPath = null)
        : this(ParseNcd(ncdPath), chipNcdPath is null ? null : ParseNcd(chipNcdPath))
    {
        foreach (var name in romFiles ?? ParseNcd(ncdPath).ObjFiles)
            Cpu.LoadRomLines(File.ReadAllLines(Path.Combine(romDir, name)));
        foreach (var p in patches ?? [])
            Cpu.LoadRomLines(File.ReadAllLines(p));
    }

    /// <summary>Machine from parsed chip definitions and a ROM image in memory.</summary>
    public Voyager(Ncd ncd, Ncd? chipNcd, RomImage rom) : this(ncd, chipNcd)
    {
        Cpu.LoadRomLines(rom.ToLines());
    }

    Voyager(Ncd ncd, Ncd? chipNcd)
    {
        Lcd = ncd.Lcd;
        Keys = ncd.Keys;
        Legends = ncd.Legends;
        Model = ncd.Model;
        var chips = (chipNcd ?? ncd).Chips;
        Cpu = new NutCpu();
        foreach (var chip in chips)
        {
            foreach (var (space, b, size) in chip.Mems)
                if (space == "data") Cpu.CreateRam(b, size);
            if (chip.Type == "voyager_r2d2")
            {
                int io = chip.Index * 0x10 + 0x08;
                Cpu.CreateRam(io, 3);
                Cpu.RamReadFn[io] = _ => new byte[NutCpu.W];
                Cpu.RamWriteFn[io] = (_, _) => { };
                Cpu.RamReadFn[io + 1] = BitmapRead;
                Cpu.RamReadFn[io + 2] = BitmapRead;
            }
        }
        Cpu.ExtraOps[0x030] = (_, _) => { SetDisplay(true); Blink = true; };
        Cpu.ExtraOps[0x2E0] = (_, _) => { SetDisplay(false); Blink = false; };
        Cpu.ExtraOps[0x320] = (_, _) => SetDisplay(!DisplayEnable);
        Cpu.OnSleep.Add(c => { if (!DisplayEnable) c.Carry = 1; }); // deep sleep
    }

    byte[] BitmapRead(int addr)
    {
        var r = (byte[])Cpu.Ram[addr].Clone();
        r[1] &= 0xC;
        r[0] = 0;
        return r;
    }

    /// <summary>Display chip state from a saved machine state.</summary>
    public void RestoreDisplay(bool on, bool blink)
    {
        SetDisplay(on);
        Blink = blink;
    }

    void SetDisplay(bool on)
    {
        DisplayEnable = on;
        Cpu.DisplayEnable = on;
    }

    // ----------------------------------------------------------- display
    /// <summary>Per LCD digit 0..10: segment -> lit (only segments the LCD has).</summary>
    public Dictionary<char, bool>[] SegmentState()
    {
        var ram = Cpu.Ram;
        var outp = new Dictionary<char, bool>[11];
        for (int d = 0; d < 11; d++)
        {
            var segs = new Dictionary<char, bool>();
            foreach (var s in Segments)
                if (Lcd.TryGetValue((d, s), out var rb))
                    segs[s] = (ram[rb.Reg][rb.Bit / 4] & (1 << (rb.Bit & 3))) != 0;
            outp[d] = segs;
        }
        return outp;
    }

    static bool Lit(Dictionary<char, bool> s, char k) => s.TryGetValue(k, out var v) && v;

    /// <summary>Display as text: sign, digits with '.' and ',' marks; "" when off.</summary>
    public string Display()
    {
        if (!DisplayEnable) return "";
        var segs = SegmentState();
        var sb = new StringBuilder(Lit(segs[0], 'g') ? "-" : " ");
        for (int d = 1; d < 11; d++)
        {
            var s = segs[d];
            int pat = 0;
            for (int i = 0; i < 7; i++) if (Lit(s, "abcdefg"[i])) pat |= 1 << i;
            sb.Append(SevenSeg.GetValueOrDefault(pat, '?'));
            if (Lit(s, 'h') && Lit(s, 'i')) sb.Append(',');
            else if (Lit(s, 'h')) sb.Append('.');
        }
        return sb.ToString();
    }

    /// <summary>LCD digit positions whose annunciator (segment j) is lit.</summary>
    public List<int> Annunciators()
    {
        var segs = SegmentState();
        return Enumerable.Range(0, 11).Where(d => Lit(segs[d], 'j')).ToList();
    }

    // -------------------------------------------------------------- keys
    /// <summary>Reset (as after inserting batteries) and run to sleep.</summary>
    public long PowerOn(long maxCycles = 2_000_000)
    {
        Cpu.Reset();
        SetDisplay(false);
        return Cpu.Run(maxCycles);
    }

    /// <summary>Press a key (HP row/column code, 41 = ON), run, release, run.</summary>
    public bool Key(int userKeycode, long holdCycles = 200_000, long maxCycles = 5_000_000)
    {
        if (!Cpu.Press(Keys[userKeycode])) return false;
        Cpu.Run(holdCycles);
        Cpu.Release();
        Cpu.Run(maxCycles);
        return true;
    }
}
