# session parity

Against Electron **v44.5.1** (`docs/api/session.md`, `shell/browser/api/electron_api_session.cc` and
`lib/browser/api/session.ts`). Audited 2026-10-09; update this file as items land.

Status: **done**, **partial** (exists with a documented gap; DRIFT.md id in the note), **missing**, or **n/a**.
Members another checklist or module row owns (`Cookies`, `WebRequest`, `protocol`, `DownloadItem`, `netLog`,
`serviceWorkers`, extensions) are listed for completeness, counted **n/a** here, and their status is in the note.

## Summary

Percentages are over the non-n/a items; the second counts a partial item as half.

| Area | Items | Done | Partial | Missing | n/a | % done | % done + partial/2 |
|---|---|---|---|---|---|---|---|
| Module methods and properties | 3 | 1 | 2 | 0 | 0 | 33.3% | 66.7% |
| Instance events | 23 | 0 | 0 | 19 | 4 | 0.0% | 0.0% |
| Instance methods | 56 | 20 | 14 | 16 | 6 | 40.0% | 54.0% |
| Instance properties | 9 | 3 | 0 | 0 | 6 | 100.0% | 100.0% |
| **Total** | 91 | 24 | 16 | 35 | 16 | 32.0% | 42.7% |

## Checklist

### Module methods and properties

| Electron | Riftway | Status | Note |
|---|---|---|---|
| `session.fromPartition(partition[, options])` | `Session.FromPartition` | partial | Same name, same session; `persist:` on disk, others in memory, `""` the default. Kept in `<data>/Partition <name>` (E1); no `cache` option (E3). |
| `session.fromPath(path[, options])` | `Session.FromPath` | partial | Absolute paths only, as Electron; only directly inside the data directory (E2); no `cache` option (E3). |
| `session.defaultSession` | `Session.DefaultSession` | done | Throws before `Ready`. |

### Instance events

| Electron | Riftway | Status | Note |
|---|---|---|---|
| `will-download` | `WillDownload` | n/a | [download-item.md](download-item.md): partial (E40). |
| `extension-loaded` | — | n/a | Extensions, a separate module (E10). |
| `extension-unloaded` | — | n/a | Extensions (E10). |
| `extension-ready` | — | n/a | Extensions (E10). |
| `file-system-access-restricted` | — | missing | E45. |
| `preconnect` | — | missing | E10. |
| `spellcheck-dictionary-initialized` | — | missing | E22. |
| `spellcheck-dictionary-download-begin` | — | missing | E22. |
| `spellcheck-dictionary-download-success` | — | missing | E22. |
| `spellcheck-dictionary-download-failure` | — | missing | E22. |
| `select-hid-device` | — | missing | Chrome's chooser (E11). |
| `hid-device-added` | — | missing | E11. |
| `hid-device-removed` | — | missing | E11. |
| `hid-device-revoked` | — | missing | E11. |
| `select-serial-port` | — | missing | Chrome's chooser (E11). |
| `serial-port-added` | — | missing | E11. |
| `serial-port-removed` | — | missing | E11. |
| `serial-port-revoked` | — | missing | E11. |
| `select-usb-device` | — | missing | Chrome's chooser (E11). |
| `usb-device-added` | — | missing | E11. |
| `usb-device-removed` | — | missing | E11. |
| `usb-device-revoked` | — | missing | E11. |
| `select-webauthn-account` | — | missing | Chrome's account chooser (E46). |

### Instance methods

