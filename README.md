# Oriel

Electron for .NET: desktop apps in C#, rendered by one Chromium (CEF) on every platform, with Blazor Hybrid as the default UI model and Electron's concepts (`app`, `BrowserWindow`, `webContents`, IPC, `protocol`) in idiomatic C#.

**Status: pre-alpha (M2 in progress).** On macOS, Linux (X11) and Windows x64: `OrielApp` and `BrowserWindow` with much of Electron's window API, IPC (`ipcMain`, `ipcRenderer`, `contextBridge`) and custom schemes (`Protocol`). See [the todo list](docs/todo.md) for what remains.

## Platforms

Windows, Linux and macOS.

## Why CEF

System webviews (WebView2, WKWebView, WebKitGTK) mean three engines with different rendering and codecs. Oriel ships one Chromium everywhere, like Electron, but with .NET as the main process instead of Node. The cost is size, about the same as Electron.

## Codecs

Oriel runs on stock CEF: open codecs (AV1, VP9, Opus, …), no H.264 or AAC. A drop-in runtime variant built with Chrome's proprietary codecs is planned; switching is a package change, not a code change. See [the plan](docs/PLAN.md).

## Plan

[docs/PLAN.md](docs/PLAN.md): architecture, Blazor Hybrid hosting, codecs, milestones.

## Try it (macOS)

```sh
src/Oriel.Subprocess.Native/build.sh artifacts/subprocess/Oriel.Subprocess macosarm64   # sandbox-capable helper
dotnet publish samples/Oriel.Sample -r osx-arm64 -c Release --self-contained -o artifacts/sample
eng/bundle-macos.sh artifacts/sample Oriel.Sample artifacts/subprocess/Oriel.Subprocess artifacts
open artifacts/Oriel.Sample.app
```

## Blazor Hybrid

`Oriel.Blazor` runs Razor components in the app's process and renders them into a `BrowserWindow`: no server, no port,
no WebAssembly. The page loads from `app://localhost/` (one origin for every window) and Blazor's messages travel over
Oriel's IPC.

```csharp
var app = OrielApp.Create();
var services = new ServiceCollection();
services.AddOrielBlazor();               // before app.Run(): registers the app scheme
services.AddSingleton<SongLibrary>();
var provider = services.BuildServiceProvider();

app.Ready += (_, _) =>
{
    var window = new BrowserWindow(new() { Width = 1440, Height = 900, Title = "KHost" });
    window.LoadBlazor<App>(provider);    // wwwroot/index.html, component at #app
};
return app.Run();
```

The app project uses `Microsoft.NET.Sdk.Razor`; its `wwwroot/index.html` loads `_framework/blazor.webview.js`. Every
window shares the app's services. `samples/Oriel.BlazorSample` has two windows on one counter and JS interop both ways;
`--self-check` drives it and exits non-zero on a failure.

On a Mac, `-p:OrielMacBundle=true` (or `OrielMacBundle=true` in the environment) makes `dotnet build` lay the output
out as an `.app`, and `dotnet run` and `dotnet watch` start it. Hot reload of `.razor` and C# edits reaches the running
window without a restart:

```sh
OrielMacBundle=true dotnet watch --project samples/Oriel.BlazorSample
```

## Try it (Linux, in Docker)

```sh
docker build -t oriel-linux-dev -f eng/docker/linux-dev.Dockerfile eng/docker
eng/fetch-cef.sh linuxarm64   # or linux64 on x64 hosts
docker run --rm -v "$PWD":/src -v "$HOME/.cache/oriel/cef":/cef-cache --shm-size=1g oriel-linux-dev /src/eng/docker/run-linux.sh
# artifacts/linux/screen.png, app.log, exit.txt
# Sandboxed, as a non-root user (sandbox.txt shows each process's seccomp mode and namespaces):
docker run --rm --cap-add SYS_ADMIN --cap-add SYS_PTRACE --security-opt seccomp=unconfined -v "$PWD":/src -v "$HOME/.cache/oriel/cef":/cef-cache --shm-size=1g oriel-linux-dev /src/eng/docker/run-linux.sh --sandbox
# A test app under another window manager (mutter, kwin or xfwm4; openbox by default), from an image that adds them:
docker build -t oriel-linux-dev-wm -f eng/docker/linux-dev-wm.Dockerfile eng/docker
docker run --rm -e ORIEL_WM=mutter -v "$PWD":/src -v "$HOME/.cache/oriel/cef":/cef-cache --shm-size=1g oriel-linux-dev-wm /src/eng/docker/test-linux.sh samples/Oriel.WindowTest Oriel.WindowTest
```

