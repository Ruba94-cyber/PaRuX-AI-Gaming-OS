# PaRuX AI & Gaming OS - Support

Draft support page for the Store listing's "Support contact info" field (Partner Center needs a web page or e-mail address; the owner supplies it - no personal contact data is stored in this public repository).

## Getting help

- Report a bug or ask a question: https://github.com/Ruba94-cyber/PaRuX-AI-Gaming-OS/issues (public; do not post private documents or logs that contain personal data).
- Security problems: follow `SECURITY.md` in the repository instead of opening a public issue.

## Common questions

**Where is my data?** In the app's private folder for your Windows user, under `%LOCALAPPDATA%\Packages\<package family name>\LocalState\PaRuX`. It is removed when you uninstall the app. Use the app's Backup feature first if you want to keep chats, indexes or models.

**I used the MSI or Portable edition before. Will my chats appear?** No. The Store edition keeps its own data folder and never reads or changes the MSI/Portable data. Move content with Backup/Restore.

**How do updates work?** The Microsoft Store updates PaRuX. The Update Manager still lets you review updates for optional runtimes and models, but it does not update the app itself in the Store edition.

**The first chat says I need a model.** The Store package contains no model files. Open the Model Hub (or the first-run model step), pick a model and confirm the download. Models are large; check free disk space.

**It is slow or runs out of memory.** Choose a smaller model or a smaller quantization. Speed depends on your CPU, GPU, memory and the model.

**Windows Firewall asks about network access.** Only the optional device pairing/remote access feature listens on your local network, and only when you enable it.

## Information to include in a bug report

PaRuX version (shown in the app), Windows version, CPU/GPU/RAM, the model you used and what you did. Remove personal data from screenshots and logs.
