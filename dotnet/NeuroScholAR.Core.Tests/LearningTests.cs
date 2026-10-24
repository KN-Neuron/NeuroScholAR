using System;
using System.Linq;
using NeuroScholAR.Core.Learning;
using NUnit.Framework;

namespace NeuroScholAR.Core.Tests
{
    public class MaterialParserTests
    {
        [Test]
        public void Json_SpecSchema_Parses()
        {
            var result = MaterialParser.ParseJson(@"{ ""set_name"": ""Biology - cell"", ""type"": ""term_definition"",
                ""pairs"": [ { ""id"": ""p1"", ""prompt"": ""Mitochondrion"", ""answer"": ""Organelle producing ATP"" } ] }");
            Assert.That(result.Set.SetName, Is.EqualTo("Biology - cell"));
            Assert.That(result.Set.Type, Is.EqualTo(StudySetType.TermDefinition));
            Assert.That(result.Set.Pairs.Single().Answer, Is.EqualTo("Organelle producing ATP"));
            Assert.That(result.Warnings, Is.Empty);
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase("{ not json")]
        [TestCase(@"{ ""set_name"": ""x"" }")]
        [TestCase(@"{ ""pairs"": [] }")]
        [TestCase(@"{ ""pairs"": [ { ""prompt"": """", ""answer"": ""a"" }, 5 ] }")]
        [TestCase(@"{ ""type"": ""quiz"", ""pairs"": [ { ""prompt"": ""a"", ""answer"": ""b"" } ] }")]
        public void Json_EmptyOrMalformed_IsRejected(string content) =>
            Assert.Throws<MaterialImportException>(() => MaterialParser.ParseJson(content));

        [Test]
        public void Json_DuplicatesAndBadRows_AreSkippedWithWarnings_AndIdsStayUnique()
        {
            var result = MaterialParser.ParseJson(@"{ ""pairs"": [
                { ""id"": ""a"", ""prompt"": ""Term"", ""answer"": ""1"" },
                { ""id"": ""a"", ""prompt"": ""Other"", ""answer"": ""2"" },
                { ""id"": ""b"", ""prompt"": "" term "", ""answer"": ""3"" },
                { ""prompt"": ""No answer"" } ] }");
            Assert.That(result.Set.Pairs.Select(p => p.Prompt), Is.EqualTo(new[] { "Term", "Other" }));
            Assert.That(result.Set.Pairs.Select(p => p.Id).Distinct().Count(), Is.EqualTo(2));
            Assert.That(result.Warnings, Has.Count.EqualTo(2));
        }

        [Test]
        public void Csv_SemicolonQuotedWithHeader_DetectsDates()
        {
            var result = MaterialParser.ParseCsv("pojęcie;odpowiedź\r\n\"Bitwa pod Grunwaldem\";1410\n\"Unia; lubelska\";1569\n", "Historia");
            Assert.That(result.Set.Type, Is.EqualTo(StudySetType.TermDate));
            Assert.That(result.Set.Pairs[1].Prompt, Is.EqualTo("Unia; lubelska"));
            Assert.That(result.Set.SetName, Is.EqualTo("Historia"));
        }

        [Test]
        public void Csv_CommaWithEscapedQuotes_IsTermDefinition()
        {
            var result = MaterialParser.ParseCsv("Mitochondrium,\"Produkuje \"\"ATP\"\", energię\"\nRybosom,Synteza białek", "Bio");
            Assert.That(result.Set.Type, Is.EqualTo(StudySetType.TermDefinition));
            Assert.That(result.Set.Pairs[0].Answer, Is.EqualTo("Produkuje \"ATP\", energię"));
        }

        [Test]
        public void Csv_OnlyHeader_IsRejected() =>
            Assert.Throws<MaterialImportException>(() => MaterialParser.ParseCsv("prompt,answer\n", "x"));

        [Test]
        public void File_UnknownExtension_IsRejected() =>
            Assert.Throws<MaterialImportException>(() => MaterialParser.ParseFile("notes.pdf", "a,b"));

        [Test]
        public void File_TooLarge_IsRejected() =>
            Assert.Throws<MaterialImportException>(() => MaterialParser.ParseFile("big.csv", new string('a', MaterialParser.MaxFileBytes + 1)));

        [Test]
        public void Manual_Entry_IsValidated()
        {
            var result = MaterialParser.FromManual("", StudySetType.TermDate, new[] { ("Chrzest", "966"), ("chrzest", "966"), ("", "x") });
            Assert.That(result.Set.Pairs, Has.Count.EqualTo(1));
            Assert.That(result.Set.SetName, Is.EqualTo("Mój zestaw"));
        }

        [Test]
        public void PredefinedSets_AreValid() =>
            Assert.That(PredefinedSets.All.All(s => s.Pairs.Count > 0 && s.Pairs.Select(p => p.Id).Distinct().Count() == s.Pairs.Count));
    }

    public class SpacedRepetitionTests
    {
        private static int Count(System.Collections.Generic.IReadOnlyList<string> round, string id) => round.Count(x => x == id);

        [Test]
        public void FirstRound_HasEveryPairOnce()
        {
            var s = new SpacedRepetitionScheduler(new[] { "a", "b", "c" });
            Assert.That(s.NextRound().OrderBy(x => x), Is.EqualTo(new[] { "a", "b", "c" }));
        }

        [Test]
        public void WrongPairs_AppearMoreOften_CappedAtMaxExtra()
        {
            var s = new SpacedRepetitionScheduler(new[] { "a", "b", "c" });
            s.NextRound();
            s.Record("a", false);
            s.Record("b", true);
            var round = s.NextRound();
            Assert.That(Count(round, "a"), Is.EqualTo(2));
            Assert.That(Count(round, "b"), Is.EqualTo(1));

            s.Record("a", false);
            s.Record("a", false);
            Assert.That(Count(s.NextRound(), "a"), Is.EqualTo(1 + s.MaxExtraCopies));
        }

        [Test]
        public void CorrectAnswer_ResetsExtraCopies()
        {
            var s = new SpacedRepetitionScheduler(new[] { "a", "b" });
            s.Record("a", false);
            s.Record("a", true);
            Assert.That(Count(s.NextRound(), "a"), Is.EqualTo(1));
        }

        [Test]
        public void Mastered_AppearLessOften_ButStillAppear()
        {
            var s = new SpacedRepetitionScheduler(new[] { "a", "b" }, masteredAfter: 3, masteredEveryRounds: 3);
            for (var i = 0; i < 3; i++)
            {
                s.Record("a", true);
            }

            Assert.That(s.IsMastered("a"), Is.True);
            var presence = Enumerable.Range(0, 6).Select(_ => Count(s.NextRound(), "a")).ToArray();
            Assert.That(presence, Is.EqualTo(new[] { 1, 0, 0, 1, 0, 0 }));
            Assert.That(presence.Sum(), Is.LessThan(6)); // less often than an unmastered pair (every round)
        }

        [Test]
        public void SpeedUp_IncludesMasteredEveryRound()
        {
            var s = new SpacedRepetitionScheduler(new[] { "a", "b" });
            for (var i = 0; i < 3; i++)
            {
                s.Record("a", true);
            }

            s.NextRound();
            Assert.That(Count(s.NextRound(includeMastered: true), "a"), Is.EqualTo(1));
        }

        [Test]
        public void Repeats_AreNeverBackToBack_WhenAvoidable()
        {
            var s = new SpacedRepetitionScheduler(new[] { "a", "b", "c", "d" }, seed: 7);
            for (var i = 0; i < 2; i++)
            {
                s.Record("a", false);
                s.Record("b", false);
            }

            for (var r = 0; r < 20; r++)
            {
                var round = s.NextRound();
                for (var i = 1; i < round.Count; i++)
                {
                    Assert.That(round[i], Is.Not.EqualTo(round[i - 1]), string.Join(",", round));
                }
            }
        }

        [Test]
        public void EmptyInput_Throws() => Assert.Throws<ArgumentException>(() => new SpacedRepetitionScheduler(new string[0]));
    }

    public class PalaceSessionTests
    {
        private static StudySet Set(int n) => new StudySet("t", StudySetType.TermDefinition,
            Enumerable.Range(1, n).Select(i => new StudyPair("p" + i, "Q" + i, "A" + i)).ToList());

        [Test]
        public void EmptySet_IsRejected()
        {
            Assert.Throws<ArgumentException>(() => new PalaceSession(Set(0), new ManualClock()));
            Assert.Throws<ArgumentException>(() => new PalaceSession(null, new ManualClock()));
        }

        [Test]
        public void ViewingAllObjects_IsTracked()
        {
            var session = new PalaceSession(Set(3), new ManualClock());
            session.StartLearning();
            for (var i = 0; i < 3; i++)
            {
                Assert.That(session.AllViewed, Is.False);
                Assert.That(session.View(i).Answer, Is.EqualTo("A" + (i + 1)));
            }

            Assert.That(session.AllViewed, Is.True);
        }

        [Test]
        public void ManualStartTest_WorksBeforeAllViewed()
        {
            var session = new PalaceSession(Set(3), new ManualClock());
            session.StartLearning();
            session.View(0);
            session.StartTest();
            Assert.That(session.Mode, Is.EqualTo(PalaceMode.Test));
        }

        [Test]
        public void Test_FlowRecordsAnswers_AndEndsWhenAllMastered()
        {
            var clock = new ManualClock();
            var session = new PalaceSession(Set(2), clock);
            session.StartTest();
            var asked = 0;
            while (session.NextQuestion())
            {
                Assert.That(session.Step, Is.EqualTo(TestStep.Find));
                Assert.That(session.Choose(session.CurrentObject, 60), Is.True);
                Assert.That(session.Step, Is.EqualTo(TestStep.Feedback));
                asked++;
                clock.Advance(5);
            }

            Assert.That(session.Mode, Is.EqualTo(PalaceMode.Finished));
            Assert.That(asked, Is.EqualTo(6)); // 2 pairs × 3 correct to master
            Assert.That(session.Answers.All(a => a.Correct && a.FocusAtAnswer == 60));
        }

        [Test]
        public void Times_AreSplitByMode_AndExcludePauses()
        {
            var clock = new ManualClock();
            var session = new PalaceSession(Set(2), clock);
            session.StartLearning();
            clock.Advance(10);
            session.Pause();
            clock.Advance(100);
            session.Resume();
            clock.Advance(5);
            session.StartTest();
            clock.Advance(20);
            session.Finish();
            Assert.That(session.LearnTimeS, Is.EqualTo(15));
            Assert.That(session.TestTimeS, Is.EqualTo(20));
        }

        [Test]
        public void LargeSet_IsCappedToRoomSize()
        {
            var session = new PalaceSession(Set(30), new ManualClock());
            Assert.That(session.Objects, Has.Count.EqualTo(PalaceSession.MaxObjects));
        }

        [Test]
        public void WrongPickAndGiveUp_CountAsWrong_AndRememberThePick()
        {
            var session = new PalaceSession(Set(3), new ManualClock());
            session.StartTest();
            session.NextQuestion();
            var wrong = (session.CurrentObject + 1) % 3;
            Assert.That(session.Choose(wrong, null), Is.False);
            Assert.That(session.PickedObject, Is.EqualTo(wrong));
            Assert.Throws<InvalidOperationException>(() => session.Choose(wrong, null)); // one answer per question

            session.NextQuestion();
            session.GiveUp(null);
            Assert.That(session.PickedObject, Is.EqualTo(-1));
            Assert.That(session.Answers.Select(a => a.Correct), Is.EqualTo(new[] { false, false }));
        }
    }
}
