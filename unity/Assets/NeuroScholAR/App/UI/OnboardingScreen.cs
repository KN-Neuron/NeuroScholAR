using System;
using UnityEngine;
using UnityEngine.UI;
using static NeuroScholAR.App.UI.Lang;

namespace NeuroScholAR.App.UI
{
    /// <summary>Three slides shown on first launch only, plus "Pomiń tutorial".</summary>
    public sealed class OnboardingScreen
    {
        private const string DoneKey = "onboarding_done";

        private static readonly (string titlePl, string bodyPl, string titleEn, string bodyEn)[] Slides =
        {
            ("1/3  Załóż opaskę EEG",
                "Załóż opaskę BrainBit tak, by elektrody za uszami (T3/T4) i z tyłu głowy (O1/O2) dotykały skóry. " +
                "Zwilż je lekko wodą. Na ekranie przygotowania każda elektroda pokaże OK albo „Słaby kontakt”. " +
                "Bez opaski też możesz się uczyć: wtedy aplikacja nie mierzy skupienia.",
                "1/3  Put on the EEG headband",
                "Put on the BrainBit headband so the electrodes behind your ears (T3/T4) and at the back of your head (O1/O2) touch the skin. " +
                "Dampen them slightly with water. On the setup screen each electrode shows OK or \"Poor contact\". " +
                "You can also learn without the headband: the app just won't measure focus."),
            ("2/3  Twój pałac pamięci",
                "Obejdź cały pokój i rozglądaj się: aplikacja postawi przedmioty na meblach, łóżku, podłodze i ścianach. " +
                "Każdy przechowuje jedną informację. Przyciski i przedmioty naciskasz palcem wskazującym. " +
                "Menu (pauza, koniec) otworzysz, patrząc na wnętrze lewej dłoni. Kontroler Beam Pro też działa.",
                "2/3  Your memory palace",
                "Walk around the whole room and look around: the app places objects on furniture, the bed, the floor and walls. " +
                "Each one holds one piece of information. Press buttons and objects with your index finger. " +
                "Open the menu (pause, end) by looking at the inside of your left palm. The Beam Pro controller works too."),
            ("3/3  Nauka i test",
                "Nauka: podejdź do przedmiotu i popatrz na niego chwilę, by zobaczyć jego parę. Gdy obejrzysz wszystkie, " +
                "zacznie się test (albo naciśnij „Zacznij test”). Test: pokażemy hasło, a Ty znajdź przedmiot, przy którym je widziałeś/aś. " +
                "Pasek u góry pokazuje skupienie; gdy spada, zaproponujemy przerwę.",
                "3/3  Learning and test",
                "Learning: walk up to an object and look at it for a moment to see its pair. Once you have seen them all, " +
                "the test starts (or press \"Start test\"). Test: we show a term, and you find the object where you saw it. " +
                "The bar at the top shows your focus; when it drops, we suggest a break."),
        };

        private readonly GameObject _root;
        private readonly Text _title;
        private readonly Text _body;
        private readonly Button _back;
        private readonly Button _next;
        private readonly Button _skip;
        private readonly Action _onDone;
        private int _slide;

        public OnboardingScreen(Transform canvas, Action onDone)
        {
            _onDone = onDone;
            var column = Ui.Column("Onboarding", canvas, 24, 40, Ui.Background);
            Ui.Stretch(column);
            _root = column.gameObject;
            _title = Ui.Label(column, string.Empty, 44, style: FontStyle.Bold);
            _body = Ui.Label(column, string.Empty, 30);
            Ui.Height(_body, 380);
            var row = Ui.Row("Nav", column);
            _back = Ui.Button(row, "Wstecz", () => Go(-1), Ui.Panel);
            _next = Ui.Button(row, "Dalej", () => Go(1));
            _skip = Ui.Button(column, "Pomiń tutorial", Finish, Ui.Panel, 56);
            Render();
        }

        public static bool AlreadySeen => PlayerPrefs.GetInt(DoneKey, 0) == 1;

        public void SetVisible(bool visible)
        {
            _root.SetActive(visible);
            if (visible)
            {
                Render();
            }
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
            _title.text = T(slide.titlePl, slide.titleEn);
            _body.text = T(slide.bodyPl, slide.bodyEn);
            _back.interactable = _slide > 0;
            Ui.SetButtonText(_back, T("Wstecz", "Back"));
            Ui.SetButtonText(_next, _slide == Slides.Length - 1 ? T("Zaczynamy", "Let's go") : T("Dalej", "Next"));
            Ui.SetButtonText(_skip, T("Pomiń tutorial", "Skip tutorial"));
        }

        private void Finish()
        {
            PlayerPrefs.SetInt(DoneKey, 1);
            PlayerPrefs.Save();
            _onDone();
        }
    }
}
