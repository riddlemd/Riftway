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

## BrowserWindow

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| W1 | macOS: `show`/`hide` also fire when the window is occluded or revealed (`windowDidChangeOcclusionState`); Windows and Linux fire them only from Oriel's own calls, as Oriel does everywhere | Fire only from `Show()`/`Hide()` | Todo: no occlusion observer on macOS | `NSWindowDidChangeOcclusionStateNotification` |
| W2 | `minimize`/`restore` events from the OS | Polled every 250 ms | CEF: reports neither; on macOS minimizing changes no bounds | OS notifications |
| W5 | Modal windows disable their parent's input on Linux | The parent still takes focus under window managers that ignore the modal hint (openbox) | CEF: no input filter to block the parent | OS: X11 input shape or grab; check GNOME/KDE |
| W7 | `openDevTools({ mode })` with docked modes | Separate window only | Unknown | CEF `show_dev_tools` with an inspector view, or `BrowserView` docking |
| W8 | `setParentWindow` | Missing | CEF: Views sets the parent only at creation | OS: `addChildWindow:`, owner `HWND`, `XSetTransientForHint` |
| W9 | `setFullScreen(false)` does nothing in simple full screen (its guard reads the full-screen style, which simple full screen does not set) | Leaves simple full screen | Design: Electron's guard looks like a bug; matching it would strand the window | None |
| W11 | `webPreferences.defaultFontFamily.math` | Throws `NotSupportedException` | CEF: no setting for it | Patch |
| W12 | `minimumFontSize`, `defaultEncoding` per window | Throw `NotSupportedException` | CEF: Chrome-style browsers take them from the profile, for every window | Set them per `Session` (profile preferences), for every window in it |
| W13 | `webSecurity`, `allowRunningInsecureContent`, `experimentalFeatures`, `enable/disableBlinkFeatures`, `autoplayPolicy`, `sandbox` per window | Missing | CEF: Chromium switches, process-wide | App-level options; per-window is a Patch |
| W14 | `contextIsolation: false` | Always on | Design: the preload runs in an isolated world by construction | None planned |
| W15 | `zoomFactor` | Re-applies to an origin whose zoom was reset to 0 | Todo | Track explicit resets |
| W16 | `isMenuBarVisible()` etc. on Windows/Linux; the bar is a Views `MenuBar` (as Oriel's), with its own colours, padding, hover and mnemonic underlines; Linux can also export a DBus global menu | Same API and a Views bar, but plain: system-theme colours, no mnemonic underlines, different padding and hover; no global menu | CEF: label buttons cannot draw underlines; the rest is Todo | Match Electron's colours and padding; DBus menu on Linux |
| W17 | Alt shows an auto-hidden bar until the user moves on | Hidden again on Alt, when its menu closes, when a key goes to the page, or on focus loss; a mouse click into the page does not hide it | Todo: the window sees no page mouse events | Hide on the page's mousedown, through the preload |
| W20 | `setProgressBar` on Linux (Unity launcher progress); error and paused colours on macOS | Linux shows no progress; macOS shows error and paused as the plain Dock bar | OS: Linux progress is desktop-specific (D-Bus `com.canonical.Unity.LauncherEntry`); `NSProgressIndicator` has no colours | D-Bus launcher entry on Linux; a drawn bar on macOS |
| W24 | `backgroundMaterial` / `setBackgroundMaterial` show Mica, Acrylic or Mica Alt behind a transparent window | DWM takes and reports the backdrop (`DWMWA_SYSTEMBACKDROP_TYPE`), but nothing shows: CEF paints the client area and its own title bar opaque, even with a transparent page and `BackgroundColor = "#00000000"`. Without the option DWM already reports Mica (`DWMSBT_MAINWINDOW`), set by Chromium or CEF, not Oriel | CEF: Views windows on Windows draw their own frame and an opaque background | `DwmExtendFrameIntoClientArea` and a transparent Views window; check whether CEF's frameless mode composites alpha |
| W25 | `titleBarStyle: 'hidden'` with `titleBarOverlay` (Windows caption buttons over the page, `getTitleBarOverlay` geometry) | Missing | OS: needs a frameless window, caption buttons drawn over the page and `WM_NCHITTEST` for the drag area | Frameless window plus an overlay view (`W.add_overlay_view`) with min/max/close buttons, after `titleBarStyle` |
| W26 | `icon` option takes a `NativeImage` or a path | Two options: `Icon` (path) and `IconImage` (`NativeImage`, wins over `Icon`) | Design: a C# property has one type | None |
| W27 | `setBounds`, `setContentBounds`, `setSize`, `setContentSize`, `setPosition` take `animate` (macOS only) | No `animate` argument; changes are immediate | Todo: not yet implemented | `NSWindow setFrame:display:animate:` through the Objective-C runtime |
| W29 | `setAlwaysOnTop(flag, level, relativeLevel)`: macOS window levels, and on Windows the floating to status levels put the window behind the taskbar | Takes `flag` only | Todo | `NSWindow.level` plus relativeLevel; on Windows the taskbar ordering |

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
| F2 | `frameTreeNodeId`, `processId`, `routingId`, `osProcessId`, `visibilityState`, `postMessage`, `collectJavaScriptCallStack`; `webFrameMain.fromId` / `fromFrameToken` | Missing; `frameToken` is Chromium's DevTools frame token | Todo, or CEF: the numeric ids are not exposed | Add what DevTools gives (frame lookup by token) |
| F3 | `webContents.focusedFrame` | Missing | CEF: the focused frame comes as a CEF frame, which cannot be matched to a DevTools frame id | Track focus in the page (`focusin`) |
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
| C2 | `UploadFile` `offset`/`length` | Throw; `modificationTime` ignored | CEF uploads whole files and does not check the time | Copy the slice to a temp file |
| C3 | User agent override sets client hints | No `Sec-CH-UA*` or `navigator.userAgentData` change | CEF/DevTools: `Emulation.setUserAgentOverride` without metadata | Pass `userAgentMetadata` through DevTools |
| C4 | `getUserAgent()` always answers | Empty until DevTools answers, just after the window is created | Todo | Read the default UA from CEF settings |
| C5 | A reload repeats the `loadURL` options | Likely a plain request (untested) | Todo | Keep the options with the navigation entry |
| C6 | `executeJavaScript` resolves a structured clone | JSON only (`JsonNode?`); undefined is null | Design: DevTools `Runtime.evaluate` returns JSON | Typed overloads |
| C7 | `sendInputEvent` wheel, `mouseEnter`/`mouseLeave`/`contextMenu` types | Keyboard and mouse down/up/move only | Todo | DevTools `Input.dispatchMouseEvent` `mouseWheel` |
| C8 | `context-menu` params `frame`, `linkText`, `altText`, `selectionRect`, `menuSourceType`, `formControlType`, `referrerPolicy`, `suggestedFilename`, `selectionStartOffset` | Missing | CEF: `cef_context_menu_params_t` lacks them; `frame`: CEF names the frame by its own identifier, which no DevTools frame id matches (WebFrameMain is built on DevTools) | Patch; or read the element under the point with JS for text/alt; Module for `frame` |

## Menu / MenuItem

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| M2 | `click(menuItem, window, event)` | No `KeyboardEvent` argument | Todo | Pass whether an accelerator triggered it, and modifiers |
| M3 | Item `icon`, `sublabel`, `toolTip`, `registerAccelerator` | Missing | Todo (`icon` is Module: `NativeImage`) | `cef_menu_model` has no icon API either: Patch for Views menus |
| M4 | macOS roles for tabs, speech, recent documents, `shareMenu`, `showSubstitutions`... | Missing | Todo | AppKit selectors for each |
| M5 | `Command`/`Super` accelerators on Windows and Linux | Ignored | CEF: Views accelerators have no such modifier | OS key hook, or Patch |
| M7 | `popup({ positioningItem })` (macOS) | Missing | CEF: `show_menu` takes a point only | Patch |

## screen

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| R1 | `display-added`, `display-removed`, `display-metrics-changed` from the OS | Displays compared once a second while a handler is attached, so events come up to a second late | CEF: reports no display changes | OS notifications (`NSApplicationDidChangeScreenParametersNotification`, `WM_DISPLAYCHANGE`, XRandR events) |
| R2 | `Display` has `label`, `internal`, `monochrome`, `colorSpace`, `colorDepth`, `depthPerComponent`, `displayFrequency`, `touchSupport`, `accelerometerSupport`, `detected`, `nativeOrigin`, `maximumCursorSize` | Only `id`, `bounds`, `workArea`, `size`, `workAreaSize`, `scaleFactor`, `rotation` | CEF: `cef_display_t` has no more | OS per field |
| R5 | `getCursorScreenPoint` under Wayland | X11 only (`XQueryPointer` on Oriel's own X connection) | OS; see P2 | Follow P2 |

## dialog

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| G1 | File dialogs without a window are app-modal | A dialog needs an open window (the given, focused or any) and is a sheet on it on macOS | CEF: `run_file_dialog` belongs to a browser host | OS dialogs for the window-less case |
| G2 | `buttonLabel`, `message`, `nameFieldLabel`, `showsTagField`, `securityScopedBookmarks`; properties `showHiddenFiles`, `createDirectory`, `promptToCreate`, `noResolveAliases`, `treatPackageAsDirectory`, `dontAddToRecent` | Throw `NotSupportedException` (`securityScopedBookmarks` not offered) | CEF: `run_file_dialog` takes a mode, title, path and filters only | OS dialogs, or Patch |
| G3 | `openFile` + `openDirectory` together (macOS); several directories | Throw | CEF: no such modes | OS dialogs, or Patch |
| G4 | `defaultPath`'s directory is where the dialog opens | Same, but only by setting Chrome's `selectfile.last_directory` preference first: the page's own file choosers then start there too, and it persists in the profile | CEF: Chrome style runs the dialog as a page's file chooser, which keeps only the file name | Patch: pass the directory through |
| G5 | Folder dialogs accept with "Select" / "Open" | Linux (GTK): the accept button says "Upload" | CEF: Chromium's web folder-upload chooser | Patch, or an OS dialog on Linux |
| G6 | A `*` filter offers every file type | The `*` filter is dropped; whether every type stays selectable depends on the platform dialog (unverified) | CEF: filters are `Name|.ext` strings | Verify per OS |
| G7 | `showOpenDialogSync` blocks the main process | The `Sync` variants throw on the UI thread, which must stay free to run the dialog | CEF: the dialog answers on the UI thread | None; use the async form there |
| G8 | Return accepts a Linux save dialog | Return in the name field did not accept Chromium's GTK save dialog in testing (Xvfb, openbox); clicking Save did | Unknown: likely no default button set | Check on a desktop session; Patch if confirmed |

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
| E9 | `setUserAgent`, `getUserAgent` per session | Missing; `WebContents.SetUserAgent` sets it per page | CEF: the user agent is process-wide or per page (DevTools) | Apply to each page in the session |
| E10 | `webRequest`, permission handlers, downloads (`will-download`), spellchecker, `preconnect`, certificate verify proc, extensions, `getPreloads`/`registerPreloadScript` | Missing | Todo: CEF has handlers for most (resource request, permission, download, certificate) | Later Session work (M6) |
| E11 | Session events (`will-download`, `select-*-device`, ...) | None | Todo: with E10 | With E10 |

## IPC, contextBridge, protocol

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| I1 | Structured clone (Map, Set, Date, ArrayBuffer, ...) | JSON only | Design: messages cross through DevTools bindings as JSON | Typed serializers for the common types |
| I2 | `ipcRenderer.sendSync/sendToHost/postMessage`; listeners get an `IpcRendererEvent` | Missing; listeners get `{ sender }`. `once`, `off` and `removeAllListeners` are done | CEF/DevTools: no synchronous path from the page to the main process | `postMessage` over the binding; `sendSync` needs a Patch |
| I3 | `event.returnValue` (answers `sendSync`) | Missing; `ipcMain.once`, `handleOnce`, `removeAllListeners` and `event.reply` are done (`off` is `RemoveListener`) | Follows I2 | With `sendSync` |
| I4 | `contextBridge` exposes live objects and nested functions | Values are copied once and frozen; only top-level functions, which return promises; callbacks one level deep; no `exposeInIsolatedWorld` | Design/CEF: worlds share only the DOM, so values cross as JSON | Deeper proxying |
| I5 | `protocol.handle` takes and returns Fetch `Request`/`Response` | `ProtocolRequest` → `Task<ProtocolResponse>`; Oriel answers `Range` itself | Design | None |
| I6 | Privileges `allowServiceWorkers`, `codeCache` | Throw `NotSupportedException` | CEF: no service workers or V8 code cache for custom schemes | Patch |

## Platform and runtime

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| P1 | H.264 and AAC | Stock CEF: open codecs only | CEF builds without proprietary codecs | Codecs runtime variant (M7); the build is deferred, see todo.md |
| P2 | Wayland | X11/XWayland only | CEF: Views on Wayland is still being fixed upstream | Follow upstream |
| P3 | Real Keychain on macOS (`safeStorage`) | `--use-mock-keychain` | Design until signing: ad-hoc builds change identity and macOS prompts every build | Real Keychain for signed builds (M4) |
| P4 | macOS apps exit cleanly | Often exit with code 2 after "Teardown watchdog expired" (~10 s) | Unknown: stock cefclient does the same | Investigate |
| P5 | A popup menu does not stall the main process | On macOS, CEF's UI tasks wait while a popup or context menu is open | CEF: AppKit's menu loop does not run CEF's task source | Run tasks in the tracking modes during menus |
