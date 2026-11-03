# PaRuX AI & Gaming OS design system

Status: foundation specification, in progress. The application remains a Windows application; “OS” describes its integrated experience, not a replacement operating system. Existing product names, identifiers, data paths and installer identity must not be changed without a compatibility and migration review.

## Current inventory and scope

The Windows desktop uses WinUI. `src/PaRuX.App/Styles/Theme.xaml` defines theme dictionaries, semantic brushes, scales and styles, merged by the application. `App.xaml` also owns resources used by code lookup. `MainWindow.xaml` and its partial classes own shell navigation and responsive/accessibility behavior. This is a partial source audit, not a screen-by-screen usability or accessibility certification. Hardcoded values and existing visuals outside the shared resources remain to be inventoried and migrated incrementally. Android is a separate client and has not been audited in this cycle.

The owner-approved PX Orbit identity is integrated into source: `px-orbit-app.png` (shell), `px-orbit-chat.png` (floating assistant), `parux.ico` (executable) and `prx-orbit-tray.ico` (tray, filename retained for compatibility). Android launcher foreground uses the same artwork. Older `prx-orbit-approved.png` and previews are retained as unshipped history. Hardware and packaging acceptance (installer, tray, pinned shortcuts and 16–256 px) remains outstanding.

## Identity direction

Use the full display name **PaRuX AI & Gaming OS**, with **PaRuX** as the short name. Prefer a restrained geometric mark, near-black graphite surfaces and satin titanium emphasis with only a restrained champagne secondary accent. Avoid purple, decorative gradients, heavy glow and literal gamepad/brain motifs. Maintain legibility and distinction at small sizes; do not imitate existing platform marks.

**Current approved mark (2026-11-03):** fused metallic P+X monogram with titanium P and restrained champagne X, crossed by a clear inclined orbital ring. The companion bubble uses the same orbit plus a small X and a neutral metallic conversation silhouette. Neither icon depicts a game controller, microchip, AI robot, or other literal product category. Editable SVG masters: `docs/branding/px-orbit/`; raster and ICO variants are checked into the Windows assets directory. The previous P-only construction in `docs/branding/concept-01/` is superseded.

Final acceptance still requires physical Windows checks of the packaged icons, high contrast, Windows reduced-motion, taskbar/tray and installer. The graphical source integration does not claim those tests have passed.

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
