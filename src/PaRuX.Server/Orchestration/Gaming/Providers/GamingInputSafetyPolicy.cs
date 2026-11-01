using PaRuX.Contracts;

namespace PaRuX.Server.Orchestration.Gaming.Providers;

/// <summary>
/// Fail-closed admission of potentially duplicating input backends.
/// We never auto-install drivers, create a virtual controller, inject buttons,
/// or run a second local mapper alongside a remote streaming input backend.
/// </summary>
internal static class GamingInputSafetyPolicy
{
    internal static string? Validate(GamingProfileDto profile)
    {
        string session = profile.SessionType?.Trim() ?? "";
        string provider = profile.InputProvider?.Trim() ?? "";

        bool remote = string.Equals(session, "remote", StringComparison.OrdinalIgnoreCase);
        bool local = string.Equals(session, "local", StringComparison.OrdinalIgnoreCase);
        if (!remote && !local)
            return "Gaming session type must be local or remote.";

        // Sunshine can provide its own virtual remote controllers. Without
        // confirmed per-device ownership and deduplication, local mapper helpers
        // must not be activated on the same remote session.
        if (remote && (string.Equals(provider, "xoutput", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(provider, "custom", StringComparison.OrdinalIgnoreCase)))
            return "Remote sessions cannot start local virtual input helpers without verified deduplication. Choose Sunshine or native input.";

        if (local && string.Equals(provider, "sunshine", StringComparison.OrdinalIgnoreCase))
            return "Sunshine virtual input is only available in remote sessions. Choose native input locally.";

        return null;
    }
}
