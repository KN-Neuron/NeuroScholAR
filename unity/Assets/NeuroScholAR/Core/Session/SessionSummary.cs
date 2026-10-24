using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace NeuroScholAR.Core.Session
{
    /// <summary>Wire format sent to the backend (spec "Session summary"). Only built when the user exports.</summary>
    public sealed class SessionSummary
    {
        [JsonProperty("session_id")] public string SessionId;
        [JsonProperty("started_at")] public DateTimeOffset StartedAt;
        [JsonProperty("duration_s")] public int DurationS;
        [JsonProperty("eeg_enabled")] public bool EegEnabled;
        [JsonProperty("learn_time_s")] public int LearnTimeS;
        [JsonProperty("test_time_s")] public int TestTimeS;
        [JsonProperty("accuracy_pct")] public double AccuracyPct;
        [JsonProperty("focus_timeline")] public List<FocusPoint> FocusTimeline = new List<FocusPoint>();
        [JsonProperty("answers")] public List<AnswerPoint> Answers = new List<AnswerPoint>();

        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);
        public static SessionSummary FromJson(string json) => JsonConvert.DeserializeObject<SessionSummary>(json);
    }

    public sealed class FocusPoint
    {
        [JsonProperty("t_s")] public int TS;
        [JsonProperty("focus")] public int Focus;
    }

    public sealed class AnswerPoint
    {
        [JsonProperty("pair_id")] public string PairId;
        [JsonProperty("correct")] public bool Correct;

        /// <summary>0–100, or null when there was no EEG at that moment.</summary>
        [JsonProperty("focus_at_answer")] public int? FocusAtAnswer;
    }

    public sealed class HardTerm
    {
        public string Prompt;
        public int Incorrect;
        public int Total;
    }

    /// <summary>What the summary screen shows. Computed on the device; never sent anywhere.</summary>
    public sealed class SummaryAnalytics
    {
        public double AccuracyPct;
        public int AnswerCount;
        public double LearnTimeS;
        public double TestTimeS;
        public List<HardTerm> HardestTerms = new List<HardTerm>();
        public double? AverageFocus;
        public FocusPoint HighestFocus;
        public FocusPoint LowestFocus;

        /// <summary>Point-biserial correlation (Pearson with a 0/1 variable) of focus at answer vs correctness. Null if undefined.</summary>
        public double? FocusCorrectnessCorrelation;

        /// <summary>Plain-language reading of the correlation for the summary screen.</summary>
        public string CorrelationText;

        public static SummaryAnalytics From(SessionSummary summary, IReadOnlyDictionary<string, string> promptOf, int maxHardTerms = 3)
        {
            var a = new SummaryAnalytics
            {
                AccuracyPct = summary.AccuracyPct,
                AnswerCount = summary.Answers.Count,
                LearnTimeS = summary.LearnTimeS,
                TestTimeS = summary.TestTimeS,
            };

            a.HardestTerms = summary.Answers
                .GroupBy(x => x.PairId)
                .Select(g => new HardTerm
                {
                    Prompt = promptOf != null && promptOf.TryGetValue(g.Key, out var p) ? p : g.Key,
                    Incorrect = g.Count(x => !x.Correct),
                    Total = g.Count(),
                })
                .Where(h => h.Incorrect > 0)
                .OrderByDescending(h => (double)h.Incorrect / h.Total)
                .ThenByDescending(h => h.Incorrect)
                .Take(maxHardTerms)
                .ToList();

            if (summary.FocusTimeline.Count > 0)
            {
                a.AverageFocus = summary.FocusTimeline.Average(f => f.Focus);
                a.HighestFocus = summary.FocusTimeline.OrderByDescending(f => f.Focus).ThenBy(f => f.TS).First();
                a.LowestFocus = summary.FocusTimeline.OrderBy(f => f.Focus).ThenBy(f => f.TS).First();
            }

            var withFocus = summary.Answers.Where(x => x.FocusAtAnswer.HasValue).ToList();
            a.FocusCorrectnessCorrelation = PointBiserial(withFocus.Select(x => (double)x.FocusAtAnswer.Value).ToList(),
                withFocus.Select(x => x.Correct).ToList());
            a.CorrelationText = Describe(a.FocusCorrectnessCorrelation, withFocus.Count, summary.EegEnabled);
            return a;
        }

        /// <summary>Returns null with fewer than 3 points or when either variable has no variance.</summary>
        public static double? PointBiserial(IReadOnlyList<double> x, IReadOnlyList<bool> y)
        {
            var n = x.Count;
            if (n < 3)
            {
                return null;
            }

            var ys = y.Select(b => b ? 1.0 : 0.0).ToList();
            var mx = x.Average();
            var my = ys.Average();
            double sxy = 0, sxx = 0, syy = 0;
            for (var i = 0; i < n; i++)
            {
                sxy += (x[i] - mx) * (ys[i] - my);
                sxx += (x[i] - mx) * (x[i] - mx);
                syy += (ys[i] - my) * (ys[i] - my);
            }

            return sxx < 1e-12 || syy < 1e-12 ? (double?)null : sxy / Math.Sqrt(sxx * syy);
        }

        private static string Describe(double? r, int n, bool eeg)
        {
            if (!eeg)
            {
                return "Sesja bez EEG: brak danych o skupieniu.";
            }

            if (!r.HasValue)
            {
                return n < 3
                    ? "Za mało odpowiedzi z pomiarem skupienia, by policzyć zależność."
                    : "Brak zależności do policzenia (wszystkie odpowiedzi takie same lub stałe skupienie).";
            }

            var strength = Math.Abs(r.Value) < 0.2 ? "brak wyraźnego związku" : Math.Abs(r.Value) < 0.5 ? "umiarkowany związek" : "silny związek";
            var direction = r.Value > 0 ? "wyższe skupienie sprzyjało poprawnym odpowiedziom" : "wyższe skupienie nie przekładało się na poprawność";
            return Math.Abs(r.Value) < 0.2 ? $"r = {r.Value:F2}: {strength}." : $"r = {r.Value:F2}: {strength}, {direction}.";
        }
    }
}
