# PaRuX Gaming — HDR / TV / Playnite comparison (16 Nov 2026)

**State:** DRAFT/source implementation, Windows native HDR probe and per-game UI NOT tested.
This is a behavioral comparison, not Playnite code reuse or a guarantee of parity.

## Reference: what Playnite actually offers
- Controller-first Fullscreen Mode, filters, presets, details and game launch.
- Per-game/emulator/global pre-start, post-start, exit scripts. Custom scripts can
  block the UI and should be used carefully; PaRuX should prefer typed,
  cancellable policies over arbitrary unattended PowerShell.
- Configurable game launch/tracking modes (original process, children, folder),
  custom emulator commands/profiles and lifecycle actions.
- Playnite's troubleshooting documentation acknowledges that returning focus
  to its fullscreen UI after a game closes can still fail on some machines.
  This is a reference for honest status, not a guarantee of focus control.
- Playnite does NOT provide a universally guaranteed built-in Windows HDR
  toggle/restore for any game. Advanced HDR automation is often separate
  software or an extension and needs ownership, restoration and hardware tests.

Upstream documentation:
https://api.playnite.link/docs/manual/gettingStarted/playniteFullscreenMode.html
https://api.playnite.link/docs/manual/features/scriptingSupport/scriptingSupportOverview.html
https://api.playnite.link/docs/manual/library/games/gameActions.html
https://api.playnite.link/docs/manual/gettingStarted/helpAndTroubleshooting/troubleshooting.html

## Source implementation in draft PR #9
1. Windows CCD read-only per-display query resolves exact active GDI display
   source to a unique target, then queries DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2.
   HDR=activeColorMode HDR only. WCG must not be presented as HDR. On older
   drivers, GET_ADVANCED_COLOR_INFO can only disclose ambiguous *Advanced
   Color*, so HDR active/supported remain unknown (not false confidence).
2. Games > Launch profile supports HDR: **System** (legacy default), **Prefer**
   (advisory only) or **Require already enabled** (refuses BEFORE AI Gaming
   hard-off/launch unless the EXACT selected display has verified HDR=on).
   The policy is checked a second time immediately before process start; the
   actual game's render pipeline is not observed or changed. Launch response
   distinguishes readiness from actual GPU rendering.
3. Console TV selector and per-game display list show Windows HDR / WCG / SDR
   status or an honest unknown. No EDID serial, monitor device key, or private
   user display path is saved in application logs by this feature.
4. Previous PR #9 scope: per-game foreground/fullscreen/one-shot click helper
   for exact owned manual game process and window; emulator-native full/window
   flag precedence to avoid an extra Alt+Enter toggle. This is NOT a trusted
   store/Playnite game process handoff.

## Critical limitations
- This draft DOES NOT turn HDR on or off, does not change color depth, Windows
  Auto HDR, SDR brightness, ICC profile, VRR/G-Sync/FreeSync, FPS, refresh,
  exclusive fullscreen, driver capabilities or game settings.
- Resolution/refresh temporary change is a SEPARATE existing Gaming Devices
  feature with a 15-second confirm/revert journal; it cannot yet guarantee
  HDR/WCG preservation across a mode change and must not be called an HDR
  transaction. Combining multiple state-changing display operations requires
  a single crash-safe journal, exact target identity, checksum and
  confirmation/recovery semantics.
- HDR can be supported at the hardware level but disabled by Windows
  policies, cable/port bandwidth, remote sessions or monitor settings.
- WCG and HDR must remain separate on Windows 11 24H2+. An older API's
  advancedColorEnabled bit is NOT proof of HDR.
- The 4K/120Hz HDR experience on an OLED can differ with HDMI bandwidth,
  color format, GPU driver, TV firmware and in-game tone-mapping. VRR is
  UNKNOWN until a supported, properly scoped API probes it.
- PaRuX cannot guarantee the start of another application's game rendering or
  manage HDR automatically when a store/launcher handoff cannot be trusted.

## Native Windows acceptance (must run on physical hardware)
1. Windows 11 24H2 or newer: HDR-capable TV with HDR on -> report HDR; HDR
   off -> SDR; WCG only -> WCG. Use Windows Display Settings and verify
   **actual** mode. Ensure two monitors (one HDR/one SDR) never mix targets.
2. Windows 10 / older Windows 11: old Advanced Color flag must report
   UNKNOWN, never a fabricated positive HDR status.
3. Physically disconnect/reconnect TV, change duplicate/extend/primary
   display, move HDMI port, suspend/resume, RDP; no stale monitor promoted.
4. System (default) mode: zero additional CCD probing during game launch;
   no behavior changes for existing per-game profiles.
5. Prefer: launch when HDR not confirmed, report advisory; Require: refuse
   before gaming hard-off and process start if disconnected, unknown, WCG,
   SDR or duplicate target. Require with HDR on: launch only exact target.
6. Toggle HDR between preflight and process start: second gate must prevent
   a stale Require startup. If it changes after process start, a status
   warning is not proof the game rendered in HDR.
7. Real 4K 120Hz HDR TV with Radeon iGPU and RTX eGPU paths, multiple
   resolutions, HDR+VRR and Alt+Enter toggles, plus emulator + controller.
8. Regression: Playnite handoff, Steam/Epic/GOG, window focus recovery,
   auto Return-to-AI, AI hard-off and no FPS impact regression. Run
   x64 Release solution, focused/full Server xUnit, exclusive FlaUI and
   real game tests. Keep validation red if physical hardware unavailable.
9. Inspect any Windows CCD struct size and P/Invoke marshalling on real
   Windows SDK x64: return UNKNOWN on failed device query, not a guessed
   HDR result. Run negative tests for monitor clones, wrong adapter and
   inaccessible desktop session.

## Next feature slices (NOT delivered)
P0: A transactional, **explicitly opted-in per-game HDR ON/OFF** action only
after the following are proven on target hardware: exact output identity,
single saved original HDR/WCG mode, atomic crash journal, bounded timeout,
no overwrite of user/external state change, automatic restore at game exit,
shutdown/crash/reboot, hard-fail on disconnect or unknown state, no silent
settings override. The user must be able to opt out and restore manually.

P1: Reliable process identity for store/emulator handoffs, exit and foreground
return to Console, cross-PC per-game profile migration, accessibility-first
controller UI, robust monitor-specific windowed/borderless fullscreen playbooks
(not a blind Alt+Enter toggle). Use profiles and typed hooks instead of
arbitrary scripts by default.

P1: Per-display refresh/VRR/HDR capability probes with trustworthy DXGI/CCD
data, not "VRR enabled" inferred from high refresh; streaming/Moonlight
HDR pipeline requires end-to-end capture→encoding→decoding validation.

Do not claim HDR automatic enable/restore, monitor switch, Playnite parity,
performance gains or native Windows tests until independently measured.
