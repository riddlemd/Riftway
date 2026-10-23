# macOS todo

General items: [todo.md](todo.md).

- [x] `CrashReporter` (branch `crash-reporter-mac-linux`): CrashTest passes (renderer and browser crashes, uploads,
      local-only, `setUploadToServer(false)`, rate limit, `getUploadedReports` from the database's xattrs)
- [ ] Browser crashes on threads other than the UI thread hang under .NET's breakpoint hook (DRIFT CR16): decide on
      `PAL_MachExceptionMode=2` in the bundle's `LSEnvironment`, or ask .NET to forward foreign breakpoints
- [ ] **Teardown watchdog**: CEF 154 apps often exit with code 2 after "Teardown watchdog expired" (~10 s);
      stock cefclient does the same. Cause unknown (the network-helper hypothesis was refuted).
      Not reproduced 2026-10-22 (4469106 + branch `blazor-hybrid`, macOS 26 arm64): 5 `app.Quit()` runs (BlazorSample
      `--self-check`) and 7 Cmd+Q or close-the-last-window runs (Oriel.Sample, example.com and a data URL) all exit 0
      within 1 s. Keep open until a WindowTest run shows it again or a longer soak passes.
- [ ] osx-x64: the `objc_msgSend_stret` path in `MacWindow` is untested
- [x] Sandbox (branch `blazor-hybrid`): `Oriel.Subprocess.Native` builds for macOS (`build.sh <out> macosarm64`); it calls
      `cef_sandbox_initialize` from the framework's `libcef_sandbox.dylib`, then opens the framework and resolves its CEF
      calls. `EnableSandbox()` works on macOS with it: BlazorSample `--self-check --enable-sandbox` sees every renderer
      sandboxed (`GetAppMetrics`), from a dev bundle and an ad-hoc-signed publish (2026-10-22). The runtime package and
      the repo's dev bundles use it; README's `bundle-macos.sh` route and the other samples' bundles still take NativeAOT
      `Oriel.Subprocess`, which cannot sandbox
- [ ] Signing and notarisation (M4, branch `blazor-hybrid`): `OrielMacSignIdentity` signs the bundle inside out with the
      hardened runtime and entitlements; ad hoc (`-`) passes `codesign --verify --strict --deep` and BlazorSample's
      self-check (2026-10-22). `OrielMacNotaryProfile` notarises and staples: written, not run (no Developer ID on Ise).
      Team-signed apps drop `--use-mock-keychain` (ad hoc keeps it: checked on the renderer's command line); check a
      Developer ID build against the real Keychain (DRIFT P3)
- [x] Run the shell and globalShortcut checks (WindowTest `MacShellTest`, `MacGlobalShortcutTest`; DRIFT H1, J1): pass on
      macOS 26. Posted F-key presses need `kCGEventFlagMaskSecondaryFn`; posted media keys reach no event tap
- [ ] globalShortcut: record what Carbon does with key code -1 (F21-F24, the volume keys), press a hardware media key
      through the event tap, and check whether CEF's media session also takes media keys (J1, J6)
- [x] Run the menu-event checks (WindowTest `MenuEventsAsync`: popup, application menu through its NSMenu delegates,
      `refreshMenuTree` through `menuNeedsUpdate:` and `menuDidClose:`): pass
- [x] Run the `SetParentWindow` checks (WindowTest, after "child moves with its parent"; the modal throw check): pass
- [ ] Check whether hide and show re-attach a window created with `Parent` to that parent after `SetParentWindow` (DRIFT W8)
- [x] Run the powerMonitor/powerSaveBlocker checks (WindowTest `PowerAndShortcutsAsync` → `MacPowerTest`: `pmset -g assertions`
      read-back, idle time after a posted CGEvent, lock and session notifications, the held shutdown) and the Tray checks
      (WindowTest `TrayAsync` → `MacTrayTest`: NSStatusItem, title runs and font, NSEvent-driven events, drops, menus). The
      CGEvent post and the System Events read-back need Accessibility for the test process. Pass
- [ ] Run the systemPreferences checks (WindowTest `SystemPreferencesChecks.RunMac`: colours and appearance under each
      `ThemeSource`, swipe, animation and transparency settings against `defaults`, Touch ID availability against `ioreg`,
      `askForMediaAccess` on a decided consent, distributed and local notifications); branch `system-preferences`
- [ ] systemPreferences by hand: `promptTouchID` on a Touch ID Mac (success, cancel), `askForMediaAccess` on an undecided
      consent (needs `NSCameraUsageDescription`/`NSMicrophoneUsageDescription` in the app's Info.plist),
      `isTrustedAccessibilityClient(true)` showing the prompt
- [ ] Check on a MacBook that unplugging raises `OnBattery` and `IsOnBatteryPower()` flips (IOPMPowerSource interest
      notifications); and a real sleep and wake raises `Suspended`/`Resumed` once each
- [ ] Run WindowTest `NativeImageAsync`'s macOS checks (branch `image-screen`): `createThumbnailFromPath` of a text file
      at 128x96 with 1x and 2x representations, its missing-file rejection, `*Template.png` and the Dock icon's template flag
- [ ] `desktopCapturer` (branch `desktop-capturer`): run WindowTest `DesktopCapturerChecks` with Screen Recording consent
      (ScreenCaptureKit list and `SCScreenshotManager` thumbnails, own windows as `window:N:N`, `NSRunningApplication`
      icons) and without it (`Failed to get sources.`); check a non-resizable window keeps its style mask after a
      capture. The unit run enumerates displays and CoreGraphics windows only. The pre-14.4 CoreGraphics path is untested
- [ ] `app` audit (branch `app-audit`, [parity/app.md](parity/app.md)): run WindowTest `MacAppEventsTest` (open-url,
      open-file and activate from dispatched Apple events, did-resign/become-active, activation policy, secure keyboard
      entry, about panel) and SessionTest `AppAuditChecks` (launch order, single-instance lock, paths, locale, GPU status,
      accessibility, host resolver, app render-process-gone, protocol info, delegate class). Not run: login items and
      protocol registration writes (need a signed, bundled app); Handoff and `moveToApplicationsFolder` (A33) are
      ported on branch `mac-integration`, not yet run
