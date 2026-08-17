# BlueRelay

BlueRelay is a small Windows desktop relay for ChatGPT Web ↔ Codex development workflows. It keeps the current handoff state for each local project visible in a compact, always-on-top window so it is clear who should act next.

The repository is in early development. Phase 2 adds a local-only browser bridge between ChatGPT Web and BlueRelay. It still does not automate a real Codex session or use any cloud relay.

## Phase 2 MVP

The current MVP includes:

- A .NET 8 WPF Windows desktop application.
- A Fluent-style WPF UI built with WPF-UI 4.3.0, while retaining the custom WindowChrome shell.
- A compact dark Windows floating window with a native-style rounded shell, explicit drag header, no resize grip, edge snapping, and persisted placement.
- A collapsed 50px status mode for keeping the current workstream visible without covering the desktop.
- Multiple Projects, each with multiple independent Workstreams and workflow states.
- A default Workstream is created automatically for every new Project.
- Friendly workflow states for the ChatGPT ↔ Codex handoff.
- Manual state changes for testing the workflow.
- Project creation, editing, directory selection, validation, and record deletion in a secondary project-management view.
- Safe local JSON persistence at `%LocalAppData%\BlueRelay\state.json`.
- Corrupt-state backup and a user-visible recovery warning.
- A system tray menu for showing BlueRelay, toggling Always on top, and exiting; closing the window hides it to the tray.
- Windows 10/11 multi-monitor-aware placement, with the last position, collapsed state, and Always on top preference retained between launches.
- Lightweight `zh-CN` and `en-US` UI copy selected from Windows `CurrentUICulture`; unsupported cultures fall back to English.
- Asynchronous Git repository detection when a project folder is selected or refreshed, including repository-root discovery and remote-name suggestions.
- Thin `IBrowserBridge` and `ICodexBridge` interfaces for future integrations.
- A Chrome/Chromium Edge Manifest V3 extension in [`browser-extension`](browser-extension) with no npm or frontend build chain.
- A Kestrel Local Bridge bound only to `127.0.0.1:48917`, with versioned DTO endpoints and consistent error responses.
- One explicit browser-tab binding per Workstream, including installation id, tab id, URL, conversation id, title, heartbeat, and connection state.
- Pairing-code setup and a random long-lived local auth token stored in the user's BlueRelay state file; no token is committed to the repository.
- `# CODEX_TASK` capture, simulated Codex confirmation/result transitions, and a command queue that targets the original ChatGPT tab without pressing Send.

Deleting a project only removes BlueRelay's saved project and workstream records. It never deletes or changes the selected local directory, Git repository, branch, or files.

## Projects and Workstreams

The data model is:

```text
Project
├── Id / Name / LocalPath
└── Workstreams[]
    ├── Id / Name
    └── CurrentState
```

Workflow state belongs to a Workstream, so two workstreams in the same Git project can move independently. New projects receive a `Default` workstream (shown as `默认工作流` in the Chinese UI). Project management can add, rename, and delete workstreams; at least one workstream is always retained.

The workflow states are:

`Idle` → `ReadyForCodex` → `CodexRunning` → `ReadyForChatGPT` → `ChatGPTReviewing` → `Completed`

Any active stage can enter `Error`. Normal UI actions advance state automatically. Manual state changes and the simulated Codex result are developer-only actions under a Workstream `⋯` → `Debug` menu. The real Codex bridge is intentionally not implemented in Phase 2.

## Browser Extension and Local Bridge

The extension is a thin adapter for `https://chatgpt.com/*` and the legacy `https://chat.openai.com/*` host. Its popup handles pairing and Workstream binding; it does not maintain a second Project database. The service worker registers tabs, sends a low-frequency five-second heartbeat, captures marked tasks, polls for handoff commands, and routes each command by the exact installation id + tab id key.

The BlueRelay process hosts the Bridge through ASP.NET Core Kestrel on `127.0.0.1:48917`. It never listens on `0.0.0.0` or LAN interfaces. `/v1/health` is read-only and unauthenticated; all other reads/writes require the pairing token except the one-time `/v1/pair` exchange. CORS headers are returned only to `chrome-extension://` or `edge-extension://` origins, so ordinary ChatGPT page JavaScript cannot call the API cross-origin. Pairing codes expire after five minutes and are consumed once; resetting pairing invalidates existing extension tokens.

The bridge stores `BrowserBinding` and `RelayTask` separately from `Workstream`. Prompt and result content is not written to normal diagnostics. A closed or stale tab keeps its binding and task/result in local state, so the result is not lost; the user can reconnect the same tab or bind another tab before trying the handoff again.

