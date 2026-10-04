# Todo

Cross-platform work. Platform-specific items: [linux-todo.md](linux-todo.md), [windows-todo.md](windows-todo.md),
[mac-todo.md](mac-todo.md). Milestones are in [PLAN.md](PLAN.md); Electron coverage in
[ELECTRON-PARITY.md](ELECTRON-PARITY.md); every difference from Electron, with its reason, in [DRIFT.md](DRIFT.md).

## M2 core

- [ ] BrowserWindow parity, in the order of [parity/browser-window.md](parity/browser-window.md#next-by-importance):
  - [ ] Window menus (`setMenu`, `removeMenu`, `autoHideMenuBar`), with the `Menu` module
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
- [x] File `Dialog` (branch `dialog`) and `Screen` (branch `screen`); gaps in [DRIFT.md](DRIFT.md)
- [ ] Remaining M2 modules: `BaseWindow`, `View`, `WebContentsView`, `WebFrameMain`, `NavigationHistory`,
      `Debugger`, core `Session`, command line
- [x] `window.open` handling: `SetWindowOpenHandler`, `DidCreateWindow` (branch `window-open`)
- [ ] Menus: Alt+letter mnemonics and the Alt toggle never fire (DRIFT W19): handle Alt in a `cef_keyboard_handler_t`
- [ ] macOS: `Width`/`Height` give a window a title bar taller than asked (DRIFT W18)
- [ ] `OrielApp.Quit()` does nothing off the UI thread (DRIFT A4)
- [ ] `Protocol`: `AllowServiceWorkers` and `CodeCache` privileges throw `NotSupportedException`

## Tests

- [ ] Flaky WindowTest checks: macOS native full screen, minimize and modal focus fail on some runs while the
      Mac is in use (also on unchanged `main`); Linux "simple full screen restores the bounds" was off by a few pixels
      once under openbox. Make them robust (wait for the transition, retry activation) or mark them as environment-bound.

## Runtime and codecs

- [ ] **Deferred (2026-10-03, ~5 h rebuild):** CEF codec build (`~/Developer/_scratch/cef-build`, branch 8037) built, but is not a drop-in: `is_official_build=false`
      leaves DCHECKs on in Release, and the framework (and its own cefclient) dies at startup on a PartitionAlloc
      DCHECK (`thread_cache_internal.h:162`). Rebuild with `is_official_build=true` (as stock), or at least
      `dcheck_always_on=false`, then re-run the drop-in probe (7 H.264/AAC checks, all failing on stock as expected).
      API headers are byte-identical to stock; dependencies match. Ask before deleting the ~150 GB tree.

## Later milestones

- [ ] M3 Blazor Hybrid · M4 ship it · M5 desktop integration · M6 session/network/processes · M7 own CEF builds
      (see [PLAN.md](PLAN.md#milestones))
