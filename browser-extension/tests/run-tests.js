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
console.log("browser-extension pure tests: 9 passed");
