using System;
using System.Collections.Generic;

namespace PaRuX.Server.Orchestration.Gaming.Providers.VirtualPad;

/// <summary>An XInput-compatible gamepad report (same layout as XINPUT_GAMEPAD): the only thing a virtual Xbox 360 pad understands.</summary>
public readonly record struct Xbox360Report(
    ushort Buttons, byte LeftTrigger, byte RightTrigger, short LeftX, short LeftY, short RightX, short RightY)
{
    public const ushort DpadUp = 0x0001, DpadDown = 0x0002, DpadLeft = 0x0004, DpadRight = 0x0008;
    public const ushort Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040, RightThumb = 0x0080;
    public const ushort LeftShoulder = 0x0100, RightShoulder = 0x0200, Guide = 0x0400;
    public const ushort A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;
}

/// <summary>Raw state of a non-XInput gamepad as a generic HID reader reports it: indexed buttons, axes and one hat switch.</summary>
public sealed record RawGamepadState(IReadOnlyList<bool> Buttons, IReadOnlyList<double> Axes, int HatDirection);

/// <summary>Where the physical hat switch is routed on the virtual Xbox pad.</summary>
public enum PadHatTarget { Dpad = 0, LeftStick = 1, RightStick = 2 }

/// <summary>
/// How the numbered controls of one physical pad map onto an Xbox 360 pad. Axis values are expected in -1..1
/// (sticks) or 0..1 (analog triggers); a hat is -1 (centred) or 0..7 clockwise from up (0=up, 2=right...).
/// Nothing here is specific to a brand: the indices come from the user's profile, because HID layouts differ.
/// </summary>
public sealed class PadMapping
{
    /// <summary>Source button index to Xbox button bit (single bit, e.g. Xbox360Report.A).</summary>
    public Dictionary<int, ushort> Buttons { get; set; } = new();
    public int? LeftXAxis { get; set; }
    public int? LeftYAxis { get; set; }
    public int? RightXAxis { get; set; }
    public int? RightYAxis { get; set; }
    public bool InvertLeftY { get; set; } = true;   // HID Y grows downwards, XInput Y grows upwards
    public bool InvertRightY { get; set; } = true;
    public int? LeftTriggerAxis { get; set; }
    public int? RightTriggerAxis { get; set; }
    /// <summary>Digital triggers: source button index that presses the trigger fully.</summary>
    public int? LeftTriggerButton { get; set; }
    public int? RightTriggerButton { get; set; }
    /// <summary>Axis range of the trigger source: true = -1..1 (rest at -1), false = 0..1.</summary>
    public bool TriggerAxisIsSigned { get; set; }
    public double StickDeadzone { get; set; } = 0.1;
    /// <summary>Hat routing is enabled (historic name; the destination is <see cref="HatTarget"/>).</summary>
    public bool HatIsDpad { get; set; } = true;
    public PadHatTarget HatTarget { get; set; } = PadHatTarget.Dpad;
    public bool InvertLeftX { get; set; }
    public bool InvertRightX { get; set; }

    public PadMapping Clone() => new()
    {
        Buttons = Buttons is null ? new() : new Dictionary<int, ushort>(Buttons),
        LeftXAxis = LeftXAxis, LeftYAxis = LeftYAxis, RightXAxis = RightXAxis, RightYAxis = RightYAxis,
        InvertLeftX = InvertLeftX, InvertRightX = InvertRightX,
        InvertLeftY = InvertLeftY, InvertRightY = InvertRightY,
        LeftTriggerAxis = LeftTriggerAxis, RightTriggerAxis = RightTriggerAxis,
        LeftTriggerButton = LeftTriggerButton, RightTriggerButton = RightTriggerButton,
        TriggerAxisIsSigned = TriggerAxisIsSigned, StickDeadzone = StickDeadzone,
        HatIsDpad = HatIsDpad, HatTarget = HatTarget
    };

    public string? Validate()
    {
        const int Max = 64;
        if (Buttons is null) return "Button mapping table is missing.";
        if (Buttons.Count > Max) return "Too many button mappings.";
        foreach (var (index, bit) in Buttons)
            if (index is < 0 or >= Max || bit == 0 || (bit & (bit - 1)) != 0) return "Each button must map to exactly one Xbox button.";
        foreach (int? axis in new[] { LeftXAxis, LeftYAxis, RightXAxis, RightYAxis, LeftTriggerAxis, RightTriggerAxis })
            if (axis is < 0 or >= Max) return "An axis index is out of range.";
        foreach (int? button in new[] { LeftTriggerButton, RightTriggerButton })
            if (button is < 0 or >= Max) return "A trigger button index is out of range.";
        if (!Enum.IsDefined(HatTarget)) return "The hat destination is not valid.";
        if (!double.IsFinite(StickDeadzone) || StickDeadzone is < 0 or > 0.6)
            return "The stick deadzone must be a finite number between 0 and 0.6.";
        return null;
    }
}

