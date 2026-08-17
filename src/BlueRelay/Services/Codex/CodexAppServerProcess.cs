using System.Diagnostics;

namespace BlueRelay.Services.Codex;

public sealed class CodexAppServerProcess : IAsyncDisposable
{
    private readonly string _executablePath;
    private Process? _process;

    public CodexAppServerProcess(string executablePath)
    {
        _executablePath = executablePath;
    }

    public TextReader? Output => _process?.StandardOutput;

    public TextWriter? Input => _process?.StandardInput;

    public bool HasExited => _process is null || _process.HasExited;

    public event EventHandler? Exited;

    public event EventHandler<string>? DiagnosticOutput;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_process is not null)
        {
            return;
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _executablePath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory
            },
            EnableRaisingEvents = true
        };
        process.StartInfo.ArgumentList.Add("app-server");
        process.StartInfo.ArgumentList.Add("--listen");
        process.StartInfo.ArgumentList.Add("stdio://");
        process.Exited += Process_Exited;

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The Codex App Server process could not be started.");
        }

        _process = process;
        _ = PumpDiagnosticsAsync(process.StandardError);
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        _process = null;
        try
        {
            process.Exited -= Process_Exited;
            try
            {
                await process.StandardInput.WriteAsync("\n").ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The server may already have closed stdin.
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task PumpDiagnosticsAsync(TextReader reader)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    DiagnosticOutput?.Invoke(this, line.Length > 512 ? line[..512] : line);
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void Process_Exited(object? sender, EventArgs e)
    {
        Exited?.Invoke(this, e);
    }
}
