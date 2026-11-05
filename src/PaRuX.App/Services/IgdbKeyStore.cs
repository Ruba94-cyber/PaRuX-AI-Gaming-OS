using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using PaRuX.Contracts;

namespace PaRuX.Services
{
    /// <summary>
    /// The optional, user-provided IGDB credentials (the Client ID and Client Secret of the user's own free Twitch application).
    /// Never shipped, never logged, never written to settings.json: encrypted with Windows DPAPI (current user, purpose-bound
    /// entropy) like <see cref="SteamGridDbKeyStore"/>. The secret is never handed back to the UI, only "is one stored".
    /// </summary>
    public static class IgdbKeyStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PaRuX.GameMetadata.IgdbCredentials.v1");
        private static readonly object Gate = new();

        private static string KeyPath => Path.Combine(AppPaths.PaRuXRoot, "secrets", "igdb.key");

        public static bool HasKey => TryGet() is not null;

        /// <summary>The stored pair, or null when none is stored or it cannot be decrypted (another Windows account or PC).</summary>
        public static (string ClientId, string ClientSecret)? TryGet()
        {
            if (!OperatingSystem.IsWindows()) return null;
            try
            {
                lock (Gate)
                {
                    if (!File.Exists(KeyPath)) return null;
                    byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(File.ReadAllText(KeyPath)), Entropy, DataProtectionScope.CurrentUser);
                    return GameMetadataParsing.ParseIgdbCredentials(Encoding.UTF8.GetString(clear));
                }
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or IOException or UnauthorizedAccessException) { return null; }
        }

        /// <summary>Validates, encrypts and stores atomically. False for an invalid pair or when it could not be written.</summary>
        public static bool Save(string? input)
        {
            if (GameMetadataParsing.ParseIgdbCredentials(input) is not { } pair || !OperatingSystem.IsWindows()) return false;
            try
            {
                string protectedText = Convert.ToBase64String(ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(pair.ClientId + ":" + pair.ClientSecret), Entropy, DataProtectionScope.CurrentUser));
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
