using PaRuX.Contracts;
using PaRuX.Server.Orchestration.Gaming;

namespace PaRuX.Server.Orchestration.Gaming.Emulators;

/// <summary>
/// Resolves only a verified emulator entry's *existing* command-line fullscreen
/// intent. Never edits emulator settings or injects new command-line flags.
/// An Alt+Enter toggle after a native fullscreen flag could undo fullscreen.
/// </summary>
internal static class EmulatorPostLaunchPolicy
{
    internal sealed record Decision(
        GamePostLaunchSettings Actions,
        bool NativeFullscreenRequested);

    internal static Decision Resolve(ManualGameDto game, GamePostLaunchSettings actions)
    {
        bool native = HasNativeFullscreenRequest(game);
        // This is a copy. Do not mutate the stored per-game profile, including
        // when an emulator's command line or version changes at a later launch.
        return new(new GamePostLaunchSettings
        {
            BringToForeground = actions.BringToForeground,
            RequestAltEnterFullscreen = actions.RequestAltEnterFullscreen && !native,
            ClickClientCenter = actions.ClickClientCenter,
            DelayMs = actions.DelayMs
        }, native);
    }

    internal static bool HasNativeFullscreenRequest(ManualGameDto game)
    {
        if (string.IsNullOrWhiteSpace(game.EmulatorId) || game.Kind != ManualEntryKinds.Game)
            return false;
        EmulatorDefinition? definition = EmulatorRegistry.Find(game.EmulatorId);
        if (definition?.FullscreenArguments is not { Count: > 0 } fullscreen)
            return false;
        if (!definition.ExecutableNames.Contains(Path.GetFileName(game.ExecutablePath),
                StringComparer.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(game.GameFilePath) ||
            !Path.IsPathFullyQualified(game.GameFilePath))
            return false;

        // The importer produced this argument sequence. Detect whole argument
        // tokens in-order, not substrings: "-fullscreen=false" != "-fullscreen".
        // A user-edited command line is never rewritten or "corrected" here.
        IReadOnlyList<string> arguments = GameLaunchProfileApplier.SplitCommandLine(game.Arguments ?? "");
        if (!arguments.Contains(game.GameFilePath, StringComparer.OrdinalIgnoreCase) &&
            !(definition.Id == "mame" && arguments.Contains(
                Path.GetFileNameWithoutExtension(game.GameFilePath), StringComparer.OrdinalIgnoreCase)))
            return false;
        for (int start = 0; start + fullscreen.Count <= arguments.Count; start++)
        {
            bool match = true;
            for (int j = 0; j < fullscreen.Count; j++)
                if (!string.Equals(arguments[start + j], fullscreen[j],
                    StringComparison.OrdinalIgnoreCase))
                { match = false; break; }
            if (match) return true;
        }
        return false;
    }
}
