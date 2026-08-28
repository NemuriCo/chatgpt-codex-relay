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
        return await RunAsync(_ => operation(), cancellationToken).ConfigureAwait(false);
    }

    public async Task<CodexComposerInjectionResult> RunAsync(
        Func<CancellationToken, CodexComposerInjectionResult> operation,
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
        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var operationStarted = false;
        var released = false;

        void ReleaseGate()
        {
            if (released)
            {
                return;
            }

            released = true;
            operationCancellation.Dispose();
            _gate.Release();
        }

        try
        {
            operationStarted = true;
            workerTask = _worker(() => operation(operationCancellation.Token));
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
                    ReleaseGate();
                }
            }

            if (completedTask == cancellationTask)
            {
                operationCancellation.Cancel();
                _ = ReleaseWhenWorkerCompletesAsync(workerTask, operationCancellation);
                return CodexComposerInjectionResult.Failed(
                    "codex_composer_cancelled",
                    "Codex composer operation was cancelled.");
            }

            StartupDiagnostics.Write($"Codex composer operation timed out elapsedMs={(long)_timeout.TotalMilliseconds}");
            operationCancellation.Cancel();
            _ = ReleaseWhenWorkerCompletesAsync(workerTask, operationCancellation);
            return CodexComposerInjectionResult.Failed(
                "codex_composer_probe_timeout",
                "Codex operation timed out. Please retry.");
        }
        catch
        {
            if (gateAcquired && workerTask is null)
            {
                if (operationStarted)
                {
                    operationCancellation.Dispose();
                }

                _gate.Release();
            }

            throw;
        }
    }

    private async Task ReleaseWhenWorkerCompletesAsync(
        Task<CodexComposerInjectionResult> workerTask,
        CancellationTokenSource operationCancellation)
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
            operationCancellation.Dispose();
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
        return RunAsync(
            operation,
            ThreadPriority.Normal,
            "BlueRelay Codex UI Automation");
    }

    public static Task<T> RunAsync<T>(
        Func<T> operation,
        ThreadPriority priority,
        string threadName)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadName);

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
            Name = threadName,
            Priority = priority
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
