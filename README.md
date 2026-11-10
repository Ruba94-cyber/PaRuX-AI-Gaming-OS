# PaRuX AI & Gaming OS

<p align="center">
  <img src="src/PaRuX.App/Assets/px-orbit-app.png" alt="PaRuX AI &amp; Gaming OS" width="200">
</p>

<p align="center"><strong>Your local AI workspace and gaming console home, powered by your own PC.</strong></p>
<p align="center">Local models, smart routing, chat, creative tools and agents, plus a game library and Console / TV mode — in one Windows application.</p>

<p align="center">
  <img src="https://img.shields.io/badge/status-BETA-orange" alt="Beta">
  <img src="https://img.shields.io/badge/platform-Windows%2010%2B-0078D6" alt="Windows 10+">
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-FSL--1.1--MIT-blue" alt="FSL-1.1-MIT license"></a>
</p>

> **BETA — v4.1.0 pre-release published 10 November 2026.** The 4.0.1 stable-channel release is still the default GitHub `latest` until the owner promotes 4.1.0. PaRuX is an experimental project, **not yet a stable release**. Features and interfaces may change, some capabilities are still being tested on real hardware, and bugs are possible. Please use it with this in mind and [report issues](https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/issues).

## What is PaRuX?

**PaRuX is a local-first AI workspace for Windows**, evolving toward an integrated AI and gaming experience. It brings different AI models and workflows together without making you manage a separate app for every task. Your own PC handles local inference; external AI services are **optional and disabled by default**. “OS” describes the intended integrated experience; PaRuX runs on Windows and does not replace it. An early, opt-in fullscreen Console/TV presenter and a consent-based manual game library are implemented in the source, with read-only Steam/Epic/GOG discovery, an opt-in combined library scan and paginated browsing of detected titles, and explicitly confirmed Steam/GOG GALAXY launch requests plus an Epic Windows-protocol activation request (actual game start is not verified; manual Return to AI required; not yet Windows-tested); the complete controller-first Console Shell, universal game library and store installation workflows remain in development and have not passed Windows acceptance.

## What can you do?

