# macOS todo

General items: [todo.md](todo.md).

- [ ] **Teardown watchdog**: CEF 154 apps often exit with code 2 after "Teardown watchdog expired" (~10 s);
      stock cefclient does the same. Cause unknown (the network-helper hypothesis was refuted).
- [ ] osx-x64: the `objc_msgSend_stret` path in `MacWindow` is untested
- [ ] Sandbox: move the helper from NativeAOT `Oriel.Subprocess` to the native one and link CEF's sandbox library
- [ ] Signing and notarisation; drop `--use-mock-keychain` for signed builds (M4)
- [ ] Run the shell and globalShortcut checks (WindowTest `MacShellTest`, `MacGlobalShortcutTest`; branch
      `mac-shell-shortcut`, DRIFT H1, J1). They need Automation (Finder) and Accessibility (posting key events, media
      keys) for the terminal; record what Carbon does with key code -1 and whether CEF's media session also takes media keys (J6)
- [ ] Run the menu-event checks (WindowTest `MenuEventsAsync`: popup, application menu through its NSMenu delegates,
      `refreshMenuTree` through `menuNeedsUpdate:` and `menuDidClose:`)
- [ ] Run the `SetParentWindow` checks (WindowTest, after "child moves with its parent"; the modal throw check), and
      check whether hide and show re-attach a window created with `Parent` to that parent after `SetParentWindow` (DRIFT W8)
- [ ] Run the powerMonitor/powerSaveBlocker checks (WindowTest `PowerAndShortcutsAsync` → `MacPowerTest`: `pmset -g assertions`
      read-back, idle time after a posted CGEvent, lock and session notifications, the held shutdown) and the Tray checks
      (WindowTest `TrayAsync` → `MacTrayTest`: NSStatusItem, title runs and font, NSEvent-driven events, drops, menus). The
      CGEvent post and the System Events read-back need Accessibility for the test process
- [ ] Check on a MacBook that unplugging raises `OnBattery` and `IsOnBatteryPower()` flips (IOPMPowerSource interest
      notifications); and a real sleep and wake raises `Suspended`/`Resumed` once each
