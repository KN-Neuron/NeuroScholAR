# NeuroScholAR: AR Memory Palace + EEG neurofeedback

A mobile AR learning app. You memorise pairs (term → definition, event → date) on virtual objects
that the app places on your real furniture, floor and walls as you walk around the room. You learn by
walking up to them and looking at them, then test yourself by finding the right object again. A BrainBit 2 EEG headband measures your focus,
and the app adapts: it suggests a break when focus stays low and speeds the test up when focus stays
high. Everything also works **without the headband** (simulator, or plain no-EEG mode).

| Part | Tech | Where |
|---|---|---|
| App | Unity 6 (6000.0.47f1), AR Foundation 6 + ARCore (XREAL via XR Plug-in Management) | `unity/` |
| Engine-free logic (DSP, focus, rules, palace game, session) | C# 9 / .NET Standard 2.1, shared with Unity | `unity/Assets/NeuroScholAR/Core/` |
| Unit + end-to-end tests | .NET 10, NUnit | `dotnet/` |
| Export backend | Python 3.13, FastAPI, SQLite | `backend/` |

## Architecture

```
IEegSource ── BrainBitEegSource (XNeuroSDK, BLE)  |  SimulatedEegSource (profiles, blinks, dropouts)
   │  SamplesReceived  (SDK thread: enqueue only)
   ▼
EegProcessingWorker ── dedicated thread ───────────────────────────────────────────────┐
   EegPipeline: notch 50 Hz → band-pass 1–45 Hz → fixed ring buffers (2 s window, 1 s hop)
     → electrode contact (OK / "Słaby kontakt") → ArtifactDetector (blink / eye movement /
       high amplitude / corrupted: window excluded) → Welch band powers δ θ α β γ (radix-2 FFT)
   FocusEstimator: 30 s median baseline → 50 + 25·log2(ratio/baseline), clamp 0–100 → EMA
   │  ConcurrentQueue<FocusFrame> at 1 Hz                                                  │
   ▼                                                                                       │
SessionController (main thread, engine-free) ◄─────────────────────────────────────────────┘
   EegConnectionManager (alert once on loss, background reconnect with backoff, light colour)
   PalaceSession (Preparation → Learning → Test "find the object", auto-switch) + SpacedRepetitionScheduler
   SurfacePlanner (where the next object goes: spacing, per-surface cap, furniture > wall, floor only as a last resort, in view)
   AdaptiveRuleEngine (IClock-driven: break prompt, speed-up, 25-min limit)
   SessionSummary + SummaryAnalytics → SummaryExporter (local results/ + outbox retry → FastAPI)
   ▼
Unity (App assembly): AppRoot ticks the controller and shows the view for the current phase:
   OnboardingScreen · SetupScreen · CalibrationScreen (mini-game) · SessionViews (HUD, toolbar,
   dialogs) · SummaryScreen (chart) · SurfacePlacer (AR planes → spots) · PalaceRoom ·
   Interactor (gaze dwell, fingertip poke, hand meshes, controller/tap) · PalmMenu
```

- **Threading.** SDK callbacks only enqueue. One background thread does all the DSP into
  preallocated buffers, at O(n log n) per window. The render thread only drains finished frames, and
  HUD text refreshes at 4 Hz, so there is no per-frame churn.
- **Latency.** The first focus value arrives about 2.5 s after streaming starts (window plus filter
  warm-up). After that it updates every 1 s and stays within the 3 s budget (tested).
- **Assemblies.** `NeuroScholAR.Core` has `noEngineReferences` and holds all logic. `NeuroScholAR.App`
  is wiring and views. `NeuroScholAR.Editor` holds project setup and build.
- **Session flow.**
  1. *Placement:* first a 25 s scan (walk around the whole room, or press **Rozmieść teraz**), then objects drop
     onto detected surfaces (desk, bed, shelves, floor, walls), each as far from the others as possible.
     **Rozmieść resztę tutaj** puts any that didn't fit on a ring around you.
  2. *Learning:* look at an object for about 1 s to read its pair. Once all are viewed, the test starts 10 s later.
  3. *Test:* "Znajdź: <hasło>". Walk to the object and touch it. A wrong pick turns red and the
     right one pulses; **Nie pamiętam** or a timeout counts as wrong.
- **Hand control.** Poke only: buttons travel with the index fingertip and click about 8 mm in; touching an
  object selects it. Hands are drawn with XR Hands' rigged hand meshes (`App/Resources/Hands`, from the package's
  HandVisualizer sample). The session menu sits on the left palm and opens when you look at the palm; without
  hand tracking it falls back to a panel low in view, used with gaze + the Beam Pro controller.
- **Panels** are within arm's reach (about 60 cm) so they can be poked; `Ui.WorldScale` keeps their apparent size.
- **UI for glasses.** A centre reticle can't reach UI that moves with your head. So the HUD is
  head-locked and display-only. Screens and cards are world-locked panels placed in front of you when
  they open. The session buttons sit on a toolbar slightly below eye level that only re-centres when
  you turn away. Every button works with reticle + select, and also with touch or mouse.

