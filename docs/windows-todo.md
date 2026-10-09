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
  - [ ] Per-window `sandbox: false`: no CEF hook maps a renderer launch to its browser (DRIFT W13)
  - [ ] Signing: tested unsigned only; exe and DLL must share a certificate (DRIFT P13). Test a mismatch and a real signature
  - [ ] Packaging (M4): build `Oriel.Host.Native` per RID into the runtime package; an MSBuild target instead of
        the script; re-check `bootstrap.exe`'s resources and hash on every CEF bump
  - [ ] The shell caches an exe path's FileDescription (MuiCache) on first launch: installers must lay out before
        any run, or the taskbar keeps "CEF Bootstrap Application"
  - [ ] getAppMetrics `sandboxed`/`integrityLevel` per child once the Windows process walk lands (A18)
- [x] Re-run Oriel.ProtocolTest on Windows: the 2 checks added since (video blob control, video paints frames)
- [x] Confirm the startup `observer_list.h:192` lines are gone on Windows (fixed in 86c2ae3, verified on macOS)
- [x] Run Oriel.WindowTest on Windows: 90/91 (3 checks are macOS-only). DevTools, F12, webPreferences and
      sendInputEvent pass.
- [x] Modal focus on Windows: `Focus()` on a parent activates it although Chromium disabled it (Win32 allows
      SetForegroundWindow on a disabled window). Fix: focus the innermost visible modal child instead; merged
      (d5e0b6b). Its `is_visible` filter has no covering check.
- [x] Run the window-menu and context-menu checks on Windows (pass, after the menu-bar reference fix)
- [ ] windows-arm64: untested
- [ ] Run the Alt-key menu checks on Windows (Alt toggles an auto-hidden bar, Alt+F opens File; pass on Linux)
- [ ] Run the menu-event checks on Windows (WindowTest `MenuEventsAsync` popup and bar, the tray's Views menu)
