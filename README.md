# BlueRelay

BlueRelay is a small Windows desktop relay for ChatGPT Web ↔ Codex development workflows. It keeps the current handoff state for each local project visible in a compact, always-on-top window so it is clear who should act next.

The repository is in early development. Phase 1 focuses on making the local project tracker useful and structurally ready for future browser and Codex integrations. It does not connect to ChatGPT Web, Codex, or any cloud service yet.

## Phase 1 MVP

The current MVP includes:

- A .NET 8 WPF Windows desktop application.
- A compact dark floating window with drag support and edge snapping.
- Multiple independent project records.
- Friendly workflow states for the ChatGPT ↔ Codex handoff.
- Manual state changes for testing the workflow.
- Project creation, editing, directory selection, validation, and record deletion.
- Safe local JSON persistence at `%LocalAppData%\BlueRelay\state.json`.
- Corrupt-state backup and a user-visible recovery warning.
- A basic system tray menu for showing BlueRelay, toggling Always on top, and exiting.
- Thin `IBrowserBridge` and `ICodexBridge` interfaces for future integrations.

Deleting a project only removes BlueRelay's saved record. It never deletes or changes the selected local directory.

## Workflow states

The application stores the state on each project rather than globally:

`Idle` → `ReadyForCodex` → `CodexRunning` → `ReadyForChatGPT` → `ChatGPTReviewing` → `Completed`

Any active stage can enter `Error`. The UI exposes a deliberate manual override so the MVP can be exercised before browser or Codex communication exists. Normal transitions remain centralized in `WorkflowStateMachine`.

## Project structure

```text
src/BlueRelay/
├── Models/          Domain data and workflow presentation
├── Persistence/     Local JSON state storage and recovery
├── Services/        Project operations, state machine, tray, and future bridges
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
dotnet test
```

Closing the window hides BlueRelay to the system tray. Choose `Exit` from the tray menu to stop the application.

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
