using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using PaRuX.Contracts;

namespace PaRuX.Server.Knowledge;

public enum SensitiveVerdict
{
    /// <summary>Nothing sensitive recognised.</summary>
    Allowed,
    /// <summary>Broad or risky location: allowed only after the person explicitly confirms.</summary>
    NeedsConsent,
    /// <summary>Credential stores, key material, browser profiles, wallets, system and PaRuX's own data: never indexed.</summary>
    Blocked
}

public sealed record SensitiveFinding(SensitiveVerdict Verdict, string? Reason);

/// <summary>
/// Decides whether a folder (or a file met while walking one) may be indexed. The index is plain searchable text on disk, so
/// credential stores must never enter it, and broad locations (a whole profile, a drive root) need an explicit yes.
/// Matching is on path segments and file names only; no file content is read.
/// </summary>
public static class SensitivePathPolicy
{
    private static readonly string[] BlockedFolderNames =
    {
        ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".docker", ".config\\gcloud", "gcloud", ".password-store", ".terraform.d",
        "Credentials", "Protect", "Vault", "CryptnetUrlCache", "SystemCertificates",
        "wallets", "wallet", ".bitcoin", ".ethereum", ".electrum", "Exodus", "Electrum", "Ethereum", "Monero", "Zcash", "MetaMask",
        "1Password", "Bitwarden", "KeePass", "LastPass", "Dashlane", "NordPass",
        "Google\\Chrome", "Microsoft\\Edge", "BraveSoftware", "Chromium", "Opera Software", "Vivaldi", "Mozilla\\Firefox", "Mozilla\\Profiles",
        "User Data", "Local Storage", "IndexedDB", "Network\\Cookies"
    };

    private static readonly string[] BlockedFileNames =
    {
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", "known_hosts", "authorized_keys", ".git-credentials", ".netrc", "_netrc", ".npmrc", ".pypirc",
        "credentials", "credentials.json", "secrets.json", "token.json", "wallet.dat", "Login Data", "Cookies", "Web Data", "key3.db", "key4.db",
        "logins.json", "cert9.db", "signons.sqlite", "SAM", "SECURITY", "SYSTEM", "NTUSER.DAT", "ntds.dit"
    };

    private static readonly string[] BlockedExtensions =
    {
        ".kdbx", ".kdb", ".key", ".pem", ".pfx", ".p12", ".jks", ".keystore", ".ppk", ".asc", ".gpg", ".pgp", ".wallet", ".seed", ".opvault", ".agilekeychain", ".rdp"
    };

    private static string Norm(string path) => path.Replace('/', '\\').TrimEnd('\\');

