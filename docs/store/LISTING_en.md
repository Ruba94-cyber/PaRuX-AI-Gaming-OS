# Microsoft Store listing - English (en-US)

Paste-ready text for Partner Center (Store listing > en-US). Everything in the "Paste" blocks is limited to what the code and `docs/REQUIREMENT_MATRIX.md` / `docs/QA_EVIDENCE.md` show as implemented and tested. No speed numbers, rankings or "best" wording. The evidence table at the end is for the owner only; do not paste it. Policy context for the price, the free build and donations: `POLICY_AND_CERTIFICATION.md`. Price: `PRICING_AND_AVAILABILITY.md`.

The product name must equal the name reserved in Partner Center (build default "PaRuX AI & Gaming OS"). Policy 10.1.1 says a title must not contain marketing or descriptive text; "AI & Gaming OS" is a risk, see `POLICY_AND_CERTIFICATION.md` section 6.

## Title

PaRuX AI & Gaming OS

## Short description (keep under 100 characters; the field accepts more)

Chat with AI models and ask questions about your own files, running on your own PC.

## Description (paste; the field allows up to 10,000 characters)

PaRuX AI & Gaming OS is a local-first AI workspace for Windows. Chat with language models that run on your own PC, and ask questions about the documents you choose.

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

About this edition: one price, one time, through the Microsoft Store. It has the same local AI features as the project's other builds, except the items listed above that the Store edition leaves out (executable downloads, torrent engine). PaRuX is source-available under the FSL-1.1-MIT license; the source is on GitHub.

Notice: PaRuX uses the llama.cpp inference engine (MIT license), included in the package.

## Product features (one per line, each under 200 characters)

Local AI chat with models you download once and run on your own PC
Ask questions about your own documents, with the source shown and a refusal when the answer is not in your files
Model Hub with resumable downloads that start only when you say so
Game library that launches games already installed; Gaming Mode frees memory and GPU for the game
English and Italian interface
No account, no advertising, no tracking, no in-app purchases
Optional pairing of your own devices on your home network, off until you turn it on
Updates through the Microsoft Store

## Search terms (max 7; 30 characters each)

local AI; offline chat; LLM; private AI; documents; model hub; game library

("llama.cpp" was removed: policy 10.1.3 forbids using other products' titles in search terms.)

## What's new in this version (4.2.0)

First Microsoft Store release. Local chat, document questions with sources, Model Hub, game library with Gaming Mode, English and Italian interface. App updates now come from the Microsoft Store.

## System requirements (Partner Center minimum / recommended hardware)

Minimum: Windows 10 version 1809 (build 17763) or later, 64-bit (x64); about 1 GB free for the app plus space for the models you download; the app starts and runs small models on CPU.
Recommended: 16 GB RAM (a 4-billion-parameter model peaked at 6.7 GB RAM on the reference PC; 8 GB machines are not yet verified), an SSD, and a GPU with Vulkan support for larger models. These are guidance only; speed on your PC is not guaranteed.

## Copyright and trademark info

(c) 2026 the PaRuX publisher. Third-party names are the property of their owners.
(The owner replaces "the PaRuX publisher" with the publisher display name already in Partner Center; do not store personal data here.)

## Category

Primary: Productivity (secondary choice: Developer tools). Not "Games": policy 10.1.1.6 asks for the most appropriate category; the game library is a secondary feature.

## Website / support / privacy policy URLs

Website: the project's public page (decision, see `POLICY_AND_CERTIFICATION.md` section 2). Support: `SUPPORT.md` content at a public URL or an owner-chosen e-mail. Privacy policy: `PRIVACY_POLICY.md` at a public HTTPS address.

## Notes for certification (paste into "Notes for certification")

See `POLICY_AND_CERTIFICATION.md` section 4 (English text to paste) and section 3 (`runFullTrust` justification).

## Age rating

Answer the IARC questionnaire yourself; ready answers in `POLICY_AND_CERTIFICATION.md` section 5.

## Screenshots

Shot list in `SCREENSHOTS.md`. None are in this repository.

## Evidence behind the claims (owner only, do not paste)

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