## Try it (Windows)

Needs Git Bash (for `fetch-cef.sh`) and Visual Studio or Build Tools with the "Desktop development with C++" workload (MSVC and a Windows SDK); `build.ps1` finds it through vswhere.

```powershell
bash eng/fetch-cef.sh windows64
src/Oriel.Host.Native/build.ps1 artifacts/host/Oriel.Host.dll
dotnet publish samples/Oriel.Sample -r win-x64 -c Release --self-contained -o artifacts/sample
eng/layout-windows.ps1 artifacts/sample artifacts/host/Oriel.Host.dll OrielSample
artifacts/sample/OrielSample.exe
```

Or in one step: `dotnet publish samples/Oriel.Sample -r win-x64 -c Release --self-contained -o artifacts/sample -p:OrielWindowsLayout=true -p:OrielExeName=OrielSample` builds the host DLL (once per RID, into `src/Oriel.Host.Native/obj/<rid>/`) and runs the same layout after publishing. `OrielExeName` defaults to the assembly name plus `Run`.

Renderers run in Chromium's sandbox, as in Electron. Only CEF's `bootstrap.exe` can host the Windows sandbox broker, so the layout makes it the app: `bootstrap.exe` is renamed to the exe name you give (`OrielSample.exe`), `Oriel.Host.dll` sits beside it under the same name (`OrielSample.dll`), and the .NET apphost is removed, its version resources and icon copied onto the new exe. Every process starts as that exe. The host DLL runs CEF directly in renderer, GPU and utility processes, and starts .NET (hostfxr from the self-contained publish) and the app's `Main` in the browser process only. So:

