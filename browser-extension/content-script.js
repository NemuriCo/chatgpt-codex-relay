(function () {
  const utils = self.BlueRelayUtils;
  const lifecycleUtils = self.BlueRelayRuntimeLifecycle;
  const runtime = chrome.runtime;
  const currentMarker = globalThis.__blueRelayContentScriptActive;
  if (!lifecycleUtils || lifecycleUtils.isSameRuntimeContext(currentMarker, runtime)) return;
  currentMarker?.stop?.();

  let lifecycle = null;
  let cleanedUp = false;
  let clickListener = null;
  let copyListener = null;
  let runtimeListener = null;
  const marker = {
    runtime,
    active: true,
    isAlive: () => lifecycle?.isAlive() === true,
    isRuntimeUsable: () => {
      try { return Boolean(runtime && runtime.id); } catch (_) { return false; }
    },
    stop: () => lifecycle?.stop()
  };

  function cleanup() {
    if (cleanedUp) return;
    cleanedUp = true;
    try { document.removeEventListener("click", clickListener, true); } catch (_) { /* stale context */ }
    try { document.removeEventListener("copy", copyListener, true); } catch (_) { /* stale context */ }
    try { runtime.onMessage.removeListener(runtimeListener); } catch (_) { /* stale context */ }
  }

  lifecycle = lifecycleUtils.createRuntimeState({
    clearIntervalImpl: (handle) => window.clearInterval(handle),
    onInvalidated: () => console.warn("[BlueRelay] extension context invalidated"),
    onStop: () => {
      marker.active = false;
      cleanup();
    }
  });
  globalThis.__blueRelayContentScriptActive = marker;

  const runtimeSend = lifecycleUtils.createRuntimeSender(runtime, lifecycle);
  let lastCapturedText = "";
  let lastCapturedAt = 0;
  const CONTENT_SCRIPT_VERSION = "0.2.0";

  function send(message) {
    return runtimeSend(message);
  }

  function pageContext() {
    return {
      url: window.location.href,
      conversationId: utils.extractConversationId(window.location.href),
      title: document.title || "ChatGPT"
    };
  }

  function extractCopyText(target) {
    const button = target && target.closest ? target.closest("button") : null;
    const label = button ? `${button.getAttribute("aria-label") || ""} ${button.getAttribute("title") || ""} ${button.textContent || ""}`.toLocaleLowerCase() : "";
    if (!label.includes("copy") && !label.includes("复制")) return "";
    const block = button.closest("pre, code, article, [data-message-author-role], main") || button.parentElement;
    return block ? block.innerText || block.textContent || "" : "";
  }

  async function captureIfTask(text) {
    if (!lifecycle.isAlive()) return;
    const normalized = utils.normalizeTaskText(text);
    if (!utils.detectCodexTask(normalized)) return;
    const now = Date.now();
    if (normalized === lastCapturedText && now - lastCapturedAt < 1500) return;
    lastCapturedText = normalized;
    lastCapturedAt = now;
    const response = await send({ type: "CAPTURE_TASK", prompt: normalized });
    if (response && response.success) {
      document.dispatchEvent(new CustomEvent("bluerelay-task-captured", { detail: response.task }));
    }
  }

  function handleCopy(event) {
    const selection = window.getSelection ? window.getSelection().toString() : "";
    safeCapture(selection || extractCopyText(event.target));
  }

  function safeCapture(text) {
    void captureIfTask(text).catch(() => undefined);
  }

  function showFallbackNotice(copySucceeded) {
    const notice = document.createElement("div");
    notice.id = "bluerelay-fallback-notice";
    const localized = chrome.i18n?.getMessage?.(copySucceeded ? "composerFallback" : "composerFallbackFailed");
    notice.textContent = localized || (/^zh/i.test(navigator.language)
      ? copySucceeded
        ? "BlueRelay：无法自动填入 ChatGPT，结果已复制到剪贴板。"
        : "BlueRelay：无法自动填入 ChatGPT，且复制到剪贴板失败。"
      : copySucceeded
        ? "BlueRelay: ChatGPT could not be filled automatically; the result was copied to the clipboard."
        : "BlueRelay: ChatGPT could not be filled automatically, and clipboard copy failed.");
    Object.assign(notice.style, {
      position: "fixed", zIndex: "2147483647", right: "18px", bottom: "18px",
      maxWidth: "360px", padding: "10px 12px", borderRadius: "8px",
      color: "#edf2f7", background: "#1a202a", border: "1px solid #4b5b70",
      boxShadow: "0 8px 24px rgba(0,0,0,.35)", font: "13px Segoe UI, sans-serif"
    });
    document.getElementById("bluerelay-fallback-notice")?.remove();
    document.body.appendChild(notice);
    window.setTimeout(() => notice.remove(), 6000);
  }

  function logComposerInjection(injection) {
    const diagnostics = injection?.diagnostics || {};
    console.info("[BlueRelay] composer injection", {
      stage: diagnostics.stage || "injection",
      method: injection?.method || diagnostics.method || null,
      composerId: diagnostics.composerId || null,
      tagName: diagnostics.tagName || null,
      contentEditable: diagnostics.contentEditable ?? null,
      role: diagnostics.role || null,
      childElementCount: diagnostics.childElementCount ?? null,
      hasParagraph: diagnostics.hasParagraph ?? null,
      hasProseMirror: diagnostics.hasProseMirror ?? null,
      childTags: diagnostics.childTags || [],
      focusable: diagnostics.focusable ?? null,
      immediateVerification: injection?.immediateVerification ?? null,
      verification1: injection?.verification1 ?? null,
      verification2: injection?.verification2 ?? null,
      resultLength: injection?.resultLength ?? null,
      existingLength: injection?.existingLength ?? null,
      code: injection?.code || null
    });
  }

  async function handleCommand(message) {
    if (message.type !== "INJECT_RESULT") return;
    if (!lifecycle.isAlive()) return { success: false, code: "extension_context_invalidated" };
    const command = message.command;
    const injection = await utils.injectComposerResult(command.result, document);
    logComposerInjection(injection);
    if (injection.success) {
      return injection;
    }

    let clipboardSucceeded = false;
    let clipboardCode = "clipboard_write_failed";
    try {
      if (!navigator.clipboard || typeof navigator.clipboard.writeText !== "function") {
        clipboardCode = "clipboard_write_unavailable";
      } else {
        await navigator.clipboard.writeText(command.result);
        clipboardSucceeded = true;
      }
    } catch (_) {
      clipboardCode = "clipboard_write_failed";
    }

    showFallbackNotice(clipboardSucceeded);
    return {
      success: false,
      code: injection.code || "injection_failed",
      method: injection.method,
      fallback: clipboardSucceeded ? "clipboard" : "clipboard_failed",
      fallbackCode: clipboardSucceeded ? null : clipboardCode
    };
  }

  clickListener = (event) => { safeCapture(extractCopyText(event.target)); };
  copyListener = handleCopy;
  runtimeListener = (message, _sender, sendResponse) => {
    if (message?.type === "BLUERELAY_PING") {
      const contextAlive = lifecycle.isAlive();
      sendResponse({ success: contextAlive, version: CONTENT_SCRIPT_VERSION, contextAlive });
      return false;
    }
    if (message?.type === "INJECT_RESULT") {
      handleCommand(message).then(sendResponse).catch(() => sendResponse({ success: false, code: "command_failed" }));
      return true;
    }
    return false;
  };
  document.addEventListener("click", clickListener, true);
  document.addEventListener("copy", copyListener, true);
  runtime.onMessage.addListener(runtimeListener);

  send({ type: "TAB_HELLO" });
  const heartbeatHandle = window.setInterval(() => {
    const current = pageContext();
    send({ type: "TAB_HEARTBEAT", url: current.url, conversationId: current.conversationId, title: current.title });
  }, 5000);
  lifecycle.setHeartbeatHandle(heartbeatHandle);
})();
