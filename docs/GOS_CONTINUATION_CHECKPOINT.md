# PaRuX AI & Gaming OS — source continuation on main

**Updated 2026-11-01. Canonical plan:** `PARUX_EXECUTION_PLAN.md`.

## Branch and evidence policy

Development is intentionally consolidated on the public `main` branch. The
product owner explicitly accepts clearly labelled WIP increments without a
local Windows build, provided user data, history, privacy and existing behavior
are protected. No force-push, GitHub Actions, private credentials, installed
drivers or hidden test claims. Every commit since the continuation is marked
**SOURCE IMPLEMENTED / NOT RUN**, not release approved.

Current continuation HEAD at this checkpoint: see `git log -1` on `main` (previous pinned value `6846cb5…` was stale).

## Verification log (Windows 11, .NET SDK 10.0.302, 2026-11-01)

- `tools/test-gos-checkpoint.ps1`: Server Release build **BUILD PASSED**, WinUI App Release build **BUILD PASSED**,
  focused Server xUnit **TESTED 266/266**. Fixed in `4d76976`: `Registry` name clash in Epic protocol probe, missing
  `PaRuX.Services` using in `MainWindow.ConsoleMode.cs`, staging lock file counted in quota/tests, provider tests
  depending on locally installed Playnite, stale `nyrna` preset expectation.
- FlaUI (`-RunUi`), hardware, display, controller, FPS, streaming: **NOT RUN**.

## Implemented source increments in this continuation

- Shared PaRuX-owned local-file admission across game installer preflight,
  Steam executable discovery and native helper launch; file/ancestor reparse,
  mapped network volume, extension and noncanonical-path checks.
- Playnite existing-process identity verification and serialized launcher
  Start/Stop. User-owned games are never terminated by Return-to-AI.
- ZIP/7z runtime archive entry staging to same-directory temporary files,
  non-overwriting publication after complete quota/cancellation checks,
  portable Windows path validation (reserved devices and ambiguous names).
- Flat, theme-aware Quick Chat bubble; first-party bilingual IT/EN localization
  of visible text, accessible action labels and tooltips.
- GOS-07 first-party local package staging transaction, not installation:
  independent SHA-256 required, copy-only with bounded quota, staging temp
  cleanup, desktop-owner authorization, Gaming cancellation, verified copy
  inventory, explicit deletion by managed ID and IT/EN desktop controls.
- Regression sources enrolled in the local Windows xUnit/FlaUI gate. No
  package/download/launcher action is executed by the new source tests.

## What remains SOFTWARE work, not merely physical PC tests

1. GOS-07 **official download and licensed installation adapters**, provider
   ownership, integrity provenance, transaction recovery and rollback. The
   staging flow intentionally does not download, unpack or execute.
2. GOS-05 **native remapping/optional virtual-controller backend**, actual
   duplicate-input prevention beyond policy refusal, opt-in driver lifecycle
   and comprehensive controller support. Read-only XInput slots are not mapping.
3. GOS-08 **reversible display/streaming profile management**, capability
   validation, restart/crash recovery and return-to-Home focus. Monitor
   topology inspection makes no display-mode changes.
4. GOS-04 **full controller-first TV navigation and virtual keyboard**, Game
   Library focus and game exit/launcher handoff recovery.
5. GOS-01/02/03 **final production brand/logo assets** and complete screen-by-
   screen UX, accessibility, high-contrast and localization migration.
6. GOS-09 **provable cross-worker speech/microphone/GPU quiescence** and
   fallback/child-process lifecycle regression across Gaming transitions.
7. GOS-10 **integrated QA, migration, performance, packaging, dependency and
   security review**, followed by physical Windows/GPU/TV/controller tests.

These are real code/product gaps. It would be inaccurate to say only testing
remains. Keep working on the open tracks in the above dependency order.

## Local acceptance instructions (Windows development machine only)

