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
| Constructor options (52 BaseWindow, 1 BrowserWindow, 46 webPreferences) | 28 | 5 | 66 |
| Methods (~137) | 30 | 6 | ~101 |
| Events (35) | 14 | 0 | 21 |
| Static methods (5) | 0 | 0 | 5 |

## Done since the audit

- Lookup: `Id`, `GetAllWindows`, `GetFocusedWindow`, `FromWebContents`, `FromId`.
- Runtime setters/getters: resizable, minimizable, maximizable, closable (+ `Closable` option; code can still close),
  minimum/maximum size.
- Sizes: `GetSize`, `GetPosition`, `Get/SetContentBounds`, `Get/SetContentSize`, `UseContentSize`, `Center` options.
- `PageTitleUpdated` (window and webContents; window title follows the page unless cancelled), `ReadyToShow`.
- `BackgroundColor` option, `Set/GetBackgroundColor`; `Icon` (path) and `IconImage` options, `SetIcon` (path or
  `NativeImage`, every scale factor). The `Icon` option was ignored before `native-image`.
- `Destroy`.
- Taskbar: `SetProgressBar` (`ProgressBarOptions.Mode`), `FlashFrame`, `SetSkipTaskbar` and the `SkipTaskbar` option,
  `SetOverlayIcon` (`NativeImage`; the path overload is gone, so `null` is unambiguous), `SetThumbarButtons` (Windows: Windows `ITaskbarList3` and `FlashWindowEx` (`WindowsTaskbar`); macOS the Dock tile
  (`MacDock`: a progress bar over the icon, `requestUserAttention:`); Linux X11 (`X11Window`: the urgency hint,
  `_NET_WM_STATE_SKIP_TASKBAR`). Gaps: Linux progress, macOS error/paused colours (DRIFT W20, W23).
- Windows only, as in Electron: `SetAppDetails` (`AppDetailsOptions`: the window property store's
  `PKEY_AppUserModel_*`), `SetThumbnailClip` and `SetThumbnailToolTip` (`ITaskbarList3`; clips run on an STA thread
  because the shell's proxy rejects the NULL clip that clears them), `BackgroundMaterial` option and
  `SetBackgroundMaterial` (`DWMWA_SYSTEMBACKDROP_TYPE`, Windows 11 22H2+). The material is set but hidden behind CEF's
  opaque painting (DRIFT W24); `titleBarOverlay` is missing (W25).
- `getNativeWindowHandle` (`W.get_window_handle`: the content `NSView`, `HWND` or X11 `Window`).
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
  modifiers), through DevTools `Input.dispatchKeyEvent`, so the browser's own shortcuts see them; and for mouse
  events (`mouseDown`, `mouseUp`, `mouseMove`; button, click count, modifiers) through `Input.dispatchMouseEvent`.
  Wheel events and `mouseEnter`/`mouseLeave`/`contextMenu` types are not supported yet.
- Context menus: webContents `context-menu` (`ContextMenu` event with `ContextMenuParams`: position, link, source,
  page and frame URLs, media type, selection, editability, spelling, `editFlags`), `Menu.popup` (window, x, y,
  callback) and `Menu.closePopup`. As in Electron, the page has no context menu unless the app shows one: CEF's
  default is cancelled through `cef_context_menu_handler_t.run_context_menu` (emptying the model in
  `on_before_context_menu` instead crashes at shutdown on macOS). Popups are `W.show_menu` with the menu bar's
  model builder. On macOS a popup runs AppKit's menu loop, during which CEF's UI tasks wait, so `closePopup`
  goes through `performSelectorOnMainThread` in the tracking run-loop modes. Gaps: without x/y the popup opens
  where the window's last context menu was asked for, not at the pointer; no `positioningItem`, `frame`,
  `linkText`, `altText`, `selectionRect`, `menuSourceType` or `formControlType` params; the default menu's
  suppression is verified on screen on macOS only.

- Window menus: `setMenu`, `removeMenu`, `autoHideMenuBar` (option and property), `setMenuBarVisibility`,
  `isMenuBarVisible`, with the `Menu`/`MenuItem` module (`Menu.setApplicationMenu`/`getApplicationMenu`,
  `getMenuItemById`, `append`/`insert`; types normal, separator, submenu, checkbox, radio; common roles; Electron
  accelerators). Routes: on Windows and Linux a CEF Views bar of `cef_menu_button_t` above the page, with
  `cef_menu_model_t` dropdowns, `W.set_accelerator`/`D.on_accelerator` for shortcuts (low priority, so the page sees
  keys first) and `D.on_key_event` for Alt (auto-hide toggle, Alt+mnemonic); on macOS an `NSMenu` main menu (OS),
  where editing, About and Hide roles are AppKit actions on the responder chain and everything else reaches C#
  through a runtime-built target class. `getContentBounds` excludes the bar. macOS gets Electron's default
  application menu (app, File, Edit, View, Window) unless the app sets one during `Ready`; unlike Electron,
  Windows and Linux get no default bar. Webcontents gains `undo`, `redo`, `cut`, `copy`, `paste`,
  `pasteAndMatchStyle`, `delete`, `selectAll`, `reloadIgnoringCache`. Gaps: menu item icons, sublabels, tooltips and `registerAccelerator`; most macOS-only roles (tabs, speech, recent documents,
  `shareMenu`); the click callback gets no `KeyboardEvent`; `Command`/`Super` accelerators are ignored on Windows
  and Linux (CEF accelerators have no such modifier); Alt shows an auto-hidden bar until pressed again, rather than
  until the menu is used; the bar takes CEF Views' default look.

## Next, by importance

1. Remaining webPreferences:
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
2. `setParentWindow` — CEF Views sets the parent only at creation, so this needs OS code (`addChildWindow:`, an owner
   `HWND`, `XSetTransientForHint`).
3. `titleBarStyle`, `titleBarOverlay`, `trafficLightPosition` — `D.with_standard_window_buttons`, `D.get_titlebar_height`, `W.add_overlay_view`, OS.
4. `showInactive` — OS.
5. Window state persistence (`windowStatePersistence`, `getNormalBounds`, `persisted-state-restored`) — Oriel.
6. `opacity`, `hasShadow`, `setIgnoreMouseEvents`, content protection, `setKiosk`, `will-*`/`resized`/`moved` events — OS.

Accuracy gaps in done items: `Shown`/`Hidden` fire only from `Show()`/`Hide()`, not when the OS shows or hides the
window; `devTools: false` blocks
`OpenDevTools` and, through `cef_command_handler_t`, Chrome's DevTools commands (F12 is live in a Views window and is
verified blocked; Chrome's default context menu, with its Inspect item, is no longer shown);
`zoomFactor` re-applies on an origin whose zoom was explicitly reset to 0.

## Platform notes

- **macOS full screen**: native full screen moves the window to its own Space and blacks out other
  displays; `SimpleFullScreen` (option) / `SetSimpleFullScreen` avoid it. With the option set, the
  green button, its hover menu and Window ▸ Enter Full Screen use simple full screen too: the window gets a
  runtime subclass overriding `toggleFullScreen:`, as Electron's NSWindow subclass does. Page-initiated full
  screen (`requestFullscreen`) also goes through `toggleFullScreen:`, so it becomes simple full screen as well
  (untested).
- **Minimize/restore events** are polled (250 ms) on top of bounds callbacks: CEF reports neither, and on
  macOS minimizing changes no bounds.

## Not applicable

`nodeIntegration*`, `webviewTag`, `enableWebSQL`, `v8CacheOptions` and other Node/`<webview>` preferences;
deprecated `BrowserView` methods and `enableDeprecatedPaste`.
