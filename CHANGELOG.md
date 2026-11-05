# Changelog

### Unreleased proposals (2026-11-05) — Authored-not-compiled, NOT merged

The following code is in separate **draft PRs**, not on main or in a published build. All new tests are **Authored-not-run**; no release 3.1.0 has been cut.

- **L2 PR #1:** strict same-file Git parser refuses unsupported changes and zero-hunk file sections, with SHA-256 in-memory preview; auto approval additionally requires verified OS sandbox **and** all mutation tools confined. No actual executor/UI or tests run.
- **L3 PR #2:** authenticated two-model Arena Core POST/GET and typed client/DTO with IT/EN status labels, strict model names, Core 4,000-character prompt limit and conservative free-VRAM media preview floor. No Chat control or hardware proof.
- **L8 PR #3:** selected local game guides must match separately verified indexed-source IDs (empty/unknown scopes rejected), and unknown Gaming telemetry blocks GPU admission; no live Chat/OS integration or tests run.
- **L6/L7 PR #4:** per-game suspend proposals with anti-cheat exclusions and monotonic continuity tracking (focus/battery/thermal; reset on changed process, rule edits or missing samples), controller diagnostic/remap previews and a typed consent-gated, PaRuX-owned undo plan; tests authored-not-run; **Needs physical HW**.
- **L4/L5/L9 PR #5:** read-only library JSON previews, default-off wishlist and public price parser with mandatory matching store/product/currency/country/freshness for eligibility; offline license and IT/EN audit scripts. No persisted monitoring, public API fetching, region proof or notifications.
- **Docs PR #6:** candid roadmap/README/handoff of current verified vs uncompiled work; Windows build, targeted xUnit, solo FlaUI regression and physical checks required before merging any of the proposals.


### 2026-11-05 - Gaming home: hero, filter rail, save restore and launch-helper UI, IGDB covers, Quick Resume status

- **Games home:** hero for the last played startable game (Play still goes through the launch consent), filter rail with counts (all, favourites, recently played, installed / not installed, store, genre), keyboard / D-pad / bumper movement between tiles, stable AutomationIds. EA app / Battle.net / Ubisoft / Xbox titles are labelled "unverified" (read-only discovery, not yet checked on a real install: **NEEDS PHYSICAL HW**).
- **Game details:** per-game save backup + **restore** (preview, confirmation, automatic pre-restore copy, SHA-256 re-check, plain-language refusals) and per-game **launch helpers** (off by default, .exe only, confirmed, never during Gaming).
- **Covers:** optional **IGDB** source with the user's own Twitch credentials (DPAPI, never in settings); per-game cover override unchanged. IGDB parsing is tested with synthetic JSON only, **not against the live API**.
- **Quick Resume:** status line on the Games home and the start-up recovery results (resumed / failed) in its panel.
- Tests: new xUnit (home rules, restore/hook wording, IGDB parsing) and FlaUI `GamingHomeCompetitiveUiTests` (isolated data root, synthetic games).

### 2026-11-04 — Virtual controller: guided activation, verified HidHide binding, per-game profiles, hot-plug
