# PRX Orbit identity

PRX Orbit is the visual identity for **RBG PaRuX AI Studio**. The approved mark
is fixed artwork: the metallic `PRX` monogram, two open blue/violet arcs and four
nodes must not be redrawn or geometrically reinterpreted.

## Dark Graphene Luxury UI decision (2026-11-03)

The owner now prefers a **near-black graphite/titanium shell without cyan/violet highlights**. The active WinUI/Android semantic palette is defined in `docs/DESIGN_SYSTEM.md` and supersedes the old Core/Orbit blue/cyan/violet **colour recommendations** for app UI. The existing PRX Orbit fixed logo artwork remains unchanged in binaries as an asset-compatibility exception; it is **not yet graphene-monochrome**. This is an outstanding asset-production/approval step, not a completed logo refresh. The icons/tray/splash/installer/Android mipmaps need a coordinated, reviewed multi-resolution replacement before anyone claims that the entire app is visually free of the former bright artwork. Preserve the original geometry and do not silently recolour or remove the canonical artwork.

## Non-destructive graphene candidate generator

On a Windows development machine, run:

```powershell
.\\tools\\branding\\preview-graphene-orbit.ps1
```

This derives an **optional local candidate** from the existing approved transparent PNG: neutral metallic pixels and silhouette are retained, saturated blue/violet pixels are converted to a luminance-aware titanium shade, and the original alpha is preserved. Output goes exclusively to `artifacts/branding/graphene-preview` (master PNG, 16–256 px samples, dark/light composites); it does **not** update `parux.ico`, tray, installer, Android launcher or the approved source. Check actual 16/24/32 px legibility, edge aliasing and brand approval before deciding whether this recolouring is appropriate. If the PRX shape itself changes, obtain separate visual approval and build new assets intentionally.

## System

- **Primary app mark:** use the approved bitmap unchanged; cropping out the source
  caption and proportional resizing are the only permitted transformations.
- **Micro mark:** use the same approved artwork, reduced proportionally.
- **Shell mark:** the shared WinUI `PrxOrbitMarkTemplate` loads the approved asset;
  do not redraw one-off letter tiles in individual views.
- **Motion:** slow orbit for thinking, quicker orbit for work, one brief pulse for
  success/error, and a dimmed sleep treatment. Motion must remain lightweight.
- **Tone:** dark-first, technical and premium. Avoid extra gradients, glows,
  sparkle motifs, robot/brain imagery and high-frequency animation.

## Legacy PRX Orbit bitmap palette (NOT the active app UI)

- PRX Core `#0A0F14`
- Orbit Blue `#2563EB`
- Orbit Light `#3B82F6`
- Routing Cyan `#22D3EE`
- Node Violet `#7C3AED`
- Success `#6CCB8F`; error `#FF9A8F` only for transient states

## Source assets

- `src/PaRuX.App/Assets/prx-orbit-approved.png` — canonical approved artwork.
- `src/PaRuX.App/Assets/prx-orbit-tray.ico` — dedicated Windows tray package.
- `tools/branding/generate-prx-assets.ps1` — reproducibly creates the multi-size
  Windows `.ico` and PNG preview by resizing the canonical artwork only.
- Android launcher artwork lives in `src/PaRuX.Android/app/src/main/res/drawable-nodpi`.
