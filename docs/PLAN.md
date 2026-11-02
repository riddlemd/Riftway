# Riftway plan

Riftway is Electron for .NET: desktop apps written in C#, rendered by one Chromium everywhere, with
Blazor Hybrid as the default UI model. Windows, macOS and Linux; x64 and arm64.

## Principles

- **Electron's model, C#'s language.** Electron's concepts — `app`, `BrowserWindow`, `webContents`,
  `session`, `protocol`, IPC, `dialog`, `Menu`, `Tray` — with idiomatic .NET shapes: options
  records, events, `async`, dependency injection. Someone who knows Electron should find their way.
- **One engine.** The same Chromium (CEF) on every OS. A page behaves and plays media the same
  everywhere. Chrome's proprietary codecs come later as a drop-in runtime variant.
- **C# first.** App code, window control and native services are C#. Native code is limited to what
  CEF itself ships; OS calls are P/Invoke (Win32, the Objective-C runtime, D-Bus).
- **Blazor Hybrid by default, not required.** Razor components run in-process and render into the
  window over a message channel — no server, no port, no WebAssembly. The core stays UI-agnostic, so
  a plain HTML/JS page or a React/Vite app works too.
- **Electron's whole surface.** Every module Electron documents, tracked against a pinned Electron
  version in [ELECTRON-PARITY.md](ELECTRON-PARITY.md). Waves are ordered by dependency, not by
  what an app asks for first.

## Architecture

```
App (.NET exe, main process — Electron's "main")
├─ Riftway.Blazor     WebViewManager per window, Blazor dispatcher, static assets, DI
├─ Riftway            RiftwayApp, BrowserWindow, WebContents, Session, Protocol, Ipc, Dialog, Screen, …
├─ Riftway.Cef        C# bindings over CEF's C API
└─ libcef (Riftway.Runtime.<rid>)
     └─ Riftway.Subprocess (native, C) — Chromium's zygote / renderer / GPU / utility processes
```

- **Main process is .NET.** `RiftwayApp.Run()` owns the main thread and runs CEF's message loop on it
  (required on macOS; the same everywhere for one model). Windows are CEF Views windows, so Riftway
  writes no per-OS windowing code.
- **Subprocess is trivial and native.** `Riftway.Subprocess` only checks the API hash and calls
  `cef_execute_process`. No .NET runs in Chromium's child processes — the Linux zygote requires a
  single-threaded process, which no .NET process is (see Risks). Electron's preload/`contextBridge`
  role is covered by a preload script injected from the main process (below), not by code in the
  renderer process.
- **macOS** needs an `NSApplication` subclass implementing `CefAppProtocol`; built at startup through
  the Objective-C runtime from C#. Helper `.app` bundles are produced by the packaging targets.

## API shape

Every Electron module maps to a Riftway type of the same name in PascalCase — `app` → `RiftwayApp`,
`BrowserWindow`, `WebContents`, `Session`, `Protocol`, `IpcMain`, `Dialog`, `Menu`, `Tray`, … — with
Electron's methods, events and option names. The full mapping, with where each implementation comes
from, is [ELECTRON-PARITY.md](ELECTRON-PARITY.md).

.NET additions on top: `RiftwayApp.CreateBuilder` hosting with `IServiceCollection`; every module also
injectable (`IDialog`, `IScreen`, …) for Blazor components and tests; optional source-generated typed
IPC contracts.

```csharp
var builder = RiftwayApp.CreateBuilder(args);
builder.Services.AddRiftwayBlazor();
builder.Services.AddSingleton<SongLibrary>();

var app = builder.Build();
app.Ready += () =>
{
    var window = new BrowserWindow(new() { Width = 1440, Height = 900, Title = "KHost" });
    window.LoadBlazor<App>("#app");
};
app.Run();
```

Threading: property getters read a state snapshot the CEF UI thread keeps current through events;
setters post to the UI thread; anything with a result is `async`. Calls are safe from any thread.

### Page ↔ main channel

Everything without renderer-side code, so the helper stays trivial:

- **Page → main:** DevTools `Runtime.addBinding` exposes a function to the page and raises
  `Runtime.bindingCalled` in the browser process (how Playwright exposes functions). Fallback: a
  `POST` to the app's custom scheme.
