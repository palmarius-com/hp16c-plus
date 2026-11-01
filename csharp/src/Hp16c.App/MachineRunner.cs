using System.Diagnostics;
using System.Text;
using Nut.Core;
using Nut.Dap;

namespace HpSim;

/// <summary>
/// Runs a calculator on a background thread. Keys go in as press/release
/// (so holding a key works as on the real one, e.g. STATUS); the UI reads
/// snapshots. Real speed: the Voyager's 215 kHz LC oscillator, 56 clocks
/// per instruction word.
/// </summary>
enum PanelView { WordSize, Bits64, Bits128 }

sealed class MachineRunner : IDisposable, IDebugTarget
{
    public const double RealWordsPerSecond = 215_000.0 / 56;
    const int HistorySize = 24;

    readonly object _gate = new();
    readonly Thread _thread;
    volatile bool _stop;
    readonly int[] _history = new int[HistorySize];
    int _historyPos;

    public Voyager Calc { get; }
    public string Title { get; }
    public volatile bool RealSpeed;
    public volatile PanelView View = PanelView.WordSize;

    /// <summary>The instruction log, if the command line asked for one (--trace, --trace-file).</summary>
    public TraceLog? TraceLog { get; }

    public MachineRunner(Voyager calc, string title, TraceLog? traceLog = null)
    {
        Calc = calc;
        Title = title;
        TraceLog = traceLog;
        Calc.Cpu.Trace = (cpu, pc, _) =>
        {
            _history[_historyPos] = pc;
            _historyPos = (_historyPos + 1) % HistorySize;
            // one line per instruction: the first word only (not the second word of a two-word instruction, not a data fetch)
            if (TraceLog is not null && cpu.InstState == NutCpu.Inst.Normal) TraceLog.Record(cpu, pc);
        };
        lock (_gate) Calc.PowerOn();
        _thread = new Thread(Loop) { IsBackground = true, Name = "Nut CPU" };
        _thread.Start();
    }

    // Key events from the UI, applied by the CPU thread: a key goes down only
    // when the previous one is fully processed (scanner idle), and stays down
    // for at least MinHoldCycles, so fast typing loses nothing.
    const long MinHoldCycles = 200;
    readonly System.Collections.Concurrent.ConcurrentQueue<int> _keys = new(); // code, or -1 = release
    long _pressedAt = -1;
    // Instruction cycles from a key going down to the calculator sleeping again: what a key (or a patched
    // routine) costs, and with RealWordsPerSecond what it would take on the real calculator.
    long _runStart = -1;
    long _lastRun = -1;
    long _releasedAt = -1;
    // The CPU thread sleeps on this while the calculator sleeps: a key (or a stop) wakes it
    // at once, otherwise it looks again every 50 ms. No polling every 2 ms.
    readonly AutoResetEvent _wake = new(false);

    // Set whenever the display may have changed (CPU ran, key applied, state loaded); the window
    // draws only when it is set, so an idle calculator costs nothing.
    int _dirty = 1;
    public void Touch() => Volatile.Write(ref _dirty, 1);
    public bool TakeDirty() => Interlocked.Exchange(ref _dirty, 0) != 0;

    void ApplyKeys()
    {
        var cpu = Calc.Cpu;
        while (_keys.TryPeek(out var ev))
        {
            if (ev >= 0)
            {
                // A press with a key still down is a chord (ON + key): only the
                // minimum hold applies. Otherwise wait for the scanner.
                if (_pressedAt >= 0)
                {
                    if (cpu.Awake && cpu.Cycles - _pressedAt < MinHoldCycles) return;
                }
                else if (cpu.KbState != NutCpu.Kb.Idle) return;
                _keys.TryDequeue(out _);
                if (Calc.Keys.TryGetValue(ev, out var hw) && cpu.Press(hw))
                {
                    _pressedAt = cpu.Cycles;
                    if (_runStart < 0) _runStart = cpu.Cycles;
                }
                else if (_keys.TryPeek(out var next) && next < 0) _keys.TryDequeue(out _);
            }
            else
            {
                if (_pressedAt >= 0 && cpu.Awake && cpu.Cycles - _pressedAt < MinHoldCycles) return;
                _keys.TryDequeue(out _);
                cpu.Release();
                _releasedAt = cpu.Cycles;
                _pressedAt = -1;
            }
        }
    }

