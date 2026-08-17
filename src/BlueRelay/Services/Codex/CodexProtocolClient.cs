using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlueRelay.Services.Codex;

public sealed record CodexServerRequest(string RequestId, string Method, JsonElement Params);

public sealed record CodexNotification(string Method, JsonElement Params);

public sealed class CodexProtocolClient : IAsyncDisposable
{
    private const int MaxJsonLineLength = 16 * 1024 * 1024;
    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private long _nextRequestId;
    private Task? _readLoop;

    public CodexProtocolClient(TextReader reader, TextWriter writer)
    {
        _reader = reader;
        _writer = writer;
    }

    public event EventHandler<CodexNotification>? NotificationReceived;

    public event EventHandler<CodexServerRequest>? ServerRequestReceived;

    public event EventHandler<Exception>? Disconnected;

    public void Start()
    {
        _readLoop ??= Task.Run(ReadLoopAsync);
    }

    public async Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextRequestId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var pending = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = pending;

        try
        {
            await WriteMessageAsync(CreateRequest(id, method, parameters), cancellationToken).ConfigureAwait(false);
            return await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    public Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        return WriteMessageAsync(CreateNotification(method, parameters), cancellationToken);
    }

    public Task RespondAsync(string requestId, object? result, CancellationToken cancellationToken = default)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(requestId) ?? JsonValue.Create(requestId),
            ["result"] = JsonSerializer.SerializeToNode(result)
        };
        return WriteMessageAsync(message, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new OperationCanceledException("The Codex App Server connection closed."));
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (TimeoutException)
            {
                // The owning process/stream will be closed by the bridge immediately after this.
            }
        }

        _writeGate.Dispose();
        _stop.Dispose();
    }

    private static JsonObject CreateRequest(string id, string method, object? parameters)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonValue.Create(long.Parse(id, System.Globalization.CultureInfo.InvariantCulture)),
            ["method"] = method
        };
        if (parameters is not null)
        {
            message["params"] = JsonSerializer.SerializeToNode(parameters);
        }

        return message;
    }

    private static JsonObject CreateNotification(string method, object? parameters)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method
        };
        if (parameters is not null)
        {
            message["params"] = JsonSerializer.SerializeToNode(parameters);
        }

        return message;
    }

    private async Task WriteMessageAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var json = message.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        if (json.Length > MaxJsonLineLength)
        {
            throw new InvalidOperationException("The Codex App Server JSON message is too large.");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(json).ConfigureAwait(false);
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_stop.IsCancellationRequested && await _reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.Length > MaxJsonLineLength)
                {
                    throw new InvalidDataException("The Codex App Server sent an oversized JSON message.");
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var id))
                {
                    var idKey = id.GetRawText();
                    if (_pending.TryRemove(idKey, out var pending))
                    {
                        if (root.TryGetProperty("error", out var error))
                        {
                            var message = error.TryGetProperty("message", out var errorMessage)
                                ? errorMessage.GetString()
                                : "The Codex App Server request failed.";
                            pending.TrySetException(new CodexProtocolException(message ?? "The Codex App Server request failed.", error));
                        }
                        else if (root.TryGetProperty("result", out var result))
                        {
                            pending.TrySetResult(result.Clone());
                        }
                    }
                    else if (root.TryGetProperty("method", out var serverMethod))
                    {
                        var parameters = root.TryGetProperty("params", out var requestParams)
                            ? requestParams.Clone()
                            : default;
                        ServerRequestReceived?.Invoke(this, new CodexServerRequest(idKey, serverMethod.GetString() ?? string.Empty, parameters));
                    }

                    continue;
                }

                if (root.TryGetProperty("method", out var method))
                {
                    var parameters = root.TryGetProperty("params", out var notificationParams)
                        ? notificationParams.Clone()
                        : default;
                    NotificationReceived?.Invoke(this, new CodexNotification(method.GetString() ?? string.Empty, parameters));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or ObjectDisposedException)
        {
            failure = exception;
        }
        finally
        {
            if (!_stop.IsCancellationRequested)
            {
                var exception = failure ?? new EndOfStreamException("The Codex App Server closed its protocol stream.");
                foreach (var pending in _pending.Values)
                {
                    pending.TrySetException(exception);
                }

                _pending.Clear();
                Disconnected?.Invoke(this, exception);
            }
        }
    }
}

public sealed class CodexProtocolException : Exception
{
    public CodexProtocolException(string message, JsonElement error)
        : base(message)
    {
        Error = error.Clone();
    }

    public JsonElement Error { get; }
}
