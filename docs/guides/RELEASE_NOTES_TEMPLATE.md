# RBG – PaRuX AI & Gaming OS <version> release notes (template)

Copy this file for each release. Every claim needs evidence (commit, test run, hardware). Use only DONE / PARTIAL / MISSING / BLOCKED-EXTERNAL
for capabilities, and write NOT RUN for what was not run.

- **Date:** YYYY-MM-DD
- **Channel:** beta pre-release / stable
- **Build commit:** `<sha>` (clean tree: yes/no)
- **Version source:** `Directory.Build.props`, `PaRuXProductVersion`

## Highlights

- <user-visible change, one line each>

## Fixes

- <defect, how it was found, regression test name>

## Known limits

- <open item with its plan ID, for example GOS-04, PERF-002>

## Artifacts

| File | Size (bytes) | SHA-256 | Signed by |
|---|---|---|---|
| `PaRuX-<version>-win-x64.msi` | | | |
| `PaRuX-<version>-portable-win-x64.zip` | | | |
| Corporate Lite ZIP (with model or NoModel) | | | |
| `PaRuX-<version>-android.apk` | | | |

State clearly which files are unsigned or self-signed.

## Verification

| Check | Result | Hardware / OS | Evidence |
|---|---|---|---|
| Release x64 build | | | |
| Server xUnit (passed / failed / not executed) | | | |
| FlaUI regression | | | |
| Android unit tests | | | |
| MSI install / upgrade / uninstall | | | |
| Gaming hard-off on a real game | | | |
| Corporate strict gate | | | |

## Not verified in this release

- <list; name any hardware that was not attached>

## Upgrade notes

- MSI: <in-app Updates or manual>. Portable: replace application files, keep `PaRuXData`.

## Links

[Install](INSTALL.md) · [Troubleshooting](TROUBLESHOOTING.md) · [Plan](../../PARUX_EXECUTION_PLAN.md) · [Changelog](../../CHANGELOG.md)
