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
    if ((element.getAttribute?.("contenteditable") || "").toLocaleLowerCase() === "false") return false;
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

  function elementChildren(element) {
    return element?.children ? Array.from(element.children) : [];
  }

  function elementClassName(element) {
    const className = element?.className;
    return typeof className === "string" ? className : String(className?.baseVal || "");
  }

  function composerDiagnostics(element, details = {}) {
    const children = elementChildren(element);
    const childTags = children.slice(0, 12)
      .map((child) => child?.tagName?.toLocaleLowerCase())
      .filter(Boolean);
    const contentEditable = element?.getAttribute?.("contenteditable") ?? element?.contentEditable ?? null;
    const hasParagraph = Boolean(element?.querySelector?.("p")) || childTags.includes("p");
    return Object.assign({
      composerId: element?.id || null,
      tagName: element?.tagName?.toLocaleLowerCase() || null,
      contentEditable,
      role: element?.getAttribute?.("role") || null,
      childElementCount: Number(element?.childElementCount ?? children.length),
      hasParagraph,
      hasProseMirror: /\bprosemirror\b/i.test(elementClassName(element)),
      childTags
    }, details);
  }

  function isEditableComposer(element) {
    if (!element || element.disabled || element.readOnly) return false;
    return (element.getAttribute?.("contenteditable") || "").toLocaleLowerCase() !== "false";
  }

  function isFocusableComposer(element) {
    return isEditableComposer(element) && typeof element.focus === "function";
  }

  function findEditableBlock(composer) {
    if (!composer?.querySelectorAll) return null;
    const selectors = [
      "p",
      "li",
      "blockquote",
      "pre",
      "h1, h2, h3, h4, h5, h6",
      "[data-lexical-node]",
      "[data-slate-node=\"element\"]"
    ];
    for (const selector of selectors) {
      const blocks = Array.from(composer.querySelectorAll(selector))
        .filter((element) => isEditableComposer(element));
      if (blocks.length) return blocks[blocks.length - 1];
    }
    return null;
  }

  function getSelection(documentRef) {
    return documentRef?.defaultView?.getSelection?.() || documentRef?.getSelection?.() || null;
  }

  function placeCaretAtEnd(composer, documentRef) {
    if (!documentRef?.createRange) return { success: false, hasBlock: false };
    let target = findEditableBlock(composer);
    if (!target && typeof documentRef.execCommand === "function") {
      try {
        documentRef.execCommand("insertParagraph", false, null);
        target = findEditableBlock(composer);
      } catch (_) {
        target = null;
      }
    }
    target = target || composer;
    const selection = getSelection(documentRef);
    try {
      const range = documentRef.createRange();
      range.selectNodeContents(target);
      range.collapse(false);
      selection?.removeAllRanges?.();
      selection?.addRange?.(range);
      return { success: true, hasBlock: target !== composer, target, selection };
    } catch (_) {
      return { success: false, hasBlock: target !== composer, target, selection };
    }
  }

  function waitForFrame(documentRef) {
    const requestAnimationFrame = documentRef?.defaultView?.requestAnimationFrame;
    if (typeof requestAnimationFrame === "function") {
      return new Promise((resolve) => requestAnimationFrame(() => resolve()));
    }
    return new Promise((resolve) => setTimeout(resolve, 0));
  }

  function waitForDelay(milliseconds) {
    return new Promise((resolve) => setTimeout(resolve, milliseconds));
  }

  function verifyInsertedText(root, result) {
    const composer = findComposer(root);
    return {
      composer,
      present: Boolean(composer && containsInsertedText(composer, result)),
      editable: isEditableComposer(composer),
      focusable: isFocusableComposer(composer)
    };
  }

  async function verifyStableInjection(root, text, method, appended, initialComposer, existingLength, options = {}) {
    const documentRef = root?.ownerDocument || (typeof document !== "undefined" ? document : null);
    const timing = options.timing || {};
    const frame = timing.waitForFrame || (() => waitForFrame(documentRef));
    const delay = timing.wait || waitForDelay;
    const initialVerification = true;

    await frame();
    await delay(220);
    const verificationOne = verifyInsertedText(root, text);
    const verificationOnePassed = verificationOne.present && verificationOne.editable && verificationOne.focusable;
    if (!verificationOnePassed) {
      return {
        success: false,
        code: "composer_reconciled",
        method,
        appended,
        resultLength: text.length,
        existingLength,
        immediateVerification: initialVerification,
        verification1: false,
        verification2: false,
        diagnostics: composerDiagnostics(verificationOne.composer || initialComposer, {
          stage: "stable_verification",
          method,
          immediateVerification: initialVerification,
          verification1: false,
          verification2: false,
          focusable: verificationOne.focusable,
          resultLength: text.length,
          existingLength
        })
      };
    }

    await delay(320);
    const verificationTwo = verifyInsertedText(root, text);
    const verificationTwoPassed = verificationTwo.present && verificationTwo.editable && verificationTwo.focusable;
    const finalComposer = verificationTwo.composer || verificationOne.composer || initialComposer;
    const diagnostics = composerDiagnostics(finalComposer, {
      stage: "stable_verification",
      method,
      immediateVerification: initialVerification,
      verification1: verificationOnePassed,
      verification2: verificationTwoPassed,
      focusable: verificationTwo.focusable,
      resultLength: text.length,
      existingLength
    });
    if (!verificationTwoPassed) {
      return {
        success: false,
        code: "composer_reconciled",
        method,
        appended,
        resultLength: text.length,
        existingLength,
        immediateVerification: initialVerification,
        verification1: true,
        verification2: false,
        diagnostics
      };
    }

    return {
      success: true,
      method,
      appended,
      resultLength: text.length,
      existingLength,
      immediateVerification: initialVerification,
      verification1: true,
      verification2: true,
      diagnostics
    };
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

  async function injectComposerResult(result, root, options = {}) {
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

      if (!containsInsertedText(composer, text)) {
        return { success: false, code: "injection_failed", resultLength: text.length, existingLength: existing.length };
      }

      return verifyStableInjection(
        root,
        text,
        composer.id === "prompt-textarea" ? "prompt-textarea-value" : "textarea-value",
        Boolean(existing.trim()),
        composer,
        existing.length,
        options);
    }

    const documentRef = composer.ownerDocument || (typeof document !== "undefined" ? document : null);
    if (!documentRef?.createRange) return { success: false, code: "injection_failed" };

    const caret = placeCaretAtEnd(composer, documentRef);
    if (!caret.success) {
      return { success: false, code: "injection_failed" };
    }

    const insertion = existing.trim() ? `\n\n${text}` : text;
    let method = composer.id === "prompt-textarea" ? "prompt-textarea-contenteditable" : "contenteditable-execCommand";
    let inserted = false;
    try {
      inserted = documentRef.execCommand?.("insertText", false, insertion) === true;
    } catch (_) {
      inserted = false;
    }

    const immediatePresentAfterCommand = containsInsertedText(composer, text);
    inserted = inserted || immediatePresentAfterCommand;
    if (!immediatePresentAfterCommand && caret.hasBlock) {
      try {
        const fallbackRange = documentRef.createRange();
        fallbackRange.selectNodeContents(caret.target);
        fallbackRange.collapse(false);
        const textNode = documentRef.createTextNode(insertion);
        fallbackRange.insertNode(textNode);
        fallbackRange.setStartAfter(textNode);
        fallbackRange.collapse(true);
        caret.selection?.removeAllRanges?.();
        caret.selection?.addRange?.(fallbackRange);
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
    return verifyStableInjection(
      root,
      text,
      method,
      Boolean(existing.trim()),
      composer,
      existing.length,
      options);
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
