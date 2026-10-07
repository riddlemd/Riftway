# Electron parity

Oriel mirrors Electron's whole API surface. Reference: **Electron v44.5.1** (`docs/api/` in
electron/electron). When Electron adds or changes a module, this table changes with it.

## Progress

Recounted 2026-10-07 against `src/Oriel`. Update these figures when a row or checklist item changes.

| Measure | Done | Partial | Not started | Total |
|---|---|---|---|---|
| Module rows below | 4 | 29 | 34 | 67 |
| M2 rows | 3 | 17 | 1 | 21 |

The 67 rows include `BrowserView` (deprecated, never implemented; counted as not started).

| Checklist | Applicable items | Done | Partial | Missing | % done | % done + partial/2 |
|---|---|---|---|---|---|---|
| [BrowserWindow](parity/browser-window.md) (with `BaseWindow`) | 294 | 143 | 18 | 133 | 48.6% | 51.7% |
| [webContents](parity/web-contents.md) (with `navigationHistory`, `WebFrameMain`) | 230 | 105 | 52 | 73 | 45.7% | 57.0% |

**Source** — where the implementation comes from:

- **CEF**: CEF's C API or the DevTools protocol through it; Oriel wraps it.
- **OS**: per-OS P/Invoke (Win32/WinRT, the Objective-C runtime, D-Bus/X11/portals on Linux).
- **.NET**: plain managed code.
- **Patch**: needs a change to Chromium or CEF, carried in Oriel's CEF build.
- **Pack**: packaging and tooling, not runtime API.

Where an implemented module differs from Electron, the difference and its reason are in [DRIFT.md](DRIFT.md).

**Wave** — the milestone that delivers it (see [PLAN.md](PLAN.md)). Status is `—` until work starts.

## Processes and lifecycle

| Electron | Oriel | Source | Wave | Status |
|---|---|---|---|---|
| `app` | `OrielApp` | CEF + OS | M2 | partial: `ready`, `window-all-closed`, `second-instance`, `before-quit`, `will-quit`, `quit()`, `exit()`, `enableSandbox`, `isReady`, `name`, `quit` (as `Exited`), `userAgentFallback`, `browser-window-created`, `web-contents-created`, `session-created`, `browser-window-focus`, `browser-window-blur`, `getVersion` (as `Version`), `hide`, `show`, `isHidden`, `focus`, `setBadgeCount`, `getBadgeCount`, `getAppMetrics` (A18), `getPath`, `setPath`, `getPreferredSystemLanguages`, `getSystemLocale`, `getLocaleCountryCode`, `certificate-error`, `login`, `select-client-certificate` (see DRIFT.md) |
| `process` | `OrielProcess` | .NET + CEF | M2 | partial: `type`, `versions` (`chrome`, `oriel`), `getCreationTime`, `getCPUUsage`, `getSystemMemoryInfo`, `getSystemVersion`, `setFdLimit`, `getProcessMemoryInfo` (`shared` is 0, P8), `windowsStore` (P10), `resourcesPath` (P17), `crash`, `hang` (P18); no `mas`; V8/Blink-only members are P7 |
| `CommandLine` (`app.commandLine`) | `OrielApp.CommandLine` | CEF | M2 | done: `appendSwitch`, `appendArgument`, `hasSwitch`, `getSwitchValue`, `removeSwitch`, in Chromium's switch syntax, applied before Chromium starts (DRIFT A19) |
| Command-line switches | same switches | CEF | M2 | partial: Chromium's switches pass through from the process's arguments and `CommandLine`; `--enable-logging`, `--log-file`, `--log-level` work (DRIFT A6); Node's (`--inspect`, ...) do not apply; the rest not checked one by one |
| Environment variables | same variables | .NET | M2 | partial: `ELECTRON_ENABLE_LOGGING`, `ELECTRON_LOG_FILE`, with `ORIEL_*` aliases (DRIFT A5) |
| `utilityProcess` | `UtilityProcess` (.NET child process) | .NET | M6 | — |
| `parentPort` | `ParentPort` | .NET | M6 | — |
| `MessageChannelMain` / `MessagePortMain` | `MessageChannelMain` / `MessagePortMain` | .NET + CEF | M6 | partial: channels in the main process, ports moved to and from pages (`webContents.postMessage`, `frame.postMessage`, `ipcRenderer.postMessage`, `event.ports`), ports through ports, `start`, `close`; JSON messages (I1); no `close` from a page's own `close()` (I7) |
| `crashReporter` | `CrashReporter` (Crashpad) | CEF | M4 | — |
| `contentTracing` | `ContentTracing` | CEF | M6 | partial: `startRecording` (`TraceConfig` and `TraceCategoriesAndOptions`), `stopRecording`, `getCategories`, `getTraceBufferUsage`; options beyond categories, `getCategories` and buffer usage go through an open page's DevTools session (P6, P14, P15, P16); no `enableHeapProfiling` |
| `autoUpdater` | `AutoUpdater` (Velopack) | Pack | M4 | — |
| Fuses, ASAR | Fuses, packed app archive with integrity check | Pack | M4 | — |

