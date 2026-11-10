using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PaRuX.Services
{
    /// <summary>
    /// The Win32 pointer primitives Computer Use drives, behind one seam. The decisions made around them (which
    /// button, whether Windows accepted each event, whether the target still holds the point) are unit-tested with
    /// a fake driver. Only the real driver in DesktopAutomationService moves the user's pointer.
    /// </summary>
    internal interface IComputerUseInputDriver
    {
        /// <summary>SetCursorPos. False when Windows did not move the pointer.</summary>
        bool MoveCursor(int x, int y);

        /// <summary>SendInput for one mouse event. Returns how many events Windows inserted; zero means the input was blocked.</summary>
        uint SendMouseEvent(uint flags, uint mouseData);

        /// <summary>SendInput for one keyboard event. Returns how many events Windows inserted; zero means the input was blocked.</summary>
        uint SendKeyboardEvent(ushort virtualKey, ushort scanCode, uint flags);
    }

    /// <summary>Text entry for Computer Use. Each character is one press and one release.</summary>
    internal static class ComputerUseKeyboard
    {
        internal const uint KeyEventKeyUp = 0x0002;
        internal const uint KeyEventUnicode = 0x0004;

        internal enum ComboOutcome { Delivered, Refused, Revoked, Cancelled, Blocked, Error }
        internal sealed record ComboResult(ComboOutcome Outcome, int PressedCount, bool AllReleasesAccepted, bool DeliveryUncertain = false);

        internal static async Task<ComboResult> PressComboAsync(IComputerUseInputDriver driver,
            IReadOnlyList<ushort> keys, Func<bool> mayContinue, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(driver);
            ArgumentNullException.ThrowIfNull(keys);
            ArgumentNullException.ThrowIfNull(mayContinue);
            int pressed = 0;
            var releaseKeys = new List<ushort>();
            bool releasesAccepted = true;
            bool deliveryUncertain = false;
            ComboOutcome outcome = keys.Count == 0 ? ComboOutcome.Refused : ComboOutcome.Delivered;
            try
            {
                foreach (ushort key in keys)
                {
                    if (ct.IsCancellationRequested) { outcome = ComboOutcome.Cancelled; break; }
                    bool allowed;
                    try { allowed = mayContinue(); }
                    catch (Exception) { outcome = ComboOutcome.Error; break; }
                    if (!allowed) { outcome = ComboOutcome.Revoked; break; }
                    if (ct.IsCancellationRequested) { outcome = ComboOutcome.Cancelled; break; }
                    uint inserted;
                    try { inserted = driver.SendKeyboardEvent(key, 0, 0); }
                    catch (Exception)
                    {
                        // A driver exception cannot prove whether the down reached Windows.
                        // Attempt the matching release and report the uncertainty.
                        releaseKeys.Add(key);
                        deliveryUncertain = true;
                        outcome = ComboOutcome.Error;
                        break;
                    }
                    if (inserted == 0) { outcome = ComboOutcome.Blocked; break; }
                    pressed++;
                    releaseKeys.Add(key);
                }
                if (outcome == ComboOutcome.Delivered)
                    await Task.Delay(30, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                outcome = ComboOutcome.Cancelled;
            }
            finally
            {
                // Releases remain necessary after cancellation or focus loss to avoid stuck modifiers.
                for (int i = releaseKeys.Count - 1; i >= 0; i--)
                {
                    try { if (driver.SendKeyboardEvent(releaseKeys[i], 0, KeyEventKeyUp) == 0) releasesAccepted = false; }
                    catch (Exception) { releasesAccepted = false; }
                }
            }
            return new ComboResult(outcome, pressed, releasesAccepted, deliveryUncertain);
        }

        /// <summary>
        /// Sends one character with KEYEVENTF_UNICODE, so no virtual-key or shift-state mapping is needed. False when Windows
        /// inserted either event. The press is still attempted when the release is refused, and the result says so.
        /// </summary>
        internal static bool TypeCharacter(IComputerUseInputDriver driver, char character)
        {
            ArgumentNullException.ThrowIfNull(driver);
            uint pressed = driver.SendKeyboardEvent(0, character, KeyEventUnicode);
            uint released = driver.SendKeyboardEvent(0, character, KeyEventUnicode | KeyEventKeyUp);
            return pressed != 0 && released != 0;
        }
    }

    /// <summary>Pointer clicks with every Windows refusal reported, never assumed delivered.</summary>
    internal static class ComputerUsePointer
    {
        internal const uint MouseLeftDown = 0x0002;
        internal const uint MouseLeftUp = 0x0004;
        internal const uint MouseRightDown = 0x0008;
        internal const uint MouseRightUp = 0x0010;
        internal const uint MouseMiddleDown = 0x0020;
        internal const uint MouseMiddleUp = 0x0040;

        internal enum Outcome
        {
            /// <summary>Every requested click was sent.</summary>
            Delivered,

            /// <summary>The target lost its place while the pointer settled. No further click was sent.</summary>
            TargetChanged,

            /// <summary>Windows refused the pointer move or a button event, for example a higher-privilege window or a locked desktop.</summary>
            Blocked,

            /// <summary>The button name is not one PaRuX supports. Nothing was sent.</summary>
            Refused
        }

        /// <summary>The outcome and how many clicks were actually sent, so a partly delivered double click is reported honestly.</summary>
        internal sealed record ClickResult(Outcome Outcome, int ClicksSent);

        /// <summary>
        /// Moves the pointer, lets it settle, then clicks once or twice. <paramref name="stillOnTarget"/> is read
        /// before every click, so a click is never delivered after the target lost the point the screenshot showed.
        /// </summary>
        internal static async Task<ClickResult> ClickAsync(
            IComputerUseInputDriver driver,
            int x,
            int y,
            string? button,
            bool doubleClick,
            Func<bool>? stillOnTarget,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(driver);
            cancellationToken.ThrowIfCancellationRequested();
            if (!ComputerUseActionParser.TryNormalizeButton(button, out string name))
                return new ClickResult(Outcome.Refused, 0);

            (uint down, uint up) = name switch
            {
                "right" => (MouseRightDown, MouseRightUp),
                "middle" => (MouseMiddleDown, MouseMiddleUp),
                _ => (MouseLeftDown, MouseLeftUp)
            };

            if (!driver.MoveCursor(x, y)) return new ClickResult(Outcome.Blocked, 0);
            await Task.Delay(60, cancellationToken).ConfigureAwait(false);

            int requested = doubleClick ? 2 : 1;
            int sent = 0;
            for (int click = 0; click < requested; click++)
            {
                if (click > 0) await Task.Delay(60, cancellationToken).ConfigureAwait(false);
                if (stillOnTarget != null && !stillOnTarget()) return new ClickResult(Outcome.TargetChanged, sent);

                // Both edges are attempted even when the first one is refused, so a press cannot be left held down.
                uint downInserted = driver.SendMouseEvent(down, 0);
                uint upInserted = driver.SendMouseEvent(up, 0);
                if (downInserted == 0 || upInserted == 0) return new ClickResult(Outcome.Blocked, sent);
                sent++;
            }
            return new ClickResult(Outcome.Delivered, sent);
        }
    }
}
