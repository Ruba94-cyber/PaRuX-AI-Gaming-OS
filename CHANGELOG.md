# Changelog

### 2026-11-05 - Gaming home: hero, filter rail, save restore and launch-helper UI, IGDB covers, Quick Resume status

- **Games home:** hero for the last played startable game (Play still goes through the launch consent), filter rail with counts (all, favourites, recently played, installed / not installed, store, genre), keyboard / D-pad / bumper movement between tiles, stable AutomationIds. EA app / Battle.net / Ubisoft / Xbox titles are labelled "unverified" (read-only discovery, not yet checked on a real install: **NEEDS PHYSICAL HW**).
- **Game details:** per-game save backup + **restore** (preview, confirmation, automatic pre-restore copy, SHA-256 re-check, plain-language refusals) and per-game **launch helpers** (off by default, .exe only, confirmed, never during Gaming).
- **Covers:** optional **IGDB** source with the user's own Twitch credentials (DPAPI, never in settings); per-game cover override unchanged. IGDB parsing is tested with synthetic JSON only, **not against the live API**.
- **Quick Resume:** status line on the Games home and the start-up recovery results (resumed / failed) in its panel.
- Tests: new xUnit (home rules, restore/hook wording, IGDB parsing) and FlaUI `GamingHomeCompetitiveUiTests` (isolated data root, synthetic games).

### 2026-11-04 — Virtual controller: guided activation, verified HidHide binding, per-game profiles, hot-plug
