# macOS todo

General items: [todo.md](todo.md).

- [ ] **Teardown watchdog**: CEF 154 apps often exit with code 2 after "Teardown watchdog expired" (~10 s);
      stock cefclient does the same. Cause unknown (the network-helper hypothesis was refuted).
- [ ] osx-x64: the `objc_msgSend_stret` path in `MacWindow` is untested
- [ ] Sandbox: move the helper from NativeAOT `Oriel.Subprocess` to the native one and link CEF's sandbox library
- [ ] Signing and notarisation; drop `--use-mock-keychain` for signed builds (M4)
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
