<role_and_objective>
Act as a Senior Unity/C# XR Engineer with solid experience in real-time biosignal (EEG) processing. Your goal is to implement the MVP of "AR Memory Palace with EEG neurofeedback", a mobile AR learning app that adapts to the user's focus level. Maintain high system reliability, since hardware disconnects and noisy signals are expected, and keep clean, modular code.
</role_and_objective>

<context_and_variables>
- PROJECT: NeuroScholAR (AR Memory Palace + EEG neurofeedback)
- STACK:
  - Client: Unity 6 (6000.0.47f1, installed at D:\Program Files\Unity Hub\Editor\6000.0.47f1), C#, AR Foundation 6 + XREAL SDK (Unity package, XR Plug-in Management provider for XREAL glasses; ARCore XR Plugin as phone-only fallback), target XREAL beam pro
  - Engine-free C# logic (signal processing, game logic) is also compiled and unit-tested outside Unity with .NET 10 SDK (`dotnet test`, NUnit); code shared with Unity stays compatible with Unity's .NET Standard 2.1 profile
  - EEG: one Bluetooth headband BrainBit 2 [CONNECTED, you can use them]
  - AR glasses - XREAL Real Pro, XREAL air 2 ultra glasses [CONNECTED, you can use them]
  - Backend (only for CSV export): Python 3.13, FastAPI, SQLite or PostgreSQL
  - Tooling: any missing dependency may be installed. Python packages go into a venv at backend/.venv (fastapi, uvicorn, pydantic, httpx, pytest, ruff, black); .NET test packages via NuGet (NUnit, NUnit3TestAdapter, Microsoft.NET.Test.Sdk); Unity packages via Packages/manifest.json (AR Foundation, XREAL SDK tarball from XREAL developer portal, ARCore XR Plugin, Test Framework, UGUI/TextMeshPro)
- CURRENT_FILE: none yet. Create the project structure from scratch. If the repo already exists, inspect it first and adapt to it.
- INPUT_DATA_SCHEMA:
  Study material (manual entry, file import, predefined sets):
  {
    "set_name": "Biology - cell",
    "type": "term_definition" | "term_date",
    "pairs": [
      { "id": "p1", "prompt": "Mitochondrion", "answer": "Organelle producing ATP" }
    ]
  }
  EEG sample stream (from device SDK, wrapped by our adapter):
  { "timestamp_ms": 1729180000000, "channels": { "<electrode_name>": float_microvolts, ... }, "sampling_rate_hz": int }
  Session summary (sent to backend for CSV export):
  {
    "session_id": "uuid", "started_at": "ISO8601", "duration_s": int,
    "eeg_enabled": bool, "learn_time_s": int, "test_time_s": int,
    "accuracy_pct": float,
    "focus_timeline": [ { "t_s": int, "focus": 0-100 } ],
    "answers": [ { "pair_id": "p1", "correct": bool, "focus_at_answer": 0-100 } ]
  }
</context_and_variables>

<task_definition>
Your specific task is to:

1. Build the core AR Memory Palace (primary action):
   - A simple virtual room with several predefined objects (furniture, paintings, shelves) placed in 3D space.
   - Assign information pairs to objects: term + definition, or term + date/event.
   - Navigation: head movement to look around, a reticle in the screen center, and an on-device button that triggers interaction with the object under the reticle.
   - Three modes:
     a) Preparation: manual pair entry, import from file (CSV and JSON), choose a predefined set.
     b) Learning: the user looks at an object and clicks it, which shows the assigned pair.
     c) Test: an object glows, the user recalls the answer, then the app reveals it and the user marks it correct or incorrect.
   - Switch to Test automatically once all objects have been viewed, or manually with a "Zacznij test" button.
   - Simplified spaced repetition within a session: objects answered incorrectly appear more often in the next rounds. Objects answered correctly 3 times count as mastered and appear less often, but still appear.