## Quick start

### 1. Logic and tests (no Unity needed)

```bash
cd dotnet
dotnet test            # signal processing, focus, rules, palace, import, scripted end-to-end run
```

### 2. Backend

```bash
cd backend
py -3.13 -m venv .venv
.venv/Scripts/python -m pip install -r requirements.txt     # Linux/macOS: .venv/bin/python
.venv/Scripts/python -m pytest
.venv/Scripts/ruff check . && .venv/Scripts/black --check .
.venv/Scripts/python -m uvicorn app.main:app --host 0.0.0.0 --port 8000
```

- `POST /sessions` stores a summary. Re-sending the same id is accepted (outbox retries).
- `GET /sessions/{id}/export.csv` returns one flat CSV: the session fields on every row, then one row
  per focus sample (`record_type=focus`) or answer (`record_type=answer`).
- The database file comes from `NEUROSCHOLAR_DB` (see `backend/.env.example`).
- Contract test, C# exporter against a live server:
  `NSAR_BACKEND_URL=http://127.0.0.1:8000 dotnet test --filter LiveBackend`

### 3. Unity app

1. Open `unity/` in Unity 6000.0.47f1. Batch mode needs Unity Hub open and signed in, for the license.
2. Run the menu **NeuroScholAR ▸ Setup Project**. It creates `Assets/Scenes/Main.unity` and sets
   Android 10+ (API 29), IL2CPP, ARM64, landscape, Input System and the ARCore XR loader.
3. Press **Play**. The app runs on the simulator. Right-drag the mouse or use the arrow keys to look
   around; Space/Enter is the select button.
4. Build with **NeuroScholAR ▸ Build Android APK** (→ `unity/Builds/NeuroScholAR.apk`), or headless:
   `Unity -batchmode -quit -buildTarget Android -projectPath unity -executeMethod NeuroScholAR.Editor.ProjectSetup.SetupAndBuild`.
   Keep `-buildTarget Android`: XREAL's settings class has Android-only fields, so the editor must
   start already compiled for Android, or the build fails with "script class layout is incompatible".

### Dev mode

The **[ ] Tryb deweloperski** checkbox on the setup screen (remembered) turns on: detected AR planes drawn by
classification (green furniture, blue wall, orange floor, grey rejected), a gaze-hit dot and fingertip dots, and a
head-locked overlay with FPS, phase, EEG internals (state, samples/s, last error, band powers, ratio, artifacts,
contact), adaptive rules, calibration game, placement heights and counts, input state and the last log lines.

### Runtime config (not in git)

`{persistentDataPath}/appconfig.json` (template: `samples/appconfig.example.json`):
`backend_url` (export target) and `use_simulator`. The environment variable
`NEUROSCHOLAR_BACKEND_URL` overrides the URL, which is handy in the Editor. With no URL, results stay on
the device. Nothing is sent unless the user presses **Eksportuj CSV**.

### Study material

- Pick a predefined set, type pairs by hand, or import `.csv` / `.json` from
  `{persistentDataPath}/Import`.
- Unity has no native file picker, so copy files there over USB (`adb push`) or in the Editor.
- Examples are in `samples/`.
- CSV: `prompt;answer[;id]` or comma-separated, with an optional header and RFC 4180 quoting. A set whose
  answers all look like dates becomes `term_date`.
- JSON: the spec schema.
- Files are validated: 1 MB limit, 200 pairs, 300 characters per field. Duplicates and incomplete rows
  are skipped with a warning; an empty set is rejected.

## Swapping in the real BrainBit device

The adapter is `unity/Assets/NeuroScholAR/App/Eeg/BrainBitEegSource.cs`. It uses the same `NeuroSDK` API
as `XNeuroSDK` (the package used by `../test/Program.cs`) and is compiled only when `NEUROSDK2` is
defined.

1. Copy `XNeuroSDK.dll` (`lib/netstandard2.0` from the `XNeuroSDK` NuGet package) to
   `unity/Assets/Plugins/NeuroSDK/`, plus `runtimes/win-x64/native/neurosdk2-x64.dll` for using the real
   headband in Editor Play mode. These are already in the repo's working copy.
2. For Android, add BrainBit's `neurosdk2.aar` to `Assets/Plugins/Android/`. BrainBit also ships a Unity
   package of NeuroSDK2; if that works better for you, use it, since the C# API is the same.
3. Run **NeuroScholAR ▸ Setup Project** again. It sees the SDK and adds the `NEUROSDK2` define.
4. In the app, switch **Źródło: symulator → BrainBit** on the setup screen, or set `"use_simulator": false`.

Any other device: implement `IEegSource` (channel names, rate, `ConnectAsync`, `SamplesReceived` in µV,
`StateChanged`) and return it from `EegSources.Create`. That is the only switch point.
`EegPipelineConfig.LeftOcularChannel/RightOcularChannel` names the channels used for eye artifacts.

## XREAL glasses

The app only uses AR Foundation's `XROrigin`/camera, so XREAL is an XR provider, not a code path.

