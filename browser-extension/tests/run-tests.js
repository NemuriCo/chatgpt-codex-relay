const assert = require("assert");
const utils = require("../shared/utils.js");
const manifest = require("../manifest.json");

assert.deepStrictEqual(manifest.permissions, ["storage", "tabs"]);
assert.deepStrictEqual(manifest.optional_permissions, ["clipboardRead", "clipboardWrite"]);
assert.strictEqual(utils.detectCodexTask("hello"), false);
assert.strictEqual(utils.detectCodexTask("# CODEX_TASK\n请实现 bridge"), true);
assert.strictEqual(utils.normalizeTaskText("  a\r\nb  "), "a\nb");
assert.strictEqual(utils.extractConversationId("https://chatgpt.com/c/abc-123"), "abc-123");
assert.strictEqual(utils.extractConversationId("https://chatgpt.com/"), null);
assert.strictEqual(utils.isSupportedChatGptUrl("https://chatgpt.com/c/abc"), true);
assert.strictEqual(utils.isSupportedChatGptUrl("https://example.com/c/abc"), false);
assert.strictEqual(utils.renderWorkstreamLabel({ projectName: "BlueProject", workstreamName: "Notifications" }), "BlueProject / Notifications");
assert.strictEqual(utils.findComposer(null), null);

function fakeElement({ id = "", tagName = "DIV", attrs = {}, value = "", text = "", visible = true } = {}) {
  const element = {
    id,
    tagName,
    disabled: false,
    readOnly: false,
    value,
    innerText: text,
    textContent: text,
    getAttribute: (name) => attrs[name] || null,
    closest: () => null,
    getBoundingClientRect: () => ({ width: visible ? 320 : 0, height: visible ? 48 : 0 }),
    focus: () => { element.focused = true; },
    dispatchEvent: (event) => { element.events.push(event.type); return true; },
    events: []
  };
  return element;
}

function fakeRoot(preferred, candidates) {
  return {
    querySelector: (selector) => selector === "#prompt-textarea" ? preferred : null,
    querySelectorAll: (selector) => selector.includes("form") || selector.includes("main") ? candidates : candidates
  };
}

const preferredComposer = fakeElement({ id: "prompt-textarea", tagName: "TEXTAREA" });
assert.strictEqual(utils.findComposer(fakeRoot(preferredComposer, [])), preferredComposer);

const searchBox = fakeElement({ attrs: { type: "search", "aria-label": "Search conversations" } });
const messageBox = fakeElement({ attrs: { role: "textbox", "aria-label": "Message" }, text: "" });
assert.strictEqual(utils.findComposer(fakeRoot(null, [searchBox, messageBox])), messageBox);

const textarea = fakeElement({ id: "prompt-textarea", tagName: "TEXTAREA", value: "Existing input" });
textarea.ownerDocument = { defaultView: { Event: class TestEvent { constructor(type) { this.type = type; } } } };
const textareaResult = utils.injectComposerResult("new result", fakeRoot(textarea, [textarea]));
assert.deepStrictEqual(textareaResult, {
  success: true,
  method: "prompt-textarea-value",
  appended: true
});
assert.strictEqual(textarea.value, "Existing input\n\nnew result");
assert.ok(textarea.events.includes("input"));

const editor = fakeElement({ id: "prompt-textarea", tagName: "DIV", text: "Existing input" });
const editorDocument = {
  defaultView: {
    getSelection: () => ({ removeAllRanges: () => {}, addRange: () => {} })
  },
  createRange: () => ({
    selectNodeContents: () => {},
    collapse: () => {},
    insertNode: (node) => {
      editor.innerText += node.textContent;
      editor.textContent = editor.innerText;
    },
    setStartAfter: () => {}
  }),
  createTextNode: (text) => ({ textContent: text }),
  execCommand: (_command, _showUi, text) => {
    editor.innerText += text;
    editor.textContent = editor.innerText;
    return true;
  }
};
editor.ownerDocument = editorDocument;
const editorResult = utils.injectComposerResult("editor result", fakeRoot(editor, [editor]));
assert.strictEqual(editorResult.success, true);
assert.strictEqual(editorResult.method, "prompt-textarea-contenteditable");
assert.ok(editor.innerText.includes("editor result"));

assert.deepStrictEqual(utils.injectComposerResult("result", fakeRoot(null, [])), {
  success: false,
  code: "composer_not_found"
});

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
  console.log("browser-extension pure tests passed");
}).catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