    private static bool ContainsSegmentSequence(string normalised, string folderPattern)
    {
        string haystack = "\\" + normalised + "\\";
        string needle = "\\" + folderPattern.Trim('\\') + "\\";
        return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verdict for a folder the person wants to add.</summary>
    public static SensitiveFinding EvaluateFolder(string folder, string? dataRoot = null)
    {
        if (string.IsNullOrWhiteSpace(folder)) return new(SensitiveVerdict.Blocked, "No folder given.");
        string full;
        try { full = Norm(Path.GetFullPath(folder)); } catch (Exception) { return new(SensitiveVerdict.Blocked, "The path is not valid."); }

        string root = Norm(dataRoot ?? PaRuXDataRoot.Path);
        if (IsSameOrInside(full, root))
            return new(SensitiveVerdict.Blocked, "This is PaRuX's own data folder (settings, keys and the search index).");   // a parent is fine: the walk skips this folder

        foreach (string special in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.GetFolderPath(Environment.SpecialFolder.System),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(special)) continue;
            if (IsSameOrInside(full, Norm(special)))
                return new(SensitiveVerdict.Blocked, "System and program folders are not indexed.");
        }

        foreach (string pattern in BlockedFolderNames)
            if (ContainsSegmentSequence(full, pattern))
                return new(SensitiveVerdict.Blocked, "This folder holds credentials, keys, wallets, password stores or a browser profile (" + Last(pattern) + ").");

        string leaf = Path.GetFileName(full);
        if (string.IsNullOrEmpty(leaf) || full.Length <= 3)
            return new(SensitiveVerdict.NeedsConsent, "This is a whole drive. Indexing everything on it is rarely what you want.");

        string profile = Norm(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (profile.Length > 3 && full.Equals(profile, StringComparison.OrdinalIgnoreCase))
            return new(SensitiveVerdict.NeedsConsent, "This is your whole user profile; it contains app data, downloads and private files.");
        string appData = Norm(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        string localAppData = Norm(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        foreach (string app in new[] { appData, localAppData })
            if (app.Length > 3 && IsSameOrInside(full, app))
                return new(SensitiveVerdict.NeedsConsent, "Application data folders often hold tokens, caches and private data.");
        // A parent of a credential store would walk into it; the walk skips it, but the person should know.
        return new(SensitiveVerdict.Allowed, null);
    }

    /// <summary>True when a path below an allowed folder must be skipped (credential folder, key file, password database).</summary>
    public static SensitiveFinding EvaluateEntry(string relativeOrFullPath, bool isDirectory)
    {
        string p = Norm(relativeOrFullPath);
        string name = Path.GetFileName(p);
        if (isDirectory)
        {
            foreach (string pattern in BlockedFolderNames)
                if (!pattern.Contains('\\') ? name.Equals(pattern, StringComparison.OrdinalIgnoreCase) : ContainsSegmentSequence(p, pattern))
                    return new(SensitiveVerdict.Blocked, "Skipped: credential, wallet or browser-profile folder (" + Last(pattern) + ").");
            return new(SensitiveVerdict.Allowed, null);
        }
        if (BlockedFileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            return new(SensitiveVerdict.Blocked, "Skipped: credential or key file.");
        if (BlockedExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            return new(SensitiveVerdict.Blocked, "Skipped: key, certificate or password-database file.");
        if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
            return new(SensitiveVerdict.Blocked, "Skipped: environment file that usually holds secrets.");
        return new(SensitiveVerdict.Allowed, null);
    }

    private static string Last(string pattern) => pattern.Contains('\\') ? pattern[(pattern.LastIndexOf('\\') + 1)..] : pattern;

    private static bool IsSameOrInside(string path, string container) =>
        path.Equals(container, StringComparison.OrdinalIgnoreCase) || path.StartsWith(container + "\\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Restricts index/database files to the current user. Windows: the directory or file keeps only the current user and SYSTEM
/// (inheritance removed); elsewhere: owner-only mode. Best effort and reported, never fatal.
/// </summary>
public static class LocalFileProtection
{
    public static bool RestrictToCurrentUser(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                SecurityIdentifier me = WindowsIdentity.GetCurrent().User!;
                var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
                if (Directory.Exists(path))
                {
                    var info = new DirectoryInfo(path);
                    var sec = new DirectorySecurity();
                    sec.SetAccessRuleProtection(true, false);
                    foreach (SecurityIdentifier sid in new[] { me, system })
                        sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                    info.SetAccessControl(sec);
                    return true;
                }
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    var sec = new FileSecurity();
                    sec.SetAccessRuleProtection(true, false);
                    foreach (SecurityIdentifier sid in new[] { me, system })
                        sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
                    info.SetAccessControl(sec);
                    return true;
                }
                return false;
            }
            if (Directory.Exists(path)) { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); return true; }
            if (File.Exists(path)) { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); return true; }
            return false;
        }
        catch (Exception) { return false; }
    }

    /// <summary>True when only the current user and SYSTEM (and no broad group) can access the path on Windows.</summary>
    public static bool IsRestricted(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows()) return true;
            AuthorizationRuleCollection rules = (Directory.Exists(path)
                ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl()
                : new FileInfo(path).GetAccessControl()).GetAccessRules(true, true, typeof(SecurityIdentifier));
            SecurityIdentifier me = WindowsIdentity.GetCurrent().User!;
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                var sid = (SecurityIdentifier)rule.IdentityReference;
                if (sid.Equals(me) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)) continue;
                return false;
            }
            return true;
        }
        catch (Exception) { return false; }
    }
}

/// <summary>One file of a pinned model download. Exactly one of the digests is set.</summary>
public sealed record PinnedFile(string RemotePath, string LocalName, long Size, string? Sha256 = null, string? GitBlobSha1 = null);

/// <summary>A model repository pinned to an immutable revision with a digest per file.</summary>
public sealed record PinnedModelManifest(string Repository, string Revision, IReadOnlyList<PinnedFile> Files)
{
    public string UrlFor(PinnedFile file) => "https://huggingface.co/" + Repository + "/resolve/" + Revision + "/" + file.RemotePath;

