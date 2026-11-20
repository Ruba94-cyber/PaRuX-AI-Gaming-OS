# Backend gap matrix vs competitors (2026-11-20)

Status words follow the Definition of Done: DONE / PARTIAL / MISSING / BLOCKED-EXTERNAL.
Competitor columns come from general knowledge of each product, **not** from re-checking their current releases tonight; treat them as a
prioritisation guide, not a measured comparison. PaRuX status is from this repository (routes in `web-session-route-decisions.txt`,
code search on 2026-11-20).

| Area | Competitors that have it | PaRuX | Notes / next step |
|---|---|---|---|
| OpenAI-compatible local API (chat, completions, embeddings, responses) | LM Studio, Jan, Open WebUI | DONE | `/v1/*` routes plus Ollama (`/api/*`) and Anthropic-style (`/v1/messages`) compatibility |
| Model library, hashed installs, runtime manager | LM Studio, Jan | DONE | Model Hub and Runtime Manager |
| Sampling parameters per request | LM Studio, Jan, Open WebUI | DONE | Core validated them; chat UI added 2026-11-20 |
| Document RAG with citations | Open WebUI, AnythingLLM | DONE | strict gate measured on Corporate Lite |
| Conversation search, export, branches, edit and resend | Open WebUI, ChatGPT-class apps | DONE | all in the desktop client |
| Plugin install with permission diff and trust | Open WebUI (pipelines), Jan (extensions) | DONE | uninstall and list added 2026-11-20 |
| Tamper-evident audit trail | Enterprise tiers of Open WebUI, AnythingLLM | PARTIAL | local hash-chained log added 2026-11-20 (plugins only); other actions, export and retention still to wire |
| Roles and per-user policy (multi-user) | Open WebUI, AnythingLLM | MISSING | single owner model; large change touching auth and every route decision |
| Outbound webhooks / event notifications | Open WebUI | MISSING | needs an off-by-default allow-list, HMAC signing and the existing Internet policy; no PaRuX data leaves without it |
| Scheduled and event-driven automations | Open WebUI (functions), n8n-style tools | PARTIAL | automations exist (disabled by default); event triggers beyond schedule not verified |
| Game library import (Steam, Epic, GOG, local, emulators) | Playnite, Heroic, Hydra | DONE | plus Playnite import and 111 emulator definitions (unverified per emulator) |
| Per-game notes, collections, play time, hidden, favourites | Playnite | DONE | |
| Metadata and covers from online providers | Playnite plugins, Hydra | PARTIAL | IGDB credentials path exists; breadth of providers and offline fallback not measured |
| Achievements | Hydra, Playnite plugins, Steam | MISSING | needs per-store providers; Steam needs the owner's key. Candidate: read-only Steam achievements with the owner's Web API key |
| Cloud-save sync with conflict handling | Hydra (cloud), Heroic, Steam | MISSING | local save backup and restore exist; no remote and no conflict-safe merge |
| Owned-store install, update and repair | Heroic (Epic, GOG, Amazon), Hydra | MISSING | PaRuX deliberately never installs from stores today |
| Downloads: HTTP, torrent, checksum, pause for Gaming | Hydra, Playnite (extensions) | PARTIAL | unit-tested; no peer proof, restart recovery NOT RUN |
| Library scripting or extension API | Playnite (PowerShell and C#), Heroic | MISSING | plugin bundles cover AI skills only, not the game library |
| Virtual Xbox pad and hiding the physical pad (ViGEmBus, HidHide, XOutput) | XOutput, x360ce, DS4Windows, Steam Input | PARTIAL | Driverless replacement for games that read XInput: `native/xinput-shim` (user-mode DLL, forwards to Windows when PaRuX is off), shared-memory backend, installer with receipt and anti-cheat refusal, UI in Gaming > Devices. Not covered: games that read DirectInput/raw HID (needs a kernel driver to hide the physical pad), rumble forwarding, anti-cheat games, a signed 32-bit shim run in a real game |
| Controller mapping | Playnite (fullscreen), Steam Input | PARTIAL | raw mapping and live view; no per-game profiles, no virtual pad without a separately installed driver |
| Cold start | Playnite, Locally Uncensored | PARTIAL | slower on the test PC; see `COMPETITOR_MEASURED_2026-11-19.md` |

## Order of work (backend only, no screen needed)

1. Wire the audit log into the other security-relevant actions (pairing, API keys, Internet policy changes) and add export. Small, testable.
2. Outbound webhooks, off by default, with allow-list, HMAC signature, size and rate limits, and an audit entry per delivery.
3. Per-game controller profiles stored with the game (backend contract and store first).
4. Steam achievements, read-only, behind the owner's own Web API key.
5. Roles and per-user policy: design note first, because it touches every route.

Cloud-save sync and owned-store install stay MISSING until a design that keeps the Store edition free of downloads is agreed.
