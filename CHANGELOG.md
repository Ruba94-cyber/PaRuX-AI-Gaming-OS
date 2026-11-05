# Changelog

### Unreleased proposals (2026-11-05) — Authored-not-compiled, NOT merged

The following code is in separate **draft PRs**, not on main or in a published build. All new tests are **Authored-not-run**; no release 3.1.0 has been cut.

- **L2 PR #1:** per-hunk diff review, corrected Git prefix parser, SHA-256 preimage-bound selective preview, sandbox-gated approvals and bounded repair policy; **no live patch executor or UI wiring**.
- **L3 PR #2:** exact two-model Arena request (4,000-character Core prompt cap), truthful side-by-side presenter, simple Auto explanation and estimated media presets; existing PersonaService reused.
- **L8 PR #3:** consent-scoped game-guide Chat policy (max 64 valid indexed source IDs, empty scope refused), game-first GPU policy; no Chat UI/runtime integration.
- **L6/L7 PR #4:** per-game suspend proposals with explicit anti-cheat and custom process exclusions, controller diagnostic/remap previews and PaRuX-owned undo planner; **Needs physical HW**.
- **L4/L5/L9 PR #5:** limited read-only game-library export parsers, opt-in local wishlist and store/product/currency-validated public price parsing, dependency-notice coverage and partial IT/EN audit; no API polling, notifications or UI integration.
- **Docs PR #6:** candid roadmap/README/handoff of current verified vs uncompiled work; Windows build, targeted xUnit, solo FlaUI regression and physical checks required before merging any of the proposals.


### 2026-11-05 - Gaming home: hero, filter rail, save restore and launch-helper UI, IGDB covers, Quick Resume status

- **Games home:** hero for the last played startable game (Play still goes through the launch consent), filter rail with counts (all, favourites, recently played, installed / not installed, store, genre), keyboard / D-pad / bumper movement between tiles, stable AutomationIds. EA app / Battle.net / Ubisoft / Xbox titles are labelled "unverified" (read-only discovery, not yet checked on a real install: **NEEDS PHYSICAL HW**).
- **Game details:** per-game save backup + **restore** (preview, confirmation, automatic pre-restore copy, SHA-256 re-check, plain-language refusals) and per-game **launch helpers** (off by default, .exe only, confirmed, never during Gaming).
- **Covers:** optional **IGDB** source with the user's own Twitch credentials (DPAPI, never in settings); per-game cover override unchanged. IGDB parsing is tested with synthetic JSON only, **not against the live API**.
- **Quick Resume:** status line on the Games home and the start-up recovery results (resumed / failed) in its panel.
- Tests: new xUnit (home rules, restore/hook wording, IGDB parsing) and FlaUI `GamingHomeCompetitiveUiTests` (isolated data root, synthetic games).

### 2026-11-04 — Virtual controller: guided activation, verified HidHide binding, per-game profiles, hot-plug
