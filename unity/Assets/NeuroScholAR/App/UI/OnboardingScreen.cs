using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace NeuroScholAR.App.UI
{
    /// <summary>
    /// Three slides shown on first launch (and again from the setup screen's "Samouczek" button), plus "Pomiń tutorial".
    /// Bilingual on purpose: it appears before the user can pick a language, so every Polish sentence has its
    /// English translation underneath, smaller and grey.
    /// </summary>
    public sealed class OnboardingScreen
    {
        private const string DoneKey = "onboarding_done";
        private const int TitleSize = 40;
        private const int TitleEnSize = 24;
        private const int BodySize = 23;
        private const int BodyEnSize = 18;
        private const int GapSize = 9; // blank line between sentence pairs

        private static readonly string MutedHex = ColorUtility.ToHtmlStringRGBA(Ui.Muted);

        private sealed class Slide
        {
            public string TitlePl;
            public string TitleEn;
            public (string pl, string en)[] Sentences;
        }

        private static readonly Slide[] Slides =
        {
            new Slide
            {
                TitlePl = "1/3  Załóż opaskę EEG",
                TitleEn = "Put on the EEG headband",
                Sentences = new[]
                {
                    ("Załóż opaskę BrainBit tak, by elektrody za uszami (T3/T4) i z tyłu głowy (O1/O2) dotykały skóry.",
                        "Put on the BrainBit headband so the electrodes behind your ears (T3/T4) and at the back of your head (O1/O2) touch the skin."),
                    ("Zwilż je lekko wodą.",
                        "Dampen them slightly with water."),
                    ("Na ekranie przygotowania każda elektroda pokaże OK albo „Słaby kontakt”.",
                        "On the setup screen each electrode shows OK or \"Poor contact\"."),
                    ("Bez opaski też możesz się uczyć: wtedy aplikacja nie mierzy skupienia.",
                        "You can also learn without the headband: the app just won't measure focus."),
                },
            },
            new Slide
            {
                TitlePl = "2/3  Twój pałac pamięci",
                TitleEn = "Your memory palace",
                Sentences = new[]
                {
                    // Longest slide: keep it to 4 short sentences so it stays clear of the button row.
                    ("Obejdź pokój: aplikacja postawi przedmioty na meblach, podłodze i ścianach.",
                        "Walk around the room: the app places objects on furniture, the floor and walls."),
                    ("Każdy przedmiot przechowuje jedną informację.",
                        "Each object holds one piece of information."),
                    ("Przyciski i przedmioty naciskasz palcem wskazującym (albo kontrolerem Beam Pro).",
                        "Press buttons and objects with your index finger (or the Beam Pro controller)."),
                    ("Menu (pauza, koniec): spójrz na wnętrze lewej dłoni.",
                        "Menu (pause, end): look at the inside of your left palm."),
                },
            },
            new Slide
            {
                TitlePl = "3/3  Nauka i test",
                TitleEn = "Learning and test",
                Sentences = new[]
                {
                    ("Nauka: podejdź do przedmiotu i popatrz na niego chwilę, by zobaczyć jego parę.",
                        "Learning: walk up to an object and look at it for a moment to see its pair."),
                    ("Gdy obejrzysz wszystkie, zacznie się test (albo naciśnij „Zacznij test”).",
                        "Once you have seen them all, the test starts (or press \"Zacznij test\")."),
                    ("Test: pokażemy hasło, a Ty znajdź przedmiot, przy którym je widziałeś/aś.",
                        "Test: we show a term, and you find the object where you saw it."),
                    ("Pasek u góry pokazuje skupienie; gdy spada, zaproponujemy przerwę.",
                        "The bar at the top shows your focus; when it drops, we suggest a break."),
                },
            },
        };

        private readonly GameObject _root;
        private readonly Text _title;
        private readonly Text _body;
        private readonly Button _back;
        private readonly Button _next;
        private readonly Action _onDone;
        private int _slide;

        public OnboardingScreen(Transform canvas, Action onDone)
        {
            _onDone = onDone;
            var column = Ui.Column("Onboarding", canvas, 20, 36, Ui.Glass, edge: true);
            Ui.Stretch(column);
            _root = column.gameObject;
            _title = Ui.Label(column, string.Empty, TitleSize, style: FontStyle.Bold);
            _body = Ui.Label(column, string.Empty, BodySize, anchor: TextAnchor.UpperLeft);
            _body.gameObject.AddComponent<LayoutElement>().flexibleHeight = 1; // takes whatever the buttons leave
            // One button row (not two): height is the scarce axis in the glasses' view.
            var row = Ui.Row("Nav", column);
            Ui.Button(row, "Pomiń / Skip", Finish, Ui.Raised, 60, 22);
            _back = Ui.Button(row, "Wstecz / Back", () => Go(-1), Ui.Raised, 60, 22);
            _next = Ui.Button(row, "Dalej / Next", () => Go(1), Ui.Accent, 60, 22);
            Render();
        }

        /// <summary>Wide and short like the setup screen: 1040 x 600 units at 0.65 m is about 40 x 23 degrees.</summary>
        public static readonly Vector2 CanvasSize = new Vector2(1040, 600);

        public static bool AlreadySeen => PlayerPrefs.GetInt(DoneKey, 0) == 1;

        public void SetVisible(bool visible)
        {
            if (visible && !_root.activeSelf)
            {
                _slide = 0; // reopened from the setup screen: start from the first slide
                Render();
            }

            _root.SetActive(visible);
        }

        private void Go(int delta)
        {
            if (_slide + delta >= Slides.Length)
            {
                Finish();
                return;
            }

            _slide = Mathf.Clamp(_slide + delta, 0, Slides.Length - 1);
            Render();
        }

        private void Render()
        {
            var slide = Slides[_slide];
            _title.text = $"{slide.TitlePl}\n{English(slide.TitleEn, TitleEnSize)}";
            _body.text = string.Join($"\n<size={GapSize}> </size>\n",
                slide.Sentences.Select(s => $"{s.pl}\n{English(s.en, BodyEnSize)}"));
            _back.interactable = _slide > 0;
            Ui.SetButtonText(_next, _slide == Slides.Length - 1 ? "Zaczynamy / Let's go" : "Dalej / Next");
        }

        /// <summary>English line under the Polish one: smaller, grey, italic.</summary>
        private static string English(string text, int size) => $"<size={size}><color=#{MutedHex}><i>{text}</i></color></size>";

        private void Finish()
        {
            PlayerPrefs.SetInt(DoneKey, 1);
            PlayerPrefs.Save();
            _onDone();
        }
    }
}