- **Chat with local AI:** streaming conversations, images and documents, saved chats, projects and knowledge search.
- **Discover and manage models:** explore Hugging Face, download models with progress and resume, and link models from compatible local tools without copying them.
- **Choose the right model automatically:** hardware-aware suggestions, smart routing for chat, coding and other tasks, or manual model selection when you want full control.
- **Create in the same app:** experimental image generation and editing, video, music, speech, background removal and 3D workflows, including media requests directly from Chat.
- **Work with coding agents:** plan, implement, review and test changes with task progress and reviewable results.
- **Use Computer Use:** run approval-aware Windows tasks rather than giving an AI unrestricted desktop access.
- **Connect your tools:** optional file and knowledge sources, Skills, MCP, workflows and a local API compatible with popular AI clients.
- **Keep control of performance:** runtime suggestions, local benchmarks, model optimization and a Gaming Mode to reduce competition for PC resources. The desktop Games view also offers explicit SHA-256 inspection of local installers; this never executes or installs files and does not establish their safety. The Games/Console home now provides a user-confirmed Return-to-AI action without closing the game, and an explicitly requested read-only XInput/display diagnostic view; real Windows acceptance is still pending. The Games workspace also includes source-implemented, on-demand display and XInput diagnostics (not Windows-verified). Voice input and speech warmup are disabled when Gaming Mode is active by default; owned speech-process shutdown and GPU resource release remain under validation.
- **Manage and optionally deploy game downloads (experimental):** HTTPS, user-supplied v1 magnet/.torrent via integrated BitTorrent (single-file or multifile packaged as a ZIP), queue/pause/progress, and opt-in **verified portable ZIP** extraction + local-library registration after download. The owner must supply a separate SHA-256, chosen ZIP name, relative game EXE and title before enqueue. No unattended setup.exe/MSI/script execution, unauthorized source catalogue, DRM bypass or automatic game launch. Source WIP; Windows tests are pending.
- **Optional reversible ZIP mod delivery (source WIP):** after generating and reviewing a PNG overlay, PaRuX can place its own verified ZIP into an **existing, user-selected** `mods`, `resourcepacks` or `addons` folder ONLY when the owner confirms that a specific offline game's loader accepts that exact standalone ZIP format, chooses the actual local game executable and confirms the game is closed. PaRuX independently checks whether that selected executable is still running (including Quick Resume suspension), then checks again immediately before ZIP publication or deletion; differently named helper processes cannot be verified universally. It writes a new unique filename without overwriting existing mods, stores a pre-copy receipt and removes only matching SHA-256 bytes after a second explicit approval. Installed-mod history is accessible in Gaming Home. This does **not** imply universal mod support, automatic texture/model hot reload or compatibility with any specific game. Windows acceptance remains unrun.
- **Game-independent Local AI Asset Mod Studio (source WIP):** available directly from Gaming Home, not dependent on Minecraft or on pausing a process. Import a real PNG from an authorized offline game, enter a documented virtual replacement path for a compatible mod/resource loader, describe a colour/material change, and preview a dimension/alpha-preserving candidate generated with PaRuX's LOCAL image engine. After separate approval, export a bounded ZIP holding only the replacement PNG and a manifest; source assets, game files and executables remain untouched. Explicit adapter capability listing distinguishes manual PNG overlays, game-specific skins and temporary config patches. **Does not imply compatibility, automatic installation, universal hot reload or game-model editing. Windows build/GPU/mod-loader acceptance remain unrun.**
- **Local AI Minecraft Skin Studio (source WIP):** two local-model workflows: (1) import a 64×64 existing Java skin, then recolour just head, torso, arms, legs or the full character while preserving Classic/Slim UV geometry, existing unselected pixels and transparency; (2) experimental generation of a new candidate atlas from scratch. Review before consent-gated PNG export. Nothing changes Minecraft files/accounts automatically; artistic quality, Windows build and real import remain unverified. Generation stays off in Gaming Mode.
- **AI temporary game mods (experimental):** when a PaRuX-managed offline portable game is actually paused with Quick Resume, local AI can request a human-approved text config proposal (.ini/.cfg/.json/.txt). The user compares the original and proposed text, explicitly applies it with a saved rollback receipt, and can later restore the original. Automatic restore after the original process exits is implemented in source with conflict-safe SHA checks, but does not guarantee live in-game reload or work with anti-cheat/memory edits. The feature is currently **uncompiled source WIP**.
- **Access it your way:** Windows desktop first, plus an optional browser client and an Android companion for supervising the Windows host.

Available features depend on your installed models, runtimes and hardware. **Not every feature or model will work on every PC during beta.**

## Corporate Lite: fully offline requires the right package

The **Corporate Lite with Qwen3-4B** package includes a CPU `llama.cpp` runtime and the starter GGUF for offline text chat. The separately distributed **NoModel** variant does **not** contain a chat model; installing it alone is not a complete standalone chat setup. Neither variant is enterprise-certified. For a verified document-search build, the local embedding ONNX model is required, and a cross-encoder ONNX reranker can now be bundled using `-RerankerDir` / `-Fetch` plus `-RequireReranker` during packaging. Files, runtime and model licences must be reviewed before distribution.

**Validation still pending on the current source:** a clean 8–16 GB CPU-only Windows PC, real offline document ingestion + search + source-grounded response, machine networking/egress, failure recovery, signed binaries and full regression testing. The `-EnforceAcceptance` mode in `tests/corporate/measure-corporate-lite.ps1` explicitly rejects missing dependencies and non-functional real-chat/RAG smoke on a fresh extracted package. A passing source check or HTTP 200 is not proof of functional RAG. See [release readiness](docs/RELEASE_READINESS.md) and the [canonical execution plan](PARUX_EXECUTION_PLAN.md).

## Getting started

1. **Check [GitHub Releases](https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/releases)** for a Windows build. If no release assets are listed, **no public installer is available yet**.
2. If you want to try the source version, follow the [Windows build and setup documentation](docs/RELEASING.md). The project currently requires a Windows development environment for source builds.
3. Launch PaRuX, review the suggested local setup, and choose which model or runtime to install. **Downloads require your confirmation.**
4. Start with Chat, or explore Model Hub, Create and the other workspaces as your hardware allows.

