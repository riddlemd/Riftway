# Drift from Electron

Every place where Oriel's API or behaviour differs from Electron's (v44.5.1), with the reason, so that a final
pass can try to close each one. Modules not started yet are not drift; they are tracked in
[ELECTRON-PARITY.md](ELECTRON-PARITY.md). Gaps inside a module that exists are drift.

**Rule:** a change that introduces drift adds its entry here in the same commit. That includes gaps in a
partly done feature (missing options, parameters, roles). Entries are removed when the drift is resolved.

**Why** — what blocks matching Electron:
- **CEF**: CEF's API has no way to do it.
- **Patch**: needs a change to Chromium or CEF, carried in Oriel's CEF build (M7).
- **OS**: needs per-OS native code Oriel does not have yet.
- **Module**: waits on another Oriel module (`NativeImage`, `Session`, `WebFrameMain`, ...).
- **Todo**: doable today, not done yet.
- **Design**: deliberate; .NET shape or a choice recorded here. Revisit only if the reason no longer holds.
- **Unknown**: the reason was not recorded; investigate first.

Entries marked _(branch `x`)_ are on that branch and not yet in `main`.

## API shape (Design)

| ID | Electron | Oriel | Why |
|---|---|---|---|
| S1 | Lower-camel methods, `on('event')` | PascalCase methods; events are .NET events in past tense (`Focused`, `Closing` with `CancelEventArgs`, `EnteredFullScreen`) | Design: Electron's event names collide with its method names in C# (`focus()` / `'focus'`) |
| S2 | Plain option objects | Immutable records with `init` properties (`BrowserWindowOptions`, `WebPreferences`, `LoadUrlOptions`, `PopupOptions`) | Design |
| S3 | Promises | `Task`; `ExecuteJavaScriptAsync` has the `Async` suffix and returns `JsonNode?` | Design |
| S4 | `app` singleton, `app.whenReady()` | `OrielApp.Create()`, `Ready` event, `Run()` blocks the main thread and returns the exit code | Design |
| S5 | `Menu.buildFromTemplate(template)` | Collection initializers: `new Menu { new MenuItem { ... } }` | Design |
| S6 | `getNativeWindowHandle()` returns a `Buffer` | Returns `nint` | Design |
| S7 | Renderer API through `require('electron')` | `window.oriel.ipcRenderer` / `oriel.contextBridge`, frozen, in the preload's isolated world; no Node | Design: Chromium's child processes run native code only, no .NET or Node |

