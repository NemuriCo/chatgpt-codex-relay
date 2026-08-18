using BlueRelay.Diagnostics;

namespace BlueRelay.Services.Desktop;

public sealed class CodexComposerOperationCoordinator
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<Func<CodexComposerInjectionResult>, Task<CodexComposerInjectionResult>> _worker;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CodexComposerOperationCoordinator(
        TimeSpan? timeout = null,
        Func<Func<CodexComposerInjectionResult>, Task<CodexComposerInjectionResult>>? worker = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        _worker = worker ?? (operation => StaAutomationWorker.RunAsync(operation));
    }

    public async Task<CodexComposerInjectionResult> RunAsync(
        Func<CodexComposerInjectionResult> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        var gateAcquired = await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        if (!gateAcquired)
        {
            return CodexComposerInjectionResult.Failed(
                "codex_composer_busy",
                "Another Codex composer operation is already running.");
        }

        Task<CodexComposerInjectionResult>? workerTask = null;
        try
        {
            workerTask = _worker(operation);
            var timeoutTask = Task.Delay(_timeout);
            var cancellationTask = cancellationToken.CanBeCanceled
                ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                : NeverCompletingTask.Instance;
            var completedTask = await Task.WhenAny(workerTask, timeoutTask, cancellationTask).ConfigureAwait(false);

            if (completedTask == workerTask)
            {
                try
                {
                    return await workerTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    StartupDiagnostics.WriteException("Codex composer worker", exception);
                    return CodexComposerInjectionResult.Failed(
                        "codex_composer_injection_failed",
                        "Codex composer injection failed.");
                }
                finally
                {
                    _gate.Release();
                }
            }

            if (completedTask == cancellationTask)
            {
                _ = ReleaseWhenWorkerCompletesAsync(workerTask);
                return CodexComposerInjectionResult.Failed(
                    "codex_composer_cancelled",
                    "Codex composer operation was cancelled.");
            }

            StartupDiagnostics.Write($"Codex composer operation timed out elapsedMs={(long)_timeout.TotalMilliseconds}");
            _ = ReleaseWhenWorkerCompletesAsync(workerTask);
            return CodexComposerInjectionResult.Failed(
                "codex_composer_probe_timeout",
                "Codex composer probe timed out.");
        }
        catch
        {
            if (gateAcquired && workerTask is null)
            {
                _gate.Release();
            }

            throw;
        }
    }

    private async Task ReleaseWhenWorkerCompletesAsync(Task<CodexComposerInjectionResult> workerTask)
    {
        try
        {
            await workerTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            StartupDiagnostics.WriteException("Codex composer abandoned worker", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static class NeverCompletingTask
    {
        public static readonly Task Instance = Task.Delay(Timeout.InfiniteTimeSpan);
    }
}

public static class StaAutomationWorker
{
    public static Task<T> RunAsync<T>(Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.TrySetResult(operation());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "BlueRelay Codex UI Automation"
        };

        try
        {
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        return completion.Task;
    }
}
