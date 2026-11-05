using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NeuroScholAR.App.Eeg;
using NeuroScholAR.Core.Eeg;
using NeuroScholAR.Core.Learning;
using NeuroScholAR.Core.Session;
using UnityEngine;
using UnityEngine.UI;

namespace NeuroScholAR.App.UI
{
    public sealed class SetupScreen
    {
        private readonly SessionController _app;
        private readonly GameObject _root;
        private readonly GameObject _main;
        private readonly GameObject _manual;

        private readonly Text _titleText;
        private readonly Text _step1Text;
        private readonly Text _step2Text;
        private readonly Text _importFileText;
        private readonly Text _importManualText;
        private readonly Text _materialInfo;
        private readonly Text _connectionText;
        private readonly Text _electrodes;
        private readonly Text _message;
        private readonly Text _manualTitleText;
        private readonly Text _manualList;
        private readonly Text _calibrationRetryText;
        private readonly Text _calibrationContinueText;

        private readonly RectTransform _fileList;
        private readonly Image _connectionLight;

        private readonly Button _langToggle;
        private readonly Button _devToggle;
        private readonly Button _connect;
        private readonly Button _simulator;
        private readonly Button _start;
        private readonly Button _manualAddBtn;
        private readonly Button _manualDoneBtn;
        private readonly Button _manualCancelBtn;

        private readonly GameObject _calibrationFailed;
        private readonly InputField _manualName;
        private readonly InputField _manualPrompt;
        private readonly InputField _manualAnswer;

        private readonly List<(string prompt, string answer)> _manualPairs = new List<(string, string)>();

        private EegConnectionException _seenError;
        private float _errorAt = float.NegativeInfinity;
        private string _seenMessage = string.Empty;
        private float _messageAt;
        private object _seenCalibration;

        private bool _useSimulator;
        private bool _connecting;
        public static bool IsEnglish = false;
        private const float MessageSeconds = 10f;

        private static string T(string pl, string en) => IsEnglish ? en : pl;

