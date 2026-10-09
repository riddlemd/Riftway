# app parity

Against Electron **v44.5.1** (`docs/api/app.md`, `shell/browser/api/electron_api_app.cc`, `shell/browser/browser*.cc|mm`,
`shell/browser/mac/electron_application*.mm`, `shell/common/electron_paths.cc` and `lib/browser/api/app.ts`). Audited
2026-10-09; update this file as items land. `app.dock` is one item here; its methods are Electron's `dock.md`.

Status: **done**, **partial** (exists with a documented gap; DRIFT.md id in the note), **missing**, or **n/a**.

## Summary

Percentages are over the non-n/a items; the second counts a partial item as half.

| Area | Items | Done | Partial | Missing | n/a | % done | % done + partial/2 |
|---|---|---|---|---|---|---|---|
| Events | 30 | 16 | 6 | 8 | 0 | 53.3% | 63.3% |
| Methods | 75 | 41 | 19 | 14 | 1 | 55.4% | 68.2% |
| Properties | 10 | 7 | 3 | 0 | 0 | 70.0% | 85.0% |
| **Total** | 115 | 64 | 28 | 22 | 1 | 56.1% | 68.4% |

## Checklist

### Events

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `will-finish-launching` | `WillFinishLaunching` | partial | Windows, Linux: just before `Ready`, as Electron. macOS: from the app delegate's `applicationWillFinishLaunching:`, which CEF reaches only after `Ready` (A23). |
| `ready` | `Ready` | partial | No `launchInfo` (the macOS launch notification's user info) (A23). |
| `window-all-closed` | `WindowAllClosed` | done | Quits when unhandled, as Electron. |
| `before-quit` | `BeforeQuit` | done | Cancellable. |
| `will-quit` | `WillQuit` | done | Cancellable. |
| `quit` | `Exited` | done | Named for the method it would collide with; carries the exit code. |
| `open-file` | `OpenFile` | partial | macOS app delegate's `application:openFile:`; `Cancel` is `preventDefault()`. Files the app is launched with arrive after `Ready` (A23). |
| `open-url` | `OpenUrl` | partial | macOS `kAEGetURL` Apple event handler, as Electron's `registerURLHandler`. Launch URLs arrive after `Ready` (A23). |
| `activate` | `Activate` | done | macOS `applicationShouldHandleReopen:hasVisibleWindows:`, answering the flag as Electron. |
| `did-become-active` | `DidBecomeActive` | done | macOS app delegate. |
| `did-resign-active` | `DidResignActive` | done | macOS app delegate. |
| `continue-activity` | — | missing | Handoff is not ported (A32). |
| `will-continue-activity` | — | missing | A32. |
| `continue-activity-error` | — | missing | A32. |
| `activity-was-continued` | — | missing | A32. |
| `update-activity-state` | — | missing | A32. |
| `new-window-for-tab` | — | missing | No native window tabbing (A32). |
| `browser-window-blur` | `BrowserWindowBlur` | done |  |
| `browser-window-focus` | `BrowserWindowFocus` | done |  |
| `browser-window-created` | `BrowserWindowCreated` | done |  |
| `web-contents-created` | `WebContentsCreated` | done |  |
| `certificate-error` | `CertificateError` | done | After the page's own, as Electron routes it. |
| `select-client-certificate` | `SelectClientCertificate` | done | After the page's own. |
| `login` | `Login` | done | After the page's own. |
| `gpu-info-update` | — | missing | CEF exposes no GpuDataManager observer (A27). |
| `render-process-gone` | `RenderProcessGone` | done | With the page and its details, before the page's own event, as Electron's forwarding listener runs first. |
| `child-process-gone` | — | missing | CEF has no browser-child-process observer (A31). |
| `accessibility-support-changed` | `AccessibilitySupportChanged` | partial | Raised by `SetAccessibilitySupportEnabled`; a screen reader starting or stopping is not seen (A28). |
| `session-created` | `SessionCreated` | done |  |
| `second-instance` | `SecondInstance` | partial | The command line is one string, not `argv`; `AdditionalData` from `RequestSingleInstanceLock` on macOS and Linux only (A29). |

### Methods

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `quit()` | `Quit` | done |  |
| `exit([exitCode])` | `Exit` | done |  |
| `relaunch([options])` | `Relaunch` | partial | Electron's argv rules; started once `Run` returns, not by a relauncher that outlives a crash (A21). |
| `isReady()` | `IsReady` | done | A property. |
| `whenReady()` | `WhenReady` | done | Completes after the `Ready` handlers, as the promise's continuations run. |
| `focus([options])` | `Focus` | done |  |
| `isActive()` | `IsActive` | done | macOS `[NSApp isActive]`; false elsewhere. |
| `hide()` | `Hide` | done | macOS. |
| `isHidden()` | `IsHidden` | done | macOS. |
| `show()` | `Show` | done | macOS. |
| `setAppLogsPath([path])` | `SetAppLogsPath` | done | Made and resolved as `PathService::Override`; macOS default from `CFBundleName`, printed `(null)` when there is none, as Electron's `%@`. |
| `getAppPath()` | `GetAppPath` | done | The entry assembly's directory, Oriel's counterpart of the package directory. |
| `getPath(name)` | `GetPath` | done | Every Electron name, `cache`, `userCache` and `userDesktop` included; `assets` fails on macOS and `recent` off Windows, as Electron; `userData`, `userCache`, `logs`, `crashDumps` and `recent` are made on first use; answers cached until the next `SetPath`, as PathService. A14: `assets` checked on Windows only. |
| `getFileIcon(path[, options])` | `GetFileIcon` | partial | Windows only (A12). |
| `setPath(name, path)` | `SetPath` | done | A missing directory is accepted and not made, as Electron's code and spec (its docs say otherwise). |
| `getVersion()` | `Version` | done | The project's `Version`. |
| `getName()` | `Name` | done |  |
| `setName(name)` | `Name` setter | done | Renames the default user agent; a `userData` already read keeps its name, as PathService's cache does. |
| `setDesktopName(name)` | `SetDesktopName` | done | Linux; A20 for the launcher entry. |
| `getLocale()` | `GetLocale` | partial | Chromium's choice recomputed from CEF's inputs; CEF starts Chromium with `--lang=en-US` (A30). |
| `getLocaleCountryCode()` | `GetLocaleCountryCode` | partial | A15 (Windows), A16 (Linux). |
| `getSystemLocale()` | `GetSystemLocale` | partial | A15, A16. |
| `getPreferredSystemLanguages()` | `GetPreferredSystemLanguages` | partial | A15, A16. |
| `addRecentDocument(path)` | `AddRecentDocument` | done | Windows (A13); macOS `NSDocumentController`; no-op on Linux, as Electron. |
| `clearRecentDocuments()` | `ClearRecentDocuments` | done | Windows, macOS. |
| `getRecentDocuments()` | `GetRecentDocuments` | done | Windows, macOS. |
| `setAsDefaultProtocolClient(protocol[, path, args])` | `SetAsDefaultProtocolClient` | done | Windows registry; macOS LaunchServices with the main bundle's id; Linux GIO with the desktop file (Linux not run yet). |
| `removeAsDefaultProtocolClient(protocol[, path, args])` | `RemoveAsDefaultProtocolClient` | done | Windows; macOS hands the scheme to the first other app, else `None`; false on Linux, as Electron. |
| `isDefaultProtocolClient(protocol[, path, args])` | `IsDefaultProtocolClient` | done | Windows, macOS (bundle id, ignoring case), Linux (desktop file id). |
| `getApplicationNameForProtocol(url)` | `GetApplicationNameForProtocol` | done | Windows, macOS, Linux. |
| `getApplicationInfoForProtocol(url)` | `GetApplicationInfoForProtocol` | done | Windows; macOS (`iconForFile:` at 1x and 2x); Linux (the GTK 3 theme's 32-pixel icon; not run yet). |
| `setUserTasks(tasks)` | `SetUserTasks` | done | Windows. |
| `getJumpListSettings()` | `GetJumpListSettings` | done | Windows. |
| `setJumpList(categories)` | `SetJumpList` | done | Windows. |
| `requestSingleInstanceLock([additionalData])` | `RequestSingleInstanceLock` | partial | Always single-instance (A2); macOS and Linux notify the running instance over Chromium's singleton socket; Windows defers to `Run`, without the data (A29). |
| `hasSingleInstanceLock()` | `HasSingleInstanceLock` | done |  |
| `releaseSingleInstanceLock()` | `ReleaseSingleInstanceLock` | partial | CEF keeps the data directory locked (A2). |
| `setUserActivity(type, userInfo[, webpageURL])` | — | missing | A32. |
| `getCurrentActivityType()` | — | missing | A32. |
| `invalidateCurrentActivity()` | — | missing | A32. |
| `resignCurrentActivity()` | — | missing | A32. |
| `updateCurrentActivity(type, userInfo)` | — | missing | A32. |
| `setAppUserModelId(id)` | `SetAppUserModelId` | done | Windows. |
| `setToastActivatorCLSID(id)` | `SetToastActivatorClsid` | done | Windows. |
| `setActivationPolicy(policy)` | `SetActivationPolicy` | done | macOS. |
| `importCertificate(options, callback)` | — | missing | Linux NSS (A34). |
| `configureHostResolver(options)` | `ConfigureHostResolver` | partial | Through Chromium's DNS preferences (A26). |
| `configureWebAuthn(options)` | — | missing | A36. |
| `disableHardwareAcceleration()` | `DisableHardwareAcceleration` | done | `--disable-gpu`, as Electron 44 appends it. |
| `isHardwareAccelerationEnabled()` | `IsHardwareAccelerationEnabled` | partial | Does not see Chromium's own fallback to software (A24). |
| `disableDomainBlockingFor3DAPIs()` | — | missing | A24. |
| `getAppMetrics()` | `GetAppMetrics` | partial | A18. |
| `getGPUFeatureStatus()` | `GetGpuFeatureStatusAsync` | partial | Asynchronous, from the browser DevTools target (A27). |
| `getGPUInfo(infoType)` | `GetGpuInfoAsync` | partial | From DevTools: fewer device fields, `basic` answers as `complete` (A27). |
| `setBadgeCount([count])` | `SetBadgeCount` | partial | A17 (Windows text), A20 (Linux). |
| `getBadgeCount()` | `GetBadgeCount` | done |  |
| `getLoginItemSettings([options])` | `GetLoginItemSettings` | done | Windows registry; macOS SMAppService with `Status` and `WasOpenedAtLogin`; all off on Linux, as Electron. |
| `setLoginItemSettings(settings)` | `SetLoginItemSettings` | done | Windows; macOS SMAppService (register / unregister); no-op on Linux. |
| `isAccessibilitySupportEnabled()` | `IsAccessibilitySupportEnabled` | partial | Only what `SetAccessibilitySupportEnabled` set (A28). |
| `setAccessibilitySupportEnabled(enabled)` | `SetAccessibilitySupportEnabled` | partial | Per page through CEF; off also clears a screen reader's mode (A28). |
| `getAccessibilitySupportFeatures()` | — | missing | A28. |
| `setAccessibilitySupportFeatures(features)` | — | missing | A28. |
| `showAboutPanel()` | `ShowAboutPanel` | done | macOS standard panel; Linux GtkAboutDialog and Windows information box (not run yet). |
| `setAboutPanelOptions(options)` | `SetAboutPanelOptions` | done |  |
| `isEmojiPanelSupported()` | `IsEmojiPanelSupported` | done | macOS, Windows 10 1803+. |
| `showEmojiPanel()` | `ShowEmojiPanel` | partial | macOS character palette; Windows not wired (A25). |
| `startAccessingSecurityScopedResource(bookmarkData)` | — | n/a | Mac App Store builds only; Oriel has none. |
| `enableSandbox()` | `EnableSandbox` | partial | A1. |
| `isInApplicationsFolder()` | `IsInApplicationsFolder` | done | macOS. |
| `moveToApplicationsFolder([options])` | — | missing | A33. |
| `isSecureKeyboardEntryEnabled()` | `IsSecureKeyboardEntryEnabled` | done | macOS. |
| `setSecureKeyboardEntryEnabled(enabled)` | `SetSecureKeyboardEntryEnabled` | partial | Oriel's switch is separate from Chromium's password-field counter (A37). |
| `setProxy(config)` | — | missing | A35. |
| `resolveProxy(url)` | — | missing | A35. |
| `setClientCertRequestPasswordHandler(handler)` | — | missing | Linux NSS (A34). |

### Properties

| Electron | Oriel | Status | Note |
|---|---|---|---|
| `accessibilitySupportEnabled` | `AccessibilitySupportEnabled` | partial | A28. |
| `applicationMenu` | `ApplicationMenu` | done | `Menu.GetApplicationMenu` / `SetApplicationMenu`. |
| `badgeCount` | `BadgeCount` | done |  |
| `commandLine` | `CommandLine` | partial | A19. |
| `dock` | `Dock` | done | Its methods do nothing elsewhere. |
| `isPackaged` | `IsPackaged` | partial | False only under the `dotnet` host (A22). |
| `toastActivatorCLSID` | `ToastActivatorClsid` | done | Windows. |
| `name` | `Name` | done |  |
| `userAgentFallback` | `UserAgentFallback` | done |  |
| `runningUnderARM64Translation` | `RunningUnderArm64Translation` | done | macOS `sysctl.proc_translated`; Windows `IsWow64Process2` (not run yet). |
