using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Nut.Core;

namespace Nut.Dap;

/// <summary>
/// Debug Adapter Protocol over TCP (127.0.0.1) for the running calculator: remote debugging. The
/// editor attaches to the live machine (<see cref="IDebugTarget"/>); attaching changes nothing, the
/// calculator keeps running until the editor pauses it or a breakpoint is hit.
/// Framing: "Content-Length: N", blank line, N bytes of UTF-8 JSON. Requests come from the editor,
/// each gets one response; the adapter also sends events (stopped, output).
/// What the editor sees: the "program" is the ROM (disassembly from the ROM words, no source), a
/// "frame" is the PC and the 4 return-stack entries, "variables" are the CPU registers, flags and the
/// RAM registers. Threads: requests are handled on the connection thread; the stopped event for a
/// breakpoint comes from the CPU thread (a lock serialises the writes).
/// </summary>
public sealed class DapServer(int port)
{
    // Breakpoints come from two places: gutter clicks in the ROM listing (setBreakpoints, by line) and
    // the Disassembly view (setInstructionBreakpoints, by address). The target gets the union.
    readonly HashSet<int> _sourceBreakpoints = [];
    readonly HashSet<int> _instructionBreakpoints = [];
    const int ListingReference = 1;
    readonly object _writeLock = new();
    IDebugTarget? _target;
    Stream? _stream;
    int _seq;

    public int Port => port;

    /// <summary>Where the server says what it does: connections, every request and its answer, events.
    /// The app points it at its console (the terminal it was started from) and the status line.</summary>
    public Action<string>? Log { get; set; }

    void Say(string text) => Log?.Invoke(text);

    // ---------------------------------------------------------------- target
    /// <summary>The calculator being debugged changed (the user chose another one) or went away.</summary>
    public void Follow(IDebugTarget? target)
    {
        var old = _target;
        if (ReferenceEquals(old, target)) return;
        if (old is not null) old.Stopped -= OnStopped;
        _target = target;
        if (target is null) return;
        target.Stopped += OnStopped;
        if (_stream is not null)
        {
            ApplyBreakpoints();
            Output($"now debugging {target.Model} (a different calculator was started)");
        }
    }

    void ApplyBreakpoints() => _target?.SetBreakpoints(_sourceBreakpoints.Union(_instructionBreakpoints).ToList());

    IDebugTarget Target => _target ?? throw new InvalidOperationException("no calculator is running");

    void OnStopped(string reason, string description) => Stopped(reason, description);

    // ---------------------------------------------------------------- connection
    public void Start() => new Thread(Serve) { IsBackground = true, Name = "DAP" }.Start();

