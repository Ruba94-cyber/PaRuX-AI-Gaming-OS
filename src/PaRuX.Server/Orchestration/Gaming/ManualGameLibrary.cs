using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Linq;
using System.Text.Json;
using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming;

/// <summary>A local list of user-entered games. Entries are never executed or scanned here.</summary>
public sealed class ManualGameLibrary
{
    private sealed record LibraryFile(int Version, List<ManualGameDto> Games);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly string _path;

    internal static string DefaultPath => Path.Combine(PaRuXDataRoot.Path, "server", "manual-games.json");
    public ManualGameLibrary() : this(DefaultPath) { }
    public ManualGameLibrary(string path) => _path = Path.GetFullPath(path);

    public IReadOnlyList<ManualGameDto> List()
    {
        lock (_gate) return Read().ToArray();
    }

    public ManualGameDto Add(AddManualGameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string kind = string.IsNullOrWhiteSpace(request.Kind) ? ManualEntryKinds.Game : request.Kind.Trim().ToLowerInvariant();
        if (!ManualEntryKinds.IsValid(kind))
            throw new ArgumentException("The entry type must be 'game' or 'application'.");
        var v = ValidateFields(request.Name, request.ExecutablePath, request.Arguments, request.WorkingDirectory,
            request.Platform, request.EmulatorId);
        // Only newly imported emulator titles carry optional typed game-file
        // metadata. Legacy library entries stay readable without migration.
        string gameFilePath = request.GameFilePath?.Trim() ?? "";
        if (gameFilePath.Length > 0 &&
            !ManualGameSessionService.IsSafeEmulatorGameFile(v.EmulatorId, gameFilePath))
            throw new ArgumentException("The emulator game file must be a supported regular local file.");
        lock (_gate)
        {
            // Prevent lost updates across independent PaRuX Core instances.
            using FileStream lease = AcquireWriteLease();
            List<ManualGameDto> games = Read();
            if (games.Count >= 10000) throw new InvalidOperationException("Library limit reached.");
            if (IsDuplicate(games, null, v.Path, v.Arguments, kind, v.WorkingDirectory))
                throw new InvalidOperationException("This executable and launch configuration are already in the library.");
            var game = new ManualGameDto(Guid.NewGuid().ToString("N"), v.Name, v.Path, v.Arguments, v.WorkingDirectory, kind, v.Platform, v.EmulatorId, gameFilePath);
            games.Add(game);
            Write(games);
            return game;
        }
    }

