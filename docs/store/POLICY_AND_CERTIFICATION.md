# Store policy review, certification notes and IARC answers

Checked 2026-11-14 against Microsoft Learn "Microsoft Store Policies" (current version and archived 7.x versions) and the Partner Center pricing, trial and age-rating pages. Policy wording changes; re-read https://learn.microsoft.com/windows/apps/publish/store-policies before submitting. This file is guidance, not legal advice. Nothing was submitted.

## 1. What policy says about payment and donations

| Rule | Text (paraphrased) | Effect on PaRuX |
|---|---|---|
| 10.8 scope | Section 10.8 applies if the product has in-product purchase, subscriptions, virtual currency, billing functionality or captures financial information. | A paid up-front app with no in-app purchase does not trigger it. PaRuX must keep no payment UI. |
| 10.8.1 | Digital goods or services consumed in the product must use the Store in-product purchase API. A product may not direct users to another purchase mechanism. Voluntary donations that give the user digital goods or services in return (extra features, ad removal) count as purchases and must use the Store API. | Any donation that unlocks, speeds up or removes anything is forbidden outside the Store API. PaRuX donations must give nothing in return (README already says "donations never unlock or restrict features"). |
| 10.8.2 | Voluntary donations must use the Microsoft payment request API or a **secure third-party purchase API**. Third-party payments must identify the transaction provider, authenticate the user, obtain the user's confirmation; user can require authentication every time or turn off in-product transactions; card handling must meet PCI DSS. Charitable-contribution facilitation must follow applicable law and state that Microsoft is not the fundraiser. | A donation page run entirely by a payment provider in the browser satisfies the spirit, but the policy text speaks of an API used by "your product". A link inside the app that opens a browser is a grey area (no explicit allow or ban found). |
| 10.1.5 (current) | A product may, with user consent and after the initial download, enable acquisition of other products published by you if also distributed through the Store, and add-ons. Older versions (7.11-7.13) said "may promote or distribute software only through the Microsoft Store". | Telling users to download another copy of the software elsewhere (the free GitHub build) from inside the app or listing is a risk. Describing that the source is public is information, not distribution. |
| 10.2.5 (older) | Apps and in-app products sold in the Store must be installed and updated only through the Store. | Matches the design: no self-updater in the Store edition. |

Not found by search (do not assume it is allowed or banned): a rule that names plain external donation links in the listing text or the website field. Treat as "unspecified; reviewer discretion".

## 2. What to do: three levels, with risk

Recommended: **Option A**.

- **Option A (recommended, lowest risk).** Store build and listing contain no donation link, no PayPal/Sponsors URL and no "download the free version" link. Donations and the free portable build are described only on the public project page (GitHub). The Partner Center "Website" field may point to that page (a normal listing field). The app shows nothing about payment. Risk: very low. Listing sentence allowed: "PaRuX is source-available under the FSL-1.1-MIT license; the source is on GitHub." (already in the descriptions).
- **Option B (moderate risk).** Same, plus a plain sentence in the long description: "A free portable build of the same open project is also available from the project's GitHub page. Optional donations are described there; they unlock nothing." No URL in the text. Risk: reviewer may read it as promoting software outside the Store (10.1.5) or as steering to another payment; cannot be ruled out. If rejected, remove the sentence (cheap to fix).
- **Option C (high risk, not recommended for the first submission).** A "Support the project" button inside the Store build opening a third-party donation page. Needs: opens only after explicit click, names the transaction provider, gives nothing in return, no payment data touched by PaRuX, and a setting to hide it. Policy 10.8.2 conditions (identify provider, confirm transaction, PCI via the provider) would then have to be argued in the certification notes. Today the app has no such control, so this would also need code and a fresh certification pass.

Hard no in any option: any text such as "buy cheaper", "pay on GitHub instead", "free version has the same features, don't pay here" next to the Store listing; any donation reward; linking the donation to the Store purchase.

Also: the donation page in `.github/FUNDING.yml` and `README.md` contains the owner's e-mail address inside the PayPal URL (already noted as SEC-F20). Do not copy that URL into Partner Center or any new file; the owner may want a donation page that does not expose the address.

## 3. runFullTrust justification (paste into the restricted-capability field)

> PaRuX AI & Gaming OS is a desktop app (WinUI 3 / .NET) that runs AI language models locally on the user's PC. The app starts its own local server process (PaRuX.Server.exe) and the local inference engine (llama.cpp, MIT license), both shipped inside the package, as child processes. It reads and writes files the user chooses (documents to index, model files) and uses native GPU libraries (Vulkan) that cannot run inside a UWP/AppContainer sandbox. It therefore needs the runFullTrust capability. It does not install services or drivers, does not run elevated, does not change Windows settings, registers no startup entries, has no self-updater (the Store updates it), and does not collect telemetry. Network use is user-initiated: model downloads, optional web search, and an optional local-network pairing feature that is off by default.

