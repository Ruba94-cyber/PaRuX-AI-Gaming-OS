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

## What does not exist yet

- Virtual keyboard, full controller-first navigation of every screen, and per-controller button labels.
- Display routing to a TV, HDR/VRR handling and a Moonlight / Sunshine session (**MISSING**).
- No resident controller polling and no controller driver is installed.

## Evidence

A FlaUI test covers entering and leaving the fullscreen presenter. Physical controller presses, a TV, 4K scaling and keyboard-only
acceptance are **NOT RUN**; controller tests with simulated devices are emulation, not hardware proof.
Authority: GOS-04 in [PARUX_EXECUTION_PLAN.md](../../PARUX_EXECUTION_PLAN.md).

<!-- screenshot: pending real capture -->

See [GAMING.md](GAMING.md) for the library and Gaming Mode.
