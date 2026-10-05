# BrowserWindow / BaseWindow parity

Against Electron **v44.5.1** (`docs/api/browser-window.md`, `base-window.md`, `structures/*-window-options.md`,
`structures/web-preferences.md`). Audited 2026-10-03; update this file as items land.

Status: **done**, **partial**, **missing**, or **n/a** (Node/`<webview>` features with no meaning in Oriel).
Route: `W` = `cef_window_t`, `V` = `cef_view_t`, `D` = `cef_window_delegate_t`, `H` = `cef_browser_host_t`,
**OS** = needs native code (Objective-C runtime, Win32, X11/Wayland), **Oriel** = bookkeeping in C#.

Event naming: Electron `'x'` → past-tense .NET event (`focus`→`Focused`, `close`→`Closing` with
`CancelEventArgs`, `closed`→`Closed`, `enter-full-screen`→`EnteredFullScreen`, ...).

## Summary

Counted from the Checklist at the end: `BaseWindow` and `BrowserWindow` are one union list (a `BrowserWindow` item is also a
`BaseWindow` one). Percentages are over the non-n/a items; the second counts a partial item as half.

| Area | Items | Done | Partial | Missing | n/a | % done | % done + partial/2 |
|---|---|---|---|---|---|---|---|
| BaseWindow + BrowserWindow options | 54 | 27 | 9 | 18 | 0 | 50.0% | 58.3% |
| webPreferences | 46 | 11 | 3 | 25 | 7 | 28.2% | 32.1% |
| Instance methods | 143 | 71 | 19 | 47 | 6 | 51.8% | 58.8% |
| Instance properties | 24 | 11 | 2 | 11 | 0 | 45.8% | 50.0% |
| Events | 35 | 12 | 6 | 17 | 0 | 34.3% | 42.9% |
| Static methods | 6 | 4 | 0 | 1 | 1 | 80.0% | 80.0% |
| **Total** | 308 | 136 | 39 | 119 | 14 | 46.3% | 52.9% |

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
  opaque painting (DRIFT W24).
- Windows shell APIs (branch `win-shell-apis`): `HookWindowMessage`/`UnhookWindowMessage`/`IsWindowMessageHooked`/
  `UnhookAllWindowMessages` (the window subclass calls the hook first, synchronously, and the message goes on),
  `ThickFrame`, `RoundedCorners`, `AccentColor`/`SetAccentColor`/`GetAccentColor` (`DWMWA_BORDER_COLOR`/`DWMWA_CAPTION_COLOR`).
  Thumbar, app details and thumbnail clip re-audited against `taskbar_host.cc` and Chromium's `SetAppDetailsForWindow`.
  Gaps: DRIFT W38-W41.
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

- Window polish, Windows (branch `window-polish`): `ShowInactive` (shown through CEF with `SWP_NOACTIVATE` forced in
  `WM_WINDOWPOSCHANGING`), `Opacity`/`SetOpacity`/`GetOpacity` (`WS_EX_LAYERED` alpha), `HasShadow`/`SetHasShadow` (stored only,
  as Electron on Windows), `SetIgnoreMouseEvents` (`WS_EX_TRANSPARENT`; `Forward` posts mouse moves to the page from a
  low-level mouse hook, as Electron), `SetContentProtection`/`IsContentProtected` (`WDA_EXCLUDEFROMCAPTURE`),
  `Kiosk`/`SetKiosk`/`IsKiosk` (full screen; `IsKiosk` is `IsFullScreen`), `GetNormalBounds` (`GetWindowPlacement`),
  cancellable `WillResize`/`WillMove` (`WM_SIZING`/`WM_MOVING`). Gaps: DRIFT W30, W31, W33.
  Fixed on the way: CEF activated a window created hidden, and any window starting a load, taking the keyboard from
  the window in front; a window meant to be hidden now hands activation back, and loads restore the foreground.

- Title bar (branch `title-bar`): `-webkit-app-region: drag`/`no-drag` in frameless windows (`cef_drag_handler_t` to
  `W.set_draggable_regions`, offset by the page's origin; the overlay is cut out as no-drag). `TitleBarStyle` (frameless on
  Windows and Linux), `TitleBarOverlay`/`SetTitleBarOverlay` (Windows: a CEF Views panel docked top right by
  `W.add_overlay_view`, three buttons as Electron sizes them (45 DIPs, the later two with a 1-DIP spacing) with drawn symbols, Electron's hover tints fading over 150 ms and red close, hidden in full
  screen; colours parsed as Chromium's `ParseCssColorString` does, alpha showing the page through; defaults from
  `COLOR_BTNFACE`/`COLOR_BTNTEXT` and the system caption height). Gaps: DRIFT W34, W37.

