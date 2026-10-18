# Linux todo

General items: [todo.md](todo.md).

- [x] **safeStorage against a real Secret Service**, and Chromium's deadlock guard on both paths (was DRIFT D4):
      `docker build -t oriel-linux-dev-keyring -f eng/docker/linux-dev-keyring.Dockerfile eng/docker` (on top of
      `oriel-linux-dev`), then run SafeStorageTest with `-e ORIEL_SECRET_SERVICE=gnome-keyring -e ORIEL_SS_EXPECT=gnome_libsecret`
      (add `-e ORIEL_SS_ASYNC_FIRST=1` for the asynchronous provider creating the item), or
      `-e ORIEL_SECRET_SERVICE=silent -e ORIEL_SS_EXPECT=gnome_libsecret -e ORIEL_SS_SILENT=1` (a fake service that answers
      only `ReadAlias`: both paths fall back after the 25 s D-Bus timeout). Unset, the default run is unchanged; keep it on
      `oriel-linux-dev`, where no keyring can be D-Bus-activated (an activatable but locked gnome-keyring waits for an unlock
      prompt nobody answers, as Chromium's would)

- [x] **`sandbox: false` per session** (DRIFT W13, CEF patches 0010 and 0011): SessionTest `patched (0010, 0011)` with
      `test-linux.sh --sandbox` (2026-10-16, branch `sessiontest-fd-floor` on 455d58e, linux-x64 Docker on Windows, CEF
      a03e714 + 0001-0011 with `disable_fieldtrial_testing_config=true`): 361 passed, 0 failed. The unsandboxed sessions'
      renderers are the browser's children with `--no-sandbox` and only Docker's seccomp filter; the sandboxed one's are
      the zygote's under Chromium's filter too. Stock CEF sandboxed: 346 passed, and the unsandboxed session is refused

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
- [x] **CrashReporter** (branch `crash-reporter-mac-linux`; macOS passes): run CrashTest. linux-x64 passes, 48 checks
      (2026-10-14, 63ac6f1, real x64 Docker on Windows, stock CEF): the handler is `Oriel.Subprocess --type=crashpad-handler`
      with the app's database, `platform=linux`, renderer reports carry `globalExtra`, and the browser crash (exit 133)
      reaches Crashpad and uploads. linux-arm64 not rerun
      `docker run --rm -v "$PWD":/src -v "$HOME/.cache/oriel/cef":/cef-cache --shm-size=1g oriel-linux-dev /src/eng/docker/test-linux.sh samples/Oriel.CrashTest Oriel.CrashTest`.
      Expect the handler to be `Oriel.Subprocess --type=crashpad-handler --database=~/.config/<name>/Crashpad`, `platform=linux`,
      the renderer keys set from `OnWebKitInitialized` after the zygote fork (CR11), and the browser crash reaching Crashpad's
      signal handler ahead of .NET's
- [ ] **Transparent windows** (DRIFT W24): CEF patch [eng/cef](../eng/cef/README.md) 0001 and Oriel's side pass on
      linux-x64 (2026-10-14, 63ac6f1, patches 0001-0009); the Electron comparisons below are open.
  - [x] Build the patched libcef (`eng/cef/apply-patches.sh`, then `cef-build-linux/build.sh --no-update --force-build`);
        check its next-version hashes against `CefPatchedApi.cs`
  - [x] Rebuild `oriel-linux-dev` (adds xcompmgr, x11-utils) and run WindowTest with `ORIEL_CEF_DIR`; the
        `LinuxTransparencyAsync` checks read the X visual and the screen over a red backdrop: all 12 pass, with the
        translucent `SetBackgroundColor` one (linux-x64, 2026-10-14, 63ac6f1; stock CEF skips the block)
  - [ ] Compare with Electron what shows where the page is clear without a compositing manager: Electron also clears
        the compositor colour, which CEF cannot
  - [ ] Electron gives every frameless window on Linux a translucent widget, for its own client-side frame
        (`ElectronFrameViewLinux`); Oriel only transparent ones. Check whether anything visible depends on it
- [x] The systemPreferences checks in WindowTest's `LinuxThemeAsync` (portal accent and `accent-color-changed`,
      `gtk-enable-animations`, the members Electron lacks on Linux) pass in Docker on stock CEF
