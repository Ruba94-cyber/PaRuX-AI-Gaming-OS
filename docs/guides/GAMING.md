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
| TV routing, HDR/VRR, streaming | MISSING |

PaRuX does not bypass DRM, anti-cheat or store terms, and does not provide game piracy sources.
Console / TV presentation: [CONSOLE_MODE.md](CONSOLE_MODE.md). Problems: [TROUBLESHOOTING.md](TROUBLESHOOTING.md).
