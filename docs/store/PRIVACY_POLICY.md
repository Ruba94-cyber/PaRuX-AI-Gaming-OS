# PaRuX AI & Gaming OS - Privacy Policy

Draft for the owner to review before publishing at a public URL (Partner Center requires one). Effective date: set on publication. Applies to the Microsoft Store edition of PaRuX 4.1.x. Statements about the software were checked against the source code at the commit this file was written; they must be re-checked when features change.

## Summary

PaRuX runs AI models on your own PC. Your chats, documents, settings and indexes are stored on your PC and are not sent to the PaRuX publisher. PaRuX has no user accounts, no advertising and no analytics or telemetry service of its own: the source contains no analytics SDK and no code that reports usage or crashes to the publisher. Data leaves your PC only when you ask for something that needs the internet or your network, as listed below.

## What is stored on your PC

Conversations, saved projects and memory, attachments, the search index of folders you choose to index, settings, downloaded models and installed components, logs, and the secret used to protect your paired devices. They live in the app's private data folder for your Windows user (`%LOCALAPPDATA%\Packages\<package family name>\LocalState`). Secrets such as API keys you enter are protected with Windows DPAPI for your user account. Uninstalling the app removes this folder, including downloaded models; use PaRuX's Backup feature first if you want to keep your data.

## What can leave your PC, and only on your action

| What | When | Goes to | What is sent |
|---|---|---|---|
| Model and component downloads | When you start a download in the Model Hub, Runtime Manager or a setup screen | Public hosts such as huggingface.co, github.com and its release servers, civitai.com, download.pytorch.org | The normal web request for the file (your IP address, the file requested). No chat content. |
| Model search and metadata | When you search or browse models | The same public hosts | Your search text |
| Web search / research in chat | Only when you turn web access on for a request or use a research feature | The search provider the app is configured to use (an HTML search endpoint of DuckDuckGo by default, or a SearXNG server you run yourself) and the pages it opens | The search query text and the page requests |
| Cloud fallback or remote AI provider | Off by default. Only if you configure a provider (for example an OpenAI-compatible API) and enable it | The provider you choose, under that provider's own privacy terms | The prompt of the turns you route to it, and the API key you entered as its credential |
| Pairing with your own devices (optional) | Off by default. Only if you enable remote access in Settings | Devices on your local network that you pair; traffic is HTTPS to a private address | Your chats and tasks for those paired devices only |
| Game library covers and metadata (Gaming area, optional) | Only if you use the game library features | Game metadata/cover services such as the Steam store, SteamGridDB, IGDB | Game titles you ask about |

PaRuX does not sell data, does not use your content to train models, and does not share data with third parties on its own initiative. Third-party services above receive requests under their own terms and policies; PaRuX does not control them.

## Children

PaRuX is a general-purpose tool and is not directed at children. It does not knowingly collect personal information from anyone.

## Your choices

You decide which models, folders and providers are used. You can delete conversations and indexes inside the app, delete the app's data folder, or uninstall the app. Because nothing is collected by the publisher, there is no publisher-side data to export or delete.

## Local AI is not a guarantee about model output

Answers come from the model you choose and may be wrong. Do not rely on them for medical, legal or financial decisions.

## Changes and contact

If this policy changes, the new version is published at the same address with a new effective date. Contact: see `SUPPORT.md` (publisher contact details to be filled in by the owner in Partner Center; none are stored in this repository).
