const assert = require("assert");
const fs = require("fs");
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
    querySelector: () => null,
    querySelectorAll: () => [],
    children: [],
    childElementCount: 0,
    className: "",
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

function fastTiming(onWait) {
  return {
    waitForFrame: async () => {},
    wait: async (milliseconds) => {
      onWait?.(milliseconds);
    }
  };
}

function createEditorFixture({ text = "", execCommand, className = "ProseMirror" } = {}) {
  const editor = fakeElement({ id: "prompt-textarea", tagName: "DIV", text });
  const block = fakeElement({ tagName: "P", text });
  editor.children = [block];
  editor.childElementCount = 1;
  editor.className = className;
  editor.querySelector = (selector) => selector === "p" ? block : null;
  editor.querySelectorAll = (selector) => selector === "p" ? [block] : [];
  const documentRef = {
    defaultView: {
      Event: class TestEvent { constructor(type) { this.type = type; } },
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
    createTextNode: (nodeText) => ({ textContent: nodeText }),
    execCommand: execCommand || ((_command, _showUi, insertedText) => {
      editor.innerText += insertedText;
      editor.textContent = editor.innerText;
      return true;
    })
  };
  editor.ownerDocument = documentRef;
  return { editor, documentRef, root: fakeRoot(editor, [editor]) };
}

async function testComposerInjection() {
  const textarea = fakeElement({ id: "prompt-textarea", tagName: "TEXTAREA", value: "Existing input" });
  textarea.ownerDocument = { defaultView: { Event: class TestEvent { constructor(type) { this.type = type; } } } };
  const textareaResult = await utils.injectComposerResult(
    "new result",
    fakeRoot(textarea, [textarea]),
    { timing: fastTiming() });
  assert.strictEqual(textareaResult.success, true);
  assert.strictEqual(textareaResult.method, "prompt-textarea-value");
  assert.strictEqual(textareaResult.appended, true);
  assert.strictEqual(textareaResult.verification1, true);
  assert.strictEqual(textareaResult.verification2, true);
  assert.strictEqual(textarea.value, "Existing input\n\nnew result");
  assert.ok(textarea.events.includes("input"));

  const editorFixture = createEditorFixture({ text: "Existing input" });
  const editorWaits = [];
  const editorResult = await utils.injectComposerResult(
    "editor result",
    editorFixture.root,
    { timing: fastTiming((milliseconds) => editorWaits.push(milliseconds)) });
  assert.strictEqual(editorResult.success, true);
  assert.strictEqual(editorResult.method, "prompt-textarea-contenteditable");
  assert.strictEqual(editorResult.verification1, true);
  assert.strictEqual(editorResult.verification2, true);
  assert.strictEqual(editorResult.diagnostics.hasProseMirror, true);
  assert.strictEqual(editorResult.diagnostics.focusable, true);
  assert.deepStrictEqual(editorWaits, [220, 320]);
  assert.ok(!JSON.stringify(editorResult.diagnostics).includes("editor result"));
  assert.ok(editorFixture.editor.innerText.includes("editor result"));

  let waitCount = 0;
  const reconciledFixture = createEditorFixture({
    execCommand: () => false
  });
  const reconciledResult = await utils.injectComposerResult(
    "unstable result",
    reconciledFixture.root,
    {
      timing: fastTiming(() => {
        waitCount += 1;
        if (waitCount === 2) {
          reconciledFixture.editor.innerText = "";
          reconciledFixture.editor.textContent = "";
        }
      })
    });
  assert.strictEqual(reconciledResult.success, false);
  assert.strictEqual(reconciledResult.code, "composer_reconciled");
  assert.strictEqual(reconciledResult.method, "prompt-textarea-contenteditable-range");
  assert.strictEqual(reconciledResult.immediateVerification, true);
  assert.strictEqual(reconciledResult.verification1, true);
  assert.strictEqual(reconciledResult.verification2, false);

  reconciledFixture.editor.innerText = "";
  reconciledFixture.editor.textContent = "";
  reconciledFixture.documentRef.execCommand = (_command, _showUi, insertedText) => {
    reconciledFixture.editor.innerText += insertedText;
    reconciledFixture.editor.textContent = reconciledFixture.editor.innerText;
    return true;
  };
  const retryResult = await utils.injectComposerResult(
    "retry result",
    reconciledFixture.root,
    { timing: fastTiming() });
  assert.strictEqual(retryResult.success, true);
  assert.ok(reconciledFixture.editor.innerText.includes("retry result"));

  assert.deepStrictEqual(await utils.injectComposerResult("result", fakeRoot(null, []), { timing: fastTiming() }), {
    success: false,
    code: "composer_not_found"
  });
}

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

async function run() {
  await testComposerInjection();
  await testClipboardReader();

  const contentScript = fs.readFileSync(require.resolve("../content-script.js"), "utf8");
  assert.match(contentScript, /await utils\.injectComposerResult\(command\.result, document\)/);
  const serviceWorker = fs.readFileSync(require.resolve("../background/service-worker.js"), "utf8");
  assert.match(serviceWorker, /code === "composer_reconciled"/);
  assert.match(serviceWorker, /const acknowledgementCode = code \|\| response\?\.fallbackCode/);
  console.log("browser-extension pure tests passed");
}

run().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
