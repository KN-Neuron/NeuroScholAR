using System;
using System.Collections.Generic;
using System.Linq;

namespace NeuroScholAR.Core.Learning
{
    public enum PalaceMode
    {
        Preparation,
        Learning,
        Test,
        Finished,
    }

    public enum TestStep
    {
        /// <summary>No question active (outside Test, or before the first question).</summary>
        Idle,

        /// <summary>A prompt is shown; the user walks to the object that holds it and selects it.</summary>
        Find,

        /// <summary>Answer recorded; the target (and a wrong pick) are highlighted until the next question.</summary>
        Feedback,
    }

    public sealed class AnswerRecord
    {
        public AnswerRecord(string pairId, bool correct, double? focusAtAnswer, double atSeconds)
        {
            PairId = pairId;
            Correct = correct;
            FocusAtAnswer = focusAtAnswer;
            AtSeconds = atSeconds;
        }

        public string PairId { get; }
        public bool Correct { get; }

        /// <summary>Null when EEG was off or had no clean signal at that moment.</summary>
        public double? FocusAtAnswer { get; }

        public double AtSeconds { get; }
    }

    /// <summary>
    /// The memory palace game: which pair sits on which object, Preparation (objects being placed in the
    /// room), Learning (look at objects to read their pairs) and Test ("find the object": a prompt is
    /// shown and the user picks the object it was attached to), with learn/test time bookkeeping that
    /// excludes pauses.
    /// Pure logic; the Unity layer only renders state and forwards clicks.
    /// </summary>
    public sealed class PalaceSession
    {
        /// <summary>Anchor points the room offers. Larger sets are trimmed with a warning at setup.</summary>
        public const int MaxObjects = 12;

        /// <summary>Safety stop if the user never masters everything and never presses "Zakończ".</summary>
        public const int MaxTestRounds = 10;

        private readonly IClock _clock;
        private readonly HashSet<int> _viewed = new HashSet<int>();
        private readonly Dictionary<string, int> _objectOfPair = new Dictionary<string, int>();
        private readonly List<AnswerRecord> _answers = new List<AnswerRecord>();
        private readonly SpacedRepetitionScheduler _scheduler;
        private readonly Queue<string> _round = new Queue<string>();
        private double _modeStartedAt;
        private double _pausedAt = -1;

        /// <exception cref="ArgumentException">The set is null or has no pairs.</exception>
        public PalaceSession(StudySet set, IClock clock, int seed = 0)
        {
            if (set?.Pairs == null || set.Pairs.Count == 0)
            {
                throw new ArgumentException("Zestaw materiału jest pusty.", nameof(set));
            }

            Set = set;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _modeStartedAt = clock.NowSeconds;
            Objects = set.Pairs.Take(MaxObjects).ToList();
            for (var i = 0; i < Objects.Count; i++)
            {
                _objectOfPair[Objects[i].Id] = i;
            }

            _scheduler = new SpacedRepetitionScheduler(Objects.Select(p => p.Id), seed);
        }

        public StudySet Set { get; }

        /// <summary>Pair shown on palace object i.</summary>
        public IReadOnlyList<StudyPair> Objects { get; }

        public PalaceMode Mode { get; private set; } = PalaceMode.Preparation;
        public bool IsPaused => _pausedAt >= 0;
        public int ViewedCount => _viewed.Count;
        public bool AllViewed => _viewed.Count == Objects.Count;
        public double LearnTimeS { get; private set; }
        public double TestTimeS { get; private set; }

        public TestStep Step { get; private set; }

        /// <summary>Object holding the asked pair in Test (Find/Feedback), -1 if none.</summary>
        public int CurrentObject { get; private set; } = -1;

        /// <summary>Object the user picked for the last question (Feedback), -1 if they gave up.</summary>
        public int PickedObject { get; private set; } = -1;

        /// <summary>Whether the last answer was correct (meaningful in Feedback).</summary>
        public bool LastCorrect { get; private set; }

        public IReadOnlyList<AnswerRecord> Answers => _answers;
        public SpacedRepetitionScheduler Scheduler => _scheduler;
        public int MasteredCount => _scheduler.Progress.Count(_scheduler.IsMastered);

        /// <summary>Raised when the mode changes (incl. the automatic Learning → Test switch).</summary>
        public event Action<PalaceMode> ModeChanged;

        public void StartLearning()
        {
            RequireMode(PalaceMode.Preparation);
            SwitchMode(PalaceMode.Learning);
        }

