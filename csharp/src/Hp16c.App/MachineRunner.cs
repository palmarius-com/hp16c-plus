using System.Diagnostics;
using System.Text;
using Nut.Core;

namespace HpSim;

/// <summary>
/// Runs a calculator on a background thread. Keys go in as press/release
/// (so holding a key works as on the real one, e.g. STATUS); the UI reads
/// snapshots. Real speed: the Voyager's 215 kHz LC oscillator, 56 clocks
/// per instruction word.
/// </summary>
sealed class MachineRunner : IDisposable
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

    public MachineRunner(Voyager calc, string title)
    {
        Calc = calc;
        Title = title;
        Calc.Cpu.Trace = (_, pc, _) =>
        {
            _history[_historyPos] = pc;
            _historyPos = (_historyPos + 1) % HistorySize;
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
                if (Calc.Keys.TryGetValue(ev, out var hw) && cpu.Press(hw)) _pressedAt = cpu.Cycles;
                else if (_keys.TryPeek(out var next) && next < 0) _keys.TryDequeue(out _);
            }
            else
            {
                if (_pressedAt >= 0 && cpu.Awake && cpu.Cycles - _pressedAt < MinHoldCycles) return;
                _keys.TryDequeue(out _);
                cpu.Release();
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
                ApplyKeys();
                awake = Calc.Cpu.Awake;
            }
            if (!awake)
            {
                Thread.Sleep(2);
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
            lock (_gate) Calc.Cpu.Run(budget);
        }
    }

    public void Press(int userKeycode) => _keys.Enqueue(userKeycode);

    public void Release() => _keys.Enqueue(-1);

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

    public void PowerOn()
    {
        _keys.Clear();
        lock (_gate) { Calc.PowerOn(); _pressedAt = -1; }
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
    }

    public sealed record Snapshot(Dictionary<char, bool>[] Segments, bool DisplayOn, string Status);

    public Snapshot Take()
    {
        lock (_gate)
        {
            var cpu = Calc.Cpu;
            return new Snapshot(Calc.SegmentState(), Calc.DisplayEnable, StatusText(cpu));
        }
    }

    static string Reg(byte[] r) => string.Concat(r.Reverse().Select(d => d.ToString("X")));

    string StatusText(NutCpu cpu)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Title}");
        sb.AppendLine($"PC {cpu.Pc:X4}  {(cpu.Awake ? "running" : cpu.DisplayEnable ? "light sleep" : "deep sleep")}");
        sb.AppendLine($"cycles {cpu.Cycles:N0}");
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
            sb.AppendLine("16C stack (64 bits)");
            foreach (var r in new[] { "x", "y", "z", "t", "lastx" })
                sb.AppendLine($"{r,-5} {h.Reg64(r):X16}");
            sb.AppendLine($"I     {h.RegI():X17}");
            sb.AppendLine();
        }
        sb.AppendLine("recent PCs (newest first)");
        for (int i = 1; i <= 8; i++)
            sb.Append($"{_history[(_historyPos - i + HistorySize) % HistorySize]:X4} ");
        sb.AppendLine();
        if (cpu.UnknownOps.Count > 0)
            sb.AppendLine($"unknown ops: {string.Join(" ", cpu.UnknownOps.Keys.Select(o => o.ToString("X3")))}");
        return sb.ToString();
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(500);
    }
}
