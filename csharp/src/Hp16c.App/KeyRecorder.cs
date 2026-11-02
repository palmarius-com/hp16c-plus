using System.Collections.Concurrent;
using System.Text;

namespace HpSim;

/// <summary>
/// --record FILE: writes every key the user presses, with the display once the calculator has handled it, as a
/// test line: <c>[LBL]  =>  '001-42,21,11'</c>. A prefix key (f, g) gets no line of its own; the key after it is
/// written under its f or g name, as a test file names it. Off unless the command line asks for it. One worker thread
/// does the writing: the key press itself costs nothing. The display is read when the calculator is idle, so keys
/// typed faster than the calculator answers (Real speed) can show a later display than their own.
/// </summary>
sealed class KeyRecorder : IDisposable
{
    readonly StreamWriter _out;
    readonly BlockingCollection<(MachineRunner Run, Skin Skin, int Code, long Cycles)> _queue = new();
    readonly Thread _worker;
    string _prefix = "";

    public string Path { get; }

    public KeyRecorder(string path)
    {
        Path = path;
        _out = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
        _worker = new Thread(Work) { IsBackground = true, Name = "key recorder" };
        _worker.Start();
    }

    /// <summary>Follow this calculator: its key presses are recorded from now on.</summary>
    public void Attach(MachineRunner run, Skin skin, string name)
    {
        _queue.Add((run, skin, -2, 0));                // marker: a new calculator (the name goes in as a comment)
        lock (_out) _out.WriteLine($"# calculator: {name}");
        run.KeyPressed = code => _queue.Add((run, skin, code, run.Read(v => v.Cpu.Cycles)));
    }

    static string Name(string label) =>
        label.Length == 1 && (char.IsDigit(label[0]) || label[0] == '.') ? label : "[" + label.Replace(' ', '_') + "]";

    void Work()
    {
        foreach (var (run, skin, code, cycles) in _queue.GetConsumingEnumerable())
        {
            if (code < 0) { _prefix = ""; continue; }
            var key = skin.Keys.FirstOrDefault(k => k.Code == code);
            if (key is null) continue;
            if (key.Main is "f" or "g") { _prefix = key.Main; continue; }
            var label = _prefix == "f" && key.F.Length > 0 ? key.F : _prefix == "g" && key.G.Length > 0 ? key.G : key.Main;
            var line = _prefix != "" && label == key.Main ? $"[{_prefix}] {Name(label)}" : Name(label);
            _prefix = "";
            // the key must have run (the cycle count moved) and the calculator be idle again; a key the calculator
            // ignores (it is off) never moves the count, so give up after a moment
            var seen = DateTime.UtcNow.AddSeconds(1.5);
            var limit = DateTime.UtcNow.AddSeconds(30);
            while ((run.Read(v => v.Cpu.Cycles) <= cycles ? DateTime.UtcNow < seen : true) && (!run.Finished || run.Read(v => v.Cpu.Cycles) <= cycles) && DateTime.UtcNow < limit) Thread.Sleep(2);
            var shown = run.Read(v => v.Display()).Trim();
            lock (_out) _out.WriteLine($"{line}  =>  '{shown}'");
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _worker.Join(2000);
        lock (_out) _out.Dispose();
    }
}
