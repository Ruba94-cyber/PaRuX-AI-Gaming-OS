using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace PaRuX.Services
{
    /// <summary>
    /// Opt-in diagnostic trace of every structured coding-agent model call. Active only when the
    /// environment variable PARUX_CODING_TRACE_DIR names a directory. Writes, per call, the exact
    /// request body sent to PaRuX Core and the raw response (status, assembled content,
    /// reasoning_content, finish_reason, usage and the raw SSE lines) BEFORE any parsing or
    /// steering. Headers are never written; the Core token and bearer-looking values are redacted,
    /// every file is size-bounded and the number of files is capped. Tracing can never fail a run.
    /// </summary>
    public sealed class CodingModelCallTrace
    {
        public const string EnvironmentVariable = "PARUX_CODING_TRACE_DIR";
        public const int MaxFileCharacters = 1_000_000;
        public const int MaxRawLines = 4_000;
        public const int MaxFilesPerProcess = 2_000;

        private static int _sequence;
        private static readonly Regex Bearer = new(@"(?i)(bearer\s+)[A-Za-z0-9._~+/=-]{8,}", RegexOptions.Compiled);
        private static readonly Regex TokenHeader = new(@"(?i)(x-parux-token""?\s*[:=]\s*""?)[A-Za-z0-9._~+/=-]{8,}", RegexOptions.Compiled);

        private readonly string _directory;
        private readonly string _stem;
        private readonly string[] _secrets;
        private readonly List<string> _raw = new();
        private readonly StringBuilder _content = new();
        private readonly StringBuilder _reasoning = new();
        private string? _finishReason;
        private string? _usage;
        private int? _status;
        private string? _error;
        private string _endpoint = string.Empty;
        private bool _rawTruncated;
        private bool _completed;

        private CodingModelCallTrace(string directory, string stem, string[] secrets)
        {
            _directory = directory;
            _stem = stem;
            _secrets = secrets;
        }

        /// <summary>Returns null (and does nothing) unless tracing is enabled and the directory is usable.</summary>
        public static CodingModelCallTrace? TryCreate(string? label, IEnumerable<string?>? secrets = null, string? directoryOverride = null)
        {
            try
            {
                string? directory = directoryOverride ?? Environment.GetEnvironmentVariable(EnvironmentVariable);
                if (string.IsNullOrWhiteSpace(directory)) return null;
                directory = Path.GetFullPath(directory.Trim());
                Directory.CreateDirectory(directory);
                int sequence = Interlocked.Increment(ref _sequence);
                if (sequence > MaxFilesPerProcess) return null;
                string safe = Regex.Replace(label ?? "call", "[^A-Za-z0-9_.-]", "_");
                if (safe.Length > 60) safe = safe.Substring(0, 60);
                string stem = sequence.ToString("D4") + "-" + safe;
                string[] secretValues = (secrets ?? Array.Empty<string?>())
                    .Where(s => !string.IsNullOrWhiteSpace(s) && s!.Length >= 8).Select(s => s!).ToArray();
                return new CodingModelCallTrace(directory, stem, secretValues);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException or System.Security.SecurityException)
            {
                return null;
            }
        }

        public string RequestPath => Path.Combine(_directory, _stem + ".request.json");
        public string ResponsePath => Path.Combine(_directory, _stem + ".response.json");

        public void WriteRequest(string endpointPath, string requestBody)
        {
            _endpoint = endpointPath ?? string.Empty;
            // The body is already JSON; embed it verbatim so the file is exactly what was sent.
            string body = Redact(requestBody ?? string.Empty);
            bool truncated = body.Length > MaxFileCharacters;
            if (truncated) body = body.Substring(0, MaxFileCharacters);
            Write(RequestPath, "{\"endpoint\":" + JsonSerializer.Serialize(_endpoint)
                + ",\"truncated\":" + (truncated ? "true" : "false")
                + ",\"bodyText\":" + JsonSerializer.Serialize(body) + "}");
        }

        public void SetStatus(int statusCode) => _status = statusCode;

        public void SetError(string message) => _error = Redact(message ?? string.Empty);

        /// <summary>Records one SSE "data:" payload exactly as received and folds it into the summary.</summary>
        public void ObserveSseData(string data)
        {
            if (data == null) return;
            if (_raw.Count < MaxRawLines) _raw.Add(Redact(data));
            else _rawTruncated = true;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(data);
                JsonElement root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
                    _usage = usage.GetRawText();
                if (!root.TryGetProperty("choices", out JsonElement choices)
                    || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0) return;
                JsonElement first = choices[0];
                if (first.TryGetProperty("delta", out JsonElement delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    Append(_content, delta, "content");
                    Append(_reasoning, delta, "reasoning_content");
                }
                if (first.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.Object)
                {
                    Append(_content, message, "content");
                    Append(_reasoning, message, "reasoning_content");
                }
                if (first.TryGetProperty("finish_reason", out JsonElement finish) && finish.ValueKind == JsonValueKind.String)
                    _finishReason = finish.GetString();
            }
            catch (JsonException) { }
        }

        /// <summary>Writes the response file. Safe to call more than once; only the first call writes.</summary>
        public void Complete()
        {
            if (_completed) return;
            _completed = true;
            string content = Cap(Redact(_content.ToString()));
            string reasoning = Cap(Redact(_reasoning.ToString()));
            var sb = new StringBuilder();
            sb.Append("{\"status\":").Append(_status.HasValue ? _status.Value.ToString() : "null");
            sb.Append(",\"finish_reason\":").Append(JsonSerializer.Serialize(_finishReason));
            sb.Append(",\"usage\":").Append(_usage ?? "null");
            sb.Append(",\"error\":").Append(JsonSerializer.Serialize(_error));
            sb.Append(",\"content\":").Append(JsonSerializer.Serialize(content));
            sb.Append(",\"reasoning_content\":").Append(JsonSerializer.Serialize(reasoning));
            sb.Append(",\"rawLinesTruncated\":").Append(_rawTruncated ? "true" : "false");
            sb.Append(",\"rawLines\":[");
            int budget = MaxFileCharacters;
            bool first = true;
            foreach (string line in _raw)
            {
                string encoded = JsonSerializer.Serialize(line);
                if (encoded.Length > budget) { sb.Append(first ? string.Empty : ",").Append("\"[trace size limit reached]\""); break; }
                budget -= encoded.Length;
                sb.Append(first ? string.Empty : ",").Append(encoded);
                first = false;
            }
            sb.Append("]}");
            Write(ResponsePath, sb.ToString());
        }

        private static void Append(StringBuilder target, JsonElement node, string name)
        {
            if (target.Length < MaxFileCharacters * 2
                && node.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                target.Append(value.GetString());
        }

        private static string Cap(string value) =>
            value.Length <= MaxFileCharacters ? value : value.Substring(0, MaxFileCharacters) + "[trace size limit reached]";

        internal string Redact(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            foreach (string secret in _secrets)
                text = text.Replace(secret, "[redacted]", StringComparison.OrdinalIgnoreCase);
            text = Bearer.Replace(text, "$1[redacted]");
            return TokenHeader.Replace(text, "$1[redacted]");
        }

        private static void Write(string path, string text)
        {
            try { File.WriteAllText(path, text, new UTF8Encoding(false)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { }
        }
    }
}
