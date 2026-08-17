(function () {
  const utils = self.BlueRelayUtils;
  const $ = (id) => document.getElementById(id);
  let activeTab = null;
  let workstreams = [];
  let rebindRequired = false;
  const clipboardPermissions = {
    contains: (...args) => chrome.permissions.contains(...args),
    request: (...args) => chrome.permissions.request(...args)
  };
  const clipboardReader = utils.createClipboardReader(clipboardPermissions, navigator.clipboard);

  function call(message) {
    return new Promise((resolve) => {
      let settled = false;
      const finish = (response) => {
        if (settled) return;
        settled = true;
        resolve(response);
      };
      try {
        const result = chrome.runtime.sendMessage(message, (response) => {
          const lastError = chrome.runtime.lastError;
          finish(lastError ? { success: false, code: "runtime_message_failed", message: lastError.message } : response);
        });
        if (result && typeof result.then === "function") result.then(finish, (error) => finish({ success: false, code: "runtime_message_failed", message: error.message }));
      } catch (error) {
        finish({ success: false, code: "runtime_message_failed", message: error.message });
      }
    });
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

  function setTabBridgeState(state) {
    const status = $("tabBridgeStatus");
    const label = $("tabBridgeText");
    const messageKey = state === "connected" ? "tabConnected" : state === "failed" ? "tabBridgeFailed" : "tabBridgeRecovering";
    status.dataset.state = state;
    label.textContent = text(messageKey, state === "connected" ? "ChatGPT tab connected" : state === "failed" ? "ChatGPT tab bridge failed" : "ChatGPT tab bridge is reconnecting");
    show("reconnectButton", state === "failed");
  }

  async function render() {
    localize();
    ["unsupported", "offline", "pairing", "connected"].forEach((id) => show(id, false));
    show("rebindButton", false);
    rebindRequired = false;
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

    const tabTitle = activeTab.title || activeTab.url;
    $("tabTitle").textContent = tabTitle;
    $("tabTitle").title = tabTitle;
    if (!health.data.paired) {
      show("pairing", true);
      return;
    }

    setTabBridgeState("recovering");
    const recovery = await call({ type: "POPUP_ENSURE_TAB", tabId: activeTab.id });
    setTabBridgeState(recovery && recovery.success ? "connected" : "failed");

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
      const binding = bound.binding;
      $("bindStatus").textContent = binding.conversationMismatch
        ? text("conversationChanged", "This tab is on a different ChatGPT conversation. Rebind it explicitly to continue.")
        : binding.connected ? text("tabConnected", "ChatGPT tab connected") : text("tabDisconnected", "ChatGPT tab disconnected");
      if (binding.conversationMismatch) {
        rebindRequired = true;
        show("rebindButton", true);
      }
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
    const response = await call({ type: "POPUP_BIND", tabId: activeTab.id, workstreamId: $("workstreamSelect").value, rebind: false });
    $("bindStatus").textContent = response && response.success ? text("tabBound", "ChatGPT tab bound") : (response?.message || text("bindingFailed", "Binding failed."));
    if (!response?.success && ["conversation_mismatch", "workstream_already_bound", "tab_already_bound"].includes(response?.code)) {
      rebindRequired = true;
      show("rebindButton", true);
      $("bindStatus").textContent = text("explicitRebindRequired", "This pairing is already in use. Rebind explicitly to use the current tab and conversation.");
    }
    if (response && response.success) await render();
  });

  $("rebindButton").addEventListener("click", async () => {
    if (!activeTab || !rebindRequired) return;
    const response = await call({ type: "POPUP_BIND", tabId: activeTab.id, workstreamId: $("workstreamSelect").value, rebind: true });
    $("bindStatus").textContent = response && response.success ? text("tabRebound", "Current ChatGPT conversation rebound") : (response?.message || text("bindingFailed", "Binding failed."));
    if (response && response.success) await render();
  });

  $("reconnectButton").addEventListener("click", async () => {
    if (!activeTab) return;
    setTabBridgeState("recovering");
    const recovery = await call({ type: "POPUP_ENSURE_TAB", tabId: activeTab.id });
    if (recovery && recovery.success) {
      await render();
    } else {
      setTabBridgeState("failed");
    }
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
