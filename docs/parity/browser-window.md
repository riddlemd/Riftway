# BrowserWindow / BaseWindow parity

Against Electron **v44.5.1** (`docs/api/browser-window.md`, `base-window.md`, `structures/*-window-options.md`,
`structures/web-preferences.md`). Audited 2026-10-03; update this file as items land.

Status: **done**, **partial**, **missing**, or **n/a** (Node/`<webview>` features with no meaning in Riftway).
Route: `W` = `cef_window_t`, `V` = `cef_view_t`, `D` = `cef_window_delegate_t`, `H` = `cef_browser_host_t`,
**OS** = needs native code (Objective-C runtime, Win32, X11/Wayland), **Riftway** = bookkeeping in C#.

Event naming: Electron `'x'` → past-tense .NET event (`focus`→`Focused`, `close`→`Closing` with
`CancelEventArgs`, `closed`→`Closed`, `enter-full-screen`→`EnteredFullScreen`, ...).

## Summary

Counted from the Checklist at the end: `BaseWindow` and `BrowserWindow` are one union list (a `BrowserWindow` item is also a
`BaseWindow` one). Percentages are over the non-n/a items; the second counts a partial item as half.

| Area | Items | Done | Partial | Missing | n/a | % done | % done + partial/2 |
|---|---|---|---|---|---|---|---|
| BaseWindow + BrowserWindow options | 54 | 34 | 20 | 0 | 0 | 63.0% | 81.5% |
| webPreferences | 46 | 12 | 8 | 19 | 7 | 30.8% | 41.0% |
| Instance methods | 143 | 107 | 30 | 0 | 6 | 78.1% | 89.1% |
| Instance properties | 24 | 17 | 7 | 0 | 0 | 70.8% | 85.4% |
| Events | 35 | 20 | 15 | 0 | 0 | 57.1% | 78.6% |
| Static methods | 7 | 6 | 0 | 0 | 1 | 100.0% | 100.0% |
| **Total** | 309 | 196 | 80 | 19 | 14 | 66.4% | 80.0% |

## Done since the audit

- Lookup: `Id`, `GetAllWindows`, `GetFocusedWindow`, `FromWebContents`, `FromId`.
- `BaseWindow`: public, a window with no page of its own; `SetContentView`; a `BrowserWindow`'s page lies below
  `ContentView`, which a new window has empty, as Electron's.
- Runtime setters/getters: resizable, minimizable, maximizable, closable (+ `Closable` option; code can still close),
  minimum/maximum size.
- Sizes: `GetSize`, `GetPosition`, `Get/SetContentBounds`, `Get/SetContentSize`, `UseContentSize`, `Center` options.
- `PageTitleUpdated` (window and webContents; window title follows the page unless cancelled), `ReadyToShow`.
- `BackgroundColor` option, `Set/GetBackgroundColor`; `Icon` (path) and `IconImage` options, `SetIcon` (path or
  `NativeImage`, every scale factor). The `Icon` option was ignored before `native-image`.