- [x] WindowTest `LinuxDisplayEventsAsync` passes under Xvfb: a `_NET_WORKAREA` change and an `xrandr` mode change each
      raise `display-metrics-changed` within 250 ms, and no poll runs (DRIFT R1). The image needs `x11-xserver-utils`
      for `xrandr`
- [ ] Listen for the GTK scale change Chromium's `X11ScreenOzone::OnDeviceScaleFactorChanged` hears (DRIFT R1)
- [ ] Linux sandbox on Ubuntu 24.04+: AppArmor profile, or documented `--no-sandbox` (M4)
- [ ] Wayland: CEF Views on Wayland is still being fixed upstream; Oriel targets X11/XWayland for now
- [ ] Clipboard: the Wayland data device with Wayland, and the XDG portal file targets (DRIFT K1)
- [x] linux-x64: only linux-arm64 is tested end to end (Docker on Apple Silicon). On linux-x64 (amd64 image under QEMU)
      `Oriel.Subprocess.Native` links and `dotnet test tests/Oriel.Tests` passes (1302 passed, 3 skipped).
  - [x] Run `eng/docker/test-linux.sh` on real x64 hardware: under QEMU `dotnet publish` segfaults (needs
        `DOTNET_EnableWriteXorExecute=0`) and Chromium's GPU process then dies with SIGTRAP, even with `--disable-gpu`.
        On real x64 (2026-10-14, 63ac6f1, Docker on Windows, stock CEF) publish needs no W^X switch and the GPU process
        (llvmpipe) lives: unit 3897 passed, 59 skipped; SessionTest, IpcTest, ProtocolTest, ShellTest, SafeStorageTest
        (default, gnome-keyring and silent) and CrashTest pass; WindowTest fails only the three Linux dialog checks
        (folder dialog, OpenDirectory, a download's save dialog), as on every Linux x64 run
  - [x] Find why those three dialog checks fail on linux-x64 only (they pass on linux-arm64 Docker, the same image):
        a test bug. The folder dialog's `xdotool search --sync` ran while the open dialog answered just before was being
        destroyed; xdotool quits on a window that goes away mid-walk, so the search came back empty in 32 ms, the folder
        dialog stayed unanswered, and CEF then refused the download's dialog ("Multiple simultaneous dialogs"). arm64
        is slow enough that the old dialog is gone first. `Answer` now retries the search for 10 s: WindowTest 0
        failures on linux-x64 (2026-10-17, 455d58e, stock CEF); without it the trio failed 9 of 9 soak rounds
  - `eng/docker/*.sh` pick the CEF platform and RID from `uname -m`
- [x] Modal windows (`Modal = true`) block the parent under any window manager, as Electron: an input-only X11 cover
      plus dropped keys (branch `linux-modal`); the same route serves `SetEnabled`/`IsEnabled` and message boxes (MB4).
      No window manager would do it: `X11Window::Map` rewrites `_NET_WM_STATE` and drops the modal hint, so without
      the cover the parent's page takes clicks under openbox, Mutter, KWin and Xfwm4. Focus goes to the parent, as Electron
  - [x] Re-run the input-blocking checks under Mutter, KWin and Xfwm4 (`ORIEL_WM`): all pass, view pages included
  - [ ] Crossing events still reach a disabled window's page with stock CEF (DRIFT W5)
    - [x] CEF patch [eng/cef](../eng/cef/README.md) 0002 (`CefWindow::SetEventListeningEnabled`, Electron's
          `DisableEventListening`) and `LinuxCefPatches.SetWindowInputEnabled`
    - [x] Called first in `ApplyLinuxEnabled`; the X11 input cover and dropped keys stay the stock fallback
    - [x] Run WindowTest's `LinuxPatchedInputAsync` on the patched libcef (crossing events, clicks, wheel, keys),
          and the modal and `SetEnabled` checks on both libcefs: all pass on linux-x64 (2026-10-14, 63ac6f1, patches
          0001-0009)
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
- [ ] `Notification` (branch `linux-notification`): Electron's `LibnotifyNotification` over D-Bus without libnotify
      (DRIFT Q5-Q11); unit tests pass on the host
  - [x] WindowTest's `LinuxNotificationTest` (fake `org.freedesktop.Notifications`,
        `samples/Shared/FakeNotificationServer.cs`) passes in Docker: Notify's arguments and hints, `show` inside
        `Show()`, the default action as `click`, no `action` or `reply`, `NotificationClosed` as `close`,
        `CloseNotification` from `Close()`, a refused Notify as `failed`, `IsSupported`. All 14 pass on linux-arm64
        and linux-x64 (2026-10-14, 63ac6f1)
  - [ ] Check against real servers: GNOME Shell, Plasma, dunst/mako (the default action, `image-data`, `append`)
  - [ ] The click's activation token on the patched libcef (DRIFT Q10), with the tray's. No check drives it: the fake
        server can emit `ActivationToken` but no notification check sends it
