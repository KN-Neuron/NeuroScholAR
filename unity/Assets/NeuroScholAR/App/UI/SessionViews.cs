using System;
using NeuroScholAR.App.Palace;
using NeuroScholAR.Core.Adaptive;
using NeuroScholAR.Core.Eeg;
using NeuroScholAR.Core.Learning;
using NeuroScholAR.Core.Session;
using UnityEngine;
using UnityEngine.UI;

namespace NeuroScholAR.App.UI
{
    /// <summary>Which modal dialog is up. Prompts the user must answer win over status overlays.</summary>
    public enum Modal
    {
        None,
        BreakPrompt,
        SessionLimit,
        Break,
        Pause,
    }

    /// <summary>
    /// Everything shown during the AR session: head-locked HUD (focus bar, timer, progress, link status,
    /// current task, reticle, toasts), the session menu on the left palm, the palace objects' labels and highlights, and
    /// modal dialogs. Pure presentation of <see cref="SessionController"/> state; text is rebuilt only when it
    /// changes or at a low rate.
    /// </summary>
    public sealed class SessionViews
    {
        private readonly SessionController _app;
        private readonly Transform _head;
        private readonly PalaceRoom _room;
        private readonly SurfacePlacer _placer;

        private readonly GameObject _hudTop;
        private readonly Image _focusFill;
        private readonly Text _focusText;
        private readonly Text _timer;
        private readonly Text _progress;
        private readonly Image _linkLight;
        private readonly Text _linkText;
        private readonly Text _toast;
        private readonly Text _task;
        private float _toastUntil;

        private readonly PalmMenu _toolbar;
        private readonly Button _placeHereButton;
        private readonly Button _testButton;
        private readonly Button _giveUpButton;
        private readonly Button _pauseButton;

        private readonly Canvas _dialogCanvas;
        private readonly LazyFollow _dialogFollow;
        private readonly Text _dialogTitle;
        private readonly Text _dialogBody;
        private readonly Button _dialogA;
        private readonly Button _dialogB;
        private Action _dialogActionA;
        private Action _dialogActionB;
        private Modal _shownModal = (Modal)(-1);

