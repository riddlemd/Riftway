# Windows todo

General items: [todo.md](todo.md). Windows work runs on the Windows laptop.

- [ ] **Sandbox**: today's layout (.NET apphost + `Oriel.Subprocess.exe`) can never be sandboxed: CEF forces
      `--no-sandbox` whenever `browser_subprocess_path` is set, and CEF 154 ships no linkable `cef_sandbox.lib`
      for MSVC. The workable model is CEF's `bootstrap.exe` renamed to `<App>.exe` plus a native `<App>.dll`
      exporting `RunWinMain` that hosts .NET in the browser process only. Experiments, in order:
  - [x] (a) Build cefsimple from the standard CEF 154 dist with `USE_SANDBOX=ON`; confirm `chrome://sandbox`
  - [x] (b) Probe: `bootstrap.exe` → `OrielProbe.exe` + `OrielProbe.dll` hosting Oriel.Sample through hostfxr;
        pass when the renderer runs at Untrusted and GPU at Low integrity with no certificate/compat errors
        (2026-10-03, both pass: renderers Untrusted/Lockdown, GPU Low, CLR in the browser process only;
        ProtocolTest, IpcTest and WindowTest pass sandboxed, unsigned. Probe on the laptop's unmerged
        `win-sandbox-probe`; packaging facts in the relay doc windows-laptop-oriel-windows-todo-sandbox-1791075323473)
  - [ ] Then decide packaging: re-brand and sign per CEF bump, no .NET apphost, likely no single-file publish
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
