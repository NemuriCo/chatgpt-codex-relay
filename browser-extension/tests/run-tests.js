const assert = require("assert");
const fs = require("fs");
const path = require("path");
const utils = require("../shared/utils.js");
const lifecycle = require("../shared/runtime-lifecycle.js");
const manifest = require("../manifest.json");

assert.deepStrictEqual(manifest.permissions, ["storage", "tabs", "scripting"]);
assert.deepStrictEqual(manifest.optional_permissions, ["clipboardRead", "clipboardWrite"]);
assert.deepStrictEqual(manifest.host_permissions, [
  "https://chatgpt.com/*",
  "https://chat.openai.com/*",
  "http://127.0.0.1:48917/*",
  "http://localhost:48917/*"
]);
assert.deepStrictEqual(manifest.icons, {
  "16": "icons/icon16.png",
  "32": "icons/icon32.png",
  "48": "icons/icon48.png",
  "128": "icons/icon128.png"
});
assert.deepStrictEqual(manifest.action.default_icon, {
  "16": "icons/icon16.png",
  "32": "icons/icon32.png"
});
assert.deepStrictEqual(manifest.content_scripts[0].js, ["shared/utils.js", "shared/runtime-lifecycle.js", "content-script.js"]);
for (const size of [16, 32, 48, 128]) {
  const png = fs.readFileSync(path.join(__dirname, "..", "icons", `icon${size}.png`));
  assert.strictEqual(png.subarray(0, 8).toString("hex"), "89504e470d0a1a0a");
  assert.strictEqual(png.readUInt32BE(16), size);
  assert.strictEqual(png.readUInt32BE(20), size);
}
const ico = fs.readFileSync(path.join(__dirname, "..", "..", "src", "BlueRelay", "Assets", "Icons", "BlueRelay.ico"));
assert.strictEqual(ico.readUInt16LE(0), 0);
assert.strictEqual(ico.readUInt16LE(2), 1);
assert.strictEqual(ico.readUInt16LE(4), 8);
const icoSizes = Array.from({ length: 8 }, (_, index) => {
  const entry = 6 + index * 16;
  const width = ico[entry] || 256;
  const height = ico[entry + 1] || 256;
  const byteLength = ico.readUInt32LE(entry + 8);
  const offset = ico.readUInt32LE(entry + 12);
  assert.strictEqual(height, width);
  assert.ok(offset + byteLength <= ico.length);
  assert.strictEqual(ico.subarray(offset, offset + 8).toString("hex"), "89504e470d0a1a0a");
  return width;
});
assert.deepStrictEqual(icoSizes, [16, 20, 24, 32, 48, 64, 128, 256]);
assert.strictEqual(utils.detectCodexTask("hello"), false);
assert.strictEqual(utils.detectCodexTask("# CODEX_TASK\n请实现 bridge"), true);
assert.strictEqual(utils.normalizeTaskText("  a\r\nb  "), "a\nb");
assert.strictEqual(utils.extractConversationId("https://chatgpt.com/c/abc-123"), "abc-123");
assert.strictEqual(utils.extractConversationId("https://chatgpt.com/"), null);
assert.strictEqual(utils.isSupportedChatGptUrl("https://chatgpt.com/c/abc"), true);
assert.strictEqual(utils.isSupportedChatGptUrl("https://sub.chatgpt.com/c/abc"), false);
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

