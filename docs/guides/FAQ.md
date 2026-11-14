# FAQ

**What is RBG – PaRuX AI & Gaming OS?** A local-first Windows application combining a local AI workspace with a game library and an early Console / TV mode.
It runs on Windows and does not replace it. Status: BETA.

**Does it need the cloud?** No for core inference; it runs on your PC. Cloud fallback is off by default and only used if you configure it.
Downloads, update checks and web research happen only when you start them. See [PRIVACY.md](PRIVACY.md).

**Which PC do I need?** Windows 10 or newer, x64. See the [requirements](../../README.md#system-requirements). More RAM and VRAM allow larger models.

**Does it work without a GPU?** Small models run on the CPU (Corporate Lite is CPU-only), but slowly: about 13 tokens per second with a 4B model on the
reference Ryzen 7 8840U. Other CPU-only and low-VRAM systems have not received equivalent validation.

**Is it a stable release?** No. 4.1.0 is a beta pre-release. See [PARUX_EXECUTION_PLAN.md](../../PARUX_EXECUTION_PLAN.md) for per-area status.

**Which models can I use?** GGUF models through the llama.cpp runtime, found in Model Hub (Hugging Face), imported, or linked from compatible local tools.
Not every model works on every PC.

**Will the AI slow my game?** Gaming Mode stops PaRuX AI processes while a game runs. In-game FPS impact was **not measured**.

**Can I run it from a USB drive?** The Portable ZIP is designed to be relocatable; use NTFS. See [PORTABLE.md](PORTABLE.md).

**Is there a Corporate version with roles and an audit log?** No. Corporate Lite is AI plus documents, offline, and PARTIAL; roles, per-user
policies and an audit log do not exist. See [CORPORATE.md](CORPORATE.md).

**Is it open source?** Source-available under FSL-1.1-MIT (not OSI-approved); each version converts to MIT two years after its release. See [LICENSE](../../LICENSE).

**Is it free?** Yes, with no required subscription. Donations are optional and unlock nothing.

**Does the Android app run models?** No, it supervises a paired Windows PC. See [Android setup](../PARUX_ANDROID_README.md).

**Linux or macOS?** Not supported.

**Where do I get help?** [TROUBLESHOOTING.md](TROUBLESHOOTING.md), then [GitHub Issues](https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Gaming-OS/issues).