- Title bar follow-ups (branch `title-bar-2`): the overlay's buttons follow `Minimizable`/`Maximizable`/`Closable`
  (disabled, greyed symbol); Windows 11 snap layouts over the drawn maximize button: the window answers `HTMAXBUTTON`,
  Chromium's page window passes that hit test up (`HTTRANSPARENT`), and the non-client hover, press and click drive
  the button. `SetParentWindow` on Windows (`GWLP_HWNDPARENT`, then a visible window re-shown in its show state and
  brought to the top, as Electron; no cycle check, modal throws), keeping `GetParentWindow`/`GetChildWindows` and
  closing with the new parent.

## Next, by importance

1. Remaining webPreferences:
   - Need other modules: `spellcheck` (Session, E10), `offscreen` (offscreen
     rendering), `transparent`. `session`/`partition` landed with the `Session` module (DRIFT E1-E11).
   - Process-wide in CEF (Chromium switches, not per window): `webSecurity`, `allowRunningInsecureContent`,
     `experimentalFeatures`, `enableBlinkFeatures`/`disableBlinkFeatures`, `autoplayPolicy`, `sandbox`. Decide between
     an app-level option and a per-window gap.
   - Profile-wide in Chrome style, so they throw `NotSupportedException`: `minimumFontSize`, `defaultEncoding`.
   - Not yet investigated: `backgroundThrottling`, `navigateOnDragDrop`, `safeDialogs`/`safeDialogsMessage`,
     `accessibleTitle`, `scrollBounce`, `imageAnimationPolicy`, `zoomMode`, `focusOnNavigation`,
     `enablePreferredSizeMode`, `disableHtmlFullscreenWindowResize`, `additionalArguments`.
   - `contextIsolation` is always on; `false` is not offered.
2. `setParentWindow` on macOS and Linux — CEF Views sets the parent only at creation; `addChildWindow:`,
   `XSetTransientForHint` (W8).
3. `titleBarStyle` on macOS, `trafficLightPosition`, the Window Controls Overlay page API (W34, W37) — OS, Patch.
4. `showInactive`, `opacity`, `setIgnoreMouseEvents`, content protection, `will-resize`/`will-move` on macOS and Linux (W30) — OS.
5. Window state persistence (`windowStatePersistence`, `getNormalBounds`, `persisted-state-restored`) — Oriel.
6. `resized`/`moved` events, `hasShadow` on macOS and Linux (W31), kiosk presentation options on macOS (W33) — OS.

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

## Checklist

One row per Electron v44.5.1 item. Status: done, partial (exists with a documented gap; DRIFT.md id in the note), missing, n/a.
Parameters in brackets are optional in Electron. `webContents` members are in [web-contents.md](web-contents.md).