        public SetupScreen(Transform canvas, SessionController app, bool useSimulator)
        {
            _app = app;
            _useSimulator = useSimulator || !EegSources.DeviceAvailable;

            var root = Ui.Rect("Setup", canvas);
            Ui.Stretch(root);
            _root = root.gameObject;

            // MAIN OUTER WINDOW
            var main = Ui.Column("Main", root, spacing: 18, padding: 24, background: Ui.Glass);
            Ui.Stretch(main);
            _main = main.gameObject;

            // HEADER ROW (Title + Top Toggles)
            var header = Ui.Row("Header", main, 12);
            _titleText = Ui.Label(header, "Przygotowanie sesji", 36, style: FontStyle.Bold);
            _titleText.gameObject.AddComponent<LayoutElement>().flexibleWidth = 3;

            _langToggle = Ui.Button(header, "PL / EN", ToggleLanguage, Ui.Panel, height: 44, fontSize: 18);
            var langLayout = _langToggle.gameObject.GetComponent<LayoutElement>();
            langLayout.preferredWidth = 90;

            _devToggle = Ui.Button(header, DevLabel(), ToggleDev, Ui.Panel, height: 44, fontSize: 18);
            var devLayout = _devToggle.gameObject.GetComponent<LayoutElement>();
            devLayout.preferredWidth = 200; // Expanded to 200 to prevent multi-line wrap

            // DASHBOARD BODY (2 COLUMNS SIDE-BY-SIDE)
            var body = Ui.Row("Body", main, spacing: 20);
            body.gameObject.AddComponent<LayoutElement>().flexibleHeight = 1;
            body.GetComponent<HorizontalLayoutGroup>().childForceExpandHeight = true; // both cards same height

            // --- LEFT COLUMN: STUDY MATERIAL CARD ---
            var leftCard = Ui.Column("LeftCard", body, spacing: 12, padding: 20, background: Ui.Panel);
            EqualColumn(leftCard);

            _step1Text = Ui.Label(leftCard, "WYBÓR MATERIAŁU", 22, Ui.Muted, style: FontStyle.Bold);

            // Stacked Vertical List for Predefined Sets (Prevents text squishing)
            var sets = Ui.Column("Sets", leftCard, spacing: 8);
            foreach (var set in PredefinedSets.All)
            {
                Ui.Button(sets, set.SetName, () => Select(new ImportResult(set, Array.Empty<string>())), Ui.Glass, height: 46, fontSize: 20);
            }

            // Custom Import Actions
            var importRow = Ui.Row("Import", leftCard, 8);
            var importFileBtn = Ui.Button(importRow, "Z pliku (CSV/JSON)", ShowFiles, Ui.Glass, height: 46, fontSize: 19);
            var importManualBtn = Ui.Button(importRow, "Wpisz ręcznie", () => ShowManual(true), Ui.Glass, height: 46, fontSize: 19);
            _importFileText = importFileBtn.GetComponentInChildren<Text>();
            _importManualText = importManualBtn.GetComponentInChildren<Text>();

            _fileList = Ui.Column("Files", leftCard, 6);
            _materialInfo = Ui.Label(leftCard, "Nie wybrano materiału.", 20, Ui.Muted);


            // --- RIGHT COLUMN: EEG & SYSTEM STATUS CARD ---
            var rightCard = Ui.Column("RightCard", body, spacing: 12, padding: 20, background: Ui.Panel);
            EqualColumn(rightCard);

            _step2Text = Ui.Label(rightCard, "OPASKA EEG I STATUS", 22, Ui.Muted, style: FontStyle.Bold);

            var eegRow = Ui.Row("Eeg", rightCard, 8);
            _connect = Ui.Button(eegRow, "Połącz opaskę EEG", Connect, Ui.Glass, height: 48, fontSize: 19);
            _simulator = Ui.Button(eegRow, string.Empty, ToggleSimulator, Ui.Glass, height: 48, fontSize: 19);

            var status = Ui.Row("Status", rightCard, 10);
            _connectionLight = Ui.Box("Light", status, Ui.Red);
            var lightLayout = _connectionLight.gameObject.AddComponent<LayoutElement>();
            lightLayout.preferredWidth = lightLayout.minWidth = 22;
            lightLayout.preferredHeight = 22;
            lightLayout.flexibleWidth = 0;

            _connectionText = Ui.Label(status, "Nie połączono: nauka bez EEG.", 20);
            _connectionText.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;

            _electrodes = Ui.Label(rightCard, string.Empty, 20, Ui.Muted);

            var failed = Ui.Row("CalibrationFailed", rightCard, 8);
            _calibrationFailed = failed.gameObject;
            var calibRetryBtn = Ui.Button(failed, "Powtórz kalibrację", () => _app.StartCalibration(), Ui.Glass, height: 44, fontSize: 18);
            var calibContBtn = Ui.Button(failed, "Kontynuuj bez EEG", _app.ContinueWithoutEeg, Ui.Glass, height: 44, fontSize: 18);
            _calibrationRetryText = calibRetryBtn.GetComponentInChildren<Text>();
            _calibrationContinueText = calibContBtn.GetComponentInChildren<Text>();

            _message = Ui.Label(rightCard, string.Empty, 20, Ui.Yellow);


            // FOOTER ROW: PRIMARY ACTION BUTTON
            _start = Ui.Button(main, "Rozpocznij naukę", Start, Ui.Accent, height: 64, fontSize: 26);


            // ---- MANUAL ENTRY PAGE ----
            var manual = Ui.Column("Manual", root, spacing: 16, padding: 28, background: Ui.Glass);
            Ui.Stretch(manual);
            _manual = manual.gameObject;
            _manualTitleText = Ui.Label(manual, "Własny zestaw", 36, style: FontStyle.Bold);
            _manualName = Ui.Input(manual, "Nazwa zestawu");
            _manualPrompt = Ui.Input(manual, "Pojęcie / wydarzenie");
            _manualAnswer = Ui.Input(manual, "Definicja / data");

            var manualRow = Ui.Row("ManualButtons", manual, 10);
            _manualAddBtn = Ui.Button(manualRow, "Dodaj parę", AddManualPair, Ui.Panel, height: 50, fontSize: 20);
            _manualDoneBtn = Ui.Button(manualRow, "Gotowe", FinishManual, Ui.Accent, height: 50, fontSize: 20);
            _manualCancelBtn = Ui.Button(manualRow, "Anuluj", () => ShowManual(false), Ui.Panel, height: 50, fontSize: 20);
            _manualList = Ui.Label(manual, string.Empty, 20, Ui.Muted);

            ShowManual(false);
            UpdateTranslations();
            Refresh();
        }