- `Destroy`.
- Taskbar: `SetProgressBar` (`ProgressBarOptions.Mode`), `FlashFrame`, `SetSkipTaskbar` and the `SkipTaskbar` option,
  `SetOverlayIcon` (`NativeImage`; the path overload is gone, so `null` is unambiguous), `SetThumbarButtons` (Windows: Windows `ITaskbarList3` and `FlashWindowEx` (`WindowsTaskbar`); macOS the Dock tile
  (`MacDock`: Electron's progress bar over the icon, `requestUserAttention:`); Linux X11 (`X11Window`: the urgency hint,
  `_NET_WM_STATE_SKIP_TASKBAR`) and the Unity launcher entry D-Bus signal for progress (`LinuxLauncherEntry`, DRIFT A20).
- Windows only, as in Electron: `SetAppDetails` (`AppDetailsOptions`: the window property store's
  `PKEY_AppUserModel_*`), `SetThumbnailClip` and `SetThumbnailToolTip` (`ITaskbarList3`; clips run on an STA thread
  because the shell's proxy rejects the NULL clip that clears them), `BackgroundMaterial` option and
  `SetBackgroundMaterial` (`DWMWA_SYSTEMBACKDROP_TYPE`, Windows 11 22H2+; a material clears the page background so
  it shows, `none` makes it white, as Electron), and `Transparent`: the page's background through DevTools'
  `Emulation.setDefaultBackgroundColorOverride` (CEF takes only opaque colours), the window's caption and sizing border
  stripped and kept off, no backdrop, not resizable or maximizable by the user, `Maximize` filling the work area as
  Electron emulates it. macOS and Linux transparency is missing (W24); `titleBarOverlay` is missing (W25).
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
  Electron: Chrome-style profiles save it across launches, so Riftway clears `partition.per_host_zoom_levels` at startup.
- webContents: `openDevTools` (own window only; no docked modes), `closeDevTools`, `isDevToolsOpened`, `toggleDevTools`,
  `setZoomFactor`/`getZoomFactor`, `setZoomLevel`/`getZoomLevel`, `executeJavaScript` (`ExecuteJavaScriptAsync`,
  DevTools `Runtime.evaluate` in the main world; rejects with `JavaScriptException`).
- `parent`, `modal`, `getParentWindow`, `getChildWindows`, `isModal` (`D.get_parent_window`, `D.is_window_modal_dialog`);
  children close with their parent. The constructor now runs from any thread. A modal window keeps focus from its
  parent on macOS; on Linux it does not, as in Electron: the modal hint reaches no window manager. Riftway now
  disables the parent's input itself there, as Electron does (`SetEnabled`, drift W5).
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
  goes through `performSelectorOnMainThread` in the tracking run-loop modes; without a window it closes the menu's
  popups in every window, as Electron's `ClosePopupAt(-1)`, and a close that overtakes its queued popup keeps it shut. Gaps: without x/y the popup opens
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
  `pasteAndMatchStyle`, `delete`, `selectAll`, `reloadIgnoringCache`. Gaps: menu item icons, sublabels and tooltips off macOS (M3; `registerAccelerator` is Electron's, and Electron shows `toolTip` on macOS only); most macOS-only roles (tabs, speech; `recentDocuments` through AppKit's private menu name, M4;
  `shareMenu` only in natively built macOS menus, M4); the click callback gets no `KeyboardEvent`; `Command`/`Super` accelerators run on the Windows key
  (matched in the keyboard handler's `on_key_event`, as CEF accelerators have no such modifier) but their items show no
  shortcut text (M5); Alt shows an auto-hidden bar until pressed again, rather than
  until the menu is used; on Windows the bar has Electron's look (background, pressed colour, Alt underlines; W16).
  `Menu.MenuWillShow`/`MenuWillClose` (Electron's `menu-will-show`/`menu-will-close`) come from CEF's
  `D.menu_will_show`/`D.menu_closed` per Views model on Windows and Linux (a popup or tray menu and each submenu on its own
  Menu; a bar dropdown on its submenu, never on the bar's Menu), from NSMenu delegates on the macOS application and Dock
  menus (every menu under them reports on the top Menu, and `menuNeedsUpdate:`/`menuDidClose:` re-apply item state as
  Electron's `refreshMenuTree`), from DBusMenu `AboutToShow` (submenus only, no close) and
  `MenuGtk`'s build on the Linux tray. Before each show `_menuWillShow` checks the first item of a radio group with none
  checked; radio groups follow Electron's `generateGroupId`. Gaps: DRIFT M8 (macOS popups report the top menu only), M9, M11.

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
- Windows window state, from Electron's `NativeWindowViews` (v44.5.1): `focusable` (option, `setFocusable`,
  `isFocusable`: `WS_EX_NOACTIVATE`, taskbar button, blur), `movable` (option, `setMovable`, `isMovable`: `WM_MOVING`),
  `type: 'toolbar'` (`WS_EX_TOOLWINDOW`), `setEnabled`/`isEnabled` (kept disabled under a shown modal), `moveTop`,
  `moveAbove` and `getMediaSourceId` (Chromium's `DesktopMediaID` strings), `isTabletMode` (`UIViewSettings`),
  `isSnapped`/`snapped` (`IsWindowArranged`), `setShape` (window region), `setAspectRatio`
  (Chromium's `SizeRectToAspectRatio` on `WM_SIZING`), and the events `moved`/`resized` (`WM_EXITSIZEMOVE`),
  `query-session-end`/`session-end`, `system-context-menu` (also from a frameless window's drag region), `app-command` and
  `always-on-top-changed`. `setAlwaysOnTop` levels below `pop-up-menu` keep the window under the taskbar, as Electron.
  Routes: a subclass of the HWND of its own (`WindowsWindowMessages`), so other subclasses keep working. Gaps: macOS and
  Linux (W43), no aura id in media source ids (W45). A non-focusable window shows without activating, through `ShowInactive`'s route.

## Next, by importance

1. Remaining webPreferences:
   - Need other modules: `spellcheck` (no per-page switch, E22), `offscreen` (offscreen
     rendering). `session`/`partition` landed with the `Session` module (DRIFT E1-E11).
   - Process-wide in CEF (Chromium switches, not per window): `webSecurity`, `allowRunningInsecureContent`,
     `experimentalFeatures`, `enableBlinkFeatures`/`disableBlinkFeatures`, `autoplayPolicy`, `sandbox`. Decide between
     an app-level option and a per-window gap.
   - Profile-wide in Chrome style, so they throw `NotSupportedException`: `minimumFontSize`, `defaultEncoding`.
   - Not yet investigated: `backgroundThrottling`, `navigateOnDragDrop`, `safeDialogs`/`safeDialogsMessage`,
     `accessibleTitle`, `scrollBounce`, `imageAnimationPolicy`, `zoomMode`, `focusOnNavigation`,
     `additionalArguments`.
   - `contextIsolation` is always on; `false` is not offered.
2. (none: `setParentWindow` is done on every OS; W8 tracks a macOS check.)
3. The Window Controls Overlay page API, and the macOS overlay rectangle beside the traffic lights (W34, W37) — OS, Patch.
4. `showInactive`, `opacity`, `setIgnoreMouseEvents`, content protection, `will-resize`/`will-move` on macOS and Linux (W30) — OS.
5. `hasShadow` on macOS and Linux (W31), kiosk presentation options on macOS (W33), the Windows window state on macOS and Linux (W43) — OS.

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
- **Minimize/restore/maximize/unmaximize events**: on Windows they come from `WM_SIZE` through a window subclass,
  in Electron's order and conditions (`HandleSizeEvent`: a minimized window coming back maximized raises `restore`,
  then `maximize`; entering or leaving full screen while maximized raises `maximize`). On macOS and Linux they are
  polled (250 ms) on top of bounds callbacks: CEF reports neither, and on macOS minimizing changes no bounds.

## Not applicable

`nodeIntegration*`, `webviewTag`, `enableWebSQL`, `v8CacheOptions` and other Node/`<webview>` preferences;
deprecated `BrowserView` methods and `enableDeprecatedPaste`.

## Checklist

One row per Electron v44.5.1 item. Status: done, partial (exists with a documented gap; DRIFT.md id in the note), missing, n/a.
Parameters in brackets are optional in Electron. `webContents` members are in [web-contents.md](web-contents.md).

### Constructor options (`BaseWindowConstructorOptions` + `BrowserWindowConstructorOptions`)

| Electron | Riftway | Status | Note |
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
| `movable` | `Movable` | partial | Windows (`WM_MOVING`) and macOS (`setMovable:`), as Electron; Linux has none, as Electron (W43). |
| `minimizable` | `Minimizable` | done |  |
| `maximizable` | `Maximizable` | done |  |
| `closable` | `Closable` | done | Honoured on Linux too (W10, superset). |
| `focusable` | `Focusable` | partial | Windows `WS_EX_NOACTIVATE`, shown without activation; macOS `canBecomeKeyWindow`/`canBecomeMainWindow` on Riftway's window subclass, as ElectronNSWindow's `disableKeyOrMainWindow` (Chromium's window has no such flag) _(branch `mac-integration`)_; stored only on Linux (W43). |
| `alwaysOnTop` | `AlwaysOnTop` | done |  |
| `fullscreen` | `FullScreen` | done |  |
| `fullscreenable` | `FullScreenable` | partial | Refuses `setFullScreen`, the initial and restored full screen; macOS through the collection behaviour and the zoom button (W53); refusing `setFullScreen` and `setFullScreenable(true)` pass on screen (macOS 26), the initial and restored full screen are unchecked. |
| `simpleFullscreen` | `SimpleFullScreen` | done | macOS only, as in Electron. |
| `skipTaskbar` | `SkipTaskbar` | done |  |
| `hiddenInMissionControl` | `HiddenInMissionControl` | done | macOS. |
| `kiosk` | `Kiosk` | partial | Full screen; on macOS also Electron's presentation options, passes on screen (macOS 26, arm64 and x64) (W33). |
| `name` | `Name` | done | Unique among open windows; reuse throws `ArgumentException` with Electron's message, free again once destroyed. |
| `windowStatePersistence` | `WindowStatePersistence` | done | `true` or `{ Bounds, DisplayMode }`; Electron's `windowStates` entry in `Local State` (sessionData), same keys, restore and clamp rules (W51). |
| `title` | `Title` | done |  |
| `icon` | `Icon`, `IconImage` | done | Two options, `IconImage` (`NativeImage`) wins (W26). |
| `show` | `Show` | done |  |
| `frame` | `Frame` | done |  |
| `parent` | `Parent` | done |  |
| `modal` | `Modal` | partial | Linux: Riftway disables the parent itself, as Electron; the pointer crossing into or out of it still reaches its page (W5). |
| `acceptFirstMouse` | `AcceptFirstMouse` | partial | macOS, CEF's `accepts_first_mouse` (enabled or disabled, never CEF's `--accept-first-mouse` default) _(branch `mac-integration`)_; not yet run on screen. |
| `disableAutoHideCursor` | `DisableAutoHideCursor` | done | Accepted with no effect, as Electron v44: its window stores the flag and neither Electron nor Chromium reads it _(branch `mac-integration`)_. |
| `autoHideMenuBar` | `AutoHideMenuBar` | done |  |
| `enableLargerThanScreen` | `EnableLargerThanScreen` | done | macOS `constrainFrameRect:toScreen:` on Riftway's window subclass, as `ElectronNSWindow` _(branch `mac-integration`)_; passes on screen (macOS 26, arm64 and x64). |
| `backgroundColor` | `BackgroundColor` | done |  |
| `hasShadow` | `HasShadow` | partial | macOS `NSWindow.hasShadow`; stored only on Windows, as Electron; Linux not yet (W31). |
| `opacity` | `Opacity` | partial | Windows and macOS (`alphaValue`); Linux not yet (W30). |
| `darkTheme` | `DarkTheme` | done | Linux: `_GTK_THEME_VARIANT=dark` at creation, as Electron; Chromium 154 already writes dark when its GTK UI is loaded. |
| `transparent` | `Transparent` | partial | Windows and macOS; Linux only with Riftway's CEF patch, not yet built (W24). |
| `type` | `Type` | partial | Windows `toolbar` (`WS_EX_TOOLWINDOW`); macOS `desktop`, `panel` (floating level; not an `NSPanel`) and `textured`, not yet run on screen; Linux types not yet (W43). |
| `visualEffectState` | `VisualEffectState` | partial | macOS _(branch `mac-integration`)_ (W54). |
| `titleBarStyle` | `TitleBarStyle` | partial | Anything but `Default` is frameless, as Electron. macOS: a transparent title bar with the traffic lights moved by Electron's `WindowButtonsProxy` (`hiddenInset` 12 × 11 in, `customButtonsOnHover` shown under the pointer, the overlay height as the container's), redrawn on resize, focus, minimise and full screen; not yet run on screen (W37). |
| `titleBarOverlay` | `TitleBarOverlay` | partial | Windows: Views buttons docked top right, CSS colours as Chromium parses them; no page API (W34); snap layouts through `HTMAXBUTTON`, buttons follow the window's flags. |
| `accentColor` | `AccentColor` | done | Windows 11, as in Electron: `bool` or CSS string (`AccentColor`); system changes followed through the `AccentColorObserver`'s watch on the DWM key (W39). |
| `trafficLightPosition` | `TrafficLightPosition` | partial | macOS, the proxy's margin; passes on screen (macOS 26, arm64 and x64) (W37). |
| `roundedCorners` | `RoundedCorners` | partial | Windows 11 (`DWMWA_WINDOW_CORNER_PREFERENCE`, frameless, off without `thickFrame`, squared in full screen); macOS a frameless window without them is borderless, titled again in full screen, as Electron _(branch `mac-integration`)_, not yet run on screen; Linux not done (W40). |
| `thickFrame` | `ThickFrame` | partial | Windows: Electron's frameless `GWL_STYLE`, framed loses `WS_THICKFRAME`, full screen by resizing to the display; resize insets follow CEF, not Electron (W40). |
| `vibrancy` | `Vibrancy` | partial | macOS: Electron's material table, the window translucent and the page transparent without a `backgroundColor` _(branch `mac-integration`)_; the effect view sits under Chromium's content view (W54), corners masked as Electron's; not yet run on screen. |
| `backgroundMaterial` | `BackgroundMaterial` | done | Windows 11 22H2+ only. |
| `zoomToPageWidth` | `ZoomToPageWidth` | partial | macOS: the window delegate's `windowWillUseStandardFrame:defaultFrame:` widens to the page's preferred width (0 without `EnablePreferredSizeMode`, as Electron's `GetPreferredSize`), never narrower, moved left only to stay on screen; Shift fills the screen _(branch `mac-integration`)_; not yet run on screen. |
| `tabbingIdentifier` | `TabbingIdentifier` | partial | macOS; without one, or transparent or frameless, the window never tabs (`NSWindowTabbingModeDisallowed`), as Electron _(branch `mac-integration`)_; a tabbed window and one without an identifier pass on screen (macOS 26, arm64 and x64), the transparent and frameless cases not yet run. |
| `webPreferences (BrowserWindow)` | `WebPreferences` | done | Per-option status in the next table. |
| `paintWhenInitiallyHidden (BrowserWindow)` | `PaintWhenInitiallyHidden` | partial | True only, Electron's default; false throws (W63). |

### webPreferences

| Electron | Riftway | Status | Note |
|---|---|---|---|
| `devTools` | `DevTools` | done |  |
| `nodeIntegration` | — | n/a | Node.js |
| `nodeIntegrationInWorker` | — | n/a | Node.js |
| `nodeIntegrationInSubFrames` | — | n/a | Node.js |
| `preload` | `Preload` | done |  |
| `sandbox` | `Sandbox` | partial | Per session, not per window: `false` needs a session made with `SessionOptions.Sandbox = false` (patch 0010), and a value contradicting the session throws (W13). |
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
| `plugins` | — | missing | Pages run with plugins enabled; CEF cannot turn them off per page (W65). |
| `experimentalFeatures` | — | missing | Process-wide in CEF (W13). |
| `scrollBounce` | `ScrollBounce` | partial | Per session (`SessionOptions.ScrollBounce`), with Riftway's patches 0010 and 0014, Electron's `scroll_bounce_flag.patch`; with stock CEF every page rubber-bands (W55). |
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
| `disableHtmlFullscreenWindowResize` | `DisableHtmlFullscreenWindowResize` | partial | False only, Electron's default; true throws (W64). |
| `accessibleTitle` | `BaseWindowOptions.AccessibleTitle` | partial | macOS: VoiceOver reads it; Windows/Linux stored only (W58). |
| `spellcheck` | — | missing | No per-page switch in CEF (E22); the session's `SetSpellCheckerEnabled` covers every page. |
| `enableWebSQL` | — | n/a | Not applicable (listed in "Not applicable"). |
| `v8CacheOptions` | — | n/a | Node/V8 option. |
| `enablePreferredSizeMode` | `EnablePreferredSizeMode` | partial | Turns on `PreferredSizeChanged`, measured in the page (C42). |
| `transparent` | `Transparent` | done | Only an explicit true acts, as Electron for a window's or view's page; the window's `backgroundColor` wins (V14). Screen-pixel checks on Windows. |
| `enableDeprecatedPaste` | — | n/a | Deprecated. |
| `focusOnNavigation` | — | missing |  |

### Instance methods (`BaseWindow` and `BrowserWindow`, union)

| Electron | Riftway | Status | Note |
|---|---|---|---|
| `setContentView(view)` | `SetContentView` | done | A `BrowserWindow`'s page stays below the new view; another window's content view moves, as Electron's |
| `getContentView()` | `ContentView` | done | A property, as Electron's `contentView` |
| `destroy()` | `Destroy` | done |  |
| `close()` | `Close` | done |  |
| `focus()` | `Focus` | done |  |
| `blur()` | `Blur` | done |  |
| `isFocused()` | `IsFocused` | done |  |
| `isDestroyed()` | `IsDestroyed` | done |  |
| `show()` | `Show` | done |  |
| `showInactive()` | `ShowInactive` | partial | Windows and macOS (not key, the app not activated; passes on screen (macOS 26, arm64 and x64)); Linux shows and focuses (W30). |
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
| `isFullScreen()` | `IsFullScreen` | done | False in simple full screen, as Electron |
| `setSimpleFullScreen(flag)` | `SetSimpleFullScreen` | done |  |
| `isSimpleFullScreen()` | `IsSimpleFullScreen` | done |  |
| `isNormal()` | `IsNormal` | done | Not minimised, maximised or full screen, nor in simple full screen on macOS, as Electron _(branch `mac-integration`)_. |
| `setAspectRatio(aspectRatio, extraSize)` | `SetAspectRatio` | partial | Windows, Chromium's sizing (whole window, `extraSize` ignored, as Electron there); 0 resets (W46); macOS as Electron's delegate (the content's ratio with `extraSize`, rounded, within the limits), not yet run on screen; Linux not yet (W43). |
| `setBackgroundColor(color)` | `SetBackgroundColor` | done |  |
| `previewFile(path, displayName)` | `PreviewFile` | partial | macOS QuickLook, the window the panel's controller and data source; passes on screen (macOS 26). |
| `closeFilePreview()` | `CloseFilePreview` | partial | macOS, only the panel this window opened, as Electron; on macOS 26 the panel stays on screen (W59). |
| `setBounds(bounds, animate)` | `SetBounds` | partial | macOS `animate` through `setFrame:display:animate:`, raised to the minimum size, lowered to the maximum and skipped in full screen, as Electron; the animation and the minimum size pass on screen (macOS 26, arm64 and x64), the maximum and full screen not yet run; ignored elsewhere, as Electron. |
| `getBounds()` | `GetBounds` | done |  |
| `getBackgroundColor()` | `GetBackgroundColor` | done |  |
| `setContentBounds(bounds, animate)` | `SetContentBounds` | partial | macOS `animate` through `setFrame:display:animate:`, raised to the minimum size, lowered to the maximum and skipped in full screen, as Electron; landing where asked passes on screen (macOS 26), the clamps and the full-screen skip unchecked; ignored elsewhere, as Electron. |
| `getContentBounds()` | `GetContentBounds` | done |  |
| `getNormalBounds()` | `GetNormalBounds` | done | Windows: `GetWindowPlacement`; elsewhere the last bounds seen while normal. |
| `setEnabled(enable)` | `SetEnabled` | partial | macOS a half-transparent sheet, as Electron; Windows `EnableWindow`, kept disabled under a shown modal; Linux an X11 input cover and dropped keys, the pointer crossing in or out still reaching the page (W5); stored only on macOS (W43). |
| `isEnabled()` | `IsEnabled` | partial | Windows `IsWindowEnabled`; Linux the state Electron keeps (disabled by `SetEnabled(false)` or a shown modal child); macOS no attached sheet, as Electron. |
| `setSize(width, height, animate)` | `SetSize` | partial | macOS `animate` through `setFrame:display:animate:`, raised to the minimum size, lowered to the maximum and skipped in full screen, as Electron; landing where asked passes on screen (macOS 26), the clamps and the full-screen skip unchecked; ignored elsewhere, as Electron. |
| `getSize()` | `GetSize` | done |  |
| `setContentSize(width, height, animate)` | `SetContentSize` | partial | macOS `animate` through `setFrame:display:animate:`, raised to the minimum size, lowered to the maximum and skipped in full screen, as Electron; landing where asked passes on screen (macOS 26), the clamps and the full-screen skip unchecked; ignored elsewhere, as Electron. |
| `getContentSize()` | `GetContentSize` | done |  |
| `setMinimumSize(width, height)` | `SetMinimumSize` | done |  |
| `getMinimumSize()` | `GetMinimumSize` | done |  |
| `setMaximumSize(width, height)` | `SetMaximumSize` | done |  |
| `getMaximumSize()` | `GetMaximumSize` | done |  |
| `setResizable(resizable)` | `SetResizable` | done |  |
| `isResizable()` | `IsResizable` | done |  |
| `setMovable(movable)` | `SetMovable` | partial | Windows and macOS (W43). |
| `isMovable()` | `IsMovable` | partial | Windows and macOS (the window's); true on Linux, as Electron (W43). |
| `setMinimizable(minimizable)` | `SetMinimizable` | done |  |
| `isMinimizable()` | `IsMinimizable` | done |  |
| `setMaximizable(maximizable)` | `SetMaximizable` | done |  |
| `isMaximizable()` | `IsMaximizable` | done |  |
| `setFullScreenable(fullscreenable)` | `SetFullScreenable` | done | Passes on screen (macOS 26). |
| `isFullScreenable()` | `IsFullScreenable` | done | macOS reads the collection behaviour, as Electron. |
| `setClosable(closable)` | `SetClosable` | done |  |
| `isClosable()` | `IsClosable` | done |  |
| `setHiddenInMissionControl(hidden)` | `SetHiddenInMissionControl` | done |  |
| `isHiddenInMissionControl()` | `IsHiddenInMissionControl` | done |  |
| `setAlwaysOnTop(flag, level, relativeLevel)` | `SetAlwaysOnTop` | done | macOS window levels; Windows: levels below `pop-up-menu` go under the taskbar (again on each activation); `level` `normal` with no `relativeLevel` leaves the window off top on macOS only, as Electron; an unknown level throws nowhere, as Electron: macOS takes it as `normal` (plus `relativeLevel`), Windows keeps the window on top and above the taskbar, and a null level is `floating`. |
| `isAlwaysOnTop()` | `IsAlwaysOnTop` | done |  |
| `moveAbove(mediaSourceId)` | `MoveAbove` | partial | Windows and macOS (`orderWindowByShuffling:`, the owner checked as WebRTC's `GetWindowOwnerPid`), Chromium's id parsing and Electron's error; Linux not yet (W43). |
| `moveTop()` | `MoveTop` | partial | Windows and macOS; Linux not yet (W43). |
| `center()` | `Center` | done |  |
| `setPosition(x, y, animate)` | `SetPosition` | partial | macOS `animate` through `setFrame:display:animate:`, raised to the minimum size, lowered to the maximum and skipped in full screen, as Electron; landing where asked passes on screen (macOS 26), the clamps and the full-screen skip unchecked; ignored elsewhere, as Electron. |
| `getPosition()` | `GetPosition` | done |  |
| `setTitle(title)` | `SetTitle` | done |  |
| `getTitle()` | `GetTitle` | done |  |
| `setSheetOffset(offsetY, offsetX)` | `SetSheetOffset` | done | macOS through the window delegate's `window:willPositionSheet:usingRect:`, which places every sheet as Electron's does _(branch `mac-integration`)_; passes on screen (macOS 26, arm64 and x64). |
| `flashFrame(flag)` | `FlashFrame` | done |  |
| `setSkipTaskbar(skip)` | `SetSkipTaskbar` | done |  |
| `setKiosk(flag)` | `SetKiosk` | partial | Full screen (W33). |
| `isKiosk()` | `IsKiosk` | done |  |
| `isTabletMode()` | `IsTabletMode` | done | Windows `UIViewSettings`; false elsewhere, as Electron. |
| `getMediaSourceId()` | `GetMediaSourceId` | partial | `window:<HWND, X11 window or macOS window number>:0`; no aura id (W45). |
| `getNativeWindowHandle()` | `GetNativeWindowHandle` | done | Returns `nint`, not a Buffer (S6). |
| `hookWindowMessage(message, callback)` | `HookWindowMessage` | done | Windows only, as in Electron; `Action<nint, nint>` with the raw values, not Buffers (W38). |
| `isWindowMessageHooked(message)` | `IsWindowMessageHooked` | done |  |
| `unhookWindowMessage(message)` | `UnhookWindowMessage` | done |  |
| `unhookAllWindowMessages()` | `UnhookAllWindowMessages` | done |  |
| `setRepresentedFilename(filename)` | `SetRepresentedFilename` | done | macOS; nothing elsewhere, as in Electron. |
| `getRepresentedFilename()` | `GetRepresentedFilename` | done |  |
| `setDocumentEdited(edited)` | `SetDocumentEdited` | done | macOS. |
| `isDocumentEdited()` | `IsDocumentEdited` | done |  |
| `focusOnWebView()` | `FocusOnWebView` | done | CEF's `set_focus(1)` on the page, the window left as it is _(branch `mac-integration`)_. |
| `blurWebView()` | `BlurWebView` | done | CEF's `set_focus(0)` does nothing for a Views page, so the focus moves to the window's contents panel, focusable only while it holds it; raises the page's `blur`. |
| `capturePage(rect, opts)` | `CapturePageAsync` | partial | `WebContents.CapturePageAsync`, as Electron; `stayHidden`/`stayAwake` have no effect (C22). |
| `loadURL(url, options)` | `LoadUrl` | partial | Returns Task; `baseURLForDataURL` throws, `UploadFile` offset/length throw, no client hints, reload drops options (C1, C2, C3, C5). |
| `loadFile(filePath, options)` | `LoadFile` | done | `query`, `search` (raw, replaces `query`), `hash` |
| `reload()` | `Reload` | done |  |
| `setMenu(menu)` | `SetMenu` | done | Drawn by CEF Views on Windows/Linux (W16). |
| `removeMenu()` | `RemoveMenu` | done |  |
| `setProgressBar(progress, options)` | `SetProgressBar` | partial | macOS Electron's own Dock bar (grey outline, blue fill, full above 1, the mode ignored) _(branch `mac-integration`)_, passes on screen (macOS 26, arm64 and x64); Linux sends Electron's launcher entry signal (A20). |
| `setOverlayIcon(overlay, description)` | `SetOverlayIcon` | done | Takes a `NativeImage`. |
| `invalidateShadow()` | `InvalidateShadow` | done | macOS; nothing elsewhere, as Electron. |
| `setHasShadow(hasShadow)` | `SetHasShadow` | partial | macOS; stored only on Windows, as Electron; Linux not yet (W31). |
| `hasShadow()` | `GetHasShadow` | done | macOS reads the window's; elsewhere the value set. |
| `setOpacity(opacity)` | `SetOpacity` | partial | Windows and macOS; Linux not yet (W30). |
| `getOpacity()` | `GetOpacity` | done |  |
| `setShape(rects)` | `SetShape` | partial | Windows window region; macOS and Linux not yet (W43). |
| `setThumbarButtons(buttons)` | `SetThumbarButtons` | done | Windows only, as in Electron: ids `40001 + slot`, icon/tooltip sent only when set, callbacks replaced before a bad flag fails the call, re-added on `TaskbarCreated`. |
| `setThumbnailClip(region)` | `SetThumbnailClip` | done | Windows only, as in Electron. |
| `setThumbnailToolTip(toolTip)` | `SetThumbnailToolTip` | done | Windows only, as in Electron. |
| `setAppDetails(options)` | `SetAppDetails` | done | Windows only, as in Electron. |
| `setAccentColor(accentColor)` | `SetAccentColor` | done | Windows only, as in Electron; null follows the system again. |
| `getAccentColor()` | `GetAccentColor` | done | Returns `AccentColor`: `#RRGGBB`, or `AccentColor.None` for Electron's `false` (W39). |
| `showDefinitionForSelection()` | `ShowDefinitionForSelection` | partial | The page's `ShowDefinitionForSelection` _(branch `mac-integration`)_; not yet run on screen. |
| `setIcon(icon)` | `SetIcon` | done | Path or `NativeImage`; Windows and Linux only, as in Electron. |
| `setWindowButtonVisibility(visible)` | `SetWindowButtonVisibility` | done | macOS, the proxy's container too; `customButtonsOnHover` leaves the buttons to the pointer, as Electron. `GetWindowButtonVisibility` is Electron's private `_getWindowButtonVisibility`. |
| `setAutoHideMenuBar(hide)` | `AutoHideMenuBar (set)` | done |  |
| `isMenuBarAutoHide()` | `AutoHideMenuBar (get)` | done |  |
| `setMenuBarVisibility(visible)` | `SetMenuBarVisibility` | done |  |
| `isMenuBarVisible()` | `IsMenuBarVisible` | done |  |
| `isSnapped()` | `IsSnapped` | done | Windows `IsWindowArranged`; false elsewhere, as Electron. |
| `setVisibleOnAllWorkspaces(visible, options)` | `SetVisibleOnAllWorkspaces` | done | macOS and Linux; Windows has no workspaces, as in Electron. |
| `isVisibleOnAllWorkspaces()` | `IsVisibleOnAllWorkspaces` | done |  |
| `setIgnoreMouseEvents(ignore, options)` | `SetIgnoreMouseEvents` | partial | Windows and macOS (`ignoresMouseEvents`, `acceptsMouseMovedEvents` for `forward`), `forward` included; Linux not yet (W30). |
| `setContentProtection(enable)` | `SetContentProtection` | partial | Windows and macOS (`sharingType`); Linux not yet (W30). |
| `isContentProtected()` | `IsContentProtected` | partial | Windows and macOS; false on Linux (W30). |
| `setFocusable(focusable)` | `SetFocusable` | partial | Windows: style, taskbar button, blur, as Electron; macOS `canBecomeKeyWindow`/`canBecomeMainWindow` on Riftway's window subclass, as ElectronNSWindow's `disableKeyOrMainWindow` (Chromium's window has no such flag) _(branch `mac-integration`)_; stored only on Linux (W43). |
| `isFocusable()` | `IsFocusable` | partial | Windows reads `WS_EX_NOACTIVATE` too; macOS reads the subclass's flag (W43). |
| `setParentWindow(parent)` | `SetParentWindow` | done | As Electron on each OS: Windows `GWLP_HWNDPARENT` with re-show, Linux `WM_TRANSIENT_FOR`, macOS `addChildWindow:`; modal throws. macOS checks pass (W8). |
| `getParentWindow()` | `GetParentWindow` | done |  |
| `getChildWindows()` | `GetChildWindows` | done |  |
| `setAutoHideCursor(autoHide)` | `SetAutoHideCursor` | done | As `disableAutoHideCursor` _(branch `mac-integration`)_. |
| `selectPreviousTab()` | `SelectPreviousTab` | done | macOS; passes on screen (macOS 26). |
| `selectNextTab()` | `SelectNextTab` | done | macOS; passes on screen (macOS 26). |
| `showAllTabs()` | `ShowAllTabs` | done | macOS `toggleTabOverview:`; passes on screen (macOS 26). |
| `mergeAllWindows()` | `MergeAllWindows` | done | macOS; passes on screen (macOS 26). |
| `moveTabToNewWindow()` | `MoveTabToNewWindow` | done | macOS _(branch `mac-integration`)_; passes on screen (macOS 26, arm64 and x64). |
| `toggleTabBar()` | `ToggleTabBar` | done | macOS; passes on screen (macOS 26). |
| `addTabbedWindow(window)` | `AddTabbedWindow` | done | macOS, `ordered:NSWindowAbove`; the window itself throws `ArgumentException` with Electron's message _(branch `mac-integration`)_; passes on screen (macOS 26, arm64 and x64). |
| `setVibrancy(type, options)` | `SetVibrancy` | partial | macOS, `animationDuration` fades through `NSAnimationContext` _(branch `mac-integration`)_ (W54); not yet run on screen. |
| `setBackgroundMaterial(material)` | `SetBackgroundMaterial` | done | Windows 11 22H2+ only. |
| `setWindowButtonPosition(position)` | `SetWindowButtonPosition` | done | macOS; null puts AppKit's margin back; passes on screen (macOS 26, arm64 and x64) (W37). |
| `getWindowButtonPosition()` | `GetWindowButtonPosition` | done | macOS; null elsewhere. |
| `setTouchBar(touchBar)` | `SetTouchBar` | done | macOS, as `ElectronNSWindow makeTouchBar` and `touch-bar.ts` (the TouchBar module row); null removes it. |
| `setBrowserView(browserView)` | — | n/a | Deprecated. |
| `getBrowserView()` | — | n/a | Deprecated. |
| `addBrowserView(browserView)` | — | n/a | Deprecated. |
| `removeBrowserView(browserView)` | — | n/a | Deprecated. |
| `setTopBrowserView(browserView)` | — | n/a | Deprecated. |
| `getBrowserViews()` | — | n/a | Deprecated. |
| `setTitleBarOverlay(options)` | `SetTitleBarOverlay` | done | Throws `InvalidOperationException` without an overlay, as Electron throws; an unparsable colour throws `ArgumentException` with Electron's `TypeError` message. |

### Instance properties

| Electron | Riftway | Status | Note |
|---|---|---|---|
| `webContents` | `WebContents` | done |  |
| `id` | `Id` | done |  |
| `contentView` | `ContentView` | done | Empty in a new window; a `BrowserWindow`'s page (Chrome style) lies below it, as Electron's; views added to it are Alloy style (DRIFT V2) |
| `tabbingIdentifier` | `GetTabbingIdentifier` | partial | macOS; null when the window never tabs _(branch `mac-integration`)_. |
| `autoHideMenuBar` | `AutoHideMenuBar` | done |  |
| `simpleFullScreen` | `SetSimpleFullScreen / IsSimpleFullScreen` | done |  |
| `fullScreen` | `SetFullScreen / IsFullScreen` | done |  |
| `focusable` | `SetFocusable / IsFocusable` | partial | Windows and macOS; stored only on Linux (W43). |
| `visibleOnAllWorkspaces` | `SetVisibleOnAllWorkspaces / IsVisibleOnAllWorkspaces` | done |  |
| `shadow` | `SetHasShadow / GetHasShadow` | partial | macOS; stored only on Windows, as Electron (W31). |
| `menuBarVisible` | `SetMenuBarVisibility / IsMenuBarVisible` | done |  |
| `kiosk` | `SetKiosk / IsKiosk` | partial | Full screen; on macOS also Electron's presentation options, passes on screen (macOS 26, arm64 and x64) (W33). |
| `documentEdited` | `SetDocumentEdited / IsDocumentEdited` | done | macOS. |
| `representedFilename` | `SetRepresentedFilename / GetRepresentedFilename` | done | macOS. |
| `title` | `SetTitle / GetTitle` | done |  |
| `minimizable` | `SetMinimizable / IsMinimizable` | done |  |
| `maximizable` | `SetMaximizable / IsMaximizable` | done |  |
| `fullScreenable` | `SetFullScreenable / IsFullScreenable` | partial | As the option (W53). |
| `resizable` | `SetResizable / IsResizable` | done |  |
| `closable` | `SetClosable / IsClosable` | done |  |
| `movable` | `SetMovable / IsMovable` | partial | Windows and macOS; always true on Linux (W43). |
| `excludedFromShownWindowsMenu` | `SetExcludedFromShownWindowsMenu / IsExcludedFromShownWindowsMenu` | done | macOS `excludedFromWindowsMenu` _(branch `mac-integration`)_; passes on screen (macOS 26, arm64 and x64). |
| `accessibleTitle` | `BaseWindow.AccessibleTitle` | partial | macOS: overrides the window's `accessibilityTitle`; Windows/Linux stored only (W58). |
| `snapped` | `IsSnapped` | done |  |

### Instance events

| Electron | Riftway | Status | Note |
|---|---|---|---|
| `page-title-updated` | `PageTitleUpdated` | done |  |
| `close` | `Closing` | done | CancelEventArgs. |
| `closed` | `Closed` | done |  |
| `query-session-end` | `QuerySessionEnd` | done | Windows only, as Electron; reasons and the refusal as Electron. |
| `session-end` | `SessionEnded` | done | Windows only, as Electron: every window, then the process ends unless quitting (that path unverified). |
| `unresponsive` | `Unresponsive` | partial | `browser-window.ts`'s timer: 50 ms after the page's `unresponsive`, 5 s after a close goes ahead, cancelled by `responsive` or the page's destruction, never while disabled; a real hang is unverified (C14). |
| `responsive` | `Responsive` | partial | The page's `responsive`; a real hang is unverified (C14). |
| `blur` | `Blurred` | done |  |
| `focus` | `Focused` | done |  |
| `show` | `Shown` | partial | Windows and Linux: every `Show`/`ShowInactive` and a hidden window's `Maximize`, as Electron's Views windows (Linux unchecked); macOS from occlusion changes (W1). |
| `hide` | `Hidden` | partial | Windows and Linux: every `Hide`, as Electron's Views windows (Linux unchecked); macOS from occlusion changes (W1). |
| `ready-to-show` | `ReadyToShow` | done |  |
| `maximize` | `Maximized` | done |  |
| `unmaximize` | `Unmaximized` | done |  |
| `minimize` | `Minimized` | partial | From `WM_SIZE` on Windows, `NSWindowDidMiniaturizeNotification` on macOS; polled every 250 ms on Linux (W2). |
| `restore` | `Restored` | partial | From `WM_SIZE` on Windows, `NSWindowDidDeminiaturizeNotification` on macOS; polled every 250 ms on Linux (W2). |
| `will-resize` | `WillResize` | partial | Windows; macOS from the window delegate's `windowWillResize:toSize:` (right or bottom edge by the drag's direction, as Electron), not yet run on screen; Linux not yet (W30). |
| `resize` | `Resized` | done |  |
| `resized` | `ResizeFinished` | partial | Windows (`WM_EXITSIZEMOVE`), macOS (`windowDidEndLiveResize`); not Linux, as Electron (W43). |
| `will-move` | `WillMove` | partial | Windows; macOS (`windowWillMove`, cannot be cancelled there, as Electron); Linux not yet (W30). |
| `move` | `Moved` | done |  |
| `moved` | `MoveFinished` | partial | Windows (`WM_EXITSIZEMOVE`), macOS (each `windowDidMove`, as Electron); not Linux (W43). |
| `enter-full-screen` | `EnteredFullScreen` | done |  |
| `leave-full-screen` | `LeftFullScreen` | done |  |
| `enter-html-full-screen` | `EnterHtmlFullScreen` | done |  |
| `leave-html-full-screen` | `LeaveHtmlFullScreen` | done |  |
| `always-on-top-changed` | `AlwaysOnTopChanged` | done | `AlwaysOnTopChangedEventArgs.IsAlwaysOnTop`. Raised by `SetAlwaysOnTop` only when the state changes, not for z-order changes made by other programs, as Electron. |
| `app-command` | `AppCommand` | partial | Windows `WM_APPCOMMAND` with Electron's names; Linux mouse buttons not yet (W43). |
| `swipe` | `Swiped` | partial | macOS: `swipeWithEvent:` on the frame view's class, as Electron swizzles it, other windows keeping AppKit's _(branch `mac-integration`)_; not yet run on screen. |
| `rotate-gesture` | `RotateGesture` | partial | macOS: `rotateWithEvent:` on Riftway's window subclass _(branch `mac-integration`)_; not yet run on screen. |
| `sheet-begin` | `SheetBegan` | done | macOS: `NSWindowWillBeginSheetNotification` _(branch `mac-integration`)_; passes on screen (macOS 26, arm64 and x64). |
| `sheet-end` | `SheetEnded` | done | macOS: `NSWindowDidEndSheetNotification` _(branch `mac-integration`)_; passes on screen (macOS 26, arm64 and x64). |
| `new-window-for-tab` | `NewWindowForTab` | partial | macOS, the tab bar's + button through the window delegate, then the app's event, as Electron; the window's event passes on screen (macOS 26), the app's after it unchecked. |
| `system-context-menu` | `SystemContextMenu` | partial | Windows: framed windows (`WM_CONTEXTMENU`) and a right click on a frameless window's drag region, as Electron. |
| `persisted-state-restored` | `PersistedStateRestored` | done | Posted to the UI thread, as Electron's `EmitEventSoon` (W51). |

### Static methods

| Electron | Riftway | Status | Note |
|---|---|---|---|
| `new BaseWindow([options])` | `new BaseWindow(BaseWindowOptions?)` | done | A top-level window with an empty `ContentView` and no page |
| `getAllWindows()` | `GetAllWindows` | done | `BaseWindow`'s lists every window; `BrowserWindow`'s only `BrowserWindow`s, as Electron's |
| `getFocusedWindow()` | `GetFocusedWindow` | done | `BaseWindow`'s finds any window; `BrowserWindow`'s only a `BrowserWindow` |
| `fromId(id)` | `FromId` | done | `BrowserWindow.FromId` of a `BaseWindow`'s id is null, as Electron's |
| `fromWebContents(webContents)` | `FromWebContents` | done |  |
| `fromBrowserView(browserView)` | — | n/a | Deprecated. |
| `clearPersistedState(name)` | `BrowserWindow.ClearPersistedState` | done |  |