### Constructor options (`BaseWindowConstructorOptions` + `BrowserWindowConstructorOptions`)

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `width` | `BrowserWindowOptions.Width` | done |  |
| `height` | `BrowserWindowOptions.Height` | done |  |
| `x` | `BrowserWindowOptions.X` | done |  |
| `y` | `BrowserWindowOptions.Y` | done |  |
| `useContentSize` | `UseContentSize` | done |  |
| `center` | `Center` | done |  |
| `minWidth` | `MinWidth` | done |  |
| `minHeight` | `MinHeight` | done |  |
| `maxWidth` | `MaxWidth` | done |  |
| `maxHeight` | `MaxHeight` | done |  |
| `resizable` | `Resizable` | done |  |
| `movable` | — | missing |  |
| `minimizable` | `Minimizable` | done |  |
| `maximizable` | `Maximizable` | done |  |
| `closable` | `Closable` | done | Honoured on Linux too (W10, superset). |
| `focusable` | — | missing |  |
| `alwaysOnTop` | `AlwaysOnTop` | done |  |
| `fullscreen` | `FullScreen` | done |  |
| `fullscreenable` | — | missing |  |
| `simpleFullscreen` | `SimpleFullScreen` | done | macOS only, as in Electron. |
| `skipTaskbar` | `SkipTaskbar` | done |  |
| `hiddenInMissionControl` | — | missing |  |
| `kiosk` | `Kiosk` | partial | Full screen; macOS presentation options not yet (W33). |
| `name` | — | missing |  |
| `windowStatePersistence` | — | missing |  |
| `title` | `Title` | done |  |
| `icon` | `Icon`, `IconImage` | done | Two options, `IconImage` (`NativeImage`) wins (W26). |
| `show` | `Show` | done |  |
| `frame` | `Frame` | done |  |
| `parent` | `Parent` | done |  |
| `modal` | `Modal` | partial | Linux parent keeps input under window managers that ignore the modal hint (W5). |
| `acceptFirstMouse` | — | missing |  |
| `disableAutoHideCursor` | — | missing |  |
| `autoHideMenuBar` | `AutoHideMenuBar` | done |  |
| `enableLargerThanScreen` | — | missing |  |
| `backgroundColor` | `BackgroundColor` | done |  |
| `hasShadow` | `HasShadow` | partial | Stored only, which is Electron's behaviour on Windows; macOS and Linux not yet (W31). |
| `opacity` | `Opacity` | partial | Windows only for now (W30). |
| `darkTheme` | — | missing |  |
| `transparent` | — | missing |  |
| `type` | — | missing |  |
| `visualEffectState` | — | missing |  |
| `titleBarStyle` | `TitleBarStyle` | partial | Windows (and Linux, unchecked): anything but `Default` is frameless; macOS keeps its title bar (W37). |
| `titleBarOverlay` | `TitleBarOverlay` | partial | Windows: Views buttons docked top right, CSS colours as Chromium parses them; no page API (W34); snap layouts through `HTMAXBUTTON`, buttons follow the window's flags. |
| `accentColor` | `AccentColor` | done | Windows 11, as in Electron: `bool` or CSS string (`AccentColor`); system changes re-applied on DWM's broadcasts (W39). |
| `trafficLightPosition` | — | missing |  |
| `roundedCorners` | `RoundedCorners` | partial | Windows 11 (`DWMWA_WINDOW_CORNER_PREFERENCE`, frameless, off without `thickFrame`, squared in full screen); Linux and the macOS full-screen rule not done (W40). |
| `thickFrame` | `ThickFrame` | partial | Windows: Electron's frameless `GWL_STYLE`, framed loses `WS_THICKFRAME`; full screen and resize insets follow CEF, not Electron (W40). |
| `vibrancy` | — | missing |  |
| `backgroundMaterial` | `BackgroundMaterial` | partial | Windows 11 22H2+ only; set but hidden behind CEF's opaque painting (W24). |
| `zoomToPageWidth` | — | missing |  |
| `tabbingIdentifier` | — | missing |  |
| `webPreferences (BrowserWindow)` | `WebPreferences` | done | Per-option status in the next table. |
| `paintWhenInitiallyHidden (BrowserWindow)` | — | missing |  |

