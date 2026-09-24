using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace aBookPlayer;

/// <summary>
/// Keeps one player per user session: a second launch (e.g. "Open with" from Explorer) hands its file
/// to the running instance over a named pipe and exits, instead of playing a second book at the same
/// time and overwriting the first one's saved position.
/// </summary>
sealed class SingleInstance : IDisposable
{
    // Pipe names are machine-wide: include user and session so different logons never talk to each other
    static readonly string Id =
        $"{AppSettings.AppName}-{Environment.UserName}-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
    static readonly string PipeName = Id;

    readonly Mutex _mutex;
    readonly CancellationTokenSource _cts = new();

    SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>Returns the instance guard when this is the first instance, otherwise null.</summary>
    public static SingleInstance? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, @"Local\" + Id, out bool createdNew);
        if (createdNew) return new SingleInstance(mutex);
        mutex.Dispose();
        return null;
    }

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Sends a file path (or an empty line, meaning "just show yourself") to the running instance.</summary>
    public static bool SendToRunningInstance(string? path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            AllowSetForegroundWindow(-1); // ASFW_ANY: this process was started by the user, let the other window come to the front
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.WriteLine(path == null ? "" : Path.GetFullPath(path));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Starts listening for other launches; <paramref name="onMessage"/> runs on a background thread.</summary>
    public void Listen(Action<string> onMessage) => _ = ListenLoopAsync(onMessage, _cts.Token);

    async Task ListenLoopAsync(Action<string> onMessage, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(ct);
                onMessage(line ?? "");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // A broken connection must not stop the listener
                await Task.Delay(200, CancellationToken.None);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
