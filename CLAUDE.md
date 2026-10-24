# CLAUDE.md

NeuroScholAR: a Unity 6 AR "memory palace" learning app (XREAL glasses / Android) that adapts to
focus measured by a BrainBit 2 EEG headband. Works without hardware (simulator / no-EEG mode).

- `README.md`: architecture, setup, device swap, **where to tune thresholds**. Read it first.
- `SPECIFICATION.md`: the original task spec and definition of done. Scope limits (no cross-session
  storage, ML models, gamification, full SM-2) come from here; don't build those.

## Layout

| Path | What |
|---|---|
| `unity/Assets/NeuroScholAR/Core/` | All logic (DSP, focus, adaptive rules, palace game, session, import, export). `noEngineReferences`: **no `UnityEngine` here.** |
| `unity/Assets/NeuroScholAR/App/` | MonoBehaviour wiring + views only. No logic beyond wiring. |
| `unity/Assets/NeuroScholAR/Editor/ProjectSetup.cs` | Menu "Setup Project" / "Build Android APK"; scene generation, player/XR settings, `NEUROSDK2` define. |
| `dotnet/` | Compiles `Core/**/*.cs` directly (glob in `NeuroScholAR.Core.csproj`) + NUnit tests. |
| `backend/` | FastAPI + SQLite, `POST /sessions`, `GET /sessions/{id}/export.csv`. Single file `app/main.py`. |
| `samples/` | Example study sets and `appconfig.example.json`. |
| `vendor/` | XREAL tarball (git-ignored, 248 MB, referenced from `unity/Packages/manifest.json`). |

## Commands

```bash
cd dotnet && dotnet test                         # all Core tests incl. scripted end-to-end run
cd backend && .venv/Scripts/python -m pytest     # backend tests
cd backend && .venv/Scripts/ruff check . && .venv/Scripts/black --check .
```

Unity headless build (needs Unity Hub open and signed in, or it fails with a license error):
`"D:\Program Files\Unity Hub\Editor\6000.0.47f1\Editor\Unity.exe" -batchmode -quit -buildTarget Android -projectPath unity -executeMethod NeuroScholAR.Editor.ProjectSetup.SetupAndBuild`
Always pass `-buildTarget Android` (XREAL settings class layout error otherwise).

## Rules that bite

- Core must stay **C# 9 / .NET Standard 2.1** (Unity profile). `TreatWarningsAsErrors` is on in
  `dotnet/`, so new warnings break the build. Verify Core changes with `dotnet test`, which is the
  fast loop; Unity compile is slow and license-gated.
- Threading: SDK callbacks only enqueue; DSP runs on `EegProcessingWorker`'s thread in preallocated
  buffers; main thread only drains `FocusFrame`s. No per-frame allocations in App code.
- Time in Core goes through `IClock` (tests use a manual clock); never `DateTime.Now` directly.
- All adaptive/focus/signal/placement thresholds live in their config objects (table in README).
  Add new knobs there, not as literals.
- BrainBit adapter (`App/Eeg/BrainBitEegSource.cs`) compiles only under `NEUROSDK2`. It targets the
  XNeuroSDK `NeuroSDK` API; signal payload is `SignalChannelsData[]`. Reference demo:
  `D:\Studia\Neuron\test\Program.cs`. New device = implement `IEegSource`, return it from `EegSources.Create`.
- UI strings shown to the user are Polish; code, comments and docs are English.
- No secrets/URLs in code: backend URL comes from `appconfig.json` or `NEUROSCHOLAR_BACKEND_URL`.
  EEG data leaves the device only on explicit "Eksportuj CSV".
- XREAL runtime only responds while glasses are connected and awake; after a Unity build re-run
  `adb connect <ip:port>` (build restarts adb).
- After `adb install -r`, run `adb shell am force-stop com.xreal.evapro.nebula` (XREAL launcher): otherwise it
  keeps a stale app entry and launching hangs on "entering NeuroScholAR". Don't launch via adb/monkey: start from the glasses.
- Debug aids go behind `DevMode.Enabled` (`App/DevMode.cs`, setup-screen checkbox): planes, markers, `DevOverlay` stats/log.
  New subsystems should expose a `DevText` line for the overlay rather than adding their own debug UI.
- Keep README's test counts and tuning table in sync when changing tests or configs.
