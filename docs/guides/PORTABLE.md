# Portable use

The Portable ZIP runs RBG – PaRuX without installing it. Short name: RBG – PaRuX. Status: BETA.

## Use it

1. Extract the ZIP to a writable **NTFS** folder (any drive). Removable FAT/exFAT drives are not certified.
2. Double-click `PaRuX.exe`. The optional `PaRuX (portable data).cmd` starts the same app with the same data location.
3. A `parux.portable` marker next to the executable, plus an adjacent `PaRuXData` folder, switches the app into portable mode.
   Chats, settings, models and indexes stay in `PaRuXData`, not in your Windows profile.

You can move the extracted folder to another path or drive; the 4.1.0 record shows the app starting healthy after a move with
`settings.json` byte-identical.

## Download in parts (large Standard Portable)

GitHub allows at most 2 GiB per release file. The Standard Portable ZIP bundles a starter model and is larger than that, so a
release may publish it as numbered parts instead of one file:

- `PaRuX-<version>-portable-win-x64.zip.part001`, `.part002`, ... (each at most 1.9 GiB)
- `PaRuX-<version>-portable-win-x64.zip.parts.sha256.txt` (SHA-256 of every part and of the joined ZIP)

Put **all** parts and the `.parts.sha256.txt` file in one folder, then join them with plain Windows PowerShell (no extra software).
Replace `<version>` and run from that folder:

```powershell
$name  = "PaRuX-<version>-portable-win-x64.zip"
$lines = Get-Content "$name.parts.sha256.txt"
# 1. every part must match the published hash (also catches a missing or truncated download)
foreach ($l in $lines) {
    $hash, $file = $l -split '  ', 2
    if ($file -ne $name -and (Get-FileHash $file -Algorithm SHA256).Hash -ne $hash) { throw "Hash mismatch: $file" }
}
# 2. concatenate the parts in order
$out = [IO.File]::Create("$PWD\$name")
Get-ChildItem "$name.part*" | Where-Object Name -match '\.part\d{3}$' | Sort-Object Name | ForEach-Object {
    $in = [IO.File]::OpenRead($_.FullName); $in.CopyTo($out); $in.Dispose()
}
$out.Dispose()
# 3. the joined ZIP must match the last line of the list
$want = ($lines | Where-Object { $_ -like "*  $name" }) -split '  ' | Select-Object -First 1
if ((Get-FileHash $name -Algorithm SHA256).Hash -ne $want) { throw "Joined ZIP hash mismatch - download the parts again." }
"Joined and verified: $name"
```

The repository also provides `tools/join-release-asset.ps1` (same steps with better error messages):
`powershell -NoProfile -ExecutionPolicy Bypass -File .\join-release-asset.ps1 -FirstPart .\<name>.zip.part001`.
`copy /b <name>.zip.part001+<name>.zip.part002 <name>.zip` in `cmd` also joins two parts, but it does not verify anything, so
compare `Get-FileHash` with the list yourself. Extract the joined ZIP as described above; the part files can then be deleted.
This is a transport workaround for the host's size limit, not a different product. A release can also offer the ZIP as one file.

## What is included

- Self-contained desktop app and Core (no .NET, Windows App Runtime, Python, Ollama or LM Studio needed for the shell).
- The **4.1.0** Portable ZIP bundles the Vulkan llama.cpp runtime and **no model**: download or import one in Model Hub
  (see [README](../../README.md#set-up-a-model)).
- The **unreleased** Standard Portable builder (`scripts/build-portable.ps1 -FetchAssets -Smoke`) also bundles a CPU runtime, a Qwen3-4B
  GGUF starter and ONNX embedding and reranker models. That build has not passed fresh-machine tests and is not part of the published 4.1.0 beta.

## Not included

Windows drivers, game stores, games, and optional image/video/training engines (installed on request). In 4.1.0 the Portable
has no CUDA or media pack, no terminal CLI and no in-app updater.

## Update

Download the new ZIP, replace the application files, keep `PaRuXData`.

## Limits

- Closing the window does not quit the app: PaRuX hides to the system tray so background work and the local Core keep running.
  Quit from the tray icon (Exit). This is by design, not a hang.
- The ZIP container cannot carry a signature. Builds made with a signing certificate (4.2.0 onward) Authenticode-sign the
  first-party executables and DLLs inside it; the certificate is self-signed, so Windows still shows "Unknown publisher"
  unless you trust it, and SmartScreen and antivirus rules still apply (no bypass). Earlier ZIPs (4.1.0) were not signed.
- Settings persistence across a move was shown by file identity, not by changing a setting in the UI.
- Other GPUs and SmartScreen behaviour were not verified. Source: [release record](../RELEASE_CANDIDATE_4.1.0.md).

For offline office use see [CORPORATE.md](CORPORATE.md). For problems see [TROUBLESHOOTING.md](TROUBLESHOOTING.md).
