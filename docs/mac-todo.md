# macOS todo

General items: [todo.md](todo.md).

- [ ] **Teardown watchdog**: CEF 154 apps often exit with code 2 after "Teardown watchdog expired" (~10 s);
      stock cefclient does the same. Cause unknown (the network-helper hypothesis was refuted).
- [ ] osx-x64: the `objc_msgSend_stret` path in `MacWindow` is untested
- [ ] Sandbox: move the helper from NativeAOT `Oriel.Subprocess` to the native one and link CEF's sandbox library
- [ ] Signing and notarisation; drop `--use-mock-keychain` for signed builds (M4)
- [ ] Run the menu-event checks (WindowTest `MenuEventsAsync`: popup, application menu through its NSMenu delegates,
      `refreshMenuTree` through `menuNeedsUpdate:` and `menuDidClose:`)
- [ ] Run the `SetParentWindow` checks (WindowTest, after "child moves with its parent"; the modal throw check), and
      check whether hide and show re-attach a window created with `Parent` to that parent after `SetParentWindow` (DRIFT W8)
