using System;
using UnityEngine;
using UnityEngine.UI;

namespace NeuroScholAR.App.UI
{
    /// <summary>Three slides shown on first launch only, plus "Pomiń tutorial".</summary>
    public sealed class OnboardingScreen
    {
        private const string DoneKey = "onboarding_done";

        private static readonly (string title, string body)[] Slides =
        {
            ("1/3  Załóż opaskę EEG",
                "Załóż opaskę BrainBit tak, by elektrody za uszami (T3/T4) i z tyłu głowy (O1/O2) dotykały skóry. " +
                "Zwilż je lekko wodą. Na ekranie przygotowania każda elektroda pokaże OK albo „Słaby kontakt”. " +
                "Bez opaski też możesz się uczyć: wtedy aplikacja nie mierzy skupienia."),
            ("2/3  Twój pałac pamięci",
                "Obejdź cały pokój i rozglądaj się: aplikacja postawi przedmioty na meblach, łóżku, podłodze i ścianach. " +
                "Każdy przechowuje jedną informację. Przyciski i przedmioty naciskasz palcem wskazującym. " +
                "Menu (pauza, koniec) otworzysz, patrząc na wnętrze lewej dłoni. Kontroler Beam Pro też działa."),
            ("3/3  Nauka i test",
                "Nauka: podejdź do przedmiotu i popatrz na niego chwilę, by zobaczyć jego parę. Gdy obejrzysz wszystkie, " +
                "zacznie się test (albo naciśnij „Zacznij test”). Test: pokażemy hasło, a Ty znajdź przedmiot, przy którym je widziałeś/aś. " +
                "Pasek u góry pokazuje skupienie; gdy spada, zaproponujemy przerwę."),
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
            var column = Ui.Column("Onboarding", canvas, 24, 40, Ui.Background);
            Ui.Stretch(column);
            _root = column.gameObject;
            _title = Ui.Label(column, string.Empty, 44, style: FontStyle.Bold);
            _body = Ui.Label(column, string.Empty, 30);
            Ui.Height(_body, 380);
            var row = Ui.Row("Nav", column);
            _back = Ui.Button(row, "Wstecz", () => Go(-1), Ui.Panel);
            _next = Ui.Button(row, "Dalej", () => Go(1));
            Ui.Button(column, "Pomiń tutorial", Finish, Ui.Panel, 56);
            Render();
        }

        public static bool AlreadySeen => PlayerPrefs.GetInt(DoneKey, 0) == 1;

        public void SetVisible(bool visible) => _root.SetActive(visible);

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
            _title.text = Slides[_slide].title;
            _body.text = Slides[_slide].body;
            _back.interactable = _slide > 0;
            Ui.SetButtonText(_next, _slide == Slides.Length - 1 ? "Zaczynamy" : "Dalej");
        }

        private void Finish()
        {
            PlayerPrefs.SetInt(DoneKey, 1);
            PlayerPrefs.Save();
            _onDone();
        }
    }
}
