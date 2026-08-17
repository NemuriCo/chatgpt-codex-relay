using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using BlueRelay.Models;
using BlueRelay.Services.Codex;

namespace BlueRelay.Tests;

[TestClass]
public sealed class CodexAppServerBridgePairingTests
{
    [TestMethod]
    public async Task RepeatedTurnsInOneProcessAttachThreadOnlyOnce()
    {
        var fixture = CreateFixture();
        var factory = new FakeProcessFactory();
        var bridge = new CodexAppServerBridge(fixture.State, new FakeExecutableLocator(), _ => factory.Create());

        for (var index = 0; index < 3; index++)
        {
            var result = await bridge.SubmitTaskAsync(fixture.Request($"prompt-{index}"));
            Assert.IsTrue(result.Success, result.Error);
        }

        var process = factory.Processes.Single();
        Assert.AreEqual(1, process.Methods.Count(method => method == "initialize"));
        Assert.AreEqual(1, process.Methods.Count(method => method == "thread/start"));
        Assert.AreEqual(0, process.Methods.Count(method => method == "thread/resume"));
        Assert.AreEqual(3, process.Methods.Count(method => method == "turn/start"));
        Assert.IsFalse(string.IsNullOrWhiteSpace(fixture.Workstream.CodexThreadId));
    }

    [TestMethod]
    public async Task RestartResumesPersistedThreadOnceThenReusesIt()
    {
        var fixture = CreateFixture();
        var factory = new FakeProcessFactory();
        var bridge = new CodexAppServerBridge(fixture.State, new FakeExecutableLocator(), _ => factory.Create());

        Assert.IsTrue((await bridge.SubmitTaskAsync(fixture.Request("first"))).Success);
        factory.Processes[0].TriggerExit();
        await WaitUntilAsync(() => bridge.Status == CodexBridgeStatus.Error);

        Assert.IsTrue((await bridge.SubmitTaskAsync(fixture.Request("after-restart"))).Success);
        Assert.IsTrue((await bridge.SubmitTaskAsync(fixture.Request("same-generation"))).Success);

        Assert.AreEqual(1, factory.Processes[1].Methods.Count(method => method == "thread/resume"));
        Assert.AreEqual(0, factory.Processes[1].Methods.Count(method => method == "thread/start"));
        Assert.AreEqual(2, factory.Processes[1].Methods.Count(method => method == "turn/start"));
    }

    [TestMethod]
    public async Task ActiveWriterConflictKeepsThreadAndReturnsActionableCode()
    {
        var fixture = CreateFixture();
        fixture.Workstream.CodexThreadId = "thread-existing";
        fixture.Workstream.CodexSessionId = "thread-existing";
        var factory = new FakeProcessFactory { ResumeConflict = true };
        var bridge = new CodexAppServerBridge(fixture.State, new FakeExecutableLocator(), _ => factory.Create());

        var result = await bridge.SubmitTaskAsync(fixture.Request("conflict"));

        Assert.IsFalse(result.Success);
        Assert.AreEqual("codex_thread_conflict", result.ErrorCode);
        Assert.AreEqual("thread-existing", result.ThreadId);
        Assert.AreEqual("thread-existing", fixture.Workstream.CodexThreadId);
        Assert.AreEqual(1, factory.Processes.Single().Methods.Count(method => method == "thread/resume"));
        StringAssert.Contains(result.Error, "another process");
    }

    [TestMethod]
    public async Task ArchivedThreadReturnsActionableCodeAndKeepsPersistedThread()
    {
        var fixture = CreateFixture();
        fixture.Workstream.CodexThreadId = "thread-archived";
        fixture.Workstream.CodexSessionId = "thread-archived";
        var factory = new FakeProcessFactory { ResumeError = "thread is archived" };
        var bridge = new CodexAppServerBridge(fixture.State, new FakeExecutableLocator(), _ => factory.Create());

        var result = await bridge.SubmitTaskAsync(fixture.Request("archived"));

        Assert.IsFalse(result.Success);
        Assert.AreEqual("codex_thread_archived", result.ErrorCode);
        Assert.AreEqual("thread-archived", result.ThreadId);
        Assert.AreEqual("thread-archived", fixture.Workstream.CodexThreadId);
        Assert.AreEqual(1, factory.Processes.Single().Methods.Count(method => method == "thread/resume"));
        Assert.AreEqual(0, factory.Processes.Single().Methods.Count(method => method == "turn/start"));
    }

    [TestMethod]
    public async Task NewThreadUsesGitRootAndDoesNotSendCustomThreadSource()
    {
        var fixture = CreateFixture();
        fixture.Project.LocalPath = Directory.GetCurrentDirectory();
        var factory = new FakeProcessFactory();
        var bridge = new CodexAppServerBridge(fixture.State, new FakeExecutableLocator(), _ => factory.Create());

        var result = await bridge.SubmitTaskAsync(fixture.Request("cwd"));

        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(result.ThreadId, fixture.Workstream.CodexThreadId);
        var process = factory.Processes.Single();
        Assert.AreEqual(FindGitRoot(Directory.GetCurrentDirectory()), process.ThreadStartCwd);
        Assert.IsFalse(process.ThreadStartParams?.TryGetProperty("threadSource", out _) == true);
    }

