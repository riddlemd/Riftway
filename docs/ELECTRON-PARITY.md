# Electron parity

Oriel mirrors Electron's whole API surface. Reference: **Electron v44.5.1** (`docs/api/` in
electron/electron). When Electron adds or changes a module, this table changes with it.

## Progress

These figures are counted from the rows: after changing a row or checklist item, run `node tools/recount-parity.js`. The unit tests (`ParityCountTests`) fail while they are stale.

| Measure | Done | Partial | Not started | Total |
|---|---|---|---|---|
| Module rows below | 8 | 41 | 18 | 67 |
| M2 rows | 3 | 17 | 1 | 21 |

The 67 rows include `BrowserView` (deprecated, never implemented; counted as not started).

| Checklist | Applicable items | Done | Partial | Missing | % done | % done + partial/2 |
|---|---|---|---|---|---|---|
| [BrowserWindow](parity/browser-window.md) (with `BaseWindow`) | 294 | 168 | 55 | 71 | 57.1% | 66.5% |
| [webContents](parity/web-contents.md) (with `navigationHistory`, `WebFrameMain`) | 230 | 111 | 70 | 49 | 48.3% | 63.5% |
| [DownloadItem](parity/download-item.md) (with the session's download API) | 31 | 20 | 10 | 1 | 64.5% | 80.6% |
| [app](parity/app.md) (with `app.dock` as one item) | 114 | 67 | 30 | 17 | 58.8% | 71.9% |
| [session](parity/session.md) (module and `Session`; other modules' members n/a) | 75 | 24 | 16 | 35 | 32.0% | 42.7% |

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
| `app` | `OrielApp` | CEF + OS | M2 | partial: 67 of 114 applicable items done (58.8%), see parity/app.md; lifecycle, paths, locale, protocol client, login items and recent documents (Windows, macOS; protocol client on Linux), the macOS app delegate's events, badge, jump list, metrics, single-instance lock over CEF's profile lock (A2), GPU status and info from a hidden chrome://gpu page (A27); `setProxy` through the global proxy preference; `child-process-gone`, the accessibility mode and `resolveProxy` with Oriel's CEF patches 0007-0009 (A28, A31, A35); no Handoff, NSS imports (see DRIFT.md) |
| `process` | `OrielProcess` | .NET + CEF | M2 | partial: `type`, `versions` (`chrome`, `oriel`), `getCreationTime`, `getCPUUsage`, `getSystemMemoryInfo`, `getSystemVersion`, `setFdLimit`, `getProcessMemoryInfo` (`shared` is 0, P8), `windowsStore` (P10), `resourcesPath` (P17), `crash`, `hang` (P18); no `mas`; V8/Blink-only members are P7 |
| `CommandLine` (`app.commandLine`) | `OrielApp.CommandLine` | CEF | M2 | done: `appendSwitch`, `appendArgument`, `hasSwitch`, `getSwitchValue`, `removeSwitch`, in Chromium's switch syntax, applied before Chromium starts (DRIFT A19) |
| Command-line switches | same switches | CEF | M2 | partial: Chromium's switches pass through from the process's arguments and `CommandLine`; `--enable-logging`, `--log-file`, `--log-level` work (DRIFT A6); Node's (`--inspect`, ...) do not apply; the rest not checked one by one |
| Environment variables | same variables | .NET | M2 | partial: `ELECTRON_ENABLE_LOGGING`, `ELECTRON_LOG_FILE`, with `ORIEL_*` aliases (DRIFT A5) |
| `utilityProcess` | `UtilityProcess` (.NET child process) | .NET | M6 | partial: `fork` (a .NET entry assembly or executable), `env`, `execArgv` (host options), `cwd`, `stdio` (string and array forms), `postMessage` with ports, `kill`, `pid`, `stdout`, `stderr`, `spawn`, `exit`, `message`, `error` (on an unhandled exception); children killed on quit; no `session`/`partition`/`login` (see DRIFT.md UP1-UP6) |
| `parentPort` | `ParentPort` | .NET | M6 | partial: `message` (with ports, queued until the first handler), `postMessage`; usable from a plain .NET app without CEF; the child ends when its parent goes (UP6) |
| `MessageChannelMain` / `MessagePortMain` | `MessageChannelMain` / `MessagePortMain` | .NET + CEF | M6 | partial: channels in the main process, ports moved to and from pages (`webContents.postMessage`, `frame.postMessage`, `ipcRenderer.postMessage`, `event.ports`) and utility processes (`child.postMessage`, `parentPort` `event.ports`, ports through ports in the child), ports through ports, `start`, `close`; JSON messages (I1); no `close` from a page's own `close()` (I7) |
| `crashReporter` | `CrashReporter` (Crashpad) | CEF | M4 | partial: every method; `Start` before `Run()` writes CEF's `crash_reporter.cfg`; Windows and macOS verified end to end (renderer and browser crashes, uploads, local-only, rate limit), Linux written and unrun; see DRIFT.md CR1–CR12, CR14–CR16 |
| `contentTracing` | `ContentTracing` | CEF | M6 | partial: `startRecording` (`TraceConfig` and `TraceCategoriesAndOptions`), `stopRecording`, `getCategories`, `getTraceBufferUsage`; options beyond categories, `getCategories` and buffer usage go through an open page's DevTools session (P6, P14, P15, P16); no `enableHeapProfiling` |
| `autoUpdater` | `AutoUpdater` (Velopack) | Pack | M4 | — |
| Fuses, ASAR | Fuses, packed app archive with integrity check | Pack | M4 | — |

## Windows and views

Detailed item-by-item status: [parity/browser-window.md](parity/browser-window.md) (`BaseWindow` items are counted there with `BrowserWindow`).

| Electron | Oriel | Source | Wave | Status |
|---|---|---|---|---|
| `BaseWindow` | `BaseWindow` | CEF (Views) | M2 | — |
| `BrowserWindow` | `BrowserWindow` | CEF (Views) | M2 | partial: 168 of 294 applicable items done (57.1%), see parity/browser-window.md |
| `View` | `View` | CEF (Views panel) | M2 | partial: `addChildView` (index, move to top, reparent), `removeChildView`, `children`, `setBounds`/`getBounds`, `setVisible`/`getVisible`, `setBackgroundColor`, `bounds-changed`; no `setBorderRadius` (DRIFT V1) |
| `WebContentsView` | `WebContentsView` | CEF (Views browser view) | M2 | partial: `new WebContentsView({ webPreferences })`, `webContents`, child views over the page, in `BrowserWindow.ContentView`; pages outlive their window and the view stays when its page closes, as Electron; popups and `webContents.close()` from view pages; `webContents` option for a `createWindow` popup (V4); Alloy style (DRIFT V2, V4, V7, V9, V12, V13) |
| `ImageView` | `ImageView` | CEF + .NET | M5 | — |
| `win.setSimpleFullScreen` (macOS) | `BrowserWindow.SetSimpleFullScreen` | OS | M2 | done (native full screen blacks out other displays on macOS) |
| `BrowserView` (deprecated) | not implemented; use `WebContentsView` | — | — | — |
| `<webview>` tag | `<webview>` | Patch | M7 | — |
| `window.open` handling | `WebContents.SetWindowOpenHandler` | CEF | M2 | partial: handler (allow/deny, override options, outlivesOpener, `createWindow` into the app's window or view), `did-create-window`, popups as full `BrowserWindow`s with the opener's preferences and preload, no popup blocker (see DRIFT.md) |
| `screen` | `Screen` | CEF | M2 | partial: displays with all of Electron's fields (macOS and Windows), nearest/matching, cursor point, DIP conversion, display events (from OS notifications on macOS, window messages on Windows and XRandR events on Linux, polled only without XRandR; see DRIFT.md) |
| `-electron-corner-smoothing` CSS | same CSS property | Patch | M7 | — |

## Web contents and frames

Detailed item-by-item status, including `navigationHistory` and `WebFrameMain`: [parity/web-contents.md](parity/web-contents.md).

| Electron | Oriel | Source | Wave | Status |
|---|---|---|---|---|
| `webContents` | `WebContents` | CEF | M2 | partial: 111 of 230 applicable items done (48.3%) across `webContents`, `navigationHistory` and `WebFrameMain`, see parity/web-contents.md; `loadURL`/`loadFile`/`reload`, `send`, IPC, window-open handler, context menu, input events, zoom, user agent, DevTools |
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
| `session` | `Session` | CEF | M2 core, M6 rest | partial: 24 of 75 applicable items done (32.0%), see parity/session.md; `defaultSession`, `fromPartition`, `fromPath` (E1-E3), user agent, permission and display-media handlers (E12, E28-E32), cache, auth and storage clearing (E34-E37), `setDownloadPath`, preloads (E21), spell checker (E22, E23), `setProxy` and `resolveHost` (E38), `enableNetworkEmulation` per page (E43), `setSSLConfig` for every session (E44), `allowNTLMCredentialsForDomains`, `storagePath`, `spellCheckerEnabled`; no device handlers or chooser events (E11, E31), `clearHostResolverCache`, `clearCodeCaches`, `setCodeCachePath`, `forceReloadProxyConfig` (E8), `resolveProxy` with Oriel's CEF patch 0008 only (A35), `preconnect` (E10), `setCertificateVerifyProc` (E24), `getBlobData` (E47), shared dictionaries (E48) |
| `protocol` | `Protocol` | CEF | M2 | partial: `handle`, `unhandle`, `isProtocolHandled`, `registerSchemesAsPrivileged`; per session through `Session.Protocol`; no `intercept*` or legacy `register*Protocol` (see DRIFT.md) |
| `Cookies` | `Cookies` | CEF | M6 | partial: `get`, `set` (Electron's rejection messages), `remove`, `flushStore`, `changed` (read from the store, see DRIFT E4, E5) |
| `WebRequest` | `WebRequest` | CEF | M6 | partial: all eight events with `urls`/`excludeUrls`/`types` filters, cancel and `redirectURL`, request header changes, `uploadData` (bytes and file paths), `frame`, `webContentsId`, custom-scheme requests, service and shared worker requests without a page; `onHeadersReceived` cannot change or cancel the response, no `fromCache`/`ip`/`initiatorOrigin`, a `net` module's requests unseen (see DRIFT.md E17-E19, E27) |
| `DownloadItem` | `DownloadItem` | CEF | M6 | partial: 20 of 31 applicable items done (64.5%) with `will-download`, `downloadURL` and `createInterruptedDownload`, see parity/download-item.md; `getInitiatorOrigin`, `will-download`'s `frame`, validators and gesture need Oriel's CEF patch 0005 and are blank with stock CEF (E13, E40), two-URL chain (E15) |
| `net` | `Net` | CEF | M6 | partial _(branch `net-module`)_: `request`, `fetch` (and `Session.FetchAsync`, reaching the session's protocol handlers unless `bypassCustomProtocolHandlers`), `isOnline`/`online`, `resolveHost`; cookies follow credentials, no `Set-Cookie`, buffered chunked bodies, no `WebSocket` (DRIFT L1-L11) |
| `ClientRequest` / `IncomingMessage` | `ClientRequest` / `IncomingMessage` | CEF | M6 | partial _(branch `net-module`)_: headers with Electron's rules and errors, write/end, abort, redirect modes with `followRedirect`, `login`, Electron's event order and `net::ERR_*` errors; gaps in DRIFT L1-L7 |
| `WebSocket` (main process) | `WebSocket` | CEF or Patch (verify) | M6 | — |
| `netLog` | `NetLog` | Patch | M6 | partial: `startLogging` (`captureMode`, `maxFileSize`), `stopLogging`, `currentlyLogging`, as `Session.NetLog` and the static `NetLog`, over Oriel's CEF patch 0006; with stock CEF they throw `PlatformNotSupportedException` and only `--log-net-log` records (DRIFT E42) |
| `ServiceWorkers` / `ServiceWorkerMain` | `Session.ServiceWorkers` / `ServiceWorkerMain` | CEF (DevTools) + Patch | M6 | partial _(branch `service-workers`)_: `getAllRunning`, `getInfoFromVersionID`, `getFromVersionID` (deprecated, warns), `getWorkerFromVersionID`, `startWorkerForScope`, `console-message`, `registration-completed`, `running-status-changed`; `ServiceWorkerMain` `versionId`, `scope`, `scriptURL`, `isDestroyed`, `startTask`; through DevTools on the session's hidden page, so state starts at first use, `renderProcessId` is 0 and console listeners keep workers alive; no `send`/`ipc` (Patch; DRIFT SW1-SW8) |
| `Extensions` / extensions API | `Extensions` | CEF (verify Chrome-style support) | M6 | — |

## Desktop integration

| Electron | Oriel | Source | Wave | Status |
|---|---|---|---|---|
| `dialog` (file dialogs) | `Dialog` | OS (macOS, Windows) + CEF | M2 | partial: macOS matches (NSOpenPanel/NSSavePanel as Electron: every option, sheets, window-less and Sync; bookmarks on any build, G9); Windows as Electron (the shell's `IFileDialog`, every option, async and sync, starting in `app.getPath('downloads')` else `home` with no `defaultPath`; G10); Linux runs CEF's dialog with a window, fewer options and async-only on the UI thread (see DRIFT.md G1–G8) |
| `dialog` (message boxes, error box, certificate trust) | `Dialog` | OS | M5 | partial: Windows message boxes (`ShowMessageBoxAsync`/`Sync` with every option, `Signal`, `ShowErrorBox`) as `message_box_win.cc` builds them, macOS (NSAlert sheets, window-less and Sync boxes, the error box) as `message_box_mac.mm`, and Linux (GtkMessageDialog async and `gtk_dialog_run` Sync boxes, transient for and modal to the window, `Signal`, the error box and its pre-ready stderr form) as `message_box_gtk.cc`, the window disabled through `SetEnabled` (MB4: not noted for ATK); `showCertificateTrustDialog` missing (see DRIFT.md) |
| `Menu` / `MenuItem` (app menu, context menus) | `Menu` / `MenuItem` | OS + CEF | M5 | partial: application and window menus, popups and context menus, roles, accelerators (Electron's key names; macOS key equivalents as Chromium's `GetKeyEquivalentAndModifierMaskFromAccelerator`), `click`'s `KeyboardEvent`, `menu-will-show`/`menu-will-close` from the menus that raise them in Electron on each OS, with `_menuWillShow`'s radio fix-up and Electron's radio groups (DRIFT M8, M9; branch `menu-events`); no icons, sublabels or tooltips |
| `Tray` | `Tray` | OS | M5 | done: Windows (Shell_NotifyIcon as Electron drives it: image, tooltip, balloons, click/double/middle/right/move/enter/leave and balloon events, Views context menu, TaskbarCreated); Linux (Chromium's StatusNotifierItem with Electron's patches: registration with `org.kde.StatusNotifierWatcher`, IconPixmap or GNOME/Pantheon icon files, tooltip, `click` on Activate and SecondaryActivate, the menu over DBusMenu and as a Views menu on ContextMenu; the `GtkStatusIcon` fallback with its GTK menu; DRIFT T5); macOS (Electron's `StatusItemView` over an `NSStatusItem`: image, pressed and template images, title with ANSI colours and font type (DRIFT T10, T11), tooltip, autosave name from the guid, `ignoreDoubleClickEvents`, click/double/right/mouse-down/up/enter/leave/move, drag and drop events, the context menu as an NSMenu through `ElectronMenuController`'s port, `popUpContextMenu`, `closeContextMenu`, `getBounds`); `getGUID` (branches `tray`, `linux-tray`, `mac-power-tray`) |
| `Notification` | `Notification` | OS | M5 | partial: Windows toasts through WinRT with Electron's toast XML (`id`, `groupId`/`groupTitle`, title, body, icon (path or `NativeImage`), silent, `hasReply`/`replyPlaceholder`, button and selection actions, `urgency`, `timeoutType`, `toastXml`; `show`, `click`, `action`, `reply`, `close`, `failed`), `Notification.handleActivation`, the toast activator (Start-menu shortcut, HKCU CLSID, COM server), `OrielApp.SetAppUserModelId`/`SetToastActivatorClsid`; Linux as Electron's libnotify backend, over D-Bus (title, body, icon as `image-data`, `urgency`, `timeoutType`, the server's "default" action as `click`, `show`, `close`, `failed`; no actions or reply, as Electron; DRIFT Q5-Q11); no macOS (DRIFT Q2-Q4) |
| `globalShortcut` | `GlobalShortcut` | OS | M5 | partial: Windows (`RegisterHotKey` with Chromium's modifiers and key repeat; DRIFT J2 for Super) and Linux X11 (`XGrabKey` on the root window with every Num/Caps/Scroll Lock variant, Chromium's keysym table, key repeat; DRIFT J3 for Super, J4 for the Wayland portal); accelerator names as Electron's `KeyboardCodeFromStr` and `StringToAccelerator` (numpad and lock keys, shifted characters add Shift), `registerAll` rolls back on failure, `setSuspended`/`isSuspended` release and retake the OS registrations, `AltGr` is its own modifier (J5); macOS as Chromium's `GlobalAcceleratorListenerMac` (Carbon hot keys with layout-aware key codes, bare media keys through an event tap for trusted accessibility clients), WindowTest checks pass (DRIFT J1, J6-J7; branches `power-shortcuts`, `linux-power-shortcuts`, `mac-shell-shortcut`) |
| `Dock` (`app.dock`, macOS) | `OrielApp.Dock` | OS | M5 | done: `setBadge`, `getBadge`, `bounce`, `cancelBounce`, `downloadFinished`, `hide`, `show` (as `ShowAsync`), `isVisible`, `setMenu`, `getMenu`, `setIcon` |
| `clipboard` / `ClipboardItem` | `Clipboard` / `ClipboardItem` | OS | M5 | partial: Windows, macOS and Linux on X11, Electron 44's W3C-style API (`read`/`write` with `ClipboardItem`, `readText`, `writeText`, `has`, `clear`; text, HTML, RTF, images, bookmark, files, raw `osclipboard` and `web` custom formats, Chromium's type list; on macOS the bookmark title and `electron application/findtext`); `selection` (PRIMARY) on Linux as `Clipboard.Selection`; no Wayland data device or file portal (DRIFT K1, K5-K12, branches `shell-clipboard`, `mac-clipboard`, `linux-clipboard`) |
| `shell` | `Shell` | OS | M5 | partial: Windows, as Electron 44 does it (openExternal with `logUsage`, openPath, showItemInFolder, trashItem, beep, shortcut links); Linux as Electron 44's `platform_util_linux.cc` (`xdg-open`/`xdg-email`, portal `OpenDirectory` or `FileManager1.ShowItems` with the `xdg-open` fallback, `gio trash` or the KDE tool, GDK beep; branch `linux-shell`); macOS as Electron 44's `platform_util_mac.mm` (NSWorkspace with `activate`, NSFileManager trash, `NSBeep`), WindowTest checks pass (DRIFT H1, H3-H9, branches `shell-clipboard`, `mac-shell-shortcut`) |
| `nativeImage` | `NativeImage` | CEF + .NET | M5 | partial: PNG/JPEG through CEF, raw bitmaps, data URLs, `@Nx` files, crop, resize, representations; window icon, overlay and thumbar buttons take it; `createThumbnailFromPath` (macOS, Windows), template images in the tray and Dock; gaps in DRIFT B1, B3-B5 (no `createFromNamedImage`, `createMenuSymbol`, `getNativeHandle`; menu item icons); resize uses Skia's Hamming-1/Lanczos-3 filters and `.ico` reads on Windows |
| `nativeTheme` | `NativeTheme` | OS + CEF | M5 | partial: `themeSource` (pages, Chromium's UI, macOS appearance, Windows title bars through Chromium), `shouldUseDarkColors`, `shouldUseHighContrastColors`, `shouldUseInvertedColorScheme`, `inForcedColorsMode`, `prefersReducedTransparency`, `updated` (at once on macOS, Windows and Linux; DRIFT Y1-Y3) |
| `powerMonitor` | `PowerMonitor` | OS | M5 | done: Windows (suspend, resume, on-ac, on-battery, lock and unlock, speed-limit-change, idle time and state, battery) and Linux as Electron (logind suspend, resume and shutdown with its delay locks, X screen saver idle time, the D-Bus screen saver's lock state, never on battery); macOS as Chromium and Electron (IOKit suspend and resume, `IOPMPowerSource` on-ac and on-battery, thermal state and `thermal-state-change`, the CPU speed limit, lock and unlock and `user-did-become-active`/`user-did-resign-active` from `MacLockMonitor`, `shutdown` on `NSWorkspaceWillPowerOffNotification` with its `terminate:` hold, `CGEventSource` idle time, the screen saver and lock as `locked`; DRIFT U1, U4); `getCurrentThermalState` (branches `power-shortcuts`, `linux-power-shortcuts`, `mac-power-tray`) |
| `powerSaveBlocker` | `PowerSaveBlocker` | OS | M5 | done: Windows (one process-wide power request at the highest active type, as Chromium and Electron) and Linux (Chromium's D-Bus inhibit, GNOME session manager first, then freedesktop power management or screen saver, plus the X screen saver suspension); macOS (an IOKit assertion, `NoIdleSleepAssertion` or `NoDisplaySleepAssertion`, taken and released on a power thread, the new one before the old; DRIFT U3) (branches `power-shortcuts`, `linux-power-shortcuts`, `mac-power-tray`) |
| `safeStorage` | `SafeStorage` | OS | M5 | partial: all methods (`isEncryptionAvailable`, `encryptString`, `decryptString`, `isAsyncEncryptionAvailable`, `encryptStringAsync`, `decryptStringAsync`, `setUsePlainTextEncryption`, `getSelectedStorageBackend`) in Chromium os_crypt's formats: Windows `v10` AES-256-GCM under CEF's `Local State` key; macOS `v10` AES-128-CBC under the "`{name} Safe Storage`" Keychain item; Linux `v11` from the Secret Service (libsecret) or `v10` `basic_text`; no KWallet or Secret portal (DRIFT D1-D5) |
| `systemPreferences` | `SystemPreferences` | OS | M5 | done: Windows `getAccentColor`, `getColor` (all 30 Windows names), `getMediaAccessStatus`, `getAnimationSettings`, `accent-color-changed`, `color-changed`; macOS `getAccentColor`, `getColor` (all 32 macOS names), `getSystemColor`, `getMediaAccessStatus`, `askForMediaAccess`, `getAnimationSettings`, `getEffectiveAppearance`/`effectiveAppearance`, `isTrustedAccessibilityClient`, `canPromptTouchID`/`promptTouchID`, user defaults (`get`/`set`/`removeUserDefault`, `registerDefaults`), notifications (`post*`/`subscribe*`/`unsubscribe*` on the distributed, local and workspace centres), `isSwipeTrackingFromScrollEventsEnabled`, the deprecated `accessibilityDisplayShouldReduceTransparency`; Linux `getAccentColor` (the portal's accent), `accent-color-changed`, `getAnimationSettings` (GTK), and no more, as Electron (DRIFT SP2-SP7; branch `system-preferences`: the macOS GUI and Linux WindowTest checks are written, not run) |
| `desktopCapturer` | `DesktopCapturer` | OS | M5 | partial: `getSources` (`types`, `thumbnailSize`, `fetchWindowIcons`, the running-request sharing) with Chromium's `DesktopMediaID` ids, screen names, per-OS window filters, letterboxed thumbnails, `appIcon` and Electron's `display_id`, on macOS (ScreenCaptureKit from 14.4, CoreGraphics before; the screen-recording request and the style-mask restore), Linux X11 and Windows; ids work as `chromeMediaSourceId` and convert to a display media handler's video; no PipeWire (DRIFT DC1-DC5); the GUI checks are written but not yet run (branch `desktop-capturer`) |

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
