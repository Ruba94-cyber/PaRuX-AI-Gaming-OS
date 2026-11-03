# PaRuX AI & Gaming OS design system

Status: foundation specification, in progress. The application remains a Windows application; “OS” describes its integrated experience, not a replacement operating system. Existing product names, identifiers, data paths and installer identity must not be changed without a compatibility and migration review.

## Current inventory and scope

The Windows desktop uses WinUI. `src/PaRuX.App/Styles/Theme.xaml` defines theme dictionaries, semantic brushes, scales and styles, merged by the application. `App.xaml` also owns resources used by code lookup. `MainWindow.xaml` and its partial classes own shell navigation and responsive/accessibility behavior. This is a partial source audit, not a screen-by-screen usability or accessibility certification. Hardcoded values and existing visuals outside the shared resources remain to be inventoried and migrated incrementally. Android is a separate client and has not been audited in this cycle.

Existing raster/icon assets in `src/PaRuX.App/Assets/`: `parux.ico`, `prx-orbit-tray.ico`, `prx-orbit-preview.png`, and `prx-orbit-approved.png`. Their presence does not establish suitability for the new identity or every packaging surface. Preserve existing assets until replacements are reviewed at target sizes and installer, taskbar, tray and shortcut compatibility has been checked.

## Identity direction

Use the full display name **PaRuX AI & Gaming OS**, with **PaRuX** as the short name. Prefer a restrained geometric mark, near-black graphite surfaces and satin titanium emphasis with only a restrained champagne secondary accent. Avoid purple, decorative gradients, heavy glow and literal gamepad/brain motifs. Maintain legibility and distinction at small sizes; do not imitate existing platform marks.

A reproducible starting construction for review, not an approved or produced logo: on a 64 × 64 square grid draw a single-color upright P using an outer path `M 14 8 H 37 C 49 8 54 15 54 25 C 54 35 49 42 37 42 H 26 V 56 H 14 Z`; subtract an inner opening `M 26 19 V 31 H 37 C 41 31 43 29 43 25 C 43 21 41 19 37 19 Z`. Inspect and adjust the silhouette, optical alignment and counters at 16 and 32 px before approval. This construction is a design proposal, not a trademark clearance or a claim that production vector/raster files exist.

An unapproved alternative geometric study is available in `docs/branding/concept-01/`, with SVG variants and a raster preview. It has not replaced application assets.

Deliverables still required: reviewed editable vector master; light, dark, monochrome, compact icon and full lockup variants; raster exports at actual Windows sizes; tray, splash, installer and README applications. Verify contrast, silhouette and recognizable rendering against ## Dark Graphene Luxury — owner direction, implemented in source (2026-11-03)

The shared AI/Gaming shell follows the owner's sketch: brand and navigation in a left sidebar, AI / Gaming mode selector across the top, a quiet AI conversation canvas, and a visual game library with explicit Play and configuration actions. Both modes share one app, settings, services and security model. The material aesthetic is created with matte layers, subtle strokes and clean typography. Avoid cyan, violet, neon, heavy shadow, decorative gradient and expensive GPU-driven animation.

| Role | Dark sRGB | Light sRGB |
| --- | --- | --- |
| App background | `#07090C` | `#F5F6F8` |
| Graphene header | `#0B0F14` | `#F5F6F8` |
| Recessed sidebar | `#0A0D11` | `#F0F3F6` |
| Card | `#0D1116` | `#FFFFFF` |
| Subtle inset | `#131920` | `#F1F3F6` |
| Raised tile | `#191F27` | `#F1F3F6` |
| Selected navigation | `#202934` | `#E3E9EF` |
| Primary text | `#F2F4F7` | `#19222B` |
| Secondary text | `#A7B0BA` | `#495967` |
| Primary titanium | `#B7C0C9` | `#344452` |
| Hover titanium | `#D8DEE5` | `#273845` |
| Stroke | `#2A313A` | `#D0D9E0` |
| Champagne accent (rare) | `#B89B6A` | `#84693C` |

WinUI semantic tokens in `src/PaRuX.App/Styles/Theme.xaml` are authoritative. Retain every existing key, light-mode support and OS-defined High Contrast resources. Avoid copying literal hex colors into individual views. The palettes specify foreground pairs with normal-text contrast targets of at least 4.5:1. Only rendered Windows/Android tests can establish final accessibility; Mica and antialiasing can affect perceived contrast.

e effective contrast and must be evaluated in the rendered app.

Android launches in the graphite dark appearance by default, including its native splash/status bar; `PaRuXTheme(darkTheme = false)` still supplies a separate accessible light palette for a future explicit user selector. The OS wallpaper colour override is disabled by default to keep the PaRuX brand predictable. The Android theme switch itself is **not yet a user-facing setting**.

## Scale and components

Target spacing scale in effective pixels: 4, 8, 12, 16, 24, 32, 48, 64. Use 8–16 for compact control groups, 16–24 for cards and 24–32 for workspace sections; adapt to available width and text expansion. Recommended radii: 4 for small controls, 8 for cards, 12 for dialogs. Prefer Windows system typography (Segoe UI / Segoe UI Variable where available); suggested type sizes are 12 caption, 14 body, 16 emphasized body, 20 section and 28 page title. Use system font scaling and allow wrapping.

Interactive controls must retain visible keyboard focus, clear accessible names and sufficient pointer/touch target size. Use shared WinUI styles and theme resources, with normal, hover, pressed, disabled and focus states; do not replace control templates without verifying behavior. Sidebar, top bar, cards, dialogs, fields, buttons, grids, icons, shadows and animations need shared definitions as each is migrated. Keep motion subtle and respect Windows animation settings. Desktop supports mouse and keyboard; a future console shell must provide controller focus, distance-readable type and a virtual keyboard without duplicating application services.

## Flat quick-chat bubble (2026-11-01; source implemented, NOT RUN)

The collapsed WinUI Quick Chat surface now consumes light/dark/high-contrast
semantic raised-surface, border and primary-text resources instead of a decorative
gradient and fixed white text. Existing pointer, drag, message and focus behavior
was not intentionally changed. Static XAML regression source is included in the
local Windows test gate; contrast in rendered Mica/translucency, keyboard focus,
DPI and actual light/high-contrast appearance remain unverified.

## Verification and migration gates

1. Migrate shared tokens and a small set of shell/Home consumers while preserving existing resource keys and light/high-contrast behavior.
2. Build the affected Windows application; run relevant tests and inspect the actual app at multiple sizes, DPI scales and both languages. Record command, outcome and environment in `PARUX_EXECUTION_PLAN.md`.
3. Check navigation with keyboard and screen reader, focus visibility, text expansion, theme changes and measured contrast in actual rendered states.
4. Audit remaining Chat, models, agents, Computer Use, coding, media, gaming, settings, onboarding, downloads, notifications, dialogs and empty/loading/error states before claiming full coverage.
5. Treat physical controller, TV/display, streaming and gaming performance as separate acceptance gates. No screen-wide WCAG compliance, logo approval or physical acceptance is claimed by this document.
