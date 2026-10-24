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
    /// <summary>
    /// Session setup in at most 3 steps: (1) pick material, (2) optionally connect the headband,
    /// (3) "Rozpocznij naukę". Imports read from <c>{persistentDataPath}/Import</c> (no native file picker in Unity;
    /// copy files there over USB or from the Editor).
    /// </summary>
    public sealed class SetupScreen
    {
        private readonly SessionController _app;
        private readonly GameObject _root;
        private readonly GameObject _main;
        private readonly GameObject _manual;
        private readonly Text _materialInfo;
        private readonly RectTransform _fileList;
        private readonly Text _connectionText;
        private readonly Image _connectionLight;
        private readonly Text _electrodes;
        private readonly Text _message;
        private const float MessageSeconds = 10f; // errors and hints fade instead of staying up for good
        private EegConnectionException _seenError;
        private float _errorAt = float.NegativeInfinity;
        private string _seenMessage = string.Empty;
        private float _messageAt;
        private object _seenCalibration;
        private readonly Button _connect;
        private readonly Button _simulator;
        private readonly Button _start;
        private readonly GameObject _calibrationFailed;
        private readonly InputField _manualName;
        private readonly InputField _manualPrompt;
        private readonly InputField _manualAnswer;
        private readonly Text _manualList;
        private readonly List<(string prompt, string answer)> _manualPairs = new List<(string, string)>();
        private readonly Button _devToggle;
        private bool _useSimulator;
        private bool _connecting;

        public SetupScreen(Transform canvas, SessionController app, bool useSimulator)
        {
            _app = app;
            _useSimulator = useSimulator || !EegSources.DeviceAvailable;

            var root = Ui.Rect("Setup", canvas);
            Ui.Stretch(root);
            _root = root.gameObject;

            // ---- main page ----
            var main = Ui.Column("Main", root, 14, 32, Ui.Background);
            Ui.Stretch(main);
            _main = main.gameObject;
            var header = Ui.Row("Header", main, 10);
            var title = Ui.Label(header, "Przygotowanie sesji", 42, style: FontStyle.Bold);
            title.gameObject.AddComponent<LayoutElement>().flexibleWidth = 3;
            // Checkbox-style toggle (the UI kit has buttons only): every debugging aid, see DevMode.
            _devToggle = Ui.Button(header, DevLabel(), ToggleDev, Ui.Panel, 52, 20);

            Ui.Label(main, "1. Materiał", 30, Ui.Muted, style: FontStyle.Bold);
            var sets = Ui.Row("Sets", main, 10);
            foreach (var set in PredefinedSets.All)
            {
                Ui.Button(sets, set.SetName, () => Select(new ImportResult(set, Array.Empty<string>())), Ui.Panel, 56, 22);
            }

            var importRow = Ui.Row("Import", main, 10);
            Ui.Button(importRow, "Z pliku (CSV/JSON)", ShowFiles, Ui.Panel, 56, 24);
            Ui.Button(importRow, "Wpisz ręcznie", () => ShowManual(true), Ui.Panel, 56, 24);
            _fileList = Ui.Column("Files", main, 6);
            _materialInfo = Ui.Label(main, "Nie wybrano materiału.", 24, Ui.Muted);

            Ui.Label(main, "2. Opaska EEG (opcjonalnie)", 30, Ui.Muted, style: FontStyle.Bold);
            var eegRow = Ui.Row("Eeg", main, 10);
            _connect = Ui.Button(eegRow, "Połącz opaskę EEG", Connect, Ui.Accent, 60, 24);
            _simulator = Ui.Button(eegRow, string.Empty, ToggleSimulator, Ui.Panel, 60, 22);
            var status = Ui.Row("Status", main, 10);
            _connectionLight = Ui.Box("Light", status, Ui.Red);
            var lightLayout = _connectionLight.gameObject.AddComponent<LayoutElement>();
            lightLayout.preferredWidth = lightLayout.minWidth = 28;
            lightLayout.preferredHeight = 28;
            lightLayout.flexibleWidth = 0;
            _connectionText = Ui.Label(status, "Nie połączono: nauka bez EEG.", 24);
            _connectionText.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1;
            _electrodes = Ui.Label(main, string.Empty, 24, Ui.Muted);

            var failed = Ui.Row("CalibrationFailed", main, 10);
            _calibrationFailed = failed.gameObject;
            Ui.Button(failed, "Powtórz kalibrację", () => _app.StartCalibration(), Ui.Panel, 56, 22);
            Ui.Button(failed, "Kontynuuj bez EEG", _app.ContinueWithoutEeg, Ui.Panel, 56, 22);

            _message = Ui.Label(main, string.Empty, 24, Ui.Yellow);
            _start = Ui.Button(main, "3. Rozpocznij naukę", Start, Ui.Green, 72, 32);

            // ---- manual entry page ----
            var manual = Ui.Column("Manual", root, 12, 32, Ui.Background);
            Ui.Stretch(manual);
            _manual = manual.gameObject;
            Ui.Label(manual, "Własny zestaw", 40, style: FontStyle.Bold);
            _manualName = Ui.Input(manual, "Nazwa zestawu");
            _manualPrompt = Ui.Input(manual, "Pojęcie / wydarzenie");
            _manualAnswer = Ui.Input(manual, "Definicja / data");
            var manualRow = Ui.Row("ManualButtons", manual, 10);
            Ui.Button(manualRow, "Dodaj parę", AddManualPair, Ui.Accent, 56, 24);
            Ui.Button(manualRow, "Gotowe", FinishManual, Ui.Green, 56, 24);
            Ui.Button(manualRow, "Anuluj", () => ShowManual(false), Ui.Panel, 56, 24);
            _manualList = Ui.Label(manual, string.Empty, 22, Ui.Muted);

            ShowManual(false);
            Refresh();
        }

        public void SetVisible(bool visible) => _root.SetActive(visible);

        /// <summary>Called a few times per second while visible.</summary>
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
            _connectionText.text = _connecting ? "Łączenie…"
                : connection == null || connection.State == EegConnectionState.Disconnected
                    ? (showError ? ErrorText(connection.LastError) : "Nie połączono: nauka bez EEG.")
                : connection.State == EegConnectionState.Connected ? (_useSimulator ? "Połączono (symulator)" : "Połączono z opaską")
                : "Ponowne łączenie…";
            Ui.SetButtonText(_connect, _app.EegStreaming ? "Rozłącz opaskę" : "Połącz opaskę EEG");
            _connect.interactable = !_connecting;
            Ui.SetButtonText(_simulator, _useSimulator ? "Źródło: symulator" : "Źródło: BrainBit");
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
            Ui.SetButtonText(_start, _app.EegStreaming ? "3. Rozpocznij naukę (z EEG)" : "3. Rozpocznij naukę (bez EEG)");
        }

        /// <summary>"O1: OK   O2: Słaby kontakt …" (shown before and, in the HUD, during the session).</summary>
        public static string ElectrodeText(IReadOnlyList<string> names, bool[] ok, bool streaming)
        {
            if (!streaming || names == null)
            {
                return string.Empty;
            }

            if (ok == null)
            {
                return "Sprawdzanie kontaktu elektrod…";
            }

            return string.Join("   ", names.Select((n, i) => $"{n}: {(i < ok.Length && ok[i] ? "OK" : "Słaby kontakt")}"));
        }

        private static string ErrorText(EegConnectionException e)
        {
            switch (e.Kind)
            {
                case EegErrorKind.BluetoothUnavailable:
                    return "Bluetooth jest wyłączony lub niedostępny. Włącz go i spróbuj ponownie.";
                case EegErrorKind.PermissionDenied:
                    return "Brak uprawnień Bluetooth. Zezwól w ustawieniach systemu.";
                case EegErrorKind.DeviceNotFound:
                    return "Nie znaleziono opaski. Włącz ją i zbliż do urządzenia.";
                default:
                    return "Nie udało się połączyć: " + e.Message;
            }
        }

        private static string CalibrationText(Core.Focus.CalibrationOutcome outcome)
        {
            switch (outcome)
            {
                case Core.Focus.CalibrationOutcome.Aborted:
                    return "Kalibracja przerwana.";
                case Core.Focus.CalibrationOutcome.TooFewCleanWindows:
                    return "Za dużo zakłóceń (mruganie, ruch). Usiądź spokojnie i powtórz kalibrację.";
                default:
                    return "Sygnał płaski: sprawdź kontakt elektrod i powtórz kalibrację.";
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
                notes.Add($"Pałac ma {PalaceSession.MaxObjects} miejsc: użyto pierwszych {used} par.");
            }

            _materialInfo.text = $"Wybrano: {set.SetName} ({used} par)" + (notes.Count > 0 ? "\n" + string.Join("\n", notes) : string.Empty);
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
                Ui.Label(_fileList, $"Brak plików. Skopiuj .csv/.json do:\n{dir}", 20, Ui.Muted);
                return;
            }

            foreach (var file in files)
            {
                Ui.Button(_fileList, Path.GetFileName(file), () => Import(file), Ui.Panel, 48, 22);
            }
        }

        private void Import(string path)
        {
            try
            {
                if (new FileInfo(path).Length > MaterialParser.MaxFileBytes)
                {
                    throw new MaterialImportException("Plik jest za duży (limit 1 MB).");
                }

                Select(MaterialParser.ParseFile(path, File.ReadAllText(path)));
            }
            catch (MaterialImportException e)
            {
                _message.text = e.Message;
            }
            catch (IOException e)
            {
                _message.text = "Nie można odczytać pliku: " + e.Message;
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
                _manualList.text = "Uzupełnij oba pola.";
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
            _app.ReleaseEeg(); // next connect builds the other source
        }

        public bool UseSimulator => _useSimulator;

        private static string DevLabel() => (DevMode.Enabled ? "[x]" : "[  ]") + " Tryb deweloperski";

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
                    _message.text = "Brak uprawnień Bluetooth. Zezwól w ustawieniach systemu.";
                    return;
                }

                await _app.ConnectEegAsync();
            }
            catch (Exception e)
            {
                // async void: never let an exception escape to the Unity log as an unhandled crash.
                Debug.LogWarning("NeuroScholAR: EEG connect error: " + e);
                _message.text = "Błąd połączenia: " + e.Message;
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
                _app.Start(); // the app root shows calibration or the session on the phase change
            }
            catch (Exception e) when (e is InvalidOperationException || e is ArgumentException)
            {
                _message.text = e.Message;
            }
        }
    }
}
