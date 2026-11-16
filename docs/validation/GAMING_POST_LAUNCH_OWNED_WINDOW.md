# Gaming Library — owned-window startup assistance

**Status: source-only, NOT Windows-tested and NOT release-ready.** Playnite is a UX benchmark here, not an integrated provider of process identity.

## Scope delivered
Per directly-launched manual game (not generic apps or store handoffs): open Launch Profile > After launch (advanced), enable one or more opt-in, per-game options:
- Bring the verified game window to foreground once via the normal Windows foreground API.
- Post one targeted Alt+Enter toggle to request fullscreen. This may do nothing, or may toggle an already-fullscreen game back to windowed. Success means messages were posted, NOT that fullscreen was achieved.
- Post one center click within the game window, after confirming the same verified foreground HWND. Games using raw input may ignore the posted message. A center click may activate a game menu control; the user must explicitly opt in.
- Optional 0–8000 ms delay after launch. No repeated clicking, blind Alt+Tab, global SendInput or display-mode mutation.

All actions default OFF and persist only for the specific library entry. Old launch-profile JSON remains valid and does not activate any new actions. The existing Fullscreen field still means preference_only; no client may claim that it forces graphics mode.

Core requires the exact directly owned game process (PID, creation time, image path), one unique stable top-level enabled/visible window, Windows-approved foreground ownership, and cancellation-safe session. It does not adopt children, scan arbitrary process titles, stop other programs, bypass UAC/UIPI/EDR or elevate itself. If TV placement is requested and not confirmed, do not send the optional input on a potentially wrong monitor.

Response PostLaunch statuses:
- foreground-confirmed: the HWND was observed in foreground
- fullscreen-requested: an Alt+Enter message pair was posted, not proof of video mode
- click-posted or fullscreen-and-click-posted: client mouse messages were posted, not proof the game accepted them
- foreground-denied or not-confirmed: no fabricated success; user can manually activate/configure game
- empty: no actions requested, legacy behavior

## Emulator support in this branch (source only)

PaRuX can already import user-installed Eden/Eden CLI, Dolphin, Cemu,
PCSX2, RPCS3, DuckStation, PPSSPP, RetroArch and MAME. This branch adds
a pure emulator command-line policy that recognizes the **actual imported
ROM path and exact emulator executable name** and reads only whole
documented fullscreen/windowed option tokens. The **last native display
option wins**. If an explicit native fullscreen argument was used, it
suppresses the extra post-launch Alt+Enter toggle without changing the
saved profile. This avoids the common "fullscreen became windowed"
problem. It reports only that the native argument was supplied, NOT
that the renderer entered exclusive fullscreen.

Additional focused unit fixtures: EmulatorPostLaunchPolicyTests tests
nine built-in definitions (including RPCS3 without a documented flag),
conflicting fullscreen/windowed switches, changed executable name,
missing ROM identity, false-positive substrings and profile nonmutation.

Windows must still test emulator sessions with an actual ROM/homebrew
file, physical pad, GUI and renderer windows, splash/Qt dialogs, window
focus restrictions, and suspend/resume; this is not a general emulator
automation guarantee.

## Known limitations
Steam/Epic/GOG and Playnite launcher handoffs lack trusted target-game HWND ownership in the current launcher orchestration. No blind focus, clicks or keyboard events on those processes. A full Playnite-style recovery system requires a new verified identity handoff and distinct tested policies. Some engines ignore Win32 posted input and need their own native fullscreen option or user-configured launch parameters.

## Windows acceptance matrix
1. Default profile: launch multiple games without any automatic focus, fullscreen or click; no AI/Gaming hard-off regression.
2. One owned window: focus only; then Alt+Enter; then optional click. Record whether the game actually consumed messages; posted status alone is insufficient.
3. Safe simulated 2-window/splash/tool/hidden/minimized/disabled/unowned/foreign PID and process-replacement cases: must refuse, without input.
4. User switches to Outlook during the delay, UAC/elevated game, session cancellation, process exits early: no input to foreign windows, no global injection or security bypass.
5. Dual-monitor TV with negative coordinates and DPI scaling; placement must be verified before optional focus. Evaluate emulator game vs emulator settings window.
6. Verify Windows x64 Release solution build; focused xUnit (OwnedGamePostLaunchPolicyTests, GameLaunchProfileTests, OwnedGameWindowPlacementPolicyTests, ManualGameLaunchTests), full Server xUnit, exclusive FlaUI, real native launch on a test game, controller-first/13-inch accessibility and per-game persistence.
7. Run tools/check-public-safety.ps1 locally before merge. Do not create GitHub Actions, release, tags, signature or Store submission from this draft branch.

## Follow-on scope (not implemented)
Verified Playnite/Steam/Epic/GOG child process identity handoff with consent; game-specific fullscreen methods rather than one universal Alt+Enter; safe user-assisted click when direct/raw input ignores window messages; richer diagnostics and controller-first per-game profile presets; scalable 2,000-title library benchmark and save/config export/import that preserves user-managed files.