### webPreferences

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `devTools` | `DevTools` | done |  |
| `nodeIntegration` | — | n/a | Node.js |
| `nodeIntegrationInWorker` | — | n/a | Node.js |
| `nodeIntegrationInSubFrames` | — | n/a | Node.js |
| `preload` | `Preload` | done |  |
| `sandbox` | — | missing | Process-wide in CEF (W13). |
| `session` | `Session` | done | Overrides `Partition`; popups cannot take another session (E7). |
| `partition` | `Partition` | done | `persist:` dirs live in the data directory (E1). |
| `zoomFactor` | `ZoomFactor` | partial | Re-applies to an origin whose zoom was reset to 0 (W15). |
| `zoomMode` | — | missing |  |
| `javascript` | `Javascript` | done |  |
| `webSecurity` | — | missing | Process-wide in CEF (W13). |
| `allowRunningInsecureContent` | — | missing | Process-wide in CEF (W13). |
| `images` | `Images` | done |  |
| `imageAnimationPolicy` | — | missing |  |
| `textAreasAreResizable` | `TextAreasAreResizable` | done |  |
| `webgl` | `WebGL` | done |  |
| `plugins` | — | missing |  |
| `experimentalFeatures` | — | missing | Process-wide in CEF (W13). |
| `scrollBounce` | — | missing |  |
| `enableBlinkFeatures` | — | missing | Process-wide in CEF (W13). |
| `disableBlinkFeatures` | — | missing | Process-wide in CEF (W13). |
| `defaultFontFamily` | `DefaultFontFamily` | partial | All families but `math`, which throws (W11). |
| `defaultFontSize` | `DefaultFontSize` | done |  |
| `defaultMonospaceFontSize` | `DefaultMonospaceFontSize` | done |  |
| `minimumFontSize` | `MinimumFontSize` | missing | Property exists but throws NotSupportedException (W12). |
| `defaultEncoding` | `DefaultEncoding` | missing | Property exists but throws NotSupportedException (W12). |
| `backgroundThrottling` | — | missing |  |
| `offscreen` | — | missing |  |
| `contextIsolation` | — | partial | Always on; `false` is not offered (W14). |
| `webviewTag` | — | n/a | <webview> |
| `additionalArguments` | — | missing |  |
| `safeDialogs` | — | missing |  |
| `safeDialogsMessage` | — | missing |  |
| `disableDialogs` | `DisableDialogs` | done |  |
| `navigateOnDragDrop` | — | missing |  |
| `autoplayPolicy` | — | missing | Process-wide in CEF (W13). |
| `disableHtmlFullscreenWindowResize` | — | missing |  |
| `accessibleTitle` | — | missing |  |
| `spellcheck` | — | missing | Needs the Session module. |
| `enableWebSQL` | — | n/a | Not applicable (listed in "Not applicable"). |
| `v8CacheOptions` | — | n/a | Node/V8 option. |
| `enablePreferredSizeMode` | — | missing |  |
| `transparent` | — | missing |  |
| `enableDeprecatedPaste` | — | n/a | Deprecated. |
| `focusOnNavigation` | — | missing |  |