    void Loop()
    {
        var clock = Stopwatch.StartNew();
        double credit = 0;
        long last = clock.ElapsedTicks;
        while (!_stop)
        {
            bool awake;
            lock (_gate)
            {
                if (!_halted)
                {
                    if (!_keys.IsEmpty) Touch();
                    ApplyKeys();
                }
                awake = Calc.Cpu.Awake;
                if (!awake && _runStart >= 0 && _keys.IsEmpty && _pressedAt < 0)
                {
                    // a key held longer than the minimum only makes the CPU spin: leave that out
                    long held = _releasedAt >= _runStart ? _releasedAt - _runStart : 0;
                    _lastRun = Calc.Cpu.Cycles - _runStart - Math.Max(0, held - MinHoldCycles);
                    _runStart = -1;
                    Touch();
                }
            }
            if (_halted)
            {
                // stopped by the debugger: the window stays alive, keys wait in the queue
                _wake.WaitOne(20);
                last = clock.ElapsedTicks;
                credit = 0;
                continue;
            }
            if (!awake)
            {
                _wake.WaitOne(_keys.IsEmpty ? 250 : 2);
                last = clock.ElapsedTicks;
                credit = 0;
                continue;
            }
            long budget = _keys.IsEmpty ? 20_000 : 50;
            if (RealSpeed)
            {
                long now = clock.ElapsedTicks;
                credit = Math.Min(credit + (now - last) * RealWordsPerSecond / Stopwatch.Frequency, 2000);
                last = now;
                budget = Math.Min(budget, (long)credit);
                credit -= budget;
                if (budget == 0) { Thread.Sleep(1); continue; }
            }
            bool hit;
            int hitPc = 0;
            lock (_gate)
            {
                Calc.Cpu.Run(budget);
                hit = Calc.Cpu.BreakHit;
                if (hit)
                {
                    Calc.Cpu.BreakHit = false;
                    _halted = true;
                    hitPc = Calc.Cpu.Pc;
                }
            }
            Touch();
            if (hit) Stopped?.Invoke("instruction breakpoint", $"breakpoint at {hitPc:X4}");
        }
    }

    /// <summary>Told about every key: the code, or -1 for release (for showing it).</summary>
    public Action<int>? KeySeen;

    public void Press(int userKeycode)
    {
        _keys.Enqueue(userKeycode);
        _wake.Set();
        KeySeen?.Invoke(userKeycode);
    }

    public void Release()
    {
        _keys.Enqueue(-1);
        _wake.Set();
        KeySeen?.Invoke(-1);
    }

    /// <summary>Press and release as one step. At full speed a gap between the
    /// two is seconds of calculator time, and the firmware cancels a key held
    /// that long.</summary>
    public void Tap(int userKeycode)
    {
        lock (_gate)
        {
            _keys.Enqueue(userKeycode);
            _keys.Enqueue(-1);
        }
        _wake.Set();
        KeySeen?.Invoke(userKeycode);
        KeySeen?.Invoke(-1);
    }

    /// <summary>Wait until the calculator ran n more words, or went to sleep.</summary>
    public void WaitCycles(long n, CancellationToken stop = default)
    {
        var limit = DateTime.UtcNow.AddSeconds(60);
        while (!KeysApplied && DateTime.UtcNow < limit) Thread.Sleep(1);
        long end = Read(v => v.Cpu.Cycles) + n;
        while (Read(v => v.Cpu.Cycles) < end && Read(v => v.Cpu.Awake) &&
               DateTime.UtcNow < limit && !stop.IsCancellationRequested) Thread.Sleep(1);
    }

    /// <summary>Two keys together: first down, second down, release.</summary>
    public void Chord(int first, int second, long hold, CancellationToken stop = default)
    {
        Press(first);
        WaitCycles(hold, stop);
        Press(second);
        WaitCycles(hold, stop);
        Release();
    }