- **Main → page:** `CefFrame.ExecuteJavaScript` into a dispatcher the preload installs.
- The preload runs in an isolated world (DevTools `Page.addScriptToEvaluateOnNewDocument` with a
  world name) and builds `window.riftway` — `ipcRenderer` (`invoke` with promise replies, `send`,
  `on`), `contextBridge`, `webFrame`, `webUtils` — plus the channel Blazor's `blazor.webview.js`
  expects.

### Blazor Hybrid

`Riftway.Blazor` subclasses `Microsoft.AspNetCore.Components.WebView.WebViewManager`, which needs:
navigation to a base URI (`app://localhost/`), static files over a privileged custom scheme
(`wwwroot`, Razor class library `_content/`, `blazor.webview.js`), the message channel above, and a
`Dispatcher`. One manager per window; all windows share the app's `IServiceProvider`.

## Runtime and codecs

Riftway starts on **stock CEF** — the Spotify CDN builds, repackaged as `Riftway.Runtime.<rid>`. That means
open codecs only (AV1, VP9, VP8, Opus, Vorbis, FLAC, MP3) and no build farm until one is needed.

Proprietary codecs (H.264, AAC; HEVC through the OS) come later as a **drop-in runtime variant**:
`Riftway.Runtime.Codecs.<rid>`, CEF built from the same commit with
`proprietary_codecs=true ffmpeg_branding=Chrome`. Swapping variants is a package or MSBuild property
change (`<RiftwayRuntime>Codecs</RiftwayRuntime>`); app code and Riftway's assemblies do not change.

What keeps it drop-in:

- **Same CEF commit, same C API.** Each variant is built from the exact commit of the stock release it
  pairs with, so `cef_api_hash` matches. Riftway checks the hash at startup and fails clearly on a
  mismatch.
- **Same file layout.** Both variants ship the same files and bundle structure; packaging targets do
  not branch on the variant.
- **Pages can detect it.** `MediaSource.isTypeSupported` reports what the runtime decodes, so an app
  can choose a stream format at run time (KHost: H.264 on the codec variant, AV1/VP9 + Opus otherwise).

Licensing: whoever distributes the codec variant carries the patent licensing (H.264: Via LA,
royalty-free under 100k units/yr with a signed licence). Whether it is a public package, a private
feed or built by the app is open.

## Milestones

| | Goal | Done when |
|---|---|---|
| **M0** | Codec proof (done early, not blocking) | A CEF 154 macOS arm64 build from the stock release's commit plays all six probe streams, and swapping it in for stock libcef needs no other change |
| **M1** | Hello window | `Riftway.Cef` bindings on CEF 154 (CefGlue core fork or own generator — decide in M1); NativeAOT helper; a Views window loads a URL on macOS, then Windows, then Linux (X11) |
| **M2** | Core | Windows and views (`BaseWindow`, `BrowserWindow`, `View`, `WebContentsView`), `WebContents`, `WebFrameMain`, `NavigationHistory`, `Debugger`, IPC + `contextBridge`, `Protocol`, core `Session`, file `Dialog`, `Screen`, `RiftwayApp` lifecycle, command line, `window.open` handling |
| **M3** | Blazor Hybrid | `Riftway.Blazor` with a sample app; multi-window; JS interop; `dotnet watch` story measured (macOS done: [todo.md](todo.md)) |
| **M4** | Ship it | `Riftway.Runtime.<rid>` packages; MSBuild targets that assemble the macOS bundle + helpers, sign and notarise; Windows and Linux layouts; Velopack installers and `AutoUpdater`; fuses and a packed app archive; `CrashReporter`; Linux sandbox (AppArmor profile or documented `--no-sandbox`) — all on stock CEF |
| **M5** | Desktop integration | `Menu`/`MenuItem`, `Tray`, `Notification`, `GlobalShortcut`, `Dock`, `Clipboard`, `Shell`, `NativeImage`, `NativeTheme`, `PowerMonitor`, `PowerSaveBlocker`, `SafeStorage`, `SystemPreferences`, message-box `Dialog`, `DesktopCapturer`, `ImageView` — on all three OSes |
| **M6** | Session, network, processes | `Session` in full, `Cookies`, `WebRequest`, `DownloadItem`, `Net`, `ClientRequest`/`IncomingMessage`, `WebSocket`, `NetLog`, service workers, `Extensions`, `ContentTracing`, `UtilityProcess`/`ParentPort`/message ports, page-side `webFrame`/`webUtils` |
| **M7** | Own CEF builds | CI builds of CEF for all six targets; `Riftway.Runtime.Codecs.<rid>` variant; Chromium/CEF patches: `<webview>`, corner-smoothing CSS, `SharedTexture`, built-in AI hooks; macOS `TouchBar`, `ShareMenu`, `InAppPurchase`, `PushNotifications` |

