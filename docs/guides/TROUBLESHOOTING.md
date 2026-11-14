# Troubleshooting

RBG – PaRuX (BETA). Review logs for prompts and local paths before sharing them.

| Symptom | Likely cause and what to do |
|---|---|
| Chat does not answer, "AI paused by Gaming" | Gaming Mode is active and is a hard-off. Close the game or use **Return to AI** (chat header, or Settings, Gaming). The previous model reloads on your next request. |
| Request fails but the UI is open | Use Settings diagnostics to tell the PaRuX server from the model runtime. If an image adapter fails, the error is returned as a recoverable chat error. |
| "No candidate fits the current memory" (HTTP 503) | Not enough RAM/VRAM for the chosen model. Close other apps, pick a smaller model or quantization in Model Hub. Corporate Lite used about 7 GB in total on the reference PC. A Corporate-specific false refusal was fixed in source at `e6c3b062`; older packages may still show it. |
| No model installed | Open Model Hub and download or import a model. The 4.1.0 MSI and Portable do not bundle a chat model; the NoModel Corporate package has none either. |
| Model download blocked | Corporate Lite offline refuses downloads (403) by design. Use the IT-approved download launcher, or import a GGUF file. |
| Windows warns "Unknown publisher" | The 4.1.0 MSI uses a self-signed certificate. Verify the SHA-256 hash first ([INSTALL.md](INSTALL.md)). The Portable ZIP is unsigned. |
| Portable app loses data after moving | Keep `PaRuXData` and the `parux.portable` marker next to `PaRuX.exe`. Use NTFS, not FAT/exFAT. |
| Gateway port 11435 is held by another program | A second Core now survives this, but the gateway does not fall back to another port. Free the port. |
| Voice input is off in Gaming | Intended: voice input and speech warm-up are disabled while Gaming Mode is active. |
| Italian UI shows an English message | Some state-machine strings are English only (known; listed in the plan). |
| Document search finds nothing | Wait until indexing of the added folder finishes. OCR for scanned PDFs was **NOT RUN** and is not established. |
| Android app asks to pair again | Re-pair with the Windows PC. The Android app is a companion, not a standalone engine. |

## Checks you can run

- Health: the Core answers `GET /api/v1/health` on a loopback port (endpoint and token are in `server\bootstrap.json` in the data root; never share that file).
- Unsure about network use: `scripts\sample-process-egress.ps1` lists non-loopback connections (polling, so evidence only).
- Build and tests from source: see [AI_LOCAL_SETUP.md](../AI_LOCAL_SETUP.md).

## Report an issue

Use [GitHub Issues](https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Gaming-OS/issues). Include version, package type, Windows version, GPU and RAM, and steps.
Do not attach tokens, bootstrap files or private documents. Security issues: [SECURITY.md](../../SECURITY.md).
See also [FAQ.md](FAQ.md).
