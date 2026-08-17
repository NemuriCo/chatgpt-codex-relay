(function (root, factory) {
  if (typeof module === "object" && module.exports) {
    module.exports = factory();
  } else {
    root.BlueRelayRuntimeLifecycle = factory();
  }
})(typeof self !== "undefined" ? self : this, function () {
  const CONTEXT_INVALIDATED_CODE = "extension_context_invalidated";

  function errorText(error) {
    if (!error) return "";
    if (typeof error === "string") return error;
    return String(error.message || error);
  }

  function classifyRuntimeMessageFailure(error) {
    const message = errorText(error);
    return /extension context invalidated|context invalidated|chrome\.runtime\.id.*(?:unavailable|undefined)|runtime id.*(?:unavailable|undefined)/i.test(message)
      ? CONTEXT_INVALIDATED_CODE
      : "runtime_message_failed";
  }

  function createRuntimeState({ clearIntervalImpl, onInvalidated, onStop } = {}) {
    const clear = typeof clearIntervalImpl === "function"
      ? clearIntervalImpl
      : (typeof clearInterval === "function" ? clearInterval : () => {});
    let alive = true;
    let stopped = false;
    let heartbeatHandle = null;

    function clearHeartbeat() {
      if (heartbeatHandle !== null && heartbeatHandle !== undefined) {
        try { clear(heartbeatHandle); } catch (_) { /* stale contexts may reject cleanup */ }
        heartbeatHandle = null;
      }
    }

    function stop(invalidated) {
      if (stopped) return;
      stopped = true;
      alive = false;
      clearHeartbeat();
      if (invalidated) onInvalidated?.();
      onStop?.();
    }

    return {
      isAlive: () => alive,
      setHeartbeatHandle(handle) {
        if (!alive) {
          try { clear(handle); } catch (_) { /* no-op */ }
          return;
        }
        clearHeartbeat();
        heartbeatHandle = handle;
      },
      invalidate: () => stop(true),
      stop: () => stop(false)
    };
  }

  function createRuntimeSender(runtime, state) {
    function failure(error) {
      const code = classifyRuntimeMessageFailure(error);
      if (code === CONTEXT_INVALIDATED_CODE) state.invalidate();
      return { success: false, code };
    }

    return function sendRuntimeMessage(message) {
      if (!state.isAlive()) {
        return Promise.resolve({ success: false, code: CONTEXT_INVALIDATED_CODE });
      }

      try {
        if (!runtime || !runtime.id) {
          return Promise.resolve(failure({ message: "chrome.runtime.id unavailable" }));
        }
      } catch (error) {
        return Promise.resolve(failure(error));
      }

      return new Promise((resolve) => {
        let settled = false;
        const finish = (response) => {
          if (settled) return;
          settled = true;
          resolve(response);
        };

        const callback = (response) => {
          let lastError = null;
          try { lastError = runtime.lastError; } catch (error) { lastError = error; }
          finish(lastError ? failure(lastError) : response);
        };

        try {
          const result = runtime.sendMessage(message, callback);
          if (result && typeof result.then === "function") {
            result.then(finish, (error) => finish(failure(error)));
          }
        } catch (error) {
          finish(failure(error));
        }
      });
    };
  }

  function isSameRuntimeContext(marker, runtime) {
    return Boolean(marker && marker.runtime === runtime && marker.active === true && marker.isAlive?.() !== false && marker.isRuntimeUsable?.() !== false);
  }

  function isLiveContentScriptResponse(response) {
    return Boolean(response && response.success === true && response.contextAlive === true);
  }

  function createContentScriptEnsurer({ isSupportedUrl, ping, inject, log, files = [] } = {}) {
    const inFlight = new Map();

    function writeLog(tab, stage, code) {
      log?.(tab, stage, code);
    }

    async function run(tab) {
      if (!tab || tab.id === undefined) return { success: false, code: "invalid_tab" };
      if (!isSupportedUrl?.(tab.url || "")) return { success: false, code: "unsupported_origin" };

      let pingResult;
      try {
        pingResult = await ping(tab);
      } catch (error) {
        pingResult = { success: false, code: classifyRuntimeMessageFailure(error) };
      }
      if (pingResult?.success) {
        writeLog(tab, "ping", pingResult.code || "alive");
        writeLog(tab, "ready", "already_ready");
        return { success: true, injected: false, code: "already_ready" };
      }

      writeLog(tab, "ping", pingResult?.code || "ping_no_response");
      if (typeof inject !== "function") {
        writeLog(tab, "inject", "scripting_unavailable");
        return { success: false, code: "scripting_unavailable" };
      }

      try {
        writeLog(tab, "inject", pingResult?.code || "ping_no_response");
        await inject(tab, files);
      } catch (error) {
        const code = error?.code || classifyRuntimeMessageFailure(error);
        writeLog(tab, "inject", code);
        return { success: false, code };
      }

      let readyResult;
      try {
        readyResult = await ping(tab);
      } catch (error) {
        readyResult = { success: false, code: classifyRuntimeMessageFailure(error) };
      }
      if (!readyResult?.success) {
        const code = readyResult?.code || "content_script_not_ready";
        writeLog(tab, "ready", code);
        return { success: false, injected: true, code };
      }

      writeLog(tab, "ready", "injected");
      return { success: true, injected: true, code: "injected" };
    }

    return function ensureContentScript(tab) {
      if (!tab || tab.id === undefined || !isSupportedUrl?.(tab.url || "")) {
        return Promise.resolve({ success: false, code: tab?.id === undefined ? "invalid_tab" : "unsupported_origin" });
      }

      const tabKey = String(tab.id);
      const existing = inFlight.get(tabKey);
      if (existing) return existing;

      const pending = run(tab).finally(() => {
        if (inFlight.get(tabKey) === pending) inFlight.delete(tabKey);
      });
      inFlight.set(tabKey, pending);
      return pending;
    };
  }

  return {
    CONTEXT_INVALIDATED_CODE,
    classifyRuntimeMessageFailure,
    createRuntimeState,
    createRuntimeSender,
    isSameRuntimeContext,
    isLiveContentScriptResponse,
    createContentScriptEnsurer
  };
});
