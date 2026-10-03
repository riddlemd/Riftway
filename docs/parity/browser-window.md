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
| Constructor options (52 BaseWindow, 1 BrowserWindow, 46 webPreferences) | 28 | 4 | 67 |
| Methods (~137) | 29 | 2 | ~106 |
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
- `loadURL` options (`LoadUrlOptions`): `httpReferrer` (string or `Referrer` with policy), `userAgent`, `extraHeaders`,
  `postData` (`UploadRawData`, `UploadFile`); webContents `setUserAgent`/`getUserAgent`. Route: `frame->load_url` plus a
  `cef_resource_request_handler_t` that rewrites that one main-frame navigation request (`load_request` is killed for
  cross-origin initiators); the user agent is DevTools `Emulation.setUserAgentOverride`, so headers and
  `navigator.userAgent` agree and it survives navigations and renderer swaps. Gaps: `baseURLForDataURL` throws
  `NotSupportedException` (CEF cannot commit a `data:` URL under another base); `UploadFile` `offset`/`length` throw
  (CEF uploads whole files) and `modificationTime` is ignored; the user agent override sets no client-hints metadata
  (`Sec-CH-UA*`, `navigator.userAgentData`); `getUserAgent` without an override is empty until DevTools answers, just after the window
  is created; a reload likely sends a plain request, without the options (untested; Electron's navigation entry keeps them); `LoadUrl` returns void, not Electron's promise.

- webPreferences: `devTools`, `zoomFactor`, `javascript`, `images`, `textAreasAreResizable`, `webgl`,
  `defaultFontFamily` (all but `math`, which throws), `defaultFontSize`, `defaultMonospaceFontSize`, `disableDialogs`.
  Routes: `cef_browser_settings_t`; the two font sizes go through DevTools `Page.setFontSizes`, because Chrome-style
  browsers overwrite them from the profile; `zoomFactor` is `H.set_zoom_level` on each origin not yet zoomed;
  `disableDialogs` is a `cef_jsdialog_handler_t` that suppresses. Zoom is per origin and lasts for the session, as in
  Electron: Chrome-style profiles save it across launches, so Oriel clears `partition.per_host_zoom_levels` at startup.
- webContents: `openDevTools` (own window only; no docked modes), `closeDevTools`, `isDevToolsOpened`, `toggleDevTools`,
  `setZoomFactor`/`getZoomFactor`, `setZoomLevel`/`getZoomLevel`, `executeJavaScript` (`ExecuteJavaScriptAsync`,
  DevTools `Runtime.evaluate` in the main world; rejects with `JavaScriptException`).
- `parent`, `modal`, `getParentWindow`, `getChildWindows`, `isModal` (`D.get_parent_window`, `D.is_window_modal_dialog`);
  children close with their parent. The constructor now runs from any thread. A modal window keeps focus from its
  parent on macOS; on Linux (openbox) it does not, because the window manager ignores the modal hint, and Electron's
  Linux workaround (disabling the parent's input) has no CEF equivalent yet.
- webContents `sendInputEvent` for keyboard events (`keyDown`, `keyUp`, `char`; Electron key names and
  modifiers), through DevTools `Input.dispatchKeyEvent`, so the browser's own shortcuts see them. Mouse and wheel
  events are not supported yet.

## Next, by importance

1. Window menus: `setMenu/removeMenu`, `autoHideMenuBar`, menu visibility — Oriel/OS (with the `Menu` module).
2. Remaining webPreferences:
   - Need other modules: `session`/`partition` and `spellcheck` (Session, request contexts), `offscreen` (offscreen
     rendering), `transparent`.
   - Process-wide in CEF (Chromium switches, not per window): `webSecurity`, `allowRunningInsecureContent`,
     `experimentalFeatures`, `enableBlinkFeatures`/`disableBlinkFeatures`, `autoplayPolicy`, `sandbox`. Decide between
     an app-level option and a per-window gap.
   - Profile-wide in Chrome style, so they throw `NotSupportedException`: `minimumFontSize`, `defaultEncoding`.
   - Not yet investigated: `backgroundThrottling`, `navigateOnDragDrop`, `safeDialogs`/`safeDialogsMessage`,
     `accessibleTitle`, `scrollBounce`, `imageAnimationPolicy`, `zoomMode`, `focusOnNavigation`,
     `enablePreferredSizeMode`, `disableHtmlFullscreenWindowResize`, `additionalArguments`.
   - `contextIsolation` is always on; `false` is not offered.
3. `setParentWindow` — CEF Views sets the parent only at creation, so this needs OS code (`addChildWindow:`, an owner
   `HWND`, `XSetTransientForHint`).
4. `titleBarStyle`, `titleBarOverlay`, `trafficLightPosition` — `D.with_standard_window_buttons`, `D.get_titlebar_height`, `W.add_overlay_view`, OS.
5. `showInactive`, `skipTaskbar`, `flashFrame`, `setProgressBar` — OS.
6. Window state persistence (`windowStatePersistence`, `getNormalBounds`, `persisted-state-restored`) — Oriel.
7. `opacity`, `hasShadow`, `setIgnoreMouseEvents`, content protection, `setKiosk`, `will-*`/`resized`/`moved` events — OS.

Accuracy gaps in done items: `Shown`/`Hidden` fire only from `Show()`/`Hide()`, not when the OS shows or hides the
window; `SetIcon` takes a file path, not Electron's `NativeImage` (that module comes later); `devTools: false` blocks
`OpenDevTools` and, through `cef_command_handler_t`, Chrome's DevTools commands (F12 is live in a Views window and is
verified blocked; the context menu's Inspect goes through the same handler but is not tested);
`zoomFactor` re-applies on an origin whose zoom was explicitly reset to 0.

## Platform notes

- **macOS full screen**: native full screen moves the window to its own Space and blacks out other
  displays; `SimpleFullScreen` (option) / `SetSimpleFullScreen` avoid it. The title bar's green button
  still uses native full screen even with the option set.
- **Minimize/restore events** are polled (250 ms) on top of bounds callbacks: CEF reports neither, and on
  macOS minimizing changes no bounds.

## Not applicable

`nodeIntegration*`, `webviewTag`, `enableWebSQL`, `v8CacheOptions` and other Node/`<webview>` preferences;
deprecated `BrowserView` methods and `enableDeprecatedPaste`.