- the exe name must differ from the app's assembly name, whose `.dll` stays beside it;
- publish self-contained (no framework-dependent or single-file publish);
- sign the exe, `<Exe>.dll` and `chrome_elf.dll` with the same trusted certificate, or none of them (a certificate Windows does not trust stops the launch, docs/DRIFT.md P13); `eng/verify-windows-signing.ps1 <Exe>.exe`, run after signing, names any file that would stop it;
- lay out before the exe ever runs at its final path: on first launch the shell caches the exe's FileDescription per path (MuiCache), and later launches of the stamped exe keep "CEF Bootstrap Application" on the taskbar. Installers copy the laid-out folder, never a half-done one. The stale name goes only if that path's `<path>.FriendlyAppName` value is removed from `HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache`; replacing or touching the exe doesn't clear it;
- a crash on start writes its reason to stderr, or shows it in a message box when there is none;
- `--no-sandbox` turns the sandbox off; `sandbox: false` is per session, not per window (`SessionOptions.Sandbox`, with Oriel's CEF patch 0010; docs/DRIFT.md W13).

`dotnet run`, and `eng/layout-windows.ps1 <publish-dir> -Subprocess <Oriel.Subprocess.exe>` (built by `src/Oriel.Subprocess.Native/build.ps1`), keep the unsandboxed apphost layout: the app's own `.exe`, with `Oriel.Subprocess.exe` for the children.

For Windows on Arm, pass `windowsarm64` to the build and layout scripts and publish with `-r win-arm64`; it cross-builds from x64 too, with the "MSVC ARM64 build tools" component installed. Not yet run on Arm hardware.

## Build an app from packages

Outside this repo an app references `Oriel` and one `Oriel.Runtime.<rid>` per RID it ships. The runtime package carries CEF and Oriel's native host, and `Oriel`'s build targets lay out the publish folder, with no Git Bash, MSVC or scripts:

```xml
<PackageReference Include="Oriel" Version="1.0.0" />
<PackageReference Include="Oriel.Runtime.win-x64" Version="1.0.0" />
```

```powershell
dotnet publish -c Release -r win-x64 --self-contained   # bin/Release/net10.0/win-x64/publish/<App>Run.exe
```

On Windows the result is the sandboxed layout above (`OrielExeName` names the exe; the same rules apply). On Linux the native executables get their exec bits when publishing on Linux; `chrome-sandbox` still needs `chown root:root` and `chmod 4755` from the installer before `EnableSandbox()`. `-p:OrielLayout=false` leaves the publish untouched.

The packages are not published to a feed yet. Build them into a local folder feed (`artifacts/pkg`):

```powershell
eng/pack.ps1                                            # Oriel, Oriel.Cef
eng/runtime/stage-windows.ps1 artifacts/runtime/win-x64 # needs MSVC, as above
dotnet pack eng/runtime/Oriel.Runtime.csproj -c Release -p:OrielRuntimeRid=win-x64 -p:OrielRuntimeStage=artifacts/runtime/win-x64 -o artifacts/pkg
```

For linux-x64, stage with `eng/runtime/stage-linux.sh artifacts/runtime/linux-x64` in the `oriel-linux-dev` image (it strips libcef.so, over 1 GB with symbols) and pack the same way; the package is 154 MB, win-x64's 184 MB.

### macOS

Reference `Oriel.Runtime.osx-arm64` and/or `osx-x64`; each carries CEF's framework and the helper executable, and
Oriel's targets put them into an app bundle. With both referenced, a publish takes the one for its `-r`, and a build
takes the one for the Mac it runs on. On a Mac, `dotnet build`, `dotnet run` and `dotnet watch` then run the app as
`bin/<config>/<tfm>/<App>.app` with no extra flags (`-p:OrielMacBundle=false` turns that off), and
`dotnet publish -r osx-arm64 --self-contained` lays out `<publish-dir>/<App>.app`:

```sh
dotnet publish -c Release -r osx-arm64 --self-contained \
  -p:OrielMacSignIdentity="Developer ID Application: Name (TEAMID)" \
  -p:OrielMacNotaryProfile=<notarytool-profile>      # optional: notarise and staple
```

`OrielMacSignIdentity` signs inside out with the hardened runtime (`-` signs ad hoc, for this Mac only);
`OrielMacEntitlements` and `OrielMacHelperEntitlements` replace the default entitlements (JIT and unsigned executable
memory for .NET and V8, library validation off). `OrielMacBundleName`, `OrielMacBundleId` and `OrielMacBundleVersion`
fill `Info.plist`. A Developer ID-signed app keeps Chromium's cookie key in the real Keychain; unsigned and ad-hoc
builds use Chromium's mock keychain, as their identity changes every build. Stage and pack the runtime on a Mac:

```sh
eng/runtime/stage-macos.sh artifacts/runtime/osx-arm64 osx-arm64
dotnet pack eng/runtime/Oriel.Runtime.csproj -c Release -p:OrielRuntimeRid=osx-arm64 -p:OrielRuntimeStage=$PWD/artifacts/runtime/osx-arm64 -o artifacts/pkg
```

In this repo, `-p:OrielMacBundle=true` does the same for the samples without the package (CEF from `eng/fetch-cef.sh`).

### Installers and updates

[Velopack](https://velopack.io)'s `vpk` (a dotnet tool, the same version as the `Velopack` package `Oriel.AutoUpdater` uses) packs the published folder:

```powershell
vpk pack --packId MyApp --packVersion 1.0.0 --packDir <publish-dir> --mainExe MyAppRun.exe --skipVeloAppCheck   # Windows: Setup.exe
vpk pack --packId MyApp --packVersion 1.0.0 --packDir <publish-dir> --mainExe MyApp                              # Linux: an AppImage (needs mksquashfs)
```

On macOS `--packDir` is the app bundle, and vpk makes a `.pkg` installer and a portable zip. vpk adds its updater to
the bundle, which breaks a signature made before it, so give vpk the identity too; it re-signs the bundle with
`--deep`, which keeps the helpers' entitlements and needs the per-file signatures `OrielMacSignIdentity` already made:

```sh
dotnet publish -c Release -r osx-arm64 --self-contained -p:OrielMacSignIdentity="Developer ID Application: ..."
vpk pack --packId MyApp --packVersion 1.0.0 --packDir <publish-dir>/MyApp.app --mainExe MyApp \
  --signAppIdentity "Developer ID Application: ..." --signEntitlements <Oriel package>/buildTransitive/Oriel.mac.entitlements \
  --notaryProfile <profile>
```

`--skipVeloAppCheck`: on Windows the main exe is CEF's native bootstrap, which vpk cannot inspect for a `VelopackApp` call. The installer runs that exe with hook arguments (`--veloapp-install` and others), which reach `Main` like any others; call `AutoUpdater.HandleInstallerHooks()` first in `Main`, before `OrielApp.Create()`, so those runs exit before a window opens. `AutoUpdater.Default` then checks a feed and installs an update, as Electron's `autoUpdater`. An AppImage mounts `nosuid`, so `chrome-sandbox` cannot be setuid in one: leave the sandbox off (Oriel's Linux default) or rely on unprivileged user namespaces.

## Known issues

### Video crashes the renderer on Linux arm64 VMs on Apple M4+ (worked around)

Chromium's libyuv, which converts video frames, uses its SME code whenever the CPU reports SME. That code is built for ARMv9-A and starts with an SVE instruction. Apple M4 and later have SME but no SVE, and Linux VMs on them (Docker, Rancher, Parallels, UTM) pass that through, so any `<video>` killed the renderer with SIGILL ("Aw, Snap!") on its first software-converted frame. Chrome and Electron have the same bug on those VMs; macOS, Windows and x64 are unaffected.

`Oriel.Subprocess.Native` works around it. On Linux arm64 it defines its own `getauxval`, which takes precedence over glibc's for libcef.so, and clears the SME bits only when SVE is absent. Every renderer and GPU process runs this exe, so apps need nothing. libyuv falls back to its NEON code. CPUs with SVE see their real flags. The .NET browser process is not covered; no crash there has been seen.

The workaround goes once Chromium fixes libyuv upstream: it should require SVE for those paths, or build them without SVE.

## Layout

- `src/Oriel` - public API: Electron's modules in C# (`OrielApp`, `BrowserWindow`, ...)
- `src/Oriel.Cef` - CEF C API bindings (`Interop/` is generated) and the native plumbing under them
- `src/Oriel.Host.Native` - Windows: the DLL that CEF's `bootstrap.exe`, renamed to the app's exe, loads in every process; it runs the children and starts .NET in the browser process (C, reusing the subprocess code)
- `src/Oriel.Subprocess.Native` - the exe Chromium runs for its zygote/renderer/GPU/utility processes on Linux, and on Windows in the unsandboxed apphost layout (a small C program: the zygote must be single-threaded, which no .NET process is)
- `src/Oriel.Subprocess` - the same exe for macOS, in NativeAOT C# (until the macOS sandbox moves it to the native one)
- `tools/cef-interop` - regenerates `Oriel.Cef/Interop` from the pinned CEF headers (ClangSharp)
- `eng/` - `cef.json` pins the CEF release; `fetch-cef.sh` downloads it; `bundle-macos.sh` assembles a .app; `layout-windows.ps1` lays out a Windows app; `pack.ps1` and `runtime/` build the NuGet packages
- `samples/` - sample apps
- `tests/` - unit tests

## License

[MIT](LICENSE)
