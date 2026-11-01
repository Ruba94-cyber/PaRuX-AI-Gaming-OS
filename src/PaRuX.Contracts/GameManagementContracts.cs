namespace PaRuX.Contracts;

// Wire shapes for "Advanced Game Management": folder import and the emulator manager.
// They mirror the Core's results by property name so the desktop client does not reference Core types.

public sealed record GameFolderInspectRequest(string Folder);

public sealed record ExecutableCandidateDto(string RelativePath, long SizeBytes, int Score, string Reason);

public sealed record GameFolderInspectionDto(
    string FolderPath, string SuggestedTitle, List<ExecutableCandidateDto> Candidates, bool Truncated, string? Error);

/// <summary>Emulator display request. Default honors the emulator's own saved configuration.</summary>
public static class EmulatorLaunchModes
{
    public const string Default = "default";
    public const string Fullscreen = "fullscreen";
    public const string Windowed = "windowed";
}

public sealed record EmulatorDescriptorDto(
    string Id, string Name, string Platform, List<string> Extensions, bool RequiresCore, string TemplateStatus,
    bool FullscreenSupported, bool WindowedSupported = false);

public sealed record DetectedEmulatorDto(string EmulatorId, string ExecutablePath);

public sealed record EmulatorListDto(List<EmulatorDescriptorDto> Emulators, List<DetectedEmulatorDto> Detected);

public sealed record EmulatorScanRequestDto(string EmulatorId, string Folder);

public sealed record EmulatedGameDto(string Path, string Title, long SizeBytes);

public sealed record EmulatorScanDto(List<EmulatedGameDto> Games, bool Truncated, string? Error);

public sealed record EmulatorImportRequestDto(
    string EmulatorId, string ExecutablePath, List<string> GamePaths, bool Fullscreen, string? ExtraArguments,
    string? CorePath, string? LaunchMode = null, string? InputProfile = null);

public sealed record EmulatorImportResultDto(int Added, int Skipped, List<string> Problems);
