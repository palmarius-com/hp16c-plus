namespace Nut.Core;

/// <summary>HP-16C keystroke layer on top of the Voyager emulator. Port of nut/hp16c.py.</summary>
public class Hp16c : Voyager
{
    /// <summary>user keycode -> (unshifted, f-shifted, g-shifted)</summary>
    public static readonly Dictionary<int, (string U, string? F, string? G)> KeyNames = new()
    {
        [11] = ("A", "SL", "LJ"), [12] = ("B", "SR", "ASR"), [13] = ("C", "RL", "RLC"),
        [14] = ("D", "RR", "RRC"), [15] = ("E", "RLN", "RLCN"), [16] = ("F", "RRN", "RRCN"),
        [17] = ("7", "MASKL", "#B"), [18] = ("8", "MASKR", "ABS"),
        [19] = ("9", "RMD", "DBLR"), [10] = ("/", "XOR", "DBL/"),
        [21] = ("GSB", "X<>(I)", "RTN"), [22] = ("GTO", "X<>I", "LBL"),
        [23] = ("HEX", "SHOWHEX", "DSZ"), [24] = ("DEC", "SHOWDEC", "ISZ"),
        [25] = ("OCT", "SHOWOCT", "SQRT"), [26] = ("BIN", "SHOWBIN", "1/X"),
        [27] = ("4", "SB", "SF"), [28] = ("5", "CB", "CF"), [29] = ("6", "B?", "F?"),
        [20] = ("*", "AND", "DBLX"),
        [31] = ("R/S", "(I)", "P/R"), [32] = ("SST", "I", "BST"),
        [33] = ("RDN", "CLRPRGM", "RUP"), [34] = ("X<>Y", "CLRREG", "PSE"),
        [35] = ("BSP", "CLRPREFIX", "CLX"), [36] = ("ENTER", "WINDOW", "LSTX"),
        [37] = ("1", "1S", "X<=Y"), [38] = ("2", "2S", "X<0"), [39] = ("3", "UNSGN", "X>Y"),
        [30] = ("-", "NOT", "X>0"),
        [41] = ("ON", null, null), [42] = ("f", null, null), [43] = ("g", null, null),
        [44] = ("STO", "WSIZE", "<"), [45] = ("RCL", "FLOAT", ">"),
        [47] = ("0", "MEM", "X#Y"), [48] = (".", "STATUS", "X#0"),
        [49] = ("CHS", "EEX", "X=Y"), [40] = ("+", "OR", "X=0"),
    };

    public static readonly Dictionary<string, int[]> Names = BuildNames();

    static Dictionary<string, int[]> BuildNames()
    {
        var n = new Dictionary<string, int[]>();
        foreach (var (code, (u, f, g)) in KeyNames)
        {
            n[u] = [code];
            if (f is not null) n[f] = [42, code];
            if (g is not null) n[g] = [43, code];
        }
        n["f"] = [42];
        n["g"] = [43];
        return n;
    }

    const string HexDigits = "0123456789ABCDEF";

    /// <summary>Script tokens (key names, shifted names, digit strings) -> keycodes.</summary>
    public static List<int> TokensToKeys(string script)
    {
        var codes = new List<int>();
        foreach (var tok in script.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var up = tok.ToUpperInvariant();
            if (Names.TryGetValue(tok, out var k)) codes.AddRange(k);
            else if (Names.TryGetValue(up, out k) && up is not ("F" or "G")) codes.AddRange(k);
            else if (up.All(HexDigits.Contains)) codes.AddRange(up.Select(ch => Names[ch.ToString()][0]));
            else throw new ArgumentException($"unknown key token '{tok}'");
        }
        return codes;
    }

    public Hp16c(string ncdPath, string romDir, IEnumerable<string>? patches = null,
                 string? chipNcdPath = null)
        : base(ncdPath, romDir, null, patches, chipNcdPath) { }

    public Hp16c(Ncd ncd, Ncd? chipNcd, RomImage rom) : base(ncd, chipNcd, rom) { }

    public Hp16c(RomImage rom, bool plusChipMap)
        : base(EmbeddedNcd("16c"), plusChipMap ? EmbeddedNcd("15c-192") : null, rom) { }

    /// <summary>16C+ from the user's original 16c.rom and our patch file
    /// (checked: stock ROM, layout, chip sums).</summary>
    public static Hp16c PlusFrom(RomImage stock16c, RomImage patch) =>
        new(RomImage.Build16cPlus(stock16c, patch), plusChipMap: true);

    /// <summary>Stock 16C from a Nonpareil directory (16c.ncd.tmpl + 16c.rom).</summary>
    public static Hp16c Stock(string nonpareilDir) =>
        new(Path.Combine(nonpareilDir, "16c.ncd.tmpl"), nonpareilDir);

    /// <summary>16C+: the 16C ROM on the 15C-192 chip map with the patch files on top.</summary>
    public static Hp16c Plus(string nonpareilDir, IEnumerable<string> patches) =>
        new(Path.Combine(nonpareilDir, "16c.ncd.tmpl"), nonpareilDir, patches,
            Path.Combine(nonpareilDir, "15c-192.ncd.tmpl"));

    /// <summary>Fresh machine: power on (Pr Error), clear it with a key.</summary>
    public Hp16c Start()
    {
        PowerOn();
        Key(41); // ON
        Key(35); // BSP clears the "Pr Error" message
        return this;
    }

    public string Run(string script, long hold = 100)
    {
        foreach (var code in TokensToKeys(script)) Key(code, hold);
        return Display();
    }

    public (bool Carry, bool OutOfRange) FlagsCG()
    {
        var ann = Annunciators();
        return (ann.Contains(9), ann.Contains(6));
    }

    public string? Error()
    {
        var d = Display();
        return d.Contains("Error") ? d.Trim() : null;
    }

    // ------------------------------------------------ integer state
    static readonly Dictionary<string, int> Hi = new()
        { ["x"] = 0, ["y"] = 3, ["z"] = 5, ["t"] = 7, ["lastx"] = 9 }; // RAM 0x07 digit
    static readonly Dictionary<string, int> Lo = new()
        { ["y"] = 0x00, ["z"] = 0x01, ["t"] = 0x02, ["lastx"] = 0x03 };

    static UInt128 Val(byte[] digits, int count)
    {
        UInt128 v = 0;
        for (int i = count - 1; i >= 0; i--) v = (v << 4) | digits[i];
        return v;
    }

    /// <summary>Stack register as a raw 64-bit pattern (x, y, z, t, lastx).</summary>
    public ulong Reg64(string name)
    {
        // X: N is the canonical copy; M is a working copy that can be stale.
        var lo = name == "x" ? Cpu.N : Cpu.Ram[Lo[name]];
        int h = Hi[name];
        var r7 = Cpu.Ram[0x07];
        ulong hi = ((ulong)r7[h + 1] << 4) | r7[h];
        return (hi << 56) | (ulong)Val(lo, NutCpu.W);
    }

    /// <summary>I, 68 bits: RAM 0xFD (low 14 nibbles) + 0xFE digits 5-3.</summary>
    public UInt128 RegI()
    {
        var fe = Cpu.Ram[0xFE];
        UInt128 hi = ((UInt128)fe[5] << 8) | ((UInt128)fe[4] << 4) | fe[3];
        return (hi << 56) | Val(Cpu.Ram[0xFD], NutCpu.W);
    }
}
