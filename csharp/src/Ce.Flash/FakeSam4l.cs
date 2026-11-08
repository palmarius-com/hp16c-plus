using System.Text;

namespace Ce.Flash;

/// <summary>
/// A SAM4L behind the SAM-BA monitor, in memory: the byte protocol (<c>N#</c>, <c>w&lt;addr&gt;,#</c>,
/// <c>W&lt;addr&gt;,&lt;value&gt;#</c>), 128 KB of flash that erases to 0xFF and programs 1 to 0 only, the page buffer, the
/// flash controller (FCMD with key 0xA5: WP, EP, CPB; FSR with FRDY, PROGE, LOCKE that clear when read), the chip id and
/// the region locks. For tests and for trying the programmer screen without a calculator.
/// What it models is what the scripts rely on, read off them and off the SAM4L data sheet; whether the real monitor
/// behaves the same (for example: does <c>W</c> answer?) is what the first run on the calculator has to show.
/// </summary>
public sealed class FakeSam4l : ISamBaTransport
{
    public byte[] Flash { get; } = new byte[Sam4lFlash.FlashSize];

    /// <summary>Bit n set: the 8 KB region n is locked (erase and write give LOCKE). The CE has region 0 (boot loader) locked.</summary>
    public uint LockedRegions { get; set; } = 1;

    /// <summary>Does <c>W</c> send an answer (0x0A 0x0D)? Unknown for the real monitor; the client must not depend on it.</summary>
    public bool WriteReplies { get; set; }

    /// <summary>
    /// Like the real monitor (seen in the log of the first run, 2026-11-08): the 4 bytes of a word read arrive as 1 byte and then
    /// 3 bytes, so a first Read returns only the first byte. On by default; the client must collect the pieces.
    /// </summary>
    public bool FragmentReadReplies { get; set; } = true;

    /// <summary>The monitor sends only half of a word for a read: the client must notice.</summary>
    public bool TruncateReadReplies { get; set; }

    /// <summary>The next write page (WP) reports PROGE and programs nothing.</summary>
    public bool FailNextProgram { get; set; }

    /// <summary>The link is gone after this many commands: later calls throw IOException, the flash stays as it was.</summary>
    public long? CutAfterCommands { get; set; }

    public long CommandsHandled { get; private set; }

    /// <summary>Words written to the device (page buffer words and FCMD): zero after a read-only job.</summary>
    public long WordWrites { get; private set; }
    public bool Connected { get; private set; } = true;

    readonly StringBuilder _in = new();
    readonly Queue<byte> _out = new();
    bool _firstByteOnly;                                    // the next Read gets only the first byte of a word reply
    readonly uint[] _buffer = new uint[Sam4lFlash.PageSize / 4];
    readonly bool[] _bufferSet = new bool[Sam4lFlash.PageSize / 4];
    uint _fsrErrors;

    public FakeSam4l() => Array.Fill(Flash, (byte)0xFF);

    /// <summary>Power-up again (a new session on the same flash): link back, buffers and flags clear.</summary>
    public void Reconnect()
    {
        Connected = true;
        CutAfterCommands = null;
        _in.Clear(); _out.Clear(); _fsrErrors = 0;
        Array.Clear(_bufferSet);
        CommandsHandled = 0;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (!Connected) throw new IOException("device gone");
        _in.Append(Encoding.ASCII.GetString(data));
        int end;
        while ((end = _in.ToString().IndexOf('#')) >= 0)
        {
            string cmd = _in.ToString(0, end + 1);
            _in.Remove(0, end + 1);
            if (CutAfterCommands is { } cut && CommandsHandled >= cut) { Connected = false; throw new IOException("device gone"); }
            CommandsHandled++;
            Handle(cmd);
        }
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        if (!Connected) throw new IOException("device gone");
        int n = 0;
        int limit = _firstByteOnly ? 1 : buffer.Length;
        while (n < limit && _out.Count > 0) buffer[n++] = _out.Dequeue();
        if (n > 0) _firstByteOnly = false;
        return n;                                           // an in-memory device answers at once: nothing to wait for
    }

    public void DiscardInput() { _out.Clear(); _firstByteOnly = false; }

    public void Dispose() { }

    void Handle(string cmd)
    {
        if (cmd == "N#") { _out.Enqueue(0x0A); _out.Enqueue(0x0D); return; }
        try
        {
            if (cmd[0] == 'w')
            {
                uint addr = Convert.ToUInt32(cmd[1..cmd.IndexOf(',')], 16);
                var b = BitConverter.GetBytes(ReadMemory(addr));
                int len = TruncateReadReplies ? 2 : 4;
                for (int i = 0; i < len; i++) _out.Enqueue(b[i]);
                _firstByteOnly = FragmentReadReplies && len == 4;
            }
            else if (cmd[0] == 'W')
            {
                int comma = cmd.IndexOf(',');
                WriteMemory(Convert.ToUInt32(cmd[1..comma], 16), Convert.ToUInt32(cmd[(comma + 1)..^1], 16));
                if (WriteReplies) { _out.Enqueue(0x0A); _out.Enqueue(0x0D); }
            }
        }
        catch (FormatException) { /* a monitor ignores what it cannot read */ }
    }

    uint ReadMemory(uint addr)
    {
        if (addr < Flash.Length) return BitConverter.ToUInt32(Flash, (int)addr);
        if (addr == Sam4lFlash.Fsr) { uint v = 1 | _fsrErrors; _fsrErrors = 0; return v; }   // FRDY; reading clears the error bits
        if (addr == Sam4lFlash.Cidr) return Sam4lFlash.CidrExpected;
        return 0;
    }

    void WriteMemory(uint addr, uint value)
    {
        WordWrites++;
        if (addr == Sam4lFlash.Fcmd) { Execute(value); return; }
        if (addr < Flash.Length)                            // a write into flash space goes to the page buffer
        {
            int i = (int)(addr % Sam4lFlash.PageSize) / 4;
            _buffer[i] = value; _bufferSet[i] = true;
        }
    }

    void Execute(uint fcmd)
    {
        if ((fcmd >> 24) != 0xA5) return;
        int cmd = (int)(fcmd & 0x3F), page = (int)((fcmd >> 8) & 0xFFFF);
        switch (cmd)
        {
            case 3:                                         // CPB: clear page buffer
                Array.Clear(_bufferSet);
                break;
            case 2:                                         // EP: erase page
                if (!PageUsable(page)) break;
                Array.Fill(Flash, (byte)0xFF, page * Sam4lFlash.PageSize, Sam4lFlash.PageSize);
                break;
            case 1:                                         // WP: write page (the words that were put in the buffer)
                if (!PageUsable(page)) break;
                if (FailNextProgram) { FailNextProgram = false; _fsrErrors |= 4; break; }
                for (int k = 0; k < _buffer.Length; k++)
                {
                    if (!_bufferSet[k]) continue;
                    int at = page * Sam4lFlash.PageSize + 4 * k;
                    uint now = BitConverter.ToUInt32(Flash, at);
                    BitConverter.GetBytes(now & _buffer[k]).CopyTo(Flash, at);   // flash programs 1 to 0 only
                }
                break;
        }
    }

    bool PageUsable(int page)
    {
        if (page < 0 || page >= Sam4lFlash.Pages) { _fsrErrors |= 4; return false; }
        if ((LockedRegions >> (page / 16) & 1) != 0) { _fsrErrors |= 8; return false; }   // LOCKE
        return true;
    }
}
