# PaRuX WorkloadOrchestrator & Gaming Resource Sharing

> **Safety status (2026-10-31; source-only):** The legacy window/title/start-time game detector is NOT authorized to suspend a process. Manual Suspend and automatic Quick Resume now refuse to suspend a game until a launcher-owned process instance can be verified by PID, executable and creation time. The current implementation deliberately returns no suspendable PID, so these two features are unavailable; Resume can only act on a previously recorded suspended game. This is a temporary fail-closed protection, not a completed Quick Resume implementation. Gaming entry/exit concurrency and speech child-process quiescence remain open. Windows build and physical acceptance have NOT RUN on this source integration. The descriptions below include planned and legacy behavior and must not be read as present-day acceptance results.

## GOS transition serialization (2026-10-31; source implemented, Windows NOT RUN)

A single asynchronous gate now serializes manual/automatic Gaming entry and exit with
Suspend/Resume side effects. An entry must observe `AiReady`; requests during an
unresolved transition, `Error`, or a persisted Gaming marker cannot start another
launcher. Duplicate requests for the same active session are idempotent. Automatic
trigger polling is awaited, not fire-and-forget, and captures a monotonic transition
version: old trigger results and remote-disconnect observations cannot act after a
different session or a user-requested exit.

An explicit Return to AI requires the automatic trigger sources to become inactive
before held source signals may re-enter Gaming. This is a conservative rearm rule,
not automatic local-game-exit detection or a promise about Sunshine detection accuracy.
Transitions do not terminate or suspend unowned processes. The separate speech child
process admission and microphone/VRAM release paths still require proof.

Deterministic xUnit cases are now **authored** for overlapping entry/exit, duplicate
entry, stale trigger/rearming and old Sunshine disconnect; these tests, the Windows
build and hardware acceptance are **NOT RUN** in the current environment. Execute
`tools/test-gos-checkpoint.ps1` on the Windows development PC before release.

## Overview
The **WorkloadOrchestrator** subsystem in PaRuX AI Studio transforms a Windows PC into an always-available local AI server that automatically yields GPU VRAM, CPU cycles, and audio/input resources to local or remote gaming sessions, and restores AI workloads when gaming ends.

---

## Key Features

1. **State Machine (`WorkloadOrchestrator`)**:
   - `AiReady`: Standard operating mode where LLM (llama.cpp / ExLlamaV3), ComfyUI (diffusion generation), and TTS/STT run natively.
   - `EnteringGaming`: Detects launcher/display/stream triggers, suspends active AI jobs, and unloads model weights from VRAM.
   - `GamingLocal` / `GamingRemote`: PC is dedicated to gaming (Playnite, Steam Big Picture, Sunshine stream).
   - `GameSuspended`: Quick Resume mode where game processes are suspended via Windows Native (`NtSuspendProcess`) or Nyrna.
   - `ExitingGaming`: Entered on an explicit user "Return to AI" or when a remote (Sunshine) session's grace period expires. There is no automatic game-process-exit detection: when a local game quits, Gaming Mode stays on until the user returns to AI.
   - `RestoringAi`: Lifts the hard-off. The runtime is reloaded only on an explicit "Return to AI" or when the profile sets `restoreAiOnExit`; paused AI jobs and downloads never resume automatically.

2. **Pluggable Modular Providers**:
   - **Triggers**: `ManualTriggerProvider`, `WindowsDisplayTriggerProvider`, `SunshineSessionTriggerProvider`.
   - **Launchers**: `PlayniteLauncherProvider`, `SteamBigPictureLauncherProvider`, `CustomExecutableLauncherProvider`, `NoneLauncherProvider`.
   - **Input & Display**: `NativeInputProvider`, `XOutputInputProvider`, `SunshineInputProvider`, `CustomExecutableInputProvider`.
   - **Suspend / Quick Resume**: `WindowsNativeSuspendProvider` (Win32 P/Invoke `NtSuspendProcess`), `NyrnaGameSuspendProvider`, `NoneGameSuspendProvider`.

3. **Quick Resume & VRAM Recovery**:
   - Game processes can be frozen instantly using Win32 kernel primitives.
   - Post-suspension VRAM allocation is tracked using `nvidia-smi` / DXGI hardware probing.
   - AI workloads are safely restored if sufficient VRAM remains.

4. **REST API Endpoints (`/api/v1/gaming/*`)**:
   - `GET /api/v1/gaming/status`: Current state, active game PID/name, VRAM usage, and active profile.
   - `POST /api/v1/gaming/enter`: Request entry into gaming mode.
   - `POST /api/v1/gaming/exit`: Return PC resources to AI server.
   - `POST /api/v1/gaming/suspend`: Freeze current game process.
   - `POST /api/v1/gaming/resume`: Unfreeze game process.
   - `GET /api/v1/gaming/profiles`: List available profiles.
   - `POST /api/v1/gaming/profiles`: Save custom profile.
   - `GET /api/v1/gaming/providers`: List detected system providers.

5. **WPF User Interface (`SettingsView`)**:
   - Dedicated "Gaming & AI Sharing" rail in Settings.
   - Real-time status badge with state indicator.
   - Quick control buttons (`Enter Gaming`, `Return to AI`, `Suspend Game`, `Resume Game`).
   - Profile selection dropdown ("Local Console", "Remote Moonlight/Sunshine", "Custom").
   - System provider detection matrix.

---

## Profile Configuration Schema

```json
{
  "id": "local-console-default",
  "name": "Local Console (Playnite / Steam)",
  "sessionType": "local",
  "triggerProvider": "display",
  "launcherProvider": "playnite",
  "inputProvider": "xoutput",
  "suspendProvider": "windows-native",
  "quickResumePolicy": "always",
  "gracePeriodSeconds": 30,
  "restoreAiOnExit": true
}
```

---

`restoreAiOnExit` (default `false`) is the explicit resume policy: when a session ends without the user asking (remote grace-period expiry, failed-entry rollback) the Gaming marker is lifted but the AI runtime is not reloaded; it loads lazily on the next request. An explicit "Return to AI" through the orchestrator (`POST /api/v1/gaming/exit`) restores the runtime eagerly; the "Exit Gaming Mode" button in Settings and the chat header "Return to AI" button lift the hard-off and let the model load lazily on the next message. Tuning, benchmarks and runtime races are cancelled as soon as Gaming Mode starts and never resume afterwards.

## Safety & Non-Destructive Directives
- Games are **never** forcefully killed without explicit user configuration or interactive user UI action.
- Grace periods allow clean session state saving before resource reallocation.
