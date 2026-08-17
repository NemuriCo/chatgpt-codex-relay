using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BlueRelay.Services.Codex;

namespace BlueRelay.Tests;

[TestClass]
public sealed class CodexProtocolClientTests
{
    [TestMethod]
    public async Task RequestUsesCanonicalJsonLinesWithoutJsonRpcEnvelope()
    {
        using var input = new ChannelTextReader();
        using var output = new CallbackTextWriter(line =>
        {
            using var request = JsonDocument.Parse(line);
            var id = request.RootElement.GetProperty("id").GetRawText();
            input.Enqueue("{\"id\":" + id + ",\"result\":{\"ok\":true}}");
        });
        await using var client = new CodexProtocolClient(input, output);
        client.Start();

        var result = await client.RequestAsync("initialize", new
        {
            clientInfo = new { name = "bluerelay", version = "test" }
        });

        Assert.IsTrue(result.GetProperty("ok").GetBoolean());
        Assert.IsNotNull(output.LastRequest);
        Assert.IsFalse(output.LastRequest!.Value.TryGetProperty("jsonrpc", out _));
        Assert.AreEqual("initialize", output.LastRequest.Value.GetProperty("method").GetString());
        Assert.AreEqual("bluerelay", output.LastRequest.Value.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task NotificationUsesCanonicalJsonLinesWithoutJsonRpcEnvelope()
    {
        using var input = new ChannelTextReader();
        using var output = new CallbackTextWriter(_ => { });
        await using var client = new CodexProtocolClient(input, output);

        await client.NotifyAsync("initialized", null);

        using var message = JsonDocument.Parse(output.Lines.Single());
        Assert.IsFalse(message.RootElement.TryGetProperty("jsonrpc", out _));
        Assert.AreEqual("initialized", message.RootElement.GetProperty("method").GetString());
    }

    [TestMethod]
    public async Task ServerRequestsAreRaisedAndCanonicalResponsesCanBeAnswered()
    {
        using var input = new ChannelTextReader();
        using var output = new CallbackTextWriter(_ => { });
        await using var client = new CodexProtocolClient(input, output);
        var request = new TaskCompletionSource<CodexServerRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ServerRequestReceived += (_, value) => request.TrySetResult(value);
        client.Start();

        input.Enqueue("{\"id\":42,\"method\":\"item/fileChange/requestApproval\",\"params\":{\"itemId\":\"item-1\"}}");
        var received = await request.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("42", received.RequestId);
        Assert.AreEqual("item/fileChange/requestApproval", received.Method);

        await client.RespondAsync(received.RequestId, new { decision = "accept" });
        using var response = JsonDocument.Parse(output.Lines.Single());
        Assert.IsFalse(response.RootElement.TryGetProperty("jsonrpc", out _));
        Assert.AreEqual(42, response.RootElement.GetProperty("id").GetInt32());
        Assert.AreEqual("accept", response.RootElement.GetProperty("result").GetProperty("decision").GetString());
    }

    [TestMethod]
    public async Task MalformedJsonDoesNotKillTheReadLoop()
    {
        using var input = new ChannelTextReader();
        using var output = new CallbackTextWriter(line =>
        {
            using var request = JsonDocument.Parse(line);
            input.Enqueue($"{{\"id\":{request.RootElement.GetProperty("id").GetRawText()},\"result\":{{\"ok\":true}}}}");
        });
        await using var client = new CodexProtocolClient(input, output);
        var diagnostics = new List<string>();
        client.Diagnostic += (_, message) => diagnostics.Add(message);
        client.Start();
        input.Enqueue("{not-json");

        var result = await client.RequestAsync("thread/list", null);

        Assert.IsTrue(result.GetProperty("ok").GetBoolean());
        Assert.IsTrue(diagnostics.Any(message => message.StartsWith("malformed_json", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task NotificationHandlerExceptionDoesNotKillTheReadLoop()
    {
        using var input = new ChannelTextReader();
        using var output = new CallbackTextWriter(line =>
        {
            using var request = JsonDocument.Parse(line);
            input.Enqueue($"{{\"id\":{request.RootElement.GetProperty("id").GetRawText()},\"result\":{{\"ok\":true}}}}");
        });
        await using var client = new CodexProtocolClient(input, output);
        var diagnostics = new List<string>();
        client.Diagnostic += (_, message) => diagnostics.Add(message);
        client.NotificationReceived += (_, _) => throw new InvalidOperationException("consumer failure");
        client.Start();
        input.Enqueue("{\"method\":\"item/started\",\"params\":{}}");

        var result = await client.RequestAsync("thread/list", null);

        Assert.IsTrue(result.GetProperty("ok").GetBoolean());
        Assert.IsTrue(diagnostics.Any(message => message.StartsWith("notification_handler_error", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ServerRequestHandlerExceptionDoesNotKillTheReadLoop()
    {
        using var input = new ChannelTextReader();
        using var output = new CallbackTextWriter(line =>
        {
            using var request = JsonDocument.Parse(line);
            input.Enqueue($"{{\"id\":{request.RootElement.GetProperty("id").GetRawText()},\"result\":{{\"ok\":true}}}}");
        });
        await using var client = new CodexProtocolClient(input, output);
        var diagnostics = new List<string>();
        client.Diagnostic += (_, message) => diagnostics.Add(message);
        client.ServerRequestReceived += (_, _) => throw new InvalidOperationException("consumer failure");
        client.Start();
        input.Enqueue("{\"id\":43,\"method\":\"item/fileChange/requestApproval\",\"params\":{}}");

        var result = await client.RequestAsync("thread/list", null);

        Assert.IsTrue(result.GetProperty("ok").GetBoolean());
        Assert.IsTrue(diagnostics.Any(message => message.StartsWith("server_request_handler_error", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task RequestErrorIsNotReportedAsTransportDisconnect()
    {
        using var input = new ChannelTextReader();
        using var output = new CallbackTextWriter(line =>
        {
            using var request = JsonDocument.Parse(line);
            input.Enqueue($"{{\"id\":{request.RootElement.GetProperty("id").GetRawText()},\"error\":{{\"code\":-32602,\"message\":\"invalid field\"}}}}");
        });
        await using var client = new CodexProtocolClient(input, output);
        var disconnects = 0;
        client.Disconnected += (_, _) => disconnects++;
        client.Start();

        var exception = await Assert.ThrowsExceptionAsync<CodexProtocolException>(
            () => client.RequestAsync("thread/start", new { invalid = true }));

        Assert.AreEqual("invalid field", exception.Message);
        Assert.AreEqual(0, disconnects);
    }

    [TestMethod]
    public async Task EofRaisesOneDisconnectEvent()
    {
        using var input = new ChannelTextReader();
        using var output = new CallbackTextWriter(_ => { });
        await using var client = new CodexProtocolClient(input, output);
        var disconnected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        client.Disconnected += (_, _) =>
        {
            count++;
            disconnected.TrySetResult(true);
        };
        client.Start();

        input.Complete();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
        input.Complete();
        await Task.Delay(50);

        Assert.AreEqual(1, count);
    }

    private sealed class ChannelTextReader : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        public void Enqueue(string line) => _lines.Writer.TryWrite(line);

        public void Complete() => _lines.Writer.TryComplete();

        public override async Task<string?> ReadLineAsync()
        {
            return await _lines.Reader.WaitToReadAsync()
                ? await _lines.Reader.ReadAsync()
                : null;
        }

        protected override void Dispose(bool disposing)
        {
            _lines.Writer.TryComplete();
            base.Dispose(disposing);
        }
    }

    private sealed class CallbackTextWriter : TextWriter
    {
        private readonly Action<string> _callback;

        public CallbackTextWriter(Action<string> callback)
        {
            _callback = callback;
        }

        public List<string> Lines { get; } = [];

        public JsonElement? LastRequest { get; private set; }

        public override Encoding Encoding => System.Text.Encoding.UTF8;

        public override Task WriteLineAsync(string? value)
        {
            if (value is not null)
            {
                Lines.Add(value);
                using var document = JsonDocument.Parse(value);
                LastRequest = document.RootElement.Clone();
                _callback(value);
            }

            return Task.CompletedTask;
        }
    }
}
