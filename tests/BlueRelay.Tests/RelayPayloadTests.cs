using BlueRelay.Models;
using BlueRelay.Persistence;
using BlueRelay.Services.Bridges;

namespace BlueRelay.Tests;

[TestClass]
public sealed class RelayPayloadTests
{
    [TestMethod]
    public void PromptComposerOmitsEmptyNoteHeaders()
    {
        Assert.AreEqual("payload", RelayPromptComposer.Compose(" ", "payload"));
        Assert.AreEqual("用户补充：\nnote\n\n完整任务：\npayload", RelayPromptComposer.Compose("note", "payload"));
        Assert.AreEqual("Codex 执行结果：\nresult", RelayPromptComposer.ComposeResult(null, "result"));
    }

    [TestMethod]
    public async Task PayloadStoreRoundTripsMetadataAndText()
    {
        var root = Path.Combine(Path.GetTempPath(), "BlueRelayPayloadTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RelayPayloadStore(root);
            var payload = await store.WriteAsync(Guid.NewGuid(), Guid.NewGuid(), "task.md", "长文本\nsecond line");
            Assert.AreEqual("长文本\nsecond line", await store.ReadAsync(payload));
            Assert.AreEqual(payload.Length, System.Text.Encoding.UTF8.GetByteCount("长文本\nsecond line"));
            Assert.IsFalse(string.IsNullOrWhiteSpace(payload.Sha256));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task NewPayloadReferencesDoNotDuplicateBodyInStateJson()
    {
        var root = Path.Combine(Path.GetTempPath(), "BlueRelayPayloadTests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var store = new RelayPayloadStore(Path.Combine(root, "relay"));
            var taskId = Guid.NewGuid();
            var workstreamId = Guid.NewGuid();
            var body = new string('x', 4096);
            var payload = await store.WriteAsync(workstreamId, taskId, "task.md", body);
            var state = new ApplicationState();
            state.BrowserBridge.Tasks.Add(new RelayTask
            {
                Id = taskId,
                WorkstreamId = workstreamId,
                Prompt = body,
                Payload = payload
            });

            var statePath = Path.Combine(root, "state.json");
            await new JsonStateStore(statePath).SaveAsync(state);
            var json = await File.ReadAllTextAsync(statePath);
            Assert.IsFalse(json.Contains(body, StringComparison.Ordinal));
            Assert.IsTrue(json.Contains("task.md", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
