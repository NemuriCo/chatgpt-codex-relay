const assert = require("assert");
const utils = require("../shared/utils.js");

assert.strictEqual(utils.detectCodexTask("hello"), false);
assert.strictEqual(utils.detectCodexTask("# CODEX_TASK\n请实现 bridge"), true);
assert.strictEqual(utils.normalizeTaskText("  a\r\nb  "), "a\nb");
assert.strictEqual(utils.extractConversationId("https://chatgpt.com/c/abc-123"), "abc-123");
assert.strictEqual(utils.extractConversationId("https://chatgpt.com/"), null);
assert.strictEqual(utils.isSupportedChatGptUrl("https://chatgpt.com/c/abc"), true);
assert.strictEqual(utils.isSupportedChatGptUrl("https://example.com/c/abc"), false);
assert.strictEqual(utils.renderWorkstreamLabel({ projectName: "BlueProject", workstreamName: "Notifications" }), "BlueProject / Notifications");
assert.strictEqual(utils.findComposer(null), null);

async function testClipboardReader() {
  let requestCount = 0;
  let readCount = 0;
  const permissionApi = {
    contains: async ({ permissions }) => {
      assert.deepStrictEqual(permissions, ["clipboardRead"]);
      return false;
    },
    request: async ({ permissions }) => {
      assert.deepStrictEqual(permissions, ["clipboardRead"]);
      requestCount += 1;
      return true;
    }
  };
  const reader = utils.createClipboardReader(permissionApi, {
    readText: async () => {
      readCount += 1;
      return "# CODEX_TASK\nclipboard task";
    }
  });

  assert.strictEqual(await reader.refreshPermission(), false);
  const firstRead = reader.readText();
  assert.strictEqual(requestCount, 1);
  assert.deepStrictEqual(await firstRead, { success: true, text: "# CODEX_TASK\nclipboard task" });
  assert.deepStrictEqual(await reader.readText(), { success: true, text: "# CODEX_TASK\nclipboard task" });
  assert.strictEqual(requestCount, 1);
  assert.strictEqual(readCount, 2);

  let alreadyGrantedRequests = 0;
  const alreadyGrantedReader = utils.createClipboardReader({
    contains: async () => true,
    request: async () => {
      alreadyGrantedRequests += 1;
      return true;
    }
  }, { readText: async () => "authorized task" });
  assert.strictEqual(await alreadyGrantedReader.refreshPermission(), true);
  assert.deepStrictEqual(await alreadyGrantedReader.readText(), { success: true, text: "authorized task" });
  assert.strictEqual(alreadyGrantedRequests, 0);

  let deniedReads = 0;
  const deniedReader = utils.createClipboardReader({
    contains: async () => false,
    request: async () => false
  }, { readText: async () => { deniedReads += 1; return "should not be read"; } });
  await deniedReader.refreshPermission();
  assert.deepStrictEqual(await deniedReader.readText(), { success: false, reason: "permission-denied" });
  assert.strictEqual(deniedReads, 0);
}

testClipboardReader().then(() => {
  console.log("browser-extension pure tests: 13 passed");
}).catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
