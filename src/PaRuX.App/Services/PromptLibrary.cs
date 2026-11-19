using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaRuX.Services
{
    /// <summary>One reusable prompt. The text may hold <c>{{placeholders}}</c> the owner fills in before it reaches the composer.</summary>
    public sealed record SavedPrompt(string Id, string Title, string Text, DateTime CreatedUtc);

    /// <summary>Placeholder handling shared by starters and saved prompts. Pure text, no UI.</summary>
    public static class PromptTemplate
    {
        private static readonly Regex Placeholder = new(@"\{\{\s*([A-Za-z][A-Za-z0-9 _-]{0,30}?)\s*\}\}", RegexOptions.Compiled);

        /// <summary>Distinct placeholder names in order of first appearance.</summary>
        public static IReadOnlyList<string> Variables(string? text) =>
            Placeholder.Matches(text ?? string.Empty).Select(m => m.Groups[1].Value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        /// <summary>Replaces every placeholder with its value; a placeholder without a value is left visible so nothing is silently dropped.</summary>
        public static string Fill(string? text, IReadOnlyDictionary<string, string>? values) =>
            Placeholder.Replace(text ?? string.Empty, m =>
            {
                string name = m.Groups[1].Value.Trim();
                if (values is not null)
                    foreach (var pair in values)
                        if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(pair.Value)) return pair.Value;
                return m.Value;
            });
    }

    /// <summary>Built-in starting points (never stored, never editable). English and Italian.</summary>
    public static class StarterPrompts
    {
        public static IReadOnlyList<SavedPrompt> All(bool italian) => new[]
        {
            Make("starter-summarize", italian ? "Riassumi un testo" : "Summarize a text",
                italian ? "Riassumi il testo seguente in 5 punti chiari, poi indica le decisioni o le azioni da fare.\n\n{{testo}}"
                        : "Summarize the following text in 5 clear points, then list any decisions or actions.\n\n{{text}}"),
            Make("starter-explain", italian ? "Spiega in modo semplice" : "Explain simply",
                italian ? "Spiegami {{argomento}} in modo semplice, con un esempio concreto e senza gergo."
                        : "Explain {{topic}} simply, with one concrete example and no jargon."),
            Make("starter-rewrite", italian ? "Riscrivi con un tono" : "Rewrite in a tone",
                italian ? "Riscrivi questo testo con un tono {{tono}}, mantenendo il significato:\n\n{{testo}}"
                        : "Rewrite this text in a {{tone}} tone, keeping the meaning:\n\n{{text}}"),
            Make("starter-translate", italian ? "Traduci" : "Translate",
                italian ? "Traduci in {{lingua}} il testo seguente, mantenendo formattazione e nomi propri:\n\n{{testo}}"
                        : "Translate the following text into {{language}}, keeping formatting and proper names:\n\n{{text}}"),
            Make("starter-review", italian ? "Revisione del codice" : "Code review",
                italian ? "Fai una revisione del codice seguente: bug probabili, casi limite, sicurezza e semplificazioni. Sii concreto.\n\n{{codice}}"
                        : "Review the following code: likely bugs, edge cases, security and simplifications. Be concrete.\n\n{{code}}"),
            Make("starter-proscons", italian ? "Pro e contro" : "Pros and cons",
                italian ? "Elenca pro e contro di questa decisione e dai una raccomandazione motivata: {{decisione}}"
                        : "List the pros and cons of this decision and give a reasoned recommendation: {{decision}}"),
        };

        private static SavedPrompt Make(string id, string title, string text) => new(id, title, text, DateTime.UnixEpoch);
    }

    /// <summary>
    /// The owner's saved prompts: a small JSON file under the PaRuX data folder. Local only; bounded (200 prompts, 80-character
    /// titles, 8,000-character texts); written through a temporary file so a crash never leaves half a file.
    /// </summary>
    public sealed class PromptLibraryStore
    {
        public const int MaxPrompts = 200;
        public const int MaxTitle = 80;
        public const int MaxText = 8000;

        private readonly string _path;
        private readonly object _gate = new();

        public PromptLibraryStore(string path) => _path = Path.GetFullPath(path);

        public IReadOnlyList<SavedPrompt> List()
        {
            lock (_gate) return Read().OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        /// <summary>Saves a prompt; the same title replaces the older prompt. Null when the title or text is empty or too long, or the library is full.</summary>
        public SavedPrompt? Save(string? title, string? text)
        {
            string t = (title ?? string.Empty).Trim();
            string body = (text ?? string.Empty).Trim();
            if (t.Length == 0 || t.Length > MaxTitle || body.Length == 0 || body.Length > MaxText || t.Any(char.IsControl)) return null;
            lock (_gate)
            {
                List<SavedPrompt> all = Read();
                SavedPrompt? existing = all.FirstOrDefault(p => string.Equals(p.Title, t, StringComparison.CurrentCultureIgnoreCase));
                if (existing is null && all.Count >= MaxPrompts) return null;
                var saved = new SavedPrompt(existing?.Id ?? Guid.NewGuid().ToString("N"), t, body, existing?.CreatedUtc ?? DateTime.UtcNow);
                all.RemoveAll(p => p.Id == saved.Id);
                all.Add(saved);
                Write(all);
                return saved;
            }
        }

        public bool Remove(string? id)
        {
            lock (_gate)
            {
                List<SavedPrompt> all = Read();
                if (all.RemoveAll(p => p.Id == id) == 0) return false;
                Write(all);
                return true;
            }
        }

        private List<SavedPrompt> Read()
        {
            try
            {
                if (!File.Exists(_path)) return new List<SavedPrompt>();
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_path, Encoding.UTF8));
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("prompts", out JsonElement arr)
                    || arr.ValueKind != JsonValueKind.Array) return new List<SavedPrompt>();
                var result = new List<SavedPrompt>();
                foreach (JsonElement e in arr.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    string id = Str(e, "id"), title = Str(e, "title"), text = Str(e, "text");
                    if (id.Length == 0 || title.Length == 0 || text.Length == 0 || title.Length > MaxTitle || text.Length > MaxText) continue;
                    DateTime created = e.TryGetProperty("createdUtc", out JsonElement c) && c.TryGetDateTime(out DateTime d) ? d : DateTime.UtcNow;
                    result.Add(new SavedPrompt(id, title, text, created));
                    if (result.Count >= MaxPrompts) break;
                }
                return result;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new List<SavedPrompt>();
            }
        }

        private void Write(List<SavedPrompt> all)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string json = JsonSerializer.Serialize(new
            {
                version = 1,
                prompts = all.Select(p => new { id = p.Id, title = p.Title, text = p.Text, createdUtc = p.CreatedUtc }),
            }, new JsonSerializerOptions { WriteIndented = true });
            string tmp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(_path)) File.Replace(tmp, _path, null); else File.Move(tmp, _path);
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
    }
}
