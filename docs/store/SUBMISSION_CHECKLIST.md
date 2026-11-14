# Microsoft Store submission checklist (owner steps)

Nothing here has been submitted. Items marked OWNER need the owner's Partner Center account or machine. Status legend: DONE / TODO / NOT RUN.

## A. Account and identity (OWNER)

1. DONE (owner) Register a free developer account (individual) at https://storedeveloper.microsoft.com (Partner Center).
2. DONE (owner) Reserve the app name. The build default display name is "PaRuX AI & Gaming OS"; if you reserve a different name, put it in the identity file as `appDisplayName`.
3. DONE (owner, 2026-11-11) In Partner Center open Product management > Product identity and copy **Package/Identity/Name**, **Package/Identity/Publisher** and **Package/Properties/PublisherDisplayName** into the Git-ignored file `<main checkout>\.ai\store\store-identity.local.json` (keys `identityName`, `identityPublisher`, `publisherDisplayName`; optional `appDisplayName`). Values are case- and punctuation-sensitive. Never commit this file (`*.local.json` is ignored).
4. TODO Pricing: paid, 0.99 USD base price, one time (see `PRICING_AND_AVAILABILITY.md`; the owner confirms the amount, per-market prices, tax/payout profile). Markets: choose. Availability: public.

## B. Build the package (agent-reproducible)

```powershell
./scripts/build-msix.ps1 -IdentityFile "<main checkout>\.ai\store\store-identity.local.json" `
    -RuntimeCacheDir "<main checkout>\artifacts\cache\runtime"
