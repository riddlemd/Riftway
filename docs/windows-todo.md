# Windows todo

General items: [todo.md](todo.md). Windows work runs on the Windows laptop.

- [ ] **Sandbox**: today's layout (.NET apphost + `Oriel.Subprocess.exe`) can never be sandboxed: CEF forces
      `--no-sandbox` whenever `browser_subprocess_path` is set, and CEF 154 ships no linkable `cef_sandbox.lib`
      for MSVC. The workable model is CEF's `bootstrap.exe` renamed to `<App>.exe` plus a native `<App>.dll`
      exporting `RunWinMain` that hosts .NET in the browser process only. Experiments, in order:
  - [ ] (a) Build cefsimple from the standard CEF 154 dist with `USE_SANDBOX=ON`; confirm `chrome://sandbox`
  - [ ] (b) Probe: `bootstrap.exe` → `OrielProbe.exe` + `OrielProbe.dll` hosting Oriel.Sample through hostfxr;
        pass when the renderer runs at Untrusted and GPU at Low integrity with no certificate/compat errors
  - [ ] Then decide packaging: re-brand and sign per CEF bump, no .NET apphost, likely no single-file publish
- [ ] Re-run Oriel.ProtocolTest on Windows: the 2 checks added since (video blob control, video paints frames)
- [ ] Confirm the startup `observer_list.h:192` lines are gone on Windows (fixed in 86c2ae3, verified on macOS)
- [ ] Run Oriel.WindowTest on Windows (55 checks; so far only macOS and Linux)
- [ ] windows-arm64: untested
