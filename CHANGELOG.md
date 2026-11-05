# Changelog

### 2026-11-05 — L5 manual Steam price quote (WIP, not compiled/tested on Windows)

- Added owner-only POST for an explicitly confirmed, one-shot Steam storefront price. The client reuses Game Discover's existing consent setting, fixed HTTPS host, cookie-free client, bounded response, timeout and request limiter; incoming country is only a requested storefront value, not an authenticated account region.
- Game Discover runtime is now cached by host lifetime rather than request-scoped service provider, so its network lock and rate limiter persist across requests. Price results are discarded when Gaming starts, Discover is disabled, or the saved wishlist item changes during the call.
- Gaming Home wishlist now shows an IT/EN **Check Steam price once** action and a clearly labeled indicative quote/threshold comparison. No scheduled polling, notifications, purchases or account login. New synthetic provider, owner-security and FlaUI regression tests are authored but **NOT RUN**; no real Steam API or device test was performed. The Store appdetails pricing endpoint is undocumented and may change.


### 2026-11-05 — Gaming L5: local Steam wishlist (WIP, not Windows tested)

- Added an offline, versioned local wishlist store with bounded file size, cross-process write lease, atomic replacement, corruption refusal, duplicate validation and explicit opt-out on new items. Separate owner-only Core GET/POST/PUT/DELETE APIs never fetch prices, run timers or touch game files.
- Added shared IT/EN desktop Gaming Home wishlist dialog to save numeric Steam app IDs, target price/currency/store country, enable or disable a stored watch **preference by explicit user confirmation**, and delete entries. No price notifications or actual monitoring are implemented.
- New synthetic `LocalWishlistStoreTests` and owner-route security cases are **authored-not-run**. The new WinUI screen, Server/App/Contracts builds, Windows release gates and gamepad accessibility remain **NOT VERIFIED**. Do not infer readiness for 3.1.0.


### 2026-11-05 — Source merged on main; Windows validation outstanding

PRs #1–#5 were squash-merged onto `main` on 2026-11-05. New code remains **Authored-not-compiled** in this remote session and all new tests remain **Authored-not-run**; neither integration nor publication of release 3.1.0 is claimed.

- **L2 PR #1:** strict same-file Git parser rejects unsupported/unknown metadata, zero-hunk sections and repeated headers, with SHA-256 in-memory preview; Auto requires OS and all mutation tools confined. No live executor/UI or tests run.
- **L3 PR #2:** owner-only two-model Arena Core POST/GET, typed client/DTO, IT/EN statuses, strict model names and 4,000-character prompt cap; added small Advanced Chat Compare button and side-by-side modal with manual refresh/cancel, plus authored-not-run FlaUI Smoke visibility test. No routing/chatbubble redesign or Windows/hardware verification; media free-VRAM presets remain estimates.
- **L8 PR #3:** selected local game guides must match separately verified indexed-source IDs (empty/unknown scopes rejected), and unknown Gaming telemetry blocks GPU admission; no live Chat/OS integration or tests run.
- **L6/L7 PR #4:** per-game suspend proposals with anti-cheat exclusions and monotonic continuity tracking (focus/battery/thermal; reset on changed process, rule edits or missing samples), controller diagnostic/remap previews and a typed consent-gated, PaRuX-owned undo plan; tests authored-not-run; **Needs physical HW**.
- **L4/L5/L9 PR #5:** bounded export previews with desktop-owner-only/Gaming-blocked Core endpoint, shared DTO, authenticated client and bilingual Gaming Home preview dialog; default-off wishlist and public price parser requiring matching store/product/currency/country/freshness; offline license/IT-EN audit scripts. **All new source/tests authored-not-compiled/not-run**. No actual games imported, public API fetching, persisted monitoring, region proof or notifications.
- **Docs PR #6:** reconcile roadmap, README and handoff to the merged source, preserving Windows build, targeted xUnit, full solo FlaUI regression and physical checks as release gates.


### 2026-11-05 - Gaming home: hero, filter rail, save restore and launch-helper UI, IGDB covers, Quick Resume status

- **Games home:** hero for the last played startable game (Play still goes through the launch consent), filter rail with counts (all, favourites, recently played, installed / not installed, store, genre), keyboard / D-pad / bumper movement between tiles, stable AutomationIds. EA app / Battle.net / Ubisoft / Xbox titles are labelled "unverified" (read-only discovery, not yet checked on a real install: **NEEDS PHYSICAL HW**).
- **Game details:** per-game save backup + **restore** (preview, confirmation, automatic pre-restore copy, SHA-256 re-check, plain-language refusals) and per-game **launch helpers** (off by default, .exe only, confirmed, never during Gaming).
- **Covers:** optional **IGDB** source with the user's own Twitch credentials (DPAPI, never in settings); per-game cover override unchanged. IGDB parsing is tested with synthetic JSON only, **not against the live API**.
- **Quick Resume:** status line on the Games home and the start-up recovery results (resumed / failed) in its panel.
- Tests: new xUnit (home rules, restore/hook wording, IGDB parsing) and FlaUI `GamingHomeCompetitiveUiTests` (isolated data root, synthetic games).

### 2026-11-04 — Virtual controller: guided activation, verified HidHide binding, per-game profiles, hot-plug
