using System.Runtime.InteropServices;

namespace HpSim;

/// <summary>
/// VoyagerSim is a window application: it has no console of its own, so Console.WriteLine goes
/// nowhere. When it is started with --debug-port from a terminal, this attaches to that terminal
/// (Windows: AttachConsole to the parent process) so the debugger's log shows up there. When it was
/// not started from a console (double click) nothing is attached and the log only goes to the status
/// line.
/// </summary>
static class DebugConsole
{
    const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool AttachConsole(int processId);

    public static bool Attach()
    {
        if (OperatingSystem.IsWindows() && !AttachConsole(AttachParentProcess)) return false;
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
        return true;
    }
}