### Instance methods (`BaseWindow` and `BrowserWindow`, union)

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `setContentView(view)` | — | missing | No View module. |
| `getContentView()` | — | missing | No View module. |
| `destroy()` | `Destroy` | done |  |
| `close()` | `Close` | done |  |
| `focus()` | `Focus` | done |  |
| `blur()` | `Blur` | done |  |
| `isFocused()` | `IsFocused` | done |  |
| `isDestroyed()` | `IsDestroyed` | done |  |
| `show()` | `Show` | done |  |
| `showInactive()` | `ShowInactive` | partial | Windows only; macOS and Linux show and focus (W30). |
| `hide()` | `Hide` | done |  |
| `isVisible()` | `IsVisible` | done |  |
| `isModal()` | `IsModal` | done |  |
| `maximize()` | `Maximize` | done |  |
| `unmaximize()` | `Unmaximize` | done |  |
| `isMaximized()` | `IsMaximized` | done |  |
| `minimize()` | `Minimize` | done |  |
| `restore()` | `Restore` | done |  |
| `isMinimized()` | `IsMinimized` | done |  |
| `setFullScreen(flag)` | `SetFullScreen` | done |  |
| `isFullScreen()` | `IsFullScreen` | done |  |
| `setSimpleFullScreen(flag)` | `SetSimpleFullScreen` | done |  |
| `isSimpleFullScreen()` | `IsSimpleFullScreen` | done |  |
| `isNormal()` | — | missing |  |
| `setAspectRatio(aspectRatio, extraSize)` | — | missing |  |
| `setBackgroundColor(color)` | `SetBackgroundColor` | done |  |
| `previewFile(path, displayName)` | — | missing |  |
| `closeFilePreview()` | — | missing |  |
| `setBounds(bounds, animate)` | `SetBounds` | partial | No `animate` argument (macOS-only parameter). |
| `getBounds()` | `GetBounds` | done |  |
| `getBackgroundColor()` | `GetBackgroundColor` | done |  |
| `setContentBounds(bounds, animate)` | `SetContentBounds` | partial | No `animate` argument (macOS-only parameter). |
| `getContentBounds()` | `GetContentBounds` | done |  |
| `getNormalBounds()` | `GetNormalBounds` | done | Windows: `GetWindowPlacement`; elsewhere the last bounds seen while normal. |
| `setEnabled(enable)` | — | missing |  |
| `isEnabled()` | — | missing |  |
| `setSize(width, height, animate)` | `SetSize` | partial | No `animate` argument (macOS-only parameter). |
| `getSize()` | `GetSize` | done |  |
| `setContentSize(width, height, animate)` | `SetContentSize` | partial | No `animate` argument (macOS-only parameter). |
| `getContentSize()` | `GetContentSize` | done |  |
| `setMinimumSize(width, height)` | `SetMinimumSize` | done |  |
| `getMinimumSize()` | `GetMinimumSize` | done |  |
| `setMaximumSize(width, height)` | `SetMaximumSize` | done |  |
| `getMaximumSize()` | `GetMaximumSize` | done |  |
| `setResizable(resizable)` | `SetResizable` | done |  |
| `isResizable()` | `IsResizable` | done |  |
| `setMovable(movable)` | — | missing |  |
| `isMovable()` | — | missing |  |
| `setMinimizable(minimizable)` | `SetMinimizable` | done |  |
| `isMinimizable()` | `IsMinimizable` | done |  |
| `setMaximizable(maximizable)` | `SetMaximizable` | done |  |
| `isMaximizable()` | `IsMaximizable` | done |  |
| `setFullScreenable(fullscreenable)` | — | missing |  |
| `isFullScreenable()` | — | missing |  |
| `setClosable(closable)` | `SetClosable` | done |  |
| `isClosable()` | `IsClosable` | done |  |
| `setHiddenInMissionControl(hidden)` | — | missing |  |
| `isHiddenInMissionControl()` | — | missing |  |
| `setAlwaysOnTop(flag, level, relativeLevel)` | `SetAlwaysOnTop` | partial | No `level` or `relativeLevel`. |
| `isAlwaysOnTop()` | `IsAlwaysOnTop` | done |  |
| `moveAbove(mediaSourceId)` | — | missing |  |
| `moveTop()` | — | missing |  |
| `center()` | `Center` | done |  |
| `setPosition(x, y, animate)` | `SetPosition` | partial | No `animate` argument (macOS-only parameter). |
| `getPosition()` | `GetPosition` | done |  |
| `setTitle(title)` | `SetTitle` | done |  |
| `getTitle()` | `GetTitle` | done |  |
| `setSheetOffset(offsetY, offsetX)` | — | missing |  |
| `flashFrame(flag)` | `FlashFrame` | done |  |
| `setSkipTaskbar(skip)` | `SetSkipTaskbar` | done |  |
| `setKiosk(flag)` | `SetKiosk` | partial | Full screen (W33). |
| `isKiosk()` | `IsKiosk` | done |  |
| `isTabletMode()` | — | missing |  |
| `getMediaSourceId()` | — | missing |  |
| `getNativeWindowHandle()` | `GetNativeWindowHandle` | done | Returns `nint`, not a Buffer (S6). |
| `hookWindowMessage(message, callback)` | `HookWindowMessage` | done | Windows only, as in Electron; `Action<nint, nint>` with the raw values, not Buffers (W38). |
| `isWindowMessageHooked(message)` | `IsWindowMessageHooked` | done |  |
| `unhookWindowMessage(message)` | `UnhookWindowMessage` | done |  |
| `unhookAllWindowMessages()` | `UnhookAllWindowMessages` | done |  |
| `setRepresentedFilename(filename)` | — | missing |  |
| `getRepresentedFilename()` | — | missing |  |
| `setDocumentEdited(edited)` | — | missing |  |
| `isDocumentEdited()` | — | missing |  |
| `focusOnWebView()` | — | missing |  |
| `blurWebView()` | — | missing |  |
| `capturePage(rect, opts)` | — | missing | `NativeImage` exists; the capture is missing. |
| `loadURL(url, options)` | `LoadUrl` | partial | Returns Task; `baseURLForDataURL` throws, `UploadFile` offset/length throw, no client hints, reload drops options (C1, C2, C3, C5). |
| `loadFile(filePath, options)` | `WebContents.LoadFile` | partial | Only on WebContents, no BrowserWindow shortcut; `query` and `hash`, no `search`. |
| `reload()` | `WebContents.Reload` | partial | Only on WebContents, no BrowserWindow shortcut. |
| `setMenu(menu)` | `SetMenu` | done | Drawn by CEF Views on Windows/Linux (W16). |
| `removeMenu()` | `RemoveMenu` | done |  |
| `setProgressBar(progress, options)` | `SetProgressBar` | partial | No Linux progress; macOS error/paused show as plain bar (W20). |
| `setOverlayIcon(overlay, description)` | `SetOverlayIcon` | done | Takes a `NativeImage`. |
| `invalidateShadow()` | — | missing |  |
| `setHasShadow(hasShadow)` | `SetHasShadow` | partial | Stored only, as Electron on Windows; macOS and Linux not yet (W31). |
| `hasShadow()` | `GetHasShadow` | done | Returns the value set. |
| `setOpacity(opacity)` | `SetOpacity` | partial | Windows only for now (W30). |
| `getOpacity()` | `GetOpacity` | done |  |
| `setShape(rects)` | — | missing |  |
| `setThumbarButtons(buttons)` | `SetThumbarButtons` | done | Windows only, as in Electron: ids `40001 + slot`, icon/tooltip sent only when set, callbacks replaced before a bad flag fails the call, re-added on `TaskbarCreated`. |
| `setThumbnailClip(region)` | `SetThumbnailClip` | done | Windows only, as in Electron. |
| `setThumbnailToolTip(toolTip)` | `SetThumbnailToolTip` | done | Windows only, as in Electron. |
| `setAppDetails(options)` | `SetAppDetails` | done | Windows only, as in Electron. |
| `setAccentColor(accentColor)` | `SetAccentColor` | done | Windows only, as in Electron; null follows the system again. |
| `getAccentColor()` | `GetAccentColor` | done | Returns `AccentColor`: `#RRGGBB`, or `AccentColor.None` for Electron's `false` (W39). |
| `showDefinitionForSelection()` | — | missing |  |
| `setIcon(icon)` | `SetIcon` | done | Path or `NativeImage`; Windows and Linux only, as in Electron. |
| `setWindowButtonVisibility(visible)` | — | missing |  |
| `setAutoHideMenuBar(hide)` | `AutoHideMenuBar (set)` | done |  |
| `isMenuBarAutoHide()` | `AutoHideMenuBar (get)` | done |  |
| `setMenuBarVisibility(visible)` | `SetMenuBarVisibility` | done |  |
| `isMenuBarVisible()` | `IsMenuBarVisible` | done |  |
| `isSnapped()` | — | missing |  |
| `setVisibleOnAllWorkspaces(visible, options)` | — | missing |  |
| `isVisibleOnAllWorkspaces()` | — | missing |  |
| `setIgnoreMouseEvents(ignore, options)` | `SetIgnoreMouseEvents` | partial | Windows only, `forward` included (W30). |
| `setContentProtection(enable)` | `SetContentProtection` | partial | Windows only for now (W30). |
| `isContentProtected()` | `IsContentProtected` | partial | Windows only for now (W30). |
| `setFocusable(focusable)` | — | missing |  |
| `isFocusable()` | — | missing |  |
| `setParentWindow(parent)` | `SetParentWindow` | partial | Windows: as Electron (`GWLP_HWNDPARENT`, re-show, modal throws) with Oriel's parent/child bookkeeping; macOS and Linux throw (W8). |
| `getParentWindow()` | `GetParentWindow` | done |  |
| `getChildWindows()` | `GetChildWindows` | done |  |
| `setAutoHideCursor(autoHide)` | — | missing |  |
| `selectPreviousTab()` | — | missing |  |
| `selectNextTab()` | — | missing |  |
| `showAllTabs()` | — | missing |  |
| `mergeAllWindows()` | — | missing |  |
| `moveTabToNewWindow()` | — | missing |  |
| `toggleTabBar()` | — | missing |  |
| `addTabbedWindow(window)` | — | missing |  |
| `setVibrancy(type, options)` | — | missing |  |
| `setBackgroundMaterial(material)` | `SetBackgroundMaterial` | partial | Windows 11 22H2+ only; set but hidden (W24). |
| `setWindowButtonPosition(position)` | — | missing |  |
| `getWindowButtonPosition()` | — | missing |  |
| `setTouchBar(touchBar)` | — | missing |  |
| `setBrowserView(browserView)` | — | n/a | Deprecated. |
| `getBrowserView()` | — | n/a | Deprecated. |
| `addBrowserView(browserView)` | — | n/a | Deprecated. |
| `removeBrowserView(browserView)` | — | n/a | Deprecated. |
| `setTopBrowserView(browserView)` | — | n/a | Deprecated. |
| `getBrowserViews()` | — | n/a | Deprecated. |
| `setTitleBarOverlay(options)` | `SetTitleBarOverlay` | done | Throws `InvalidOperationException` without an overlay, as Electron throws; an unparsable colour throws `ArgumentException` with Electron's `TypeError` message. |

