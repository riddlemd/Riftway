# Windows todo

General items: [todo.md](todo.md). Windows work runs on the Windows laptop.

- [x] **Sandbox** (branch `win-sandbox`): renderers, GPU and the storage service are sandboxed by default, as in
      Electron, in the bootstrap layout: CEF's `bootstrap.exe` renamed `<Exe>.exe` plus `src/Oriel.Host.Native` as
      `<Exe>.dll` (`RunWinMain`: children go to `cef_execute_process` with bootstrap's sandbox info; the browser
      starts the self-contained app through hostfxr, and `OrielApp.Run` takes the sandbox info from the DLL's
      `oriel_sandbox_info` and leaves `browser_subprocess_path` unset). `eng/layout-windows.ps1` builds it by
      default and copies the apphost's version resources and icon onto the exe. chrome://sandbox and token reads
      (2026-10-05): renderers Untrusted/Lockdown, GPU Low/Limited, storage Untrusted, network service unsandboxed
      (Chromium's default); `--no-sandbox` turns it all off. WindowTest 400/401 (always-on-top flake), SessionTest
      141/143 (spellcheck flake, getAppMetrics CPU: no Windows children, A18), IpcTest 11/11, ProtocolTest 37/37.
  - [x] `sandbox: false`, per session rather than per window (DRIFT W13): CEF patch 0010 names a renderer launch's
        profile, and `SessionOptions.Sandbox = false` gives that session's renderers `--no-sandbox`. SessionTest
        `patched (0010)` (2026-10-14, bootstrap layout, CEF a03e714 + 0001-0011): two windows each on an in-memory
        unsandboxed and a sandboxed session plus one on a persistent unsandboxed one, interleaved, the same site in
        all: the unsandboxed sessions' renderers at medium integrity (`Sandboxed` false), the sandboxed one's
        untrusted, at once. Stock CEF refuses such a session in a sandboxed app.
  - [x] Signing (DRIFT P13), tested with a self-signed certificate trusted per user: exe + `<Exe>.dll` +
        `chrome_elf.dll` signed with one certificate runs sandboxed (IpcTest 0 failures); unsigned `chrome_elf.dll`, any
        one signed alone, the DLL under another certificate, or an untrusted root is refused with only a stderr FATAL
        and exit 0x80000003. A friendlier error would need a patched bootstrap
    - [ ] Pre-flight: `eng/verify-windows-signing.ps1 <Exe>.exe`, run after signing, names each file and its problem
          (unsigned among signed, a signature `WinVerifyTrust` does not accept, two certificates). Each refused case
          fails it and an unsigned layout passes (2026-10-17, self-signed certificates, removed after); the trusted,
          correctly signed case is not yet run: trusting a root per user needs the user to accept Windows' prompt
  - [ ] Packaging (M4): build `Oriel.Host.Native` per RID into the runtime package; re-check `bootstrap.exe`'s
        resources and hash on every CEF bump
    - [x] Opt-in publish target (`-p:OrielWindowsLayout=true`, `eng/windows-layout.targets`) that builds the host and
          runs `layout-windows.ps1`: IpcTest and Sample lay out file for file as the script does and run sandboxed
          (2026-10-14, d07986c, Wojtek). Still a prototype: it needs `pwsh` and a C++ toolchain on the publishing machine
  - [x] The shell caches an exe path's FileDescription (MuiCache) on first launch: installers must lay out before
        any run, or the taskbar keeps "CEF Bootstrap Application". Reproduced with Oriel.Sample (2026-10-18, 1f300fe,
        Wojtek): one launch of the unstamped bootstrap.exe at a new path, then the stamped exe there, replaced and
        touched, still shows "CEF Bootstrap Application" on the taskbar; removing that path's `.FriendlyAppName`
        value fixes the next launch without restarting Explorer. Rule in the README Windows section; the layout
        writes no registry value
  - [x] getAppMetrics `sandboxed`/`integrityLevel` per child (A18): SessionTest "each process's integrityLevel is its
        token's, and sandboxed means below medium" passes; browser and network service medium, GPU and one utility
        low, renderers untrusted (2026-10-13, d07986c, Wojtek, default layout)
- [x] Re-run Oriel.ProtocolTest on Windows: the 2 checks added since (video blob control, video paints frames)
- [x] Confirm the startup `observer_list.h:192` lines are gone on Windows (fixed in 86c2ae3, verified on macOS)
- [x] Run Oriel.WindowTest on Windows: 90/91 (3 checks are macOS-only). DevTools, F12, webPreferences and
      sendInputEvent pass.
- [x] Modal focus on Windows: `Focus()` on a parent activates it although Chromium disabled it (Win32 allows
      SetForegroundWindow on a disabled window). Fix: focus the innermost visible modal child instead; merged
      (d5e0b6b). Its `is_visible` filter has no covering check.
- [x] Run the window-menu and context-menu checks on Windows (pass, after the menu-bar reference fix)
- [x] Run WindowTest `NativeImageAsync`'s `createThumbnailFromPath` check: "Windows: createThumbnailFromPath of a PNG is
      one 1x bitmap from the thumbnail cache" and the six `.ico` checks (LoadImage's conversion, pixel for pixel) pass
      (2026-10-13, d07986c, Wojtek). The per-step rejection checks are macOS-only; Windows has none
- [ ] windows-arm64: the unsandboxed layout builds, publishes and lays out from x64 with every native binary ARM64
      (2026-10-10); so does the sandboxed one: `Oriel.Host.Native/build.ps1 ... windowsarm64`, Oriel.Sample published
      `-r win-arm64` and laid out with `-Platform windowsarm64`, and dumpbin reads ARM64 on all 24 native PEs (the exe,
      host DLL, libcef, CoreCLR) and the 91 ReadyToRun assemblies (2026-10-17, 455d58e, from x64 on Wojtek). Nothing has
      run on Arm hardware
- [x] Run the Alt-key menu checks on Windows (Alt toggles an auto-hidden bar, Alt+F opens File): all 7 pass in a
      default WindowTest run (2026-10-10, 01e9524: WindowTest 235/235, ProtocolTest 34/34, IpcTest 11/11,
      SessionTest 42/42, unit 381 + 3 macOS skips; at 100% scaling over RDP)
- [x] Screen checks at 125% scaling: all pass on the primary display at 125% (2026-10-18, 1f300fe, Wojtek console,
      3 monitors): DipToScreenPoint/Rect (100,50 -> 125,62), ScreenToDipPoint/Rect back within a pixel (100,49),
      GetCursorScreenPoint, each display's centre and floored NativeOrigin. At 150% (2026-10-13) they pass too
- [ ] Three other WindowTest checks fail at 125% only (0 failures at 100%, 2026-10-18): PreferredSizeChanged after an
      animation ends at 300x332, not 333; CapturePage with a rect has red in its last device pixel (50x38 for a 40x30
      DIP rect); TitleBarStyle.Hidden's content bounds are 1 DIP taller than the window's (251 vs 250). Each is a
      fractional-pixel rounding: decide per check whether Oriel or the check rounds differently from Electron
- [x] Run the menu-event checks on Windows: WindowTest `MenuEventsAsync`'s popup and menu-bar checks (6) and the tray's
      Views menu ("the tray menu raises MenuWillShow and MenuWillClose each time it opens and closes") pass
      (2026-10-13, d07986c, Wojtek). The submenu, Escape and nested-menu checks drive xdotool and run on Linux only
- [ ] `desktopCapturer`: WindowTest `DesktopCapturerChecks` passes on Windows (13 checks: the window listed by title,
      its thumbnail's aspect and pixels, `fetchWindowIcons` app icons, one screen per display, `display_id` equal to
      `Screen.GetAllDisplays`, Chromium's names; 2026-10-13, d07986c, Wojtek). Still open: SessionTest's two
      `chromeMediaSourceId` checks fail on main (empty `displaySurface`, fixed on `win-fixes`); DXGI and WGC capture
      are DRIFT DC3
  - [ ] Windows-specific checks written on branch `desktopcapturer-win`, not yet run (need an unlocked console): own
        windows follow Chromium's collector (hidden, minimized, untitled, owned out; owned `WS_EX_APPWINDOW` in), a
        cloaked own window still listed, another process's window merged between two of ours by z-order and dropped
        once minimized, own titles read past 500 characters (fixed on the branch), the primary device's screen
        carries `Screen.GetPrimaryDisplay`'s id, each screen's display matches its device's mode size, window
        thumbnails upright and in colour order, a screen thumbnail shows a window where it sits and has the
        letterboxed size, `appIcon` order (big `WM_GETICON` before small, class icon before small) and an empty
        `appIcon` for a window without one
- [ ] `app` audit (branch `app-audit`): `showEmojiPanel` (Win+. through `SendInput`) and the singleton's
      WM_COPYDATA client for `requestSingleInstanceLock` data are ported (SessionTest's second-launch check passes;
      the emoji picker is unverified, DRIFT A25); still to run: the about panel's message box and
      `runningUnderARM64Translation` (`IsWow64Process2`)
  - [x] SessionTest `AppAuditChecks`: all 24 pass on Windows (2026-10-13, d07986c, Wojtek)
  - [ ] The about panel's message box: no check shows it (the unit test covers only its text)
  - [ ] `runningUnderARM64Translation`: no check; needs an arm64 device (x64 answers false either way)
