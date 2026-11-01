namespace PaRuX.Contracts;

/// <summary>
/// First-party, dependency-free controller navigation policy for PaRuX Console/TV.
/// Consumes only key events already delivered by Windows/WinUI; does not poll
/// hardware, inject game input, allocate virtual controllers or start a worker.
/// </summary>
public enum ConsoleNavigationAction
{
    None,
    Exit,
    Up,
    Down,
    Left,
    Right
}

public static class ConsoleNavigationPolicy
{
    // VirtualKey codes are the Windows SDK's stable gamepad-navigation constants.
    // Keep this shared policy Windows-assembly-free for deterministic xUnit tests.
    public static ConsoleNavigationAction Resolve(int virtualKey, bool interactionBusy)
    {
        return virtualKey switch
        {
            27 or 196 when !interactionBusy => ConsoleNavigationAction.Exit, // Escape / GamepadB
            203 or 211 => ConsoleNavigationAction.Up,    // DPad / left stick
            204 or 212 => ConsoleNavigationAction.Down,
            205 or 214 => ConsoleNavigationAction.Left,
            206 or 213 => ConsoleNavigationAction.Right,
            _ => ConsoleNavigationAction.None
        };
    }
}
