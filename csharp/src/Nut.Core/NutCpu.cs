namespace Nut.Core;

/// <summary>
/// Nut CPU, cycle level, headless. Port of nut/cpu.py, which follows Eric
/// Smith's Nonpareil (src/proc_nut.c, digit_ops.c). Registers are arrays of
/// 14 digits, index 0 = least significant digit.
/// </summary>
public sealed class NutCpu
{
    public const int W = 14;
    public const int ExpSize = 3;
    public const int StackDepth = 4;
    public const int MaxRam = 1024;
    public const int KbdReleaseDebounceCycles = 32;

    static readonly int[] TMap = [3, 4, 5, 10, 8, 6, 11, -1, 2, 9, 7, 13, 1, 12, 0, -1];

    public enum Kb { Idle, Pressed, Released, WaitChk, WaitCyc }
    public enum Inst { Normal, LongBranch, Cxisa, Ldi, Selpf }

    // [dec][sub][carry][x][y] -> digit | carry << 4
    static readonly byte[] AddSub = BuildAddSub();

    static int AsIdx(bool dec, int sub, int cin, int x, int y) =>
        ((((dec ? 1 : 0) * 2 + sub) * 2 + cin) * 16 + x) * 16 + y;

    static byte[] BuildAddSub()
    {
        var t = new byte[2 * 2 * 2 * 16 * 16];
        foreach (var dec in new[] { false, true })
            for (int sub = 0; sub < 2; sub++)
                for (int cin = 0; cin < 2; cin++)
                    for (int x = 0; x < 16; x++)
                        for (int y = 0; y < 16; y++)
                        {
                            int c = cin ^ sub;
                            int yy = sub == 1 ? y ^ 0xF : y;
                            int s = x + yy + c;
                            c = s > 0xF ? 1 : 0;
                            if (dec)
                            {
                                if (sub == 1) { if (c == 0) s += 10; }
                                else
                                {
                                    c |= s > 9 ? 1 : 0;
                                    if (c == 1) s += 6;
                                }
                            }
                            c ^= sub;
                            t[AsIdx(dec, sub, cin, x, y)] = (byte)((s & 0xF) | (c << 4));
                        }
        return t;
    }

    // ------------------------------------------------------------- state
    public readonly int[] Rom = new int[0x10000];
    public readonly bool[] RomExists = new bool[0x10000];
    public readonly byte[][] Ram = new byte[MaxRam][];
    public readonly bool[] RamExists = new bool[MaxRam];
    public readonly Dictionary<int, Func<int, byte[]>> RamReadFn = [];
    public readonly Dictionary<int, Action<int, byte[]>> RamWriteFn = [];
    public readonly int[] ExtFlag = new int[14];
    public readonly Dictionary<int, int> UnknownOps = [];
    public readonly List<Action<NutCpu>> OnSleep = [];
    public readonly Dictionary<int, Action<NutCpu, int>> ExtraOps = [];
    public long Cycles;
    public Action<NutCpu, int, int>? Trace;
    public HashSet<int>? Coverage;

    // Debugger (optional, off when null/empty). A breakpoint is an instruction address; reaching it
    // before the instruction runs sets BreakHit and makes Cycle() return false without executing.
    // SkipBreak lets the next Cycle() run the instruction a halt stopped at.
    public HashSet<int>? Breakpoints;
    public bool BreakHit, SkipBreak;

    public byte[] A = new byte[W], B = new byte[W], C = new byte[W];
    public byte[] M = new byte[W], N = new byte[W];
    public byte[] G = new byte[2];
    public int P, Q;
    public bool QSel;
    public int Fo;
    public bool Decimal;
    public int Carry, PrevCarry;
    public byte[] S = new byte[14];
    public int Pc, PrevPc;
    public int[] Stack = new int[StackDepth];
    public Inst InstState;
    public int FirstWord, CxisaAddr, RamAddr, PfAddr;
    public bool Awake, KeyDown;
    public Kb KbState;
    public int KbCounter, KeyBuf;
    public bool DisplayEnable;

    public NutCpu()
    {
        for (int i = 0; i < MaxRam; i++) Ram[i] = new byte[W];
        Reset();
    }

