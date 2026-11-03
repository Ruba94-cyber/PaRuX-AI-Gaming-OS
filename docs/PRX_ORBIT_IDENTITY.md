# PaRuX PX Orbit identity — owner-approved 2026-11-03

The legacy blue/violet PRX Orbit identity has been **superseded by explicit owner approval**. The adopted B+D hybrid is a titanium/silver **P+X monogram**, champagne X, and a genuine inclined elliptical orbit on near-black graphite. No game controller, AI chip, robot, literal gamepad or blue/violet glow should appear in the general icon.

## Active sources

- `docs/branding/px-orbit/app-mark.svg`: editable main P+X and orbit.
- `docs/branding/px-orbit/chat-mark.svg`: editable companion speech mark, mini X and orbital ring.
- `src/PaRuX.App/Assets/px-orbit-app.png`: shell/main UI artwork.
- `src/PaRuX.App/Assets/px-orbit-chat.png`: floating bubble companion artwork.
- `src/PaRuX.App/Assets/parux.ico`: multi-resolution executable icon.
- `src/PaRuX.App/Assets/prx-orbit-tray.ico`: multi-resolution tray icon; legacy *filename only* retained for compatibility.
- `src/PaRuX.Android/app/src/main/res/drawable-nodpi/prx_orbit_approved.png`: new main artwork with original Android resource name retained.

The legacy `prx-orbit-approved.png` remains only as a historical source and is not packaged or referenced by the active shell. Regenerate Windows icons only from the two `px-orbit-*.png` sources.

## Behaviour

The original lightweight bubble state machinery is retained: a **visible elliptical orbit** with champagne/titanium motion for thinking (2.4 s) and working (1.45 s); static idle ring, brief success/error pulse and dimmed sleep. Skip motion with Windows reduced-motion settings, when the bubble is not visible or is expanded, and during gaming when the bubble is hidden. No persistent voice listening is introduced.

## Release checklist

Inspect real 16/20/24/32/48/64/128/256 px icons, exe/taskbar/tray/shortcut/installer, Android launcher crop, Windows high contrast and light/dark states, drag gestures, animation frame-rate, no GPU-heavy effects and actual gaming suspension on the target PC. **GitHub source integration is not a Windows runtime acceptance result.**


## 2026-11-03 follow-up: orbit motion and Android notification

The quick-chat companion is now used both in the collapsed bubble and its expanded header. Its highlighted dash travels **along the fixed -36° elliptical plane** with a `StrokeDashOffset` animation; the entire ellipse is not spun as a second competing orbital path. An original static orbit stays present in the logo, while the animated segment adds movement only while visible/busy. Reduced-motion keeps a static highlighted segment.

Android notification vector is a monochrome PX plus ellipse (the OS owns tint); the launcher remains the detailed colored bitmap. The ICO generator reproduces matching 16, 20, 24, 32, 48, 64, 128, and 256 px frames for both app and tray. This is source coverage only: perform actual small-size and motion acceptance on Windows hardware.

## Adaptive launcher safe area and motion completion

Android adaptive icons use a centered 68dp foreground inside the 108dp canvas (20dp inset each side) so the PX/X and the orbital stroke remain within the system's 66dp safe-zone guidance even with circular/rounded OEM masks. The monochrome foreground uses the matching PX Orbit status silhouette and is offered to launchers that support themed icons. The full-colour launcher remains graphite/titanium/champagne by default. Quick-chat **expansion transitions** now respect Windows reduced-motion in addition to the orbital status animation. Validate actual 108dp Android icon clipping and light/dark themed rendering on a device; source checks alone do not replace that evidence.


## Local, offline source and publish gate

Run `powershell -ExecutionPolicy Bypass -File tools\branding\verify-px-orbit-assets.ps1` to inspect existing PNG/ICO frames, XAML and Android artwork without modifying the checkout. The normal Windows `tools\validate-software-complete.ps1` gate now calls the same verifier **after** self-contained WinUI publish with `-PublishDirectory`; it compares every published icon's SHA-256 with the source and refuses mismatched/stale brand assets. This is a static/payload gate, not evidence that Windows, Android, animation, installer or accessibility look correct in a real display.

The direct Windows MSI builder (`scripts\build-msi.ps1`) invokes the same read-only PX Orbit asset checker **even with `-SkipPublish`**, so stale old-brand publish folders cannot produce a new MSI unnoticed. It verifies exact source/publish icon hashes before WiX packaging; installation tests and Windows icon-cache refresh remain physical acceptance work.