        private void ToggleLanguage()
        {
            IsEnglish = !IsEnglish;
            UpdateTranslations();
            Refresh();
        }

        private void UpdateTranslations()
        {
            _titleText.text = T("Przygotowanie sesji", "Session Setup");
            _step1Text.text = T("WYBÓR MATERIAŁU", "MATERIAL SELECTION");
            _importFileText.text = T("Z pliku (CSV/JSON)", "From file (CSV/JSON)");
            _importManualText.text = T("Wpisz ręcznie", "Enter manually");
            _step2Text.text = T("OPASKA EEG I STATUS", "EEG & DEVICE STATUS");
            _manualTitleText.text = T("Własny zestaw", "Custom Set");
            _calibrationRetryText.text = T("Powtórz kalibrację", "Retry Calibration");
            _calibrationContinueText.text = T("Kontynuuj bez EEG", "Continue without EEG");

            Ui.SetButtonText(_manualAddBtn, T("Dodaj parę", "Add Pair"));
            Ui.SetButtonText(_manualDoneBtn, T("Gotowe", "Done"));
            Ui.SetButtonText(_manualCancelBtn, T("Anuluj", "Cancel"));
            Ui.SetButtonText(_devToggle, DevLabel());

            if (_manualName.placeholder is Text namePh) namePh.text = T("Nazwa zestawu", "Set Name");
            if (_manualPrompt.placeholder is Text promptPh) promptPh.text = T("Pojęcie / wydarzenie", "Term / Event");
            if (_manualAnswer.placeholder is Text answerPh) answerPh.text = T("Definicja / data", "Definition / Date");

            if (_app.Material == null)
                _materialInfo.text = T("Nie wybrano materiału.", "No material selected.");
        }

        /// <summary>
        /// Wide and short: the glasses' view is ~46 x 26 degrees, so height is the scarce axis.
        /// 1040 x 560 units at 0.65 m is about 40 x 22 degrees.
        /// </summary>
        public static readonly Vector2 CanvasSize = new Vector2(1040, 560);

        /// <summary>
        /// Zero preferred width means the row splits all space by flexibleWidth alone, so both cards stay
        /// exactly half-width instead of growing with their text (which also made them jump when messages changed).
        /// </summary>
        private static void EqualColumn(RectTransform card)
        {
            var e = card.gameObject.AddComponent<LayoutElement>();
            e.minWidth = 0;
            e.preferredWidth = 0;
            e.flexibleWidth = 1;
        }

        public void SetVisible(bool visible) => _root.SetActive(visible);