    /// <summary>
    /// Edits name, executable, arguments, working directory, emulator and platform of a saved entry using the
    /// exact validation of <see cref="Add"/>. The id, kind and imported game file are kept; nothing on disk
    /// other than the library file is touched.
    /// </summary>
    /// <returns>The updated entry, or null when the id does not exist.</returns>
    /// <exception cref="ArgumentException">Invalid input (nothing is changed).</exception>
    /// <exception cref="InvalidOperationException">The new launch configuration duplicates another entry.</exception>
    public ManualGameDto? Update(string id, UpdateManualGameRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(id) || id.Length > 100)
            throw new ArgumentException("Invalid entry id.");
        var v = ValidateFields(request.Name, request.ExecutablePath, request.Arguments, request.WorkingDirectory,
            request.Platform, request.EmulatorId);
        lock (_gate)
        {
            using FileStream lease = AcquireWriteLease();
            List<ManualGameDto> games = Read();
            int index = games.FindIndex(g => string.Equals(g.Id, id, StringComparison.Ordinal));
            if (index < 0) return null;
            ManualGameDto current = games[index];
            string kind = string.IsNullOrWhiteSpace(current.Kind) ? ManualEntryKinds.Game : current.Kind;
            // An imported emulator game file must still belong to the (possibly changed) emulator.
            string gameFile = current.GameFilePath ?? "";
            if (gameFile.Length > 0 && !ManualGameSessionService.IsSafeEmulatorGameFile(v.EmulatorId, gameFile))
                throw new ArgumentException("The imported game file does not match the chosen emulator.");
            if (IsDuplicate(games, id, v.Path, v.Arguments, kind, v.WorkingDirectory))
                throw new InvalidOperationException("This executable and launch configuration are already in the library.");
            var updated = new ManualGameDto(current.Id, v.Name, v.Path, v.Arguments, v.WorkingDirectory, kind, v.Platform, v.EmulatorId, gameFile);
            games[index] = updated;
            Write(games);
            return updated;
        }
    }

    private sealed record ValidatedFields(string Name, string Path, string Arguments, string WorkingDirectory, string Platform, string EmulatorId);

    private static ValidatedFields ValidateFields(string? rawName, string? rawPath, string? rawArguments,
        string? rawWorkingDirectory, string? rawPlatform, string? rawEmulatorId)
    {
        string name = rawName?.Trim() ?? "";
        string path = rawPath?.Trim() ?? "";
        if (name.Length is < 1 or > 200 || path.Length is < 1 or > 1024 ||
            !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Enter a name and an absolute local executable path.");
        path = Path.GetFullPath(path);
        string arguments = rawArguments?.Trim() ?? "";
        string workingDirectory = rawWorkingDirectory?.Trim() ?? "";
        string platform = rawPlatform?.Trim() ?? "";
        string emulatorId = rawEmulatorId?.Trim().ToLowerInvariant() ?? "";
        if (platform.Length > 80 || platform.Any(char.IsControl))
            throw new ArgumentException("The platform name is too long or invalid.");
        if (emulatorId.Length > 0 && Emulators.EmulatorRegistry.Find(emulatorId) is null)
            throw new ArgumentException("Unknown emulator.");
        if (!ValidArguments(arguments))
            throw new ArgumentException("Arguments must be at most 1024 characters without control characters.");
        if (workingDirectory.Length > 0)
        {
            if (workingDirectory.Length > 1024 || !Path.IsPathFullyQualified(workingDirectory) ||
                workingDirectory.StartsWith("\\\\", StringComparison.Ordinal))
                throw new ArgumentException("The working directory must be an absolute local folder.");
            workingDirectory = Path.GetFullPath(workingDirectory);
            if (!IsSafeDirectory(workingDirectory))
                throw new ArgumentException("The working directory does not exist or is a linked folder.");
        }
        return new ValidatedFields(name, path, arguments, workingDirectory, platform, emulatorId);
    }

    // A launch identity includes the effective working directory and entry kind:
    // a portable game may legitimately use the same binary/args from different
    // isolated game directories, or offer distinct application/game entries.
    // Treat an omitted working directory as the executable's folder for compatibility.
    private static bool IsDuplicate(List<ManualGameDto> games, string? ignoreId, string path, string arguments, string kind, string workingDirectory)
    {
        string effectiveFolder = workingDirectory.Length == 0 ? Path.GetDirectoryName(path)! : workingDirectory;
        return games.Any(g => !string.Equals(g.Id, ignoreId, StringComparison.Ordinal) &&
                           string.Equals(g.ExecutablePath, path, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(g.Arguments ?? "", arguments, StringComparison.Ordinal) &&
                           string.Equals(string.IsNullOrWhiteSpace(g.Kind) ? ManualEntryKinds.Game : g.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
                           string.Equals(string.IsNullOrWhiteSpace(g.WorkingDirectory)
                               ? Path.GetDirectoryName(g.ExecutablePath)
                               : g.WorkingDirectory,
                               effectiveFolder, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool ValidArguments(string arguments) =>
        arguments.Length <= 1024 && !arguments.Any(char.IsControl);

    /// <summary>An existing local folder with no linked (junction/symlink) component.</summary>
    internal static bool IsSafeDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return false;
            for (string? dir = Path.GetFullPath(path); dir != null; dir = Path.GetDirectoryName(dir))
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Removes only the library entry. The executable, game files and folders are never touched.</summary>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            using FileStream lease = AcquireWriteLease();
            List<ManualGameDto> games = Read();
            int index = games.FindIndex(g => string.Equals(g.Id, id, StringComparison.Ordinal));
            if (index < 0) return false;
            games.RemoveAt(index);
            Write(games);
            return true;
        }
    }

    /// <summary>
    /// Cross-process lease around the read-modify-replace transaction. Keep the tiny
    /// .lock file: deleting it during another writer's wait could split the lock identity.
    /// The OS releases the handle automatically on process termination.
    /// </summary>
    private FileStream AcquireWriteLease()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string lockPath = _path + ".lock";
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                Thread.Sleep(30); // bounded contention; never a global or infinite wait
            }
        }
    }

    private List<ManualGameDto> Read()
    {
        if (!File.Exists(_path)) return new();
        LibraryFile? file = JsonSerializer.Deserialize<LibraryFile>(File.ReadAllText(_path), JsonOptions);
        if (file is null || file.Version != 1 || file.Games is null || file.Games.Count > 10000 ||
            file.Games.Any(g => g is null || string.IsNullOrWhiteSpace(g.Id) || string.IsNullOrWhiteSpace(g.Name) || string.IsNullOrWhiteSpace(g.ExecutablePath)))
            throw new InvalidDataException("Manual game library has an unsupported or invalid format.");
        return file.Games;
    }

    private void Write(List<ManualGameDto> games)
    {
        string directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        string temp = Path.Combine(directory, ".manual-games-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new LibraryFile(1, games), JsonOptions);
                stream.Flush(true);
            }
            File.Move(temp, _path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
