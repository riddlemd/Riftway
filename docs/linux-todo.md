# Linux todo

General items: [todo.md](todo.md).

- [ ] **libyuv SIGILL on SME-without-SVE arm64** (Linux VMs on Apple M4+): the renderer crashes on the first
      software-converted video frame.
  - [x] Workaround: `Oriel.Subprocess.Native` overrides `getauxval` and clears HWCAP2 SME when SVE is absent
        (branch `sme-override`; ProtocolTest green plain and sandboxed without any preload)
  - [ ] Check whether Chromium/libyuv already has a bug or fix; file one if not, then drop the override
  - [ ] Build-check `Oriel.Subprocess.Native` on linux-x64 (`--export-dynamic-symbol` link flag)
- [ ] Linux sandbox on Ubuntu 24.04+: AppArmor profile, or documented `--no-sandbox` (M4)
- [ ] Wayland: CEF Views on Wayland is still being fixed upstream; Oriel targets X11/XWayland for now
- [ ] linux-x64: only linux-arm64 (Docker on Apple Silicon) has been tested
- [ ] Modal windows (`Modal = true`) don't block the parent under openbox: the parent still takes focus. Electron
      disables the parent's input itself on Linux; CEF exposes no input filter, so this needs another route.
      Check GNOME/KDE, which may honour `_NET_WM_STATE_MODAL`.
- [ ] Window-manager behaviour beyond openbox (GNOME/Mutter, KDE/KWin): maximize, minimize, full screen
- [ ] Screen capture: Chromium's X11 desktop capturer dies with SIGILL under the Docker Xvfb (arm64) when getDisplayMedia starts; SessionTest skips the capture checks there (ORIEL_XVFB). Verify on a real X server; if it crashes there too, find the CHECK with CEF's symbols