## app / OrielApp

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| A1 | `app.enableSandbox()` on every OS | Linux only; `Run()` throws `PlatformNotSupportedException` on macOS and Windows | OS/packaging: CEF forces `--no-sandbox` whenever `browser_subprocess_path` is set (Windows), and the macOS helper does not link the sandbox library | Windows: the `bootstrap.exe` layout (probe passed, see windows-todo.md). macOS: native helper + `cef_sandbox` |
| A2 | Several instances may run; `requestSingleInstanceLock()` opts in to one | Always single instance per data directory: a second launch exits and the first gets `SecondInstance` | CEF: Chrome-style profiles lock their data directory | Per-instance cache paths when the lock is not requested; `RequestSingleInstanceLock` |
| A14 | `app.getPath('assets')` | Missing; the other names follow Chromium's PathService (checked on macOS and Linux, Windows untested) | Todo: CEF's resources directory per OS | Todo |
| A15 | `app.getPreferredSystemLanguages()`, `getSystemLocale()`, `getLocaleCountryCode()` on Windows | .NET's `CultureInfo` and `RegionInfo` stand in for `GetUserPreferredUILanguages`, ICU's default locale and `GetLocaleInfoEx` | Todo: the Win32 calls are not ported yet | Port them (windows-laptop) |
| A16 | The Linux locale getters follow what the C library and GLib make of the environment | They parse `LANGUAGE`, `LC_*` and `LANG` directly: a locale that is not installed still counts (Electron's `setlocale` falls back to C, so `getLocaleCountryCode()` is empty), and GLib's `locale.alias` names are not expanded | Todo | Query `setlocale(LC_TIME, NULL)` in-process; resolve aliases as GLib does |
| A17 | `app.setBadgeCount()` on Linux (Unity launcher count) and Windows (taskbar overlay badge) | Returns false and shows nothing off macOS | Todo: Linux needs the D-Bus `com.canonical.Unity.LauncherEntry` signal; Windows needs a drawn overlay icon per window | Port both |

## BrowserWindow

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| W1 | macOS: `show`/`hide` also fire when the window is occluded or revealed (`windowDidChangeOcclusionState`); Windows and Linux fire them only from Oriel's own calls, as Oriel does everywhere | Fire only from `Show()`/`Hide()` | Todo: no occlusion observer on macOS | `NSWindowDidChangeOcclusionStateNotification` |
| W2 | `minimize`/`restore` events from the OS | Polled every 250 ms | CEF: reports neither; on macOS minimizing changes no bounds | OS notifications |
| W5 | Modal windows disable their parent's input on Linux | The parent still takes focus under window managers that ignore the modal hint (openbox) | CEF: no input filter to block the parent | OS: X11 input shape or grab; check GNOME/KDE |
| W7 | `openDevTools({ mode })` with docked modes | Separate window only | Unknown | CEF `show_dev_tools` with an inspector view, or `BrowserView` docking |
| W8 | `setParentWindow` | Missing | CEF: Views sets the parent only at creation | OS: `addChildWindow:`, owner `HWND`, `XSetTransientForHint` |
| W9 | `setFullScreen(false)` does nothing in simple full screen (its guard reads the full-screen style, which simple full screen does not set) | Leaves simple full screen | Design: Electron's guard looks like a bug; matching it would strand the window | None |
| W12 | `minimumFontSize`, `defaultEncoding` per window | Throw `NotSupportedException` | CEF: Chrome-style browsers take them from the profile, for every window | Set them per `Session` (profile preferences), for every window in it |
| W13 | `webSecurity`, `allowRunningInsecureContent`, `experimentalFeatures`, `enable/disableBlinkFeatures`, `autoplayPolicy`, `sandbox` per window | Missing | CEF: Chromium switches, process-wide | App-level options; per-window is a Patch |
| W14 | `contextIsolation: false` | Always on | Design: the preload runs in an isolated world by construction | None planned |
| W16 | `isMenuBarVisible()` etc. on Windows/Linux; the bar is a Views `MenuBar` (as Oriel's), with its own colours, padding, hover and mnemonic underlines; Linux can also export a DBus global menu | Same API and a Views bar, but plain: system-theme colours, no mnemonic underlines, different padding and hover; no global menu | CEF: label buttons cannot draw underlines; the rest is Todo | Match Electron's colours and padding; DBus menu on Linux |
| W20 | `setProgressBar` on Linux (Unity launcher progress); error and paused colours on macOS | Linux shows no progress; macOS shows error and paused as the plain Dock bar | OS: Linux progress is desktop-specific (D-Bus `com.canonical.Unity.LauncherEntry`); `NSProgressIndicator` has no colours | D-Bus launcher entry on Linux; a drawn bar on macOS |
| W24 | `backgroundMaterial` / `setBackgroundMaterial` show Mica, Acrylic or Mica Alt behind a transparent window | DWM takes and reports the backdrop (`DWMWA_SYSTEMBACKDROP_TYPE`), but nothing shows: CEF paints the client area and its own title bar opaque, even with a transparent page and `BackgroundColor = "#00000000"`. Without the option DWM already reports Mica (`DWMSBT_MAINWINDOW`), set by Chromium or CEF, not Oriel | CEF: Views windows on Windows draw their own frame and an opaque background | `DwmExtendFrameIntoClientArea` and a transparent Views window; check whether CEF's frameless mode composites alpha |
| W25 | `titleBarStyle: 'hidden'` with `titleBarOverlay` (Windows caption buttons over the page, `getTitleBarOverlay` geometry) | Missing | OS: needs a frameless window, caption buttons drawn over the page and `WM_NCHITTEST` for the drag area | Frameless window plus an overlay view (`W.add_overlay_view`) with min/max/close buttons, after `titleBarStyle` |
| W26 | `icon` option takes a `NativeImage` or a path | Two options: `Icon` (path) and `IconImage` (`NativeImage`, wins over `Icon`) | Design: a C# property has one type | None |
| W29 | Windows: `setAlwaysOnTop` levels from `pop-up-menu` up put the window above the taskbar, the lower ones behind it | Always on top at one level; `level` and `relativeLevel` work on macOS only | CEF: `set_always_on_top` has no level; Todo: the taskbar ordering on Windows | Win32 z-order against the taskbar |

## nativeImage

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| B1 | `createThumbnailFromPath` (macOS, Windows), `createFromNamedImage` and `getNativeHandle` (macOS) | Missing | OS: Quick Look, `IShellItemImageFactory`, `NSImage` | P/Invoke per OS |
| B2 | `resize` `quality` `good`/`better`/`best` pick Skia filters | One filter for all three: box averaging when shrinking, bilinear when enlarging | Todo | Lanczos for `best` |
| B3 | `setTemplateImage` makes macOS tint the image in menus and the tray | Stored only; nothing in Oriel draws template images yet | Module: `Tray`, menu item icons | Apply in `Tray` and `NSMenu` items |
| B4 | `toPNG`, `toBitmap`, `toDataURL`, `getSize`, `getAspectRatio` take `{ scaleFactor }`; `createFromPath` also reads ICO on Windows | A `double scaleFactor` parameter; PNG and JPEG only (CEF's codecs) | Design for the parameter; CEF for the formats | ICO through WIC on Windows |
| B5 | `nativeImage` decodes with Chromium's codecs in the main process | Decoding and encoding PNG/JPEG need CEF, so after `OrielApp.Create`; raw bitmaps, crop and resize work any time | CEF: the codecs live in libcef | Managed PNG codec |

## window.open

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| O1 | `details.features` is the page's raw string, and features such as `frame=no` or `show=no` apply to the window | Rebuilt from what CEF parsed: `left`, `top`, `width`, `height` only; other features are lost | CEF: `on_before_popup` gives parsed position and size only | Patch |
| O2 | `details.referrer`, `details.postBody` | Missing | CEF: `on_before_popup` has neither | Patch |
| O3 | Handler can `createWindow` with its own `webContents` | Not offered | Todo | Adopt a given window's view |
| O4 | A popup's first page loads once, with the preload | When it commits before Oriel's runtime and preload are registered over DevTools, it is reloaded once so they run: the page loads twice, and a POST target is untested | CEF: the popup's browser exists only as it starts loading; there is no about:blank start as for other windows | Patch, or register before the first commit (DevTools auto-attach) |

## webFrameMain

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| F1 | Every frame runs script and receives messages, cross-site iframes included | A frame in another process (a cross-site iframe, with site isolation) is listed but cannot run script, `send` or `reload` | CEF/DevTools: out-of-process frames are separate DevTools targets, which Oriel does not attach to | DevTools `Target.setAutoAttach` with flattened sessions |
| F2 | `frameTreeNodeId`, `processId`, `routingId`, `osProcessId`, `visibilityState`, `collectJavaScriptCallStack`; `webFrameMain.fromId` / `fromFrameToken` | Missing; `frameToken` is Chromium's DevTools frame token | Todo, or CEF: the numeric ids are not exposed | Add what DevTools gives (frame lookup by token) |
| F3 | `webContents.focusedFrame` includes a cross-site iframe in another process | Such a frame taking focus is not seen; same-process frames are | Follows F1: Oriel's runtime does not run in out-of-process frames | With F1 |
| F4 | `webContents.mainFrame` always exists | Null until the page's frame tree has been read, just after the window is created | Design: frames come from DevTools | None |

## navigationHistory

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| N1 | `removeEntryAtIndex`, `restore` (entries with `pageState`) | Missing; entries have only `url` and `title` | CEF: no way to remove or restore entries, and no page state | Patch |
| N3 | A window's history starts with its first page | Same, by resetting the history after the first page loads, which drops the about:blank Oriel starts on; until a first page loads successfully, that entry is there | Design: the preload is registered over DevTools on about:blank before the real page | Register before the first navigation without a document |

## webContents

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| C1 | `loadURL(url, { baseURLForDataURL })` | Throws `NotSupportedException` | CEF cannot commit a `data:` URL under another base | Patch |
| C3 | A user agent override keeps the default client hints (`navigator.userAgentData`, `Sec-CH-UA*`) | Empty brands once a page or session overrides it (checked) | Todo: DevTools needs the metadata passed in, and Chromium generates its brand list internally (GREASE) | Read the metadata from a page before overriding, or reproduce Chromium's brand list |
| C5 | A reload repeats the `loadURL` options | Likely a plain request (untested) | Todo | Keep the options with the navigation entry |
| C6 | `executeJavaScript` resolves a structured clone | JSON only (`JsonNode?`); undefined is null | Design: DevTools `Runtime.evaluate` returns JSON | Typed overloads |
| C8 | `context-menu` params `frame`, `linkText`, `altText`, `selectionRect`, `menuSourceType`, `formControlType`, `referrerPolicy`, `suggestedFilename`, `selectionStartOffset` | Missing | CEF: `cef_context_menu_params_t` lacks them; `frame`: CEF names the frame by its own identifier, which no DevTools frame id matches (WebFrameMain is built on DevTools) | Patch; or read the element under the point with JS for text/alt; Module for `frame` |
| C12 | Navigation event `details.initiator` names the frame that started it; `frame` is always exact; `did-frame-navigate`/`did-navigate-in-page` carry `frameProcessId`/`frameRoutingId` | `Initiator` is always null; a subframe's `Frame` is found by name and URL, so two iframes with the same name and URL under one parent can be confused; `Frame` is given instead of the ids (F2) | CEF: `on_before_browse` gives no initiator, and its frame ids are not DevTools'; the ids follow F2 | Patch for the initiator |
| C13 | `render-process-gone` `reason` `clean-exit` | Never raised; a status CEF adds later maps to `abnormal-exit` | CEF: `cef_termination_status_t` has no normal-exit value | Patch |
| C14 | `unresponsive` / `responsive` follow Chromium's hang monitor | Wired to `on_render_process_unresponsive` / `_responsive`: one `Unresponsive` per hang, Chrome's hung-page dialog suppressed, CEF told to keep waiting. A real hang was not reproduced under Linux/Xvfb (a 50 s busy loop plus DevTools-injected input never raised it), so the check calls the CEF callbacks by hand | Todo: every page has a DevTools session, which probably suspends the monitor, and DevTools input may not arm it | Hang with real OS input and see if a DevTools-free page reports |
| C15 | `before-input-event` `input.modifiers` is a string array (`shift`, `capsLock`, `numLock`, `left`, `right`, `leftButtonDown`, ...) | `KeyInput.Modifiers` is `InputModifiers` flags: Shift, Control, Alt, Meta, IsKeypad, IsAutoRepeat; `Shift`/`Control`/`Alt`/`Meta` booleans as Electron | Design: .NET flags; lock and button states are not in `InputModifiers` | Add the flags |
| C16 | `input.key`, `code`, `isComposing`, `location` are the DOM values | Derived from CEF's Windows virtual key code, typed character and left/right flags: `code` assumes a US layout and is empty for unmapped keys, an unnamed key with no character has `key` `Unidentified`, `isComposing` is always false. Checked on Linux only; macOS may deliver menu key equivalents before the hook | CEF: `cef_key_event_t` has no DOM key, code or composition state | Patch |
| C17 | `getAllWebContents()` lists every WebContents (DevTools, ...); `getFocusedWebContents()` can be a DevTools page | Pages of `BrowserWindow`s only | Module: DevTools' own page is not a `WebContents` | Wrap DevTools pages |
| C18 | `findInPage` `findNext: true` begins a new session even for the same text; `stopFindInPage('activateSelection')` activates the match | A new session starts only when the text or case changes, else it moves on (what Electron does there is unverified); `ActivateSelection` keeps the selection | CEF: `find` has no new-session flag (its `findNext` decides whether a match is selected), and `stop_finding` only clears or keeps | Patch |
| C19 | `isLoadingMainFrame()` is Chromium's loading-UI state and `isWaitingForResponse()` its main-frame `NavigationRequest`, until ready-to-commit | Derived from CEF's `is_loading` and DevTools frame events: a cross-document `Page.frameStartedNavigating` starts both, `Page.frameNavigated` (commit) ends waiting, `frameStartedLoading`/`frameStoppedLoading` bound the document's load; a navigation that fails before committing is cleared by its `did-fail-provisional-load`. Checked: response pending, page-initiated, partial body, iframe only, 204, stop, stop after a redirect | CEF: only `is_loading` is exposed | Patch: expose `ShouldShowLoadingUI` / `IsWaitingForResponse` |
| C20 | `replace`, `unselect`, `scrollToTop`, `scrollToBottom`, `centerSelection`, `adjustSelection` run Blink commands on the focused frame's widget | Script in Oriel's preload world of the focused frame as Oriel tracks it (else the main frame; not a cross-site iframe, F1). `replace` finds the word with `Intl.Segmenter` and inserts with `execCommand('insertText')` (`inputType` `insertText`, not `insertReplacementText`; no smart replace); `adjustSelection` outside a text field moves by `Selection.modify` characters, not Blink's plain-text offsets; scrolling starts at the focused element (not the last-pressed node), is instant, vertical only, and does not bubble into the parent frame; macOS `centerSelection` scrolls the window only | CEF: `cef_frame_t` has only undo, redo, cut, copy, paste, delete and select-all | Patch: expose the other `WebContents` edit commands |
| C21 | `adjustSelection({ end })`: v44 reads the end offset from the `matchCase` key, so `end` is ignored | `End` is honoured, as documented | Design: Electron's code looks like a bug; matching it would make `End` dead | None |
| C22 | `capturePage` copies the view's surface; without `stayHidden` a hidden page is made visible while captured, `stayAwake` holds a wake lock | DevTools `Page.captureScreenshot` of the whole view, cropped by Oriel; DevTools always keeps a hidden page hidden and holds the wake lock, so `StayHidden` and `StayAwake` have no effect | CEF: no `CopyFromSurface` or capturer count | Patch |
| C23 | `isCurrentlyAudible()`, `audio-state-changed` | Missing | CEF: `cef_audio_handler_t` is called only for Alloy-style browsers (`AlloyBrowserHostImpl::OnAudioStateChanged`); Oriel's are Chrome style, and a handler wired up was never called with an oscillator playing | Patch: forward `WebContentsObserver::OnAudioStateChanged` |
| C24 | `enableDeviceEmulation` sets `blink::DeviceEmulationParams` on the main frame's widget | DevTools `Emulation.setDeviceMetricsOverride`, applied asynchronously: `mobile` there also forces viewport-meta, text-size-adjust and overlay scrollbars, which Electron's params leave off; a `viewPosition` off the emulated screen makes DevTools reject the whole call | CEF: no emulation API | Patch |
| C25 | `insertCSS` injects a sheet with Blink's `InsertStyleSheet`, in the `user` or `author` cascade origin | A DevTools inspector sheet (`CSS.createStyleSheet`): outside `document.styleSheets` and gone with the document, as Electron's, but always author-level, so `CssOrigin.User` behaves as `Author`; the key is DevTools' sheet id, and removal empties the sheet. DOM and CSS agents stay enabled after the first call | CEF: no style injection API | Patch |
| C26 | `executeJavaScriptInIsolatedWorld` runs in Blink world `worldId` (any int32; `setIsolatedWorldInfo` can configure it) and returns a structured clone | 0 is the main world, 999 the preload's (Electron's `contextIsolation` world); any other id is a DevTools-named world (`oriel-world-<id>`), one per frame document; results are JSON (C6); ids outside 0 ≤ id < 2^29 throw `ArgumentOutOfRangeException` | Design for ids and JSON; CEF: no Blink world ids | None |
| C27 | `forcefullyCrashRenderer` crashes the renderer on Linux (`crashed`) but kills it with `RESULT_CODE_HUNG` on macOS and Windows | DevTools `Page.crash` everywhere: the renderer crashes on its IO thread, Electron's Linux route | CEF: no process handle API | Kill the process by pid on macOS and Windows |
| C28 | `console-message` details carry the `frame` that logged | `Frame` is always null | CEF: `cef_display_handler_t::on_console_message` gets the browser only, though Chromium's `DidAddMessageToConsole` has the frame | Patch: pass the frame through |
| C29 | `frame-created` for every active frame Chromium creates: the main frame, iframes, and frames replacing them after a cross-process navigation | Raised for iframes only, from DevTools `Page.frameAttached` | CEF: no render-frame-created callback in the browser process; DevTools reports attachment of subframes, not the main frame's or a swapped frame's creation | Patch: forward `WebContentsObserver::RenderFrameCreated` |
| C30 | `blur` when the page's view loses focus, including to another view in the same window | Raised when the page's window loses focus | CEF: `cef_focus_handler_t` reports gaining focus only; the page's own blur events miss it when focus is inside an iframe | Patch: forward `OnWebContentsLostFocus`; or blur the other views when a `WebContentsView` takes focus |
| C31 | A `loadURL` kept from leaving by `beforeunload` raises `did-fail-load` (ERR_ABORTED) | Not raised | Todo: remember the URL a `LoadUrl` is waiting on, as Electron's `pending_unload_url_` does, and fail it when the unload is refused | Port |
| C32 | `webContents.close()` without `waitForBeforeUnload` closes without running the page's `beforeunload` | The page's `beforeunload` listeners run; their objection is answered "leave" at once, and `will-prevent-unload` is not raised | CEF: Chrome-style `close_browser(force_close=1)` still dispatches `beforeunload` to a page with user activation | Patch: close the `WebContents` without the unload handlers |
| C33 | `media-started-playing` and `media-paused` follow Chromium's media players: a stop is also reported when a playing player is destroyed with its document | Raised from each frame's `playing` and `pause` events; a document that unloads while its media plays reports no stop (unverified against Electron) | CEF: no media player observer callbacks | Patch: forward `MediaStartedPlaying` / `MediaStoppedPlaying` |
| C34 | `setIgnoreMenuShortcuts` on macOS keeps the app menu's key equivalents from the page's keys | No effect on macOS | Todo: on macOS the app menu's shortcuts are AppKit key equivalents on the NSMenu, which the flag does not gate; it gates only the Windows/Linux menu bar's handling | Gate the key equivalents for the focused page while ignoring |
| C35 | `zoom-changed` for a Ctrl+wheel the page leaves unhandled | Raised from the page's wheel events, after listeners below the window; a page's own window-level wheel listener that cancels the event later does not stop it | CEF: no `ContentsZoomChange` callback | Patch: forward `WebContentsDelegate::ContentsZoomChange` |
| C36 | `devtools-opened`, `devtools-closed`, `devtools-focused`, `isDevToolsFocused()` as DevTools open, close and take focus | Opened and closed are seen by asking CEF's `has_dev_tools` every 250 ms, so they arrive up to a quarter second late; focus is not reported | CEF: Chrome style opens DevTools in Chrome's own window, with no CEF callbacks and no handle to it | Patch: forward `DevToolsWindow` open, close and activation |

## Menu / MenuItem

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| M3 | Item `icon`, `sublabel`, `toolTip`, `registerAccelerator` | Missing | Todo (`icon` is Module: `NativeImage`) | `cef_menu_model` has no icon API either: Patch for Views menus |
| M4 | Roles `recentDocuments` and `shareMenu` build their submenus; `toggleSpellChecker` shows a check; `about` shows the about panel on Windows and Linux; `minimize`, `close` and `togglefullscreen` are enabled by the window's state | Those two submenus are empty; no check on `toggleSpellChecker` (it toggles); `about` does nothing off macOS; the three are always enabled. Every other role and label follows Electron v44's role table, macOS speech, substitution, tab and recent-document roles included | Todo: NSDocumentController's recent menu, NSSharingService items, an about panel, checked and enabled state computed when the menu opens | Todo |
| M5 | `Command`/`Super` accelerators on Windows and Linux | Ignored | CEF: Views accelerators have no such modifier | OS key hook, or Patch |
| M7 | `popup({ positioningItem })` (macOS) | Missing | CEF: `show_menu` takes a point only | Patch |

## screen

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| R1 | `display-added`, `display-removed`, `display-metrics-changed` from the OS | macOS matches (the notifications Chromium's `ScreenMac` observes). Windows, Linux: displays compared once a second while a handler is attached, so events come up to a second late | OS: Todo on Windows and Linux; CEF reports no display changes | `WM_DISPLAYCHANGE` / `WM_SETTINGCHANGE` (Windows), XRandR events (Linux), as `ScreenWin` and `X11ScreenOzone` |
| R2 | `Display.label`, `internal`, `colorSpace`, `colorDepth`, `depthPerComponent`, `displayFrequency`, `nativeOrigin`, `detected` from the OS on Windows and Linux; `colorSpace` from any ICC profile on macOS | Windows, Linux: Chromium's unset defaults (`""`, `false`, sRGB, 24, 8, 0, (0, 0), `true`). macOS: matches, except a profile with a `cicp` tag is read through its matrix and curves, a malformed `A2B`/`B2A` tag is not rejected, and `--force-color-profile` is ignored | OS: Todo per OS (Windows: `ScreenWin`'s `DisplayInfo`; Linux: XRandR output properties and EDID). CEF: `cef_display_t` has none of them | Port `ScreenWin::CreateDisplayFromDisplayInfo` and `x11_display_util.cc`'s `BuildDisplaysFromXRandRInfo`; `skia::CICPGetSkColorSpace` for `cicp` |
| R5 | `getCursorScreenPoint` under Wayland | X11 only (`XQueryPointer` on Oriel's own X connection) | OS; see P2 | Follow P2 |

## dialog

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| G1 | A dialog without a window stands alone (Linux: app-modal) | Linux and Windows (until the IFileDialog branch lands): a dialog needs an open window (the given, focused or any). macOS matches: NSOpenPanel/NSSavePanel as Electron's `file_dialog_mac.mm` | CEF: `run_file_dialog` belongs to a browser host | OS dialogs for the window-less case |
| G2 | `buttonLabel`, open `showHiddenFiles` and save `properties` honoured on Linux; the macOS-only and Windows-only options ignored there | Linux and Windows (until the IFileDialog branch lands): `buttonLabel`, `message`, `nameFieldLabel`, every open property beyond `openFile`/`openDirectory`/`multiSelections`, and any save property throw `NotSupportedException`; `showsTagField` and `securityScopedBookmarks` are ignored, as Electron. macOS matches | CEF: `run_file_dialog` takes a mode, title, path and filters only | OS dialogs, or Patch |
| G3 | Several directories | Linux and Windows (until the IFileDialog branch lands): throw (and `openFile` + `openDirectory` too, which Electron ignores there). macOS matches | CEF: no such modes | OS dialogs, or Patch |
| G4 | `defaultPath`'s directory is where the dialog opens | Linux and Windows (until the IFileDialog branch lands): same, but only by setting Chrome's `selectfile.last_directory` preference first: the page's own file choosers then start there too, and it persists in the profile. macOS matches, including Electron's split and its Downloads-else-home start | CEF: Chrome style runs the dialog as a page's file chooser, which keeps only the file name | Patch: pass the directory through |
| G5 | Folder dialogs accept with "Select" / "Open" | Linux (GTK): the accept button says "Upload" | CEF: Chromium's web folder-upload chooser | Patch, or an OS dialog on Linux |
| G6 | A `*` filter offers every file type | Linux and Windows (until the IFileDialog branch lands): the `*` filter is dropped; whether every type stays selectable depends on the platform dialog (unverified). macOS matches (no allowed types, and a Format popup entry that clears them) | CEF: filters are `Name|.ext` strings | Verify per OS |
| G7 | `showOpenDialogSync` blocks the main process | Linux and Windows (until the IFileDialog branch lands): the `Sync` variants throw on the UI thread, which must stay free to run the dialog. macOS matches: they run modally on the UI (main) thread, which runs no CEF tasks meanwhile, as Chromium in a native nested loop | CEF: the dialog answers on the UI thread | None; use the async form there |
| G8 | Return accepts a Linux save dialog | Return in the name field did not accept Chromium's GTK save dialog in testing (Xvfb, openbox); clicking Save did | Unknown: likely no default button set | Check on a desktop session; Patch if confirmed |
| G9 | `securityScopedBookmarks` returns bookmarks in Mac App Store builds only; there `bookmarks` is always present (`[]` when cancelled or not asked), and `bookmark` is `""` when cancelled and absent when not asked | macOS, any build: `Bookmarks` (`Bookmark`) only when asked (empty when cancelled), else null; a save path's file is created for its bookmark, as Electron's MAS build | Design: an Oriel app has no MAS/non-MAS build split; a sandboxed app needs them | — |

## session

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| E1 | `persist:` partitions live in `<userData>/Partitions/<name>` | `<data directory>/Partition <name>` | CEF: Chrome-style profiles must be direct children of the root cache path | Patch |
| E2 | `session.fromPath(path)` takes any absolute path | Only a directory directly inside the app's data directory; others throw `ArgumentException` | CEF: as E1 | Patch |
| E3 | `fromPartition(partition, { cache: false })` | No options | CEF: a request context has no switch to turn its HTTP cache off | Patch |
| E4 | `cookies.on('changed')` | Missing | CEF: no cookie-change observer | DevTools has none either; Patch |
| E5 | `cookies.set()` rejects with the reason (bad domain, ...) | `ArgumentException` without the reason | CEF: `set_cookie` reports success only | Patch |
| E6 | `ses.flushStorageData()` writes all DOM storage | `FlushStorageDataAsync` flushes cookies | CEF: only the cookie store has a flush | Patch |
| E7 | `overrideBrowserWindowOptions` may give a popup another session | Blocked, with a console message | CEF: a popup always gets its opener's request context | Patch |
| E8 | `clearStorageData`, `clearData`, `getCacheSize`, `clearHostResolverCache`, `resolveProxy` | Missing | CEF: no API on the request context | DevTools `Storage.clearDataForOrigin` through an open page for `clearStorageData` by origin; Patch for the rest |
| E10 | `setDevicePermissionHandler`, `preconnect`, extensions, the deprecated `setPreloads`/`getPreloads` | Missing | Todo: check what CEF offers for each | Later Session work (M6) |
| E12 | Permissions: no `setPermissionCheckHandler`; for non-media prompts `setPermissionRequestHandler` gets the requesting origin as `requestingUrl`, `isMainFrame` is always true, and there is no `requestingFrame` | Partial | Limit: CEF has no permission-check hook, and its permission prompt carries only the requesting origin (media requests carry the frame) | — |
| E13 | `DownloadItem` `hasUserGesture`, `getLastModifiedTime`, `getETag` | `false`, `""`, `""` | CEF: its download item has no gesture, Last-Modified or ETag | Patch |
| E14 | An interruption the download can resume from raises `updated` with `interrupted` and `canResume()` is true | Every interruption raises `done` with `interrupted`; `canResume()` is true only while paused | CEF: no resume mode for an interrupted item | Patch, or port Chromium's resume-mode table over the interrupt reason |
| E15 | `getURLChain` lists every redirect | The original and final URLs only | CEF: the item gives only those two | Patch |
| E16 | `session.downloadURL`, `ses.createInterruptedDownload`, `downloadURL` `headers` option | Missing; `WebContents.DownloadUrl(url)` only | CEF: `start_download` takes a URL and needs a browser | A hidden page for the session one; Patch for headers |
| E17 | `onHeadersReceived`'s answer can cancel the request or change `responseHeaders` and `statusLine` | `OnHeadersReceived` only watches: its listener is an `Action` | CEF: `on_resource_response` cannot change the response or wait for an answer | Patch, or a resource handler that makes the request itself |
| E18 | `webRequest` sees requests without a page (service workers, `net`) | Only requests a page makes | Todo: the request context handler's `get_resource_request_handler` | Todo |
| E19 | Listener details carry `fromCache`, `ip`, `uploadData`, `frame`, `webContentsId`; `statusLine` has the real protocol | `FromCache` is always false; no `ip`, `uploadData`, `frame` or `webContentsId` (`WebContents` is there); `StatusLine` always says `HTTP/1.1` | CEF: its request and response carry no cache, IP or protocol version; Todo for `uploadData` and `frame` | Todo for the two CEF has |
| E21 | `registerPreloadScript` takes `service-worker` scripts | Throws `NotSupportedException`; `frame` scripts match Electron | CEF: no script runs in a service worker | Patch |
| E22 | Spell checker: `removeWordFromSpellCheckerDictionary`, `setSpellCheckerDictionaryDownloadURL`, the `spellcheck-dictionary-*` events, `webPreferences.spellcheck` | The two methods throw `NotSupportedException`; no events; no per-page switch | CEF: no way to remove a custom word, choose the download server, hear about dictionary downloads, or turn checking off for one page | Patch |
| E23 | `addWordToSpellCheckerDictionary` works with no page open | Needs an open page in the session; false without one | CEF: adding a word goes through a browser | — |
| E24 | `ses.setCertificateVerifyProc` decides every certificate | Missing | CEF: it reports only certificates Chromium rejected, through `certificate-error`; nothing sees each verification | Patch |
| E25 | `select-client-certificate` works with each OS's certificate store | Checked on Linux with NSS only; macOS (Keychain) and Windows (certificate store) are untested | Todo: a test needs a client certificate in the user's store, which the macOS run should not touch | Check on Windows |
| E26 | `certificate-error` also fires for a subframe, with `isMainFrame` false | Only a main-frame navigation raises it; a subframe's invalid certificate is refused without asking (checked) | CEF: `on_certificate_error` is not called for subframes | Patch |
| E11 | Session events (`select-*-device`, ...) | None | Todo: with E10 | With E10 |

## IPC, contextBridge, protocol

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| I1 | Structured clone (Map, Set, Date, ArrayBuffer, ...) | JSON only | Design: messages cross through DevTools bindings as JSON | Typed serializers for the common types |
| I2 | `ipcRenderer.sendSync/sendToHost`; listeners get an `IpcRendererEvent` | Missing; listeners get `{ sender, ports }`. `once`, `off` and `removeAllListeners` are done | CEF/DevTools: no synchronous path from the page to the main process | `sendSync` needs a Patch |
| I3 | `event.returnValue` (answers `sendSync`) | Missing; `ipcMain.once`, `handleOnce`, `removeAllListeners` and `event.reply` are done (`off` is `RemoveListener`) | Follows I2 | With `sendSync` |
| I4 | `contextBridge` exposes live objects and nested functions | Values are copied once and frozen; only top-level functions, which return promises; callbacks one level deep; no `exposeInIsolatedWorld` | Design/CEF: worlds share only the DOM, so values cross as JSON | Deeper proxying |
| I5 | `protocol.handle` takes and returns Fetch `Request`/`Response` | `ProtocolRequest` → `Task<ProtocolResponse>`; Oriel answers `Range` itself | Design | None |
| I6 | Privileges `allowServiceWorkers`, `codeCache` | Throw `NotSupportedException` | CEF: no service workers or V8 code cache for custom schemes | Patch |
| I7 | `MessagePortMain` raises `close` when the page closes its end | Raised when the page navigates away or the main side closes; not when the page calls `close()` on its end | CEF: Chromium 154 gives the bridged end no `close` event, and a close marker would leak into the page's own channels | Patch |
| I8 | Renderer `webFrame`: `setSpellCheckProvider`, `setIsolatedWorldInfo`, `getResourceUsage`, `clearCache`, the frame tree (`getFrameForSelector`, `findFrameByName`, `findFrameByToken`, `top`, `opener`, `parent`, `firstChild`, `nextSibling`), `routingId`; `getZoomFactor` follows every zoom change | Missing; `getZoomFactor`/`getZoomLevel` follow zoom set through Oriel (`webFrame` or `WebContents`), not keyboard or wheel zoom; `insertCSS` keeps the `user` origin as author (C25) | Design: `oriel.webFrame` runs in the preload world and works through the main process over DevTools, which reports no zoom changes and has no renderer frame objects | Patch |

## Platform and runtime

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| P1 | H.264 and AAC | Stock CEF: open codecs only | CEF builds without proprietary codecs | Codecs runtime variant (M7); the build is deferred, see todo.md |
| P2 | Wayland | X11/XWayland only | CEF: Views on Wayland is still being fixed upstream | Follow upstream |
| P3 | Real Keychain on macOS (`safeStorage`) | `--use-mock-keychain` | Design until signing: ad-hoc builds change identity and macOS prompts every build | Real Keychain for signed builds (M4) |
| P4 | macOS apps exit cleanly | Often exit with code 2 after "Teardown watchdog expired" (~10 s) | Unknown: stock cefclient does the same | Investigate |
| P5 | A popup menu does not stall the main process | On macOS, CEF's UI tasks wait while a popup or context menu is open | CEF: AppKit's menu loop does not run CEF's task source | Run tasks in the tracking modes during menus |
| P6 | `contentTracing.getCategories()`, `getTraceBufferUsage()`, trace options (`recording_mode`, buffer size, argument filter, mixed included and excluded categories) | The two methods and those options throw `NotSupportedException`; categories are recorded as CEF takes them | CEF: `cef_begin_tracing` takes one category list, and only the browser's DevTools target lists categories or buffer use | Patch |
