// Read-only tool for a HP-15C Collector's Edition in programming mode. It sends only the monitor's N# and w (read a word)
// commands: nothing is ever written. Every byte that crosses the port goes to a log file (see RecordingTransport), so that
// the fake calculator used in the tests can be made to answer exactly like the real one.
//
//   Ce.Tool ports
//   Ce.Tool probe PORT [--log FILE] [--uid]       chip id, controller status, a few reads, read speed
//   Ce.Tool dump PORT OUT.bin [--log FILE]        the whole flash, read twice, kept only when both reads are equal
using System.Diagnostics;
using Ce.Flash;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("Ce.Tool ports | probe PORT [--log FILE] [--uid] | dump PORT OUT.bin [--log FILE]   (read only)");
    return 0;
}

if (args[0] == "ports")
{
    var ports = SerialSamBaTransport.PortNames();
    Console.WriteLine(ports.Length == 0 ? "no serial ports" : string.Join(Environment.NewLine, ports));
    return 0;
}

if (args[0] == "read")
{
    // Ce.Tool read PORT START LENGTH OUT.bin [--log FILE]: raw words from the flash (0x0-0x1FFFF) or the SRAM (0x20000000-0x20007FFF), read only.
    if (args.Length < 5) { Console.WriteLine("Ce.Tool read PORT START LENGTH OUT.bin [--log FILE]   (hex addresses, flash or SRAM only)"); return 2; }
    uint start = Convert.ToUInt32(args[2].Replace("0x", ""), 16);
    int length = Convert.ToInt32(args[3].Replace("0x", ""), 16);
    bool flashRange = start + (uint)length <= 0x20000, sramRange = start >= 0x20000000 && start + (uint)length <= 0x20008000;
    if (length <= 0 || length % 4 != 0 || start % 4 != 0 || !(flashRange || sramRange)) { Console.WriteLine("refused: word aligned reads inside the flash or the SRAM only"); return 2; }
    string readLog = args.Length > 6 && args[5] == "--log" ? args[6] : $"ce-read-{DateTime.Now:yyyyMMdd-HHmmss}.log";
    using var lf = new StreamWriter(readLog, false, new System.Text.UTF8Encoding(false));
    try
    {
        using var rec = new RecordingTransport(new SerialSamBaTransport(args[1]), lf, $"Ce.Tool read {args[1]} {start:X8}+{length:X}") { DetailLimit = 300 };
        var c = new SamBaClient(rec);
        c.Hello();
        var data = new byte[length];
        for (int k = 0; k < length / 4; k++) BitConverter.GetBytes(c.ReadWord(start + (uint)(4 * k))).CopyTo(data, 4 * k);
        File.WriteAllBytes(args[4], data);
        Console.WriteLine($"saved {args[4]} ({length} bytes from {start:X8}), SHA-256 {CeUnit.Sha256Hex(data).ToUpperInvariant()}");
        return 0;
    }
    catch (IOException e) { Console.WriteLine("connection problem: " + e.Message); return 1; }
    catch (SamBaException e) { Console.WriteLine("monitor: " + e.Message); return 1; }
}

if (args[0] is not ("probe" or "dump") || args.Length < 2)
{
    Console.WriteLine("unknown command; Ce.Tool --help");
    return 2;
}

string cmd = args[0], port = args[1];
string? logPath = null, outPath = null;
bool uid = false;
for (int i = 2; i < args.Length; i++)
{
    if (args[i] == "--log" && i + 1 < args.Length) logPath = args[++i];
    else if (args[i] == "--uid") uid = true;
    else if (cmd == "dump" && outPath is null) outPath = args[i];
    else { Console.WriteLine($"unexpected argument {args[i]}"); return 2; }
}
if (cmd == "dump" && outPath is null) { Console.WriteLine("dump needs an output file"); return 2; }
logPath ??= $"ce-{cmd}-{DateTime.Now:yyyyMMdd-HHmmss}.log";

using var logFile = new StreamWriter(logPath, false, new System.Text.UTF8Encoding(false));
Console.WriteLine($"log: {Path.GetFullPath(logPath)}");
try
{
    using var rec = new RecordingTransport(new SerialSamBaTransport(port), logFile, $"Ce.Tool {cmd} {port}") { DetailLimit = cmd == "dump" ? 600 : null };
    var client = new SamBaClient(rec);
    var flash = new Sam4lFlash(client);

    if (cmd == "probe")
    {
        rec.Note("hello");
        var hello = client.Hello();
        Console.WriteLine($"N#  -> {BitConverter.ToString(hello)}");
        rec.Note("chip id, flash controller status");
        uint id = client.ReadWord(Sam4lFlash.Cidr);
        Console.WriteLine($"CIDR {id:X8} {(id == Sam4lFlash.CidrExpected ? "(the chip the kit knows)" : "(NOT the expected AB0A07E1)")}");
        uint fsr = client.ReadWord(Sam4lFlash.Fsr);
        Console.WriteLine($"FSR  {fsr:X8}  (FRDY {(fsr & 1)}, PROGE {(fsr >> 2) & 1}, LOCKE {(fsr >> 3) & 1})");
        rec.Note("the first 8 words of the boot loader and of the vector table");
        foreach (uint a in new uint[] { 0x0, 0x4, 0x8, 0xC, 0x4000, 0x4004, 0x4008, 0x400C })
            Console.WriteLine($"  {a:X5}: {client.ReadWord(a):X8}");
        rec.Note("read speed: 512 words from 0x8000");
        var clock = Stopwatch.StartNew();
        for (uint k = 0; k < 512; k++) client.ReadWord(0x8000 + 4 * k);
        Console.WriteLine($"512 word reads: {clock.ElapsedMilliseconds} ms ({clock.Elapsed.TotalMilliseconds / 512:F2} ms per word)");
        if (uid)
        {
            rec.Note("unique id of the chip (0x0080020C, 15 bytes)");
            for (uint k = 0; k < 4; k++) Console.WriteLine($"  UID+{4 * k}: {client.ReadWord(0x0080020C + 4 * k):X8}");
        }
        Console.WriteLine("done (nothing was written).");
    }
    else
    {
        var progress = new Progress<JobProgress>(p => { if (p.Step % 32 == 0 || p.Step == p.Steps) Console.Write($"\r{p.Message}        "); });
        var bytes = CeDump.Read(flash, progress);
        Console.WriteLine();
        File.WriteAllBytes(outPath!, bytes);
        Console.WriteLine($"saved {outPath} ({bytes.Length} bytes, two identical reads, SHA-256 {CeUnit.Sha256Hex(bytes).ToUpperInvariant()})");
    }
    return 0;
}
catch (IOException e) { Console.WriteLine("connection problem: " + e.Message); return 1; }
catch (SamBaException e) { Console.WriteLine("monitor: " + e.Message); return 1; }
catch (JobFailedException e) { Console.WriteLine(e.Message); return 1; }
