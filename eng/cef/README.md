# Oriel's CEF patches

Oriel runs on stock CEF. Where stock CEF cannot give Electron's behaviour, a patch here adds the missing piece to CEF
itself, and Oriel uses it only when the loaded libcef has it. With stock CEF the matching DRIFT row stays true.

Base: CEF `a03e714` (branch 8037, CEF 154, Chromium 154.0.8037.94), the version in [`eng/cef.json`](../cef.json).

## Patches

| Patch | Applies in | What it does | DRIFT |
|---|---|---|---|
| [`cef/0001-window-delegate-is-translucent.patch`](patches/cef/0001-window-delegate-is-translucent.patch) | `chromium/src/cef` | Adds `CefWindowDelegate::IsTranslucent`. `CefWindowView::CreateWidget` reads it into `views::Widget::InitParams::opacity` (`kTranslucent`), as Electron's `NativeWindowViews` does for `transparent: true`. On Linux X11, Chromium then gives the window a 32-bit ARGB visual while a compositing manager runs; on macOS the `NSWindow` is created non-opaque. | W24 (Linux; macOS's `InitParams::opacity`) |

There are no Chromium patches yet; they would go in `patches/chromium/` and apply in `chromium/src`.

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
  `is_translucent`), since CEF refuses a `base.size` that is not the configured version's;
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

The `cef-build-linux` script builds with `is_official_build=false`. The macOS build with the same defines died at
startup on a PartitionAlloc DCHECK ([docs/todo.md](../../docs/todo.md#runtime-and-codecs)); if the Linux one does too,
rebuild with `is_official_build=true` or `dcheck_always_on=false`.
