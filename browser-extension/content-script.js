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
    return new Promise((resolve) => chrome.runtime.sendMessage(message, (response) => resolve(response)));
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

  function injectResult(result) {
    const composer = utils.findComposer(document);
    if (!composer) return false;
    composer.focus();
    if (composer instanceof HTMLTextAreaElement || composer instanceof HTMLInputElement) {
      const setter = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(composer), "value")?.set;
      if (setter) setter.call(composer, result); else composer.value = result;
      composer.dispatchEvent(new Event("input", { bubbles: true, composed: true }));
      composer.dispatchEvent(new Event("change", { bubbles: true, composed: true }));
      return true;
    }

    composer.textContent = result;
    composer.dispatchEvent(new InputEvent("input", { bubbles: true, composed: true, inputType: "insertText", data: result }));
    return true;
  }

  function showFallbackNotice() {
    const notice = document.createElement("div");
    notice.id = "bluerelay-fallback-notice";
    notice.textContent = /^zh/i.test(navigator.language)
      ? "BlueRelay：找不到 ChatGPT 输入框，结果已复制到剪贴板。"
      : "BlueRelay: ChatGPT composer not found. The result was copied to the clipboard.";
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
    const success = injectResult(command.result);
    if (!success) {
      try { await navigator.clipboard.writeText(command.result); } catch (_) { /* result remains in BlueRelay */ }
      showFallbackNotice();
    }
    return { success };
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
