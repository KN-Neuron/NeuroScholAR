using System;
using System.Linq;
using NeuroScholAR.Core.Adaptive;
using NeuroScholAR.Core.Session;
using UnityEngine;
using UnityEngine.UI;

namespace NeuroScholAR.App.UI
{
    /// <summary>Results: accuracy, learn vs test time, hardest terms, focus stats and chart, focus/correctness link, export.</summary>
    public sealed class SummaryScreen
    {
        private const int ChartW = 640;
        private const int ChartH = 180;

        private readonly SessionController _app;
        private readonly SummaryExporter _exporter;
        private readonly Action _onNewSession;
        private readonly GameObject _root;
        private readonly Text _stats;
        private readonly Text _focus;
        private readonly RawImage _chart;
        private readonly Text _status;
        private Texture2D _texture;

        public SummaryScreen(Transform canvas, SessionController app, SummaryExporter exporter, Action onNewSession)
        {
            _app = app;
            _exporter = exporter;
            _onNewSession = onNewSession;
            var column = Ui.Column("Summary", canvas, 12, 28, Ui.Background);
            Ui.Stretch(column);
            _root = column.gameObject;
            Ui.Label(column, "Podsumowanie sesji", 40, style: FontStyle.Bold);
            // Buttons right under the title: near eye level, where the glasses' narrow view and the fingers both reach.
            // The long results text below can grow without pushing them off the panel.
            var row = Ui.Row("Actions", column, 14);
            Ui.Button(row, "Zapisz wyniki", Save, Ui.Panel, 84, 26);
            Ui.Button(row, "Eksportuj CSV", Export, Ui.Accent, 84, 26);
            Ui.Button(row, "Nowa sesja", () => _onNewSession(), Ui.Green, 84, 26);
            _status = Ui.Label(column, string.Empty, 22, Ui.Yellow);
            _stats = Ui.Label(column, string.Empty, 24);
            _chart = Ui.Rect("Chart", column).gameObject.AddComponent<RawImage>();
            _chart.raycastTarget = false;
            Ui.Height(_chart, ChartH);
            _focus = Ui.Label(column, string.Empty, 22, Ui.Muted);
        }

        public void SetVisible(bool visible) => _root.SetActive(visible);

        public void Show()
        {
            var s = _app.Summary;
            var a = _app.Analytics;
            if (s == null || a == null)
            {
                return;
            }

            var hardest = a.HardestTerms.Count == 0
                ? "brak błędów"
                : string.Join(", ", a.HardestTerms.Select(h => $"{h.Prompt} ({h.Incorrect}/{h.Total} źle)"));
            _stats.text =
                $"Poprawne odpowiedzi: {a.AccuracyPct:F0}% ({a.AnswerCount} odpowiedzi)\n" +
                $"Czas nauki: {Ui.Clock(a.LearnTimeS)}   Czas testu: {Ui.Clock(a.TestTimeS)}   Razem: {Ui.Clock(s.DurationS)}\n" +
                $"Najtrudniejsze: {hardest}";

            _focus.text = !s.EegEnabled || !a.AverageFocus.HasValue
                ? a.CorrelationText
                : $"Średnie skupienie: {a.AverageFocus:F0}%   Najwyższe: {a.HighestFocus.Focus}% (w {Ui.Clock(a.HighestFocus.TS)})   " +
                  $"Najniższe: {a.LowestFocus.Focus}% (w {Ui.Clock(a.LowestFocus.TS)})\nSkupienie a poprawność: {a.CorrelationText}";
            DrawChart(s, _app.AdaptiveConfig);
            _status.text = string.Empty;
        }

        private void Save()
        {
            try
            {
                _status.text = "Zapisano: " + _exporter.SaveLocal(_app.Summary);
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
            {
                _status.text = "Nie udało się zapisać: " + e.Message;
            }
        }

        private async void Export()
        {
            _status.text = "Eksportowanie…";
            try
            {
                var result = await _exporter.ExportAsync(_app.Summary);
                switch (result.Status)
                {
                    case ExportStatus.Exported:
                        _status.text = "Wyeksportowano CSV: " + result.CsvPath;
                        break;
                    case ExportStatus.QueuedForRetry:
                        _status.text = "Serwer nieosiągalny: wyniki zapisane lokalnie, eksport zostanie ponowiony.";
                        break;
                    default:
                        _status.text = "Brak adresu serwera (appconfig.json): wyniki zapisane lokalnie.";
                        break;
                }
            }
            catch (Exception e)
            {
                _status.text = "Błąd eksportu: " + e.Message;
            }
        }

        /// <summary>Focus over time with the 35 % / 70 % rule thresholds. Drawn once into a texture (no per-frame cost).</summary>
        private void DrawChart(SessionSummary s, AdaptiveConfig config)
        {
            _texture ??= new Texture2D(ChartW, ChartH, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[ChartW * ChartH];
            var background = new Color32(20, 22, 30, 255);
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = background;
            }

            void HLine(double focus, Color32 color)
            {
                var y = (int)(focus / 100 * (ChartH - 1));
                for (var x = 0; x < ChartW; x += 3)
                {
                    pixels[y * ChartW + x] = color;
                }
            }

            HLine(config.LowFocusThreshold, new Color32(235, 66, 61, 255));
            HLine(config.HighFocusThreshold, new Color32(51, 199, 89, 255));

            var points = s.FocusTimeline;
            if (points.Count > 1)
            {
                var span = Math.Max(1, points[points.Count - 1].TS - points[0].TS);
                int Px(int t) => (int)((double)(t - points[0].TS) / span * (ChartW - 1));
                int Py(int f) => (int)(f / 100.0 * (ChartH - 1));
                for (var i = 1; i < points.Count; i++)
                {
                    if (points[i].TS - points[i - 1].TS > 5)
                    {
                        continue; // gap (disconnect / break): don't draw a misleading line across it
                    }

                    Line(pixels, Px(points[i - 1].TS), Py(points[i - 1].Focus), Px(points[i].TS), Py(points[i].Focus), new Color32(64, 140, 242, 255));
                }
            }

            _texture.SetPixels32(pixels);
            _texture.Apply();
            _chart.texture = _texture;
        }

        private static void Line(Color32[] pixels, int x0, int y0, int x1, int y1, Color32 color)
        {
            int dx = Math.Abs(x1 - x0), dy = -Math.Abs(y1 - y0), sx = x0 < x1 ? 1 : -1, sy = y0 < y1 ? 1 : -1, err = dx + dy;
            while (true)
            {
                for (var t = 0; t < 2; t++)
                {
                    var y = Mathf.Clamp(y0 + t, 0, ChartH - 1);
                    pixels[y * ChartW + Mathf.Clamp(x0, 0, ChartW - 1)] = color;
                }

                if (x0 == x1 && y0 == y1)
                {
                    break;
                }

                var e2 = 2 * err;
                if (e2 >= dy)
                {
                    err += dy;
                    x0 += sx;
                }

                if (e2 <= dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }
    }
}
