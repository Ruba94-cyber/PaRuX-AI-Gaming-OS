# Store screenshots and artwork to produce

**Status 2026-11-14:** 9 of the 10 screens are captured in `screenshots/` (`en/` 1-7, `it/` 9 and 10), 1920x1080 PNG, synthetic data only, from the Debug app run in Store-edition mode on a Radeon 780M PC. They were NOT taken from the installed Store package: retake them there before submission if the package UI differs. Screen 8 (Update Manager "managed by the Microsoft Store") needs the installed package and is still open; Italian versions of 2 and 4 are not published because those screens still show English catalogue text.


None exist in the repository. Capture from the **installed Store package** (not a Debug build) on the reference PC with a small model installed. PNG, 1920x1080 preferred (minimum 1366x768; Partner Center accepts several, keep 4 to 10). English UI first, then Italian UI for the it-IT listing. Use only synthetic documents and synthetic chat text: the repository is public and the screens go on a public page. No real names, paths with a user name, e-mail addresses, company files or game accounts. Hide the taskbar clock and notifications.

| # | Screen | Shows | Needed for | Status |
|---|---|---|---|---|
| 1 | Chat home with a streamed answer from a local model | Core value, local model name visible | EN + IT | serve screenshot |
| 2 | Document question with the source shown (synthetic folder) | RAG with sources | EN + IT | serve screenshot |
| 3 | Refusal when the answer is not in the documents | Honest behavior | EN | serve screenshot |
| 4 | Model Hub with a model list and the download confirmation | Downloads only on consent | EN + IT | serve screenshot |
| 5 | First-run model setup (Detect / Locate existing / Install with PaRuX / Skip) | No model bundled, user decides | EN | serve screenshot |
| 6 | Game library with a game launch prompt and Gaming Mode state (use a free/open-source test game, no store accounts visible) | Supports the "Gaming" part of the name | EN | serve screenshot |
| 7 | Settings showing local-first/privacy options, LAN pairing off | Privacy claims | EN | serve screenshot |
| 8 | Update Manager showing "managed by the Microsoft Store" | Proves no self-updater | EN | serve screenshot |
| 9 | Italian UI of screen 1 | it-IT localization | IT | serve screenshot |
| 10 | Italian UI of screen 4 or 6 | Checks there is no mixed-language screen | IT | serve screenshot |

Rules: every screenshot must show something the product really does in the Store edition (no torrent, no runtime-download screens, no donation or purchase UI). If a screen shows an English string in the Italian UI, fix the app or drop the screenshot and keep the "some screens may show English" sentence.

## Store artwork

| Asset | Spec | Source | Status |
|---|---|---|---|
| Store logo 1:1 | 300x300 PNG | `assets/store-logo-1x1-300x300.png` (from `prx-orbit-approved.png`) | pronto |
| Poster art 2:3 and box art 1:1 | optional, 720x1080 / 1080x1080 | `assets/poster-art-2x3-720x1080.png`, `assets/box-art-1x1-1080x1080.png` | optional, pronti |
| Hero images / trailers | optional | none | skip |

Do not use third-party logos (Hugging Face, Steam, llama.cpp, game covers) in any screenshot or artwork without permission (policy 10.1.1: no claim of relationship with other companies).
