using System.Text;
using Nut.Core;

namespace Nut.Dap;

/// <summary>
/// A log of the instructions the CPU executed: one line per instruction (a two-word instruction is one line, the
/// unit the debugger steps), as pure CSV: <c>cycle,pc,words,mnemonic,repeat</c>, with the registers appended as more
/// CSV columns when asked for. A group of up to 8 instructions that runs again and again (a key-wait loop) is written
/// once, every line of it with <c>repeat</c> = how many times the group ran in a row; ordinary lines have 1.
/// Two parts, both off unless the command line asks for them: a ring buffer in memory that keeps the last N
/// instructions (--trace N), and a text file that gets the instructions as they run, up to a limit of written lines
/// (--trace-file FILE, --trace-max N). The CPU thread calls <see cref="Record"/>.
/// </summary>
public sealed class TraceLog
{
    public const string Header = "cycle,pc,words,mnemonic,repeat";
    public const string HeaderRegs = Header + ",carry,A,B,C,M,N,P,Q,S";

    readonly int _size;
    readonly long[] _cycles;
    readonly int[] _pcs;
    readonly ulong[]? _regs;        // A, B, C, M, N packed 4 bits a digit (14 digits = 56 bits), five per entry
    readonly ulong[]? _extra;       // carry, P, Q, S flags
    long _count;                    // instructions recorded since the start
    StreamWriter? _writer;
    Collapser? _fileOut;
    readonly long _fileMax;
    long _fileLines;
    bool _limitReported;

    public bool WithRegisters { get; }
    public long Recorded => _count;
    public string? FilePath { get; }

    /// <summary>Told once when the file reached its line limit (the CSV itself stays pure data).</summary>
    public Action<string>? OnLimit { get; set; }

    public TraceLog(int ringSize, bool withRegisters, string? filePath, long fileMax)
    {
        _size = Math.Max(ringSize, 0);
        _cycles = new long[_size];
        _pcs = new int[_size];
        if (withRegisters && _size > 0)
        {
            _regs = new ulong[_size * 5];
            _extra = new ulong[_size];
        }
        WithRegisters = withRegisters;
        FilePath = filePath;
        _fileMax = fileMax;
        if (filePath is not null)
        {
            _writer = new StreamWriter(filePath, false, new UTF8Encoding(false), 1 << 16);
            _writer.WriteLine(withRegisters ? HeaderRegs : Header);
            _fileOut = new Collapser(WriteFileLine);
        }
    }

    static ulong Pack(byte[] digits)
    {
        ulong v = 0;
        for (int i = NutCpu.W - 1; i >= 0; i--) v = (v << 4) | (uint)(digits[i] & 0xF);
        return v;
    }

    static ulong PackExtra(NutCpu c)
    {
        ulong s = 0;
        for (int i = 13; i >= 0; i--) s = (s << 1) | (c.S[i] != 0 ? 1UL : 0UL);
        return (uint)c.Carry & 1 | (ulong)(c.P & 0xF) << 1 | (ulong)(c.Q & 0xF) << 5 | s << 9;
    }

    /// <summary>Called before an instruction runs (first word only), on the CPU thread.</summary>
    public void Record(NutCpu cpu, int pc)
    {
        if (_size > 0)
        {
            int i = (int)(_count % _size);
            _cycles[i] = cpu.Cycles;
            _pcs[i] = pc;
            if (_regs is not null && _extra is not null)
            {
                int k = i * 5;
                _regs[k] = Pack(cpu.A); _regs[k + 1] = Pack(cpu.B); _regs[k + 2] = Pack(cpu.C);
                _regs[k + 3] = Pack(cpu.M); _regs[k + 4] = Pack(cpu.N);
                _extra[i] = PackExtra(cpu);
            }
        }
        _count++;
        if (_fileOut is not null)
            _fileOut.Add(Make(cpu.Cycles, pc, cpu.Rom, Pack(cpu.A), Pack(cpu.B), Pack(cpu.C), Pack(cpu.M), Pack(cpu.N),
                              PackExtra(cpu)));
    }

    Item Make(long cycle, int pc, int[] rom, ulong a, ulong b, ulong c, ulong m, ulong n, ulong extra)
    {
        var d = NutDisassembler.Decode(rom, pc);
        var words = string.Join(' ', Enumerable.Range(0, d.Length).Select(k => rom[(pc + k) & 0xFFFF].ToString("X3")));
        var core = $"{pc:X4},{words},{d.Text}";
        string regs = "";
        if (WithRegisters)
        {
            var sb = new StringBuilder();
            sb.Append(',').Append(extra & 1);
            foreach (var r in new[] { a, b, c, m, n }) sb.Append(',').Append(r.ToString("X14"));
            sb.Append(',').Append(((extra >> 1) & 0xF).ToString("X")).Append(',').Append(((extra >> 5) & 0xF).ToString("X"));
            sb.Append(',').Append(((extra >> 9) & 0x3FFF).ToString("X4"));
            regs = sb.ToString();
        }
        return new Item(core + regs, cycle, core, regs);
    }

