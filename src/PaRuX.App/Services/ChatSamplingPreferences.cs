using System;
using System.IO;
using System.Text.Json;

namespace PaRuX.Services
{
    /// <summary>
    /// The owner's optional generation settings for chat (creativity, nucleus sampling, reply length, repeatable seed).
    /// A null value means "let PaRuX decide", which is the default and keeps the existing behaviour byte for byte.
    /// </summary>
    public sealed record ChatSamplingValues(double? Temperature, double? TopP, int? MaxTokens, long? Seed)
    {
        public static ChatSamplingValues Default { get; } = new(null, null, null, null);
        public bool IsDefault => Temperature is null && TopP is null && MaxTokens is null && Seed is null;
    }

    /// <summary>Clamps values to what Core accepts (temperature 0-2, top_p 0-1, tokens 1-131072) so the request is never refused for them.</summary>
    public static class ChatSamplingRules
    {
        public const double MaxTemperature = 2.0;
        public const int MaxTokensCeiling = 131072;

        public static ChatSamplingValues Normalize(ChatSamplingValues? values)
        {
            if (values is null) return ChatSamplingValues.Default;
            return new ChatSamplingValues(
                values.Temperature is { } t && double.IsFinite(t) ? Math.Round(Math.Clamp(t, 0, MaxTemperature), 2) : null,
                values.TopP is { } p && double.IsFinite(p) ? Math.Round(Math.Clamp(p, 0, 1), 2) : null,
                values.MaxTokens is { } m ? Math.Clamp(m, 1, MaxTokensCeiling) : null,
                values.Seed is { } s && s >= 0 ? s : null);
        }
    }

    /// <summary>Persists <see cref="ChatSamplingValues"/> as a small JSON file. A missing or damaged file means defaults; nothing throws.</summary>
    public sealed class ChatSamplingStore
    {
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
        private readonly string _path;
        private ChatSamplingValues? _cached;

        public ChatSamplingStore(string path) => _path = path;

        public ChatSamplingValues Load()
        {
            if (_cached is not null) return _cached;
            try
            {
                if (File.Exists(_path))
                    return _cached = ChatSamplingRules.Normalize(JsonSerializer.Deserialize<ChatSamplingValues>(File.ReadAllText(_path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { }
            return _cached = ChatSamplingValues.Default;
        }

        public ChatSamplingValues Save(ChatSamplingValues values)
        {
            ChatSamplingValues clean = ChatSamplingRules.Normalize(values);
            _cached = clean;
            try
            {
                if (clean.IsDefault) { if (File.Exists(_path)) File.Delete(_path); return clean; }
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(clean, Json));
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return clean;
        }
    }
}