| Electron | Riftway | Status | Note |
|---|---|---|---|
| `ses.getCacheSize()` | `GetCacheSizeAsync` | partial | Counts the cache files on disk; throws for an in-memory session (E34). |
| `ses.clearCache()` | `ClearCacheAsync` | done | CEF `clear_http_cache`. |
| `ses.clearStorageData([options])` | `ClearStorageDataAsync` | partial | Every `storages` name, case-insensitive, unknown ones ignored, `origin` or all; through DevTools on a hidden page (E37); the media device id salt stays (E36). |
| `ses.flushStorageData()` | `FlushStorageDataAsync` | partial | Flushes cookies only (E6). |
| `ses.setProxy(config)` | `SetProxyAsync` | done | Through the profile's `proxy` preference, with Electron's mode rules (A35). |
| `ses.resolveHost(host[, options])` | `ResolveHostAsync` | partial | No options; `HostResolutionException` carries the net error (E38). |
| `ses.resolveProxy(url)` | `ResolveProxyAsync` | partial | Riftway's CEF patch 0008 only; stock CEF throws `PlatformNotSupportedException` (A35, E8). |
| `ses.forceReloadProxyConfig()` | — | missing | E8. |
| `ses.setDownloadPath(path)` | `SetDownloadPath` | done | `download.default_directory`, as Electron. |
| `ses.enableNetworkEmulation(options)` | `EnableNetworkEmulation` | partial | Per page through DevTools, open and later pages; page-less requests are not throttled (E43). |
| `ses.preconnect(options)` | — | missing | E10. |
| `ses.closeAllConnections()` | `CloseAllConnectionsAsync` | done | CEF `close_all_connections`. |
| `ses.fetch(input[, init])` | `FetchAsync` | partial | The `net` module's fetch on this session (L1-L11). |
| `ses.disableNetworkEmulation()` | `DisableNetworkEmulation` | done | Clears what `EnableNetworkEmulation` set. |
| `ses.setCertificateVerifyProc(proc)` | — | missing | E24. |
| `ses.setPermissionRequestHandler(handler)` | `SetPermissionRequestHandler` | partial | Details name an origin's frame (E12); grants remembered for the session (E29); fullscreen and pointer lock unasked (E32). |
| `ses.setPermissionCheckHandler(handler)` | `SetPermissionCheckHandler` | partial | `navigator.permissions.query()` only (E28). |
| `ses.setDisplayMediaRequestHandler(handler[, opts])` | `SetDisplayMediaRequestHandler` | partial | Whole desktop whatever source; no `useSystemPicker` (E30). |
| `ses.setDevicePermissionHandler(handler)` | — | missing | E31. |
| `ses.setUSBProtectedClassesHandler(handler)` | — | missing | E31. |
| `ses.setBluetoothPairingHandler(handler)` | — | missing | E31. |
| `ses.clearHostResolverCache()` | — | missing | E8. |
| `ses.allowNTLMCredentialsForDomains(domains)` | `AllowNtlmCredentialsForDomains` | done | Chromium's `auth.server_allowlist`, which reconfigures the network service for every session, as Electron's `ConfigureHttpAuthPrefs`; reset at start-up, since Local State outlives the run. |
| `ses.setUserAgent(userAgent[, acceptLanguages])` | `SetUserAgent` | done | Open pages included; a page's own wins. |
| `ses.isPersistent()` | `IsPersistent` | done |  |
| `ses.getUserAgent()` | `GetUserAgent` | done |  |
| `ses.setSSLConfig(config)` | `SetSslConfig` | partial | `tls1.2`/`tls1.3` only, as Electron; applies to every session (E44). |
| `ses.getBlobData(identifier)` | — | missing | E47. |
| `ses.downloadURL(url[, options])` | `DownloadUrl` | n/a | [download-item.md](download-item.md): partial (E37). |
| `ses.createInterruptedDownload(options)` | — | n/a | [download-item.md](download-item.md): missing (E16). |
| `ses.clearAuthCache()` | `ClearAuthCacheAsync` | done | CEF `clear_http_auth_credentials`. |
| `ses.setPreloads(preloads)` | `SetPreloads` | done | Deprecated, warns once, as Electron. |
| `ses.getPreloads()` | `GetPreloads` | done | Deprecated, warns once. |
| `ses.registerPreloadScript(script)` | `RegisterPreloadScript` | partial | `frame` only; `service-worker` throws (E21). |
| `ses.unregisterPreloadScript(id)` | `UnregisterPreloadScript` | done |  |
| `ses.getPreloadScripts()` | `GetPreloadScripts` | done |  |
| `ses.setCodeCachePath(path)` | — | missing | E8. |
| `ses.clearCodeCaches(options)` | — | missing | E8. |
| `ses.getSharedDictionaryUsageInfo()` | — | missing | E48. |
| `ses.getSharedDictionaryInfo(options)` | — | missing | E48. |
| `ses.clearSharedDictionaryCache()` | — | missing | E48. |
| `ses.clearSharedDictionaryCacheForIsolationKey(options)` | — | missing | E48. |
| `ses.setSpellCheckerEnabled(enable)` | `SetSpellCheckerEnabled` | done | `browser.enable_spellchecking`. |
| `ses.isSpellCheckerEnabled()` | `IsSpellCheckerEnabled` | done |  |
| `ses.setSpellCheckerLanguages(languages)` | `SetSpellCheckerLanguages` | done | Electron's error for an unknown code; nothing on macOS, as Electron. |
| `ses.getSpellCheckerLanguages()` | `GetSpellCheckerLanguages` | done |  |
| `ses.setSpellCheckerDictionaryDownloadURL(url)` | `SetSpellCheckerDictionaryDownloadUrl` | missing | Throws `NotSupportedException` (E22). |
| `ses.listWordsInSpellCheckerDictionary()` | `ListWordsInSpellCheckerDictionaryAsync` | done | Chromium's custom dictionary file plus words added this run. |
| `ses.addWordToSpellCheckerDictionary(word)` | `AddWordToSpellCheckerDictionary` | partial | Needs an open page (E23). |
| `ses.removeWordFromSpellCheckerDictionary(word)` | `RemoveWordFromSpellCheckerDictionary` | missing | Throws `NotSupportedException` (E22). |
| `ses.loadExtension(path[, options])` | — | n/a | Deprecated move to `ses.extensions`; Extensions (E10). |
| `ses.removeExtension(extensionId)` | — | n/a | Extensions (E10). |
| `ses.getExtension(extensionId)` | — | n/a | Extensions (E10). |
| `ses.getAllExtensions()` | — | n/a | Extensions (E10). |
| `ses.getStoragePath()` | `GetStoragePath` | done | Null in memory. |
| `ses.clearData([options])` | `ClearDataAsync` | partial | E35, E37. |

### Instance properties

| Electron | Riftway | Status | Note |
|---|---|---|---|
| `ses.availableSpellCheckerLanguages` | `Session.AvailableSpellCheckerLanguages` | done | Static: Chromium's list is the same for every session. |
| `ses.spellCheckerEnabled` | `SpellCheckerEnabled` | done |  |
| `ses.storagePath` | `StoragePath` | done | Null in memory. |
| `ses.cookies` | `Cookies` | n/a | `Cookies` row in ELECTRON-PARITY.md: partial (E4, E5). |
| `ses.extensions` | — | n/a | Extensions (E10). |
| `ses.serviceWorkers` | `ServiceWorkers` | n/a | `ServiceWorkers` row: partial (SW1-SW8). |
| `ses.webRequest` | `WebRequest` | n/a | `webRequest` row: partial (E17-E19, E27). |
| `ses.protocol` | `Protocol` | n/a | `protocol` row: partial. |
| `ses.netLog` | `NetLog` | n/a | `netLog` row: partial (E42). |