**Windows requirements:** Windows 10 or newer (x64). Building from source requires the .NET 8 SDK and the documented Windows build prerequisites. A dedicated GPU is strongly recommended for demanding models and media generation; exact RAM/VRAM requirements vary.

**Android companion:** an optional controller for PaRuX running on Windows, **not** a standalone mobile inference engine. See [Android setup](docs/PARUX_ANDROID_README.md).

## Local-first, with choices

- **Local inference comes first.** You do not need an OpenAI, Anthropic or other paid cloud API for normal local use.
- **Cloud fallback is off by default** and only available when you explicitly configure it.
- **Models and runtimes are not silently downloaded or switched**: suggestions are yours to accept or decline.
- **Internet features are optional**, except when you choose actions such as downloading models, checking updates or doing online research.
- **Remote browser access is opt-in.** The desktop PC remains the execution host; see [local gateway and integrations](docs/UNIVERSAL_GATEWAY.md).

## Project status

**PARTIAL — v4.1.0 beta published; later source changes still require native Windows validation and PHYSICAL_PC_ACCEPTANCE_REQUIRED.** The canonical status and the open work (the BIC-01 to BIC-17 backlog) live in [PARUX_EXECUTION_PLAN.md](PARUX_EXECUTION_PLAN.md). Earlier "software complete" markers in the documentation are dated historical evidence, not the status of the current code. The Release build and the test classes touched in the latest fixes pass; the full xUnit suite and the FlaUI UI tests were NOT RUN for this statement.

## Historical validation matrix (5 November 2026 — superseded by the 10 November 4.1.0 evidence)

Status refers to a **specific test or delivery**, not to the entire product. Earlier PRs have been merged onto `main`; the latest source-only increments, including GOS-13, are not compiled, run or release-verified.

| Capability / evidence | What is established | What remains |
|---|---|---|
| Latest published release | **4.1.0 beta pre-release** (4.0.1 remains the stable-channel GitHub `latest`) | Built and signed on the owner's Windows PC; see GitHub Releases |
| Baseline Server tests (before PR merges) | **Auto-tested on Windows**: 12,410 passed, 9 skipped (handoff dated 5 Nov 2026) | Newly merged PR tests are **Authored-not-run**, must not inherit prior evidence |
| FlaUI desktop | **Verified on Windows**: Smoke 34/34. Prior full suite 270/283; 11 failures rerun passed after the bundled Core was refreshed | Run the **complete** Regression suite again by itself against the final source |
| Coding diff and safe permissions (L2) | Main runtime/sandbox exists; **Merged source, authored-not-compiled** PR #1 adds strict metadata-checked per-hunk review, SHA-256 preview and OS + all-mutation-tools Auto admission | Rejects unknown metadata, unreviewable sections, binary/rename/add/delete; no transactional apply, UI or Windows tests |
| Model comparison / personas (L3) | Arena/PersonaService on main; **Merged source, authored-not-compiled** PR #2 adds owner-only Core POST/GET, DTO/client, IT/EN statuses, advanced-only Chat Compare button, shared-prompt side-by-side dialog with refresh/cancel, and conservative VRAM preview policy | **FlaUI Smoke, HTTP security and focused xUnit authored-not-run**; no Windows UI, GPU/model-fit or 8 GB reference hardware proof; normal Chat routing unchanged |
| Local game knowledge and GPU guard (L8) | Existing document Chat and Gaming hard-off; **Merged source, authored-not-compiled** PR #3 now verifies selected source IDs against independently supplied eligible local index IDs and blocks GPU admission for unknown game state | No Chat UI or index wiring; **Needs physical HW** for GPU isolation proof |
| Auto-suspend and controllers (L6/L7) | Existing Quick Resume, virtual-pad and HidHide source; **Merged source, authored-not-compiled** PR #4 adds per-game suspend/debounce policies (3/5/10s, verified-process resets), anti-cheat guards and controller previews | **Needs physical HW**: event/sensor sampling, anti-cheat detection, device recovery; no XOutput-equivalence claim |
| External library, wishlist and compliance (L4/L5/L9) | PR #5 metadata preview plus **new uncompiled main WIP**: local Steam wishlist with owner-only settings and bilingual UI; explicit one-shot price quote reuses Discover's opt-in, rate limit, HTTPS allow-list and Gaming hard-off | Windows build/UI/security tests and real-world Store JSON not verified. A requested country is **not** a verified Steam account region. No automatic price polling, alert delivery, purchases or complete i18n/accessibility validation |
| Integrated BitTorrent (GOS-13) | **Main source WIP, not compiled/tested:** MonoTorrent 3.0.2, source-selected v1 magnets/local torrents, verified bounded staging, safe multifile-to-ZIP packaging, no unattended seeding; DPAPI-encrypted native-job recovery shown paused on restart | Verify Windows NuGet/build, real peer swarm, piece rehash/restart, pause/cancel/Gaming hard-off, bandwidth/disk-full/cleanup, FlaUI and provider integrity. Raw multifile folder deployment and unattended installers remain unavailable |
| Corporate Lite | Offline AI-only CPU runtime; model-inclusive 4B and NoModel package variants; source now permits local ONNX reranker, requiring rebuilt package | Low-end 8 GB PC, complete real document answer and every-new-commit Windows gates **not established** |

