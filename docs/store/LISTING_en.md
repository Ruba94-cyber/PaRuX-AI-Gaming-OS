# Microsoft Store listing - English (en-US)

This file mirrors, character for character, the text entered in the Partner Center draft of product 9MWV6SWW7Q4C (submission 1, en-US listing), read in read-only mode on 2026-11-14. Nothing has been submitted. Policy context for the price, the free build and donations: `POLICY_AND_CERTIFICATION.md`. Price: `PRICING_AND_AVAILABILITY.md`. The evidence table at the end is for the owner only; it is not entered in Partner Center.

## Product name (as reserved in Partner Center)

RBG-PaRuX AI & Gaming OS

## Short description

Chat with AI models and ask questions about your own files, running on your own PC.

## Description

RBG-PaRuX AI & Gaming OS (PaRuX) is a local-first AI workspace for Windows. Chat with language models that run on your own PC, and ask questions about the documents you choose.

Important to know before you buy or install:
- PaRuX needs an AI model to answer. The Store package contains the app and a local inference engine, but no model files. The first time you chat, PaRuX helps you choose a model and downloads it only after you say yes (models are large, often several GB).
- The Microsoft Store edition includes the inference engine and never downloads or installs programs: no extra engines, no Python tooling and no installers. Only AI model files and other data are downloaded, and only after you say yes. The built-in torrent engine and game-archive downloads are not part of this edition. You can still point PaRuX at software you installed yourself.
- Updates for the app come from the Microsoft Store.
- Speed depends on your PC. A computer with a recent GPU or enough memory runs bigger models better.
- This is an early release. Some features are still being tested on real hardware.

What you can do
- Chat with models that run on your PC. Conversations are saved on your PC.
- Ask questions about your own documents. PaRuX indexes the folders you pick and answers with the source it used, and declines when your files do not contain the answer.
- Find and manage models in the built-in Model Hub, with download progress and resume.
- Launch games already installed on your PC from a game library. Gaming Mode stops local AI processes while a game started from PaRuX is running, to leave memory and the GPU to the game.
- Use PaRuX in English or Italian.
- Optional: connect your own paired devices over your home network (off by default).

Your files and chats are not uploaded to a PaRuX server. There is no PaRuX account, no advertising and no in-app purchase. See the privacy policy for what leaves your PC and when.

About this edition: one price, one time, through the Microsoft Store. Your purchase supports the ongoing development of PaRuX. All versions of PaRuX are also available on GitHub (PaRuX-AI-Gaming-OS). This edition has the same local AI features as the project's other builds, except the items listed above that the Store edition leaves out (executable downloads, torrent engine). PaRuX is source-available under the FSL-1.1-MIT license.

Notice: PaRuX uses the llama.cpp inference engine (MIT license), included in the package.

## What's new in this version

Version 4.3.1:
- Chat: generation settings and saved presets, edit-and-resend of your own messages, a calmer empty state and a responsive composer.
- Game library: smart collections, a compact top option, achievements from Steam (online editions only), and real downloads with pause, resume and checksum.
- Controllers: live view, a guided mapping wizard and an Xbox pad without a driver for games that use XInput.
- Settings: plugin list, a tamper-evident activity log, per-device read-only access and webhooks (not in Corporate Lite).
- Corporate Lite stays fully offline.

Version 4.3.0:
- Game library: separate Library, Downloads, Add & import, Emulators and Gaming settings pages; "Pick for me", library insights, notes per game, export and restore of your library data, and import from a Playnite library on your PC.
- Emulators: more than 100 known emulators can be set up with detection, ROM folder scan and saved settings; the programs themselves are never downloaded by PaRuX.
- Chat: a prompt library with fill-in fields, a Ctrl+K command palette, export of all chats as readable files, and a suggested first model that suits your PC.
- Fixes: the game library no longer fails when the window is narrowed with a game card open; clearer Italian text; several layout fixes.

## Product features (one per line)

