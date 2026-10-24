using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NeuroScholAR.Core.Learning
{
    /// <summary>Import rejected. The message is user-facing (Polish).</summary>
    public sealed class MaterialImportException : Exception
    {
        public MaterialImportException(string message, Exception inner = null) : base(message, inner) { }
    }

    public sealed class ImportResult
    {
        public ImportResult(StudySet set, IReadOnlyList<string> warnings)
        {
            Set = set;
            Warnings = warnings;
        }

        public StudySet Set { get; }

        /// <summary>Non-fatal fixes (duplicates skipped, ids renumbered, set truncated). Shown under the import button.</summary>
        public IReadOnlyList<string> Warnings { get; }
    }

    /// <summary>
    /// Validates and parses study material from JSON (spec schema), CSV, or manual entry. Imported files
    /// are untrusted: size, row count and field lengths are capped, and everything is trimmed.
    /// </summary>
    public static class MaterialParser
    {
        public const int MaxFileBytes = 1024 * 1024;
        public const int MaxPairs = 200;
        public const int MaxFieldLength = 300;

        private static readonly Regex DateLike = new Regex(@"^\s*(\d{1,4}([.\-/]\d{1,2}){0,2}|\d{1,2}\s+\p{L}+\s+\d{1,4})\s*(r\.?|p\.n\.e\.|n\.e\.)?\s*$");
        private static readonly string[] HeaderPrompts = { "prompt", "term", "pojęcie", "pojecie", "termin", "pytanie" };

        /// <summary>Picks the parser by file extension.</summary>
        public static ImportResult ParseFile(string fileName, string content)
        {
            var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
            var name = Path.GetFileNameWithoutExtension(fileName ?? "Import");
            switch (extension)
            {
                case ".json":
                    return ParseJson(content);
                case ".csv":
                case ".txt":
                    return ParseCsv(content, name);
                default:
                    throw new MaterialImportException("Nieobsługiwany format pliku. Użyj .csv lub .json.");
            }
        }

        public static ImportResult ParseJson(string content)
        {
            CheckSize(content);
            JObject root;
            try
            {
                root = JObject.Parse(content);
            }
            catch (JsonException e)
            {
                throw new MaterialImportException("Plik JSON jest uszkodzony: " + e.Message, e);
            }

            var setName = (root.Value<string>("set_name") ?? "Import").Trim();
            var type = ParseType(root.Value<string>("type"));
            if (!(root["pairs"] is JArray pairs))
            {
                throw new MaterialImportException("Brak listy \"pairs\" w pliku JSON.");
            }

            var rows = new List<(string id, string prompt, string answer)>();
            foreach (var token in pairs)
            {
                if (token is JObject pair)
                {
                    rows.Add((pair.Value<string>("id"), pair.Value<string>("prompt"), pair.Value<string>("answer")));
                }
                else
                {
                    rows.Add((null, null, null)); // counted as an invalid row below
                }
            }

            return Build(setName, type, rows);
        }

        /// <summary>
        /// CSV with columns <c>prompt,answer[,id]</c>. Comma or semicolon (Polish Excel default) separated,
        /// RFC 4180 quoting, optional header row. The set type is guessed: all answers date-like → term_date.
        /// </summary>
        public static ImportResult ParseCsv(string content, string setName)
        {
            CheckSize(content);
            var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count == 0)
            {
                throw new MaterialImportException("Plik jest pusty.");
            }

            var delimiter = lines[0].Count(c => c == ';') > lines[0].Count(c => c == ',') ? ';' : ',';
            var rows = new List<(string id, string prompt, string answer)>();
            for (var i = 0; i < lines.Count; i++)
            {
                var cells = SplitCsvLine(lines[i], delimiter);
                if (i == 0 && cells.Count > 0 && HeaderPrompts.Contains(cells[0].Trim().ToLowerInvariant()))
                {
                    continue;
                }

                rows.Add((cells.Count > 2 ? cells[2] : null, cells.Count > 0 ? cells[0] : null, cells.Count > 1 ? cells[1] : null));
            }

            var type = rows.Count > 0 && rows.All(r => r.answer != null && DateLike.IsMatch(r.answer))
                ? StudySetType.TermDate
                : StudySetType.TermDefinition;
            return Build(string.IsNullOrWhiteSpace(setName) ? "Import" : setName.Trim(), type, rows);
        }

        /// <summary>Manual entry from the setup screen goes through the same validation.</summary>
        public static ImportResult FromManual(string setName, StudySetType type, IEnumerable<(string prompt, string answer)> pairs) =>
            Build(string.IsNullOrWhiteSpace(setName) ? "Mój zestaw" : setName.Trim(), type,
                pairs.Select(p => ((string)null, p.prompt, p.answer)).ToList());

        private static ImportResult Build(string setName, StudySetType type, List<(string id, string prompt, string answer)> rows)
        {
            var warnings = new List<string>();
            var pairs = new List<StudyPair>();
            var seenPrompts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var invalid = 0;
            var duplicates = 0;

            foreach (var row in rows)
            {
                var prompt = Clean(row.prompt);
                var answer = Clean(row.answer);
                if (prompt.Length == 0 || answer.Length == 0)
                {
                    invalid++;
                    continue;
                }

                if (!seenPrompts.Add(prompt))
                {
                    duplicates++;
                    continue;
                }

                if (pairs.Count == MaxPairs)
                {
                    warnings.Add($"Zestaw ograniczono do {MaxPairs} par.");
                    break;
                }

                var id = Clean(row.id);
                if (id.Length == 0 || !seenIds.Add(id))
                {
                    id = "p" + (pairs.Count + 1);
                    while (!seenIds.Add(id))
                    {
                        id += "_";
                    }
                }

                pairs.Add(new StudyPair(id, prompt, answer));
            }

            if (invalid > 0)
            {
                warnings.Add($"Pominięto {invalid} niepełnych wierszy.");
            }

            if (duplicates > 0)
            {
                warnings.Add($"Pominięto {duplicates} duplikatów.");
            }

            if (pairs.Count == 0)
            {
                throw new MaterialImportException("Zestaw nie zawiera żadnej poprawnej pary.");
            }

            return new ImportResult(new StudySet(Truncate(setName), type, pairs), warnings);
        }

        private static StudySetType ParseType(string value)
        {
            switch ((value ?? "term_definition").Trim().ToLowerInvariant())
            {
                case "term_definition":
                    return StudySetType.TermDefinition;
                case "term_date":
                    return StudySetType.TermDate;
                default:
                    throw new MaterialImportException($"Nieznany typ zestawu: \"{value}\". Dozwolone: term_definition, term_date.");
            }
        }

        private static void CheckSize(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new MaterialImportException("Plik jest pusty.");
            }

            if (Encoding.UTF8.GetByteCount(content) > MaxFileBytes)
            {
                throw new MaterialImportException("Plik jest za duży (limit 1 MB).");
            }
        }

        private static string Clean(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                sb.Append(char.IsControl(c) ? ' ' : c);
            }

            return Truncate(sb.ToString().Trim());
        }

        private static string Truncate(string value) => value.Length <= MaxFieldLength ? value : value.Substring(0, MaxFieldLength);

        private static List<string> SplitCsvLine(string line, char delimiter)
        {
            var cells = new List<string>();
            var cell = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        cell.Append('"');
                        i++;
                    }
                    else if (c == '"')
                    {
                        quoted = false;
                    }
                    else
                    {
                        cell.Append(c);
                    }
                }
                else if (c == '"')
                {
                    quoted = true;
                }
                else if (c == delimiter)
                {
                    cells.Add(cell.ToString());
                    cell.Clear();
                }
                else
                {
                    cell.Append(c);
                }
            }

            cells.Add(cell.ToString());
            return cells;
        }
    }
}
