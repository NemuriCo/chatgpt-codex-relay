# BlueRelay

BlueRelay is a small Windows desktop relay for ChatGPT Web ↔ Codex development workflows. It keeps the current handoff state for each local project visible in a compact, always-on-top window so it is clear who should act next.

The repository is in early development. Phase 1.5 focuses on a useful local project/workstream tracker and preparation for future browser and Codex integrations. It does not connect to ChatGPT Web, Codex, or any cloud service yet.

## Phase 1.5 MVP

The current MVP includes:

- A .NET 8 WPF Windows desktop application.
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

Any active stage can enter `Error`. The UI exposes a deliberate manual override so the MVP can be exercised before browser or Codex communication exists. Normal transitions remain centralized in `WorkflowStateMachine`.

## Git folder detection

When a folder is selected in project management, BlueRelay asynchronously runs Git commands for that folder. It uses `git rev-parse --show-toplevel` to find the repository root and then tries `git remote get-url origin` for a suggested project name. HTTPS and SSH remote URLs are supported. If the selected folder is a repository subdirectory, the UI shows the repository root and offers `Use repository root`; it does not silently hide that choice.

If Git is unavailable, the project remains usable. BlueRelay falls back to the selected folder and its directory name without showing a fatal error. Git detection only runs after folder selection or when the user presses `Refresh Git info`.

When an older Phase 1 `state.json` contains `Project.CurrentState`, startup migrates that value into a new default Workstream, preserves project selection and future binding fields, and saves schema version 2. The old JSON is not treated as corrupt.

## Project structure

```text
src/BlueRelay/
├── Models/          Projects, workstreams, and workflow presentation
├── Persistence/     Local JSON state storage and recovery
├── Services/        Project/workstream operations, state machine, Git detection, tray, and future bridges
├── Presentation/    Thin MVVM helpers, view models, and converters
├── App.xaml         Application resources and startup
└── MainWindow.xaml  Compact floating window
tests/BlueRelay.Tests/
└──                 State machine and persistence tests
```

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

Run `dotnet run --project src/BlueRelay/BlueRelay.csproj` to start the desktop app. The first launch creates the local state file. Closing the floating window hides BlueRelay to the system tray; choose `Exit` from the tray menu to stop the application. The project-management button in the header is the place to add or edit project records, while the main view stays focused on workflow status and the next action.

Phase 1.5 stores local project/workstream metadata and workflow status only. It does not yet connect to ChatGPT Web, Codex, a browser extension, or any cloud service. The current window layout should be visually checked on a real Windows desktop, especially with multiple monitors and different display scaling.

## Roadmap

- ChatGPT browser extension
- ChatGPT tab or conversation binding
- Prompt capture
- Codex integration
- Result handoff
- Automatic workflow state tracking
- Multiple ChatGPT/Codex sessions per project

## License

BlueRelay is released under the MIT License. See [LICENSE](LICENSE).
