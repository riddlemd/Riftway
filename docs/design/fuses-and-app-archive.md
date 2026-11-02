# Fuses and the packed app archive

Design for M4's "fuses and a packed app archive" (ELECTRON-PARITY: Fuses, ASAR). Status: proposal, nothing built.

## What Electron's fuses guard, and what Riftway has instead

Electron fuses are bits flipped in the shipped binary (`@electron/fuses`), read at startup, so a signed app cannot be
re-purposed by its launcher. Riftway has no Node, so half of them have no meaning; the risks they cover have .NET and
Chromium equivalents.

| Electron fuse | Default | Riftway equivalent |
|---|---|---|
| `RunAsNode` | on | n/a (no Node). The analogue is a launcher running other code in the app's signed process: `DOTNET_STARTUP_HOOKS` → fuse `StartupHooks` (off = runtimeconfig `System.StartupHookProvider.IsSupported=false`, which the runtime enforces) |
| `EnableNodeOptionsEnvironmentVariable` | on | `DOTNET_*` runtime knobs from the environment → fuse `DotnetEnvironment` (off = Riftway refuses to start when `DOTNET_STARTUP_HOOKS`, `DOTNET_ADDITIONAL_DEPS` or `DOTNET_SHARED_STORE` are set; most others are harmless) |
| `EnableNodeCliInspectArguments` | on | Debuggers attaching: fuse `Diagnostics` (off = runtimeconfig `System.Runtime.EnableDiagnostics=false`: no debugger, profiler or EventPipe) and fuse `RemoteDebugging` (off = Riftway strips `--remote-debugging-port`/`--remote-debugging-pipe`/`--remote-allow-origins` in `OnBeforeCommandLineProcessing`) |
| `EnableCookieEncryption` | off | Chromium already encrypts cookies; on macOS Riftway uses the mock keychain for unsigned and ad-hoc builds (DRIFT P3). Fuse `CookieEncryption` (on = the real Keychain whatever the signature) |
| `EnableEmbeddedAsarIntegrityValidation` | off | Fuse `ArchiveIntegrity`: the packed archive's header hash must match the one built into the app (below) |
| `OnlyLoadAppFromAsar` | off | Fuse `OnlyLoadFromArchive`: `Riftway.Blazor` and `app://` serve from the archive only, never from a loose `wwwroot` beside it |
| `GrantFileProtocolExtraPrivileges` | on | `file://` in CEF has Chromium's own (lesser) privileges already; Riftway records it as fixed off (DRIFT) |
| `LoadBrowserProcessSpecificV8Snapshot` | off | n/a (no V8 in the browser process) |

**Where fuses live.** MSBuild properties (`<RiftwayFuseDiagnostics>false</RiftwayFuseDiagnostics>`), written two ways:
the runtime-enforced ones as `RuntimeHostConfigurationOption` items (runtimeconfig.json), the Riftway-enforced ones as
an `[assembly: AssemblyMetadata("Riftway.Fuse.<Name>", "...")]` in the app's main assembly, read by `RiftwayApp.Create`.
Both files are covered by the signature (macOS seals every file in `Contents/MacOS`; on Windows a signed main
assembly and the Authenticode-signed exe). `Riftway.Fuses.Read(path)` and a `dotnet riftway fuses` view answer "what is
this build's state", as `npx @electron/fuses read` does.

## The packed archive

**Format: ASAR itself.** Electron's format is simple (a pickled JSON header of offsets, sizes and per-file SHA-256
block hashes, then the files back to back) and tools already read and write it (`@electron/asar`). Riftway writes it in
C#; nothing about it needs Node.

- **Packing:** a target after publish packs `wwwroot` (Riftway.Blazor apps: the static web assets too) into
  `resources/app.asar` (macOS: `Contents/Resources/app.asar`) and removes the loose files when `RiftwayPackArchive` is
  true. `RiftwayArchiveUnpack` globs stay outside, as `asar.unpacked` does (native modules have no analogue; large media
  might).
- **Serving:** an `IFileProvider` over the archive (`AsarFileProvider`), used by `BlazorWebView` in place of the
  `PhysicalFileProvider`, and `Protocol.HandleArchive(scheme, path)` for plain-HTML apps. Range requests come free:
  each entry is an offset and length into one file.
- **Integrity:** the build hashes the archive header (SHA-256, as Electron) and embeds it next to the fuses. With
  `ArchiveIntegrity` on, opening an archive whose header hash differs fails at startup; each file's block hashes are
  checked on read, so a byte changed after packing fails that read (Electron's `ValidateIntegrityOrDie`).

## Order of work

1. Fuse plumbing: MSBuild properties → runtimeconfig + assembly metadata; `RiftwayApp` reads them; `StartupHooks`,
   `Diagnostics`, `RemoteDebugging`, `DotnetEnvironment`. Unit tests for the reader and the switch stripping; a sample
   check that a `DOTNET_STARTUP_HOOKS` hook does not run with the fuse off.
2. ASAR reader/writer with Electron's own test archives as fixtures (byte-for-byte header compatibility).
3. Pack-after-publish target; `AsarFileProvider` in `Riftway.Blazor`; BlazorSample self-check from a packed publish.
4. Integrity: embed, verify header at startup and blocks on read; a check that a tampered archive refuses to load.
