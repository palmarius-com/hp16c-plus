using System.Text;
using System.Text.RegularExpressions;

namespace Ce.Flash;

/// <summary>
/// The SAM-BA monitor's commands, byte for byte: the only commands that can leave this class are the three in
/// <see cref="Allowed"/> (hello <c>N#</c>, read a word <c>w&lt;addr&gt;,#</c>, write a word <c>W&lt;addr&gt;,&lt;value&gt;#</c>).
/// A read answers with exactly four bytes, least significant first; a write answers with nothing we rely on.
/// </summary>
public sealed class SamBaClient(ISamBaTransport transport)
{
    /// <summary>The pattern of every command that may be sent. Anything else is refused before a byte leaves.</summary>
    public static readonly Regex Allowed = new(@"^(N#|w[0-9A-F]{1,8},#|W[0-9A-F]{1,8},[0-9A-F]{8}#)$", RegexOptions.Compiled);

    public int ReplyTimeoutMs { get; init; } = 600;

    /// <summary>Commands sent so far (tests count them).</summary>
    public long CommandsSent { get; private set; }

    /// <summary>"N#": the monitor answers 0x0A 0x0D. Returns the bytes that came back (may be fewer: only informative).</summary>
    public byte[] Hello()
    {
        transport.DiscardInput();
        Send("N#");
        return ReadUpTo(2, ReplyTimeoutMs);
    }

    /// <summary>"w&lt;addr&gt;,#": the word at addr (flash or peripheral).</summary>
    public uint ReadWord(uint address)
    {
        transport.DiscardInput();                         // a late reply of an earlier command must not be taken for this one
        Send($"w{address:X},#");
        var b = ReadUpTo(4, ReplyTimeoutMs);
        if (b.Length != 4) throw new SamBaException($"no 4-byte reply at {address:X8} ({b.Length} bytes): the monitor may need ERASE + RESET");
        return BitConverter.ToUInt32(b, 0);
    }

    /// <summary>"W&lt;addr&gt;,&lt;value&gt;#": write one word. Does not wait for anything: the caller reads back what matters.</summary>
    public void WriteWord(uint address, uint value) => Send($"W{address:X},{value:X8}#");

    void Send(string command)
    {
        if (!Allowed.IsMatch(command)) throw new SamBaException($"refused command: {command}");
        transport.Write(Encoding.ASCII.GetBytes(command));
        CommandsSent++;
    }

    byte[] ReadUpTo(int count, int timeoutMs)
    {
        var got = new byte[count];
        int n = 0;
        var deadline = Environment.TickCount64 + timeoutMs;
        while (n < count)
        {
            long left = deadline - Environment.TickCount64;
            if (left <= 0) break;
            int r = transport.Read(got.AsSpan(n), (int)left);
            if (r == 0) break;                            // the transport waited as long as it was asked to
            n += r;
        }
        return n == count ? got : got[..n];
    }
}