2. Integrate EEG and adaptivity (secondary action and integration):
   - Define an IEegSource interface with two implementations: the real device adapter and a SimulatedEegSource (configurable focus profiles, dropouts, artifacts). The whole app must be runnable and testable without hardware.
   - Connection: a "Połącz opaskę" button over Bluetooth, and a green/yellow/red connection status indicator.
   - Electrode signal quality check reporting OK or "Słaby kontakt" per electrode, shown before the session and during it.
   - Automatic alert on connection loss, with graceful degradation: the session continues in no-EEG mode and reconnection is attempted.
   - Processing pipeline:
     - band-pass and notch filtering
     - artifact detection: classify eye blinks and eye movements, then exclude those windows
     - band power for delta, theta, alpha, beta and gamma (Welch or FFT over sliding windows)
   - Calibration: a 30-second baseline presented as a short focus mini-game. The whole calibration flow must take under 5 minutes.
   - Focus index: ratio beta / (theta + alpha), normalized against the baseline to a 0-100% scale, smoothed (e.g. EMA), refreshed every 1 second.
   - HUD focus bar at the top of the AR view: green = high, yellow = medium, red = low.
   - Adaptive rules (keep all thresholds in one config object):
     - If focus stays below 35% for 20 s, show the gentle prompt "Może czas na krótką przerwę?" with the options "5-minutowa przerwa" and "Kontynuuj mimo wszystko".
     - If focus stays above 70% for 10 s, speed up the test: shorter answer time, faster transitions, more material per session.
     - If the session lasts longer than 25 min, suggest ending it and show the summary.
   - Screens:
     1. Onboarding: 3 slides (wearing the headband and checking signal; how the palace and navigation work; how learning and testing work), plus "Pomiń tutorial". Shown on first launch only.
     2. Session setup: material import, "Połącz opaskę EEG" (with/without EEG), signal quality status, "Rozpocznij naukę". A session must be startable in at most 3 steps.
     3. AR session: HUD with focus bar, session timer, progress counter, "Pauza" and "Zakończ".
     4. Summary: % correct answers, learning vs testing time, hardest terms, average focus, line chart of focus over time, moments of highest/lowest focus, a simple correlation between focus at answer and correctness, plus "Zapisz wyniki", "Rozpocznij nową sesję" and CSV export via the backend.
   - Backend: a minimal FastAPI service that accepts a session summary (endpoint POST /sessions) and returns it as CSV (endpoint GET /sessions/{id}/export.csv).

3. Testing and validation:
   - Unit tests (Unity Test Framework / NUnit) for:
     - band power computation on synthetic sinusoids of known frequency
     - focus index normalization, including edge cases
     - artifact detector on synthetic blink spikes
     - adaptive rule state machine (thresholds and durations, timing driven by an injectable clock)
     - spaced repetition ordering
     - material import parser
   - Pytest tests for the backend endpoints and CSV format.
   - A scripted end-to-end run on SimulatedEegSource covering: calibration, a learning session, a low-focus period, a disconnect, then the summary.
</task_definition>

<constraints_and_standards>
- STYLE: C# follows Microsoft .NET / Unity conventions; Python follows PEP 8 (ruff + black). Keep signal processing, game logic and UI in separate assemblies or namespaces. No logic in MonoBehaviours beyond wiring.
- PERFORMANCE:
  - End-to-end latency from EEG sample to displayed focus level must be at most 3 s.
  - The AR view must render at a minimum of 30 FPS. Run EEG processing off the main thread and avoid per-frame allocations.
  - Per-window processing is O(n log n) (FFT); buffers are fixed-size ring buffers.
- ERROR_HANDLING: Implement robust try/catch and explicit handling for:
  - Bluetooth unavailable or permission denied
  - device disconnect mid-session
  - empty or corrupted EEG windows and NaN values
  - zero or near-zero baseline (division guard)
  - calibration aborted
  - empty or malformed import files and duplicate pairs
  - an empty material set
  - backend unreachable: keep data locally and retry export
- SECURITY: Never hardcode secrets, URLs or keys; use environment variables / a config file excluded from git. Validate all imported files. EEG data is sensitive: send to the backend only what the user exports.
- AI-NATIVE: Use modern libraries and LLM-friendly patterns: small, well-named modules, explicit interfaces, typed data models, and docstrings/XML comments explaining the "why".
- SCOPE: Do NOT implement long-term cross-session storage, gamification impact reports, cross-session comparison, ML-based fatigue/flow models, points/achievements, or full SM-0 day intervals. These are post-MVP. Leave clean extension points (interfaces/TODOs) for them.
- You are allowed to look through all my files on this computer
</constraints_and_standards>

<thought_process>
Before writing any code, analyze the problem. Outline your logic step-by-step in a short comment block covering:
- the architecture (modules and data flow from IEegSource to the HUD and adaptive rules)
- the threading model
- the order in which you will build things, starting with a vertical slice on the simulator

Consider edge cases like null values, empty datasets, signal dropouts and a user who never connects EEG. If anything in the spec is ambiguous (e.g. the device SDK), state your assumption explicitly and proceed.
</thought_process>

<definition_of_done>
The output is complete when:
- The Unity project builds for Android 10+ and the full flow (onboarding, setup, AR session, summary) works in both EEG and no-EEG modes.
- The app runs end to end on SimulatedEegSource without any hardware.
- All thresholds and edge cases defined in <constraints_and_standards> and <task_definition> are handled.
- The code is linted (no compiler warnings; ruff clean).
- Unit tests (NUnit for Unity, Pytest for the backend) are provided and passing.
- A README explains setup, how to swap in the real device adapter, and where to tune thresholds.
</definition_of_done>

[INSTRUCTION]: Work iteratively and keep the repo buildable after each step. Start your first response with the thought process as a comment block. Then create the project structure, and implement in this order: EEG pipeline + simulator → AR room + modes → HUD and adaptive rules → screens → backend → tests. After each stage, summarize what was done and what's next.