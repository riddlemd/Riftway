# Todo

Cross-platform work. Platform-specific items: [linux-todo.md](linux-todo.md), [windows-todo.md](windows-todo.md),
[mac-todo.md](mac-todo.md). Milestones are in [PLAN.md](PLAN.md); Electron coverage in
[ELECTRON-PARITY.md](ELECTRON-PARITY.md).

## M2 core

- [ ] BrowserWindow parity, in the order of [parity/browser-window.md](parity/browser-window.md#next-by-importance):
  - [ ] Window menus (`setMenu`, `removeMenu`, `autoHideMenuBar`), with the `Menu` module
  - [ ] `webPreferences`: `devTools`, `zoomFactor`, `webSecurity`, fonts, `spellcheck`, `session`/`partition`
  - [ ] `parent`/`modal`, `setParentWindow`, `isModal`, `getChildWindows`
  - [ ] `titleBarStyle`, `titleBarOverlay`, `trafficLightPosition`
  - [ ] `showInactive`, `skipTaskbar`, `flashFrame`, `setProgressBar`
  - [ ] `loadURL` options (userAgent, extraHeaders, httpReferrer, postData)
  - [ ] Window state persistence (`getNormalBounds`, `persisted-state-restored`)
  - [ ] `opacity`, `hasShadow`, `setIgnoreMouseEvents`, content protection, `setKiosk`, `will-*` events
  - [ ] Accuracy gaps: `Shown`/`Hidden` fire only from `Show()`/`Hide()`; `SetIcon` takes a path, not a `NativeImage`
- [ ] Remaining M2 modules: `BaseWindow`, `View`, `WebContentsView`, `WebFrameMain`, `NavigationHistory`,
      `Debugger`, core `Session`, file `Dialog`, `Screen`, command line, `window.open` handling
- [ ] `Protocol`: `AllowServiceWorkers` and `CodeCache` privileges throw `NotSupportedException`

## Runtime and codecs

- [ ] CEF codec build (`~/Developer/_scratch/cef-build`, branch 8037): when done, run the drop-in check (swap the
      codec libcef into the stock app, H.264/AAC play, API hash matches). Then ask before deleting the ~150 GB tree.

## Docs

- [ ] README status still says pre-alpha M1; M2 IPC, Protocol and BrowserWindow parity batch 1 have landed.
- [ ] PLAN.md risk "macOS full screen blacks out other displays" still reads as unsolved; `SimpleFullScreen` exists now.

## Later milestones

- [ ] M3 Blazor Hybrid · M4 ship it · M5 desktop integration · M6 session/network/processes · M7 own CEF builds
      (see [PLAN.md](PLAN.md#milestones))