async function testRuntimeLifecycle() {
  assert.strictEqual(lifecycle.classifyRuntimeMessageFailure(new Error("Extension context invalidated.")), "extension_context_invalidated");
  assert.strictEqual(lifecycle.classifyRuntimeMessageFailure({ message: "The message port closed before a response was received." }), "runtime_message_failed");

  const clearedHandles = [];
  let invalidationCount = 0;
  const state = lifecycle.createRuntimeState({
    clearIntervalImpl: (handle) => clearedHandles.push(handle),
    onInvalidated: () => { invalidationCount += 1; }
  });
  state.setHeartbeatHandle("heartbeat");
  const throwingSender = lifecycle.createRuntimeSender({
    id: "blue-relay",
    sendMessage: () => { throw new Error("Extension context invalidated."); }
  }, state);
  assert.deepStrictEqual(await throwingSender({ type: "TAB_HEARTBEAT" }), { success: false, code: "extension_context_invalidated" });
  assert.strictEqual(state.isAlive(), false);
  assert.deepStrictEqual(clearedHandles, ["heartbeat"]);
  assert.strictEqual(invalidationCount, 1);
  assert.deepStrictEqual(await throwingSender({ type: "TAB_HEARTBEAT" }), { success: false, code: "extension_context_invalidated" });

  let lastErrorCallback;
  const lastErrorSender = lifecycle.createRuntimeSender({
    id: "blue-relay",
    lastError: { message: "The message port closed before a response was received." },
    sendMessage: (_message, callback) => {
      lastErrorCallback = callback;
      callback(undefined);
    }
  }, lifecycle.createRuntimeState());
  assert.deepStrictEqual(await lastErrorSender({ type: "TAB_HELLO" }), { success: false, code: "runtime_message_failed" });
  assert.strictEqual(typeof lastErrorCallback, "function");

  const normalSender = lifecycle.createRuntimeSender({
    id: "blue-relay",
    sendMessage: (_message, callback) => callback({ success: true, code: "ok" })
  }, lifecycle.createRuntimeState());
  assert.deepStrictEqual(await normalSender({ type: "TAB_HELLO" }), { success: true, code: "ok" });

  const promiseRejectSender = lifecycle.createRuntimeSender({
    id: "blue-relay",
    sendMessage: () => Promise.reject(new Error("Extension context invalidated."))
  }, lifecycle.createRuntimeState());
  assert.deepStrictEqual(await promiseRejectSender({ type: "TAB_HELLO" }), { success: false, code: "extension_context_invalidated" });

  const runtime = { id: "blue-relay" };
  assert.strictEqual(lifecycle.isSameRuntimeContext({ runtime, active: true, isAlive: () => true }, runtime), true);
  assert.strictEqual(lifecycle.isSameRuntimeContext({ runtime, active: false, isAlive: () => true }, runtime), false);
  assert.strictEqual(lifecycle.isSameRuntimeContext({ runtime: {}, active: true, isAlive: () => true }, runtime), false);
  assert.strictEqual(lifecycle.isLiveContentScriptResponse({ success: true, version: "0.2.0", contextAlive: true }), true);
  assert.strictEqual(lifecycle.isLiveContentScriptResponse({ success: true, contextAlive: false }), false);

  const supportedTab = { id: 7, url: "https://chatgpt.com/c/example" };
  const logs = [];
  let injectCount = 0;
  let pingCount = 0;
  const pingResponses = [
    { success: true, code: "alive" }
  ];
  const alreadyReady = lifecycle.createContentScriptEnsurer({
    isSupportedUrl: (url) => url.startsWith("https://chatgpt.com/"),
    ping: async () => {
      pingCount += 1;
      return pingResponses[0];
    },
    inject: async () => { injectCount += 1; },
    log: (_tab, stage, code) => logs.push([stage, code])
  });
  assert.deepStrictEqual(await alreadyReady(supportedTab), { success: true, injected: false, code: "already_ready" });
  assert.strictEqual(injectCount, 0);
  assert.strictEqual(pingCount, 1);

  let missingPingCount = 0;
  const needsInjection = lifecycle.createContentScriptEnsurer({
    isSupportedUrl: (url) => url.startsWith("https://chatgpt.com/"),
    ping: async () => {
      missingPingCount += 1;
      return missingPingCount === 1 ? { success: false, code: "runtime_message_failed" } : { success: true, code: "alive" };
    },
    inject: async () => { injectCount += 1; },
    log: (_tab, stage, code) => logs.push([stage, code])
  });
  assert.deepStrictEqual(await needsInjection(supportedTab), { success: true, injected: true, code: "injected" });
  assert.strictEqual(injectCount, 1);
  assert.strictEqual(missingPingCount, 2);

  let concurrentInjectCount = 0;
  let releaseInjection;
  const concurrent = lifecycle.createContentScriptEnsurer({
    isSupportedUrl: (url) => url.startsWith("https://chatgpt.com/"),
    ping: async () => ({ success: false, code: "ping_no_response" }),
    inject: () => new Promise((resolve) => {
      concurrentInjectCount += 1;
      releaseInjection = resolve;
    })
  });
  const first = concurrent(supportedTab);
  const second = concurrent({ ...supportedTab });
  assert.strictEqual(first, second);
  await new Promise((resolve) => setTimeout(resolve, 0));
  releaseInjection();
  // The second ping remains missing, so the result verifies the single injection attempt.
  assert.deepStrictEqual(await first, { success: false, injected: true, code: "ping_no_response" });
  assert.strictEqual(concurrentInjectCount, 1);
  assert.deepStrictEqual(await concurrent({ id: 8, url: "https://example.com/" }), { success: false, code: "unsupported_origin" });
}

async function run() {
  await testComposerInjection();
  await testClipboardReader();

  const contentScript = fs.readFileSync(require.resolve("../content-script.js"), "utf8");
  assert.match(contentScript, /await utils\.injectComposerResult\(command\.result, document\)/);
  assert.match(contentScript, /function send\(message\)/);
  assert.match(contentScript, /BLUERELAY_PING/);
  assert.match(contentScript, /__blueRelayContentScriptActive/);
  assert.match(contentScript, /setHeartbeatHandle/);
  const serviceWorker = fs.readFileSync(require.resolve("../background/service-worker.js"), "utf8");
  assert.match(serviceWorker, /code === "composer_reconciled"/);
  assert.match(serviceWorker, /const acknowledgementCode = code \|\| response\?\.fallbackCode/);
  assert.match(serviceWorker, /ensureContentScript = lifecycleUtils\.createContentScriptEnsurer/);
  assert.match(serviceWorker, /chrome\.scripting\?\.executeScript/);
  assert.match(serviceWorker, /BLUERELAY_PING/);
  assert.match(serviceWorker, /createContentScriptEnsurer/);
  await testRuntimeLifecycle();
  console.log("browser-extension pure tests passed");
}

run().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
