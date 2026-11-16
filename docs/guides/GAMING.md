# Gaming Mode and the game library

Status: **PARTIAL.** Parts are source-implemented but not Windows-verified; each is marked below.

## Idea

When a game runs, AI must not compete for your GPU and RAM. Gaming Mode is a **hard-off**: while it is active, AI start is refused
(HTTP 409 `gaming_active`) and PaRuX-owned AI processes are stopped. Leaving Gaming loads nothing heavy by itself.

## Measured (see [PERFORMANCE_BENCHMARKS.md](../PERFORMANCE_BENCHMARKS.md))

- Core-only, Qwen3-4B on an RTX 5080 (eGPU): `llama-server` gone in about 114 ms, VRAM 4318 to 261 MiB, AI start refused. RTX figures are
  from the 2026-11-10 run; that GPU was not attached for later runs, so treat them as unverified for new commits.
- Real non-elevated game (an emulator launched from the library), Radeon 780M / CPU: `llama-server` gone in 362 to 511 ms, 3 of 3 rounds.
- Speech, retrieval, uv install and LoRA child processes also stopped in 1.3 to 1.9 s (12 of 12, Core-owned processes).
- **NOT RUN:** in-game FPS impact, a 20-entry soak with voice loaded, speech while a real game runs.

## Return to AI

After a game, use **Return to AI** (chat header, or Settings, Gaming). The model that was loaded is remembered and reloads on your first
request; it is not reloaded automatically except in the always-on Local AI mode.

## Add games

Open **Games**, then **Add & import**: add a local game executable, scan your stores, or import an emulator folder.
Steam, Epic and GOG discovery is read-only and opt-in. Launch needs your confirmation. Real scans of all three stores are not yet recorded.

## A game starts behind another window or does not enter fullscreen

**Source-only option, not yet verified on physical Windows games:** for a game added by choosing its local executable, open **Games → Game details → Launch profile → After-launch assistance**. The default is **None**. You can opt into **Bring to foreground**, **Foreground + Alt+Enter**, **Foreground + one center click**, or **All**. The advanced section also offers an optional delay and individual toggles.

These actions run only after PaRuX has started and verified the game's exact process, and has observed one stable, eligible game window. They never send system-wide keyboard/mouse input, never touch unrelated applications, never bypass Windows foreground restrictions and do not silently persist a different model/game configuration. Launching through Steam/Epic/GOG/Playnite is currently a launcher handoff without a trustworthy target window; automatic focus/click is not yet offered there.

**Alt+Enter toggles display mode** in games that support it; it can also undo fullscreen and some games ignore it. Similarly, a window-targeted click may be ignored by a raw-input game or may activate an in-game menu. PaRuX reports whether foreground was observed and messages were *posted*, not whether fullscreen or a game action actually happened. Use the game's own graphics/launch options when needed. Real Windows + controller/TV testing is still required; see [post-launch acceptance](../validation/GAMING_POST_LAUNCH_OWNED_WINDOW.md).

## Emulator fullscreen, controller-first startup and per-game profiles

The emulator importer can add existing local game files for **Eden / Eden CLI,
Dolphin, Cemu, PCSX2, RPCS3, DuckStation, PPSSPP, RetroArch (with a user-owned
libretro core) and MAME**. It does not download, provision or bypass firmware,
keys, BIOS images or game content; the emulator and game files remain yours.

Emulator imports already support documented native fullscreen launch arguments
on compatible versions, and PCSX2 Qt has an opt-in Big Picture flag. The
per-game **After-launch assistance** presets additionally work only when the
imported emulator process has been started and its exact window identity has
been verified by PaRuX. The helper checks the actual imported command arguments:
when an emulator already has a native fullscreen switch, it **does not post an
extra Alt+Enter toggle**, which might inadvertently return the game to windowed.
If an explicit *windowed* switch appears later, that later setting wins.
PaRuX reports the native fullscreen *request*, not a guaranteed renderer mode.

