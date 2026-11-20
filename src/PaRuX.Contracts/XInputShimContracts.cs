namespace PaRuX.Contracts;

/// <summary>One game folder the PaRuX XInput shim was put into, with the hash of what was written.</summary>
public sealed record XInputShimInstall(string Folder, string Architecture, string[] Files, string Sha256, DateTime InstalledUtc);

public sealed record XInputShimResult(bool Ok, string Message, XInputShimInstall? Install = null);

public sealed record XInputShimStatus(bool Available, IReadOnlyList<XInputShimInstall> Installs);

public sealed class XInputShimFolderRequest
{
    public string Folder { get; set; } = string.Empty;
}
