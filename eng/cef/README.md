# Oriel's CEF patches

Oriel runs on stock CEF. Where stock CEF cannot give Electron's behaviour, a patch here adds the missing piece to CEF
itself, and Oriel uses it only when the loaded libcef has it. With stock CEF the matching DRIFT row stays true.

Base: CEF `a03e714` (branch 8037, CEF 154, Chromium 154.0.8037.94), the version in [`eng/cef.json`](../cef.json).

## Patches

| Patch | Applies in | What it does | DRIFT |
|---|---|---|---|
| [`cef/0001-window-delegate-is-translucent.patch`](patches/cef/0001-window-delegate-is-translucent.patch) | `chromium/src/cef` | Adds `CefWindowDelegate::IsTranslucent`. `CefWindowView::CreateWidget` reads it into `views::Widget::InitParams::opacity` (`kTranslucent`), as Electron's `NativeWindowViews` does for `transparent: true`. On Linux X11, Chromium then gives the window a 32-bit ARGB visual while a compositing manager runs; on macOS the `NSWindow` is created non-opaque. | W24 (Linux; macOS's `InitParams::opacity`) |
| [`cef/0002-window-set-event-listening-enabled.patch`](patches/cef/0002-window-set-event-listening-enabled.patch) | `chromium/src/cef` | Adds `CefWindow::SetEventListeningEnabled`. On Linux `CefWindowImpl` calls `DesktopWindowTreeHostLinux::DisableEventListening` (a null event targeter) and keeps the closure that undoes it, as Electron's `NativeWindowViews::SetEnabledInternal` does for `setEnabled(false)` and a shown modal's parent: no mouse, key, touch or pointer crossing event reaches the window, and activation is untouched. Does nothing elsewhere. | W5 |
| [`chromium/0003-gtk-clear-style-color-cache-on-theme-change.patch`](patches/chromium/0003-gtk-clear-style-color-cache-on-theme-change.patch) | `chromium/src` | `OsSettingsProviderGtk::OnSignal` clears GTK's style colour cache (`gtk::ClearStyleColorCache`) before it notifies. CEF's `linux_gtk_theme_3610.patch` disconnects `GtkUi::OnThemeChanged`, which clears it in Electron, so with no portal colour scheme `PreferredColorScheme()` kept reading the startup theme's window background. No API change. | Y2 |
| [`cef/0004-window-set-xdg-activation-token.patch`](patches/cef/0004-window-set-xdg-activation-token.patch) | `chromium/src/cef` | Adds the static `CefWindow::SetXdgActivationToken` (`cef_window_set_xdg_activation_token`), which calls `base::nix::SetActivationToken` as Chromium's `StatusIconLinuxDbus::OnProvideXdgActivationToken` does; Chromium puts the token on the next window it maps or activates (`_NET_STARTUP_ID` on X11, xdg-activation on Wayland). Does nothing off Linux. | T5 (branch `linux-tray`) |
| [`cef/0005-download-item-gesture-validators-initiator-frame.patch`](patches/cef/0005-download-item-gesture-validators-initiator-frame.patch) | `chromium/src/cef` | Adds `CefDownloadItem::HasUserGesture`, `GetLastModifiedTime`, `GetETag`, `GetRequestInitiator` (the initiator's `url::Origin::Serialize`) and `GetFrameIdentifier` (the `CefFrame` identifier of `content::DownloadItemUtils::GetRenderFrameHost`, empty once the frame is gone), all read from the `download::DownloadItem` that `CefDownloadItemImpl` wraps, as Electron's `DownloadItem` and `will-download` read them. | E13, E40 |
| [`cef/0006-request-context-net-log.patch`](patches/cef/0006-request-context-net-log.patch) | `chromium/src/cef` | Adds `CefRequestContext::StartNetLog`, `StopNetLog` and `IsNetLogging`, with the client callback `CefNetLogCallback`, as Electron's `electron_api_net_log.cc`: the profile's network context creates a `network::mojom::NetLogExporter`, the file is opened off the UI thread, and the JSON export starts with the content client's net log constants. One export per profile, kept as its user data; failures carry Electron's texts. | E42 |
| [`cef/0007-browser-process-handler-child-process-gone.patch`](patches/cef/0007-browser-process-handler-child-process-gone.patch) | `chromium/src/cef` | Adds `CefBrowserProcessHandler::OnChildProcessGone(type, status, exit_code, service_name, name, system_error_code)`. A `content::BrowserChildProcessObserver` that `CefContext` owns from context initialisation to shutdown reports `BrowserChildProcessCrashed`, `Killed` and `LaunchFailed` as Electron's `App::EmitChildProcessGone`: `GetProcessTypeNameInEnglish`, the status (as `CefBrowserContentsDelegate` maps it), the exit code, `metrics_name`, `name`, and on Windows a failed launch's `last_error`. | A31 |
| [`cef/0008-request-context-resolve-proxy.patch`](patches/cef/0008-request-context-resolve-proxy.patch) | `chromium/src/cef` | Adds `CefRequestContext::ResolveProxy` and the static `ResolveSystemProxy` (`cef_request_context_resolve_system_proxy`), with the client callback `CefResolveProxyCallback`, as Electron's `ResolveProxyHelper`: `NetworkContext::LookUpProxyForURL` on the profile's or the system network context, answered with `net::ProxyInfo::ToPacString`. Setting proxies needs no patch (the `proxy` preference). | A35, E8 |
| [`cef/0009-process-accessibility-mode.patch`](patches/cef/0009-process-accessibility-mode.patch) | `chromium/src/cef` | Adds the static `CefBrowserHost::GetProcessAccessibilityMode` and `SetProcessAccessibilityMode` (`ui::AXMode` flags) and `CefBrowserProcessHandler::OnAccessibilityModeChanged`: the getter reads `BrowserAccessibilityState::GetAccessibilityMode`, the setter keeps one `ScopedAccessibilityMode` for the process (Electron's `scoped_accessibility_mode_`), and a `ui::AXModeObserver` reports the flags Chromium adds by itself, not the setter's. `BUILD.gn` gains `//ui/accessibility:ax_base` and `//ui/accessibility/platform`. | A28 |
| [`cef/0010-browser-process-handler-before-render-process-launch.patch`](patches/cef/0010-browser-process-handler-before-render-process-launch.patch) | `chromium/src/cef` | Adds `CefBrowserProcessHandler::OnBeforeRenderProcessLaunch(command_line, request_context)`, called from `ChromeContentBrowserClientCef::AppendExtraCommandLineSwitches` after `OnBeforeChildProcessLaunch`, for renderers only, with an existing request context (`CefBrowserContext::GetAnyRequestContext`, preferring one without a handler) of the profile `RenderProcessHost::FromID(child_process_id)` serves: a client with one context per profile gets its own back. Chromium never shares a renderer between profiles, so a switch added there holds through every process reuse. | W13 (per session) |
| [`chromium/0011-renderer-no-zygote-per-process.patch`](patches/chromium/0011-renderer-no-zygote-per-process.patch) | `chromium/src` | On Linux, `RenderProcessHostImpl::Init` gives `RendererSandboxedProcessLauncherDelegate` `use_zygote = false` when the renderer's own command line has `--no-zygote`, and `GetZygote` then returns null, so the renderer is launched directly instead of forked from the sandboxed generic zygote: Electron's `support_mixed_sandbox_with_zygote.patch`. Without it, 0010's `--no-sandbox` on Linux changes nothing. Off `USE_ZYGOTE` builds it compiles to nothing. | W13 (Linux) |

CEF patches apply first, then Chromium patches (`patches/chromium/`, in `chromium/src`), each set in name order; the
numbers are shared, so their order is the order they were written in. Later patches change the context of earlier ones
in the same file (0008 follows 0006 in `request_context_impl.cc`, 0009 follows 0007 in `context.cc`, and 0010 follows 0007 and 0009
in `cef_browser_process_handler.h`), so
`apply-patches.sh` works out each patch's state on a copy of the files they touch: it takes the applied ones out newest
first, then puts them back in order.

Not patched: A23 (macOS: `ready` after AppKit's `applicationDidFinishLaunching:`, with `launchInfo`). CEF calls
`on_context_initialized` from `CefContext::OnContextInitialized` while `cef_initialize` runs, before `[NSApp run]`
finishes launching. The patch would, on macOS, check `[NSApp isRunning]` / the launch state there and, if launching is
not finished, observe `NSApplicationDidFinishLaunchingNotification` and call the handler from it, passing the
notification's `userInfo` (a new `on_context_initialized` variant or a `CefDictionaryValue` getter at `CEF_NEXT`). That
needs Objective-C++ in `context.cc` (or a new `.mm` file in `BUILD.gn`), and a check that nothing CEF starts itself
(the Chrome-style startup browser, `CefBrowserProcessHandler::GetDefaultClient`, the external message pump) relies on the
early call; Oriel would then raise `WillFinishLaunching`, the launch's `OpenFile` / `OpenUrl` and `Ready` in Electron's
order. Larger than 0007-0009, so left for its own branch.

Not patched: T6 (export the tray through Chromium's `dbus::Bus`). `dbus::Bus` has no public accessor for its
`DBusConnection`, and it runs on Chromium's D-Bus thread with its own dispatch, so exposing it means either a Chromium
patch that hands libdbus's connection out (Oriel's tray handlers would then run on Chromium's D-Bus thread and be torn
down by its shutdown) or a CEF API for exported objects, methods, properties and signals. Either is far more than the
other patches, for a difference only in the item's unique bus name: the process, and so the PID a host reads, is the same.

### Why a delegate method

Chromium already does everything once a widget asks for `kTranslucent`: `X11Window::Initialize` picks the ARGB visual
(`VisualManager::ChooseVisualForWindow`, which falls back to the opaque visual without a compositing manager), and
`DesktopNativeWidgetAura::UpdateWindowTransparency` clears the compositor. CEF only never sets the field. A delegate
query is how CEF asks for everything else about a new window (`IsFrameless`, `CanResize`,
`GetLinuxWindowProperties`), it is per window, and it needs no new type. The alternative, a field in
`cef_window_info_t` or `CefBrowserSettings`, would not reach `CreateWidget` for Views windows, and making
`GetColor` (`libcef/browser/context.cc`) accept translucent colours for windowed browsers would cover only the
first page colour: `setBackgroundColor` changes it later, which CEF has no call for. So the page background stays on
DevTools' `Emulation.setDefaultBackgroundColorOverride`, as on Windows and macOS.

The method is new API, so it follows CEF's API versioning: it is declared at `CEF_NEXT` (`added=next`), the version CEF
uses for API not yet released. Version 15400 and its hash do not change; libcef calls the method only when the client
configured the next version (`CEF_API_IS_ADDED(CEF_NEXT)`), because an older client's struct has no slot for it.

### How Oriel finds out

`CefRuntime.Load` asks libcef for the hash of version 999998 (`cef_api_hash`), which fixes the process's API version:

- stock libcef: next is the same API as 15400, so its hash equals 15400's; Oriel runs as before;
- patched libcef: the hash equals `CefPatchedApi.Hash*` ([`src/Oriel.Cef/CefPatchedApi.cs`](../../src/Oriel.Cef/CefPatchedApi.cs));
  `CefRuntime.IsPatched` is true, and `WindowDelegate` allocates `cef_window_delegate_next_t` (the stock struct plus
  `is_translucent`), since CEF refuses a `base.size` that is not the configured version's. The `cef_window_t` libcef
  hands out is then its next-version struct, `cef_window_next_t` (plus `set_event_listening_enabled`), and
  `cef_window_set_xdg_activation_token` is exported; `CefPatchedApi.SetEventListeningEnabled` and
  `SetXdgActivationToken` call them only then, and return false otherwise. Likewise the `cef_download_item_t` and
  `cef_request_context_t` it hands out are `cef_download_item_next_t` (0005's five getters appended) and
  `cef_request_context_next_t` (0006's `start_net_log`, `stop_net_log`, `is_net_logging`), and the client's
  `cef_net_log_callback_t` is hand-written too; `CefPatchedApi.ReadDownloadExtras` returns null and the net log calls
  return false (releasing the callback) without the patches. 0007, 0009 and 0010 append `on_child_process_gone`,
  `on_accessibility_mode_changed` and `on_before_render_process_launch` to the client's
  `cef_browser_process_handler_t`, so `BrowserProcessHandler` allocates
  `cef_browser_process_handler_next_t` when patched; 0008 appends `resolve_proxy` to `cef_request_context_next_t` and
  adds the client's `cef_resolve_proxy_callback_t`; the exports `cef_request_context_resolve_system_proxy` (0008) and
  `cef_browser_host_get_process_accessibility_mode` / `_set_` (0009) are called only when patched;
- any other hash: Oriel refuses to start, as for any API mismatch.

The generated bindings (`tools/cef-interop/generate.sh`) stay at 15400 and need no regenerating: the new member is
outside that version, so it is hand-written. The native subprocess keeps configuring 15400, which a patched libcef
still serves.

## Applying and building

```sh
# 1. Sync without building (automate-git.py; for Linux see ~/Developer/_scratch/cef-build-linux/build.sh):
build.sh --no-build
# 2. Apply into <download>/chromium/src; safe to repeat, --check only reports:
eng/cef/apply-patches.sh <download-dir>
# 3. Build: gclient_hook.py regenerates the translated files (include/capi, libcef_dll/ctocpp) and
#    cef_api_untracked.json, then ninja builds and make_distrib packs the minimal distribution:
build.sh --no-update --force-build
```

On Linux, a non-official build needs `use_partition_alloc_as_malloc=false enable_backup_ref_ptr_support=false` in
`GN_DEFINES`. CEF's `gn_args.py` adds them for official builds only, and without them libcef crashes in the .NET host,
which mixes glibc and PartitionAlloc heaps (CEF issues #3061, #3095). Build `--build-target=libcef`: the default
`cefclient` target needs GTK headers the arm64 sysroot lacks, and the minimal distribution doesn't use it. On x64,
add `use_sysroot=true` too: `gn_args.py` turns the sysroot off for local builds, and `gn gen` then fails on host
packages such as `libpipewire-0.3`.

Every non-official build also needs `disable_fieldtrial_testing_config=true` in `GN_DEFINES` (`gn_args.py` sets it
for official builds only). Without it Chromium applies `testing/variations/fieldtrial_testing_config.json`, which the
stock download and Electron don't: on Windows its `BrowserDynamicCodeDisabled` stops the .NET JIT in the browser
process, and `cef_initialize` fails with a CLR "Out of memory" (0xE0434352) or a stack overflow (0xC00000FD). Oriel
passes `--disable-field-trial-config` itself, which has the same effect on a libcef built without it.

Before updating the checkout (a new CEF commit or Chromium), run `eng/cef/apply-patches.sh <download-dir> --revert`:
automate-git runs `version_manager.py -a` after a Chromium change on a release branch, and that refuses `CEF_NEXT` in
the headers. A changed CEF checkout re-copies `chromium/src/cef` and drops the patches; apply them again.

Then check that the build's hashes are the ones Oriel expects:

```sh
python3 -c "import json; print(json.load(open('<download>/chromium/src/cef/cef_api_untracked.json'))['hashes']['999998'])"
```

`linux`, `mac` and `windows` must equal `HashLinux`, `HashMac` and `HashWindows` in `CefPatchedApi.cs`.

### When a patch changes a header in `include/`

The next-version hashes change. They are computed from the headers alone, for all three platforms, so they can be
recomputed without a build: copy `chromium/src/cef` (without `.git`) to a scratch `src/cef`, give the copy a
`src/third_party/llvm-build` symlink, `src/chrome/VERSION` and the four sandbox files `tools/version_manager.py` lists
(`SANDBOX_COMPAT_FILES`), `git init` and commit it, apply the patches, and run `python3 tools/version_manager.py -u`
in it. That is how the current values were made; without the patches the same run reproduces stock 15400's hashes.
For `apply-patches.sh` to apply the Chromium patches there too, copy the files they change
(`ui/gtk/os_settings_provider_gtk.cc`) from the checkout into the scratch `src`; they do not change the hashes.
Update `CefPatchedApi.cs` and add any new struct members to it.

## Running Oriel on the patched build

Unpack the minimal distribution (`<download>/chromium/src/cef/binary_distrib/cef_binary_*_minimal.tar.bz2`) and point
`ORIEL_CEF_DIR` at it: `eng/fetch-cef.sh`, and with it `eng/layout-linux.sh` and the native subprocess build, then
use it instead of the pinned stock download. In Docker:

```sh
docker build -t oriel-linux-dev -f eng/docker/linux-dev.Dockerfile eng/docker   # has xcompmgr
docker run --rm -v "$PWD":/src -v "$HOME/.cache/oriel/cef":/cef-cache -v <unpacked-distribution>:/cef-patched \
  -e ORIEL_CEF_DIR=/cef-patched --shm-size=1g oriel-linux-dev \
  /src/eng/docker/test-linux.sh samples/Oriel.WindowTest Oriel.WindowTest
```

WindowTest's `LinuxTransparencyAsync` runs its transparency checks only when `CefRuntime.IsPatched`; with stock CEF it
checks that `Transparent` is still ignored (W24). It starts `xcompmgr` itself after checking the fallback without one.
The same goes for `LinuxPatchedInputAsync` (0002, real X input through xdotool), `ThemeAsync`'s page checks after a
GTK theme change (0003) and `LinuxActivationTokenAsync` (0004, the next window's `_NET_STARTUP_ID`), and SessionTest's
`patched (0005)` download checks (gesture, initiator, `will-download` frame, ETag and Last-Modified) and `NetLogChecks`
(0006), and its `patched (0007)` (`chrome://gpucrash` raises `ChildProcessGone` for the GPU process), `patched (0008)`
(`ResolveProxyAsync` for fixed servers, a PAC script and `DIRECT`, and `app.SetProxyAsync` / `app.ResolveProxyAsync`
on the system context), `patched (0009)` (the accessibility features read back) and `patched (0010)` (with the app
sandboxed, an unsandboxed session's renderers at medium integrity on Windows, or without seccomp on Linux, while a
sandboxed session's run at untrusted or under seccomp) checks; with stock CEF SessionTest checks the blanks and the
`PlatformNotSupportedException` instead. On Linux the 0010 split needs `--enable-sandbox`.

### Wiring

- `BrowserWindow.ApplyLinuxEnabled` calls `LinuxCefPatches.SetWindowInputEnabled(_window, _enabled.Enabled)` first
  and returns when it is true: a patched libcef blocks input in Views (crossing events and keys included) and Oriel
  adds neither the X11 input cover nor its key filter, as Electron relies on `DisableEventListening` alone. With stock
  CEF the cover and the dropped keys stay.
- `DownloadSnapshot.Read` adds 0005's fields as `Extras`; `DownloadItem` answers `HasUserGesture`, `GetLastModifiedTime`,
  `GetETag` and `GetInitiatorOrigin` from them (false or empty with stock CEF), and `Session.OnBeforeDownload` turns the
  frame identifier into `WillDownloadEventArgs.Frame` through `WebContents.FromFrameToken`.
- `Session.NetLog` (`SessionNetLog`) and the static `NetLog` call 0006 on the UI thread; with stock CEF they throw
  `PlatformNotSupportedException` (Electron's own errors are kept for the patched path). Stock CEF's only net log is
  Chromium's `--log-net-log=<path>` switch, read when the network service starts: every profile, the whole run, with
  `--net-log-capture-mode` and `--net-log-max-size-mb`; CEF passes it through.
- `OrielApp.ChildProcessGone` is raised from 0007's callback with Electron's details (`ChildProcessGoneFrom`, the same
  reason strings as `render-process-gone`); never with stock CEF.
- `Session.SetProxyAsync` and `OrielApp.SetProxyAsync` write Electron's `ProxyConfigDictionary` choice to the profile's
  or the global `proxy` preference on stock CEF too, and reset it when a session or the app starts, as Electron keeps it
  in memory. `Session.ResolveProxyAsync` and `OrielApp.ResolveProxyAsync` call 0008, and throw
  `PlatformNotSupportedException` with stock CEF.
- `OrielApp`'s accessibility members use 0009's process-wide mode when patched (`AccessibilityModes` maps Electron's
  feature names); with stock CEF they keep the per-page `set_accessibility_state` path, and
  `SetAccessibilitySupportFeatures` throws `PlatformNotSupportedException`.
- `Session.OnBeforeRenderProcessLaunch` answers 0010's callback: a renderer whose profile an unsandboxed session
  (`SessionOptions.Sandbox = false`) shares, by `is_sharing_with`, gets `--no-sandbox`, and on Linux `--no-zygote`, as
  Electron's `AppendExtraCommandLineSwitches`. With stock CEF, creating such a session in a sandboxed app throws
  `PlatformNotSupportedException` rather than unsandboxing every renderer.
- `Tray.Linux.cs` `HandleItemCall` passes `ProvideXdgActivationToken`'s token to `LinuxCefPatches.ProvideActivationToken`
  (on the UI thread) and replies as before.

The `cef-build-linux` script builds with `is_official_build=false`. The macOS build with the same defines died at
startup on a PartitionAlloc DCHECK ([docs/todo.md](../../docs/todo.md#runtime-and-codecs)); if the Linux one does too,
rebuild with `is_official_build=true` or `dcheck_always_on=false`.
