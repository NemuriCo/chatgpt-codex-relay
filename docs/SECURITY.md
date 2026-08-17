# BlueRelay local bridge security

BlueRelay's Phase 2 bridge is intentionally local-only:

- Kestrel listens on `127.0.0.1:48917` and never binds `0.0.0.0` or a LAN address.
- `/v1/pair` accepts a six-digit code generated in the BlueRelay UI. The code expires after five minutes and is consumed after one successful exchange.
- Pairing issues a random long-lived token. The token is stored in the user's local `%LocalAppData%\BlueRelay\state.json` and in the browser extension's `chrome.storage.local`; it is not hard-coded, logged, or committed.
- Every read/write endpoint except health and the one-time pairing exchange requires the token. Resetting pairing clears the token and paired installation ids.
- CORS is limited to extension origins (`chrome-extension://` and `edge-extension://`). A normal ChatGPT page cannot make an authorized cross-origin request using page JavaScript.
- Browser bindings use the explicit `installationId + tabId` key. A page title, active tab, URL, or Project name is never used as the unique routing key.
- Prompt and result bodies are retained in local state only as current task data. Startup diagnostics record ids and operational failures, never tokens or full prompt/result text.

This protects the local bridge from accidental webpage access; it is not a claim that a compromised local process or a user-controlled browser extension is trusted. BlueRelay does not add a cloud server, account system, or extra cloud transfer. ChatGPT and other web services still have their own network behavior.
