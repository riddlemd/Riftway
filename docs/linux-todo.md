# Linux todo

General items: [todo.md](todo.md).

- [ ] **libyuv SIGILL on SME-without-SVE arm64** (Linux VMs on Apple M4+): the renderer crashes on the first
      software-converted video frame.
  - [x] Workaround: `Oriel.Subprocess.Native` overrides `getauxval` and clears HWCAP2 SME when SVE is absent
        (branch `sme-override`; ProtocolTest green plain and sandboxed without any preload)
  - [ ] Check whether Chromium/libyuv already has a bug or fix; file one if not, then drop the override
  - [x] `Oriel.Subprocess.Native` builds and links on linux-x64 (clang 18, GNU ld; `--export-dynamic-symbol` is accepted).
        The override is `__aarch64__`-only, so x64 exports no `getauxval` by design.
- [ ] Linux sandbox on Ubuntu 24.04+: AppArmor profile, or documented `--no-sandbox` (M4)
- [ ] Wayland: CEF Views on Wayland is still being fixed upstream; Oriel targets X11/XWayland for now
- [ ] linux-x64: only linux-arm64 is tested end to end (Docker on Apple Silicon). On linux-x64 (amd64 image under QEMU)
      `Oriel.Subprocess.Native` links and `dotnet test tests/Oriel.Tests` passes (1302 passed, 3 skipped).
  - [ ] Run `eng/docker/test-linux.sh` on real x64 hardware: under QEMU `dotnet publish` segfaults (needs
        `DOTNET_EnableWriteXorExecute=0`) and Chromium's GPU process then dies with SIGTRAP, even with `--disable-gpu`
  - `eng/docker/*.sh` pick the CEF platform and RID from `uname -m`
- [ ] Modal windows (`Modal = true`) don't block the parent under openbox: the parent still takes focus. Electron
      disables the parent's input itself on Linux; CEF exposes no input filter, so this needs another route.
      Check GNOME/KDE, which may honour `_NET_WM_STATE_MODAL`.
- [ ] Window-manager behaviour beyond openbox (GNOME/Mutter, KDE/KWin): maximize, minimize, full screen
- [ ] Screen capture: Chromium's X11 desktop capturer dies with SIGILL under the Docker Xvfb (arm64) when getDisplayMedia starts; SessionTest skips the capture checks there (ORIEL_XVFB). Verify on a real X server; if it crashes there too, find the CHECK with CEF's symbols
