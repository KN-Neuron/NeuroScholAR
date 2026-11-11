using UnityEngine;

namespace NeuroScholAR.App.UI
{
    /// <summary>
    /// UI language (Polish default, English optional). Chosen on the setup screen only; every other screen
    /// reads it when it is shown. Remembered across launches like the onboarding flag.
    /// Use as <c>using static NeuroScholAR.App.UI.Lang;</c> then <c>T("polski", "English")</c>.
    /// </summary>
    public static class Lang
    {
        private const string Key = "ui_lang_en";
        private static bool? _english;

        public static bool English => _english ??= PlayerPrefs.GetInt(Key, 0) == 1;

        public static string T(string pl, string en) => English ? en : pl;

        public static void Toggle()
        {
            _english = !English;
            PlayerPrefs.SetInt(Key, _english.Value ? 1 : 0);
            PlayerPrefs.Save();
        }
    }
}