    // ------------------------------------------------------------- setup
    public void Reset()
    {
        A = new byte[W]; B = new byte[W]; C = new byte[W];
        M = new byte[W]; N = new byte[W];
        G = new byte[2];
        P = 0; Q = 0; QSel = false; Fo = 0; Decimal = false;
        Carry = 1; PrevCarry = 0;
        S = new byte[14];
        Pc = 0; PrevPc = 0;
        Stack = new int[StackDepth];
        InstState = Inst.Normal;
        FirstWord = 0; CxisaAddr = 0; RamAddr = 0; PfAddr = 0;
        Awake = true; KeyDown = false;
        KbState = Kb.Idle; KbCounter = 0; KeyBuf = 0;
        DisplayEnable = false;
    }

    /// <summary>Nonpareil ROM text: "aaaa:www" per line, '#' comments.</summary>
    public void LoadRomLines(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 8 && line[4] == ':')
            {
                int addr = Convert.ToInt32(line[..4], 16);
                Rom[addr] = Convert.ToInt32(line[5..], 16);
                RomExists[addr] = true;
            }
        }
    }

    public void CreateRam(int baseAddr, int size)
    {
        for (int i = baseAddr; i < baseAddr + size; i++) RamExists[i] = true;
    }

    // ----------------------------------------------------------- helpers
    int Pt => QSel ? Q : P;

    void SetPt(int v)
    {
        if (QSel) Q = v; else P = v;
    }

    (int First, int Last) Field(int field) => field switch
    {
        0 => (Pt, Pt),
        1 => (0, ExpSize - 1),
        2 => (0, Pt),
        3 => (0, W - 1),
        4 => P > Q ? (P, W - 1) : (P, Q),
        5 => (ExpSize - 1, ExpSize - 1),
        6 => (ExpSize, W - 2),
        _ => (W - 1, W - 1),
    };

    void Add(byte[] dest, byte[] s1, byte[]? s2, int first, int last)
    {
        int c = Carry;
        for (int i = first; i <= last; i++)
        {
            int r = AddSub[AsIdx(Decimal, 0, c, s1[i], s2 is null ? 0 : s2[i])];
            dest[i] = (byte)(r & 0xF);
            c = r >> 4;
        }
        Carry = c;
    }

    void Sub(byte[]? dest, byte[]? s1, byte[]? s2, int first, int last)
    {
        int c = Carry;
        for (int i = first; i <= last; i++)
        {
            int r = AddSub[AsIdx(Decimal, 1, c, s1 is null ? 0 : s1[i], s2 is null ? 0 : s2[i])];
            if (dest is not null) dest[i] = (byte)(r & 0xF);
            c = r >> 4;
        }
        Carry = c;
    }

    void Push(int a)
    {
        for (int i = StackDepth - 1; i > 0; i--) Stack[i] = Stack[i - 1];
        Stack[0] = a;
    }

    int Pop()
    {
        int r = Stack[0];
        for (int i = 0; i < StackDepth - 1; i++) Stack[i] = Stack[i + 1];
        Stack[StackDepth - 1] = 0;
        return r;
    }

    int CAddr => (C[6] << 12) | (C[5] << 8) | (C[4] << 4) | C[3];

    static bool AnyNonZero(byte[] r, int first, int last)
    {
        for (int i = first; i <= last; i++) if (r[i] != 0) return true;
        return false;
    }

    static void Clear(byte[] r, int first, int last) => Array.Clear(r, first, last - first + 1);

    static void Copy(byte[] src, byte[] dst, int first, int last) =>
        Array.Copy(src, first, dst, first, last - first + 1);

    static void Swap(byte[] x, byte[] y, int first, int last)
    {
        for (int i = first; i <= last; i++) (x[i], y[i]) = (y[i], x[i]);
    }

    // --------------------------------------------------------- execution
    /// <summary>One instruction word (Nonpareil nut_execute_cycle).</summary>
    public bool Cycle()
    {
        KbdScannerCycle();
        if (!Awake) return false;
        if (Breakpoints is { Count: > 0 })
        {
            if (SkipBreak) SkipBreak = false;
            else if (InstState == Inst.Normal && Breakpoints.Contains(Pc)) { BreakHit = true; return false; }
        }
        PrevPc = InstState == Inst.Cxisa ? CxisaAddr : Pc;
        int op = Rom[PrevPc];
        Coverage?.Add(PrevPc);
        Trace?.Invoke(this, PrevPc, op);
        PrevCarry = Carry;
        Carry = 0;
        switch (InstState)
        {
            case Inst.Normal:
                Pc = (Pc + 1) & 0xFFFF;
                Execute(op);
                break;
            case Inst.LongBranch:
                Pc = (Pc + 1) & 0xFFFF;
                LongBranch2(op);
                break;
            case Inst.Cxisa:
                LoadC012(op);
                InstState = Inst.Normal;
                break;
            case Inst.Ldi:
                Pc = (Pc + 1) & 0xFFFF;
                LoadC012(op);
                InstState = Inst.Normal;
                break;
            default: // selpf: no smart peripherals on Voyagers
                Pc = (Pc + 1) & 0xFFFF;
                if ((op & 1) != 0) InstState = Inst.Normal;
                break;
        }
        Cycles++;
        return true;
    }

    void LoadC012(int op)
    {
        C[2] = (byte)(op >> 8);
        C[1] = (byte)((op >> 4) & 0xF);
        C[0] = (byte)(op & 0xF);
    }

    /// <summary>Run until asleep or maxCycles; returns cycles used.</summary>
    public long Run(long maxCycles)
    {
        long n = 0;
        while (Awake && n < maxCycles)
        {
            Cycle();
            if (BreakHit) break;
            n++;
        }
        return n;
    }

    void Execute(int op)
    {
        switch (op & 3)
        {
            case 2:
                Arith(op);
                break;
            case 3:
                int off = (op >> 3) & 0x3F;
                if ((op & 0x200) != 0) off -= 64;
                if (((op >> 2) & 1) == PrevCarry) Pc = (Pc + off - 1) & 0xFFFF;
                break;
            case 1:
                InstState = Inst.LongBranch;
                FirstWord = op;
                Carry = PrevCarry;
                break;
            default:
                Misc(op);
                break;
        }
    }

    void LongBranch2(int op)
    {
        InstState = Inst.Normal;
        int target = (FirstWord >> 2) | ((op & 0x3FC) << 6);
        if ((op & 1) != PrevCarry) return;
        if ((op & 2) != 0)
        {
            Pc = target;
        }
        else
        {
            Push(Pc);
            Pc = target;
            if (Rom[Pc] == 0) Pc = Pop();
        }
    }

    void Arith(int op)
    {
        int o = op >> 5;
        var (first, last) = Field((op >> 2) & 7);
        byte[] a = A, b = B, c = C;
        switch (o)
        {
            case 0x00: Clear(a, first, last); break;
            case 0x01: Clear(b, first, last); break;
            case 0x02: Clear(c, first, last); break;
            case 0x03: Swap(a, b, first, last); break;
            case 0x04: Copy(a, b, first, last); break;
            case 0x05: Swap(a, c, first, last); break;
            case 0x06: Copy(b, c, first, last); break;
            case 0x07: Swap(b, c, first, last); break;
            case 0x08: Copy(c, a, first, last); break;
            case 0x09: Add(a, a, b, first, last); break;
            case 0x0A: Add(a, a, c, first, last); break;
            case 0x0B: Carry = 1; Add(a, a, null, first, last); break;
            case 0x0C: Sub(a, a, b, first, last); break;
            case 0x0D: Carry = 1; Sub(a, a, null, first, last); break;
            case 0x0E: Sub(a, a, c, first, last); break;
            case 0x0F: Add(c, c, c, first, last); break;
            case 0x10: Add(c, a, c, first, last); break;
            case 0x11: Carry = 1; Add(c, c, null, first, last); break;
            case 0x12: Sub(c, a, c, first, last); break;
            case 0x13: Carry = 1; Sub(c, c, null, first, last); break;
            case 0x14: Sub(c, null, c, first, last); break;
            case 0x15: Carry = 1; Sub(c, null, c, first, last); break;
            case 0x16: Carry = AnyNonZero(b, first, last) ? 1 : 0; break;
            case 0x17: Carry = AnyNonZero(c, first, last) ? 1 : 0; break;
            case 0x18: Sub(null, a, c, first, last); break;
            case 0x19: Sub(null, a, b, first, last); break;
            case 0x1A: Carry = AnyNonZero(a, first, last) ? 1 : 0; break;
            case 0x1B:
                Carry = 0;
                for (int i = first; i <= last; i++)
                    if (a[i] != c[i]) { Carry = 1; break; }
                break;
            case 0x1C: case 0x1D: case 0x1E:
                var r = o == 0x1C ? a : o == 0x1D ? b : c;
                for (int i = first; i < last; i++) r[i] = r[i + 1];
                r[last] = 0;
                break;
            default: // 0x1F a sl
                for (int i = last; i > first; i--) a[i] = a[i - 1];
                a[first] = 0;
                break;
        }
    }

    void Misc(int op)
    {
        if (ExtraOps.TryGetValue(op, out var fn))
        {
            fn(this, op);
            return;
        }
        int lo = op & 0x3C;
        int arg = op >> 6;
        int tm = TMap[arg];
        switch (lo)
        {
            case 0x00:
                if (op != 0x000) Unknown(op); // wrrom, enbank, wr pil: not on Voyagers
                break;
            case 0x04:
                if (op == 0x3C4) Array.Clear(S, 0, 8);
                else if (tm >= 0) S[tm] = 0;
                else Unknown(op);
                break;
            case 0x08:
                if (op == 0x3C8) { if (KbState == Kb.Released) KbState = Kb.WaitChk; }
                else if (tm >= 0) S[tm] = 1;
                else Unknown(op);
                break;
            case 0x0C:
                if (op == 0x3CC)
                {
                    Carry = KbState is Kb.Pressed or Kb.Released ? 1 : 0;
                    if (KbState == Kb.WaitChk)
                    {
                        KbState = Kb.WaitCyc;
                        KbCounter = KbdReleaseDebounceCycles;
                    }
                }
                else if (tm >= 0) Carry = S[tm];
                else Unknown(op);
                break;
            case 0x10: // lc n
            {
                int pt = Pt;
                C[pt] = (byte)arg;
                SetPt(pt > 0 ? pt - 1 : W - 1);
                break;
            }
            case 0x14:
                if (op == 0x3D4) { int pt = Pt; SetPt(pt > 0 ? pt - 1 : W - 1); }
                else if (tm >= 0) Carry = Pt == tm ? 1 : 0;
                else Unknown(op);
                break;
            case 0x18: Op18(op); break;
            case 0x1C:
                if (op == 0x3DC) { int pt = Pt + 1; SetPt(pt >= W ? 0 : pt); }
                else if (tm >= 0) SetPt(tm);
                else Unknown(op);
                break;
            case 0x20: Op20(op); break;
            case 0x24: InstState = Inst.Selpf; break;
            case 0x28: // reg=c n
                RamAddr = (RamAddr & ~0x0F) | arg;
                RamWrite(RamAddr);
                break;
            case 0x2C: // ? ext n
                Carry = tm >= 0 ? ExtFlag[tm] : 0;
                break;
            case 0x30: Op30(op); break;
            case 0x38: // c=reg n / c=data
                C = new byte[W];
                if (arg != 0) RamAddr = (RamAddr & ~0x0F) | arg;
                RamRead(RamAddr);
                break;
            case 0x3C:
                if (tm >= 0) // rcr n
                {
                    var r = new byte[W];
                    for (int i = 0; i < W; i++) r[i] = C[(i + tm) % W];
                    C = r;
                }
                else Unknown(op); // 0x3fc display compensation
                break;
            default:
                Unknown(op);
                break;
        }
    }

    void Op18(int op)
    {
        int k = op >> 6;
        int pt = Pt;
        var (lo, hi) = pt < W - 1 ? (pt, pt + 1) : (0, W - 1);
        switch (k)
        {
            case 0x1: G = [C[lo], C[hi]]; break; // g=c
            case 0x2: C[lo] = G[0]; C[hi] = G[1]; break; // c=g
            case 0x3: // c<>g
            {
                byte[] old = [C[lo], C[hi]];
                C[lo] = G[0]; C[hi] = G[1];
                G = old;
                break;
            }
            case 0x5: M = (byte[])C.Clone(); break;
            case 0x6: C = (byte[])M.Clone(); break;
            case 0x7: (C, M) = (M, C); break;
            case 0x9: Fo = SBits(0, 8); break; // f=sb
            case 0xA: SetSBits(0, 8, Fo); break; // sb=f
            case 0xB: // f<>sb
            {
                int t = SBits(0, 8);
                SetSBits(0, 8, Fo);
                Fo = t;
                break;
            }
            case 0xD: SetSBits(0, 4, C[0]); SetSBits(4, 4, C[1]); break; // st=c
            case 0xE: C[0] = (byte)SBits(0, 4); C[1] = (byte)SBits(4, 4); break; // c=st
            case 0xF: // c<>st
            {
                int t0 = SBits(0, 4), t1 = SBits(4, 4);
                SetSBits(0, 4, C[0]);
                SetSBits(4, 4, C[1]);
                C[0] = (byte)t0; C[1] = (byte)t1;
                break;
            }
            default: Unknown(op); break;
        }
    }

    void Op20(int op)
    {
        switch (op >> 6)
        {
            case 0x0: Pop(); break; // pop stk
            case 0x1: PowOff(); break;
            case 0x2: QSel = false; break;
            case 0x3: QSel = true; break;
            case 0x4: if (P == Q) Carry = 1; break; // ? p=q
            case 0x5: Carry = 0; break; // lld: batteries fine
            case 0x6: A = new byte[W]; B = new byte[W]; C = new byte[W]; break; // clr abc
            case 0x7: Pc = CAddr; break; // goto c
            case 0x8: C[4] = (byte)(KeyBuf >> 4); C[3] = (byte)(KeyBuf & 0xF); break; // c=keys
            case 0x9: Decimal = false; break;
            case 0xA: Decimal = true; break;
            case 0xD: if (PrevCarry != 0) Pc = Pop(); break; // ?c rtn
            case 0xE: if (PrevCarry == 0) Pc = Pop(); break; // ?nc rtn
            case 0xF: Pc = Pop(); break; // rtn
            default: Unknown(op); break; // 0x2e0 / 0x320 come from the display chip
        }
    }

    void Op30(int op)
    {
        switch (op >> 6)
        {
            case 0x1: N = (byte[])C.Clone(); break;
            case 0x2: C = (byte[])N.Clone(); break;
            case 0x3: (C, N) = (N, C); break;
            case 0x4: InstState = Inst.Ldi; break;
            case 0x5: Push(CAddr); break; // stk=c
            case 0x6: // c=stk
            {
                int a = Pop();
                C[6] = (byte)(a >> 12); C[5] = (byte)((a >> 8) & 0xF);
                C[4] = (byte)((a >> 4) & 0xF); C[3] = (byte)(a & 0xF);
                break;
            }
            case 0x8: Pc = (Pc & 0xFF00) | KeyBuf; break; // goto keys
            case 0x9: RamAddr = ((C[2] << 8) | (C[1] << 4) | C[0]) & 0x3FF; break; // c=dadd
            case 0xB: RamWrite(RamAddr); break; // data=c
            case 0xC: CxisaAddr = CAddr; InstState = Inst.Cxisa; break;
            case 0xD: { var r = new byte[W]; for (int i = 0; i < W; i++) r[i] = (byte)(C[i] | A[i]); C = r; break; }
            case 0xE: { var r = new byte[W]; for (int i = 0; i < W; i++) r[i] = (byte)(C[i] & A[i]); C = r; break; }
            case 0xF: PfAddr = (C[1] << 4) | C[0]; break; // sel pfad
            default: Unknown(op); break;
        }
    }

    int SBits(int first, int count)
    {
        int r = 0;
        for (int i = 0; i < count; i++) if (S[first + i] != 0) r |= 1 << i;
        return r;
    }

    void SetSBits(int first, int count, int v)
    {
        for (int i = 0; i < count; i++) S[first + i] = (byte)((v >> i) & 1);
    }

    void RamRead(int addr)
    {
        if (!RamExists[addr]) return;
        C = RamReadFn.TryGetValue(addr, out var fn) ? fn(addr) : (byte[])Ram[addr].Clone();
    }

    void RamWrite(int addr)
    {
        if (!RamExists[addr]) return;
        if (RamWriteFn.TryGetValue(addr, out var fn)) fn(addr, (byte[])C.Clone());
        else Ram[addr] = (byte[])C.Clone();
    }

    void Unknown(int op) => UnknownOps[op] = UnknownOps.GetValueOrDefault(op) + 1;

    // ---------------------------------------------------------- keyboard
    void KbdScannerCycle()
    {
        if (KbState != Kb.WaitCyc) return;
        KbCounter--;
        if (KbCounter != 0) return;
        if (KeyDown)
        {
            KbState = Kb.Pressed;
            Awake = true;
        }
        else KbState = Kb.Idle;
    }

    void PowOff()
    {
        Awake = false;
        Pc = 0;
        if (KbState == Kb.Pressed) Awake = true;
        if (KbState == Kb.WaitCyc)
        {
            if (KeyDown)
            {
                KbState = Kb.Pressed;
                Awake = true;
            }
            else KbState = Kb.Idle;
        }
        foreach (var fn in OnSleep) fn(this);
    }

    /// <summary>Key down. In deep sleep (display off) only ON (0x18) wakes the CPU.</summary>
    public bool Press(int hwKeycode)
    {
        if (!Awake && !DisplayEnable && hwKeycode != 0x18) return false;
        KeyBuf = hwKeycode;
        KeyDown = true;
        if (KbState == Kb.Idle) KbState = Kb.Pressed;
        Awake = true;
        return true;
    }

    public void Release()
    {
        KeyDown = false;
        if (KbState == Kb.Pressed) KbState = Kb.Released;
    }
}