### Instance properties

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `webContents` | `WebContents` | done |  |
| `id` | `Id` | done |  |
| `contentView` | — | missing | No View module. |
| `tabbingIdentifier` | — | missing |  |
| `autoHideMenuBar` | `AutoHideMenuBar` | done |  |
| `simpleFullScreen` | `SetSimpleFullScreen / IsSimpleFullScreen` | done |  |
| `fullScreen` | `SetFullScreen / IsFullScreen` | done |  |
| `focusable` | — | missing |  |
| `visibleOnAllWorkspaces` | — | missing |  |
| `shadow` | `SetHasShadow / GetHasShadow` | partial | Stored only, as Electron on Windows (W31). |
| `menuBarVisible` | `SetMenuBarVisibility / IsMenuBarVisible` | done |  |
| `kiosk` | `SetKiosk / IsKiosk` | partial | Full screen; macOS presentation options not yet (W33). |
| `documentEdited` | — | missing |  |
| `representedFilename` | — | missing |  |
| `title` | `SetTitle / GetTitle` | done |  |
| `minimizable` | `SetMinimizable / IsMinimizable` | done |  |
| `maximizable` | `SetMaximizable / IsMaximizable` | done |  |
| `fullScreenable` | — | missing |  |
| `resizable` | `SetResizable / IsResizable` | done |  |
| `closable` | `SetClosable / IsClosable` | done |  |
| `movable` | — | missing |  |
| `excludedFromShownWindowsMenu` | — | missing |  |
| `accessibleTitle` | — | missing |  |
| `snapped` | — | missing |  |

