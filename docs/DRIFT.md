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
| A3 | `will-quit`, `before-quit`, `quit` events; `quit()` closes windows first | `Quit()` ends the message loop directly; no quit events | Todo | Close windows (with `Closing`), then quit; raise the events |

## BrowserWindow

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| W1 | `show`/`hide` fire whenever the window is shown or hidden | Fire only from `Show()`/`Hide()` | Unknown (CEF reports no visibility change) | OS notifications, or poll like minimize |
| W2 | `minimize`/`restore` events from the OS | Polled every 250 ms | CEF: reports neither; on macOS minimizing changes no bounds | OS notifications |
| W3 | `setIcon` takes a `NativeImage` | Takes a PNG/JPEG file path; Windows and Linux only | Module: `NativeImage` (M5) | Overload taking `NativeImage` |
| W5 | Modal windows disable their parent's input on Linux | The parent still takes focus under window managers that ignore the modal hint (openbox) | CEF: no input filter to block the parent | OS: X11 input shape or grab; check GNOME/KDE |
| W7 | `openDevTools({ mode })` with docked modes | Separate window only | Unknown | CEF `show_dev_tools` with an inspector view, or `BrowserView` docking |
| W8 | `setParentWindow` | Missing | CEF: Views sets the parent only at creation | OS: `addChildWindow:`, owner `HWND`, `XSetTransientForHint` |
| W9 | Native full screen on macOS | Same, but it blacks out other displays; `SimpleFullScreen` is the alternative. With the option set, the green button and the page's Fullscreen API also use simple full screen _(page path untested)_ | Design: KHost-style audience screens need the other displays | None needed; document |
| W10 | `closable` not implemented on Linux | Honoured on every OS | Design (superset) | None |
| W11 | `webPreferences.defaultFontFamily.math` | Throws `NotSupportedException` | CEF: no setting for it | Patch |
| W12 | `minimumFontSize`, `defaultEncoding` per window | Throw `NotSupportedException` | CEF: Chrome-style browsers take them from the profile, for every window | Per-window profiles (request contexts, with `Session`), or app-level options |
| W13 | `webSecurity`, `allowRunningInsecureContent`, `experimentalFeatures`, `enable/disableBlinkFeatures`, `autoplayPolicy`, `sandbox` per window | Missing | CEF: Chromium switches, process-wide | App-level options; per-window is a Patch |
| W14 | `contextIsolation: false` | Always on | Design: the preload runs in an isolated world by construction | None planned |
| W15 | `zoomFactor` | Re-applies to an origin whose zoom was reset to 0 | Todo | Track explicit resets |
| W16 | `isMenuBarVisible()` etc. on Windows/Linux; `setMenu` on macOS sets nothing; Windows uses a native Win32 menu bar | Same API; the bar is drawn by CEF Views: on Windows a plain strip following the system theme, without mnemonic underlines and with different padding and hover | CEF: Views windows have no native menu bar | Style the bar (colours, font, underlines) to match the OS |
| W17 | Alt shows an auto-hidden bar until the menu is used or focus moves on | Hidden again when its menu closes, or on Alt; not when focus moves back to the page without using a menu | Todo | Hide on focus loss and on a click into the page |
| W20 | `setProgressBar` on Linux (Unity launcher progress); error and paused colours on macOS | Linux shows no progress; macOS shows error and paused as the plain Dock bar | OS: Linux progress is desktop-specific (D-Bus `com.canonical.Unity.LauncherEntry`); `NSProgressIndicator` has no colours | D-Bus launcher entry on Linux; a drawn bar on macOS |
| W21 | `setOverlayIcon(overlay: NativeImage \| null, description)` | Takes a PNG/JPEG file path or null | Module: `NativeImage` (M5) | Overload taking `NativeImage` |
| W22 | `setThumbarButtons` (Windows) | Missing | Module: `NativeImage` for the icons; the clicks also need the window's `WM_COMMAND` (`THBN_CLICKED`) routed back | `ITaskbarList3::ThumbBarAddButtons` and a subclassed `HWND`, after `NativeImage` |
| W23 | `setProgressBar(NaN)` is unspecified (Electron casts it to an arbitrary value) | Removes the bar | Design | None |

## window.open

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| O1 | `details.features` is the page's raw string, and features such as `frame=no` or `show=no` apply to the window | Rebuilt from what CEF parsed: `left`, `top`, `width`, `height` only; other features are lost | CEF: `on_before_popup` gives parsed position and size only | Patch |
| O2 | `details.referrer`, `details.postBody` | Missing | CEF: `on_before_popup` has neither | Patch |
| O3 | Handler can `createWindow` with its own `webContents` | Not offered | Todo | Adopt a given window's view |
| O4 | A popup's first page loads once, with the preload | When it commits before Oriel's runtime and preload are registered over DevTools, it is reloaded once so they run: the page loads twice, and a POST target is untested | CEF: the popup's browser exists only as it starts loading; there is no about:blank start as for other windows | Patch, or register before the first commit (DevTools auto-attach) |
| O5 | `width`/`height` from features | Size the page area (`UseContentSize`), as browsers treat them; not compared with Electron | Unknown | Check Electron |