## Windows and views

Detailed item-by-item status: [parity/browser-window.md](parity/browser-window.md) (`BaseWindow` items are counted there with `BrowserWindow`).

| Electron | Oriel | Source | Wave | Status |
|---|---|---|---|---|
| `BaseWindow` | `BaseWindow` | CEF (Views) | M2 | — |
| `BrowserWindow` | `BrowserWindow` | CEF (Views) | M2 | partial: 143 of 294 applicable items done (48.6%), see parity/browser-window.md |
| `View` | `View` | CEF (Views panel) | M2 | partial: `addChildView` (index, move to top, reparent), `removeChildView`, `children`, `setBounds`/`getBounds`, `setVisible`/`getVisible`, `setBackgroundColor`, `bounds-changed`; no `setBorderRadius` (DRIFT V1) |
| `WebContentsView` | `WebContentsView` | CEF (Views browser view) | M2 | partial: `new WebContentsView({ webPreferences })`, `webContents`, child views over the page, in `BrowserWindow.ContentView`; pages outlive their window and the view stays when its page closes, as Electron; popups and `webContents.close()` from view pages; `webContents` option for a `createWindow` popup (V4); Alloy style (DRIFT V2, V4, V7, V9, V12, V13) |
| `ImageView` | `ImageView` | CEF + .NET | M5 | — |
| `win.setSimpleFullScreen` (macOS) | `BrowserWindow.SetSimpleFullScreen` | OS | M2 | done (native full screen blacks out other displays on macOS) |
| `BrowserView` (deprecated) | not implemented; use `WebContentsView` | — | — | — |
| `<webview>` tag | `<webview>` | Patch | M7 | — |
| `window.open` handling | `WebContents.SetWindowOpenHandler` | CEF | M2 | partial: handler (allow/deny, override options, outlivesOpener, `createWindow` into the app's window or view), `did-create-window`, popups as full `BrowserWindow`s with the opener's preferences and preload, no popup blocker (see DRIFT.md) |
| `screen` | `Screen` | CEF | M2 | partial: displays with all of Electron's fields (macOS), nearest/matching, cursor point, DIP conversion, display events (polled on Windows and Linux; see DRIFT.md) |
| `-electron-corner-smoothing` CSS | same CSS property | Patch | M7 | — |

## Web contents and frames

Detailed item-by-item status, including `navigationHistory` and `WebFrameMain`: [parity/web-contents.md](parity/web-contents.md).

| Electron | Oriel | Source | Wave | Status |
|---|---|---|---|---|
| `webContents` | `WebContents` | CEF | M2 | partial: 105 of 230 applicable items done (45.7%) across `webContents`, `navigationHistory` and `WebFrameMain`, see parity/web-contents.md; `loadURL`/`loadFile`/`reload`, `send`, IPC, window-open handler, context menu, input events, zoom, user agent, DevTools |
| `webFrameMain` | `WebFrameMain` | CEF | M2 | partial: frame tree, url/origin/name, `frameToken`, `processId`, `osProcessId`, `visibilityState`, `fromFrameToken`, `executeJavaScript`, `reload`, `send`, IPC `senderFrame`; script and messages in in-process frames only; no `frameTreeNodeId`, `routingId`, `fromId`, `collectJavaScriptCallStack` (see DRIFT.md F1, F2, F5-F7) |
| `NavigationHistory` | `NavigationHistory` | CEF | M2 | partial: back/forward, index/offset moves, entries, clear; no `removeEntryAtIndex` or `restore` (see DRIFT.md) |
| `Debugger` (`webContents.debugger`) | `Debugger` (DevTools protocol) | CEF | M2 | done: `attach` (protocol version check), `isAttached`, `detach`, `sendCommand` (with `sessionId`), `message` and `detach` events; shares Oriel's protocol session (DRIFT C43) |
| `webFrame` (renderer) | `oriel.webFrame` in the preload | CEF (DevTools protocol) | M6 | partial: zoom (factor and level), `insertCSS`/`removeInsertedCSS`, `insertText`, `executeJavaScript`, `executeJavaScriptInIsolatedWorld`, `frameToken`, for the frame that calls (I8) |
| `webUtils` (renderer) | `oriel.webUtils` | CEF | M6 | — |
| `sharedTexture` | `SharedTexture` | Patch | M7 | — |

## IPC and preload

| Electron | Oriel | Source | Wave | Status |
|---|---|---|---|---|
| `ipcMain` | `IpcMain` | CEF | M2 | partial: handle, handleOnce, on, once, removeHandler, removeListener, removeAllListeners, event.reply, event.senderFrame; no `event.returnValue` (see DRIFT.md) |
| `ipcRenderer` | `oriel.ipcRenderer` in the page | CEF | M2 | partial: invoke, send, sendSync (I9), on, once, off, removeListener, removeAllListeners, postMessage with ports; no `sendToHost` (see DRIFT.md) |
| `contextBridge` | `oriel.contextBridge` (isolated world) | CEF | M2 | partial: exposeInMainWorld and executeInMainWorld, with functions proxied at any depth and returning synchronously, promises, errors, cloneable types and DOM nodes; no `exposeInIsolatedWorld` (I4) |
| `ipcMainServiceWorker` | `IpcMainServiceWorker` | CEF | M6 | — |

## Session and network

| Electron | Oriel | Source | Wave | Status |
|---|---|---|---|---|
| `session` | `Session` | CEF | M2 core, M6 rest | partial: core done (`setUserAgent`/`getUserAgent`, `defaultSession`, `fromPartition`, `fromPath`, `persist:` partitions, `WebPreferences.Session`/`Partition`, `WebContents.Session`, per-session `protocol`, `flushStorageData`, `webRequest`, `setPermissionRequestHandler`, `setPermissionCheckHandler` (`navigator.permissions.query` only, E28), `setDisplayMediaRequestHandler` (whole desktop, E30), `setDownloadPath`, `clearCache`, `clearAuthCache`, `closeAllConnections`, `setProxy`, `resolveHost` (no options, E38), `clearStorageData`, `clearData` (E35), `getCacheSize` (sessions on disk, E34), `downloadURL` with `headers` (hidden page, E37), `will-download`); no device permission handlers (E31), `clearHostResolverCache`, `resolveProxy`, `clearCodeCaches` (E8), `createInterruptedDownload` (E16) (see DRIFT.md E1-E38) |
| `protocol` | `Protocol` | CEF | M2 | partial: `handle`, `unhandle`, `isProtocolHandled`, `registerSchemesAsPrivileged`; per session through `Session.Protocol`; no `intercept*` or legacy `register*Protocol` (see DRIFT.md) |
| `Cookies` | `Cookies` | CEF | M6 | partial: `get`, `set` (Electron's rejection messages), `remove`, `flushStore`, `changed` (read from the store, see DRIFT E4, E5) |
| `WebRequest` | `WebRequest` | CEF | M6 | partial: all eight events with `urls`/`excludeUrls`/`types` filters, cancel and `redirectURL`, request header changes, `uploadData` (bytes and file paths), `frame`, `webContentsId`, custom-scheme requests, service and shared worker requests without a page; `onHeadersReceived` cannot change or cancel the response, no `fromCache`/`ip`/`initiatorOrigin`, a `net` module's requests unseen (see DRIFT.md E17-E19, E27) |
| `DownloadItem` | `DownloadItem` | CEF | M6 | — |
| `net` | `Net` | CEF | M6 | — |
| `ClientRequest` / `IncomingMessage` | `ClientRequest` / `IncomingMessage` | CEF | M6 | — |
| `WebSocket` (main process) | `WebSocket` | CEF or Patch (verify) | M6 | — |
| `netLog` | `NetLog` | CEF | M6 | — |
| `ServiceWorkers` / `ServiceWorkerMain` | `ServiceWorkers` / `ServiceWorkerMain` | CEF + Patch (verify) | M6 | — |
| `Extensions` / extensions API | `Extensions` | CEF (verify Chrome-style support) | M6 | — |

## Desktop integration

| Electron | Oriel | Source | Wave | Status |
|---|---|---|---|---|
| `dialog` (file dialogs) | `Dialog` | OS (macOS) + CEF | M2 | partial: macOS matches (NSOpenPanel/NSSavePanel as Electron: every option, sheets, window-less and Sync; bookmarks on any build, G9); Linux and Windows run CEF's dialog with a window, fewer options and async-only on the UI thread (see DRIFT.md G1–G8) |
| `dialog` (message boxes, error box, certificate trust) | `Dialog` | OS | M5 | partial: Windows message boxes (`ShowMessageBoxAsync`/`Sync` with every option, `Signal`, `ShowErrorBox`) as `message_box_win.cc` builds them, macOS (NSAlert sheets, window-less and Sync boxes, the error box) as `message_box_mac.mm`, and Linux (GtkMessageDialog async and `gtk_dialog_run` Sync boxes, transient for and modal to the window, `Signal`, the error box and its pre-ready stderr form) as `message_box_gtk.cc`, the parent left enabled (MB4); `showCertificateTrustDialog` missing (see DRIFT.md) |
| `Menu` / `MenuItem` (app menu, context menus) | `Menu` / `MenuItem` | OS + CEF | M5 | partial: application and window menus, popups and context menus, roles, accelerators, `click`'s `KeyboardEvent`; no icons, sublabels or tooltips |
| `Tray` | `Tray` | OS | M5 | — |
| `Notification` | `Notification` | OS | M5 | — |
| `globalShortcut` | `GlobalShortcut` | OS | M5 | — |
| `Dock` (`app.dock`, macOS) | `OrielApp.Dock` | OS | M5 | done: `setBadge`, `getBadge`, `bounce`, `cancelBounce`, `downloadFinished`, `hide`, `show` (as `ShowAsync`), `isVisible`, `setMenu`, `getMenu`, `setIcon` |
| `clipboard` / `ClipboardItem` | `Clipboard` / `ClipboardItem` | OS | M5 | partial: Windows and macOS, Electron 44's W3C-style API (`read`/`write` with `ClipboardItem`, `readText`, `writeText`, `has`, `clear`; text, HTML, RTF, images, bookmark, files, raw `osclipboard` and `web` custom formats, Chromium's type list; on macOS the bookmark title and `electron application/findtext`); Linux throws; no `selection` (DRIFT K1-K11, branches `shell-clipboard`, `mac-clipboard`) |
| `shell` | `Shell` | OS | M5 | partial: Windows, as Electron 44 does it (openExternal with `logUsage`, openPath, showItemInFolder, trashItem, beep, shortcut links); macOS and Linux throw (DRIFT H1, H3, H4, branch `shell-clipboard`) |
| `nativeImage` | `NativeImage` | CEF + .NET | M5 | partial: PNG/JPEG through CEF, raw bitmaps, data URLs, `@Nx` files, crop, resize, representations; window icon, overlay and thumbar buttons take it; gaps in DRIFT B1, B3-B5 (no `createThumbnailFromPath`, template images unused); resize uses Skia's Hamming-1/Lanczos-3 filters and `.ico` reads on Windows |
| `nativeTheme` | `NativeTheme` | OS + CEF | M5 | partial: `themeSource` (pages, Chromium's UI, macOS appearance), `shouldUseDarkColors`, `shouldUseHighContrastColors`, `shouldUseInvertedColorScheme`, `inForcedColorsMode`, `prefersReducedTransparency`, `updated` (at once on macOS, polled elsewhere; DRIFT Y1-Y3) |
| `powerMonitor` | `PowerMonitor` | OS | M5 | — |
| `powerSaveBlocker` | `PowerSaveBlocker` | OS | M5 | — |
| `safeStorage` | `SafeStorage` | OS | M5 | — |
| `systemPreferences` | `SystemPreferences` | OS | M5 | — |
| `desktopCapturer` | `DesktopCapturer` | CEF + Patch (verify) | M5 | — |

## macOS-only

| Electron | Oriel | Source | Wave | Status |
|---|---|---|---|---|
| `TouchBar` + 11 item classes | `TouchBar` + items | OS | M7 | — |
| `ShareMenu` | `ShareMenu` | OS | M7 | — |
| `inAppPurchase` | `InAppPurchase` | OS | M7 | — |
| `pushNotifications` | `PushNotifications` | OS | M7 | — |

## Built-in AI

| Electron | Oriel | Source | Wave | Status |
|---|---|---|---|---|
| `LanguageModelUtility` | `LanguageModelUtility` | Patch (verify) | M7 | — |
| `localAIHandler` | `LocalAIHandler` | Patch (verify) | M7 | — |

## Naming

Electron's names in PascalCase. `on('x')` events become .NET events; callbacks and promises become
`Task`; option objects become records with Electron's property names. Electron's `spec/` tests are
the behaviour reference: each module is ported with tests derived from Electron's.