    void Serve()
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        while (true)
        {
            using var client = listener.AcceptTcpClient();
            Say($"client connected from {client.Client.RemoteEndPoint}");
            try { Session(client.GetStream()); }
            catch (IOException e) { Say($"connection error: {e.Message}"); }
            Detach();
            Say("client gone: breakpoints removed, calculator runs free");
        }
    }

    void Session(Stream stream)
    {
        _stream = new BufferedStream(stream, 8192);
        while (ReadMessage(stream) is { } msg)
        {
            if (msg["type"]?.GetValue<string>() != "request") continue;
            Say($"-> {msg["command"]} {Short(msg["arguments"])}");
            try
            {
                if (!Handle(msg)) return;
            }
            catch (Exception e) when (e is not IOException)
            {
                Respond(msg, null, false, e.Message);
            }
        }
    }

    /// <summary>The editor is gone: take the breakpoints out and let the calculator run free.</summary>
    void Detach()
    {
        _stream = null;
        _sourceBreakpoints.Clear();
        _instructionBreakpoints.Clear();
        if (_target is { } t)
        {
            t.SetBreakpoints([]);
            if (t.Halted) t.Resume();
        }
    }

    static JsonObject? ReadMessage(Stream s)
    {
        int length = -1;
        while (true)
        {
            var line = ReadLine(s);
            if (line is null) return null;
            if (line.Length == 0) break;
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line[15..].Trim());
        }
        if (length < 0) return null;
        var body = new byte[length];
        for (int got = 0; got < length;)
        {
            int n = s.Read(body, got, length - got);
            if (n <= 0) return null;
            got += n;
        }
        return JsonNode.Parse(body) as JsonObject;
    }

    static string? ReadLine(Stream s)
    {
        var sb = new StringBuilder();
        while (true)
        {
            int b = s.ReadByte();
            if (b < 0) return null;
            if (b == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append((char)b);
        }
    }

    void Send(JsonObject msg)
    {
        lock (_writeLock)
        {
            if (_stream is null) return;
            msg["seq"] = ++_seq;
            var body = Encoding.UTF8.GetBytes(msg.ToJsonString());
            var head = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            _stream.Write(head);
            _stream.Write(body);
            _stream.Flush();
        }
    }

    void Respond(JsonObject req, JsonNode? body, bool success = true, string? message = null)
    {
        var r = new JsonObject
        {
            ["type"] = "response",
            ["request_seq"] = req["seq"]!.GetValue<int>(),
            ["success"] = success,
            ["command"] = req["command"]!.GetValue<string>(),
        };
        if (message is not null) r["message"] = message;
        if (body is not null) r["body"] = body;
        Say($"   <- {req["command"]} {(success ? "ok" : "FAILED: " + message)}");
        Send(r);
    }

    static string Short(JsonNode? node)
    {
        var text = node?.ToJsonString() ?? "";
        return text.Length > 160 ? text[..160] + "..." : text;
    }

    void Event(string name, JsonNode? body = null)
    {
        var e = new JsonObject { ["type"] = "event", ["event"] = name };
        if (body is not null) e["body"] = body;
        Say($"   <= event {name} {Short(body)}");
        Send(e);
    }

    void Output(string text) =>
        Event("output", new JsonObject { ["category"] = "console", ["output"] = text + "\n" });

    void Stopped(string reason, string description) =>
        Event("stopped", new JsonObject
        {
            ["reason"] = reason,
            ["description"] = description,
            ["threadId"] = 1,
            ["allThreadsStopped"] = true,
        });

    // ---------------------------------------------------------------- requests
    /// <summary>Returns false when the session ends.</summary>
    bool Handle(JsonObject req)
    {
        var args = req["arguments"] as JsonObject;
        switch (req["command"]!.GetValue<string>())
        {
            case "initialize":
                Respond(req, new JsonObject
                {
                    ["supportsConfigurationDoneRequest"] = true,
                    ["supportsDisassembleRequest"] = true,
                    ["supportsReadMemoryRequest"] = true,
                    ["supportsInstructionBreakpoints"] = true,
                    ["supportsSteppingGranularity"] = true,
                    ["supportsEvaluateForHovers"] = false,
                });
                Event("initialized");
                Output(_target is null
                    ? "Nut CPU debugger: no calculator is running yet."
                    : $"Nut CPU debugger attached to the running {Target.Model}. Pause to look at it; 'help' lists the console commands.");
                break;

            case "launch":
            case "attach":
                Respond(req, null);
                break;

            case "setBreakpoints":
            {
                // the ROM listing (virtual source): line n is address n - 1
                _sourceBreakpoints.Clear();
                var result = new JsonArray();
                foreach (var bp in args?["breakpoints"]?.AsArray() ?? [])
                {
                    int line = (int?)bp!["line"] ?? 0;
                    bool ok = line is >= 1 and <= 0x10000;
                    if (ok) _sourceBreakpoints.Add(line - 1);
                    result.Add(new JsonObject { ["verified"] = ok, ["line"] = line });
                }
                ApplyBreakpoints();
                Respond(req, new JsonObject { ["breakpoints"] = result });
                break;
            }

            case "setFunctionBreakpoints":
                Respond(req, new JsonObject { ["breakpoints"] = new JsonArray() });
                break;

            case "source":
                Respond(req, new JsonObject { ["content"] = Listing(), ["mimeType"] = "text/plain" });
                break;

            case "setExceptionBreakpoints":
                Respond(req, new JsonObject());
                break;

            case "setInstructionBreakpoints":
            {
                _instructionBreakpoints.Clear();
                var result = new JsonArray();
                foreach (var bp in args?["breakpoints"]?.AsArray() ?? [])
                {
                    int addr = (ParseAddress(bp!["instructionReference"]!.GetValue<string>()) +
                                ((int?)bp["offset"] ?? 0)) & 0xFFFF;
                    _instructionBreakpoints.Add(addr);
                    result.Add(new JsonObject { ["verified"] = true, ["instructionReference"] = Ref(addr) });
                }
                ApplyBreakpoints();
                Respond(req, new JsonObject { ["breakpoints"] = result });
                break;
            }

            case "configurationDone":
                Respond(req, null);
                // attaching does not stop anything; a calculator that was halted before says so
                if (_target is { Halted: true }) Stopped("pause", "halted by the debugger");
                break;

            case "threads":
                Respond(req, new JsonObject
                {
                    ["threads"] = new JsonArray(new JsonObject { ["id"] = 1, ["name"] = "Nut CPU" }),
                });
                break;

            case "stackTrace":
                Respond(req, StackTrace());
                break;

            case "scopes":
                Respond(req, new JsonObject
                {
                    ["scopes"] = new JsonArray(Scope("Registers", 1), Scope("State", 2), Scope("RAM", 3)),
                });
                break;

            case "variables":
                Respond(req, new JsonObject { ["variables"] = Variables((int?)args?["variablesReference"] ?? 0) });
                break;

            case "disassemble":
                Respond(req, Disassemble(args!));
                break;

            case "readMemory":
                Respond(req, ReadMemory(args!));
                break;

            case "continue":
                Respond(req, new JsonObject { ["allThreadsContinued"] = true });
                Target.Resume();
                break;

            case "next":
                Respond(req, null);
                Target.StepOver();
                break;

            case "stepIn":
                Respond(req, null);
                Target.Step();
                break;

            case "stepOut":
                Respond(req, null);
                Target.StepOut();
                break;

            case "pause":
                Respond(req, null);
                Target.Halt();
                break;

            case "evaluate":
                Respond(req, Evaluate((string?)args?["expression"] ?? ""));
                break;

            case "disconnect":
            case "terminate":
                Respond(req, null);
                return false;

            default:
                Respond(req, null, false, $"not supported: {req["command"]}");
                break;
        }
        return true;
    }

    JsonObject StackTrace()
    {
        var (pc, stack) = Target.Read(v => (v.Cpu.Pc, (int[])v.Cpu.Stack.Clone()));
        var frames = new JsonArray
        {
            new JsonObject
            {
                ["id"] = 1, ["name"] = $"PC {pc:X4}", ["source"] = ListingSource(), ["line"] = pc + 1, ["column"] = 1,
                ["instructionPointerReference"] = Ref(pc),
            },
        };
        for (int i = 0; i < stack.Length; i++)
            frames.Add(new JsonObject
            {
                ["id"] = i + 2, ["name"] = $"return stack [{i}] = {stack[i]:X4}", ["source"] = ListingSource(),
                ["line"] = (stack[i] & 0xFFFF) + 1, ["column"] = 1,
                ["instructionPointerReference"] = Ref(stack[i]),
            });
        return new JsonObject { ["stackFrames"] = frames, ["totalFrames"] = frames.Count };
    }

    /// <summary>
    /// The ROM as a virtual source file: one line per address, line = address + 1, so the editor shows the
    /// current instruction and takes breakpoints in the gutter. VS Code only inspects a frame (scopes,
    /// variables) when it has a source, or after an instruction-level step; "pause" needs the source.
    /// </summary>
    JsonObject ListingSource() => new()
    {
        ["name"] = $"{Target.Model.ToUpperInvariant()} ROM.nutasm",
        ["sourceReference"] = ListingReference,
        ["presentationHint"] = "normal",
    };

    /// <summary>One line per address (65536 lines): address, the word, the mnemonic. A second word of a
    /// two-word instruction gets its own line marked (operand). The listing is built from the ROM as it is
    /// now; a patch applied later is not reflected until the editor asks again.</summary>
    string Listing()
    {
        var (rom, exists) = Target.Read(v => ((int[])v.Cpu.Rom.Clone(), (bool[])v.Cpu.RomExists.Clone()));
        var sb = new StringBuilder(0x10000 * 36);
        int a = 0;
        while (a < 0x10000)
        {
            if (!exists[a]) { sb.Append($"{a:X4}  ---\n"); a++; continue; }
            var d = NutDisassembler.Decode(rom, a);
            sb.Append($"{a:X4}  {rom[a]:X3}      {d.Text}\n");
            for (int k = 1; k < d.Length && a + k < 0x10000; k++)
                sb.Append($"{a + k:X4}  {rom[a + k]:X3}      (operand of {a:X4})\n");
            a += d.Length;
        }
        return sb.ToString();
    }

    static JsonObject Scope(string name, int reference) =>
        new() { ["name"] = name, ["variablesReference"] = reference, ["expensive"] = false };

    static JsonObject Var(string name, string value) =>
        new() { ["name"] = name, ["value"] = value, ["variablesReference"] = 0 };

    // Memory map for the editor's hex views (readMemory): a flat byte space, explicit and documented.
    //   0x00000000 - 0x00001FFF  RAM: register r at r * 8; bytes 0..6 are its 14 hex digits, most
    //                            significant first (so the dump reads like the register text); byte 7 is
    //                            0x00 for a register that exists, 0xFF for one that does not.
    //   0x00010000 - 0x0002FFFF  ROM: word w at 0x10000 + w * 2, big endian, 10 significant bits;
    //                            0xFFFF where the chip has no ROM.
    const int RamBase = 0, RegisterStride = 8, RomBase = 0x10000;

    static JsonObject VarMem(string name, string value, int address)
    {
        var v = Var(name, value);
        v["memoryReference"] = $"0x{address:X8}";
        return v;
    }

    static string Hex(byte[] digits) => string.Concat(digits.Reverse().Select(d => d.ToString("X")));

    JsonArray Variables(int reference) => Target.Read(v =>
    {
        var cpu = v.Cpu;
        var list = new JsonArray();
        switch (reference)
        {
            case 1:
                list.Add(Var("A", Hex(cpu.A)));
                list.Add(Var("B", Hex(cpu.B)));
                list.Add(Var("C", Hex(cpu.C)));
                list.Add(Var("M", Hex(cpu.M)));
                list.Add(Var("N", Hex(cpu.N)));
                list.Add(Var("G", $"{cpu.G[1]:X}{cpu.G[0]:X}"));
                list.Add(Var("P", $"{cpu.P}{(cpu.QSel ? "" : " (selected)")}"));
                list.Add(Var("Q", $"{cpu.Q}{(cpu.QSel ? " (selected)" : "")}"));
                list.Add(Var("FO", $"{cpu.Fo:X2}"));
                break;
            case 2:
                list.Add(Var("PC", $"{cpu.Pc:X4}"));
                list.Add(Var("carry", cpu.Carry.ToString()));
                list.Add(Var("mode", cpu.Decimal ? "DEC" : "HEX"));
                var s = new StringBuilder();
                for (int i = 13; i >= 0; i--) s.Append(cpu.S[i] != 0 ? '1' : '0');
                list.Add(Var("S (13..0)", s.ToString()));
                list.Add(Var("awake", cpu.Awake.ToString()));
                list.Add(Var("keyboard", cpu.KbState.ToString()));
                list.Add(Var("RAM address", $"{cpu.RamAddr:X3}"));
                list.Add(Var("cycles", cpu.Cycles.ToString("N0")));
                list.Add(Var("display", v.Display().Trim()));
                list.Add(VarMem("RAM (8 bytes per register)", "0x00000000", RamBase));
                list.Add(VarMem("ROM (2 bytes per word)", $"0x{RomBase:X8}", RomBase));
                break;
            case 3:
                for (int a = 0; a < NutCpu.MaxRam; a++)
                    if (cpu.RamExists[a]) list.Add(VarMem($"{a:X3}", Hex(cpu.Ram[a]), RamBase + a * RegisterStride));
                break;
        }
        return list;
    });

    JsonObject Disassemble(JsonObject args)
    {
        int addr = (ParseAddress(args["memoryReference"]!.GetValue<string>()) + ((int?)args["offset"] ?? 0)) & 0xFFFF;
        int offset = (int?)args["instructionOffset"] ?? 0;
        int count = (int?)args["instructionCount"] ?? 20;
        // the ROM does not change while we look: copy the words and the existence map once
        var (rom, exists) = Target.Read(v => ((int[])v.Cpu.Rom.Clone(), (bool[])v.Cpu.RomExists.Clone()));
        var starts = new List<int>();
        int a = addr;
        int missing = 0;       // instructions asked for before the first address that do not exist
        if (offset < 0)
        {
            var before = NutDisassembler.Before(rom, addr, -offset);
            missing = -offset - before.Count;
            starts.AddRange(before);
        }
        else
            for (int i = 0; i < offset; i++) a = (a + NutDisassembler.Decode(rom, a).Length) & 0xFFFF;
        while (starts.Count + missing < count)
        {
            starts.Add(a);
            a = (a + NutDisassembler.Decode(rom, a).Length) & 0xFFFF;
        }
        var list = new JsonArray();
        // The editor puts the current instruction at index -instructionOffset of the answer. Near the
        // start of the ROM there are fewer instructions before it than asked for, so fill the front with
        // invalid entries (addresses below 0, as 32-bit two's complement), or the marker lands elsewhere.
        int firstReal = starts.Count > 0 ? starts[0] : addr;
        for (int k = 0; k < missing; k++)
            list.Add(new JsonObject
            {
                ["address"] = $"0x{(uint)(firstReal - (missing - k)):X8}",
                ["instruction"] = "(before the start of the ROM)",
                ["presentationHint"] = "invalid",
            });
        foreach (int at in starts.Take(count - missing))
        {
            if (!exists[at])
            {
                list.Add(new JsonObject { ["address"] = Ref(at), ["instruction"] = "(no ROM here)" });
                continue;
            }
            var d = NutDisassembler.Decode(rom, at);
            var words = string.Join(" ", Enumerable.Range(0, d.Length).Select(i => $"{rom[(at + i) & 0xFFFF]:X3}"));
            list.Add(new JsonObject
            {
                ["address"] = Ref(at),
                ["instructionBytes"] = words,
                ["instruction"] = d.Text,
            });
        }
        return new JsonObject { ["instructions"] = list };
    }

    JsonObject ReadMemory(JsonObject args)
    {
        long start = ParseAddress(args["memoryReference"]!.GetValue<string>()) + ((int?)args["offset"] ?? 0);
        int count = Math.Clamp((int?)args["count"] ?? 0, 0, 1 << 16);
        var data = new byte[count];
        Target.Read(v =>
        {
            var cpu = v.Cpu;
            for (int i = 0; i < count; i++)
            {
                long p = start + i;
                if (p >= RomBase)
                {
                    long w = (p - RomBase) / 2;
                    if (w > 0xFFFF || !cpu.RomExists[w]) { data[i] = 0xFF; continue; }
                    int word = cpu.Rom[w];
                    data[i] = (p & 1) == 0 ? (byte)((word >> 8) & 0x3) : (byte)(word & 0xFF);
                }
                else
                {
                    long reg = (p - RamBase) / RegisterStride;
                    int b = (int)((p - RamBase) % RegisterStride);
                    if (reg >= NutCpu.MaxRam || !cpu.RamExists[reg]) { data[i] = 0xFF; continue; }
                    if (b == 7) { data[i] = 0; continue; }
                    var digits = cpu.Ram[reg];
                    data[i] = (byte)((digits[13 - 2 * b] << 4) | digits[12 - 2 * b]);
                }
            }
            return 0;
        });
        return new JsonObject
        {
            ["address"] = $"0x{start:X8}",
            ["data"] = Convert.ToBase64String(data),
            ["unreadableBytes"] = 0,
        };
    }

    JsonObject Evaluate(string expression)
    {
        var words = expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string result;
        switch (words.FirstOrDefault()?.ToLowerInvariant())
        {
            case "key" or "keys":
                var names = Target.Read(BuildKeyNames);
                var unknown = new List<string>();
                int tapped = 0;
                foreach (var w in words.Skip(1))
                {
                    if (names.TryGetValue(w.Trim('[', ']').ToUpperInvariant(), out int code)) { Target.Tap(code); tapped++; }
                    else unknown.Add(w);
                }
                result = $"{tapped} key(s) tapped" + (unknown.Count > 0 ? $"; unknown: {string.Join(" ", unknown)}" : "");
                break;
            case "display":
                result = $"'{Target.Read(v => v.Display()).Trim()}'";
                break;
            case "trace":
                if (words.Length >= 3 && words[1].Equals("save", StringComparison.OrdinalIgnoreCase))
                    result = Target.TraceSave(string.Join(' ', words.Skip(2)));
                else
                {
                    int n = words.Length > 1 && int.TryParse(words[1], out int parsed) ? parsed : 20;
                    result = string.Join('\n', Target.TraceLast(n));
                }
                break;
            case "reset":
                Target.PowerOn();
                result = "reset (batteries out and in)";
                break;
            default:
                result = "commands: keys NAME... (tap keys as the window does, e.g. keys 2 ENTER 3 +; names are the " +
                         "printed legends), display, reset, trace [N] (the last N instructions, needs --trace), " +
                         "trace save FILE. The calculator window works as usual.";
                break;
        }
        return new JsonObject { ["result"] = result, ["variablesReference"] = 0 };
    }

    // ---------------------------------------------------------------- helpers
    static string Ref(int addr) => $"0x{addr & 0xFFFF:X4}";

    static int ParseAddress(string s) =>
        s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(s[2..], 16) : int.Parse(s);

    /// <summary>Printed legend (first one on the key) to HP key code; the first key wins.</summary>
    static Dictionary<string, int> BuildKeyNames(Voyager v)
    {
        var names = new Dictionary<string, int>();
        foreach (var (code, legends) in v.Legends.OrderBy(kv => kv.Key))
            if (legends.Length > 0 && !string.IsNullOrEmpty(legends[0]))
                names.TryAdd(legends[0].ToUpperInvariant(), code);
        names.TryAdd("ON", 41);
        return names;
    }
}
