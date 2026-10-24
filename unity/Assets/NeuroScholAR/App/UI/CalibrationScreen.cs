using NeuroScholAR.Core.Session;
using UnityEngine;
using UnityEngine.UI;

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

        /// <param name="press">Game press at the session clock's "now" (the same clock the game is timed with).</param>
        public CalibrationScreen(Transform canvas, SessionController app, System.Action press)
        {
            _app = app;
            var column = Ui.Column("Calibration", canvas, 16, 32, Ui.Background);
            Ui.Stretch(column);
            _root = column.gameObject;
            Ui.Label(column, "Kalibracja (30 s)", 42, style: FontStyle.Bold);
            Ui.Label(column, "Siedź spokojnie i śledź kropkę wzrokiem, bez ruszania głową. " +
                             "Gdy zmieni kolor na zielony, dotknij palcem przycisku „Teraz!”.", 26, Ui.Muted);

            var arena = Ui.Box("Arena", column, new Color(0, 0, 0, 0.35f));
            Ui.Height(arena, 300);
            _target = Ui.Box("Target", arena.transform, Ui.Accent);
            _target.rectTransform.sizeDelta = new Vector2(56, 56);

            _progress = Ui.Bar(column, 24, Ui.Accent);
            _status = Ui.Label(column, string.Empty, 26);
            Ui.Button(column, "Teraz!", press, Ui.Green, 90, 34);
            Ui.Button(column, "Przerwij", () => _app.AbortCalibration(), Ui.Panel, 60);
        }

        public void SetVisible(bool visible) => _root.SetActive(visible);

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
            _status.text = game == null ? string.Empty : $"Trafienia: {game.Hits}/{game.Targets}   Postęp: {_app.CalibrationProgress:P0}";
        }

        /// <summary>Select press during calibration counts as a game press.</summary>
        public void Press(double now) => _app.Game?.Press(now);
    }
}