Merged source PRs: [L2 PR #1](https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/pull/1), [L3 PR #2](https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/pull/2), [L8 PR #3](https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/pull/3), [L6/L7 PR #4](https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/pull/4), [L4/L5/L9 PR #5](https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/pull/5). See [HANDOFF.md](HANDOFF.md) for the independent Windows gate and [PARUX_EXECUTION_PLAN.md](PARUX_EXECUTION_PLAN.md) for status lines. All merged source remains local-first; no new model/driver downloads or game piracy support.

## Beta limitations

- Development and acceptance testing are ongoing; **this is not production-ready software**.
- The current media paths have been exercised on a Windows PC with an NVIDIA GPU. Other GPUs, CPU-only systems and low-VRAM configurations have not received equivalent physical-PC validation.
- Hands-free microphone input has not yet been confirmed in live use on the reference PC.
- Some advanced model families, training options and workflows are not implemented yet. See the [changelog](CHANGELOG.md) and [project roadmap](PARUX_EXECUTION_PLAN.md) for the current status.
- Linux and macOS are **not supported** as PaRuX desktop platforms.

## More information

- [Changelog](CHANGELOG.md) · [Roadmap](PARUX_EXECUTION_PLAN.md) · [Local AI setup](docs/AI_LOCAL_SETUP.md)
- [First-party dependency strategy](docs/DEPENDENCY_STRATEGY.md) · [Third-party notices](THIRD_PARTY_NOTICES.md) · [Code provenance](OPEN_SOURCE_PROVENANCE.md)
- [Architecture](docs/ARCHITECTURE.md) · [Android companion](docs/PARUX_ANDROID_README.md) · [Universal AI gateway](docs/UNIVERSAL_GATEWAY.md)
- [Report a bug or suggest an improvement](https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/issues) · [Contributing](CONTRIBUTING.md) · [Security](SECURITY.md)

## License

PaRuX is **source-available** under [FSL-1.1-MIT](LICENSE), **not an OSI-approved open-source license**. Each released version converts to MIT two years after its release. Third-party models and runtimes retain their own licenses.

## Support PaRuX

PaRuX is free to use, with **no required subscription or paid feature unlocks**. If you enjoy the project, you can support its continued development with an entirely optional donation.

[![Donate with PayPal](https://img.shields.io/badge/Donate-PayPal-00457C?logo=paypal&logoColor=white)](https://www.paypal.com/donate?business=paolo.ruba.1913%40gmail.com&no_recurring=0&item_name=Support+PaRuX&currency_code=EUR)

Thank you for supporting PaRuX — donations never unlock or restrict features.