    static string Line(Item it, long repeat) => $"{it.Cycle},{it.Core},{repeat}{it.Regs}";

    void WriteFileLine(Item it, long repeat)
    {
        if (_writer is null) return;
        if (_fileLines >= _fileMax)
        {
            if (!_limitReported)
            {
                _limitReported = true;
                OnLimit?.Invoke($"Trace file {FilePath}: {_fileMax} lines written (--trace-max), no more lines are added.");
            }
            return;
        }
        _writer.WriteLine(Line(it, repeat));
        _fileLines++;
    }

    /// <summary>The last <paramref name="n"/> recorded instructions, oldest first, repeated groups collapsed.</summary>
    public List<string> Last(int n, int[] rom)
    {
        var lines = new List<string>();
        if (_size == 0) return lines;
        long take = Math.Min(Math.Min(n, _count), _size);
        var c = new Collapser((it, rep) => lines.Add(Line(it, rep)));
        for (long k = _count - take; k < _count; k++)
        {
            int i = (int)(k % _size);
            if (_regs is not null && _extra is not null)
            {
                int j = i * 5;
                c.Add(Make(_cycles[i], _pcs[i], rom, _regs[j], _regs[j + 1], _regs[j + 2], _regs[j + 3], _regs[j + 4], _extra[i]));
            }
            else c.Add(Make(_cycles[i], _pcs[i], rom, 0, 0, 0, 0, 0, 0));
        }
        c.Flush();
        return lines;
    }

    /// <summary>Write the ring buffer as a CSV file with a header line; returns how many lines.</summary>
    public int Save(string path, int[] rom)
    {
        var lines = Last(_size, rom);
        File.WriteAllLines(path, lines.Prepend(WithRegisters ? HeaderRegs : Header), new UTF8Encoding(false));
        return lines.Count;
    }

    /// <summary>Make the file complete up to now (an open loop is closed), for reading it while the app runs.</summary>
    public void Flush()
    {
        _fileOut?.Flush();
        _writer?.Flush();
    }

    public void Close()
    {
        Flush();
        _writer?.Dispose();
        _writer = null;
        _fileOut = null;
    }

    sealed record Item(string Key, long Cycle, string Core, string Regs);

    /// <summary>
    /// Finds groups of up to <see cref="MaxPeriod"/> instructions that repeat in a row (same address, words, mnemonic
    /// and, when the registers are logged, the same registers) and writes the group once with the repeat count.
    /// Lines are held back only while a repeat may be going on (at most 2 x MaxPeriod lines, or the whole loop).
    /// </summary>
    sealed class Collapser(Action<Item, long> write)
    {
        const int MaxPeriod = 8;
        readonly List<Item> _win = [];          // lines not yet written, no repeat seen
        List<Item>? _block;                     // the first pass of a repeating group
        readonly List<Item> _partial = [];      // lines of the current, unfinished pass
        long _reps;

        public void Add(Item x)
        {
            if (_block is not null)
            {
                if (_block[_partial.Count].Key == x.Key)
                {
                    _partial.Add(x);
                    if (_partial.Count == _block.Count) { _reps++; _partial.Clear(); }
                    return;
                }
                EndLoop();
            }
            _win.Add(x);
            for (int p = 1; p <= MaxPeriod && _win.Count >= 2 * p; p++)
            {
                bool same = true;
                for (int i = 0; i < p && same; i++) same = _win[_win.Count - 1 - i].Key == _win[_win.Count - 1 - p - i].Key;
                if (!same) continue;
                int start = _win.Count - 2 * p;
                for (int i = 0; i < start; i++) write(_win[i], 1);
                _block = _win.GetRange(start, p);
                _win.Clear();
                _partial.Clear();
                _reps = 2;
                return;
            }
            while (_win.Count > 2 * MaxPeriod) { write(_win[0], 1); _win.RemoveAt(0); }
        }

        void EndLoop()
        {
            foreach (var it in _block!) write(it, _reps);
            foreach (var it in _partial) write(it, 1);
            _block = null;
            _partial.Clear();
        }

        public void Flush()
        {
            if (_block is not null) EndLoop();
            foreach (var it in _win) write(it, 1);
            _win.Clear();
        }
    }
}
