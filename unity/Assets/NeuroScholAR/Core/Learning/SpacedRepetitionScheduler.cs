using System;
using System.Collections.Generic;
using System.Linq;

namespace NeuroScholAR.Core.Learning
{
    public sealed class PairProgress
    {
        public PairProgress(string pairId) => PairId = pairId;

        public string PairId { get; }
        public int Correct { get; internal set; }
        public int Incorrect { get; internal set; }

        /// <summary>Wrong answers since the last correct one. Drives the extra repetitions.</summary>
        public int WrongStreak { get; internal set; }
    }

    /// <summary>
    /// In-session spaced repetition, deliberately simple (full SM-2 day intervals are post-MVP):
    /// every round holds each unmastered pair once, plus one extra copy per consecutive wrong answer
    /// (capped), so missed pairs come back more often. A pair is mastered after <see cref="MasteredAfter"/>
    /// correct answers. Mastered pairs still return, every <see cref="MasteredEveryRounds"/>-th round.
    /// </summary>
    public sealed class SpacedRepetitionScheduler
    {
        private readonly Dictionary<string, PairProgress> _progress;
        private readonly List<string> _order;
        private readonly Random _random;

        public SpacedRepetitionScheduler(IEnumerable<string> pairIds, int seed = 0, int masteredAfter = 3,
            int masteredEveryRounds = 3, int maxExtraCopies = 2)
        {
            _order = pairIds?.Distinct().ToList() ?? throw new ArgumentNullException(nameof(pairIds));
            if (_order.Count == 0)
            {
                throw new ArgumentException("Nothing to schedule", nameof(pairIds));
            }

            _progress = _order.ToDictionary(id => id, id => new PairProgress(id));
            _random = new Random(seed);
            MasteredAfter = masteredAfter;
            MasteredEveryRounds = masteredEveryRounds;
            MaxExtraCopies = maxExtraCopies;
        }

        public int MasteredAfter { get; }
        public int MasteredEveryRounds { get; }
        public int MaxExtraCopies { get; }

        /// <summary>Rounds built so far.</summary>
        public int Rounds { get; private set; }

        public bool AllMastered => _progress.Values.All(IsMastered);
        public IReadOnlyCollection<PairProgress> Progress => _progress.Values;

        public bool IsMastered(PairProgress p) => p.Correct >= MasteredAfter;
        public bool IsMastered(string pairId) => IsMastered(_progress[pairId]);

        /// <summary>Builds the next round of pair ids.</summary>
        /// <param name="includeMastered">High-focus speed-up: show mastered pairs too (more material per session).</param>
        public IReadOnlyList<string> NextRound(bool includeMastered = false)
        {
            var masteredTurn = includeMastered || Rounds % MasteredEveryRounds == 0;
            var round = new List<string>();
            foreach (var id in _order)
            {
                var p = _progress[id];
                if (IsMastered(p))
                {
                    if (masteredTurn)
                    {
                        round.Add(id);
                    }

                    continue;
                }

                round.Add(id);
                for (var extra = 0; extra < Math.Min(MaxExtraCopies, p.WrongStreak); extra++)
                {
                    round.Add(id);
                }
            }

            if (round.Count == 0)
            {
                round.AddRange(_order); // everything mastered and not its turn: still never return an empty round
            }

            Rounds++;
            return Shuffle(round);
        }

        public void Record(string pairId, bool correct)
        {
            if (!_progress.TryGetValue(pairId, out var p))
            {
                throw new ArgumentException($"Unknown pair {pairId}", nameof(pairId));
            }

            if (correct)
            {
                p.Correct++;
                p.WrongStreak = 0;
            }
            else
            {
                p.Incorrect++;
                p.WrongStreak++;
            }
        }

        /// <summary>
        /// Random order where a repeat is never asked twice in a row, unless unavoidable. Greedy: place the most
        /// frequent pair now if waiting would force neighbours, otherwise pick randomly (weighted) among the others.
        /// </summary>
        private List<string> Shuffle(List<string> items)
        {
            var counts = new Dictionary<string, int>();
            var ids = new List<string>();
            foreach (var id in items)
            {
                if (!counts.ContainsKey(id))
                {
                    counts[id] = 0;
                    ids.Add(id);
                }

                counts[id]++;
            }

            var result = new List<string>(items.Count);
            string last = null;
            for (var remaining = items.Count; remaining > 0; remaining--)
            {
                var most = ids.OrderByDescending(id => counts[id]).First();
                string pick;
                if (most != last && counts[most] * 2 > remaining)
                {
                    pick = most;
                }
                else
                {
                    var candidates = ids.Where(id => counts[id] > 0 && id != last).ToList();
                    if (candidates.Count == 0)
                    {
                        candidates.Add(last);
                    }

                    var ticket = _random.Next(candidates.Sum(id => counts[id]));
                    pick = candidates.First(id => (ticket -= counts[id]) < 0);
                }

                result.Add(pick);
                counts[pick]--;
                last = pick;
            }

            return result;
        }
    }
}
