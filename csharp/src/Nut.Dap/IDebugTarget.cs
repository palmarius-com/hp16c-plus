using Nut.Core;

namespace Nut.Dap;

/// <summary>
/// What the debug adapter needs from a running calculator. The app's MachineRunner implements it, so
/// the debugger taps into the calculator that is really running (remote debugging), it does not run a
/// copy. Halt/Resume/Step stop and start the CPU thread; the machine's registers and RAM may only
/// be read through <see cref="Read{T}"/> (it holds the CPU lock) and only make sense while halted.
/// </summary>
public interface IDebugTarget
{
    string Model { get; }

    /// <summary>The CPU thread is stopped by the debugger.</summary>
    bool Halted { get; }

    /// <summary>Raised on the thread that stops the CPU: reason is "pause", "step" or
    /// "instruction breakpoint", the text says where.</summary>
    event Action<string, string>? Stopped;

    /// <summary>Read the machine under the CPU lock.</summary>
    T Read<T>(Func<Voyager, T> read);

    /// <summary>Replace the set of instruction breakpoints (addresses).</summary>
    void SetBreakpoints(IReadOnlyCollection<int> addresses);

    /// <summary>Stop the CPU where it is.</summary>
    void Halt();

    /// <summary>Let the CPU run again; the instruction it stopped at is executed first.</summary>
    void Resume();

    /// <summary>Execute one instruction word, then stay halted.</summary>
    void Step();

    /// <summary>Like Step, but a subroutine call runs to its return.</summary>
    void StepOver();

    /// <summary>Run until the current subroutine returns (calls it makes on the way are run through),
    /// then stay halted at the instruction after the call.</summary>
    void StepOut();

    /// <summary>Tap keys as the window does (press, release), by HP key code.</summary>
    void Tap(int userKeycode);

    /// <summary>Batteries out and in.</summary>
    void PowerOn();

    /// <summary>The last N executed instructions as CSV lines (empty with a note when --trace is off).</summary>
    IReadOnlyList<string> TraceLast(int n);

    /// <summary>Write the trace ring buffer to a CSV file; returns a sentence about what happened.</summary>
    string TraceSave(string path);
}
