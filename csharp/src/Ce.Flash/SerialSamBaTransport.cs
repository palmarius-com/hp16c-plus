using System.Diagnostics;
using System.IO.Ports;
using System.Text;

namespace Ce.Flash;

/// <summary>
/// The calculator's "Bossa Program Port" as <see cref="ISamBaTransport"/>: a serial port at 115200 8N1 (the baud rate means
/// nothing on a USB CDC port; the settings are those of the PowerShell scripts that worked). Any failure of the port is an
/// <see cref="IOException"/>: the link is gone.
/// </summary>
public sealed class SerialSamBaTransport : ISamBaTransport
{
    readonly SerialPort _port;

    public SerialSamBaTransport(string portName)
    {
        _port = new SerialPort(portName, 115200, Parity.None, 8, StopBits.One) { ReadTimeout = 100, WriteTimeout = 1000 };
        try { _port.Open(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            throw new IOException($"cannot open {portName}: {e.Message}", e);
        }
    }

    public static string[] PortNames() => SerialPort.GetPortNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();

    public void Write(ReadOnlySpan<byte> data)
    {
        try { _port.Write(data.ToArray(), 0, data.Length); }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new IOException("serial port write failed: " + e.Message, e);
        }
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        try
        {
            _port.ReadTimeout = Math.Max(1, timeoutMs);
            var tmp = new byte[buffer.Length];
            int n = _port.Read(tmp, 0, tmp.Length);          // waits for at least one byte
            tmp.AsSpan(0, n).CopyTo(buffer);
            return n;
        }
        catch (TimeoutException) { return 0; }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new IOException("serial port read failed: " + e.Message, e);
        }
    }

    public void DiscardInput()
    {
        try { _port.DiscardInBuffer(); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { throw new IOException("serial port failed: " + e.Message, e); }
    }

    public void Dispose() => _port.Dispose();
}

/// <summary>
/// Wraps any transport and writes everything that crosses it to a text log, one line per event, flushed at once (a killed
/// process keeps what happened): time since the start in ms, direction, byte count, hex, and the ASCII where it is text.
/// The log is what a <see cref="FakeSam4l"/> is made to answer like: what the monitor really sends, how fast, and when it is silent.
/// </summary>
public sealed class RecordingTransport : ISamBaTransport
{
    readonly ISamBaTransport _inner;
    readonly TextWriter _log;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    long _events, _tx, _rx, _txBytes, _rxBytes, _silences;
    double _latencySum, _latencyMax;

    /// <summary>After this many events only the unusual ones are written (silences, errors); the statistics still count everything.</summary>
    public int? DetailLimit { get; init; }

    public RecordingTransport(ISamBaTransport inner, TextWriter log, string title)
    {
        _inner = inner;
        _log = log;
        Line("--", $"{title} ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        _tx++; _txBytes += data.Length;
        Line("TX", Describe(data));
        try { _inner.Write(data); }
        catch (IOException e) { Line("IO", "write failed: " + e.Message, always: true); throw; }
    }

    public int Read(Span<byte> buffer, int timeoutMs)
    {
        var before = _clock.Elapsed.TotalMilliseconds;
        int n;
        try { n = _inner.Read(buffer, timeoutMs); }
        catch (IOException e) { Line("IO", "read failed: " + e.Message, always: true); throw; }
        double waited = _clock.Elapsed.TotalMilliseconds - before;
        if (n == 0) { _silences++; Line("RX", $"nothing (waited {waited:F1} ms, allowed {timeoutMs} ms)", always: true); }
        else
        {
            _rx++; _rxBytes += n; _latencySum += waited; _latencyMax = Math.Max(_latencyMax, waited);
            Line("RX", $"{Describe(buffer[..n])}  (after {waited:F1} ms)");
        }
        return n;
    }

    public void DiscardInput()
    {
        Line("--", "discard input");
        _inner.DiscardInput();
    }

    public void Note(string text) => Line("##", text);

    public void Dispose()
    {
        Line("--", $"summary: {_tx} sends ({_txBytes} bytes), {_rx} receives ({_rxBytes} bytes), {_silences} silences, " +
                   $"reply latency average {(_rx == 0 ? 0 : _latencySum / _rx):F2} ms, maximum {_latencyMax:F1} ms, {_events} events in all", always: true);
        Line("--", "closed", always: true);
        _log.Flush();
        _inner.Dispose();
    }

    void Line(string kind, string text, bool always = false)
    {
        lock (_log)
        {
            _events++;
            if (!always && DetailLimit is { } limit && _events > limit)
            {
                if (_events == limit + 1) _log.WriteLine($"{_clock.Elapsed.TotalMilliseconds,10:F1} -- (more events follow; only silences, errors and the summary are written from here)");
                return;
            }
            _log.WriteLine($"{_clock.Elapsed.TotalMilliseconds,10:F1} {kind,-2} {text}");
            _log.Flush();
        }
    }

    static string Describe(ReadOnlySpan<byte> data)
    {
        var hex = new StringBuilder();
        var text = new StringBuilder();
        foreach (var b in data)
        {
            hex.Append(b.ToString("X2")).Append(' ');
            text.Append(b is >= 0x20 and < 0x7F ? (char)b : b == 0x0A ? '␊' : b == 0x0D ? '␍' : '.');
        }
        return $"{data.Length}B  {hex.ToString().TrimEnd(),-48} \"{text}\"";
    }
}
