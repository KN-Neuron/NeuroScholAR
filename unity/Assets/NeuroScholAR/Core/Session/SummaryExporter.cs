using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NeuroScholAR.Core.Session
{
    public enum ExportStatus
    {
        /// <summary>Uploaded and the CSV was saved locally.</summary>
        Exported,

        /// <summary>Backend unreachable: the summary waits in the outbox and will be retried.</summary>
        QueuedForRetry,

        /// <summary>No backend URL configured: the summary is kept locally only.</summary>
        NotConfigured,
    }

    public sealed class ExportResult
    {
        public ExportResult(ExportStatus status, string csvPath = null, string error = null)
        {
            Status = status;
            CsvPath = csvPath;
            Error = error;
        }

        public ExportStatus Status { get; }
        public string CsvPath { get; }
        public string Error { get; }
    }

    /// <summary>
    /// Local storage plus backend export with an outbox. EEG data is sensitive, so nothing leaves the device
    /// unless the user presses export; until the backend confirms, the summary stays in <c>outbox/</c> and
    /// <see cref="RetryPendingAsync"/> resends it later.
    /// TODO(post-MVP): long-term cross-session storage and comparison would build on <c>results/</c>.
    /// </summary>
    public sealed class SummaryExporter : IDisposable
    {
        private readonly string _results;
        private readonly string _outbox;
        private readonly string _exports;
        private readonly Uri _backend;
        private readonly HttpClient _http;

        /// <param name="dataDirectory">Writable app folder (Unity: Application.persistentDataPath).</param>
        /// <param name="backendBaseUrl">From config or environment, never hardcoded. Null/empty disables upload.</param>
        public SummaryExporter(string dataDirectory, string backendBaseUrl, HttpMessageHandler handler = null)
        {
            _results = Directory.CreateDirectory(Path.Combine(dataDirectory, "results")).FullName;
            _outbox = Directory.CreateDirectory(Path.Combine(dataDirectory, "outbox")).FullName;
            _exports = Directory.CreateDirectory(Path.Combine(dataDirectory, "exports")).FullName;
            if (!string.IsNullOrWhiteSpace(backendBaseUrl) && Uri.TryCreate(backendBaseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri))
            {
                _backend = uri;
            }

            _http = handler == null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = TimeSpan.FromSeconds(10);
        }

        public bool IsConfigured => _backend != null;
        public int PendingCount => Directory.GetFiles(_outbox, "*.json").Length;

        /// <summary>"Zapisz wyniki": keep the summary on the device.</summary>
        public string SaveLocal(SessionSummary summary)
        {
            var path = Path.Combine(_results, SafeName(summary.SessionId) + ".json");
            File.WriteAllText(path, summary.ToJson(), Encoding.UTF8);
            return path;
        }

        /// <summary>"Eksportuj CSV": queue, upload, then download the CSV the backend renders.</summary>
        public async Task<ExportResult> ExportAsync(SessionSummary summary, CancellationToken ct = default)
        {
            SaveLocal(summary);
            if (!IsConfigured)
            {
                return new ExportResult(ExportStatus.NotConfigured, error: "Brak adresu serwera w konfiguracji.");
            }

            var queued = Path.Combine(_outbox, SafeName(summary.SessionId) + ".json");
            File.WriteAllText(queued, summary.ToJson(), Encoding.UTF8);
            try
            {
                await UploadAsync(queued, ct).ConfigureAwait(false);
                return new ExportResult(ExportStatus.Exported, await DownloadCsvAsync(summary.SessionId, ct).ConfigureAwait(false));
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is IOException)
            {
                return new ExportResult(ExportStatus.QueuedForRetry, error: e.Message);
            }
        }

        /// <summary>Resends everything in the outbox. Returns how many went through. Call on start and periodically.</summary>
        public async Task<int> RetryPendingAsync(CancellationToken ct = default)
        {
            if (!IsConfigured)
            {
                return 0;
            }

            var sent = 0;
            foreach (var file in Directory.GetFiles(_outbox, "*.json"))
            {
                try
                {
                    await UploadAsync(file, ct).ConfigureAwait(false);
                    sent++;
                }
                catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is IOException)
                {
                    break; // still offline; try again next time
                }
            }

            return sent;
        }

        public void Dispose() => _http.Dispose();

        private async Task UploadAsync(string file, CancellationToken ct)
        {
            var json = File.ReadAllText(file, Encoding.UTF8);
            using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
            using (var response = await _http.PostAsync(new Uri(_backend, "sessions"), content, ct).ConfigureAwait(false))
            {
                if ((int)response.StatusCode >= 500)
                {
                    throw new HttpRequestException($"Serwer zwrócił {(int)response.StatusCode}");
                }

                // 4xx means the payload itself is rejected; retrying will not help, so it leaves the outbox
                // (the copy in results/ is kept).
            }

            File.Delete(file);
        }

        private async Task<string> DownloadCsvAsync(string sessionId, CancellationToken ct)
        {
            var url = new Uri(_backend, "sessions/" + Uri.EscapeDataString(sessionId) + "/export.csv");
            var csv = await _http.GetStringAsync(url).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(_exports, SafeName(sessionId) + ".csv");
            File.WriteAllText(path, csv, Encoding.UTF8);
            return path;
        }

        private static string SafeName(string id)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                id = id.Replace(c, '_');
            }

            return id;
        }
    }
}