```

Output: `artifacts\msix\PaRuX_<major.minor.patch>.0_x64.msix` (+ `.sha256.txt`). It is unsigned on purpose: the Store re-signs MSIX submissions. The Store requires version `x.y.z.0`; the script enforces it.

## C. Evidence still missing before submitting

| Check | Status | How |
|---|---|---|
| Windows App Certification Kit on the .msix | DONE 2026-11-11: overall PASS, 2 optional tests fail (WebView2 metadata, blocked executables) - QA-044. Re-run after any packaging change (needs an elevated prompt) | In an elevated PowerShell: `& "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe" reset` then `appcert.exe test -appxpackagepath <msix> -reportoutputpath wack-report.xml`. Keep the XML report; fix every failure. |
| Local install + first run of the packaged app | DONE 2026-11-11 through the Core API (QA-043); the window and a chat with a downloaded model are still to be checked by eye (needs trust of a test certificate machine-wide, or Developer Mode) | Option 1: `./scripts/build-msix.ps1 -IdentityFile ... -SkipPublish -SignTestCopy`, then in an elevated PowerShell import the printed certificate's `.cer` into `Cert:\LocalMachine\TrustedPeople` and `Add-AppxPackage <TESTSIGNED.msix>`. Option 2: turn on Developer Mode and `Add-AppxPackage -Register <layout>\AppxManifest.xml`. Remove the certificate and package afterwards. |
| First-run checks once installed | NOT RUN | PaRuX starts; Core starts (`PaRuX.Server.exe` child of PaRuX.exe); Chat with a downloaded small model works; data appears under `%LOCALAPPDATA%\Packages\<PFN>\LocalState\PaRuX` and **not** under `%LOCALAPPDATA%\PaRuX`; Update Manager says the Store manages updates; app uninstalls cleanly. If the app fails to start, the first suspect is WinRT activation / `resources.pri` for a self-contained WinUI 3 app with package identity (see `PACKAGING_DECISION.md`). |
| Partner Center certification | NOT RUN | Upload the .msix on the Packages page. |

## D. Store listing (OWNER)

Every field is in section I below. Short version: paste text from `LISTING_en.md` and `LISTING_it.md` (they are paste-ready; no evidence references remain), add Italian as a second listing language, publish `PRIVACY_POLICY.md` at a public HTTPS address, give a support contact (`SUPPORT.md`), complete the IARC questionnaire yourself (`POLICY_AND_CERTIFICATION.md` section 5), choose the category (recommended Productivity) and upload the 300x300 store logo.

## E. Screenshots

See `SCREENSHOTS.md` (10 shots, EN and IT, from the installed Store package, synthetic data only).

## F. Restricted capability justification (paste into the `runFullTrust` field)

> PaRuX AI & Gaming OS is a desktop app (WinUI 3 / .NET) that runs AI language models locally on the user's PC. The app starts its own local server process (PaRuX.Server.exe) and the local inference engine (llama.cpp, MIT license) as child processes, reads and writes files the user chooses (documents to index, model files), and uses native GPU libraries (Vulkan) that cannot run inside a UWP/AppContainer sandbox. It therefore needs the runFullTrust capability. It does not install services or drivers, does not run elevated, does not change Windows settings, registers no startup entries, and does not collect telemetry. Network use is user-initiated: model downloads, optional web search, and an optional local-network pairing feature that is off by default.

Network capabilities declared: `internetClient`, `internetClientServer`, `privateNetworkClientServer` (justify the last two as the optional local-network pairing listener).

## G. Notes for certification (paste in Partner Center)

Paste the English text in `POLICY_AND_CERTIFICATION.md` section 4 (it already covers: no sign-in, a small model is enough to test, synthetic text file for the document feature, no in-app purchase).

## H. Policy risks to decide before or after the first certification result

1. **Executable downloads (policies 10.2.2, 10.2.3, 10.2.4): RESOLVED in code.** The Store edition (package identity, or `PARUX_STORE_EDITION=1` set by the packaged app on the Core) never downloads or installs executable code: no additional inference runtimes or engines, no managed Python/pip tooling, no ComfyUI/media engines, no game archives or installers. The Core refuses those routes with HTTP 403 and "Not available in the Microsoft Store edition" before any network or disk activity (`StoreEditionPolicy`, `StoreEditionGuardMiddleware`), and the UI hides or disables them. The baseline engine ships in the package; models and other pure data still download on user consent; "Locate existing" for software the user installed still works. Still to verify on the packaged install: the refusal and the hidden controls (see section C).
2. **Gaming area**: the embedded torrent engine and the external torrent-client adapter are unavailable in the Store edition (never started, no listening port), and HTTP(S) game-archive/installer downloads are disabled too. What remains is the game library, launching games already installed on the PC and store discovery. Decide whether that remaining surface is acceptable for a reviewer, or hide the Gaming workspace entirely.
3. **Open security findings** in `docs/SECURITY_REVIEW.md` (pairing guessing and token expiry, token accepted from URL query, runtime ports without API key, .NET 8 end of support) touch the optional LAN/pairing feature that the Store listing mentions. Fix or drop the claim before submitting.
4. **Beta status**: the repository calls 4.1.0 a beta and `docs/REQUIREMENT_MATRIX.md` shows most rows as not Release Ready. A Store listing should say "early release", as the drafts do.
5. **Data retention**: uninstall deletes downloaded models and chats (normal for MSIX). The listing and support page say so.
6. **Name/branding**: confirm the reserved name does not conflict with third-party trademarks (the app mentions llama.cpp, Hugging Face, Steam etc.; do not use them in the title).

## I. Partner Center fields, one by one

Nothing was submitted and Partner Center was not opened for this list; field names follow the MSIX submission flow and may differ slightly in the live UI. Status: PRONTO = text exists in this folder, SERVE SCREENSHOT, SERVE DECISIONE = owner chooses, DONE (owner) = already done by the owner, NOT RUN = needs a run first.

| Page | Field | Value / source | Status |
|---|---|---|---|
| Overview | Product / name reservation | Reserved by the owner; identity values live only in the Git-ignored local file | DONE (owner) |
| Pricing and availability | Markets | All, or a chosen list (`PRICING_AND_AVAILABILITY.md` section 4) | SERVE DECISIONE |
| | Discoverability / visibility | Public, discoverable | PRONTO |
| | Base price | Paid, lowest tier 0.99 USD; review per market | SERVE DECISIONE |
| | Free trial | None (recommended) | SERVE DECISIONE |
| | Schedule | Publish when certified, only after the owner says go | SERVE DECISIONE |
| | Payout and tax profile (account level) | Owner account; nothing in the repo | SERVE DECISIONE |
| Properties | Category and subcategory | Productivity (alternative Developer tools) | SERVE DECISIONE |
| | Privacy policy URL | Public HTTPS page with `PRIVACY_POLICY.md` | SERVE DECISIONE (needs a URL) |
| | Website | Public project page (donations and free build are described there; see policy Option A) | SERVE DECISIONE |
| | Support contact info | `SUPPORT.md` at a public URL or an owner-chosen e-mail | SERVE DECISIONE |
| | System requirements (minimum / recommended) | `LISTING_en.md` "System requirements" | PRONTO |
| | Product declarations | Do not tick unverified declarations (accessibility tested, etc.) | SERVE DECISIONE |
| Age ratings | IARC questionnaire | Owner answers; facts in `POLICY_AND_CERTIFICATION.md` section 5 | SERVE DECISIONE (owner must answer) |
| Packages | MSIX upload | `artifacts\msix\PaRuX_<ver>_x64.msix` from the packaging agent's worktree/build; version 4.2.0.0 | NOT RUN (build and install check by the packaging agent) |
| | Device families | Windows 10/11 Desktop x64 | PRONTO |
| | Restricted capability justification | `POLICY_AND_CERTIFICATION.md` section 3 | PRONTO |
| Store listings (en-US) | Description | `LISTING_en.md` | PRONTO |
| | What's new | `LISTING_en.md` (4.2.0) | PRONTO |
| | Product features | `LISTING_en.md` | PRONTO |
| | Search terms (7) | `LISTING_en.md` | PRONTO |
| | Short description | `LISTING_en.md` | PRONTO |
| | Copyright and trademark info | `LISTING_en.md` (owner fills the publisher name) | SERVE DECISIONE |
| | Screenshots | `SCREENSHOTS.md` shots 1-8 | SERVE SCREENSHOT |
| | Store logo 1:1 300x300 | export from the PRX Orbit approved artwork | SERVE SCREENSHOT (asset export) |
| | Optional art / trailers | skip | PRONTO |
| Store listings (it-IT) | Same fields | `LISTING_it.md` | PRONTO |
| | Screenshots (Italian UI) | `SCREENSHOTS.md` shots 9-10 plus 1, 2, 4 in Italian | SERVE SCREENSHOT |
| Submission options | Notes for certification | `POLICY_AND_CERTIFICATION.md` section 4 | PRONTO |
| | Publishing hold / release timing | Choose manual publish after certification | SERVE DECISIONE |
| Evidence before submit | Window check, chat with a downloaded model, Store-managed update message, WACK re-run on the final package | Section C | NOT RUN (partly) |
| Policy decisions | Title wording; donation/free-build mention (Option A/B); category | `POLICY_AND_CERTIFICATION.md` sections 2 and 6 | SERVE DECISIONE |
