# PaRuX AI & Gaming OS - Privacy Policy

Effective date: November 15, 2026. This policy applies to the Microsoft Store edition of PaRuX 4.2.x.

## Summary

PaRuX runs AI models on your PC. Chats, documents, settings and indexes are stored locally. PaRuX has no user accounts, advertising or analytics service of its own. Some features communicate with third-party services or devices, as described below. Automatic maintenance can also request model catalog metadata in the background.

## What is stored on your PC

PaRuX can store conversations, projects and memory, attachments, indexes of folders you choose, settings, downloaded models, logs and paired-device credentials. Its default data location is the app's private data folder for your Windows user (`%LOCALAPPDATA%\Packages\<package family name>\LocalState`). Some credentials, including API keys, are protected with Windows DPAPI for your user account. Files you select, exports and backups can be stored elsewhere. Uninstalling normally removes the package's local data, including models stored there; back up data you want to keep before uninstalling. Copies outside that folder are not necessarily removed.

## Connections and information shared

| What | When | Goes to | What is sent |
|---|---|---|---|
| Model and component downloads | When you start a download in the Model Hub, Runtime Manager or a setup screen | Public hosts such as huggingface.co, github.com and its release servers, civitai.com, download.pytorch.org | The normal web request for the file (your IP address, the file requested). No chat content. |
| Model search and metadata | When you search or browse models | The same public hosts | Your search text |
| Automatic catalog refresh | When automatic maintenance is enabled (enabled by default), including at startup when its catalog is stale and on its configured schedule | Hugging Face by default, or a configured discovery provider | Requests for model metadata and the network information normally sent with them, including your IP address. No chat content is needed for this refresh. You can disable automatic maintenance in settings. |
| Web search / research in chat | Only when you turn web access on for a request or use a research feature | The search provider the app is configured to use (an HTML search endpoint of DuckDuckGo by default, or a SearXNG server you run yourself) and the pages it opens | The search query text and the page requests |
| Provider credential test | When you explicitly test a configured cloud fallback provider | The provider endpoint you configured | A request for its model list and the API key as a credential, plus normal network information such as your IP address. This test does not send a chat prompt or conversation. Core does not use that fallback for inference. |
| Optional integrations and endpoint tests | When you configure or use an external integration, or run an endpoint probe or benchmark | The endpoint or external MCP server you choose | Depending on the feature, requests can include credentials, test prompts or workload content, tool requests and their arguments, and normal network information such as your IP address. Review the endpoint and the information provided to it before use. |
| Pairing with your own devices (optional) | Off by default. Only if you enable remote access in Settings | Devices on your local network that you pair; traffic is HTTPS to a private address | Your chats and tasks for those paired devices only |
| Game library covers and metadata (Gaming area, optional) | Only if you use the game library features | Game metadata/cover services such as the Steam store, SteamGridDB, IGDB | Game titles you ask about |

These examples describe common connections; the information exchanged depends on the integrations and features you configure and use. The publisher does not sell data or use your content to train models through PaRuX. Third-party services receive requests under their own terms and privacy policies; the publisher does not control their handling of those requests. Windows and the Microsoft Store may also process information under Microsoft's privacy policies.

## Purchase

The Store edition is bought through the Microsoft Store. Microsoft handles the payment and the customer account under its own terms and privacy statement; the publisher does not receive card or payment details from PaRuX, and PaRuX contains no payment screen. The app contains no donation or payment function.

## Children

PaRuX is a general-purpose tool and is not directed at children. It does not knowingly collect personal information from anyone.

## Your choices

You decide which models and folders are used and can change the automatic maintenance setting. You can delete conversations and indexes inside the app or uninstall it. Review and delete any exports, backups or other copies you saved separately. Information you submit to a third-party service, including a support issue, is subject to that service's own retention and deletion processes.

## Local AI is not a guarantee about model output

Answers come from the model you choose and may be wrong. Do not rely on them for medical, legal or financial decisions.

## Changes and contact

If this policy changes, the new version will be published at this address with an updated effective date. For support or privacy questions, contact the publisher through [PaRuX's GitHub issue tracker](https://github.com/Ruba94-cyber/RBG-PaRuX-AI-Gaming-OS/issues). Issues are public: do not include sensitive information. GitHub processes information you submit under its own privacy policy.
