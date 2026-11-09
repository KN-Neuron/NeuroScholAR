using System;
using NeuroScholAR.App.Palace;
using NeuroScholAR.Core.Adaptive;
using NeuroScholAR.Core.Eeg;
using NeuroScholAR.Core.Learning;
using NeuroScholAR.Core.Session;
using UnityEngine;
using UnityEngine.UI;
using static NeuroScholAR.App.UI.Lang;

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
    /// Everything shown during the AR session: head-locked HUD (status pill with focus, timer and progress; toast pill;
    /// task card; reticle), the session menu on the left palm, the palace objects' labels and highlights, and
    /// modal dialogs. Pure presentation of <see cref="SessionController"/> state; text is rebuilt only when it
    /// changes or at a low rate. All HUD panels are faint glass so the room and the palace objects stay visible.
    /// </summary>
    public sealed class SessionViews
    {
        private const float ToastFadeIn = 0.25f;
        private const float ToastFadeOut = 0.5f;
        private const float ToastMaxWidth = 620;
        private const int TaskTextSize = 26;
        private const int TaskTermSize = 34; // the term to find is what the user searches for: largest text on the HUD

        private readonly SessionController _app;
        private readonly Transform _head;
        private readonly PalaceRoom _room;
        private readonly SurfacePlacer _placer;

        // Status pill
        private readonly GameObject _hudTop;
        private readonly Image _linkLight;
        private readonly Text _focusLabel;
        private readonly Text _focusText;
        private readonly GameObject _focusBar;
        private readonly Image _focusFill;
        private readonly Text _timer;
        private readonly Text _progress;

        // Toast pill
        private readonly Text _toast;
        private readonly LayoutElement _toastWidth;
        private readonly CanvasGroup _toastGroup;
        private float _toastShownAt;
        private float _toastUntil;

        // Task card
        private readonly GameObject _taskCard;
        private readonly Text _taskMode;
        private readonly Text _taskMain;
        private readonly Text _taskHint;
        private readonly GameObject _answerBar;
        private readonly Image _answerFill;

        private readonly PalmMenu _toolbar;
        private readonly Button _placeHereButton;
        private readonly Button _testButton;
        private readonly Button _giveUpButton;
        private readonly Button _pauseButton;
        private readonly Button _endButton;

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
            // 760 x 440 units at 1.2 m: about 35 x 21 degrees, inside the glasses' view. The middle stays empty for the room.
            var hud = Ui.WorldCanvas("HUD", _head, new Vector2(760, 440), camera, interactive: false);
            hud.transform.localPosition = new Vector3(0, 0, 1.2f);
            hud.transform.localScale = Vector3.one * 0.001f; // display only, so it stays far (easier on the eyes) and keeps its size
            hud.sortingOrder = 10;

            // Status pill, top centre: link dot · focus · bar · timer · progress. Width follows its content.
            var status = Ui.Card("Status", hud.transform, Ui.Panel, 24, 20, 0, 10, horizontal: true);
            _hudTop = status.gameObject;
            status.anchorMin = status.anchorMax = new Vector2(0.5f, 1);
            status.pivot = new Vector2(0.5f, 1);
            status.anchoredPosition = new Vector2(0, -16);
            status.sizeDelta = new Vector2(0, 48);
            status.gameObject.AddComponent<ContentSizeFitter>().horizontalFit = ContentSizeFitter.FitMode.PreferredSize;

            _linkLight = Ui.Box("Link", status, Ui.Red);
            _linkLight.raycastTarget = false;
            Ui.Corners(_linkLight, 7);
            var dot = _linkLight.gameObject.AddComponent<LayoutElement>();
            dot.preferredWidth = dot.minWidth = dot.preferredHeight = 14;
            _focusLabel = Ui.Label(status, string.Empty, 18, Ui.Muted);
            _focusText = Ui.Label(status, string.Empty, 22, style: FontStyle.Bold);
            _focusFill = Ui.Bar(status, 8, Ui.Accent);
            _focusBar = _focusFill.transform.parent.gameObject;
            _focusBar.GetComponent<LayoutElement>().preferredWidth = 90;
            Separator(status);
            _timer = Ui.Label(status, "00:00", 22);
            Separator(status);
            _progress = Ui.Label(status, string.Empty, 20);

            // Toast pill, just under the status pill; fades in and out.
            var toast = Ui.Card("Toast", hud.transform, new Color(Ui.Accent.r, Ui.Accent.g, Ui.Accent.b, 0.45f), 22, 22, 10, 0, horizontal: true);
            toast.anchorMin = toast.anchorMax = new Vector2(0.5f, 1);
            toast.pivot = new Vector2(0.5f, 1);
            toast.anchoredPosition = new Vector2(0, -78);
            var toastFit = toast.gameObject.AddComponent<ContentSizeFitter>();
            toastFit.horizontalFit = toastFit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _toastGroup = toast.gameObject.AddComponent<CanvasGroup>();
            _toastGroup.alpha = 0;
            _toast = Ui.Label(toast, string.Empty, 22, Color.white, TextAnchor.MiddleCenter, FontStyle.Bold);
            _toastWidth = _toast.gameObject.AddComponent<LayoutElement>();

            // Task card, below the reticle: small mode label, main line, muted hint, answer-time bar in the test.
            var task = Ui.Card("Task", hud.transform, Ui.Panel, 20, 22, 14, 4, horizontal: false);
            _taskCard = task.gameObject;
            task.anchorMin = task.anchorMax = new Vector2(0.5f, 0.5f);
            task.pivot = new Vector2(0.5f, 1);
            task.anchoredPosition = new Vector2(0, -80);
            task.sizeDelta = new Vector2(560, 0);
            task.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _taskMode = Ui.Label(task, string.Empty, 16, Ui.AccentSoft, style: FontStyle.Bold);
            _taskMain = Ui.Label(task, string.Empty, TaskTextSize, style: FontStyle.Bold);
            _taskHint = Ui.Label(task, string.Empty, 19, Ui.Muted);
            _answerFill = Ui.Bar(task, 6, Ui.Accent);
            _answerBar = _answerFill.transform.parent.gameObject;

            var reticle = Ui.Box("Reticle", hud.transform, new Color(1, 1, 1, 0.9f));
            reticle.rectTransform.sizeDelta = new Vector2(10, 10);
            reticle.raycastTarget = false;
            Reticle = reticle.rectTransform;

            // ---- Session menu: on the left palm (look at your palm), poked with the right hand ----
            var toolbar = Ui.WorldCanvas("Palm menu", worldRoot, new Vector2(300, 330), camera);
            var bar = Ui.Column("Buttons", toolbar.transform, 10, 10, Ui.Background);
            Ui.Stretch(bar);
            _placeHereButton = Ui.Button(bar, string.Empty, PlaceNow, Ui.Accent, 70, 22);
            _testButton = Ui.Button(bar, string.Empty, () => _app.StartTest(), Ui.Accent, 70, 24);
            _giveUpButton = Ui.Button(bar, string.Empty, () => _app.GiveUp(), Ui.Accent, 70, 24);
            _pauseButton = Ui.Button(bar, string.Empty, TogglePause, Ui.Panel, 70, 24);
            _endButton = Ui.Button(bar, string.Empty, () => _app.EndSession(), Ui.Red, 70, 24);
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
            var dialog = Ui.Column("Body", _dialogCanvas.transform, 12, 24, Ui.Panel, edge: true);
            Ui.Stretch(dialog);
            _dialogTitle = Ui.Label(dialog, string.Empty, 32, style: FontStyle.Bold);
            _dialogBody = Ui.Label(dialog, string.Empty, 26);
            var dialogRow = Ui.Row("Buttons", dialog, 12);
            _dialogA = Ui.Button(dialogRow, string.Empty, () => _dialogActionA?.Invoke(), Ui.Accent, 64, 24);
            _dialogB = Ui.Button(dialogRow, string.Empty, () => _dialogActionB?.Invoke(), Ui.Raised, 64, 24);

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
            if (visible)
            {
                // Labels that Refresh doesn't rewrite; the language can only change on the setup screen.
                _focusLabel.text = T("Skupienie", "Focus");
                Ui.SetButtonText(_testButton, T("Zacznij test", "Start test"));
                Ui.SetButtonText(_giveUpButton, T("Nie pamiętam", "I don't remember"));
                Ui.SetButtonText(_endButton, T("Zakończ", "End"));
            }

            _taskCard.SetActive(false);
            _dialogCanvas.gameObject.SetActive(false);
            _shownModal = (Modal)(-1);
        }

        public void Toast(string text, float seconds = 4)
        {
            _toast.text = text;
            _toastWidth.preferredWidth = Mathf.Min(_toast.preferredWidth, ToastMaxWidth); // short toasts stay a small pill, long ones wrap
            _toastShownAt = Time.time;
            _toastUntil = Time.time + seconds;
        }

        /// <summary>Every frame: objects, dialog state (cheap unless something changed), toast fade, answer-time bar.</summary>
        public void Tick()
        {
            TickToast();
            SyncObjects();
            UpdateModal();

            if (_answerBar.activeSelf)
            {
                var left = (float)(_app.AnswerTimeLeft / _app.AdaptiveConfig.AnswerTime(_app.SpeedUp));
                Ui.SetFill(_answerFill, left);
                _answerFill.color = left < 0.25f ? Ui.Yellow : Ui.Accent;
            }
        }

        /// <summary>Toast fade in/out. Also called outside the session, since toasts show on every screen.</summary>
        public void TickToast()
        {
            if (_toast.text.Length == 0)
            {
                return;
            }

            var now = Time.time;
            if (now > _toastUntil)
            {
                _toast.text = string.Empty;
                _toastGroup.alpha = 0;
                return;
            }

            _toastGroup.alpha = Mathf.Clamp01((now - _toastShownAt) / ToastFadeIn) * Mathf.Clamp01((_toastUntil - now) / ToastFadeOut);
        }

        /// <summary>A few times per second: numbers on the HUD.</summary>
        public void Refresh()
        {
            var focus = _app.Focus;
            var reconnecting = _app.Connection?.State == EegConnectionState.Reconnecting;
            _focusText.text = focus.HasValue ? $"{focus.Value:F0}%"
                : reconnecting ? T("łączenie…", "reconnecting…")
                : _app.EegEnabledForSession ? T("brak sygnału", "no signal")
                : T("bez EEG", "no EEG");
            _focusBar.SetActive(focus.HasValue);
            Ui.SetFill(_focusFill, focus.HasValue ? (float)focus.Value / 100f : 0);
            _focusFill.color = _app.FocusLevel == FocusLevel.Low ? Ui.Yellow : Ui.Accent; // calm purple, amber only when low

            var light = _app.Light;
            _linkLight.color = light == ConnectionLight.Green ? Ui.Green : light == ConnectionLight.Yellow ? Ui.Yellow : Ui.Red;

            var palace = _app.Palace;
            _timer.text = Ui.Clock(_app.SessionSeconds) + (_app.SpeedUp ? T("  tempo+", "  speed+") : string.Empty);
            var total = palace?.Objects.Count ?? 0;
            _progress.text = palace == null ? string.Empty
                : palace.Mode == PalaceMode.Preparation ? T("Rozmieszczone ", "Placed ") + $"{_placer.PlacedCount}/{total}"
                : palace.Mode == PalaceMode.Learning ? T("Obejrzane ", "Viewed ") + $"{palace.ViewedCount}/{total}"
                : T("Opanowane ", "Mastered ") + $"{palace.MasteredCount}/{total}";

            var mode = palace?.Mode ?? PalaceMode.Finished;
            _placeHereButton.gameObject.SetActive(mode == PalaceMode.Preparation);
            Ui.SetButtonText(_placeHereButton, _placer.IsScanning ? T("Rozmieść teraz", "Place now") : T("Rozmieść resztę tutaj", "Place the rest here"));
            _testButton.gameObject.SetActive(mode == PalaceMode.Learning);
            _giveUpButton.gameObject.SetActive(mode == PalaceMode.Test && palace.Step == TestStep.Find);
            Ui.SetButtonText(_pauseButton, _app.IsPaused ? T("Wznów", "Resume") : T("Pauza", "Pause"));
            _taskCard.SetActive(palace != null && !_app.IsPaused && ShowTask(palace));

            if (_shownModal == Modal.Break)
            {
                _dialogBody.text = T($"Odpocznij, rozluźnij wzrok. Zostało {Ui.Clock(_app.BreakTimeLeft)}.",
                    $"Rest and relax your eyes. {Ui.Clock(_app.BreakTimeLeft)} left.");
            }
        }

        /// <summary>Fills the task card with what the user should do now. False when there is nothing to show.</summary>
        private bool ShowTask(PalaceSession palace)
        {
            switch (palace.Mode)
            {
                case PalaceMode.Preparation when _placer.IsScanning && _placer.HasPlaneDetection:
                    SetTask(T("SKANOWANIE POKOJU", "ROOM SCAN"),
                        T("Obejdź pokój i rozglądaj się", "Walk around the room and look around"),
                        T($"Meble, podłoga, ściany · znalezione powierzchnie: {_placer.SurfaceCount} · rozmieszczę za {_placer.ScanSecondsLeft:F0} s\nMenu: spójrz na lewą dłoń.",
                            $"Furniture, floor, walls · surfaces found: {_placer.SurfaceCount} · placing in {_placer.ScanSecondsLeft:F0} s\nMenu: look at your left palm."));
                    return true;
                case PalaceMode.Preparation:
                    SetTask(T("ROZMIESZCZANIE", "PLACING"),
                        _placer.HasPlaneDetection
                            ? T("Rozmieszczam przedmioty po całym pokoju…", "Placing objects around the room…")
                            : T("Rozmieszczam przedmioty wokół Ciebie…", "Placing objects around you…"));
                    return true;
                case PalaceMode.Learning:
                    var startsIn = _app.TestStartsIn;
                    if (startsIn.HasValue)
                    {
                        SetTask(T("NAUKA", "LEARNING"), T("Wszystko obejrzane", "All viewed"),
                            T($"Test za {startsIn.Value:F0} s", $"Test in {startsIn.Value:F0} s"));
                    }
                    else
                    {
                        SetTask(T("NAUKA", "LEARNING"), T("Podejdź do przedmiotów i patrz na nie", "Walk up to the objects and look at them"),
                            T("Każdy przedmiot pokaże swoją parę. Menu: spójrz na lewą dłoń.", "Each object shows its pair. Menu: look at your left palm."));
                    }

                    return true;
                case PalaceMode.Test when palace.Step == TestStep.Find:
                    SetTask(T("TEST · ZNAJDŹ", "TEST · FIND"), palace.Objects[palace.CurrentObject].Prompt,
                        T("Podejdź do tego przedmiotu i dotknij go palcem.", "Walk up to that object and touch it with your finger."),
                        size: TaskTermSize, answerBar: true);
                    return true;
                case PalaceMode.Test when palace.Step == TestStep.Feedback:
                    if (palace.LastCorrect)
                    {
                        SetTask("TEST", T("Dobrze!", "Correct!"), color: Ui.Green);
                    }
                    else
                    {
                        SetTask("TEST", T("To nie ten", "Not that one"), T("Właściwy przedmiot świeci.", "The right object is glowing."), color: Ui.Yellow);
                    }

                    return true;
                default:
                    return false;
            }
        }

        private void SetTask(string mode, string main, string hint = null, Color? color = null, int size = TaskTextSize, bool answerBar = false)
        {
            _taskMode.text = mode;
            _taskMain.text = main;
            _taskMain.fontSize = size;
            _taskMain.color = color ?? Ui.TextColor;
            _taskHint.text = hint ?? string.Empty;
            _taskHint.gameObject.SetActive(hint != null);
            _answerBar.SetActive(answerBar);
        }

        private static void Separator(Transform parent) => Ui.Label(parent, "·", 22, Ui.Muted);

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
                    Dialog(T("Może czas na krótką przerwę?", "Time for a short break?"),
                        T("Twoje skupienie od dłuższej chwili jest niskie.", "Your focus has been low for a while."),
                        T("5-minutowa przerwa", "5-minute break"), () => AnswerPrompt(() => _app.AnswerBreakPrompt(true)),
                        T("Kontynuuj mimo wszystko", "Keep going anyway"), () => AnswerPrompt(() => _app.AnswerBreakPrompt(false)));
                    break;
                case Modal.SessionLimit:
                    Dialog(T("Sesja trwa już 25 minut", "You've been learning for 25 minutes"),
                        T("Dłuższa nauka bez przerwy zwykle mniej daje. Zakończyć i zobaczyć podsumowanie?",
                            "Studying longer without a break usually helps less. End and see the summary?"),
                        T("Zakończ sesję", "End session"), () => AnswerPrompt(_app.EndSession),
                        T("Jeszcze chwilę", "A bit longer"), () => AnswerPrompt(null));
                    break;
                case Modal.Break:
                    Dialog(T("Przerwa", "Break"), string.Empty, T("Wróć do nauki", "Back to learning"), _app.EndBreak, null, null);
                    break;
                case Modal.Pause:
                    Dialog(T("Pauza", "Paused"), T("Czas sesji jest wstrzymany.", "The session timer is paused."),
                        T("Wznów", "Resume"), _app.Resume, T("Zakończ", "End"), _app.EndSession);
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
