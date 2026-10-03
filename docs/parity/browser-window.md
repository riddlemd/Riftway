# BrowserWindow / BaseWindow parity

Against Electron **v44.5.1** (`docs/api/browser-window.md`, `base-window.md`, `structures/*-window-options.md`,
`structures/web-preferences.md`). Audited 2026-10-03; update this file as items land.

Status: **done**, **partial**, **missing**, or **n/a** (Node/`<webview>` features with no meaning in Oriel).
Route: `W` = `cef_window_t`, `V` = `cef_view_t`, `D` = `cef_window_delegate_t`, `H` = `cef_browser_host_t`,
**OS** = needs native code (Objective-C runtime, Win32, X11/Wayland), **Oriel** = bookkeeping in C#.

Event naming: Electron `'x'` → past-tense .NET event (`focus`→`Focused`, `close`→`Closing` with
`CancelEventArgs`, `closed`→`Closed`, `enter-full-screen`→`EnteredFullScreen`, ...).

## Summary

| Area | Done | Partial | Missing |
|---|---|---|---|
| Constructor options (52 BaseWindow, 1 BrowserWindow, 46 webPreferences) | 17 | 3 | 79 |
| Methods (~137) | 26 | 2 | ~109 |
| Events (35) | 14 | 0 | 21 |
| Static methods (5) | 0 | 0 | 5 |

## Next, by importance

1. `BrowserWindow.GetAllWindows/GetFocusedWindow/FromWebContents/FromId` and `Id` — Oriel.
2. Window menus: `setMenu/removeMenu`, `autoHideMenuBar`, menu visibility — Oriel/OS (with the `Menu` module).
3. `webPreferences`: `devTools`, `zoomFactor`, `webSecurity`, fonts, `spellcheck`, `session`/`partition` — `cef_browser_settings_t`, `H`, request contexts.
4. `parent`/`modal`, `setParentWindow`, `isModal`, `getChildWindows` — `D.get_parent_window`, `D.is_window_modal_dialog`.
5. Runtime setters for `resizable`, `minimizable`, `maximizable`, `closable`, min/max size — `D.can_*`, size callbacks + `V.invalidate_layout`.
6. `getSize`, `getPosition`, `get/setContentBounds`, `get/setContentSize`, `useContentSize` — `V`, `W.get_client_area_bounds_in_screen`.
7. `page-title-updated` — display handler `on_title_change`.
8. `ready-to-show` — load handler (first paint approximation).
9. `backgroundColor` — `V.set_background_color` / browser settings.
10. `titleBarStyle`, `titleBarOverlay`, `trafficLightPosition` — `D.with_standard_window_buttons`, `D.get_titlebar_height`, `W.add_overlay_view`, OS.
11. `icon`/`setIcon` — `W.set_window_icon`, `W.set_window_app_icon`.
12. `destroy`, `showInactive` — `H.close_browser(force)` + `W.close`; OS.
13. `skipTaskbar`, `flashFrame`, `setProgressBar` — OS.
14. `loadFile`, `reload`, `loadURL` options — Oriel + `cef_browser_t`/`cef_frame_t`.
15. Window state persistence (`windowStatePersistence`, `getNormalBounds`, `persisted-state-restored`) — Oriel.

Accuracy gap in a done item: `Shown`/`Hidden` fire only from `Show()`/`Hide()`, not when the OS shows or
hides the window.

## Platform notes

- **macOS full screen**: native full screen moves the window to its own Space and blacks out other
  displays; `SimpleFullScreen` (option) / `SetSimpleFullScreen` avoid it. The title bar's green button
  still uses native full screen even with the option set.
- **Minimize/restore events** are polled (250 ms) on top of bounds callbacks: CEF reports neither, and on
  macOS minimizing changes no bounds.

## Not applicable

`nodeIntegration*`, `webviewTag`, `enableWebSQL`, `v8CacheOptions` and other Node/`<webview>` preferences;
deprecated `BrowserView` methods and `enableDeprecatedPaste`.