- [ ] `desktopCapturer` on X11 (branch `desktop-capturer`): Oriel's own enumeration and capture (XQueryTree, `WM_STATE`,
      `WM_NAME`, XRandR monitors, XComposite + `XGetImage`), separate from Chromium's capturer below. WindowTest's
      `DesktopCapturerChecks` pass under Xvfb; run them on a real X server; the EDID-based `display_id` needs a real monitor (Xvfb
      has no EDID, so it falls back to the output's place). SessionTest's `chromeMediaSourceId` checks are skipped under
      the arm64 Xvfb with the other capture checks (they pass under the x64 Xvfb)
  - [ ] Wayland: the xdg-desktop-portal ScreenCast picker (DRIFT DC2)
- [ ] Screen capture: Chromium's X11 desktop capturer dies with SIGILL under the Docker Xvfb (arm64) when getDisplayMedia starts; SessionTest skips the capture checks there (ORIEL_XVFB on arm64). Verify on a real X server; if it crashes there too, find the CHECK with CEF's symbols.
      Under the x64 Docker Xvfb all of them pass (2026-10-14, 63ac6f1): getDisplayMedia video and loopback audio, and
      `chromeMediaSourceId` screen and window sources
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
- [x] nativeTheme: with no portal preference, pages keep GTK's colours from startup with stock CEF (DRIFT Y2). Chromium
      patch [eng/cef](../eng/cef/README.md) 0003 clears the GTK style colour cache on the settings notifications.
      WindowTest's three `NativeTheme, CEF patch 0003` page checks pass on linux-x64 (2026-10-14, 63ac6f1)
- [x] Tray activation token (linux-tray's T5): CEF patch [eng/cef](../eng/cef/README.md) 0004
      (`CefWindow::SetXdgActivationToken`) and `LinuxCefPatches.ProvideActivationToken`, called from `Tray.Linux.cs`
      `HandleItemCall` for `ProvideXdgActivationToken`. WindowTest's `LinuxActivationTokenAsync` (3 checks: taken,
      `_NET_STARTUP_ID` on the next window, used once) passes on linux-x64 (2026-10-14, 63ac6f1). T6 (Chromium's
      `dbus::Bus`) is not patched: see the eng/cef README
- [ ] `app` audit (branch `app-audit`): build and run SessionTest `AppAuditChecks`, and check by hand the GIO
      protocol client (`SetAsDefaultProtocolClient` with an installed desktop file, `GetApplicationInfoForProtocol`'s GTK 3
      icon) and `ShowAboutPanel`'s GtkAboutDialog (DRIFT A12); A14's `assets` check