    private static Fixture CreateFixture()
    {
        var state = new ApplicationState();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "Test project",
            LocalPath = Directory.GetCurrentDirectory()
        };
        var workstream = new Workstream { Id = Guid.NewGuid(), Name = "Test workstream", ProjectId = project.Id };
        project.Workstreams.Add(workstream);
        state.Projects.Add(project);
        return new Fixture(state, project, workstream);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var index = 0; index < 40; index++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        Assert.IsTrue(condition(), "Condition did not become true in time.");
    }

    private static string FindGitRoot(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        Assert.Fail($"Could not find the Git root for {path}.");
        return string.Empty;
    }

    private sealed record Fixture(ApplicationState State, Project Project, Workstream Workstream)
    {
        public CodexTaskRequest Request(string prompt)
        {
            var task = new RelayTask { Id = Guid.NewGuid(), WorkstreamId = Workstream.Id, Prompt = prompt };
            return new CodexTaskRequest(Project, Workstream, task, prompt);
        }
    }

    private sealed class FakeExecutableLocator : ICodexExecutableLocator
    {
        public Task<CodexExecutableInfo> LocateAsync(string? configuredPath = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new CodexExecutableInfo("fake-codex.exe", string.Empty, "codex fake", "app-server --help"));
        }
    }

    private sealed class FakeProcessFactory
    {
        public bool ResumeConflict { get; init; }

        public string? ResumeError { get; init; }

        public List<FakeProcess> Processes { get; } = [];

        public FakeProcess Create()
        {
            var process = new FakeProcess(Processes.Count + 1, ResumeConflict, ResumeError);
            Processes.Add(process);
            return process;
        }
    }

    private sealed class FakeProcess : ICodexAppServerProcess
    {
        private readonly Channel<string> _outputLines = Channel.CreateUnbounded<string>();
        private readonly FakeInput _input;
        private readonly bool _resumeConflict;
        private readonly string? _resumeError;
        private int _threadNumber;
        private bool _stopped;

        public FakeProcess(int processId, bool resumeConflict, string? resumeError)
        {
            ProcessId = processId;
            _resumeConflict = resumeConflict;
            _resumeError = resumeError;
            _input = new FakeInput(HandleRequest);
        }

        public TextReader Output => new ChannelReader(_outputLines);

        public TextWriter Input => _input;

        public int? ProcessId { get; }

        public int? ExitCode { get; private set; }

        public List<string> Methods { get; } = [];

        public JsonElement? ThreadStartParams { get; private set; }

        public string? ThreadStartCwd => ThreadStartParams is { } parameters &&
                                         parameters.TryGetProperty("cwd", out var cwd)
            ? cwd.GetString()
            : null;

        public event EventHandler<CodexProcessExit>? Exited;

        public event EventHandler<string>? DiagnosticOutput;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            DiagnosticOutput?.Invoke(this, "fake Codex App Server started");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _stopped = true;
            _outputLines.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => new(StopAsync());

        public void TriggerExit()
        {
            if (_stopped) return;
            ExitCode = 1;
            _outputLines.Writer.TryComplete();
            Exited?.Invoke(this, new CodexProcessExit(ExitCode));
        }

        private void HandleRequest(string line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("method", out var methodElement)) return;
            var method = methodElement.GetString()!;
            Methods.Add(method);
            if (!root.TryGetProperty("id", out var id)) return;
            var idText = id.GetRawText();
            switch (method)
            {
                case "initialize":
                    Respond(idText, new { userAgent = "fake-codex" });
                    break;
                case "thread/start":
                    ThreadStartParams = root.GetProperty("params").Clone();
                    Respond(idText, new { thread = new { id = $"thread-{++_threadNumber}" } });
                    break;
                case "thread/resume":
                    if (_resumeError is not null)
                    {
                        Error(idText, _resumeError);
                    }
                    else if (_resumeConflict)
                    {
                        Error(idText, "thread already has an active writer");
                    }
                    else
                    {
                        var resumedThreadId = root.GetProperty("params").GetProperty("threadId").GetString()!;
                        Respond(idText, new { thread = new { id = resumedThreadId } });
                    }

                    break;
                case "turn/start":
                    var threadId = root.GetProperty("params").GetProperty("threadId").GetString()!;
                    Respond(idText, new { turn = new { id = "turn-1" } });
                    Notify("turn/started", new { threadId, turn = new { id = "turn-1" } });
                    Notify("item/completed", new { threadId, item = new { type = "agentMessage", text = "fake result" } });
                    Notify("turn/completed", new { threadId, turn = new { id = "turn-1", status = "completed" } });
                    break;
                default:
                    Respond(idText, new { });
                    break;
            }
        }

        private void Respond(string id, object result) => Enqueue(new { id = JsonDocument.Parse(id).RootElement, result });

        private void Error(string id, string message) => Enqueue(new { id = JsonDocument.Parse(id).RootElement, error = new { message } });

        private void Notify(string method, object parameters) => Enqueue(new { method, @params = parameters });

        private void Enqueue(object message)
        {
            _outputLines.Writer.TryWrite(JsonSerializer.Serialize(message));
        }

        private sealed class FakeInput : TextWriter
        {
            private readonly Action<string> _callback;

            public FakeInput(Action<string> callback) => _callback = callback;

            public override Encoding Encoding => Encoding.UTF8;

            public override Task WriteLineAsync(string? value)
            {
                if (value is not null) _callback(value);
                return Task.CompletedTask;
            }
        }

        private sealed class ChannelReader : TextReader
        {
            private readonly Channel<string> _channel;

            public ChannelReader(Channel<string> channel) => _channel = channel;

            public override async Task<string?> ReadLineAsync()
            {
                return await _channel.Reader.WaitToReadAsync()
                    ? await _channel.Reader.ReadAsync()
                    : null;
            }
        }
    }
}
