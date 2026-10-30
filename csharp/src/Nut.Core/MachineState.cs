using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nut.Core;

/// <summary>
/// Complete state of a running Voyager (CPU, RAM, display chip) as readable
/// JSON. Registers are hex digit strings, most significant digit first, as
/// the status panel shows them; RAM is listed per register address. The ROM
/// is not saved, only its fingerprint, so a state is never loaded into a
/// different ROM unnoticed.
/// </summary>
public sealed class MachineState
{
    public string Format { get; set; } = "voyager-state 1";
    public string Calculator { get; set; } = "";
    public string Model { get; set; } = "";
    public string Rom { get; set; } = "";
    public string RomFingerprint { get; set; } = "";
    public List<string> Patches { get; set; } = [];
    public string SavedAt { get; set; } = "";
    public string Note { get; set; } = "";
    public string DisplayText { get; set; } = "";
    public DisplayChip Display { get; set; } = new();
    public Cpu Processor { get; set; } = new();
    /// <summary>RAM register address (hex) -> 14 digits (hex, most significant first).</summary>
    public SortedDictionary<string, string> Ram { get; set; } = [];

    public sealed class DisplayChip
    {
        public bool On { get; set; }
        public bool Blink { get; set; }
    }

    public sealed class Cpu
    {
        public string A { get; set; } = "";
        public string B { get; set; } = "";
        public string C { get; set; } = "";
        public string M { get; set; } = "";
        public string N { get; set; } = "";
        public string G { get; set; } = "";
        public int P { get; set; }
        public int Q { get; set; }
        public bool QSelected { get; set; }
        /// <summary>Status bits 13..0 as 0/1.</summary>
        public string S { get; set; } = "";
        public string Fo { get; set; } = "";
        public bool DecimalMode { get; set; }
        public int Carry { get; set; }
        public int PrevCarry { get; set; }
        public string Pc { get; set; } = "";
        public string PrevPc { get; set; } = "";
        public List<string> ReturnStack { get; set; } = [];
        public string InstState { get; set; } = "";
        public string FirstWord { get; set; } = "";
        public string CxisaAddr { get; set; } = "";
        public string RamAddr { get; set; } = "";
        public string PfAddr { get; set; } = "";
        public bool Awake { get; set; }
        public bool KeyDown { get; set; }
        public string KeyboardState { get; set; } = "";
        public int KeyboardCounter { get; set; }
        public string KeyBuffer { get; set; } = "";
        public long Cycles { get; set; }
    }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static MachineState FromJson(string text) =>
        JsonSerializer.Deserialize<MachineState>(text, Json) ?? throw new InvalidDataException("empty state file");

    static string Reg(byte[] r) => string.Concat(r.Reverse().Select(d => d.ToString("x")));

    static byte[] Reg(string s, int len = NutCpu.W)
    {
        s = s.Trim();
        if (s.Length != len) throw new InvalidDataException($"register '{s}' must have {len} hex digits");
        var r = new byte[len];
        for (int i = 0; i < len; i++) r[len - 1 - i] = (byte)Convert.ToInt32(s[i].ToString(), 16);
        return r;
    }

    static int Hex(string s) => Convert.ToInt32(s, 16);

    public static MachineState Capture(Voyager v)
    {
        var c = v.Cpu;
        var st = new MachineState
        {
            Model = v.Model,
            SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            DisplayText = v.Display(),
            Display = new DisplayChip { On = v.DisplayEnable, Blink = v.Blink },
            Processor = new Cpu
            {
                A = Reg(c.A), B = Reg(c.B), C = Reg(c.C), M = Reg(c.M), N = Reg(c.N),
                G = $"{c.G[1]:x}{c.G[0]:x}",
                P = c.P, Q = c.Q, QSelected = c.QSel,
                S = string.Concat(Enumerable.Range(0, 14).Reverse().Select(i => c.S[i] != 0 ? '1' : '0')),
                Fo = c.Fo.ToString("x2"), DecimalMode = c.Decimal,
                Carry = c.Carry, PrevCarry = c.PrevCarry,
                Pc = c.Pc.ToString("x4"), PrevPc = c.PrevPc.ToString("x4"),
                ReturnStack = c.Stack.Select(a => a.ToString("x4")).ToList(),
                InstState = c.InstState.ToString(),
                FirstWord = c.FirstWord.ToString("x3"), CxisaAddr = c.CxisaAddr.ToString("x4"),
                RamAddr = c.RamAddr.ToString("x3"), PfAddr = c.PfAddr.ToString("x2"),
                Awake = c.Awake, KeyDown = c.KeyDown,
                KeyboardState = c.KbState.ToString(), KeyboardCounter = c.KbCounter,
                KeyBuffer = c.KeyBuf.ToString("x2"), Cycles = c.Cycles,
            },
        };
        for (int a = 0; a < NutCpu.MaxRam; a++)
            if (c.RamExists[a]) st.Ram[a.ToString("x3")] = Reg(c.Ram[a]);
        return st;
    }

    /// <summary>Put this state into a machine with the same chips. Throws with a
    /// readable reason on a malformed file; checks nothing about the ROM.</summary>
    public void Apply(Voyager v)
    {
        var c = v.Cpu;
        var p = Processor;
        foreach (var (addr, digits) in Ram)
        {
            int a = Hex(addr);
            if (a < 0 || a >= NutCpu.MaxRam || !c.RamExists[a])
                throw new InvalidDataException($"RAM register {addr} does not exist on this calculator");
            Reg(digits); // validate
        }
        c.A = Reg(p.A); c.B = Reg(p.B); c.C = Reg(p.C); c.M = Reg(p.M); c.N = Reg(p.N);
        var g = Reg(p.G, 2);
        c.G = g;
        c.P = p.P; c.Q = p.Q; c.QSel = p.QSelected;
        var s = p.S.Trim();
        if (s.Length != 14) throw new InvalidDataException("status bits S must have 14 digits (13..0)");
        for (int i = 0; i < 14; i++) c.S[13 - i] = (byte)(s[i] == '1' ? 1 : 0);
        c.Fo = Hex(p.Fo); c.Decimal = p.DecimalMode;
        c.Carry = p.Carry; c.PrevCarry = p.PrevCarry;
        c.Pc = Hex(p.Pc); c.PrevPc = Hex(p.PrevPc);
        for (int i = 0; i < NutCpu.StackDepth; i++)
            c.Stack[i] = i < p.ReturnStack.Count ? Hex(p.ReturnStack[i]) : 0;
        c.InstState = Enum.Parse<NutCpu.Inst>(p.InstState);
        c.FirstWord = Hex(p.FirstWord); c.CxisaAddr = Hex(p.CxisaAddr);
        c.RamAddr = Hex(p.RamAddr); c.PfAddr = Hex(p.PfAddr);
        c.Awake = p.Awake; c.KeyDown = p.KeyDown;
        c.KbState = Enum.Parse<NutCpu.Kb>(p.KeyboardState); c.KbCounter = p.KeyboardCounter;
        c.KeyBuf = Hex(p.KeyBuffer); c.Cycles = p.Cycles;
        foreach (var (addr, digits) in Ram) c.Ram[Hex(addr)] = Reg(digits);
        v.RestoreDisplay(Display.On, Display.Blink);
    }
}