Local AI chat with models you download once and run on your own PC
Ask questions about your own documents, with the source shown and a refusal when the answer is not in your files
Model Hub with resumable downloads that start only when you say so
Game library that launches games already installed; Gaming Mode frees memory and GPU for the game
English and Italian interface
No account, no advertising, no tracking, no in-app purchases
Optional pairing of your own devices on your home network, off until you turn it on
Updates through the Microsoft Store

## Keywords (7)

local AI; offline chat; LLM; private AI; documents; model hub; game library

("llama.cpp" is not used: policy 10.1.3 forbids other products' titles in search terms.)

## Copyright and trademark info

© 2026 Paolo Rubagotti. All rights reserved. Third-party names are the property of their owners.


## Developed by

Paolo Rubagotti

## Notes for certification (Supplemental info > Additional testing information)

No account or sign-in is needed. The package contains the app and a local inference engine but no AI model; to test, start the app, choose "Skip" or pick a small model in the first-run screen and confirm the download (model files are downloaded from public model hosts only after the user confirms). For the document feature, point it at a folder with a small plain-text file. The Microsoft Store edition never downloads or installs executable code (no additional inference engines, no Python tooling, no installers) and has no torrent engine; those routes are refused by the app. The app has no self-updater and shows that updates come from the Store. The app has no in-app purchases, no ads and no payment or donation screen; the Store price is the only payment. runFullTrust is required because the app runs its own local server (PaRuX.Server.exe) and the local inference engine (llama.cpp, MIT license) as child processes, reads and writes files the user chooses, and uses native GPU libraries (Vulkan) that cannot run in an AppContainer. It does not install services or drivers, does not run elevated, registers no startup entries and collects no telemetry. internetClientServer and privateNetworkClientServer are used only by an optional local-network pairing listener that is off by default. The Gaming area only launches games already installed on the PC; it sells and downloads nothing in this edition. Third-party component: llama.cpp (MIT) is included in the package. Open-source notice and source: https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS

Submission options in the draft: publishing hold set to "Don't publish this submission until I select Publish now" (manual release).

## System requirements (Properties page)

Minimum: Windows 10 version 1809 (build 17763) or later, 64-bit (x64); about 1 GB free for the app plus space for the models you download; the app starts and runs small models on CPU.
Recommended: 16 GB RAM (a 4-billion-parameter model peaked at 6.7 GB RAM on the reference PC; 8 GB machines are not yet verified), an SSD, and a GPU with Vulkan support for larger models. These are guidance only; speed on your PC is not guaranteed.

## Category

Primary: Productivity (secondary choice: Developer tools). Not "Games": policy 10.1.1.6 asks for the most appropriate category; the game library is a secondary feature.

## Store logos and screenshots

Logo assets are in `assets/` (`assets/README.md`). Screenshots: `SCREENSHOTS.md`. The draft had 0 screenshots on 2026-11-14; at least one is required.

## Evidence behind the claims (owner only)

| Claim | Evidence |
|---|---|
| Local chat on a downloaded model | Standard Portable acceptance 12/12 (QA-036); real-app local chat end to end (QA-055, Radeon 780M + CPU) |
| Documents with sources, refusal when absent | RAG-01/02, EV-09 on the reference PC |
| RAM figure | EV-06 (6.7 GB peak, 4B model) |
| Store edition: no executable downloads, no torrent, no self-update | `StoreEditionPolicy` tests (QA-038, 224 focused tests); installed package refusals with HTTP 403 (QA-043) |
| Gaming Mode stops local AI while a game runs | QA-055 (Debug build, one game, 3/3; llama-server gone in 362-511 ms) - PARTIAL, re-check on the Store package |
| No in-app purchases, no donation UI in the app | source search: no Store purchase API and no donation link in `src/` |
| English and Italian UI | localization batch4 gate; verify the Italian screenshots by eye (no mixed screen) |
| Data roots under package identity | QA-043 |

Not verified on the packaged install: the window itself, chat with a downloaded model, update through the Store (see `SUBMISSION_CHECKLIST.md` section C).
