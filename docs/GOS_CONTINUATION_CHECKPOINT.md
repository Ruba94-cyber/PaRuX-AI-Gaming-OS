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
