importScripts("../shared/utils.js", "../shared/runtime-lifecycle.js", "../shared/bridge-client.js");

const utils = self.BlueRelayUtils;
const lifecycleUtils = self.BlueRelayRuntimeLifecycle;
const bridge = self.BlueRelayBridgeClient;
const CONTENT_SCRIPT_FILES = ["shared/utils.js", "shared/runtime-lifecycle.js", "content-script.js"];

function tabPayload(tab) {
  return {
    installationId: null,
    tabId: String(tab.id),
    chatGPTUrl: tab.url || "",
    chatGPTConversationId: utils.extractConversationId(tab.url || ""),
    pageTitle: tab.title || "ChatGPT"
  };
}

async function ensureRegistered(tab) {
  if (!tab || !utils.isSupportedChatGptUrl(tab.url || "")) return null;
  const config = await bridge.getConfig();
  const payload = tabPayload(tab);
  payload.installationId = config.installationId;
  await bridge.registerTab(payload);
  return { config, payload };
}

function lifecycleLog(tab, stage, code) {
  console.info("[BlueRelay] content-script lifecycle", {
    tabId: tab?.id === undefined ? null : String(tab.id),
    stage,
    code: code || "unknown"
  });
}

function runtimeFailureCode(error, fallback = "runtime_message_failed") {
  return lifecycleUtils?.classifyRuntimeMessageFailure
    ? lifecycleUtils.classifyRuntimeMessageFailure(error)
    : (error?.code || fallback);
}

async function pingContentScript(tab) {
  try {
    const response = await chrome.tabs.sendMessage(tab.id, { type: "BLUERELAY_PING" });
    if (lifecycleUtils.isLiveContentScriptResponse(response)) {
      return { success: true, response, code: "alive" };
    }

    return { success: false, code: response?.code || "ping_no_response" };
  } catch (error) {
    return { success: false, code: runtimeFailureCode(error, "ping_failed") };
  }
}

const ensureContentScript = lifecycleUtils.createContentScriptEnsurer({
  isSupportedUrl: (url) => utils.isSupportedChatGptUrl(url),
  ping: pingContentScript,
  inject: (tab, files) => {
    if (!chrome.scripting?.executeScript) return Promise.reject({ code: "scripting_unavailable" });
    return chrome.scripting.executeScript({ target: { tabId: tab.id }, files });
  },
  log: lifecycleLog,
  files: CONTENT_SCRIPT_FILES
});

async function recoverTab(tab) {
  const result = await ensureContentScript(tab);
  if (result.success && !result.injected) {
    await registerAndPoll(tab);
  }
  return result;
}

async function ensureOpenChatGptTabs() {
  try {
    const tabs = await chrome.tabs.query({});
    await Promise.all(tabs
      .filter((tab) => utils.isSupportedChatGptUrl(tab.url || ""))
      .map((tab) => recoverTab(tab).catch((error) => {
        lifecycleLog(tab, "ready", error?.code || "recovery_failed");
      })));
  } catch (error) {
    lifecycleLog(null, "ready", error?.code || "tab_query_failed");
  }
}

function deliveryLog(stage, command, code) {
  console.warn("[BlueRelay] result delivery", {
    commandId: command?.commandId || null,
    tabId: command?.tabId || null,
    stage,
    code: code || "unknown"
  });
}

function errorCode(error, fallback) {
  return error?.code || fallback;
}

async function acknowledgeCommand(command, success, code, method) {
  try {
    await bridge.acknowledge(command.commandId, success, code, method);
    return true;
  } catch (error) {
    deliveryLog("ack_api_failed", command, errorCode(error, "ack_api_failed"));
    return false;
  }
}

async function pollCommand(tab, config, payload) {
  let command;
  try {
    command = await bridge.nextCommand(config.installationId, payload.tabId);
  } catch (error) {
    deliveryLog("next_command_failed", { tabId: payload.tabId }, errorCode(error, "next_command_failed"));
    return;
  }

  if (!command) return;

  let response;
  try {
    response = await chrome.tabs.sendMessage(tab.id, { type: "INJECT_RESULT", command });
  } catch (error) {
    const code = errorCode(error, "content_script_unavailable");
    deliveryLog("content_script_unavailable", command, code);
    await acknowledgeCommand(command, false, code);
    return;
  }

  if (!response || response.success !== true) {
    const code = response?.code || "injection_failed";
    const stage = code === "composer_not_found"
      ? "composer_not_found"
      : code === "composer_reconciled"
        ? "composer_reconciled"
        : "composer_injection_failed";
    deliveryLog(stage, command, code);
    const acknowledgementCode = code || response?.fallbackCode || "injection_failed";
    await acknowledgeCommand(command, false, acknowledgementCode, response?.method);
    return;
  }

  const acknowledged = await acknowledgeCommand(command, true, null, response.method);
  if (!acknowledged) {
    deliveryLog("ack_api_failed", command, "ack_api_failed");
  }
}

