using System.Text.Json;
using System.Text;
using System.Threading.Channels;
using BlueRelay.Services.Codex;

namespace BlueRelay.Tests;

[TestClass]
public sealed class CodexProtocolClientTests
{
    [TestMethod]
    public async Task RequestUsesJsonLinesAndMatchesTheResponse()
    {
        using var input = new ChannelTextReader();
        using var output = new CallbackTextWriter(line =>
        {
            using var request = JsonDocument.Parse(line);
            var id = request.RootElement.GetProperty("id").GetRawText();
            input.Enqueue("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"ok\":true}}");
        });
        await using var client = new CodexProtocolClient(input, output);
        client.Start();

        var result = await client.RequestAsync("initialize", new
        {
            clientInfo = new { name = "bluerelay", version = "test" }
        });

        Assert.IsTrue(result.GetProperty("ok").GetBoolean());
        Assert.AreEqual("initialize", output.LastRequest!.Value.GetProperty("method").GetString());
        Assert.AreEqual("bluerelay", output.LastRequest!.Value.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task ServerRequestsAreRaisedAndCanBeAnswered()
    {
        using var input = new ChannelTextReader();
        using var output = new CallbackTextWriter(_ => { });
        await using var client = new CodexProtocolClient(input, output);
        var request = new TaskCompletionSource<CodexServerRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ServerRequestReceived += (_, value) => request.TrySetResult(value);
        client.Start();

        input.Enqueue("""{"jsonrpc":"2.0","id":42,"method":"item/fileChange/requestApproval","params":{"itemId":"item-1"}}""");
        var received = await request.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("42", received.RequestId);
        Assert.AreEqual("item/fileChange/requestApproval", received.Method);

        await client.RespondAsync(received.RequestId, new { decision = "accept" });
        Assert.IsTrue(output.Lines.Any(line => line.Contains("\"id\":42", StringComparison.Ordinal)));
        Assert.IsTrue(output.Lines.Any(line => line.Contains("\"decision\":\"accept\"", StringComparison.Ordinal)));
    }

    private sealed class ChannelTextReader : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

        public void Enqueue(string line) => _lines.Writer.TryWrite(line);

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