        public SessionViews(SessionController app, Camera camera, PalaceRoom room, SurfacePlacer placer, Interactor hands, Transform worldRoot)
        {
            _app = app;
            _head = camera.transform;
            _room = room;
            _placer = placer;

            // ---- HUD: head-locked, display only (the reticle can't reach things that move with the head) ----
            var hud = Ui.WorldCanvas("HUD", _head, new Vector2(760, 440), camera, interactive: false);
            hud.transform.localPosition = new Vector3(0, 0, 1.2f);
            hud.transform.localScale = Vector3.one * 0.001f; // display only, so it stays far (easier on the eyes) and keeps its size
            hud.sortingOrder = 10;
            var top = Ui.Column("Top", hud.transform, 6, 10, new Color(0, 0, 0, 0.45f));
            _hudTop = top.gameObject;
            top.anchorMin = new Vector2(0, 1);
            top.anchorMax = new Vector2(1, 1);
            top.pivot = new Vector2(0.5f, 1);
            top.sizeDelta = new Vector2(0, 120);
            var focusRow = Ui.Row("Focus", top, 10);
            _focusText = Ui.Label(focusRow, "Skupienie: —", 22);
            _focusText.gameObject.AddComponent<LayoutElement>().preferredWidth = 220;
            _focusFill = Ui.Bar(focusRow, 22, Ui.Yellow);
            _focusFill.transform.parent.gameObject.AddComponent<LayoutElement>().flexibleWidth = 3;
            var infoRow = Ui.Row("Info", top, 10);
            _timer = Ui.Label(infoRow, "00:00", 22);
            _progress = Ui.Label(infoRow, string.Empty, 22);
            _linkLight = Ui.Box("Link", infoRow, Ui.Red);
            var lightLayout = _linkLight.gameObject.AddComponent<LayoutElement>();
            lightLayout.preferredWidth = lightLayout.minWidth = 18;
            lightLayout.flexibleWidth = 0;
            _linkText = Ui.Label(infoRow, string.Empty, 18, Ui.Muted);

            _toast = Ui.Label(hud.transform, string.Empty, 24, Ui.Yellow, TextAnchor.MiddleCenter, FontStyle.Bold);
            _toast.rectTransform.anchoredPosition = new Vector2(0, 60);
            _toast.rectTransform.sizeDelta = new Vector2(700, 80);

            _task = Ui.Label(hud.transform, string.Empty, 26, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            _task.rectTransform.anchoredPosition = new Vector2(0, -150);
            _task.rectTransform.sizeDelta = new Vector2(740, 110);

            var reticle = Ui.Box("Reticle", hud.transform, new Color(1, 1, 1, 0.9f));
            reticle.rectTransform.sizeDelta = new Vector2(10, 10);
            reticle.raycastTarget = false;
            Reticle = reticle.rectTransform;

            // ---- Session menu: on the left palm (look at your palm), poked with the right hand ----
            var toolbar = Ui.WorldCanvas("Palm menu", worldRoot, new Vector2(300, 330), camera);
            var bar = Ui.Column("Buttons", toolbar.transform, 10, 10, Ui.Background);
            Ui.Stretch(bar);
            _placeHereButton = Ui.Button(bar, "Rozmieść teraz", PlaceNow, Ui.Accent, 70, 22);
            _testButton = Ui.Button(bar, "Zacznij test", () => _app.StartTest(), Ui.Accent, 70, 24);
            _giveUpButton = Ui.Button(bar, "Nie pamiętam", () => _app.GiveUp(), Ui.Accent, 70, 24);
            _pauseButton = Ui.Button(bar, "Pauza", TogglePause, Ui.Panel, 70, 24);
            Ui.Button(bar, "Zakończ", () => _app.EndSession(), Ui.Red, 70, 24);
            _toolbar = toolbar.gameObject.AddComponent<PalmMenu>();
            _toolbar.Head = _head;
            _toolbar.Hands = hands;

            // ---- Dialog: world-locked, in front of the card ----
            _dialogCanvas = Ui.WorldCanvas("Dialog", worldRoot, new Vector2(620, 300), camera);
            _dialogCanvas.sortingOrder = 5;
            _dialogFollow = _dialogCanvas.gameObject.AddComponent<LazyFollow>();
            _dialogFollow.Head = _head;
            _dialogFollow.Distance = 0.6f;
            // Prompts must be seen wherever the user looks: re-centre on the gaze once it leaves the panel
            // (620 units * 0.45 mm = 28 cm wide at 60 cm: about +-13 degrees).
            _dialogFollow.FollowGaze = true;
            _dialogFollow.Threshold = 15f;
            var dialog = Ui.Column("Body", _dialogCanvas.transform, 12, 24, Ui.Panel);
            Ui.Stretch(dialog);
            _dialogTitle = Ui.Label(dialog, string.Empty, 32, style: FontStyle.Bold);
            _dialogBody = Ui.Label(dialog, string.Empty, 26);
            var dialogRow = Ui.Row("Buttons", dialog, 12);
            _dialogA = Ui.Button(dialogRow, string.Empty, () => _dialogActionA?.Invoke(), Ui.Accent, 64, 24);
            _dialogB = Ui.Button(dialogRow, string.Empty, () => _dialogActionB?.Invoke(), Ui.Panel, 64, 24);

            SetVisible(false);
        }

        public RectTransform Reticle { get; }

        /// <summary>Set by the app when a notice asks a question; cleared when answered.</summary>
        public Modal PendingPrompt { get; set; }

        /// <summary>Session chrome on/off. The HUD canvas itself stays, since it hosts the reticle and toasts on every screen.</summary>
        public void SetVisible(bool visible)
        {
            _hudTop.SetActive(visible);
            _toolbar.gameObject.SetActive(visible);
            _task.text = string.Empty;
            _dialogCanvas.gameObject.SetActive(false);
            _shownModal = (Modal)(-1);
        }

        public void Toast(string text, float seconds = 4)
        {
            _toast.text = text;
            _toastUntil = Time.time + seconds;
        }

        /// <summary>Every frame: objects and dialog state (cheap unless something changed), toast expiry.</summary>
        public void Tick()
        {
            if (_toast.text.Length > 0 && Time.time > _toastUntil)
            {
                _toast.text = string.Empty;
            }

            SyncObjects();
            UpdateModal();
        }

        /// <summary>A few times per second: numbers on the HUD.</summary>
        public void Refresh()
        {
            var focus = _app.Focus;
            var level = _app.FocusLevel;
            _focusText.text = focus.HasValue ? $"Skupienie: {focus.Value:F0}%" : _app.EegEnabledForSession ? "Skupienie: brak sygnału" : "Skupienie: bez EEG";
            Ui.SetFill(_focusFill, focus.HasValue ? (float)focus.Value / 100f : 0);
            _focusFill.color = level == FocusLevel.High ? Ui.Green : level == FocusLevel.Medium ? Ui.Yellow : Ui.Red;

            var palace = _app.Palace;
            _timer.text = Ui.Clock(_app.SessionSeconds) + (_app.SpeedUp ? "  (tempo+)" : string.Empty);
            _progress.text = palace == null ? string.Empty
                : palace.Mode == PalaceMode.Preparation ? $"Rozmieszczone: {_placer.PlacedCount}/{palace.Objects.Count}"
                : palace.Mode == PalaceMode.Learning ? $"Obejrzane: {palace.ViewedCount}/{palace.Objects.Count}"
                : $"Test: {palace.Answers.Count} odp., opanowane {palace.MasteredCount}/{palace.Objects.Count}";

            var light = _app.Light;
            _linkLight.color = light == ConnectionLight.Green ? Ui.Green : light == ConnectionLight.Yellow ? Ui.Yellow : Ui.Red;
            _linkText.text = _app.Connection?.State == EegConnectionState.Reconnecting ? "EEG: łączenie ponowne…"
                : _app.EegStreaming ? SetupScreen.ElectrodeText(_app.ChannelNames, _app.ContactOk, true)
                : "EEG wyłączone";

            var mode = palace?.Mode ?? PalaceMode.Finished;
            _placeHereButton.gameObject.SetActive(mode == PalaceMode.Preparation);
            Ui.SetButtonText(_placeHereButton, _placer.IsScanning ? "Rozmieść teraz" : "Rozmieść resztę tutaj");
            _testButton.gameObject.SetActive(mode == PalaceMode.Learning);
            _giveUpButton.gameObject.SetActive(mode == PalaceMode.Test && palace.Step == TestStep.Find);
            Ui.SetButtonText(_pauseButton, _app.IsPaused ? "Wznów" : "Pauza");
            _task.text = palace == null || _app.IsPaused ? string.Empty : TaskText(palace);

            if (_shownModal == Modal.Break)
            {
                _dialogBody.text = $"Odpocznij, rozluźnij wzrok. Zostało {Ui.Clock(_app.BreakTimeLeft)}.";
            }
        }

        /// <summary>What the user should do now, in one line under their view.</summary>
        private string TaskText(PalaceSession palace)
        {
            switch (palace.Mode)
            {
                case PalaceMode.Preparation when _placer.IsScanning && _placer.HasPlaneDetection:
                    return "Obejdź cały pokój i rozglądaj się (meble, podłoga, ściany). " +
                           $"Znalezione powierzchnie: {_placer.SurfaceCount}. Rozmieszczę przedmioty za {_placer.ScanSecondsLeft:F0} s.\n" +
                           "Spójrz na lewą dłoń, aby otworzyć menu.";
                case PalaceMode.Preparation:
                    return _placer.HasPlaneDetection
                        ? "Rozmieszczam przedmioty po całym pokoju…"
                        : "Rozmieszczam przedmioty wokół Ciebie…";
                case PalaceMode.Learning:
                    var startsIn = _app.TestStartsIn;
                    return startsIn.HasValue
                        ? $"Wszystko obejrzane. Test za {startsIn.Value:F0} s."
                        : "Podejdź do przedmiotów i patrz na nie, aby poznać ich hasła.";
                case PalaceMode.Test when palace.Step == TestStep.Find:
                    return $"Znajdź: {palace.Objects[palace.CurrentObject].Prompt}  ({_app.AnswerTimeLeft:F0} s)\n" +
                           "Podejdź do tego przedmiotu i dotknij go palcem.";
                case PalaceMode.Test when palace.Step == TestStep.Feedback:
                    return palace.LastCorrect ? "Dobrze!" : "To nie ten. Właściwy przedmiot świeci.";
                default:
                    return string.Empty;
            }
        }

        /// <summary>Palace objects mirror the game state: labels in Learning, highlights in Test feedback.</summary>
        public void SyncObjects()
        {
            var palace = _app.Palace;
            if (palace == null)
            {
                return;
            }

            foreach (var view in _room.Objects)
            {
                var i = view.Index;
                var pair = palace.Objects[i];
                var learning = palace.Mode == PalaceMode.Learning;
                var feedback = palace.Mode == PalaceMode.Test && palace.Step == TestStep.Feedback;
                view.SetViewed(learning && palace.WasViewed(i));

                var highlight = Highlight.None;
                if (feedback && i == palace.CurrentObject)
                {
                    highlight = palace.LastCorrect ? Highlight.Correct : Highlight.Target;
                }
                else if (feedback && i == palace.PickedObject)
                {
                    highlight = Highlight.Wrong;
                }

                view.SetHighlight(highlight);
                view.SetLabel(
                    learning ? view.Hovered ? pair.Prompt + "\n" + pair.Answer : palace.WasViewed(i) ? pair.Prompt : "?"
                    : feedback && i == palace.CurrentObject ? pair.Prompt + "\n" + pair.Answer
                    : string.Empty);
            }
        }

        private void PlaceNow()
        {
            if (_placer.IsScanning)
            {
                _placer.PlaceNow();
            }
            else
            {
                _placer.PlaceRemainingAround();
            }
        }

        private void TogglePause()
        {
            if (_app.IsPaused)
            {
                _app.Resume();
            }
            else
            {
                _app.Pause();
            }
        }

        private void UpdateModal()
        {
            var modal = PendingPrompt != Modal.None ? PendingPrompt
                : _app.Rules != null && _app.Rules.OnBreak ? Modal.Break
                : _app.IsPaused ? Modal.Pause
                : Modal.None;
            if (modal == _shownModal)
            {
                return;
            }

            _shownModal = modal;
            if (modal == Modal.None)
            {
                _dialogCanvas.gameObject.SetActive(false);
                return;
            }

            _dialogFollow.SnapNow();
            _dialogCanvas.gameObject.SetActive(true);
            switch (modal)
            {
                case Modal.BreakPrompt:
                    Dialog("Może czas na krótką przerwę?", "Twoje skupienie od dłuższej chwili jest niskie.",
                        "5-minutowa przerwa", () => AnswerPrompt(() => _app.AnswerBreakPrompt(true)),
                        "Kontynuuj mimo wszystko", () => AnswerPrompt(() => _app.AnswerBreakPrompt(false)));
                    break;
                case Modal.SessionLimit:
                    Dialog("Sesja trwa już 25 minut", "Dłuższa nauka bez przerwy zwykle mniej daje. Zakończyć i zobaczyć podsumowanie?",
                        "Zakończ sesję", () => AnswerPrompt(_app.EndSession),
                        "Jeszcze chwilę", () => AnswerPrompt(null));
                    break;
                case Modal.Break:
                    Dialog("Przerwa", string.Empty, "Wróć do nauki", _app.EndBreak, null, null);
                    break;
                case Modal.Pause:
                    Dialog("Pauza", "Czas sesji jest wstrzymany.", "Wznów", _app.Resume, "Zakończ", _app.EndSession);
                    break;
            }
        }

        private void AnswerPrompt(Action action)
        {
            PendingPrompt = Modal.None;
            action?.Invoke();
        }

        private void Dialog(string title, string body, string a, Action actionA, string b, Action actionB)
        {
            _dialogTitle.text = title;
            _dialogBody.text = body;
            Ui.SetButtonText(_dialogA, a);
            _dialogActionA = actionA;
            _dialogB.gameObject.SetActive(b != null);
            if (b != null)
            {
                Ui.SetButtonText(_dialogB, b);
                _dialogActionB = actionB;
            }
        }
    }
}
