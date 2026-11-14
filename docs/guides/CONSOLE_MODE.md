# Console / TV mode

Status: **early foundation, PARTIAL.** It is not a finished controller-first Console Shell.

## What exists

- An opt-in **Console / TV** toggle in the navigation footer. It switches the same main window to fullscreen
  and does not change resolution, HDR, refresh rate or Windows display settings.
- The same Game Library, with larger text, targets and spacing for TV distance.
- Navigation: D-pad or left stick moves focus, **A** selects, **Escape** or **B** returns to desktop.
  Exit restores the previous window size and focus.
- A user-confirmed **Return to AI** action after a game; the game is not closed by it.
- After a verified game exit in Console mode, PaRuX can bring Console home back to the selected TV (conditions in source; real TV not tested).

- An **on-screen keyboard** (PaRuX's own overlay, not Windows TabTip/osk.exe) for text, password and search fields. In Console mode it opens when
  a field receives focus from a gamepad key, and when **A**/**Y** is pressed on a focused field (F4 from a physical keyboard). It never opens in Desktop
  mode, after a pointer click or after physical-keyboard navigation. Layers: letters (with an optional Italian accent row), digits, symbols; Shift, Caps,
  Space, Backspace, cursor keys. D-pad/left stick moves across keys, **A** types, **X** backspace, **Y** space, **LB/RB** move the cursor, **LT** Shift,
  **RT** letters/symbols, **Start** or the Done key confirms, **B**/Escape cancels (restores the text the field had). Closing returns focus to the same
  field; password fields show dots in the preview. Every key has an automation name and id. Pad-button and 1280x720 / 4K layout behaviour is
  **NOT physically verified**; a FlaUI test covers it with keyboard emulation (F4 plus UIA invoke).

## What does not exist yet

- Full controller-first navigation of every screen, and per-controller button labels.
- Display routing to a TV, HDR/VRR handling and a Moonlight / Sunshine session (**MISSING**).
- No resident controller polling and no controller driver is installed.

## Evidence

A FlaUI test covers entering and leaving the fullscreen presenter and typing through the on-screen keyboard (keyboard-emulated). Physical controller presses, a TV, 4K scaling and keyboard-only
acceptance are **NOT RUN**; controller tests with simulated devices are emulation, not hardware proof.
Authority: GOS-04 in [PARUX_EXECUTION_PLAN.md](../../PARUX_EXECUTION_PLAN.md).

<!-- screenshot: pending real capture -->

See [GAMING.md](GAMING.md) for the library and Gaming Mode.