Network capabilities declared: `internetClient`, `internetClientServer`, `privateNetworkClientServer` (the last two for the optional local pairing listener, opened only when the user enables remote access).

## 4. Notes for certification (paste into Partner Center, English)

> No account or sign-in is needed. The package contains the app and a local inference engine but no AI model; to test, start the app, choose "Skip" or pick a small model in the first-run screen and confirm the download (model files are downloaded from public model hosts only after the user confirms). For the document feature, point it at a folder with a small plain-text file. The Microsoft Store edition never downloads or installs executable code (no additional inference engines, no Python tooling, no installers) and has no torrent engine; those routes are refused by the app. The app has no self-updater and shows that updates come from the Store. The app has no in-app purchases, no ads and no payment or donation screen; the Store price is the only payment. runFullTrust is required because the app runs its own local server and inference engine as child processes (see the capability justification). The optional LAN pairing listener is off by default. The Gaming area only launches games already installed on the PC; it sells and downloads nothing in this edition. Third-party component: llama.cpp (MIT) is included in the package. Open-source notice and source: https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS

Check before pasting: that every sentence is still true on the package being submitted (QA-043 verified the refusals through the API on the installed package; the window itself was not checked).

## 5. Age rating (IARC questionnaire): ready answers

Answer yourself in Partner Center; the wording of the questions can change, so use these as the factual base, not as a copy-paste:

| Topic | Answer |
|---|---|
| App category | Utility / productivity (an AI chat tool), not a game and not a social app |
| Violence, blood, sexual content, nudity, profanity, drugs, gambling | None authored by the app. The app has no content of its own in these areas. |
| Generated content | Text (and where installed, images) is produced by a model the user chooses and downloads; the app has no content filter beyond what that model does. If the questionnaire asks about user-generated or AI-generated content, answer yes and that it is not moderated by the publisher. Expect IARC to rate this conservatively. Do not understate. |
| Online interaction / chat between users | No user-to-user communication. Optional pairing of the user's own devices on a private network, off by default. |
| Shares user location | No |
| Collects personal information | No collection by the publisher; data stays on the PC (see the privacy policy). |
| Purchases | The Store price only; no in-app purchases, no virtual currency, no loot boxes |
| Ads | None |
| Web access | Optional web search and model downloads on user action |
| Game features | A game library that launches games already installed; sells none |

Expected outcome: a low rating that may be raised by the generated-content answer. Accept what IARC returns; retake the questionnaire if features change.

## 6. Risks of rejection or delay (ranked)

| # | Risk | Policy | Mitigation | Residual |
|---|---|---|---|---|
| 1 | Product title "PaRuX AI & Gaming OS" contains descriptive text and "OS" can mislead (README says "OS" describes an intended experience, PaRuX does not replace Windows) | 10.1.1 (title unique, no marketing/descriptive text; no misleading) | Consider reserving and using "PaRuX" as the Store title (needs a new name reservation and matching the identity file; the package identity name can stay). The description already says it is a workspace running on Windows. | Medium. Owner decision |
| 2 | Executable downloads and torrent in the Store edition | 10.2.2, 10.2.4 | Removed in the Store edition (`StoreEditionPolicy`, tests QA-038, refusals QA-043) | Low if the installed package behaves as verified; the window and Store-install path are still unchecked |
| 3 | Gaming area under a non-game category and the title word "Gaming" | 10.1.1 (category), 10.1 accuracy | Category Productivity; description explains the game library; no game sales | Low-medium |
| 4 | Paid app that is free elsewhere; reviewer sees the free GitHub build | 10.1.1 (do not mislead about relationship to other products) | Listing states honestly what the Store edition leaves out; no feature claims beyond the free build; no "free" next to the Store edition | Low |
| 5 | Donation or free-build link in the listing/app | 10.8.2, 10.1.5 | Option A | Low (A), medium (B), high (C) |
| 6 | Optional LAN pairing claim vs. open findings in `docs/SECURITY_REVIEW.md` | 10.2 security | Keep the claim as "optional, off by default"; fix or drop the claim if findings are still open at submission | Medium until findings are re-checked |
| 7 | Unverified packaged first run (window, chat with a model, Store update) | 10.1.2 (fully functional), 10.3 testable | Do the on-screen install check first (`SUBMISSION_CHECKLIST.md` section C) | Medium until done |
| 8 | Third-party model downloads (licences vary) | 10.5, 10.2 | Models are user-chosen data downloaded on consent; the listing says so | Low |
| 9 | Search terms: "llama.cpp" is another product's title | 10.1.3 | Removed | Low |
| 10 | Italian listing says some screens may show English | 10.7 | Statement included; verify on the Italian screenshots | Low |
| 11 | Beta/early-release wording vs. paid price | 10.1.1 (important limitations) | "Early release" stated in the description; do not remove it | Low |
