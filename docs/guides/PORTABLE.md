# Portable use

The Portable ZIP runs RBG – PaRuX without installing it. Short name: RBG – PaRuX. Status: BETA.

## Use it

1. Extract the ZIP to a writable **NTFS** folder (any drive). Removable FAT/exFAT drives are not certified.
2. Double-click `PaRuX.exe`. The optional `PaRuX (portable data).cmd` starts the same app with the same data location.
3. A `parux.portable` marker next to the executable, plus an adjacent `PaRuXData` folder, switches the app into portable mode.
   Chats, settings, models and indexes stay in `PaRuXData`, not in your Windows profile.

You can move the extracted folder to another path or drive; the 4.1.0 record shows the app starting healthy after a move with
`settings.json` byte-identical.

## What is included

- Self-contained desktop app and Core (no .NET, Windows App Runtime, Python, Ollama or LM Studio needed for the shell).
- The **4.1.0** Portable ZIP bundles the Vulkan llama.cpp runtime and **no model**: download or import one in Model Hub
  (see [README](../../README.md#set-up-a-model)).
- The **unreleased** Standard Portable builder (`scripts/build-portable.ps1 -FetchAssets -Smoke`) also bundles a CPU runtime, a Qwen3-4B
  GGUF starter and ONNX embedding and reranker models. That build has not passed fresh-machine tests and is not part of the published 4.1.0 beta.

## Not included

Windows drivers, game stores, games, and optional image/video/training engines (installed on request). In 4.1.0 the Portable
has no CUDA or media pack, no terminal CLI and no in-app updater.

## Update

Download the new ZIP, replace the application files, keep `PaRuXData`.

## Limits

- The Portable ZIP is **not Authenticode-signed**; SmartScreen and antivirus rules still apply (no bypass).
- Settings persistence across a move was shown by file identity, not by changing a setting in the UI.
- Other GPUs and SmartScreen behaviour were not verified. Source: [release record](../RELEASE_CANDIDATE_4.1.0.md).

For offline office use see [CORPORATE.md](CORPORATE.md). For problems see [TROUBLESHOOTING.md](TROUBLESHOOTING.md).
