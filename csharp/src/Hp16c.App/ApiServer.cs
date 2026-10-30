using System.IO.Pipes;
using System.Text;
using Avalonia.Threading;

namespace HpSim;

/// <summary>
/// Optional command channel for tests and scripts: a local named pipe, one
/// command per line, one answer line back ("ok ..." or "error ..."). Only
/// runs when the app is started with --api. Commands execute on the window's
/// thread, through the same code as the mouse and keyboard.
/// </summary>
sealed class ApiServer(string pipeName, Func<string, Task<string>> handle)
{
    public const string DefaultPipe = "voyager-sim";

    public void Start() => new Thread(Loop) { IsBackground = true, Name = "API" }.Start();

    void Loop()
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.None);
                pipe.WaitForConnection();
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
                while (reader.ReadLine() is { } line)
                {
                    var answer = Dispatcher.UIThread.InvokeAsync(() => handle(line)).GetAwaiter().GetResult();
                    writer.WriteLine(answer.Replace('\r', ' ').Replace('\n', '|'));
                }
            }
            catch (IOException) { /* client gone: wait for the next one */ }
        }
    }
}
