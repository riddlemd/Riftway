# Electron parity

Oriel mirrors Electron's whole API surface. Reference: **Electron v44.5.1** (`docs/api/` in
electron/electron). When Electron adds or changes a module, this table changes with it.

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
| `app` | `OrielApp` | CEF + OS | M2 | partial: ready, window-all-closed, second-instance, quit, enableSandbox |
| `process` | `OrielProcess` | .NET + CEF | M2 | — |
| `CommandLine` (`app.commandLine`) | `OrielApp.CommandLine` | CEF | M2 | — |
| Command-line switches | same switches | CEF | M2 | — |
| Environment variables | same variables | .NET | M2 | — |
| `utilityProcess` | `UtilityProcess` (.NET child process) | .NET | M6 | — |
| `parentPort` | `ParentPort` | .NET | M6 | — |
| `MessageChannelMain` / `MessagePortMain` | `MessageChannel` / `MessagePort` | .NET + CEF | M6 | — |
| `crashReporter` | `CrashReporter` (Crashpad) | CEF | M4 | — |
| `contentTracing` | `ContentTracing` | CEF | M6 | — |
| `autoUpdater` | `AutoUpdater` (Velopack) | Pack | M4 | — |
| Fuses, ASAR | Fuses, packed app archive with integrity check | Pack | M4 | — |

## Windows and views

Detailed item-by-item status: [parity/browser-window.md](parity/browser-window.md).

| Electron | Oriel | Source | Wave | Status |
|---|---|---|---|---|
| `BaseWindow` | `BaseWindow` | CEF (Views) | M2 | — |
| `BrowserWindow` | `BrowserWindow` | CEF (Views) | M2 | partial: ~60 of ~300 items, see parity/browser-window.md |
| `View` | `View` | CEF (Views panel) | M2 | — |
| `WebContentsView` | `WebContentsView` | CEF (Views browser view) | M2 | — |
| `ImageView` | `ImageView` | CEF + .NET | M5 | — |
| `win.setSimpleFullScreen` (macOS) | `BrowserWindow.SetSimpleFullScreen` | OS | M2 | done (native full screen blacks out other displays on macOS) |
| `BrowserView` (deprecated) | not implemented; use `WebContentsView` | — | — | — |
| `<webview>` tag | `<webview>` | Patch | M7 | — |
| `window.open` handling | `WebContents.SetWindowOpenHandler` | CEF | M2 | — |
| `screen` | `Screen` | CEF | M2 | partial: displays, nearest/matching, cursor point, DIP conversion, polled display events (see DRIFT.md) |
| `-electron-corner-smoothing` CSS | same CSS property | Patch | M7 | — |

## Web contents and frames

| Electron | Oriel | Source | Wave | Status |
|---|---|---|---|---|
| `webContents` | `WebContents` | CEF | M2 | partial: loadURL, send |
| `webFrameMain` | `WebFrameMain` | CEF | M2 | — |
| `NavigationHistory` | `NavigationHistory` | CEF | M2 | — |
| `Debugger` (`webContents.debugger`) | `Debugger` (DevTools protocol) | CEF | M2 | — |
| `webFrame` (renderer) | `oriel.webFrame` in the page | CEF (DevTools protocol) | M6 | — |
| `webUtils` (renderer) | `oriel.webUtils` | CEF | M6 | — |
| `sharedTexture` | `SharedTexture` | Patch | M7 | — |

## IPC and preload

| Electron | Oriel | Source | Wave | Status |
|---|---|---|---|---|
| `ipcMain` | `IpcMain` | CEF | M2 | done: handle, on, removeHandler, removeListener |
| `ipcRenderer` | `oriel.ipcRenderer` in the page | CEF | M2 | done: invoke, send, on, removeListener |
| `contextBridge` | `oriel.contextBridge` (isolated world) | CEF | M2 | done: exposeInMainWorld (methods return promises; function args one level deep) |
| `ipcMainServiceWorker` | `IpcMainServiceWorker` | CEF | M6 | — |

## Session and network

| Electron | Oriel | Source | Wave | Status |
|---|---|---|---|---|
| `session` | `Session` | CEF | M2 core, M6 rest | — |
| `protocol` | `Protocol` | CEF | M2 | — |
| `Cookies` | `Cookies` | CEF | M6 | — |
| `WebRequest` | `WebRequest` | CEF | M6 | — |
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
| `dialog` (file dialogs) | `Dialog` | CEF | M2 | partial: open (files, several files, a directory) and save dialogs, async and sync (see DRIFT.md) |
| `dialog` (message boxes, error box, certificate trust) | `Dialog` | OS | M5 | — |
| `Menu` / `MenuItem` (app menu, context menus) | `Menu` / `MenuItem` | OS + CEF | M5 | — |
| `Tray` | `Tray` | OS | M5 | — |
| `Notification` | `Notification` | OS | M5 | — |
| `globalShortcut` | `GlobalShortcut` | OS | M5 | — |
| `Dock` (`app.dock`, macOS) | `OrielApp.Dock` | OS | M5 | — |
| `clipboard` / `ClipboardItem` | `Clipboard` / `ClipboardItem` | OS | M5 | — |
| `shell` | `Shell` | OS | M5 | — |
| `nativeImage` | `NativeImage` | .NET | M5 | — |
| `nativeTheme` | `NativeTheme` | OS + CEF | M5 | — |
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