    /// <summary>sentence-transformers/all-MiniLM-L6-v2 at a fixed commit; digests read from the Hugging Face file listing (LFS SHA-256, git blob SHA-1 for small files).</summary>
    public static PinnedModelManifest MiniLmL6V2 { get; } = new("sentence-transformers/all-MiniLM-L6-v2", "1110a243fdf4706b3f48f1d95db1a4f5529b4d41", new[]
    {
        new PinnedFile("onnx/model.onnx", "model.onnx", 90405214, Sha256: "6fd5d72fe4589f189f8ebc006442dbb529bb7ce38f8082112682524616046452"),
        new PinnedFile("vocab.txt", "vocab.txt", 231508, GitBlobSha1: "fb140275c155a9c7c5a3b3e0e77a9e839594a938"),
        new PinnedFile("tokenizer_config.json", "tokenizer_config.json", 350, GitBlobSha1: "c79f2b6a0cea6f4b564fed1938984bace9d30ff0"),
        new PinnedFile("config.json", "config.json", 612, GitBlobSha1: "72b987fd805cfa2b58c4c8c952b274a11bfd5a00")
    });
}

public sealed class ModelIntegrityException : InvalidOperationException
{
    public ModelIntegrityException(string message) : base(message) { }
}

/// <summary>
/// Downloads model files only when the network is allowed, into a partial file that is hashed before it is moved into place.
/// A file that does not match its pinned digest is deleted and never becomes visible to the loader.
/// </summary>
public sealed class PinnedModelDownloader
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpMessageHandler? _handler;
    private readonly KnowledgeSettingsStore? _settings;

    public PinnedModelDownloader(HttpMessageHandler? handler = null, KnowledgeSettingsStore? settings = null) { _handler = handler; _settings = settings; }

    public async Task DownloadAsync(PinnedModelManifest manifest, string targetDirectory, IProgress<string>? progress, CancellationToken ct)
    {
        OfflinePolicy.ThrowIfBlocked("the model download", _settings);   // before any request is created
        Directory.CreateDirectory(targetDirectory);
        using HttpClient http = _handler == null ? new HttpClient { Timeout = TimeSpan.FromMinutes(15) } : new HttpClient(_handler, false) { Timeout = TimeSpan.FromMinutes(15) };
        foreach (PinnedFile file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            string destination = Path.Combine(targetDirectory, file.LocalName);
            if (File.Exists(destination) && await VerifyAsync(destination, file, ct).ConfigureAwait(false)) continue;   // already good
            progress?.Report("Downloading " + file.LocalName + "...");
            string partial = destination + ".partial";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, manifest.UrlFor(file));
                using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var target = new FileStream(partial, FileMode.Create, FileAccess.Write))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    while (true)
                    {
                        using var guard = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        guard.CancelAfter(StallTimeout);
                        int read;
                        try { read = await source.ReadAsync(buffer, guard.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Download stalled: no data received for " + StallTimeout.TotalSeconds + "s."); }
                        if (read == 0) break;
                        total += read;
                        if (total > file.Size + 1024) throw new ModelIntegrityException(file.LocalName + " is larger than the pinned size; download refused.");
                        await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    }
                }
                progress?.Report("Verifying " + file.LocalName + "...");
                if (!await VerifyAsync(partial, file, ct).ConfigureAwait(false))
                    throw new ModelIntegrityException(file.LocalName + " does not match its pinned SHA-256/digest; it was discarded.");
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(partial, destination);
            }
            finally { try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { } }
        }
    }

    /// <summary>True when the file at <paramref name="path"/> has the pinned size and digest.</summary>
    public static async Task<bool> VerifyAsync(string path, PinnedFile file, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != file.Size) return false;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (file.Sha256 != null)
                return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase);
            if (file.GitBlobSha1 != null)
            {
                using var sha1 = SHA1.Create();   // git's content address; used only as a pin, with the size check above
                byte[] header = Encoding.ASCII.GetBytes("blob " + info.Length + "\0");
                sha1.TransformBlock(header, 0, header.Length, null, 0);
                var buffer = new byte[81920];
                int n;
                while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0) sha1.TransformBlock(buffer, 0, n, null, 0);
                sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return Convert.ToHexString(sha1.Hash!).Equals(file.GitBlobSha1, StringComparison.OrdinalIgnoreCase);
            }
            return false;   // an unpinned file is never accepted
        }
        catch (IOException) { return false; }
    }
}
