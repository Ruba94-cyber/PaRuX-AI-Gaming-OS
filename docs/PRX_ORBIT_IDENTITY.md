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
