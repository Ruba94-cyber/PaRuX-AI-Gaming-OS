# Store listing artwork

Derived from the approved PRX Orbit artwork already in the repository (`src/PaRuX.App/Assets/prx-orbit-approved.png`, 380x380). No new artwork was invented and nothing here is third-party.

| File | Size | Partner Center field |
|---|---|---|
| `store-logo-1x1-300x300.png` | 300x300, transparent | Store logos > 1:1 App tile icon (Partner Center derives the 150 and 71 sizes; upload them only if it asks) |
| `box-art-1x1-1080x1080.png` | 1080x1080, solid dark background, logo in the upper two thirds | 1:1 Box art (optional for apps; Microsoft documents it as a game asset) |
| `poster-art-2x3-720x1080.png` | 720x1080, solid dark background, logo in the upper two thirds | 9:16 / 2:3 Poster art (optional for apps; required only for Xbox) |

Limits: the source is 380 px, so the 1080 and 720x1080 files are upscaled and slightly soft; export the artwork from the original vector or a larger master if one exists before using them. Only the 300x300 logo is needed for a normal PC app. The package itself already carries its own logo set (`Assets\` in the MSIX); the listing logos are used only if "display uploaded logo images instead of the images from my packages" is ticked.