    /// <summary>Read the machine under the CPU lock.</summary>
    public T Read<T>(Func<Voyager, T> read)
    {
        lock (_gate) return read(Calc);
    }

    /// <summary>The CPU thread has taken every queued key event.</summary>
    public bool KeysApplied => _keys.IsEmpty;

    /// <summary>All keys handled and the calculator waits for the next one.</summary>
    public bool Finished
    {
        get
        {
            lock (_gate)
                return _keys.IsEmpty && _pressedAt < 0 && !Calc.Cpu.Awake &&
                       Calc.Cpu.KbState == NutCpu.Kb.Idle;
        }
    }

    /// <summary>Count from 0 again (the count is saved in a state file). Keeps a key that is
    /// down counting its hold time.</summary>
    public void ResetCycles()
    {
        lock (_gate)
        {
            Calc.Cpu.Cycles = 0;
            if (_pressedAt >= 0) _pressedAt = 0;
            if (_runStart >= 0) _runStart = 0;
            _lastRun = -1;
        }
        Touch();
    }

    public void PowerOn()
    {
        _keys.Clear();
        lock (_gate) { Calc.PowerOn(); _pressedAt = -1; }
        Touch();
        _wake.Set();
    }

    public MachineState SaveState()
    {
        lock (_gate) return MachineState.Capture(Calc);
    }

    public void LoadState(MachineState state)
    {
        _keys.Clear();
        lock (_gate)
        {
            state.Apply(Calc);
            _pressedAt = -1;
        }
        Touch();
        _wake.Set();
    }

    public sealed record Snapshot(Dictionary<char, bool>[] Segments, bool DisplayOn, bool Blink, string Status);

    /// <summary>Only what the view draws: the segments and whether the display is on.
    /// No status text, which is costly to build and only the panel needs.</summary>
    public Snapshot TakeDisplay()
    {
        lock (_gate) return new Snapshot(Calc.SegmentState(), Calc.DisplayEnable, Calc.Blink, "");
    }

    public Snapshot Take()
    {
        lock (_gate)
        {
            var cpu = Calc.Cpu;
            return new Snapshot(Calc.SegmentState(), Calc.DisplayEnable, Calc.Blink, StatusText(cpu));
        }
    }

    static string Reg(byte[] r) => string.Concat(r.Reverse().Select(d => d.ToString("X")));

    const int PanelColumns = 42;
    const string Below = " (below)";

    static IEnumerable<string> Wrap(string text)
    {
        var line = "";
        foreach (var word in text.Split(' '))
        {
            var rest = word;
            while (rest.Length > PanelColumns)
            {
                if (line.Length > 0) { yield return line; line = ""; }
                yield return rest[..PanelColumns];
                rest = rest[PanelColumns..];
            }
            if (line.Length + 1 + rest.Length > PanelColumns) { yield return line; line = rest; }
            else line = line.Length == 0 ? rest : line + " " + rest;
        }
        if (line.Length > 0) yield return line;
    }

