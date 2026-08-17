(function () {
  const utils = self.BlueRelayUtils;
  let lastCapturedText = "";
  let lastCapturedAt = 0;

  function pageContext() {
    return {
      url: window.location.href,
      conversationId: utils.extractConversationId(window.location.href),
      title: document.title || "ChatGPT"
    };
  }

  function send(message) {
    return new Promise((resolve) => chrome.runtime.sendMessage(message, (response) => {
      if (chrome.runtime.lastError) {
        console.warn("[BlueRelay] content message failed", { stage: "runtime_message", code: "runtime_message_failed" });
        resolve({ success: false, code: "runtime_message_failed" });
        return;
      }

      resolve(response);
    }));
  }

  function extractCopyText(target) {
    const button = target && target.closest ? target.closest("button") : null;
    const label = button ? `${button.getAttribute("aria-label") || ""} ${button.getAttribute("title") || ""} ${button.textContent || ""}`.toLocaleLowerCase() : "";
    if (!label.includes("copy") && !label.includes("复制")) return "";
    const block = button.closest("pre, code, article, [data-message-author-role], main") || button.parentElement;
    return block ? block.innerText || block.textContent || "" : "";
  }

  async function captureIfTask(text) {
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

  async function handleCopy(event) {
    const selection = window.getSelection ? window.getSelection().toString() : "";
    await captureIfTask(selection || extractCopyText(event.target));
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

  async function handleCommand(message) {
    if (message.type !== "INJECT_RESULT") return;
    const command = message.command;
    const injection = utils.injectComposerResult(command.result, document);
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

  document.addEventListener("click", (event) => { captureIfTask(extractCopyText(event.target)); }, true);
  document.addEventListener("copy", handleCopy, true);
  chrome.runtime.onMessage.addListener((message, _sender, sendResponse) => {
    if (message.type === "INJECT_RESULT") {
      handleCommand(message).then(sendResponse);
      return true;
    }
    return false;
  });

  const context = pageContext();
  send({ type: "TAB_HELLO" });
  window.setInterval(() => {
    const current = pageContext();
    send({ type: "TAB_HEARTBEAT", url: current.url, conversationId: current.conversationId, title: current.title });
  }, 5000);
})();
