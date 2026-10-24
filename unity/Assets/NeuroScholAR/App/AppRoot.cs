using System;
using NeuroScholAR.App.Eeg;
using NeuroScholAR.App.Palace;
using NeuroScholAR.App.UI;
using NeuroScholAR.Core;
using NeuroScholAR.Core.Session;
using UnityEngine;

namespace NeuroScholAR.App
{
    /// <summary>
    /// Composition root and frame loop. Builds the services and views, then each frame: ticks the
    /// <see cref="SessionController"/> (all app logic), forwards input, and shows the screen for the current
    /// phase. No decisions are made here beyond "which view is visible".
    /// </summary>
    public sealed class AppRoot : MonoBehaviour
    {
        private const float RefreshInterval = 0.25f; // text refresh rate: 4 Hz keeps GC pressure negligible
        private const float RetryInterval = 60f;

        private readonly SystemClock _clock = new SystemClock();
        private SessionController _app;
        private SummaryExporter _exporter;
        private Camera _camera;
        private PalaceRoom _room;
        private SurfacePlacer _placer;
        private Interactor _input;
        private Canvas _screens;
        private LazyFollow _screensFollow;
        private OnboardingScreen _onboarding;
        private SetupScreen _setup;
        private CalibrationScreen _calibration;
        private SummaryScreen _summary;
        private SessionViews _session;
        private DevOverlay _dev;
        private AppPhase? _shownPhase;
        private bool _onboardingVisible;
        private float _nextRefresh;
        private float _nextRetry;

        private void Start()
        {
            Application.targetFrameRate = 60;
            _camera = Camera.main;
            if (_camera == null)
            {
                Debug.LogError("NeuroScholAR: no MainCamera in the scene. Run NeuroScholAR ▸ Setup Project.");
                enabled = false;
                return;
            }

            var config = AppConfig.Load();
            var setupWantsSimulator = config.UseSimulator;
            _app = new SessionController(_clock, () => EegSources.Create(_setup == null ? setupWantsSimulator : _setup.UseSimulator));
            _exporter = new SummaryExporter(Application.persistentDataPath, config.BackendUrl);

            var world = new GameObject("World UI").transform;
            _room = new GameObject("Palace").AddComponent<PalaceRoom>();
            _placer = _room.gameObject.AddComponent<SurfacePlacer>();
            _placer.Init(_room, _camera.transform);
            _placer.Completed += () =>
            {
                _app.FinishPlacement();
                _session.Toast("Wszystko rozmieszczone! Podchodź do przedmiotów i patrz na nie.", 6);
            };
            _input = gameObject.AddComponent<Interactor>();
            _input.ObjectSelected += i => _app.SelectObject(i);
            _input.ObjectGazed += i => _app.ViewObject(i);
            _input.AnyPress += () =>
            {
                if (_app.Phase == AppPhase.Calibration)
                {
                    _calibration.Press(_clock.NowSeconds);
                }
            };

            _session = new SessionViews(_app, _camera, _room, _placer, _input, world);
            _input.Init(_camera, _session.Reticle);
            _dev = new DevOverlay(_camera, _app, _placer, _input);

            _screens = Ui.WorldCanvas("Screens", world, new Vector2(900, 720), _camera);
            _screensFollow = _screens.gameObject.AddComponent<LazyFollow>();
            _screensFollow.Head = _camera.transform;
            _screensFollow.HeightOffset = -0.1f; // slightly below the eyes: comfortable to read and to poke
            _screensFollow.Distance = 0.65f;
            _onboarding = new OnboardingScreen(_screens.transform, () => ShowOnboarding(false));
            _setup = new SetupScreen(_screens.transform, _app, config.UseSimulator);
            _calibration = new CalibrationScreen(_screens.transform, _app, () => _calibration.Press(_clock.NowSeconds));
            _summary = new SummaryScreen(_screens.transform, _app, _exporter, () => _app.NewSession());

            ShowOnboarding(!OnboardingScreen.AlreadySeen);
        }