async function registerAndPoll(tab) {
  try {
    const registration = await ensureRegistered(tab);
    if (registration) await pollCommand(tab, registration.config, registration.payload);
  } catch (error) {
    console.warn("[BlueRelay] tab registration failed", {
      tabId: tab?.id ? String(tab.id) : null,
      stage: "registration",
      code: errorCode(error, "registration_failed")
    });
  }
}

async function handleContentMessage(message, sender) {
  const tab = sender.tab || (message.type === "POPUP_CAPTURE_CLIPBOARD" ? await chrome.tabs.get(message.tabId) : null);
  if (!tab || !utils.isSupportedChatGptUrl(tab.url || "")) return { success: false, code: "unsupported_origin" };
  const config = await bridge.getConfig();
  const payload = tabPayload(tab);
  payload.installationId = config.installationId;

  if (message.type === "TAB_HELLO" || message.type === "TAB_HEARTBEAT") {
    if (message.type === "TAB_HELLO") await bridge.registerTab(payload);
    else await bridge.heartbeat(payload);
    await pollCommand(tab, config, payload);
    return { success: true };
  }

  if (message.type === "CAPTURE_TASK") {
    const taskText = utils.normalizeTaskText(message.prompt);
    if (!utils.detectCodexTask(taskText)) return { success: false, code: "not_a_codex_task" };
    const task = await bridge.captureTask({
      installationId: config.installationId,
      tabId: payload.tabId,
      prompt: taskText,
      chatGPTUrl: payload.chatGPTUrl,
      chatGPTConversationId: payload.chatGPTConversationId,
      pageTitle: payload.pageTitle
    });
    return { success: true, task };
  }

  if (message.type === "POPUP_CAPTURE_CLIPBOARD") {
    const taskText = utils.normalizeTaskText(message.prompt);
    if (!utils.detectCodexTask(taskText)) return { success: false, code: "not_a_codex_task", message: "The clipboard text does not contain # CODEX_TASK." };
    const task = await bridge.captureTask({
      installationId: config.installationId,
      tabId: String(tab.id),
      prompt: taskText,
      chatGPTUrl: payload.chatGPTUrl,
      chatGPTConversationId: payload.chatGPTConversationId,
      pageTitle: payload.pageTitle
    });
    return { success: true, task };
  }

  return { success: false, code: "unknown_message" };
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message.type === "POPUP_ENSURE_TAB") {
    (async () => {
      const tab = await chrome.tabs.get(message.tabId);
      const result = await ensureContentScript(tab);
      if (result.success && !result.injected) {
        await registerAndPoll(tab);
      }
      sendResponse(result);
    })().catch((error) => sendResponse({ success: false, code: errorCode(error, "tab_recovery_failed"), message: error.message }));
    return true;
  }

  if (message.type === "POPUP_HEALTH") {
    bridge.health().then((data) => sendResponse({ success: true, data })).catch((error) => sendResponse({ success: false, message: error.message, code: error.code }));
    return true;
  }

  if (message.type === "POPUP_PAIR") {
    bridge.getConfig().then((config) => bridge.pair(message.pairingCode, config.installationId)).then((data) => sendResponse({ success: true, data })).catch((error) => sendResponse({ success: false, message: error.message, code: error.code }));
    return true;
  }

  if (message.type === "POPUP_WORKSTREAMS") {
    bridge.workstreams().then((data) => sendResponse({ success: true, data })).catch((error) => sendResponse({ success: false, message: error.message, code: error.code }));
    return true;
  }

  if (message.type === "POPUP_BIND") {
    (async () => {
      const tab = await chrome.tabs.get(message.tabId);
      const recovery = await ensureContentScript(tab);
      if (!recovery.success) {
        sendResponse({ success: false, code: recovery.code, message: recovery.code === "unsupported_origin" ? "The current page is not a supported ChatGPT page." : "Unable to reconnect the ChatGPT tab." });
        return;
      }
      const registration = await ensureRegistered(tab);
      const data = await bridge.bindTab({ installationId: registration.config.installationId, tabId: String(tab.id), workstreamId: message.workstreamId, rebind: Boolean(message.rebind) });
      sendResponse({ success: true, data });
    })().catch((error) => sendResponse({ success: false, message: error.message, code: error.code }));
    return true;
  }

  handleContentMessage(message, sender).then(sendResponse).catch((error) => sendResponse({ success: false, message: error.message, code: error.code }));
  return true;
});

chrome.runtime.onInstalled.addListener(() => {
  bridge.getConfig().catch(() => undefined);
  void ensureOpenChatGptTabs();
});

chrome.runtime.onStartup?.addListener(() => {
  void ensureOpenChatGptTabs();
});

chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  const candidateUrl = changeInfo.url || tab.url || "";
  if ((changeInfo.status === "complete" || changeInfo.url) && utils.isSupportedChatGptUrl(candidateUrl)) {
    void recoverTab(Object.assign({}, tab, { id: tabId, url: candidateUrl }));
  }
});

void ensureOpenChatGptTabs();