Emulators with an extra settings/splash window can be ambiguous: PaRuX then
refuses to guess which window should receive input. Emulator-native controller
mapping, gamepad drivers and raw input remain controlled by the emulator, not
altered silently by PaRuX. Tests with physical controllers, TV, multi-monitor,
suspend/resume and launcher handoffs are still required. The current policy is
documented under [verified post-launch acceptance](../validation/GAMING_POST_LAUNCH_OWNED_WINDOW.md).

## HDR, WCG and the selected TV — verified-readiness option (draft)

PaRuX now has a **source-only read-only Windows HDR probe** for each active
monitor. On Windows/driver versions that expose an explicit HDR color mode,
it distinguishes **HDR**, **WCG (wide color gamut, not HDR)** and **SDR**.
A legacy Advanced Color bit is NOT treated as evidence that HDR is active:
the UI says `HDR unknown` when the OS cannot prove it. Do not infer HDR,
VRR or OLED capability from a 4K resolution or refresh rate.

In **Games → Game details → Launch profile → HDR readiness**:
- **Use Windows settings** is the default; existing game profiles are unchanged.
- **Prefer HDR (warn only)** reports whether HDR is ready but still starts
  the game when it is off/unknown. Windows color state is untouched.
- **Require HDR already active** needs an exact connected monitor selected
  in the profile and **refuses before AI hard-off or executable launch** when
  Windows does not positively report HDR on THAT monitor. Rechecks immediately
  before process start in case a cable/OS state changed. It does NOT enable HDR
  and does NOT prove the game actually rendered HDR.

Console → Display Settings now shows the per-monitor HDR/WCG observation.
Games → Devices → Display Modes now targets the **selected Console TV**, not
always the primary monitor; if the TV is missing or unassigned PaRuX does
NOT silently change another screen. While HDR or WCG is active, direct
resolution/Hz mutation is disabled until a coordinated crash-safe restore
can preserve color state. The existing SDR mode-change feature still uses
its 15-second confirm/revert transaction.

Automatic HDR toggle/restore, Windows Auto HDR, VRR, 10-bit output, tone
mapping and Sunshine/Moonlight HDR streaming are NOT implemented by these
changes. Physical TV/eGPU/controller testing is still pending. See
[HDR & Playnite comparison](../validation/GAMING_HDR_PLAYNITE_PARITY.md)
and [post-launch policy](../validation/GAMING_POST_LAUNCH_OWNED_WINDOW.md).

## Features by status

| Feature | Status |
|---|---|
| Library with 2,000 synthetic titles (virtualized) | Verified on the real app; cover-size control and grouping not built |
| Store discovery (Steam, Epic, GOG) | Unit-tested; real scan NOT RUN |
| Per-game post-launch foreground / Alt+Enter / center-click | Source-only, opt-in for directly verified manual games; native Windows test pending |
| Quick Resume (pause a launched game) and per-game save backup | Source and tests; see the plan for current evidence |
| HTTPS and BitTorrent downloads with SHA-256 | Experimental; no peer-swarm proof, no unattended installers |
| Drop-in ZIP mods and AI asset studios | PARTIAL; no real game loading a ZIP was run |
| Controller input | Read-only detection; physical button presses NOT RUN (hardware-presence tests are simulated) |
| Console TV routing and per-game monitor selection | Source implemented; selected-TV mode editor and HDR/WCG indication not Windows-verified |
| HDR/WCG read-only detection and per-game System/Prefer/Require | Source implemented, physical TV and native x64 CCD acceptance NOT RUN |
| Automatic HDR ON/OFF, crash-safe restore, VRR and HDR streaming | MISSING; tracked in issue #11 |

PaRuX does not bypass DRM, anti-cheat or store terms, and does not provide game piracy sources.
Console / TV presentation: [CONSOLE_MODE.md](CONSOLE_MODE.md). Problems: [TROUBLESHOOTING.md](TROUBLESHOOTING.md).
