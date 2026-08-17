importScripts("../shared/utils.js", "../shared/bridge-client.js");

const utils = self.BlueRelayUtils;
const bridge = self.BlueRelayBridgeClient;

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

async function pollCommand(tab, config, payload) {
  try {
    const command = await bridge.nextCommand(config.installationId, payload.tabId);
    if (!command) return;
    const response = await chrome.tabs.sendMessage(tab.id, { type: "INJECT_RESULT", command });
    await bridge.acknowledge(command.commandId, Boolean(response && response.success));
  } catch (_) {
    // A closed tab or a missing content script is expected during tab lifecycle changes.
  }
}

async function registerAndPoll(tab) {
  try {
    const registration = await ensureRegistered(tab);
    if (registration) await pollCommand(tab, registration.config, registration.payload);
  } catch (_) {
    // The popup exposes the actionable connection error; background heartbeats stay quiet.
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
      const registration = await ensureRegistered(tab);
      const data = await bridge.bindTab({ installationId: registration.config.installationId, tabId: String(tab.id), workstreamId: message.workstreamId });
      sendResponse({ success: true, data });
    })().catch((error) => sendResponse({ success: false, message: error.message, code: error.code }));
    return true;
  }

  handleContentMessage(message, sender).then(sendResponse).catch((error) => sendResponse({ success: false, message: error.message, code: error.code }));
  return true;
});

chrome.runtime.onInstalled.addListener(() => bridge.getConfig().catch(() => undefined));