### Instance events

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `page-title-updated` | `PageTitleUpdated` | done |  |
| `close` | `Closing` | done | CancelEventArgs. |
| `closed` | `Closed` | done |  |
| `query-session-end` | — | missing |  |
| `session-end` | — | missing |  |
| `unresponsive` | — | missing |  |
| `responsive` | — | missing |  |
| `blur` | `Blurred` | done |  |
| `focus` | `Focused` | done |  |
| `show` | `Shown` | partial | Fires only from Show(), not when the OS shows the window (W1). |
| `hide` | `Hidden` | partial | Fires only from Hide(), not when the OS hides the window (W1). |
| `ready-to-show` | `ReadyToShow` | done |  |
| `maximize` | `Maximized` | done |  |
| `unmaximize` | `Unmaximized` | done |  |
| `minimize` | `Minimized` | partial | Polled every 250 ms (W2). |
| `restore` | `Restored` | partial | Polled every 250 ms (W2). |
| `will-resize` | `WillResize` | partial | Windows only for now (W30). |
| `resize` | `Resized` | done |  |
| `resized` | — | missing |  |
| `will-move` | `WillMove` | partial | Windows only for now (W30). |
| `move` | `Moved` | done |  |
| `moved` | — | missing |  |
| `enter-full-screen` | `EnteredFullScreen` | done |  |
| `leave-full-screen` | `LeftFullScreen` | done |  |
| `enter-html-full-screen` | — | missing |  |
| `leave-html-full-screen` | — | missing |  |
| `always-on-top-changed` | — | missing |  |
| `app-command` | — | missing |  |
| `swipe` | — | missing |  |
| `rotate-gesture` | — | missing |  |
| `sheet-begin` | — | missing |  |
| `sheet-end` | — | missing |  |
| `new-window-for-tab` | — | missing |  |
| `system-context-menu` | — | missing |  |
| `persisted-state-restored` | — | missing |  |

### Static methods

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `getAllWindows()` | `GetAllWindows` | done |  |
| `getFocusedWindow()` | `GetFocusedWindow` | done |  |
| `fromId(id)` | `FromId` | done |  |
| `fromWebContents(webContents)` | `FromWebContents` | done |  |
| `fromBrowserView(browserView)` | — | n/a | Deprecated. |
| `clearPersistedState(name)` | — | missing |  |
