# Linux todo

General items: [todo.md](todo.md).

- [ ] **libyuv SIGILL on SME-without-SVE arm64** (Linux VMs on Apple M4+): the renderer crashes on the first
      software-converted video frame.
  - [x] Workaround: `Oriel.Subprocess.Native` overrides `getauxval` and clears HWCAP2 SME when SVE is absent
        (branch `sme-override`; ProtocolTest green plain and sandboxed without any preload)
  - [ ] Check whether Chromium/libyuv already has a bug or fix; file one if not, then drop the override
    - Likely tracked as issuetracker.google.com/issues/558654023 (M5 Pro under OrbStack; sign-in only, read from a search
      summary, fix status unknown). Cause, per pingdotgg/t3code#11731: SME code built for `-march=armv9-a+i8mm+sme`
      (libyuv BUILD.gn) runs a non-streaming SVE `cntd` before `smstart`, which M4+ lacks. Chromium 154 detection is not
      the bug: `cpu_id.cc` enables SME without SVE on purpose. No libyuv fix found; recheck when CEF moves past 154
  - [x] `Oriel.Subprocess.Native` builds and links on linux-x64 (clang 18, GNU ld; `--export-dynamic-symbol` is accepted).
        The override is `__aarch64__`-only, so x64 exports no `getauxval` by design.
- [ ] Linux sandbox on Ubuntu 24.04+: AppArmor profile, or documented `--no-sandbox` (M4)
- [ ] Wayland: CEF Views on Wayland is still being fixed upstream; Oriel targets X11/XWayland for now
- [ ] linux-x64: only linux-arm64 is tested end to end (Docker on Apple Silicon). On linux-x64 (amd64 image under QEMU)
      `Oriel.Subprocess.Native` links and `dotnet test tests/Oriel.Tests` passes (1302 passed, 3 skipped).
  - [ ] Run `eng/docker/test-linux.sh` on real x64 hardware: under QEMU `dotnet publish` segfaults (needs
        `DOTNET_EnableWriteXorExecute=0`) and Chromium's GPU process then dies with SIGTRAP, even with `--disable-gpu`
  - `eng/docker/*.sh` pick the CEF platform and RID from `uname -m`
- [x] Modal windows (`Modal = true`) block the parent under any window manager, as Electron: an input-only X11 cover
      plus dropped keys (branch `linux-modal`); the same route serves `SetEnabled`/`IsEnabled` and message boxes (MB4).
      No window manager would do it: `X11Window::Map` rewrites `_NET_WM_STATE` and drops the modal hint, so without
      the cover the parent's page takes clicks under openbox, Mutter, KWin and Xfwm4. Focus goes to the parent, as Electron
  - [x] Re-run the input-blocking checks under Mutter, KWin and Xfwm4 (`ORIEL_WM`): all pass, view pages included
  - [ ] Crossing events still reach a disabled window's page (DRIFT W5); needs a CEF patch
  - [ ] Check an input method (ibus) composing into a disabled window's focused field
- [x] Window-manager behaviour beyond openbox: WindowTest passes under Mutter 46 (`--x11`), KWin 5.27 (`kwin_x11`) and
      Xfwm4 4.18 as under openbox, including maximize, minimize, full screen, always on top, focus and workspaces.
      `ORIEL_WM=mutter|kwin|xfwm4 eng/docker/test-linux.sh` with the `oriel-linux-dev-wm` image
      (`eng/docker/linux-dev-wm.Dockerfile`). Not covered: GNOME Shell and Plasma themselves (panels, struts, portals)
  - Xfwm4: a window restored maximized by `windowStatePersistence` keeps its saved normal size, but sometimes (1 run
    in 2) not its position: `X11Window::OnWindowMapped` maximizes again and takes the restore bounds from wherever Xfwm4 has placed the window by then;
    Electron shares that code (checked in the source, not run). WindowTest checks only the size under Xfwm4
- [x] `Tray` (branch `linux-tray`): Chromium 154's StatusNotifierItem and DBusMenu with Electron v44.5.1's patches,
      falling back to a `GtkStatusIcon`; WindowTest drives it through a fake watcher and DBusMenu client
      (`samples/Shared/FakeStatusNotifierWatcher.cs`) and through GTK
  - [ ] Check against real hosts: Plasma's system tray, GNOME Shell with the AppIndicator extension (icon files),
        an XEmbed tray for the GTK fallback (Docker has none of them)
- [ ] Screen capture: Chromium's X11 desktop capturer dies with SIGILL under the Docker Xvfb (arm64) when getDisplayMedia starts; SessionTest skips the capture checks there (ORIEL_XVFB). Verify on a real X server; if it crashes there too, find the CHECK with CEF's symbols
- [x] `Shell` (branch `linux-shell`): Electron's `platform_util_linux.cc`, checked by `samples/Oriel.ShellTest` against fake
      `xdg-open`/`xdg-email`/`gio` scripts, a fake portal `OpenDirectory` and a fake `FileManager1`, and the X bell
  - [x] A real `gio trash` landing in `~/.local/share/Trash`: the image has `libglib2.0-bin` (rebuild `oriel-linux-dev`)
  - [ ] Against a real desktop: GNOME's portal and Nautilus, KDE's `kioclient5`/`kioclient`, Dolphin's `FileManager1`
- [x] `GlobalShortcut`, `PowerMonitor`, `PowerSaveBlocker` (branch `linux-power-shortcuts`): X11 key grabs, logind sleep and
      shutdown delay locks, the D-Bus inhibit services and screen saver lock state, read back in WindowTest against fake
      services (`samples/Shared/FakeDBusService.cs`) and a private system bus (`eng/docker/with-system-bus.sh`)
  - [ ] Check them on a real desktop: GNOME's session manager inhibit, logind's real `PrepareForSleep`/`PrepareForShutdown`,
        and a real screen locker's `ActiveChanged`
- [ ] nativeTheme against a real `xdg-desktop-portal` (GNOME, KDE): Docker has none, so the checks use a fake portal
      (`samples/Shared/FakePortal.cs`) that WindowTest starts before CEF
- [ ] nativeTheme: with no portal preference, pages keep GTK's colours from startup (DRIFT Y2); a CEF patch to clear
      Chromium's GTK style colour cache on theme notifications would let them follow
