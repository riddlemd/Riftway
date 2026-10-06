# Todo

Cross-platform work. Platform-specific items: [linux-todo.md](linux-todo.md), [windows-todo.md](windows-todo.md),
[mac-todo.md](mac-todo.md). Milestones are in [PLAN.md](PLAN.md); Electron coverage in
[ELECTRON-PARITY.md](ELECTRON-PARITY.md); every difference from Electron, with its reason, in [DRIFT.md](DRIFT.md).

## M2 core

- [ ] BrowserWindow parity, in the order of [parity/browser-window.md](parity/browser-window.md#next-by-importance):
  - [x] Window menus (`setMenu`, `removeMenu`, `autoHideMenuBar`), with the `Menu` module
  - [x] `Menu.popup` (context menus), webContents `context-menu`, mouse `sendInputEvent`
  - [x] `webPreferences` batch 1 (devTools, zoomFactor, javascript, images, textarea, webgl, fonts, disableDialogs)
  - [ ] Remaining `webPreferences` (see the parity doc: process-wide switches, spellcheck, ...; session/partition done)
  - [x] `parent`/`modal`, `isModal`, `getChildWindows`, `getParentWindow`
  - [ ] `setParentWindow` (needs OS code: CEF sets the parent only at creation)
  - [ ] `titleBarStyle`, `titleBarOverlay`, `trafficLightPosition`
  - [x] Taskbar on Windows: `setProgressBar`, `flashFrame`, `setSkipTaskbar`/`skipTaskbar`, `setOverlayIcon`
  - [x] Taskbar on macOS (Dock progress and bounce) and Linux (urgency hint, skip taskbar)
  - [x] `setThumbarButtons`, `setIcon`/`setOverlayIcon` with `NativeImage`
  - [ ] Linux taskbar progress (desktop-specific)
  - [x] Windows: `setAppDetails`, `setThumbnailClip`, `setThumbnailToolTip`, `backgroundMaterial`/`setBackgroundMaterial`
  - [x] Make `backgroundMaterial` visible; `transparent` on Windows
  - [ ] `transparent` on macOS and Linux (DRIFT W24)
  - [ ] `showInactive`
  - [x] `loadURL` options (userAgent, extraHeaders, httpReferrer, postData); `baseURLForDataURL` throws
  - [ ] Window state persistence (`getNormalBounds`, `persisted-state-restored`)
  - [ ] `opacity`, `hasShadow`, `setIgnoreMouseEvents`, content protection, `setKiosk`, `will-*` events
  - [ ] Accuracy gaps: `Shown`/`Hidden` fire only from `Show()`/`Hide()`
- [x] webContents: `executeJavaScript`, DevTools open/close/toggle, zoom factor/level, `setUserAgent`/`getUserAgent`
- [x] File `Dialog` and `Screen`; gaps in [DRIFT.md](DRIFT.md)
- [x] `NavigationHistory` (`WebContents.NavigationHistory`)
- [x] `WebFrameMain` (`WebContents.MainFrame`, IPC `SenderFrame`)
- [x] Core `Session` (`Session`, `Cookies`, partitions; gaps DRIFT E1-E11) and `NativeImage` (gaps DRIFT B1-B5)
- [x] `Debugger` (`WebContents.Debugger`; shares Oriel's protocol session, DRIFT C43)
- [ ] Remaining M2 modules: `BaseWindow`, `View`, `WebContentsView`; the remaining Electron command-line
      switches and `ELECTRON_*` variables, checked one by one
- [x] `app.commandLine` (`OrielApp.CommandLine`; DRIFT A19)
- [x] `window.open` handling: `SetWindowOpenHandler`, `DidCreateWindow` (branch `window-open`)
- [x] Menus: Alt+letter mnemonics and the Alt toggle (was DRIFT W19)
- [x] macOS: `Width`/`Height` gave a window a title bar taller than asked (was DRIFT W18)
- [x] `OrielApp.Quit()` from any thread (was DRIFT A4)
- [ ] `Protocol`: `AllowServiceWorkers` and `CodeCache` privileges throw `NotSupportedException`

- [ ] When merging `web-contents-view`: a `WebContentsView` in a persistent partition must create its browser once `Session.Initialized` completes, as `BrowserWindow` does (CEF refuses a browser view on a profile still loading)

## Tests

- [x] Screen checks at 125% scaling (Windows): Chromium floors when converting, so the checks tolerate one pixel
      (re-run at 125% on the laptop to confirm)

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

- [x] `Shell` and `Clipboard` on Windows (branch `shell-clipboard`); macOS and Linux backends to come (DRIFT H1, K1)
- [ ] M3 Blazor Hybrid · M4 ship it · M5 desktop integration · M6 session/network/processes · M7 own CEF builds
      (see [PLAN.md](PLAN.md#milestones))
