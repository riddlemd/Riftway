# BaseWindow, View and WebContentsView

Design for Electron's `BaseWindow`, `View` and `WebContentsView` (v44) on CEF 154 Views. Status: phase 1 done; spike findings below.

## Constraints from CEF

- **One Chrome-style browser per window.** `cef_types_runtime.h`: "a Chrome style Window can host at most one Chrome
  style BrowserView but potentially multiple Alloy style BrowserViews". A `WebContentsView` returns
  `CEF_RUNTIME_STYLE_ALLOY` from `get_browser_runtime_style`; the window's own page stays Chrome style.
- **A view's browser closes its window by default.** A delayed `close_browser` runs the top-level window's `can_close`
  unless the Alloy-only `do_close` returns 1, after which Oriel must tear the view down itself.
- **Panels have a layout but no "keep my bounds" mode.** There is no way to clear a panel's layout, and the default
  (FillLayout, as Oriel's own windows show) resizes every child. Absolute child bounds come from re-applying each
  child's requested bounds in the panel delegate's `on_layout_changed` (plan A). Fallbacks: a box layout per child
  (plan B, no overlap) or window overlays with `CEF_DOCKING_MODE_CUSTOM` (plan C, window-level only).
- **No corner radius** on `cef_view_t`.
- **A browser is created when its view joins a widget** (`cef_browser_view_capi.h`). Whether it survives
  `remove_child_view` while referenced is not documented.

## Classes

- `public class BaseWindow`: most of today's `BrowserWindow` (window delegate, menu bar, bounds, state, taskbar,
  parent/child, `Closing`/`Closed`, `GetAllWindows`, `FromId`), plus `ContentView` / `SetContentView`.
- `public sealed class BrowserWindow : BaseWindow`: `WebContents`, popups, `ReadyToShow`, `PageTitleUpdated`,
  `LoadUrl`; joins the close flow through `private protected` hooks.
- Options: `BaseWindowOptions` record; `BrowserWindowOptions : BaseWindowOptions` adds `WebPreferences`.
- `public class View` (a `cef_panel_t` with a panel delegate) and `public sealed class WebContentsView : View`
  (a `cef_browser_view_t`, Alloy style; it cannot have children).
- Inheritance over composition: composition needs ~90 forwarding members and loses `BaseWindow`-typed parents.

## Mapping

- `AddChildView(view, index?)`: removes the view from a previous parent; an existing child is moved to the top with
  `reorder_child_view`; else `add_child_view_at`. `Children` mirrors the panel in a managed list.
- `SetBounds` stores the requested bounds and applies them; the parent's `on_layout_changed` re-applies them. Null
  requested bounds means "fill" (the window's own page, `ContentView.Children[0]`).
- Windows/Linux menu bar: the window is a vertical box of `[menu bar, content view (flex 1)]`.
- `BoundsChanged` from `on_layout_changed`, only on change. `SetBackgroundColor` re-applied in `on_theme_changed`.

## WebContents

A `WebContentsHost` (or WebContents itself) takes from `BrowserWindow`: the client handler, browser view delegate,
web preference application and popup adoption. Popups belong to the opener `WebContents`. `FromWebContents` stays
an identity check (null for a view's page, as Electron). IPC needs no change: it is per `WebContents`.

## Lifetime

- Each `View` holds one reference to its native view; every call that passes a view into CEF adds one first.
- Removing a `WebContentsView` keeps its page (Electron) if CEF allows; else DRIFT.
- `view.WebContents.Close()`: `try_close_browser`; `do_close` returns 1 for view-hosted browsers and removes the view.
- Closing a window destroys its views' pages (Electron keeps them for `webContents.close()`): DRIFT unless the spike
  shows a detach path. Quit force-closes detached views' browsers first.

## Phases

0. Spike: panel default layout; bounds surviving resizes; an Alloy view beside the Chrome page (input, focus);
   the browser after `remove_child_view`; an Alloy opener's popup style; a panel drawn over a browser on macOS.
1. `WebContentsView`; `BrowserWindow.ContentView` with `AddChildView`/`RemoveChildView`, `SetBounds`/`GetBounds`,
   `SetVisible`.
2. Full `View`: nesting, reorder, `Children`, `SetBackgroundColor`, `BoundsChanged`; `WebContentsView` options.
3. `BaseWindow` extraction; `BrowserWindow : BaseWindow`; `SetContentView`; `BaseWindowOptions`.
4. Popups from view pages, `WebContents.Close()` for views, quit, parity docs.

## Expected drift

`setBorderRadius` no-op; view pages are Alloy style (context menu, Chrome commands, font prefs differ); no children
under a `WebContentsView`; no `new WebContentsView({ webContents })`; views don't outlive their window; possibly
"removing a view destroys its page" and "a plain View can't draw over a page on macOS".

## Spike findings (2026-10-04, macOS; phase 1 checks on Linux too)

- Windows and panels default to a fill layout. Plan A holds: re-applying each child's bounds in the panel's
  `on_layout_changed` keeps them through resizes; Linux applies the page's new size asynchronously (DRIFT V12).
- An Alloy browser view renders beside the window's Chrome-style page, and the Chrome-style page works nested in a
  panel (the full WindowTest passes with it inside `ContentView`).
- `remove_child_view` keeps the browser while Oriel holds a reference; adding the view back shows the same page.
- A plain panel added over a browser view does not show on macOS (DRIFT V7).
- Every view passed into CEF consumes a reference: `add_child_view`, `remove_child_view`, `reorder_child_view` and
  `is_same` included. Missing one freed the view (PartitionAlloc's 0xCD pattern in the crash).
- A browser and its browser view hold each other: removing the view from a live window and releasing it does not
  destroy the page, even after `do_close` returned 1 or a second `close_browser`. CEF deletes a view only with the
  window it is in, so closing a view's page moves the view into a hidden window and closes that.

## Electron-fidelity pass (2026-10-05)

- A `WebContentsView` is a panel holding its page (a browser view, below and filling it) and any child views, as
  Electron's `WebContentsView` is a `View`. Closing the page removes only the page; the view stays.
- A window's views outlive it: when the window's own page is destroyed (its window is torn down next, without asking
  `can_close` again), the views are removed from `ContentView`, so their pages live on until closed or quit.
- An Alloy page's unhandled keys reach the window delegate, so Alt and mnemonics work from view pages with no handler.
- Chromium lays a resized panel out later; Linux may never, so Oriel lays out a view's own panel when it resizes it.
- `addChildView` clamps an index past the end, as Electron (`std::min`).