        public void Refresh()
        {
            var connection = _app.Connection;
            var light = _app.Light;
            if (connection?.LastError != null && connection.LastError != _seenError)
            {
                _seenError = connection.LastError;
                _errorAt = Time.time;
                Debug.LogWarning($"NeuroScholAR: EEG connect failed ({_seenError.Kind}): {_seenError.Message} {_seenError.InnerException}");
            }

            var showError = connection?.LastError != null && Time.time - _errorAt < MessageSeconds;
            _connectionLight.color = light == ConnectionLight.Green ? Ui.Green : light == ConnectionLight.Yellow ? Ui.Yellow : Ui.Red;

            _connectionText.text = _connecting ? T("Łączenie…", "Connecting...")
                : connection == null || connection.State == EegConnectionState.Disconnected
                    ? (showError ? ErrorText(connection.LastError) : T("Nie połączono: nauka bez EEG.", "Not connected: learning without EEG."))
                : connection.State == EegConnectionState.Connected ? (_useSimulator ? T("Połączono (symulator)", "Connected (Simulator)") : T("Połączono z opaską", "Connected to headband"))
                : T("Ponowne łączenie…", "Reconnecting...");

            Ui.SetButtonText(_connect, _app.EegStreaming ? T("Rozłącz opaskę", "Disconnect EEG") : T("Połącz opaskę EEG", "Connect EEG"));
            _connect.interactable = !_connecting;

            Ui.SetButtonText(_simulator, _useSimulator ? T("Źródło: symulator", "Source: Simulator") : T("Źródło: BrainBit", "Source: BrainBit"));
            _simulator.interactable = EegSources.DeviceAvailable && !_connecting;

            _electrodes.text = ElectrodeText(_app.ChannelNames, _app.ContactOk, _app.EegStreaming);

            var calibration = _app.CalibrationResult;
            var failed = calibration != null && !calibration.Succeeded;
            _calibrationFailed.SetActive(failed && _app.EegStreaming);

            if (failed && calibration != _seenCalibration)
            {
                _seenCalibration = calibration;
                _message.text = CalibrationText(calibration.Outcome);
            }

            if (_message.text != _seenMessage)
            {
                _seenMessage = _message.text;
                _messageAt = Time.time;
            }
            else if (_message.text.Length > 0 && Time.time - _messageAt > MessageSeconds)
            {
                _message.text = _seenMessage = string.Empty;
            }

            _start.interactable = _app.Material != null && !_connecting;
            string startBtnTxt = _app.EegStreaming ? T("Rozpocznij naukę (z EEG)", "Start Learning (with EEG)") : T("Rozpocznij naukę (bez EEG)", "Start Learning (no EEG)");
            Ui.SetButtonText(_start, startBtnTxt);
        }

        public static string ElectrodeText(IReadOnlyList<string> names, bool[] ok, bool streaming)
        {
            if (!streaming || names == null) return string.Empty;
            if (ok == null) return T("Sprawdzanie kontaktu elektrod…", "Checking electrode contact...");
            return string.Join("   ", names.Select((n, i) => $"{n}: {(i < ok.Length && ok[i] ? "OK" : T("Słaby kontakt", "Poor contact"))}"));
        }

        private string ErrorText(EegConnectionException e)
        {
            switch (e.Kind)
            {
                case EegErrorKind.BluetoothUnavailable: return T("Bluetooth jest wyłączony lub niedostępny. Włącz go i spróbuj ponownie.", "Bluetooth is disabled or unavailable. Turn it on and try again.");
                case EegErrorKind.PermissionDenied: return T("Brak uprawnień Bluetooth. Zezwól w ustawieniach systemu.", "Missing Bluetooth permissions. Allow in system settings.");
                case EegErrorKind.DeviceNotFound: return T("Nie znaleziono opaski. Włącz ją i zbliż do urządzenia.", "Headband not found. Turn it on and bring it closer.");
                default: return T("Nie udało się połączyć: ", "Failed to connect: ") + e.Message;
            }
        }

        private string CalibrationText(Core.Focus.CalibrationOutcome outcome)
        {
            switch (outcome)
            {
                case Core.Focus.CalibrationOutcome.Aborted: return T("Kalibracja przerwana.", "Calibration aborted.");
                case Core.Focus.CalibrationOutcome.TooFewCleanWindows: return T("Za dużo zakłóceń (mruganie, ruch). Usiądź spokojnie i powtórz kalibrację.", "Too much interference (blinking, movement). Sit still and retry calibration.");
                default: return T("Sygnał płaski: sprawdź kontakt elektrod i powtórz kalibrację.", "Flat signal: check electrode contact and retry calibration.");
            }
        }

        private void Select(ImportResult material)
        {
            _app.SelectMaterial(material);
            var set = material.Set;
            var used = Math.Min(set.Pairs.Count, PalaceSession.MaxObjects);
            var notes = material.Warnings.ToList();
            if (set.Pairs.Count > used)
            {
                notes.Add(T($"Pałac ma {PalaceSession.MaxObjects} miejsc: użyto pierwszych {used} par.", $"Palace has {PalaceSession.MaxObjects} slots: used first {used} pairs."));
            }

            _materialInfo.text = T($"Wybrano: {set.SetName} ({used} par)", $"Selected: {set.SetName} ({used} pairs)") +
                                 (notes.Count > 0 ? "\n" + string.Join("\n", notes) : string.Empty);
            _message.text = string.Empty;
            Ui.Clear(_fileList);
        }