        private void Update()
        {
            if (_app == null)
            {
                return;
            }

            if (Time.unscaledDeltaTime > 0.5f)
            {
                // ponytail: diagnostics for on-device stalls (adb logcat -s Unity); remove once the app runs smooth
                Debug.LogWarning($"NeuroScholAR: frame stall {Time.unscaledDeltaTime:F2} s in {_app.Phase}/{_app.Palace?.Mode}");
            }

            _app.Tick();
            _dev.Tick();
            HandleNotices();
            if (_shownPhase != _app.Phase)
            {
                ShowPhase(_app.Phase);
            }

            if (_app.Phase == AppPhase.Calibration)
            {
                _calibration.Animate(_clock.NowSeconds);
            }
            else if (_app.Phase == AppPhase.Session)
            {
                _session.Tick();
            }

            if (Time.unscaledTime >= _nextRefresh)
            {
                _nextRefresh = Time.unscaledTime + RefreshInterval;
                RefreshVisible();
                _dev.Refresh();
            }

            if (Time.unscaledTime >= _nextRetry)
            {
                _nextRetry = Time.unscaledTime + RetryInterval;
                RetryExports();
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                _app?.Pause(); // phone call, headset off: freeze the session clock
            }
        }

        private void OnDestroy()
        {
            _dev?.Dispose();
            _app?.Dispose();
            _exporter?.Dispose();
        }

        private void ShowOnboarding(bool show)
        {
            _onboardingVisible = show;
            _shownPhase = null; // force re-layout
        }

        private void ShowPhase(AppPhase phase)
        {
            _shownPhase = phase;
            var onboarding = _onboardingVisible && phase == AppPhase.Setup;
            _onboarding.SetVisible(onboarding);
            _setup.SetVisible(!onboarding && phase == AppPhase.Setup);
            _calibration.SetVisible(phase == AppPhase.Calibration);
            _summary.SetVisible(phase == AppPhase.Summary);
            _screens.gameObject.SetActive(phase != AppPhase.Session);
            _session.SetVisible(phase == AppPhase.Session);

            if (phase == AppPhase.Session)
            {
                _placer.Begin(_app.Palace.Objects.Count);
            }
            else
            {
                _placer.Stop();
                _room.Clear();
                _screensFollow.SnapNow();
            }

            if (phase == AppPhase.Summary)
            {
                _summary.Show();
            }
        }

        private void RefreshVisible()
        {
            switch (_app.Phase)
            {
                case AppPhase.Setup:
                    _setup.Refresh();
                    break;
                case AppPhase.Calibration:
                    _calibration.Refresh();
                    break;
                case AppPhase.Session:
                    _session.Refresh();
                    break;
            }
        }

        private void HandleNotices()
        {
            while (_app.TryTakeNotice(out var notice))
            {
                switch (notice)
                {
                    case Notice.ConnectionLost:
                        _session.Toast("Utracono połączenie z opaską. Nauka trwa bez EEG, łączę ponownie…", 8);
                        break;
                    case Notice.Reconnected:
                        _session.Toast("Opaska połączona ponownie.");
                        break;
                    case Notice.SuggestBreak:
                        _session.PendingPrompt = Modal.BreakPrompt;
                        break;
                    case Notice.SuggestEndSession:
                        _session.PendingPrompt = Modal.SessionLimit;
                        break;
                    case Notice.BreakEnded:
                        _session.Toast("Koniec przerwy, wracamy do nauki.");
                        break;
                    case Notice.SpeedUpOn:
                        _session.Toast("Świetne skupienie! Przyspieszamy test.");
                        break;
                    case Notice.SpeedUpOff:
                        _session.Toast("Wracamy do zwykłego tempa.");
                        break;
                    case Notice.AllViewed:
                        _session.Toast("Wszystkie przedmioty obejrzane. Za chwilę test.");
                        break;
                    case Notice.TestStarted:
                        _session.Toast("Test! Znajdź przedmiot, przy którym było pokazane hasło.", 6);
                        break;
                    case Notice.CalibrationFailed:
                    case Notice.SessionFinished:
                        break; // the phase change shows the right screen
                }
            }
        }

        private async void RetryExports()
        {
            try
            {
                await _exporter.RetryPendingAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning("Export retry failed: " + e.Message);
            }
        }
    }
}