    string StatusText(NutCpu cpu)
    {
        var sb = new StringBuilder();
        var notes = new List<string>();
        // Every register keeps its line in every view: what does not fit is
        // cut and marked, and the full text goes to the notes at the bottom.
        string Fit(string line)
        {
            if (line.Length <= PanelColumns) return line;
            notes.Add(line);
            return line[..(PanelColumns - Below.Length)] + Below;
        }
        sb.AppendLine(Fit(Title));
        sb.AppendLine($"PC {cpu.Pc:X4}  {(_halted ? "HALTED by the debugger" : cpu.Awake ? "running" : cpu.DisplayEnable ? "light sleep" : "deep sleep")}");
        sb.AppendLine($"cycles since reset {cpu.Cycles:N0}");
        sb.AppendLine(_lastRun < 0 ? "last key: (none yet)"
            : $"last key: {_lastRun:N0} cycles, {_lastRun / RealWordsPerSecond * 1000:N0} ms real");
        sb.AppendLine();
        sb.AppendLine($"A  {Reg(cpu.A)}");
        sb.AppendLine($"B  {Reg(cpu.B)}");
        sb.AppendLine($"C  {Reg(cpu.C)}");
        sb.AppendLine($"M  {Reg(cpu.M)}");
        sb.AppendLine($"N  {Reg(cpu.N)}");
        sb.AppendLine($"G  {cpu.G[1]:X}{cpu.G[0]:X}   P {cpu.P,2}{(cpu.QSel ? " " : "*")}  Q {cpu.Q,2}{(cpu.QSel ? "*" : " ")}");
        sb.AppendLine($"carry {cpu.Carry}  {(cpu.Decimal ? "DEC" : "HEX")} mode  FO {cpu.Fo:X2}");
        var s = new StringBuilder();
        for (int i = 13; i >= 0; i--) s.Append(cpu.S[i] != 0 ? '1' : '0');
        sb.AppendLine($"S  {s}  (13..0)");
        sb.AppendLine($"stack {string.Join(" ", cpu.Stack.Select(a => a.ToString("X4")))}");
        sb.AppendLine($"RAM addr {cpu.RamAddr:X3}  key {cpu.KeyBuf:X2} {cpu.KbState}");
        sb.AppendLine();
        if (Calc is Hp16c h)
        {
            int w = h.WordSize();
            var regs = new[] { "x", "y", "z", "t", "lastx" };
            bool ext = h.HasExtensionRam;
            if (View == PanelView.Bits64)
            {
                sb.AppendLine($"16C stack (raw 64 bits), W={w}");
                foreach (var r in regs) sb.AppendLine($"{r,-5} {h.Reg64(r):X16}");
                sb.AppendLine($"I     {h.RegI():X17}");
            }
            else if (View == PanelView.Bits128 && ext)
            {
                sb.AppendLine($"16C+ stack (raw 128 bits), W={w}");
                if (!h.PlusMode) notes.Add("Plus mode is off (word size 64 or less): the upper bits are zero.");
                foreach (var r in regs) sb.AppendLine($"{r,-5} {h.Reg128(r):X32}");
                sb.AppendLine($"I     {h.RegI128():X32}");
            }
            else
            {
                if (View == PanelView.Bits128)
                    notes.Add("128 bits: this chip layout has no extension RAM, showing the word size.");
                sb.AppendLine($"16C stack (word size), W={w}");
                UInt128 mask = w >= 128 ? UInt128.MaxValue : (UInt128.One << w) - 1;
                string fmt = "X" + (w + 3) / 4;
                foreach (var r in regs)
                    sb.AppendLine($"{r,-5} {((ext ? h.Reg128(r) : h.Reg64(r)) & mask).ToString(fmt)}");
                sb.AppendLine($"I     {(ext ? h.RegI128() : h.RegI()).ToString("X" + Math.Max(17, (w + 3) / 4))}");
            }
            sb.AppendLine();
        }
        sb.AppendLine("recent PCs (newest first)");
        for (int i = 1; i <= 8; i++)
            sb.Append($"{_history[(_historyPos - i + HistorySize) % HistorySize]:X4} ");
        sb.AppendLine();
        if (cpu.UnknownOps.Count > 0)
            notes.Add($"unknown ops: {string.Join(" ", cpu.UnknownOps.Keys.Select(o => o.ToString("X3")))}");
        if (notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("notes");
            foreach (var note in notes)
                foreach (var line in Wrap(note)) sb.AppendLine(line);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------ debugger (IDebugTarget)
    // The debug adapter (Nut.Dap, started with --debug-port) taps into this running calculator:
    // it halts the CPU thread, reads the machine under the lock, steps, resumes. The window keeps
    // working while halted (it shows the frozen display); keys wait in the queue.
    volatile bool _halted;

    public string Model => Calc.Model;
    public bool Halted => _halted;
    public event Action<string, string>? Stopped;

    public void SetBreakpoints(IReadOnlyCollection<int> addresses)
    {
        lock (_gate)
        {
            Calc.Cpu.Breakpoints = addresses.Count == 0 ? null : new HashSet<int>(addresses);
            Calc.Cpu.BreakHit = false;
            Calc.Cpu.SkipBreak = false;
        }
    }

    public void Halt()
    {
        int pc;
        bool asleep;
        lock (_gate)
        {
            _halted = true;
            pc = Calc.Cpu.Pc;
            asleep = !Calc.Cpu.Awake;
        }
        Touch();
        Stopped?.Invoke("pause", $"paused at PC {pc:X4}" + (asleep ? " (the CPU sleeps, waiting for a key)" : ""));
    }

    public void Resume()
    {
        lock (_gate)
        {
            Calc.Cpu.BreakHit = false;
            // run the instruction we stopped at first; a sleeping CPU stopped at no instruction (the next one
            // after a key wake is the first of the wake path, a breakpoint there must stop)
            Calc.Cpu.SkipBreak = Calc.Cpu.Breakpoints is not null && Calc.Cpu.Awake;
            _halted = false;
        }
        Touch();
        _wake.Set();
    }

    public void Step() => StepCore(StepMode.Into);

    public void StepOver() => StepCore(StepMode.Over);

    public void StepOut() => StepCore(StepMode.Out);

    enum StepMode { Into, Over, Out }

    void StepCore(StepMode mode)
    {
        string reason = "step";
        int pc;
        bool asleep;
        lock (_gate)
        {
            var cpu = Calc.Cpu;
            asleep = !cpu.Awake;
            var d = NutDisassembler.Decode(cpu.Rom, cpu.Pc);
            cpu.SkipBreak = cpu.Breakpoints is not null && cpu.Awake;
            if (mode == StepMode.Out)
            {
                // run until the routine we are in returns: count the calls made on the way (taken calls
                // go one level down, taken returns one up; a return at level 0 is the one we wait for)
                int depth = 0;
                long steps = 0;
                while (cpu.Awake && !cpu.BreakHit && steps < 3_000_000)
                {
                    int at = cpu.Pc;
                    var inst = NutDisassembler.Decode(cpu.Rom, at);
                    bool atInstruction = cpu.InstState == NutCpu.Inst.Normal;
                    do { cpu.Cycle(); steps++; }
                    while (cpu.Awake && !cpu.BreakHit && cpu.InstState != NutCpu.Inst.Normal && steps < 3_000_000);
                    if (!atInstruction) continue;
                    int after = cpu.Pc;
                    if (inst.Kind == Flow.CondCall && after == inst.Target) depth++;
                    else if (inst.Kind is Flow.Return or Flow.CondReturn && after != ((at + 1) & 0xFFFF))
                    {
                        if (depth == 0) break;
                        depth--;
                    }
                }
                if (cpu.BreakHit) { cpu.BreakHit = false; reason = "instruction breakpoint"; }
            }
            else if (mode == StepMode.Over && d.Kind == Flow.CondCall && cpu.InstState == NutCpu.Inst.Normal)
            {
                int ret = (cpu.Pc + d.Length) & 0xFFFF;
                long n = 0;
                do { cpu.Cycle(); n++; }
                while (cpu.Awake && !cpu.BreakHit && n < 3_000_000 && !(cpu.Pc == ret && cpu.InstState == NutCpu.Inst.Normal));
                if (cpu.BreakHit) { cpu.BreakHit = false; reason = "instruction breakpoint"; }
            }
            else cpu.Cycle();
            cpu.SkipBreak = false;
            pc = cpu.Pc;
        }
        Touch();
        Stopped?.Invoke(reason, asleep
            ? $"nothing to step: the CPU sleeps at PC {pc:X4}, waiting for a key (press a key in the calculator; a breakpoint at 0000 stops on the key)"
            : reason == "step" ? $"at PC {pc:X4}" : $"breakpoint at {pc:X4}");
    }

    public IReadOnlyList<string> TraceLast(int n)
    {
        if (TraceLog is null) return ["tracing is off: start VoyagerSim with --trace N (last N instructions) or --trace-file FILE"];
        lock (_gate) { TraceLog.Flush(); return TraceLog.Last(n, Calc.Cpu.Rom); }
    }

    public string TraceSave(string path)
    {
        if (TraceLog is null) return "tracing is off: start VoyagerSim with --trace N";
        lock (_gate) { TraceLog.Flush(); return $"{TraceLog.Save(path, Calc.Cpu.Rom)} lines written to {path}"; }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(500);
        _wake.Dispose();
    }
}
