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

## Done since the audit

- Lookup: `Id`, `GetAllWindows`, `GetFocusedWindow`, `FromWebContents`, `FromId`.
- Runtime setters/getters: resizable, minimizable, maximizable, closable (+ `Closable` option; code can still close),
  minimum/maximum size.
- Sizes: `GetSize`, `GetPosition`, `Get/SetContentBounds`, `Get/SetContentSize`, `UseContentSize`, `Center` options.
- `PageTitleUpdated` (window and webContents; window title follows the page unless cancelled), `ReadyToShow`.
- `BackgroundColor` option, `Set/GetBackgroundColor`; `Icon` option, `SetIcon` (PNG/JPEG).
- `Destroy`.
- webContents: `LoadFile` (query, hash), `Reload`, `GetUrl`, `GetTitle`, `did-start-loading`, `did-stop-loading`,
  `did-finish-load`, `did-fail-load`.

## Next, by importance

1. Window menus: `setMenu/removeMenu`, `autoHideMenuBar`, menu visibility — Oriel/OS (with the `Menu` module).
2. `webPreferences`: `devTools`, `zoomFactor`, `webSecurity`, fonts, `spellcheck`, `session`/`partition` — `cef_browser_settings_t`, `H`, request contexts.
3. `parent`/`modal`, `setParentWindow`, `isModal`, `getChildWindows` — `D.get_parent_window`, `D.is_window_modal_dialog`.
4. `titleBarStyle`, `titleBarOverlay`, `trafficLightPosition` — `D.with_standard_window_buttons`, `D.get_titlebar_height`, `W.add_overlay_view`, OS.
5. `showInactive`, `skipTaskbar`, `flashFrame`, `setProgressBar` — OS.
6. `loadURL` options (userAgent, extraHeaders, httpReferrer, postData) — `cef_request_t` + `load_request`.
7. Window state persistence (`windowStatePersistence`, `getNormalBounds`, `persisted-state-restored`) — Oriel.
8. `opacity`, `hasShadow`, `setIgnoreMouseEvents`, content protection, `setKiosk`, `will-*`/`resized`/`moved` events — OS.

Accuracy gaps in done items: `Shown`/`Hidden` fire only from `Show()`/`Hide()`, not when the OS shows or hides the
window; `SetIcon` takes a file path, not Electron's `NativeImage` (that module comes later).

## Platform notes

- **macOS full screen**: native full screen moves the window to its own Space and blacks out other
  displays; `SimpleFullScreen` (option) / `SetSimpleFullScreen` avoid it. The title bar's green button
  still uses native full screen even with the option set.
- **Minimize/restore events** are polled (250 ms) on top of bounds callbacks: CEF reports neither, and on
  macOS minimizing changes no bounds.

## Not applicable

`nodeIntegration*`, `webviewTag`, `enableWebSQL`, `v8CacheOptions` and other Node/`<webview>` preferences;
deprecated `BrowserView` methods and `enableDeprecatedPaste`.