        /// <summary>User looked at (or selected) an object in Learning: returns its pair and counts it as viewed.</summary>
        public StudyPair View(int objectIndex)
        {
            RequireMode(PalaceMode.Learning);
            if (objectIndex < 0 || objectIndex >= Objects.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(objectIndex));
            }

            _viewed.Add(objectIndex);
            return Objects[objectIndex];
        }

        public bool WasViewed(int objectIndex) => _viewed.Contains(objectIndex);

        /// <summary>"Zacznij test", or the automatic switch.</summary>
        public void StartTest()
        {
            if (Mode == PalaceMode.Preparation)
            {
                SwitchMode(PalaceMode.Learning);
            }

            RequireMode(PalaceMode.Learning);
            SwitchMode(PalaceMode.Test);
        }

        /// <summary>Asks the next pair. Returns false when the test is over (all mastered or round cap).</summary>
        /// <param name="speedUp">High-focus mode: mastered pairs are mixed into the next round.</param>
        public bool NextQuestion(bool speedUp = false)
        {
            RequireMode(PalaceMode.Test);
            if (_round.Count == 0)
            {
                if (_scheduler.AllMastered || _scheduler.Rounds >= MaxTestRounds)
                {
                    Finish();
                    return false;
                }

                foreach (var id in _scheduler.NextRound(speedUp))
                {
                    _round.Enqueue(id);
                }
            }

            CurrentObject = _objectOfPair[_round.Dequeue()];
            PickedObject = -1;
            Step = TestStep.Find;
            return true;
        }

        /// <summary>
        /// User selected <paramref name="objectIndex"/> as the home of the asked pair. <paramref name="focus"/>
        /// is the focus at that moment, if EEG is on. Returns whether it was the right object.
        /// </summary>
        public bool Choose(int objectIndex, double? focus)
        {
            RequireStep(TestStep.Find);
            if (objectIndex < 0 || objectIndex >= Objects.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(objectIndex));
            }

            Record(objectIndex, objectIndex == CurrentObject, focus);
            return LastCorrect;
        }

        /// <summary>"Nie pamiętam", or the answer time ran out: counts as wrong and shows the right object.</summary>
        public void GiveUp(double? focus)
        {
            RequireStep(TestStep.Find);
            Record(-1, false, focus);
        }

        private void Record(int picked, bool correct, double? focus)
        {
            var pair = Objects[CurrentObject];
            _scheduler.Record(pair.Id, correct);
            _answers.Add(new AnswerRecord(pair.Id, correct, focus, _clock.NowSeconds));
            PickedObject = picked;
            LastCorrect = correct;
            Step = TestStep.Feedback;
        }

        public void Pause()
        {
            if (!IsPaused && Mode != PalaceMode.Finished)
            {
                AccumulateModeTime();
                _pausedAt = _clock.NowSeconds;
            }
        }

        public void Resume()
        {
            if (IsPaused)
            {
                _pausedAt = -1;
                _modeStartedAt = _clock.NowSeconds;
            }
        }

        /// <summary>"Zakończ", the 25-minute suggestion, or the end of the test.</summary>
        public void Finish()
        {
            if (Mode != PalaceMode.Finished)
            {
                SwitchMode(PalaceMode.Finished);
                Step = TestStep.Idle;
                CurrentObject = -1;
                PickedObject = -1;
            }
        }

        private void SwitchMode(PalaceMode mode)
        {
            AccumulateModeTime();
            Mode = mode;
            _modeStartedAt = _clock.NowSeconds;
            ModeChanged?.Invoke(mode);
        }

        private void AccumulateModeTime()
        {
            if (IsPaused)
            {
                return;
            }

            var spent = _clock.NowSeconds - _modeStartedAt;
            if (Mode == PalaceMode.Learning || Mode == PalaceMode.Preparation)
            {
                LearnTimeS += spent; // walking around while objects get placed is already studying the room
            }
            else if (Mode == PalaceMode.Test)
            {
                TestTimeS += spent;
            }

            _modeStartedAt = _clock.NowSeconds;
        }

        private void RequireMode(PalaceMode mode)
        {
            if (Mode != mode)
            {
                throw new InvalidOperationException($"Expected mode {mode}, but session is in {Mode}");
            }
        }

        private void RequireStep(TestStep step)
        {
            if (Mode != PalaceMode.Test || Step != step)
            {
                throw new InvalidOperationException($"Expected test step {step}, but is {Mode}/{Step}");
            }
        }
    }
}
