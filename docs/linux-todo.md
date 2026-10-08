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
- [ ] Modal windows (`Modal = true`) don't block the parent's input (DRIFT W5). Electron disables it itself on Linux;
      CEF exposes no input filter, so this needs another route. No window manager helps: under openbox, Mutter,
      KWin and Xfwm4 the parent takes focus and clicks, in Electron too, because `X11Window::Map` rewrites
      `_NET_WM_STATE` and drops the modal hint.
- [x] Window-manager behaviour beyond openbox: WindowTest passes under Mutter 46 (`--x11`), KWin 5.27 (`kwin_x11`) and
      Xfwm4 4.18 as under openbox, including maximize, minimize, full screen, always on top, focus and workspaces.
      `ORIEL_WM=mutter|kwin|xfwm4 eng/docker/test-linux.sh` with the `oriel-linux-dev-wm` image
      (`eng/docker/linux-dev-wm.Dockerfile`). Not covered: GNOME Shell and Plasma themselves (panels, struts, portals)
- [ ] Screen capture: Chromium's X11 desktop capturer dies with SIGILL under the Docker Xvfb (arm64) when getDisplayMedia starts; SessionTest skips the capture checks there (ORIEL_XVFB). Verify on a real X server; if it crashes there too, find the CHECK with CEF's symbols
- [x] `Shell` (branch `linux-shell`): Electron's `platform_util_linux.cc`, checked by `samples/Oriel.ShellTest` against fake
      `xdg-open`/`xdg-email`/`gio` scripts, a fake portal `OpenDirectory` and a fake `FileManager1`, and the X bell
  - [x] A real `gio trash` landing in `~/.local/share/Trash`: the image has `libglib2.0-bin` (rebuild `oriel-linux-dev`)
  - [ ] Against a real desktop: GNOME's portal and Nautilus, KDE's `kioclient5`/`kioclient`, Dolphin's `FileManager1`
- [ ] nativeTheme against a real `xdg-desktop-portal` (GNOME, KDE): Docker has none, so the checks use a fake portal
      (`samples/Shared/FakePortal.cs`) that WindowTest starts before CEF
- [ ] nativeTheme: with no portal preference, pages keep GTK's colours from startup (DRIFT Y2); a CEF patch to clear
      Chromium's GTK style colour cache on theme notifications would let them follow
