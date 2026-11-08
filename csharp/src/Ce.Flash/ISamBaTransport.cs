namespace Ce.Flash;

/// <summary>
/// A byte pipe to the SAM-BA monitor of the calculator (a serial port, or <see cref="FakeSam4l"/> in tests). It knows no
/// commands: framing is <see cref="SamBaClient"/>'s job.
/// </summary>
public interface ISamBaTransport : IDisposable
{
    /// <summary>Send the bytes. Throws <see cref="IOException"/> when the link is gone (cable pulled, port closed).</summary>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>
    /// Read up to buffer.Length bytes, waiting at most timeoutMs for the first one. Returns the number read, 0 on timeout.
    /// Throws <see cref="IOException"/> when the link is gone.
    /// </summary>
    int Read(Span<byte> buffer, int timeoutMs);

    /// <summary>Throw away what has arrived and was not asked for.</summary>
    void DiscardInput();
}

/// <summary>The monitor did not answer as the protocol says, or a safety rule refused a command. Nothing is retried silently.</summary>
public sealed class SamBaException(string message) : Exception(message);
