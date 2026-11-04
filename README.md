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

> **BETA — Active development (2.0.0).** PaRuX is an experimental project, **not yet a stable release**. Features and interfaces may change, some capabilities are still being tested on real hardware, and bugs are possible. Please use it with this in mind and [report issues](https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/issues).

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
- **Access it your way:** Windows desktop first, plus an optional browser client and an Android companion for supervising the Windows host.

Available features depend on your installed models, runtimes and hardware. **Not every feature or model will work on every PC during beta.**

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

**PARTIAL — active software gaps; PHYSICAL_PC_ACCEPTANCE_REQUIRED.** The canonical status and the open work (the BIC-01 to BIC-17 backlog) live in [PARUX_EXECUTION_PLAN.md](PARUX_EXECUTION_PLAN.md). Earlier "software complete" markers in the documentation are dated historical evidence, not the status of the current code. The Release build and the test classes touched in the latest fixes pass; the full xUnit suite and the FlaUI UI tests were NOT RUN for this statement.

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
