# ADR-002 — Desktop-only application (mobile support cut)

## Status

Accepted — decided by the owner (Jonathan Lindsay), 2026-08-10, during the
Wave 0 → Wave 1 review.

## Context

Idle Detection is a mandatory, non-negotiable product requirement
(`CLAUDE.md §4`). The Idle Detection API exists only in Chromium browsers
(Chrome/Edge on desktop, Chrome on Android). Apple has formally declined to
implement it in WebKit, and every iOS browser is WebKit underneath, so no
iPhone or iPad can ever satisfy the capability gate — installing the PWA to
the home screen does not change the engine. This directly contradicted the
approved GUI package, which included iOS onboarding instructions and an
active "iPhone · Safari PWA" session in the Devices mockup
(`docs/wave-0-assessment.md` §9.1).

Presented with keeping strict desktop/Android support, an audited iOS
exception path, or cutting mobile entirely, the owner chose to cut mobile.

## Decision

**The Weed Man Sales Hub is a desktop-only application, for all roles.**

- Supported browsers: Chromium desktop (Chrome, Edge) on Windows/macOS/Linux.
- No iOS, iPadOS, or Android support. Phones and tablets are not work
  devices and are not companion devices.
- The mandatory Idle Detection gate is therefore satisfiable on every
  supported browser; no exception path exists or will be built.

## Consequences

Superseded parts of the approved GUI package (retained in `reference-gui/`
as history, no longer product requirements):

- `weed_man_mobile_menu_navigation_gui_mockup.html` (mobile shell, 5-tab
  navigation, Add Sale bottom sheet) — dropped; the desktop app shell is the
  only shell.
- iOS "Add to Home Screen" onboarding path in the PWA onboarding mockup —
  dropped. PWA install remains as **desktop** PWA install only.
- Mobile/touch layouts referenced in other mockups — informative only.

Requirements that stay, retargeted to desktop:

- Web Push notifications remain (desktop Chrome/Edge support Web Push);
  delivery is to desktop browsers only.
- The PWA onboarding sequence remains, minus the iOS branch.
- Device/session surfaces (Profile → Devices & Sessions, Sync Health) now
  list desktop browsers only; the `pwa_installed` and device-metadata fields
  on `user_sessions` are unchanged.
- The frontend browser matrix (docs/05) reduces to Chromium desktop; an
  unsupported browser still gets the remediation screen rather than the
  working app.

Simplifications unlocked for later waves: no mobile breakpoints in the
rebuilt frontend, no iOS push workarounds, one Idle Detection code path,
"Sales entries require a live connection" loses its mobile-offline edge
cases (the offline queue still exists for desktop network drops).

No Wave 0 code changes were required: the capability gate, session
metadata, and policies were already device-agnostic.