M7 is where Riftway first needs its own CEF builds; the codec variant and the patches share that
pipeline. M2–M7 together cover every module in [ELECTRON-PARITY.md](ELECTRON-PARITY.md). M5 and M6 are
independent of each other and can run in parallel.

**Dogfooding:** KHost moves to Riftway once M4 lands, and keeps moving with each wave. Until the codec
variant exists, its LocalScreen uses AV1 or VP9 + Opus streams (measured working in stock CEF 154).

**Size of the job:** Electron's full surface on three OSes is roughly 12–18 months for one developer
with AI help, plus the ongoing Chromium cadence. Estimate, not measured.

## Risks

- **Chromium cadence:** a new milestone every 2 weeks and weekly security fixes. Each CEF bump means
  regenerating bindings and re-signing; from M7 also rebuilding six targets. Needs automation.
- **Build farm (from M7):** 6 targets, hours each. macOS needs Mac hardware (GitHub's Mac runners lack the disk);
  Windows needs large runners or self-hosted; Linux x64/arm64 can share a box.
- **Wayland:** CEF Views on Wayland is still being fixed upstream; start on X11/XWayland.
- **Native subprocess (decided, proven on Linux):** Chromium's Linux zygote CHECKs that its process
  is single-threaded, and every .NET process — NativeAOT included — has runtime threads. So the
  subprocess is ~25 lines of C over CEF's own C API (`src/Riftway.Subprocess.Native`), prebuilt per
  RID in the runtime package; app code stays C#. On Linux it runs the zygote and the sandbox
  (measured: renderers under seccomp-BPF in their own PID/net namespaces). macOS still uses the
  NativeAOT `Riftway.Subprocess`; moving it to the native one comes with the macOS sandbox, which links
  CEF's sandbox library into the helper.
- **Linux sandbox:** Ubuntu 24.04+ blocks Chromium's sandbox unless an AppArmor profile is installed.
- **Chromium/libyuv SIGILL on SME-without-SVE arm64 (Linux VMs on Apple M4+ hosts):** libyuv picks its
  `*_SME` YUV→RGB rows on HWCAP2_SME alone, but builds them with `-march=armv9-a+sme`, whose prologue
  runs an SVE `cntd`; the renderer dies ("Aw, Snap! SIGILL") on the first software-converted video
  frame. Linux/Android arm64 only. Worked around in `Riftway.Subprocess.Native`: its own `getauxval`
  interposes libcef.so's and clears HWCAP2 SME when SVE is absent (see README, Known issues). Remove it
  once fixed upstream; to file with libyuv/Chromium.
- **macOS full screen blacks out other displays:** native full screen (`SetFullScreen`, the green
  button) moves the window to its own Space, and the other displays go black. Mitigated:
  `SetSimpleFullScreen` and the `SimpleFullScreen` option fill the display without a new Space, like
  Electron's `setSimpleFullScreen`. Still open: the green button uses native full screen even with the
  option set ([mac-todo.md](mac-todo.md)).
- **Size:** ~130–175 MB compressed per platform, comparable to Electron.
- **Blazor over a message hop:** render batches cross browser → renderer; very busy UIs may need a
  faster path.
- **Chromium patches:** `<webview>`, corner smoothing, shared textures and built-in AI need
  patches carried on every CEF bump. Kept in M7 and kept small; each one is a rebase cost forever.
- **Electron moves too:** a new Electron major every 8 weeks can add or change modules. Parity is
  pinned to one Electron version and bumped deliberately.
- **Preload without renderer code:** `Runtime.addBinding` and injected scripts must give isolation
  comparable to Electron's `contextIsolation`; verify in M2.

## Rejected

- **System webviews (Photino, Tauri, MAUI)** — three engines with different rendering and codecs.
- **Electron binaries driven from .NET** — fastest route to codecs, but Node in every app and C# as
  a guest in Electron's process.
- **Forking Electron / own Content API shell** — Electron's C++ is bound to Node and V8; owning
  Chromium's internal API is a team-sized job.
- **System Chrome in app mode over CDP** — no window ownership, depends on an installed browser.
- **WebView2 cross-platform** — not shipped for macOS/Linux.
