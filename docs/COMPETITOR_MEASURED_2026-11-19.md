# Measured comparison on one PC (2026-11-19)

Machine: Windows 11, Ryzen 7 8840U class CPU, Radeon 780M iGPU (shared memory), 25.8 GB RAM, 2560x1600 panel at 200% (1280x800 DIP). Isolated profiles only. Nothing here is a claim of superiority; every row says what was and was not measured.

## What was run
| App | Version | How | Profile |
|---|---|---|---|
| PaRuX | candidate builds of `main` (Release x64, self-contained) | `tools/measure-app-startup.ps1`, FlaUI | isolated data root, onboarding done, empty library |
| Playnite | 10.53 (MIT) | portable copy of the installed app, `portable.dat` | copy of the owner's library (17 games) |
| Locally Uncensored | 3.1.1 (AGPL-3.0, official Windows installer, SHA-256 verified, unsigned) | installed, launched, first-run observed | default |

Not run: Hydra, Heroic, LM Studio (data folder only, no executable), Jan, Open WebUI, AnythingLLM. No code from AGPL projects was reused.

## Results
| Flow | PaRuX | Playnite | Locally Uncensored | Evidence |
|---|---|---|---|---|
| Cold start to a titled window | 0.73-1.29 s (3 sets, noisy) | 0.38-0.44 s | 0.17-0.51 s | automated, 3 runs each |
| Idle CPU after 90 s settle (share of one core) | about 1.6% (Core+UI) | not valid yet | not valid yet | automated; competitor idle runs failed (see below) |
| CPU seconds in the first 30 s after launch | about 10 | not measured | not measured | automated; includes JIT and hardware probes |
| Working set (Core + UI) | 430-450 MB | about 45 MB at the dialog, 310-410 MB running | 500-550 MB (8 processes) | automated |
| ReadyToRun build | no measurable CPU gain, window about 0.1-0.4 s faster, +34% size | n/a | n/a | one A/B, not adopted |
| Migration from Playnite | 13 of 17 games imported with covers, favourites and play time; 4 store games left to discovery | n/a | n/a | real library copy, FlaUI + screenshot |
| First-run | Model Hub Start card: all abilities on one page; Chat shows "Set up local chat" | n/a | four-step modal, one decision per step, "Skip for now" | observed, not timed |

## Known gaps
- PaRuX loses cold start to both competitors. A startup experiment (window before Core start) gave no gain and was reverted; the time is WinUI and main-window construction.
- Playnite idle and Locally Uncensored settled-idle numbers are NOT valid: a forced stop made Playnite show its crash-recovery dialog on the next start, and Locally Uncensored showed its first-run modal. Repeat with graceful shutdown and a completed first run.
- No search or navigation timing on equal 2,000-title libraries across apps, no physical controller/TV pass, no first-token timing on one shared model.