/// <summary>Pure translation of a generic pad state into an Xbox 360 report. Out-of-range indices and NaN are treated as "not pressed / centred".</summary>
public static class GenericPadMapper
{
    public static Xbox360Report Map(RawGamepadState state, PadMapping mapping)
    {
        ushort buttons = 0;
        foreach (var (index, bit) in mapping.Buttons)
            if (index >= 0 && index < state.Buttons.Count && state.Buttons[index]) buttons |= bit;
        if (mapping.HatIsDpad && mapping.HatTarget == PadHatTarget.Dpad) buttons |= HatToDpad(state.HatDirection);

        short lx = Stick(state, mapping.LeftXAxis, mapping.InvertLeftX, mapping.StickDeadzone);
        short ly = Stick(state, mapping.LeftYAxis, mapping.InvertLeftY, mapping.StickDeadzone);
        short rx = Stick(state, mapping.RightXAxis, mapping.InvertRightX, mapping.StickDeadzone);
        short ry = Stick(state, mapping.RightYAxis, mapping.InvertRightY, mapping.StickDeadzone);
        if (mapping.HatIsDpad && mapping.HatTarget != PadHatTarget.Dpad && HatToStick(state.HatDirection) is { } hat)
        {
            // A pressed hat overrides the stick it is routed to; centred leaves the analogue value alone.
            if (mapping.HatTarget == PadHatTarget.LeftStick) { lx = hat.X; ly = hat.Y; }
            else { rx = hat.X; ry = hat.Y; }
        }
        byte lt = Trigger(state, mapping.LeftTriggerAxis, mapping.LeftTriggerButton, mapping.TriggerAxisIsSigned);
        byte rt = Trigger(state, mapping.RightTriggerAxis, mapping.RightTriggerButton, mapping.TriggerAxisIsSigned);
        return new Xbox360Report(buttons, lt, rt, lx, ly, rx, ry);
    }

    public static ushort HatToDpad(int hat) => hat switch
    {
        0 => Xbox360Report.DpadUp,
        1 => (ushort)(Xbox360Report.DpadUp | Xbox360Report.DpadRight),
        2 => Xbox360Report.DpadRight,
        3 => (ushort)(Xbox360Report.DpadDown | Xbox360Report.DpadRight),
        4 => Xbox360Report.DpadDown,
        5 => (ushort)(Xbox360Report.DpadDown | Xbox360Report.DpadLeft),
        6 => Xbox360Report.DpadLeft,
        7 => (ushort)(Xbox360Report.DpadUp | Xbox360Report.DpadLeft),
        _ => 0,
    };

    /// <summary>Hat direction as full-deflection stick values (X right positive, Y up positive); null when centred.</summary>
    public static (short X, short Y)? HatToStick(int hat)
    {
        ushort d = HatToDpad(hat);
        if (d == 0) return null;
        short x = (short)((d & Xbox360Report.DpadRight) != 0 ? 32767 : (d & Xbox360Report.DpadLeft) != 0 ? -32767 : 0);
        short y = (short)((d & Xbox360Report.DpadUp) != 0 ? 32767 : (d & Xbox360Report.DpadDown) != 0 ? -32767 : 0);
        return (x, y);
    }

    private static double Axis(RawGamepadState state, int? index) =>
        index is { } i && i >= 0 && i < state.Axes.Count && double.IsFinite(state.Axes[i])
            ? Math.Clamp(state.Axes[i], -1.0, 1.0) : 0.0;

    private static short Stick(RawGamepadState state, int? axis, bool invert, double deadzone)
    {
        double v = Axis(state, axis);
        if (invert) v = -v;
        double magnitude = Math.Abs(v);
        if (magnitude <= deadzone) return 0;
        double scaled = Math.Min(1.0, (magnitude - deadzone) / (1.0 - deadzone)) * Math.Sign(v);
        return (short)Math.Clamp(Math.Round(scaled * 32767.0), -32767, 32767);
    }

    private static byte Trigger(RawGamepadState state, int? axis, int? button, bool signed)
    {
        if (button is { } b && b >= 0 && b < state.Buttons.Count && state.Buttons[b]) return 255;
        // A missing/disconnected/non-finite signed axis must be released, not
        // interpreted as its midpoint (128) by normalising a fallback zero.
        if (axis is not { } i || i < 0 || i >= state.Axes.Count ||
            !double.IsFinite(state.Axes[i])) return 0;
        double v = Math.Clamp(state.Axes[i], -1.0, 1.0);
        double unit = signed ? (v + 1.0) / 2.0 : Math.Max(0.0, v);
        return (byte)Math.Clamp(Math.Round(unit * 255.0), 0, 255);
    }
}
