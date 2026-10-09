# Todo

Cross-platform work. Platform-specific items: [linux-todo.md](linux-todo.md), [windows-todo.md](windows-todo.md),
[mac-todo.md](mac-todo.md). Milestones are in [PLAN.md](PLAN.md); Electron coverage in
[ELECTRON-PARITY.md](ELECTRON-PARITY.md); every difference from Electron, with its reason, in [DRIFT.md](DRIFT.md).

## M2 core

- [ ] BrowserWindow parity, in the order of [parity/browser-window.md](parity/browser-window.md#next-by-importance):
  - [x] Window menus (`setMenu`, `removeMenu`, `autoHideMenuBar`), with the `Menu` module
  - [x] `Menu.popup` (context menus), webContents `context-menu`, mouse `sendInputEvent`
  - [x] `Menu` events `menu-will-show`/`menu-will-close` with `_menuWillShow`'s radio fix-up (branch `menu-events`;
        WindowTest passes on Linux, its macOS and Windows checks are unrun)
  - [x] `webPreferences` batch 1 (devTools, zoomFactor, javascript, images, textarea, webgl, fonts, disableDialogs)
  - [ ] Remaining `webPreferences` (see the parity doc: process-wide switches, spellcheck, ...; session/partition done)
  - [x] `parent`/`modal`, `isModal`, `getChildWindows`, `getParentWindow`
  - [x] `setParentWindow` on Windows (branch `title-bar-2`)
  - [x] `setParentWindow` on Linux (branch `linux-parent`)
  - [x] `setParentWindow` on macOS (branch `mac-menu-parent`; WindowTest checks unrun, DRIFT W8)
  - [x] Windows: `titleBarStyle`, `titleBarOverlay`/`setTitleBarOverlay`, `app-region` drag regions (branch `title-bar`)
  - [ ] Title bar on macOS (`trafficLightPosition`, hidden styles) and the page's Window Controls Overlay API (DRIFT W34, W37)
  - [x] Taskbar on Windows: `setProgressBar`, `flashFrame`, `setSkipTaskbar`/`skipTaskbar`, `setOverlayIcon`
  - [x] Taskbar on macOS (Dock progress and bounce) and Linux (urgency hint, skip taskbar)
  - [x] `setThumbarButtons`, `setIcon`/`setOverlayIcon` with `NativeImage`
  - [x] Linux taskbar progress and badge count (Unity launcher entry on the session bus), `app.setDesktopName`
  - [x] Windows: `setAppDetails`, `setThumbnailClip`, `setThumbnailToolTip`, `backgroundMaterial`/`setBackgroundMaterial`
  - [x] Make `backgroundMaterial` visible; `transparent` on Windows
  - [x] `transparent` on macOS
  - [ ] `transparent` on Linux (DRIFT W24): needs the CEF patch in [eng/cef](../eng/cef/README.md); written, to build and
        run ([linux-todo.md](linux-todo.md))
  - [x] Windows: `hookWindowMessage` family, `thickFrame`, `roundedCorners`, `accentColor`/`setAccentColor`/`getAccentColor` (branch `win-shell-apis`)
  - [x] Windows: `showInactive`, `opacity`, `hasShadow` (stored, as Electron), `setIgnoreMouseEvents` (with `forward`), content protection, kiosk, `getNormalBounds`, `will-resize`/`will-move` (branch `window-polish`)
  - [x] `loadURL` options (userAgent, extraHeaders, httpReferrer, postData); `baseURLForDataURL` throws
  - [ ] Window state persistence (`persisted-state-restored`)
  - [ ] `resized`/`moved` events
  - [ ] Accuracy gaps: `Shown`/`Hidden` fire only from `Show()`/`Hide()`
- [x] webContents: `executeJavaScript`, DevTools open/close/toggle, zoom factor/level, `setUserAgent`/`getUserAgent`
- [x] File `Dialog` and `Screen`; gaps in [DRIFT.md](DRIFT.md)
- [x] `NavigationHistory` (`WebContents.NavigationHistory`)
- [x] `WebFrameMain` (`WebContents.MainFrame`, IPC `SenderFrame`)
- [x] Core `Session` (`Session`, `Cookies`, partitions; gaps DRIFT E1-E11) and `NativeImage` (gaps DRIFT B1, B3-B5)
- [x] `Debugger` (`WebContents.Debugger`; shares Oriel's protocol session, DRIFT C43)
- [ ] `View` and `WebContentsView` ([design](design/web-contents-view.md)): phase 1 done (`ContentView`, child views,
      bounds, visibility, `bounds-changed`, popups and `close()` from view pages); next `BaseWindow` (DRIFT V1, V2, V4, V7, V9, V12, V13)
- [x] Windows: closing a view's page tears it down in a hidden, never-shown window; check it takes no focus there (WindowTest: no `EVENT_SYSTEM_FOREGROUND`, the window keeps the foreground)
- [ ] macOS: check whether a hidden window or a `LoadUrl` takes focus (it did on Windows; fixed on branch `window-polish`)
- [ ] Remaining M2 modules: `BaseWindow`; the remaining Electron command-line switches and `ELECTRON_*` variables,
      checked one by one
- [x] `app.commandLine` (`OrielApp.CommandLine`; DRIFT A19)
- [x] `window.open` handling: `SetWindowOpenHandler`, `DidCreateWindow` (branch `window-open`)
- [x] Menus: Alt+letter mnemonics and the Alt toggle (was DRIFT W19): handled in a `cef_keyboard_handler_t`; WindowTest covers both
- [x] macOS: `Width`/`Height` gave a window a title bar taller than asked (was DRIFT W18)
- [x] `OrielApp.Quit()` from any thread (was DRIFT A4)
- [ ] `Protocol`: `AllowServiceWorkers` and `CodeCache` privileges throw `NotSupportedException`


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

- [x] `Shell` and `Clipboard` on Windows (branch `shell-clipboard`); `Shell` on Linux (branch `linux-shell`); `Clipboard` on
      macOS (branch `mac-clipboard`) and Linux X11 with `selection` (branch `linux-clipboard`); the macOS `Shell` (branch `mac-shell-shortcut`, unrun: DRIFT H1)
- [x] `GlobalShortcut`, `PowerMonitor`, `PowerSaveBlocker` on Windows (branch `power-shortcuts`) and Linux (branch `linux-power-shortcuts`); `PowerMonitor` and `PowerSaveBlocker` on macOS (branch `mac-power-tray`); macOS `GlobalShortcut` (branch `mac-shell-shortcut`, unrun: DRIFT J1)
- [x] `Tray` on Windows (branch `tray`), Linux (branch `linux-tray`) and macOS (branch `mac-power-tray`)
- [ ] M3 Blazor Hybrid · M4 ship it · M5 desktop integration · M6 session/network/processes · M7 own CEF builds
      (see [PLAN.md](PLAN.md#milestones))
- [ ] M7: CEF patches in [eng/cef](../eng/cef/README.md): 0001 `CefWindowDelegate::IsTranslucent` (W24), 0002
      `CefWindow::SetEventListeningEnabled` (W5), 0003 GTK style colour cache, a Chromium patch (Y2), 0004
      `CefWindow::SetXdgActivationToken` (linux-tray's T5). Oriel detects a patched libcef by its next-version API
      hash (`CefRuntime.IsPatched`) and runs unchanged on stock CEF. None is built yet: build, check the hashes, run
      WindowTest on Linux ([linux-todo.md](linux-todo.md))
