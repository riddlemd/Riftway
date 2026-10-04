# Todo

Cross-platform work. Platform-specific items: [linux-todo.md](linux-todo.md), [windows-todo.md](windows-todo.md),
[mac-todo.md](mac-todo.md). Milestones are in [PLAN.md](PLAN.md); Electron coverage in
[ELECTRON-PARITY.md](ELECTRON-PARITY.md).

## M2 core

- [ ] BrowserWindow parity, in the order of [parity/browser-window.md](parity/browser-window.md#next-by-importance):
  - [x] Window menus (`setMenu`, `removeMenu`, `autoHideMenuBar`), with the `Menu` module
  - [ ] `Menu.popup` (context menus): `W.show_menu` with the bar's menu-model builder; replace CEF's default page context menu
  - [x] `webPreferences` batch 1 (devTools, zoomFactor, javascript, images, textarea, webgl, fonts, disableDialogs)
  - [ ] Remaining `webPreferences` (see the parity doc: process-wide switches, session/partition, spellcheck, ...)
  - [x] `parent`/`modal`, `isModal`, `getChildWindows`, `getParentWindow`
  - [ ] `setParentWindow` (needs OS code: CEF sets the parent only at creation)
  - [ ] `titleBarStyle`, `titleBarOverlay`, `trafficLightPosition`
  - [ ] `showInactive`, `skipTaskbar`, `flashFrame`, `setProgressBar`
  - [x] `loadURL` options (userAgent, extraHeaders, httpReferrer, postData); `baseURLForDataURL` throws
  - [ ] Window state persistence (`getNormalBounds`, `persisted-state-restored`)
  - [ ] `opacity`, `hasShadow`, `setIgnoreMouseEvents`, content protection, `setKiosk`, `will-*` events
  - [ ] Accuracy gaps: `Shown`/`Hidden` fire only from `Show()`/`Hide()`; `SetIcon` takes a path, not a `NativeImage`
- [x] webContents: `executeJavaScript`, DevTools open/close/toggle, zoom factor/level, `setUserAgent`/`getUserAgent`
- [ ] Remaining M2 modules: `BaseWindow`, `View`, `WebContentsView`, `WebFrameMain`, `NavigationHistory`,
      `Debugger`, core `Session`, file `Dialog`, `Screen`, command line, `window.open` handling
- [ ] `Protocol`: `AllowServiceWorkers` and `CodeCache` privileges throw `NotSupportedException`

## Runtime and codecs

- [ ] **Deferred (2026-10-03, ~5 h rebuild):** CEF codec build (`~/Developer/_scratch/cef-build`, branch 8037) built, but is not a drop-in: `is_official_build=false`
      leaves DCHECKs on in Release, and the framework (and its own cefclient) dies at startup on a PartitionAlloc
      DCHECK (`thread_cache_internal.h:162`). Rebuild with `is_official_build=true` (as stock), or at least
      `dcheck_always_on=false`, then re-run the drop-in probe (7 H.264/AAC checks, all failing on stock as expected).
      API headers are byte-identical to stock; dependencies match. Ask before deleting the ~150 GB tree.

## Docs

- [ ] README status still says pre-alpha M1; M2 IPC, Protocol and BrowserWindow parity batch 1 have landed.
- [ ] PLAN.md risk "macOS full screen blacks out other displays" still reads as unsolved; `SimpleFullScreen` exists now.

## Later milestones

- [ ] M3 Blazor Hybrid · M4 ship it · M5 desktop integration · M6 session/network/processes · M7 own CEF builds
      (see [PLAN.md](PLAN.md#milestones))