From the checkout root, after reviewing WIP source and restoring packages:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\test-gos-checkpoint.ps1
powershell -ExecutionPolicy Bypass -File .\tools\test-gos-checkpoint.ps1 -RunUi
```

The first command builds Server + WinUI and runs focused xUnit;
`-RunUi` additionally needs an interactive Windows session and FlaUI
prerequisites. Run both only on the separate development PC with synthetic
data; Windows enterprise PC, DRM/account/launcher, video/audio performance,
driver and controller acceptance are distinct and must be explicitly recorded.
Do not claim any of these commands were run merely because they are documented.

## Historical checkpoint (superseded for current HEAD)

The older `feature/parux-ai-gaming-os-public` / `checkpoint/gos-continuation`
notes described an unmerged interrupted branch at `2c5a0b4`; they were
accurate for that historical snapshot but do **not** describe the current
source on public `main`. Its pending race and verification concerns are
retained as open gates in `PARUX_EXECUTION_PLAN.md`.

## Increments after the Windows verification (same session, all on `main`)

Each row: SOURCE IMPLEMENTED + BUILD PASSED + TESTED (xUnit, `tools/test-gos-checkpoint.ps1`, 579/579 at the last run).
Nothing below is WINDOWS VERIFIED through FlaUI/visual checks or HARDWARE VERIFIED (all NOT RUN).

| Track | Increment | Key files |
|---|---|---|
| GOS-09 | Bounded polling cost (controller trigger TTL + disposed handles, Sunshine loopback-only URL + 3 s reuse), cached VRAM in `GetStatus`, 30 s provider start timeout, `IGamingResumable` wired in `Program.cs`, `EndGamingAsync` reports failure | `TriggerProviders.cs`, `WorkloadOrchestrator.cs`, `LocalRuntimeService.cs`, `Program.cs` |
| GOS-09 | Crash recovery: persisted `gaming-session.json` (profile/trigger/input/launcher ids) resumed by `ReconcileWithPersistedRuntimeState` | `GamingSessionRecord.cs` |
| GOS-07 | Model download resume: `If-Range` with stored strong ETag, `Content-Range` start must equal local length, validator cleaned with partial artifacts | `ResumeValidator.cs`, `ModelDownloadService.cs` |
| GOS-05 | Persisted controller profile (deadzones, trigger threshold, Y invert, button remap), explicit bounded calibration, endpoints; diagnostics/Console only, no virtual device | `GamingControllerProfile.cs`, `ControllerProfileStore.cs`, `ControllerProfileMath.cs`, `NativeXInputProbe.cs` |
| GOS-04 | Focus restored after busy interactions; controller on-screen keyboard for the library filter (gamepad A/Y) | `ManualGameLibraryView.cs`, `ConsoleKeyboardFlyout.cs`, `ConsoleTextEntry.cs` |
| GOS-08 | Reversible display mode change: driver test, session-temporary apply, 15 s auto-revert, persisted restore point + restart recovery, refused during Gaming; HDR/VRR untouched | `GamingDisplayModeService.cs`, `DisplayModeApi.cs`, `GamingEndpoints.cs` |

### Known software gaps still open (not SOURCE COMPLETE)
- GOS-09: Quick Resume / `GameSuspended` unreachable (`FindOwnedSuspendableGameProcessId` always null); 1 s orchestrator loop not yet event-driven; `SpeechWarmWorker._process` unlocked read; missing rollback/restart-in-remote tests.
- GOS-05: no DirectInput/HID, no hot-plug events, remap/profile has no App UI yet and is not injected into games (by design).
- GOS-08: no App UI for display change; no stored named display profiles; HDR/VRR not read.
- GOS-04: key/stick mapping for bumpers/menu, Console-level Accept path, FlaUI coverage of the keyboard and focus restore.
- GOS-07: `GamePackageStageService` copy is not resumable; no install adapters.
- GOS-01..03, GOS-06, GOS-10: not advanced in this session.

### Reuse policy applied
MIT/Apache-2.0 sources (Hydra, Playnite, Codex, Unsloth) may be ported with notices in `THIRD_PARTY_NOTICES.md`; GPL/AGPL sources (Nyrna, LocalUncensored) are not copied, only behaviours are reimplemented natively. No third-party code was ported in the increments above.

## Later increments (same session, `main`)

All SOURCE IMPLEMENTED + BUILD PASSED + TESTED (gate 613/613). FlaUI/visual/hardware NOT RUN.

| Track | Increment |
|---|---|
| GOS-05/08 | Games workspace `GamingDevicesPanel`: display mode list/apply/confirm/revert with countdown; controller profile load/calibrate/save/reset (`/api/v1/gaming/displays/modes`, client methods) |
| GOS-09 | Owned game process registration (PID + creation time + image path, rechecked before acting) makes Quick Resume / explicit suspend reachable; `SpeechWarmWorker._process` volatile |
| GOS-06 | `DiscoveredGameDto.InstallState` (Steam `StateFlags` mapped conservatively, Epic installed, GOG unknown), cross-store duplicate hint and store/state filters (`GameLibraryQuery`), `ManualGameLaunchResult.StartState` (none / requested / process-started / verified) with honest UI text |
| GOS-06 | Library entries are no longer "games only": `Kind` (game / application), `Arguments`, `WorkingDirectory` like Playnite; applications start as-is and never change AI/Gaming state; old library files load as games |
| GOS-10 | `tools/validate-license-inventory.ps1` (in the gate; verified to fail on tampered input), five missing notice rows added (Windows ML Runtime recorded as proprietary, not cleared), notice test now has no exemptions; `tools/bench-gaming-transition.ps1` + `GamingTransitionBenchmarkTests` (stub providers: enter p50 ≈ 3.5 ms, exit p50 ≈ 0.7 ms on this machine; orchestration only, NOT a GPU/FPS/VRAM result) |

### Sources deliberately NOT integrated
Repack sites and launchers distributing unlicensed copies (FitGirl, DODI and similar) are not integrated: they conflict with the project rule against unverified repacks, cracks and DRM bypass. Game acquisition stays with Steam, Epic, GOG, official DRM-free installers and user-chosen local files verified by SHA-256.

### Still open
GOS-01/02/03 visual identity audit, GOS-04 Console shell mapping + FlaUI, GOS-05 DirectInput/HID and hot-plug, GOS-06 cover art from authorized sources and exit handling for store-owned games, GOS-07 resumable staging copy and install adapters, GOS-08 named display profiles, GOS-10 migration/offline/packaging test categories and real-hardware matrix.
