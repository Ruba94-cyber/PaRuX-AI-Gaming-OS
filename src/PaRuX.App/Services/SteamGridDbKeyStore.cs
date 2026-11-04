using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PaRuX.Services
{
    /// <summary>
    /// The optional, user-provided SteamGridDB API key. It is never shipped, never logged and never written to settings.json:
    /// it is encrypted with Windows DPAPI (current user, purpose-bound entropy) into a file under the PaRuX data folder,
    /// the same mechanism the integrations gateway key uses. The key is never handed back to the UI, only "is one stored".
    /// </summary>
    public static class SteamGridDbKeyStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PaRuX.GameMetadata.SteamGridDbApiKey.v1");
        private static readonly object Gate = new();

        private static string KeyPath => Path.Combine(AppPaths.PaRuXRoot, "secrets", "steamgriddb.key");

        public static bool HasKey => TryGet() is not null;

        /// <summary>Normalises pasted input: trimmed, a visible "Bearer " prefix dropped. Null when it cannot be an API key.</summary>
        public static string? Clean(string? input)
        {
            string key = (input ?? string.Empty).Trim();
            if (key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) key = key[7..].Trim();
            if (key.Length is < 8 or > 128) return null;
            foreach (char c in key)
                if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_') return null;
            return key;
        }

        /// <summary>The stored key, or null when none is stored or it cannot be decrypted (another Windows account or PC).</summary>
        public static string? TryGet()
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                lock (Gate)
                {
                    if (!File.Exists(KeyPath)) return null;
                    byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(File.ReadAllText(KeyPath)), Entropy, DataProtectionScope.CurrentUser);
                    return Clean(Encoding.UTF8.GetString(clear));
                }
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or IOException or UnauthorizedAccessException) { return null; }
        }

        /// <summary>Encrypts and stores the key atomically. Returns false for an invalid key or when it could not be written.</summary>
        public static bool Save(string? input)
        {
            string? key = Clean(input);
            if (key is null || !OperatingSystem.IsWindows()) return false;
            try
            {
                string protectedText = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
                    string temp = KeyPath + ".tmp";
                    File.WriteAllText(temp, protectedText, new UTF8Encoding(false));
                    File.Move(temp, KeyPath, overwrite: true);
                }
                return true;
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException) { return false; }
        }

        public static bool Remove()
        {
            try
            {
                lock (Gate) { if (File.Exists(KeyPath)) File.Delete(KeyPath); }
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }
}