        private void ShowFiles()
        {
            Ui.Clear(_fileList);
            var dir = Path.Combine(Application.persistentDataPath, "Import");
            Directory.CreateDirectory(dir);
            var files = Directory.GetFiles(dir).Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).Take(6).ToList();

            if (files.Count == 0)
            {
                Ui.Label(_fileList, T($"Brak plików. Skopiuj .csv/.json do:\n{dir}", $"No files. Copy .csv/.json to:\n{dir}"), 18, Ui.Muted);
                return;
            }

            foreach (var file in files)
            {
                Ui.Button(_fileList, Path.GetFileName(file), () => Import(file), Ui.Glass, height: 44, fontSize: 18);
            }
        }

        private void Import(string path)
        {
            try
            {
                if (new FileInfo(path).Length > MaterialParser.MaxFileBytes)
                {
                    throw new MaterialImportException(T("Plik jest za duży (limit 1 MB).", "File is too large (1 MB limit)."));
                }
                Select(MaterialParser.ParseFile(path, File.ReadAllText(path)));
            }
            catch (MaterialImportException e)
            {
                _message.text = e.Message;
            }
            catch (IOException e)
            {
                _message.text = T("Nie można odczytać pliku: ", "Cannot read file: ") + e.Message;
            }
        }

        private void ShowManual(bool show)
        {
            _manual.SetActive(show);
            _main.SetActive(!show);
        }

        private void AddManualPair()
        {
            if (string.IsNullOrWhiteSpace(_manualPrompt.text) || string.IsNullOrWhiteSpace(_manualAnswer.text))
            {
                _manualList.text = T("Uzupełnij oba pola.", "Fill in both fields.");
                return;
            }

            _manualPairs.Add((_manualPrompt.text, _manualAnswer.text));
            _manualPrompt.text = _manualAnswer.text = string.Empty;
            _manualList.text = string.Join("\n", _manualPairs.Select(p => $"• {p.prompt}: {p.answer}"));
        }

        private void FinishManual()
        {
            try
            {
                var type = _manualPairs.All(p => p.answer.Trim().All(c => char.IsDigit(c) || c == '.' || c == '-'))
                    ? StudySetType.TermDate
                    : StudySetType.TermDefinition;
                Select(MaterialParser.FromManual(_manualName.text, type, _manualPairs));
                ShowManual(false);
            }
            catch (MaterialImportException e)
            {
                _manualList.text = e.Message;
            }
        }

        private void ToggleSimulator()
        {
            _useSimulator = !_useSimulator;
            _app.ReleaseEeg();
        }

        public bool UseSimulator => _useSimulator;

        private string DevLabel() => (DevMode.Enabled ? "[x]" : "[  ]") + T(" Tryb deweloperski", " Dev Mode");

        private void ToggleDev()
        {
            DevMode.Enabled = !DevMode.Enabled;
            Ui.SetButtonText(_devToggle, DevLabel());
        }

        private async void Connect()
        {
            if (_app.EegStreaming)
            {
                _app.DisconnectEeg();
                return;
            }

            _connecting = true;
            _message.text = string.Empty;
            try
            {
                if (!_useSimulator && !await BluetoothPermissions.EnsureAsync())
                {
                    _message.text = T("Brak uprawnień Bluetooth. Zezwól w ustawieniach systemu.", "Missing Bluetooth permissions. Allow in system settings.");
                    return;
                }

                await _app.ConnectEegAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning("NeuroScholAR: EEG connect error: " + e);
                _message.text = T("Błąd połączenia: ", "Connection error: ") + e.Message;
            }
            finally
            {
                _connecting = false;
            }
        }

        private void Start()
        {
            try
            {
                _app.Start();
            }
            catch (Exception e) when (e is InvalidOperationException || e is ArgumentException)
            {
                _message.text = e.Message;
            }
        }
    }
}