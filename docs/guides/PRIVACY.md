# Privacy (product level)

This page describes the software, checked against the source and [docs/store/PRIVACY_POLICY.md](../store/PRIVACY_POLICY.md) (the draft policy for the
Microsoft Store edition). It is not legal advice and must be re-checked when features change.

## Summary

RBG – PaRuX runs AI models on your PC. Chats, documents, settings and indexes are stored locally. The source contains no analytics SDK and no code that
reports usage or crashes to the publisher. There are no user accounts. Data leaves your PC only when you ask for something that needs the network.

## What is stored, and where

Conversations, projects, attachments, indexes of folders you choose, settings, models, components, logs, and the secret protecting paired devices.
Secrets you enter (for example API keys) are protected with Windows DPAPI for your user.

- MSI: `%LOCALAPPDATA%\PaRuX`
- Portable and Corporate: `PaRuXData` next to the executable
- Store edition: the per-package `LocalState` folder

The Core listens on `127.0.0.1` only and protected requests need a random session token. The bootstrap file holds local runtime state; never share it.

## What can leave your PC (only on your action)

| Action | Goes to |
|---|---|
| Model and component downloads, model search | Public hosts such as huggingface.co and github.com |
| Web search or research in chat | The configured provider (DuckDuckGo HTML by default, or your own SearXNG) |
| Cloud fallback or remote AI provider | Off by default; only the provider you configure |
| LAN pairing and browser access | Off by default; devices on your network |
| Game covers and metadata (optional) | Services such as the Steam store, SteamGridDB, IGDB |
| Update check | GitHub release metadata, when you use Updates |

Corporate Lite is offline by default and refuses internet, runtime and model downloads. No cloud API is needed for local use.

## Evidence and limits

- Strict-local tests assert that restored cloud credentials never arm inference. See [STRICT_LOCAL_EGRESS_EVIDENCE.md](../STRICT_LOCAL_EGRESS_EVIDENCE.md).
- The Corporate gate observed 0 non-loopback connections by polling; short connections can be missed, so this is evidence, not proof.
- Local logs may contain prompts and paths; review them before sharing. Security review: [SECURITY_REVIEW.md](../SECURITY_REVIEW.md).
- Model answers may be wrong; do not rely on them for medical, legal or financial decisions.

## Your choices

Delete chats and indexes in the app, delete the data folder, or uninstall. Use Backup before doing so. The publisher holds no data about you.