Phase 2 does not claim that ChatGPT DOM capture has been verified against a real logged-in ChatGPT page. The content script uses semantic copy-button candidates and `pre`/`code`/message relationships, and its fallback copies an undelivered result to the clipboard when no composer can be found.

## Manual Extension Installation and Test

1. Start BlueRelay with `dotnet run --project src/BlueRelay/BlueRelay.csproj`.
2. Open Chrome or Edge → Extensions, enable Developer mode, and choose **Load unpacked**.
3. Select the repository's `browser-extension` directory.
4. In BlueRelay, open Header `…` → Project management and copy the five-minute pairing code.
5. Open the extension popup on a ChatGPT tab, enter the code, choose a `Project / Workstream`, and click **Bind**.
6. Copy a ChatGPT message containing `# CODEX_TASK`; BlueRelay should show `ReadyForCodex` for only the bound Workstream.
7. Click **Confirm send**, then use Workstream `⋯` → **Simulate Codex result** and save a result.
8. Click **Send back to ChatGPT**. The extension activates the original tab and fills the composer without sending; inspect and press ChatGPT's Send button yourself.

The real Chrome + ChatGPT DOM flow, composer behavior, and visual layout must be accepted by a user on a logged-in browser. BlueRelay's automated tests cover the bridge contracts and pure extension helpers, not a live ChatGPT session.

## Git folder detection

When a folder is selected in project management, BlueRelay asynchronously runs Git commands for that folder. It uses `git rev-parse --show-toplevel` to find the repository root and then tries `git remote get-url origin` for a suggested project name. HTTPS and SSH remote URLs are supported. If the selected folder is a repository subdirectory, the UI shows the repository root and offers `Use repository root`; it does not silently hide that choice.

If Git is unavailable, the project remains usable. BlueRelay falls back to the selected folder and its directory name without showing a fatal error. Git detection only runs after folder selection or when the user presses `Refresh Git info`.

When an older Phase 1 `state.json` contains `Project.CurrentState`, startup migrates that value into a new default Workstream, preserves project selection and future binding fields, and saves schema version 2. The old JSON is not treated as corrupt.

## Project structure

```text
src/BlueRelay/
├── Models/          Projects, workstreams, and workflow presentation
├── Persistence/     Local JSON state storage and recovery
├── Services/        Project/workstream operations, state machine, Git detection, tray, and browser bridge
├── Presentation/    Thin MVVM helpers, view models, and converters
├── App.xaml         Application resources and startup
└── MainWindow.xaml  Compact floating window
tests/BlueRelay.Tests/
└──                 State machine and persistence tests

browser-extension/
├── manifest.json
├── background/      MV3 service worker and localhost routing
├── content-script.js ChatGPT DOM capture and composer injection
├── popup/           Pairing/binding popup and dark styling
├── shared/          Pure helpers and bridge client
└── _locales/        English and Simplified Chinese extension copy
```

## Fluent UI attribution

BlueRelay uses [WPF UI](https://github.com/lepoco/wpfui) 4.3.0 under the MIT License for its dark theme, Fluent controls, and Fluent System Icons. The required license and third-party notices are included in [`THIRD-PARTY-NOTICES`](THIRD-PARTY-NOTICES).

The unified BlueRelay application icon reuses the same `Flow20` / Fluent System Icons `Flow 20 Regular` path shown in the empty workflow state. Windows and the browser extension derive their icon assets from [`BlueRelay-flow-20-regular.svg`](src/BlueRelay/Assets/Icons/BlueRelay-flow-20-regular.svg); the existing Fluent System Icons MIT notice above covers this bundled asset.

## Running locally

Requirements:

- Windows 10 or later
- .NET 8 SDK

From the repository root:

```powershell
dotnet restore
dotnet build
dotnet run --project src/BlueRelay/BlueRelay.csproj
dotnet test .\BlueRelay.sln
```

Run `dotnet run --project src/BlueRelay/BlueRelay.csproj` to start the desktop app. The first launch creates the local state file. Closing the floating window hides BlueRelay to the system tray; choose `Exit` from the tray menu to stop the application. Use the header `…` button to add or edit project records; the main view stays focused on workflow status and the next action.

Phase 2 stores project/workstream metadata, browser bindings, and the current relay task/result locally. It connects only to a paired local browser extension; it still has no real Codex automation and no BlueRelay cloud service. The current window layout should be visually checked on a real Windows desktop, especially with multiple monitors and different display scaling.

## Roadmap

- Real Codex integration
- Codex session lifecycle and result streaming
- Rich task history
- Multiple ChatGPT/Codex sessions per project

## License

BlueRelay is released under the MIT License. See [LICENSE](LICENSE).