## navigationHistory

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| N1 | `removeEntryAtIndex`, `restore` (entries with `pageState`) | Missing; entries have only `url` and `title` | CEF: no way to remove or restore entries, and no page state | Patch |
| N2 | `goToIndex`, `goToOffset` move the history directly | Through the page's own `history.go(n)`, so they do nothing in a page with `Javascript = false` | CEF: only one-step `go_back` / `go_forward` | Patch, or step repeatedly |
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
| C8 | `context-menu` params `frame`, `linkText`, `altText`, `selectionRect`, `menuSourceType`, `formControlType`, `referrerPolicy`, `suggestedFilename`, `selectionStartOffset` | Missing | CEF: `cef_context_menu_params_t` lacks them; `frame` needs `WebFrameMain` | Patch; or read the element under the point with JS for text/alt; Module for `frame` |

## Menu / MenuItem

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| M1 | Default application menu on every OS | macOS only; Windows and Linux get no bar unless the app sets one | Design: keeps existing Oriel windows unchanged; macOS needs a menu for Cmd+C/V/Q | Decide; Electron's default is mostly reload/DevTools items |
| M2 | `click(menuItem, window, event)` | No `KeyboardEvent` argument | Todo | Pass whether an accelerator triggered it, and modifiers |
| M3 | Item `icon`, `sublabel`, `toolTip`, `registerAccelerator` | Missing | Todo (`icon` is Module: `NativeImage`) | `cef_menu_model` has no icon API either: Patch for Views menus |
| M4 | macOS roles for tabs, speech, recent documents, `shareMenu`, `showSubstitutions`... | Missing | Todo | AppKit selectors for each |
| M5 | `Command`/`Super` accelerators on Windows and Linux | Ignored | CEF: Views accelerators have no such modifier | OS key hook, or Patch |
| M7 | `popup({ positioningItem })` (macOS) | Missing | CEF: `show_menu` takes a point only | Patch |
| M8 | Role items' `click` runs if the role has no action | Same; but on macOS the editing, About and Hide roles are AppKit actions and never run `click` | Design: they then also work in DevTools and native text fields | None |

## screen

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| R1 | `display-added`, `display-removed`, `display-metrics-changed` from the OS | Displays compared once a second while a handler is attached, so events come up to a second late | CEF: reports no display changes | OS notifications (`NSApplicationDidChangeScreenParametersNotification`, `WM_DISPLAYCHANGE`, XRandR events) |
| R2 | `Display` has `label`, `internal`, `monochrome`, `colorSpace`, `colorDepth`, `depthPerComponent`, `displayFrequency`, `touchSupport`, `accelerometerSupport`, `detected`, `nativeOrigin`, `maximumCursorSize` | Only `id`, `bounds`, `workArea`, `size`, `workAreaSize`, `scaleFactor`, `rotation` | CEF: `cef_display_t` has no more | OS per field |
| R4 | `screenToDipPoint` / `dipToScreenPoint` on Windows and Linux only | Also on macOS | Design (superset) | None |
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

## IPC, contextBridge, protocol

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| I1 | Structured clone (Map, Set, Date, ArrayBuffer, ...) | JSON only | Design: messages cross through DevTools bindings as JSON | Typed serializers for the common types |
| I2 | `ipcRenderer.once/off/removeAllListeners/sendSync/sendToHost/postMessage` | Missing; listeners get `{ sender }`, not an `IpcRendererEvent` | Todo (`sendSync` and `postMessage` may be CEF-blocked) | Add the simple ones; investigate the rest |
| I3 | `ipcMain.once/handleOnce/off/removeAllListeners`, `event.reply`, `event.returnValue`, `senderFrame` | Missing | Todo (`senderFrame` is Module: `WebFrameMain`) | Add |
| I4 | `contextBridge` exposes live objects and nested functions | Values are copied once and frozen; only top-level functions, which return promises; callbacks one level deep; no `exposeInIsolatedWorld` | Design/CEF: worlds share only the DOM, so values cross as JSON | Deeper proxying |
| I5 | `protocol.handle` takes and returns Fetch `Request`/`Response` | `ProtocolRequest` → `Task<ProtocolResponse>`; Oriel answers `Range` itself | Design | None |
| I6 | Privileges `allowServiceWorkers`, `codeCache` | Throw `NotSupportedException` | CEF: no service workers or V8 code cache for custom schemes | Patch |
| I7 | `session.protocol` per session | `Protocol` is global | Module: `Session` | Add with `Session` |

## Platform and runtime

| ID | Electron | Oriel | Why | Possible resolution |
|---|---|---|---|---|
| P1 | H.264 and AAC | Stock CEF: open codecs only | CEF builds without proprietary codecs | Codecs runtime variant (M7); the build is deferred, see todo.md |
| P2 | Wayland | X11/XWayland only | CEF: Views on Wayland is still being fixed upstream | Follow upstream |
| P3 | Real Keychain on macOS (`safeStorage`) | `--use-mock-keychain` | Design until signing: ad-hoc builds change identity and macOS prompts every build | Real Keychain for signed builds (M4) |
| P4 | macOS apps exit cleanly | Often exit with code 2 after "Teardown watchdog expired" (~10 s) | Unknown: stock cefclient does the same | Investigate |
| P5 | A popup menu does not stall the main process | On macOS, CEF's UI tasks wait while a popup or context menu is open | CEF: AppKit's menu loop does not run CEF's task source | Run tasks in the tracking modes during menus |
