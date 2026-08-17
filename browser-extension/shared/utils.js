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

  function findComposer(root) {
    const scope = root || (typeof document !== "undefined" ? document : null);
    if (!scope || !scope.querySelectorAll) return null;
    const candidates = Array.from(scope.querySelectorAll("textarea, [contenteditable=\"true\"], [role=\"textbox\"]"));
    return candidates.find((element) => {
      const rect = element.getBoundingClientRect ? element.getBoundingClientRect() : { width: 0, height: 0 };
      return !element.disabled && rect.width > 0 && rect.height > 0;
    }) || candidates.find((element) => !element.disabled) || null;
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
    createClipboardReader
  };
});
