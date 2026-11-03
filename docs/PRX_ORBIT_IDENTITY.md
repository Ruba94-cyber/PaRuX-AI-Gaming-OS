# PaRuX PX Orbit identity — owner-approved 2026-11-03

The legacy blue/violet PRX Orbit identity has been **superseded by explicit owner approval**. The adopted B+D hybrid is a titanium/silver **P+X monogram**, champagne X, and a genuine inclined elliptical orbit on near-black graphite. No game controller, AI chip, robot, literal gamepad or blue/violet glow should appear in the general icon.

## Active sources

- `docs/branding/px-orbit/app-mark.svg`: editable main P+X and orbit.
- `docs/branding/px-orbit/chat-mark.svg`: historical companion speech concept; not the floating Quick Chat icon.
- `src/PaRuX.App/Assets/px-orbit-app.png`: shell/main UI artwork.
- `src/PaRuX.App/Assets/px-orbit-chat.png`: retained companion asset used by the tray generator; not displayed inside Quick Chat.
- `src/PaRuX.App/Assets/parux.ico`: multi-resolution executable icon.
- `src/PaRuX.App/Assets/prx-orbit-tray.ico`: multi-resolution tray icon; legacy *filename only* retained for compatibility.
- `src/PaRuX.Android/app/src/main/res/drawable-nodpi/prx_orbit_approved.png`: new main artwork with original Android resource name retained.

The legacy `prx-orbit-approved.png` remains only as a historical source and is not packaged or referenced by the active shell. Regenerate Windows icons only from the two `px-orbit-*.png` sources.

## Behaviour

The floating Quick Chat retains its **pre-branding pill, spacing, hit targets, drag/expand behavior and original rotating elliptical status ring**. Only the inner 30px/20px monogram now uses the new PX Orbit app logo through `PrxOrbitMarkTemplate`; status colors use champagne/titanium, with semantic success/error colors. The original rotating ring uses 2.4 s for thinking and 1.45 s for working, with the original idle/sleep/pulse handling. Skip motion with Windows reduced-motion, when hidden or expanded, and during gaming when the bubble is hidden. No persistent voice listening is introduced.

## Release checklist

Inspect real 16/20/24/32/48/64/128/256 px icons, exe/taskbar/tray/shortcut/installer, Android launcher crop, Windows high contrast and light/dark states, drag gestures, animation frame-rate, no GPU-heavy effects and actual gaming suspension on the target PC. **GitHub source integration is not a Windows runtime acceptance result.**


## 2026-11-03 follow-up: orbit motion and Android notification

Correction after owner feedback: the temporary speech-bubble companion icon and dash-offset orbit are **not** used in the floating Quick Chat. Both logo slots display the approved PX monogram while the old floating pill and the old -25° to 335° rotating status ellipse remain unchanged in layout and motion. Reduced-motion leaves the ring static.

Android notification vector is a monochrome PX plus ellipse (the OS owns tint); the launcher remains the detailed colored bitmap. The ICO generator reproduces matching 16, 20, 24, 32, 48, 64, 128, and 256 px frames for both app and tray. This is source coverage only: perform actual small-size and motion acceptance on Windows hardware.

## Adaptive launcher safe area and motion completion

Android adaptive icons use a centered 68dp foreground inside the 108dp canvas (20dp inset each side) so the PX/X and the orbital stroke remain within the system's 66dp safe-zone guidance even with circular/rounded OEM masks. The monochrome foreground uses the matching PX Orbit status silhouette and is offered to launchers that support themed icons. The full-colour launcher remains graphite/titanium/champagne by default. Quick-chat **expansion transitions** now respect Windows reduced-motion in addition to the orbital status animation. Validate actual 108dp Android icon clipping and light/dark themed rendering on a device; source checks alone do not replace that evidence.


## Local, offline source and publish gate

Run `powershell -ExecutionPolicy Bypass -File tools\branding\verify-px-orbit-assets.ps1` to inspect existing PNG/ICO frames, XAML and Android artwork without modifying the checkout. The normal Windows `tools\validate-software-complete.ps1` gate now calls the same verifier **after** self-contained WinUI publish with `-PublishDirectory`; it compares every published icon's SHA-256 with the source and refuses mismatched/stale brand assets. This is a static/payload gate, not evidence that Windows, Android, animation, installer or accessibility look correct in a real display.

The direct Windows MSI builder (`scripts\build-msi.ps1`) invokes the same read-only PX Orbit asset checker **even with `-SkipPublish`**, so stale old-brand publish folders cannot produce a new MSI unnoticed. It verifies exact source/publish icon hashes before WiX packaging; installation tests and Windows icon-cache refresh remain physical acceptance work.

## Local browser client

The existing loopback/reverse-proxy browser client now uses the same graphite/titanium semantic palette in both light/dark mode, has a fixed miniature metallic PX+orbit wordmark on both the web workspace and its sign-in screen, and removes the old violet accent colors. The monogram is an inline inert SVG: no CDN, external icon request or JavaScript asset loader. Authentication, token handling and CSP logic are unchanged. The mobile navigation keeps the reduced-size brand icon visible. Verify browser rendering on target devices before release.
