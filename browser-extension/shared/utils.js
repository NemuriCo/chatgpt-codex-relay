(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory();
  } else {
    root.BlueRelayUtils = factory();
  }
})(typeof self !== "undefined" ? self : this, function () {
  const CODEX_TASK_MARKER = "# CODEX_TASK";

  function normalizeTaskText(text) {
    return String(text || "")
      .replace(/\r\n/g, "\n")
      .replace(/\r/g, "\n")
      .trim();
  }

  function detectCodexTask(text) {
    const normalized = normalizeTaskText(text);
    return normalized.toLocaleLowerCase().includes(CODEX_TASK_MARKER.toLocaleLowerCase());
  }

  function extractConversationId(url) {
    try {
      const parsed = new URL(url);
      const segments = parsed.pathname.split("/").filter(Boolean);
      const conversationIndex = segments.findIndex((segment) => segment === "c" || segment === "conversation");
      if (conversationIndex >= 0 && segments[conversationIndex + 1]) {
        return segments[conversationIndex + 1];
      }

      const uuid = segments.find((segment) => /^[0-9a-f]{8}-[0-9a-f-]{27,}$/i.test(segment));
      return uuid || null;
    } catch (_) {
      return null;
    }
  }

  function isSupportedChatGptUrl(url) {
    try {
      const parsed = new URL(url);
      return parsed.protocol === "https:" &&
        (parsed.hostname === "chatgpt.com" || parsed.hostname.endsWith(".chatgpt.com") || parsed.hostname === "chat.openai.com");
    } catch (_) {
      return false;
    }
  }

  function renderWorkstreamLabel(workstream) {
    if (!workstream) return "";
    return `${workstream.projectName || ""} / ${workstream.workstreamName || ""}`.trim();
  }

  function attributeText(element) {
    return [
      element?.id,
      element?.getAttribute?.("aria-label"),
      element?.getAttribute?.("placeholder"),
      element?.getAttribute?.("name"),
      element?.getAttribute?.("data-testid"),
      element?.getAttribute?.("type")
    ].filter(Boolean).join(" ").toLocaleLowerCase();
  }

  function isSearchLike(element) {
    const type = (element?.getAttribute?.("type") || "").toLocaleLowerCase();
    return type === "search" || /search|搜索/.test(attributeText(element));
  }

  function isVisible(element) {
    const rect = element?.getBoundingClientRect ? element.getBoundingClientRect() : { width: 0, height: 0 };
    return Number(rect.width) > 0 && Number(rect.height) > 0;
  }

  function isComposerCandidate(element, requireVisible = true) {
    if (!element || element.disabled || element.readOnly || isSearchLike(element)) return false;
    if (element.closest?.("aside, nav")) return false;
    return !requireVisible || isVisible(element);
  }

  function findComposer(root) {
    const scope = root || (typeof document !== "undefined" ? document : null);
    if (!scope || !scope.querySelectorAll) return null;

    const preferred = scope.querySelector?.("#prompt-textarea");
    if (isComposerCandidate(preferred)) return preferred;

    const selectors = [
      "main form textarea, main form [contenteditable=\"true\"], main form [role=\"textbox\"]",
      "form textarea, form [contenteditable=\"true\"], form [role=\"textbox\"]",
      "main textarea, main [contenteditable=\"true\"], main [role=\"textbox\"]"
    ];
    for (const selector of selectors) {
      const candidates = Array.from(scope.querySelectorAll(selector));
      const match = candidates.find((element) => isComposerCandidate(element));
      if (match) return match;
    }

    const candidates = Array.from(scope.querySelectorAll("textarea, [contenteditable=\"true\"], [role=\"textbox\"]"));
    return candidates.find((element) => isComposerCandidate(element)) ||
      candidates.find((element) => isComposerCandidate(element, false)) || null;
  }

  function composerText(element) {
    if (!element) return "";
    if (element.tagName?.toLocaleLowerCase() === "textarea" || element.tagName?.toLocaleLowerCase() === "input") {
      return String(element.value || "");
    }
    return String(element.innerText ?? element.textContent ?? "");
  }

  function normalizedComparableText(text) {
    return String(text || "").replace(/\s+/g, " ").trim();
  }

  function containsInsertedText(element, result) {
    const current = composerText(element);
    return current.includes(result) || normalizedComparableText(current).includes(normalizedComparableText(result));
  }

  function appendText(existing, result) {
    return existing.trim() ? `${existing}\n\n${result}` : result;
  }

  function dispatchEvent(element, type, init = {}) {
    const view = element?.ownerDocument?.defaultView || (typeof window !== "undefined" ? window : null);
    const EventType = type === "input" && view?.InputEvent ? view.InputEvent : view?.Event;
    if (!EventType || !element?.dispatchEvent) return;
    try {
      element.dispatchEvent(new EventType(type, Object.assign({ bubbles: true, composed: true }, init)));
    } catch (_) {
      // Older embedded documents may reject the composed option; the value is still updated.
    }
  }

  function setNativeValue(element, value) {
    const view = element?.ownerDocument?.defaultView || (typeof window !== "undefined" ? window : null);
    const constructor = element?.tagName?.toLocaleLowerCase() === "textarea"
      ? view?.HTMLTextAreaElement
      : view?.HTMLInputElement;
    const setter = constructor
      ? Object.getOwnPropertyDescriptor(constructor.prototype, "value")?.set
      : null;
    if (setter) setter.call(element, value);
    else element.value = value;
  }

  function injectComposerResult(result, root) {
    const text = String(result || "");
    if (!text) return { success: false, code: "result_empty" };

    const composer = findComposer(root);
    if (!composer) return { success: false, code: "composer_not_found" };

    composer.focus?.();
    const existing = composerText(composer);
    const nextText = appendText(existing, text);
    const tagName = composer.tagName?.toLocaleLowerCase();
    if (tagName === "textarea" || tagName === "input") {
      try {
        setNativeValue(composer, nextText);
        dispatchEvent(composer, "input", { inputType: "insertText", data: text });
        dispatchEvent(composer, "change");
      } catch (_) {
        return { success: false, code: "injection_failed" };
      }

      return composerText(composer) === nextText || containsInsertedText(composer, text)
        ? { success: true, method: composer.id === "prompt-textarea" ? "prompt-textarea-value" : "textarea-value", appended: Boolean(existing.trim()) }
        : { success: false, code: "injection_failed" };
    }

    const documentRef = composer.ownerDocument || (typeof document !== "undefined" ? document : null);
    if (!documentRef?.createRange) return { success: false, code: "injection_failed" };

    const view = documentRef.defaultView || (typeof window !== "undefined" ? window : null);
    const selection = view?.getSelection?.();
    let range;
    try {
      range = documentRef.createRange();
      range.selectNodeContents(composer);
      range.collapse(false);
      selection?.removeAllRanges?.();
      selection?.addRange?.(range);
    } catch (_) {
      return { success: false, code: "injection_failed" };
    }

    const insertion = existing.trim() ? `\n\n${text}` : text;
    let method = composer.id === "prompt-textarea" ? "prompt-textarea-contenteditable" : "contenteditable";
    let inserted = false;
    try {
      inserted = documentRef.execCommand?.("insertText", false, insertion) === true;
    } catch (_) {
      inserted = false;
    }

    if (containsInsertedText(composer, text)) {
      inserted = true;
    }

    if (!containsInsertedText(composer, text)) {
      try {
        const fallbackRange = documentRef.createRange();
        fallbackRange.selectNodeContents(composer);
        fallbackRange.collapse(false);
        const textNode = documentRef.createTextNode(insertion);
        fallbackRange.insertNode(textNode);
        fallbackRange.setStartAfter(textNode);
        fallbackRange.collapse(true);
        selection?.removeAllRanges?.();
        selection?.addRange?.(fallbackRange);
        method = `${method}-range`;
        inserted = true;
      } catch (_) {
        inserted = false;
      }
    }

    if (!inserted || !containsInsertedText(composer, text)) {
      return { success: false, code: "injection_failed" };
    }

    dispatchEvent(composer, "input", { inputType: "insertText", data: insertion });
    return { success: true, method, appended: Boolean(existing.trim()) };
  }

  function createClipboardReader(permissionsApi, clipboardApi) {
    const permission = { permissions: ["clipboardRead"] };
    let permissionChecked = false;
    let permissionGranted = false;

    async function refreshPermission() {
      permissionChecked = true;
      try {
        permissionGranted = await permissionsApi.contains(permission);
      } catch (_) {
        permissionGranted = false;
      }
      return permissionGranted;
    }

    async function readText() {
      if (!permissionChecked) {
        return { success: false, reason: "permission-check-failed" };
      }

      if (!permissionGranted) {
        let granted = false;
        try {
          // This call is reached synchronously from the Popup click handler.
          granted = await permissionsApi.request(permission);
        } catch (_) {
          granted = false;
        }
        if (!granted) {
          return { success: false, reason: "permission-denied" };
        }
        permissionGranted = true;
      }

      if (!clipboardApi || typeof clipboardApi.readText !== "function") {
        return { success: false, reason: "read-failed" };
      }

      try {
        return { success: true, text: await clipboardApi.readText() };
      } catch (_) {
        return { success: false, reason: "read-failed" };
      }
    }

    return { refreshPermission, readText };
  }

  return {
    CODEX_TASK_MARKER,
    normalizeTaskText,
    detectCodexTask,
    extractConversationId,
    isSupportedChatGptUrl,
    renderWorkstreamLabel,
    findComposer,
    composerText,
    injectComposerResult,
    createClipboardReader
  };
});