- The XREAL XR Plugin (3.1.0, from the XREAL developer portal) is referenced in
  `unity/Packages/manifest.json` as `file:../../vendor/com.xreal.xr.tar.gz`. `vendor/` is git-ignored
  because the tarball is 248 MB: put the downloaded `com.xreal.xr.tar.gz` there after cloning.
- **NeuroScholAR ▸ Setup Project** makes XREAL the Android XR loader when the package is present, and
  falls back to ARCore (phone only) when it isn't.
- Black (cleared) pixels are see-through on the glasses.
- **Start the app with the glasses connected and awake** (ideally from the app list inside the glasses).
  The XREAL runtime (Nebula) only answers while the glasses are active; otherwise you get `NotFindRuntime`
  and nothing on the glasses.
- Setup sets XREAL's input to controller + hands and adds an `ARPlaneManager` (horizontal + vertical).
- Wireless adb: Unity's build restarts the adb server, so run `adb connect <ip:port>` again after a build.
- The XREAL package needs Unity's built-in Audio/Video modules (already in the manifest), and building
  needs the Unity editor's **Android Build Support** module with SDK/NDK and OpenJDK.

## Where to tune things

| What | Where |
|---|---|
| Adaptive rules: 35 %/20 s break prompt, 70 %/10 s speed-up, exit hysteresis, 25-min limit, break length, prompt cooldown, answer time and transitions (normal/fast); HUD colours use the same thresholds | `Core/Adaptive/AdaptiveRuleEngine.cs` → `AdaptiveConfig` |
| Focus: calibration length, clean-window minimum, zero-baseline guard, % per ratio doubling, EMA, hold time | `Core/Focus/FocusEstimator.cs` → `FocusConfig` |
| Signal: notch frequency (50/60 Hz), band-pass, window/hop, warm-up, artifact and contact thresholds, ocular channels | `Core/Signal/EegPipeline.cs` → `EegPipelineConfig` |
| Spaced repetition: correct answers to master, mastered recurrence, extra copies for misses | `SpacedRepetitionScheduler` constructor |
| Placement rules: spacing, relaxed spacing (when surfaces run out), max distance, objects per surface, kind preference | `Core/Learning/SurfacePlanner.cs` → `PlacementConfig` |
| Surface sampling: grid step, edge margin, floor tolerance, wall heights, object sizes | `App/Palace/SurfacePlacer.cs` constants |
| Fallback ring layout (no plane detection) | `App/Palace/PalaceRoom.cs` → `Slots` |
| Look-to-learn dwell, poke press depth / travel | `App/Palace/Interactor.cs` constants |
| Scan time before placing | `App/Palace/SurfacePlacer.cs` → `ScanSeconds` |
| Palm menu open angle and offsets | `App/UI/PalmMenu.cs` constants |
| Find time, feedback time, auto test delay | `AdaptiveConfig` (`AnswerTimeS`, `TransitionS`, `AutoTestDelayS`) |

## Tests

- `dotnet test` runs 99 tests:
  - band power on synthetic sinusoids per band, with amplitude checked against A²/2
  - focus normalization edge cases: zero, negative, NaN and ∞ baselines and ratios, clamping, EMA
    hold/stale, calibration failure modes
  - artifact detector on synthetic blinks, saccades, motion and NaNs
  - adaptive state machine with an injected clock: thresholds, durations, cooldown, break, hysteresis,
    session limit
  - spaced-repetition ordering
  - import parser: CSV/JSON, malformed, empty, duplicates, size limit
  - connection manager: Bluetooth off, permission denied, link loss and reconnect
  - exporter outbox
- **Scripted end-to-end run** (`EndToEndTests.FullSession_OnSimulator`), on the simulator with a manual
  clock: calibration mini-game → learning (auto-switch to test) → low-focus period (break prompt) →
  headband disconnect (alert, learning continues without EEG, auto-reconnect without recalibrating) →
  summary (timeline gap, `null` focus at answers during the outage, analytics) → export while offline
  (queued) → retry.
- `backend/`: `pytest` runs 13 tests (endpoints, validation, idempotency, CSV format and escaping).

## Assumptions and scope

- BrainBit 2: 4 channels (O1, O2, T3, T4) at 250 Hz. There are no frontal electrodes, so eye artifacts
  are read off T3/T4: same polarity means a blink, opposite polarity means horizontal eye movement.
- Focus = β/(θ+α) relative to your own 30 s baseline, on a log scale: baseline = 50 %, 2× = 75 %,
  ½× = 25 %. It is a relative engagement index, not a clinical measure.
- Electrode contact is judged from the signal itself (NaN / flat / huge amplitude), the same way for both
  sources. The SDK's resistance mode is an alternative if you need ohms.
- Post-MVP (not implemented, by spec): long-term cross-session storage, cross-session comparison,
  ML fatigue/flow models, points/achievements, full SM-2 intervals. Extension points:
  - `SummaryExporter` (`results/` folder) for storage
  - `FocusEstimator.SetBaseline` for reusing a stored per-user baseline
  - `SpacedRepetitionScheduler` for interval scheduling
  - `IEegSource` for new devices
