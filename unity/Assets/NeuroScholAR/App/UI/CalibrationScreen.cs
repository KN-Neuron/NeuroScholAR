using NeuroScholAR.Core.Session;
using UnityEngine;
using UnityEngine.UI;
using static NeuroScholAR.App.UI.Lang;

namespace NeuroScholAR.App.UI
{
    /// <summary>
    /// 30-second baseline as a mini-game: follow the dot with your eyes, press "Teraz!" (poke) when it turns
    /// green. A controller / select press anywhere counts too.
    /// </summary>
    public sealed class CalibrationScreen
    {
        private const float Radius = 180;

        private readonly SessionController _app;
        private readonly GameObject _root;
        private readonly Image _target;
        private readonly Image _progress;
        private readonly Text _status;
        private readonly Text _title;
        private readonly Text _instructions;
        private readonly Button _now;
        private readonly Button _abort;

        /// <param name="press">Game press at the session clock's "now" (the same clock the game is timed with).</param>
        public CalibrationScreen(Transform canvas, SessionController app, System.Action press)
        {
            _app = app;
            var column = Ui.Column("Calibration", canvas, 16, 32, Ui.Background);
            Ui.Stretch(column);
            _root = column.gameObject;
            _title = Ui.Label(column, string.Empty, 42, style: FontStyle.Bold);
            _instructions = Ui.Label(column, string.Empty, 26, Ui.Muted);

            var arena = Ui.Box("Arena", column, new Color(0, 0, 0, 0.35f));
            Ui.Height(arena, 300);
            _target = Ui.Box("Target", arena.transform, Ui.Accent);
            _target.rectTransform.sizeDelta = new Vector2(56, 56);

            _progress = Ui.Bar(column, 24, Ui.Accent);
            _status = Ui.Label(column, string.Empty, 26);
            _now = Ui.Button(column, string.Empty, press, Ui.Green, 90, 34);
            _abort = Ui.Button(column, string.Empty, () => _app.AbortCalibration(), Ui.Panel, 60);
            Translate();
        }

        public void SetVisible(bool visible)
        {
            _root.SetActive(visible);
            if (visible)
            {
                Translate();
            }
        }

        private void Translate()
        {
            _title.text = T("Kalibracja (30 s)", "Calibration (30 s)");
            _instructions.text = T(
                "Siedź spokojnie i śledź kropkę wzrokiem, bez ruszania głową. Gdy zmieni kolor na zielony, dotknij palcem przycisku „Teraz!”.",
                "Sit still and follow the dot with your eyes, without moving your head. When it turns green, tap the \"Now!\" button.");
            Ui.SetButtonText(_now, T("Teraz!", "Now!"));
            Ui.SetButtonText(_abort, T("Przerwij", "Cancel"));
        }

        /// <summary>Every frame while visible (cheap: no allocations except the status text at 4 Hz).</summary>
        public void Animate(double now)
        {
            var game = _app.Game;
            if (game == null)
            {
                return;
            }

            var angle = (float)CalibrationGame.AngleAt(now) * Mathf.Deg2Rad * 4;
            _target.rectTransform.anchoredPosition = new Vector2(Mathf.Sin(angle) * Radius, Mathf.Sin(angle * 0.5f) * Radius * 0.5f);
            _target.color = game.IsGreen ? Ui.Green : Ui.Accent;
            Ui.SetFill(_progress, (float)_app.CalibrationProgress);
        }

        public void Refresh()
        {
            var game = _app.Game;
            _status.text = game == null ? string.Empty
                : T($"Trafienia: {game.Hits}/{game.Targets}   Postęp: {_app.CalibrationProgress:P0}",
                    $"Hits: {game.Hits}/{game.Targets}   Progress: {_app.CalibrationProgress:P0}");
        }

        /// <summary>Select press during calibration counts as a game press.</summary>
        public void Press(double now) => _app.Game?.Press(now);
    }
}
