# Oriel

Electron for .NET: desktop apps in C#, rendered by one Chromium (CEF) on every platform, with Blazor Hybrid as the default UI model and Electron's concepts (`app`, `BrowserWindow`, `webContents`, IPC, `protocol`) in idiomatic C#.

**Status: pre-alpha (M1).** On macOS, Linux (X11) and Windows x64, `OrielApp` + `BrowserWindow` open a CEF 154 window and load a URL.

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
dotnet publish src/Oriel.Subprocess -r osx-arm64 -c Release -o artifacts/subprocess
dotnet publish samples/Oriel.Sample -r osx-arm64 -c Release --self-contained -o artifacts/sample
eng/bundle-macos.sh artifacts/sample Oriel.Sample artifacts/subprocess/Oriel.Subprocess artifacts
open artifacts/Oriel.Sample.app
```

## Try it (Linux, in Docker)

```sh
docker build -t oriel-linux-dev -f eng/docker/linux-dev.Dockerfile eng/docker
eng/fetch-cef.sh linuxarm64   # or linux64 on x64 hosts
docker run --rm -v "$PWD":/src -v "$HOME/.cache/oriel/cef":/cef-cache --shm-size=1g oriel-linux-dev /src/eng/docker/run-linux.sh
# artifacts/linux/screen.png, app.log, exit.txt
# Sandboxed, as a non-root user (sandbox.txt shows each process's seccomp mode and namespaces):
docker run --rm --cap-add SYS_ADMIN --cap-add SYS_PTRACE --security-opt seccomp=unconfined -v "$PWD":/src -v "$HOME/.cache/oriel/cef":/cef-cache --shm-size=1g oriel-linux-dev /src/eng/docker/run-linux.sh --sandbox
```

## Try it (Windows)

Needs Git Bash (for `fetch-cef.sh`) and [zig](https://ziglang.org/download/) on PATH or in `$env:ZIG`.

```powershell
bash eng/fetch-cef.sh windows64
src/Oriel.Subprocess.Native/build.ps1 artifacts/subprocess/Oriel.Subprocess.exe
dotnet publish samples/Oriel.Sample -r win-x64 -c Release --self-contained -o artifacts/sample
eng/layout-windows.ps1 artifacts/sample artifacts/subprocess/Oriel.Subprocess.exe
artifacts/sample/Oriel.Sample.exe
```

## Layout

- `src/Oriel` - public API: Electron's modules in C# (`OrielApp`, `BrowserWindow`, ...)
- `src/Oriel.Cef` - CEF C API bindings (`Interop/` is generated) and the native plumbing under them
- `src/Oriel.Subprocess.Native` - the exe Chromium runs for its zygote/renderer/GPU/utility processes on Linux and Windows (C, ~25 lines: the zygote must be single-threaded, which no .NET process is)
- `src/Oriel.Subprocess` - the same exe for macOS, in NativeAOT C# (until the macOS sandbox moves it to the native one)
- `tools/cef-interop` - regenerates `Oriel.Cef/Interop` from the pinned CEF headers (ClangSharp)
- `eng/` - `cef.json` pins the CEF release; `fetch-cef.sh` downloads it; `bundle-macos.sh` assembles a .app
- `samples/` - sample apps
- `tests/` - unit tests

## License

[MIT](LICENSE)
