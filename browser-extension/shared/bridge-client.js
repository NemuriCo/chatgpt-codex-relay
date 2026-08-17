(function (root) {
  const BASE_URL = "http://127.0.0.1:48917/v1";
  const CONFIG_KEY = "blueRelayBridge";

  function createInstallationId() {
    if (crypto.randomUUID) return crypto.randomUUID();
    const bytes = new Uint8Array(16);
    crypto.getRandomValues(bytes);
    return Array.from(bytes, (value) => value.toString(16).padStart(2, "0")).join("");
  }

  async function getConfig() {
    const stored = await chrome.storage.local.get(CONFIG_KEY);
    const config = stored[CONFIG_KEY] || {};
    if (!config.installationId) {
      config.installationId = createInstallationId();
      await chrome.storage.local.set({ [CONFIG_KEY]: config });
    }
    return config;
  }

  async function setToken(token) {
    const config = await getConfig();
    config.token = token;
    await chrome.storage.local.set({ [CONFIG_KEY]: config });
    return config;
  }

  async function request(path, options) {
    const config = await getConfig();
    const headers = Object.assign({ "Content-Type": "application/json" }, (options && options.headers) || {});
    if (config.token) headers["X-BlueRelay-Token"] = config.token;
    const response = await fetch(`${BASE_URL}${path}`, Object.assign({}, options || {}, { headers }));
    let payload = null;
    try { payload = await response.json(); } catch (_) { /* empty response */ }
    if (!response.ok) {
      const error = payload && payload.error ? payload.error : { code: "network_error", message: `BlueRelay returned ${response.status}.` };
      const exception = new Error(error.message);
      exception.code = error.code;
      exception.status = response.status;
      throw exception;
    }
    return payload ? payload.data : null;
  }

  async function post(path, body) {
    return request(path, { method: "POST", body: JSON.stringify(body || {}) });
  }

  root.BlueRelayBridgeClient = {
    BASE_URL,
    CONFIG_KEY,
    getConfig,
    setToken,
    health: () => request("/health"),
    pair: async (pairingCode, installationId) => {
      const data = await post("/pair", { pairingCode, installationId });
      await setToken(data.token);
      return data;
    },
    workstreams: () => request("/workstreams"),
    registerTab: (payload) => post("/tabs/register", payload),
    heartbeat: (payload) => post("/tabs/heartbeat", payload),
    bindTab: (payload) => post("/tabs/bind", payload),
    captureTask: (payload) => post("/tasks/capture", payload),
    nextCommand: (installationId, tabId) => request(`/commands/next?installationId=${encodeURIComponent(installationId)}&tabId=${encodeURIComponent(tabId)}`),
    acknowledge: (commandId, success) => post(`/commands/${encodeURIComponent(commandId)}/ack`, { success })
  };
})(typeof self !== "undefined" ? self : window);
