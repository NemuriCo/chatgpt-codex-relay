(function () {
  const utils = self.BlueRelayUtils;
  const $ = (id) => document.getElementById(id);
  let activeTab = null;
  let workstreams = [];
  const clipboardPermissions = {
    contains: (...args) => chrome.permissions.contains(...args),
    request: (...args) => chrome.permissions.request(...args)
  };
  const clipboardReader = utils.createClipboardReader(clipboardPermissions, navigator.clipboard);

  function call(message) {
    return new Promise((resolve) => chrome.runtime.sendMessage(message, resolve));
  }

  function show(id, visible) {
    $(id).classList.toggle("hidden", !visible);
  }

  function localize() {
    document.querySelectorAll("[data-i18n]").forEach((element) => {
      const value = chrome.i18n.getMessage(element.dataset.i18n);
      if (value) element.textContent = value;
    });
  }

  function text(key, fallback) {
    return chrome.i18n.getMessage(key) || fallback;
  }

  async function render() {
    localize();
    ["unsupported", "offline", "pairing", "connected"].forEach((id) => show(id, false));
    const health = await call({ type: "POPUP_HEALTH" });
    if (!health || !health.success) {
      show("offline", true);
      return;
    }

    const tabs = await chrome.tabs.query({ active: true, currentWindow: true });
    activeTab = tabs[0] || null;
    if (!activeTab || !utils.isSupportedChatGptUrl(activeTab.url || "")) {
      show("unsupported", true);
      return;
    }

    $("tabTitle").textContent = activeTab.title || activeTab.url;
    if (!health.data.paired) {
      show("pairing", true);
      return;
    }

    await clipboardReader.refreshPermission();
    const response = await call({ type: "POPUP_WORKSTREAMS" });
    if (!response || !response.success) {
      $("bindStatus").textContent = response?.message || text("unableList", "Unable to list Workstreams.");
      show("connected", true);
      return;
    }

    workstreams = response.data || [];
    const select = $("workstreamSelect");
    select.replaceChildren();
    workstreams.forEach((workstream) => {
      const option = document.createElement("option");
      option.value = workstream.workstreamId;
      option.textContent = utils.renderWorkstreamLabel(workstream);
      select.appendChild(option);
    });
    const bound = workstreams.find((item) => item.binding && item.binding.tabId === String(activeTab.id));
    if (bound) {
      select.value = bound.workstreamId;
      $("bindStatus").textContent = bound.binding.connected ? text("tabConnected", "ChatGPT tab connected") : text("tabDisconnected", "ChatGPT tab disconnected");
      $("workflowStatus").textContent = bound.currentState;
    }
    show("connected", true);
  }

  $("pairButton").addEventListener("click", async () => {
    const response = await call({ type: "POPUP_PAIR", pairingCode: $("pairingCode").value.trim() });
    if (!response || !response.success) {
      $("pairError").textContent = response?.message || text("pairFailed", "Pairing failed.");
      return;
    }
    await render();
  });

  $("bindButton").addEventListener("click", async () => {
    if (!activeTab) return;
    const response = await call({ type: "POPUP_BIND", tabId: activeTab.id, workstreamId: $("workstreamSelect").value });
    $("bindStatus").textContent = response && response.success ? text("tabBound", "ChatGPT tab bound") : (response?.message || text("bindingFailed", "Binding failed."));
    if (response && response.success) await render();
  });

  $("clipboardButton").addEventListener("click", async () => {
    const clipboardResult = await clipboardReader.readText();
    if (!clipboardResult.success) {
      const message = clipboardResult.reason === "permission-denied"
        ? text("clipboardPermissionRequired", "Clipboard read permission is required to use this fallback capture feature.")
        : text("clipboardFailed", "Clipboard capture failed.");
      $("captureStatus").textContent = message;
      return;
    }

    const response = await call({ type: "POPUP_CAPTURE_CLIPBOARD", tabId: activeTab && activeTab.id, prompt: clipboardResult.text });
    $("captureStatus").textContent = response && response.success ? text("taskCaptured", "Task captured") : (response?.message || text("clipboardFailed", "Clipboard capture failed."));
  });

  render().catch((error) => {
    show("offline", true);
    $("offline").querySelector("p").textContent = error.message;
  });
})();
