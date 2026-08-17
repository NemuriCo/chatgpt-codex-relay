using System.Text.Json;
using BlueRelay.Services.Codex;

namespace BlueRelay.Tests;

[TestClass]
public sealed class CodexAppServerSmokeTests
{
    [TestMethod]
    public async Task ReadOnlyAppServerSmokeCanRunWhenExplicitlyEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("BLUERELAY_RUN_CODEX_SMOKE"), "1", StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set BLUERELAY_RUN_CODEX_SMOKE=1 to run the non-modifying Codex App Server smoke test.");
        }

        var executable = new CodexExecutableLocator().Locate(Environment.GetEnvironmentVariable("BLUERELAY_CODEX_PATH"));
        if (!executable.Found)
        {
            Assert.Inconclusive(executable.Error);
        }

        await using var process = new CodexAppServerProcess(executable.Path!);
        await process.StartAsync();
        await using var protocol = new CodexProtocolClient(process.Output!, process.Input!);
        var completed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        protocol.NotificationReceived += (_, notification) =>
        {
            if (notification.Method == "turn/completed")
            {
                completed.TrySetResult(notification.Params);
            }
        };
        protocol.ServerRequestReceived += (_, request) =>
        {
            _ = RespondToReadOnlyApprovalAsync(protocol, request);
        };
        protocol.Start();

        await protocol.RequestAsync(
            "initialize",
            new { clientInfo = new { name = "bluerelay-smoke-test", title = "BlueRelay smoke test", version = "0.1.0" } });
        await protocol.NotifyAsync("initialized", null);

        var thread = await protocol.RequestAsync(
            "thread/start",
            new
            {
                cwd = Environment.CurrentDirectory,
                approvalPolicy = "on-request",
                approvalsReviewer = "user",
                sandbox = "workspace-write",
                threadSource = "bluerelay-smoke-test"
            });
        var threadId = thread.GetProperty("thread").GetProperty("id").GetString();
        Assert.IsFalse(string.IsNullOrWhiteSpace(threadId));

        await protocol.RequestAsync(
            "turn/start",
            new
            {
                threadId,
                input = new[]
                {
                    new
                    {
                        type = "text",
                        text = "Read only: return the first line of README.md. Do not create, modify, or delete any files."
                    }
                }
            });

        var completion = await completed.Task.WaitAsync(TimeSpan.FromMinutes(2));
        var status = completion.GetProperty("turn").GetProperty("status").GetString();
        Assert.IsTrue(
            status is "completed" or "interrupted" or "failed",
            $"Unexpected Codex turn status: {status}");

        await process.StopAsync();
    }

    private static Task RespondToReadOnlyApprovalAsync(CodexProtocolClient protocol, CodexServerRequest request)
    {
        object response = request.Method switch
        {
            "item/commandExecution/requestApproval" => new { decision = "decline" },
            "item/fileChange/requestApproval" => new { decision = "decline" },
            "item/permissions/requestApproval" => new
            {
                permissions = new { fileSystem = (object?)null, network = (object?)null },
                scope = "turn"
            },
            _ => new { answers = new Dictionary<string, object>() }
        };
        return protocol.RespondAsync(request.RequestId, response);
    }
}
