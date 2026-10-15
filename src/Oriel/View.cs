using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>A rectangle of a window that can hold other views. Electron's <c>View</c>.</summary>
/// <remarks>Children are placed by their own bounds, in their parent's coordinates; a new view has empty bounds until
/// <see cref="SetBounds"/>. Methods may be called from any thread once the app is ready.</remarks>
public unsafe class View
{
    private readonly PanelDelegate? _delegate;
    private readonly List<View> _children = [];
    private cef_panel_t* _panel;
    // A WebContentsView's page: below the children, filling the view, not one of Children.
    private cef_view_t* _page;
    private Rectangle _requested;
    private uint? _backgroundColor;
    private cef_rect_t _lastRaised;

    /// <summary>Creates an empty view. Electron's <c>new View()</c>.</summary>
    /// <exception cref="InvalidOperationException">The app is not ready.</exception>
    public View()
    {
        RequireReady();
        _delegate = new PanelDelegate
        {
            // A child is first sized to its whole parent by the parent's fill layout, then put back by PlaceChildren, so
            // only the placement counts for it; a view with no parent is placed by its window's layout.
            LayoutChanged = (view, bounds) =>
            {
                if (Parent is null) RaiseIfMoved(view->get_bounds(view));
                PlaceChildren(bounds);
            },
            ThemeChanged = ApplyBackgroundColor,
        };
        CefThread.InvokeOnUi(() =>
        {
            NativeObject.AddRef(_delegate.Native);
            _panel = CefNative.cef_panel_create(_delegate.Native);
            NativeView = &_panel->@base;
            return true;
        });
    }

    /// <summary>The native panel, holding one reference of this object's.</summary>
    internal cef_view_t* NativeView { get; private set; }

    /// <summary>UI thread: drops this object's reference to the panel, for a view no window will ever hold.</summary>
    internal void ReleaseNative()
    {
        if (NativeView is null) return;
        NativeObject.Release(NativeView);
        NativeView = null;
        _panel = null;
    }

    // Native child indexes count the page first.
    private int Offset => _page is null ? 0 : 1;

    /// <summary>UI thread: puts a page below the children; takes over the caller's reference to it.</summary>
    private protected void SetPage(cef_view_t* page)
    {
        _page = page;
        NativeObject.AddRef(page);
        _panel->add_child_view_at(_panel, page, 0);
    }

    /// <summary>UI thread: takes the page out of the view; returns the reference the view held.</summary>
    private protected cef_view_t* TakePage()
    {
        var page = _page;
        if (page is null) return null;
        _page = null;
        NativeObject.AddRef(page);
        _panel->remove_child_view(_panel, page);
        return page;
    }

    /// <summary>The view this one is a child of.</summary>
    internal View? Parent { get; private set; }

    /// <summary>Whether this view always fills its parent instead of keeping its own bounds: a window's own page and its
    /// content view.</summary>
    internal bool FillsParent { get; set; }

    /// <summary>UI thread: a view that filled its parent keeps the bounds it has, as a view placed there would.</summary>
    internal void StopFilling()
    {
        if (!FillsParent) return;
        FillsParent = false;
        if (NativeView is not null) _requested = BrowserWindow.ToRectangle(NativeView->get_bounds(NativeView));
    }

    /// <summary>The view's bounds changed, by <see cref="SetBounds"/> or a layout. Raised on the UI thread.
    /// Electron's <c>bounds-changed</c> event.</summary>
    public event EventHandler? BoundsChanged;

    /// <summary>The child views, bottom first. Electron's <c>view.children</c>.</summary>
    public IReadOnlyList<View> Children
    {
        get { lock (_children) return [.. _children]; }
    }

    /// <summary>Adds <paramref name="view"/> above the others, or at <paramref name="index"/> (past the end means the end,
    /// as Electron); a view already here moves, and a view in another parent leaves it. Electron's <c>view.addChildView()</c>.</summary>
    /// <exception cref="InvalidOperationException"><paramref name="view"/> is this view or contains it, or a
    /// <see cref="WebContentsView"/> whose page was closed before it ever started.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    public void AddChildView(View view, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (CefThread.InvokeOnUi(() => view.IsDestroyedChild)) throw new InvalidOperationException("Can't add a destroyed child view to a parent view");
        for (var ancestor = this; ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor == view) throw new InvalidOperationException("A view cannot be added to itself or to a view inside it.");
        CefThread.InvokeOnUi(() =>
        {
            int count;
            lock (_children) count = _children.Count - (view.Parent == this ? 1 : 0);
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            var at = Math.Min(index ?? count, count);
            if (view.Parent is { } previous && previous != this)
            {
                previous.RemoveChild(view);
                // Only a window fills its content view; anywhere else a view keeps its own bounds.
                view.StopFilling();
            }
            else view.Parent?.RemoveChild(view);
            InsertChild(view, at);
            return true;
        });
    }

    /// <summary>Removes a child view; anything else is ignored. Electron's <c>view.removeChildView()</c>.</summary>
    public void RemoveChildView(View view)
    {
        ArgumentNullException.ThrowIfNull(view);
        CefThread.InvokeOnUi(() =>
        {
            if (view.Parent == this) RemoveChild(view);
            return true;
        });
    }

    /// <summary>Places the view, in its parent's coordinates. Electron's <c>view.setBounds()</c>.</summary>
    public void SetBounds(Rectangle bounds) => CefThread.InvokeOnUi(() =>
    {
        _requested = bounds;
        FillsParent = false;
        ApplyBounds();
        return true;
    });

    /// <summary>The view's bounds in its parent's coordinates. Electron's <c>view.getBounds()</c>.</summary>
    public Rectangle GetBounds() => CefThread.InvokeOnUi(() =>
        Parent is not null && NativeView is not null ? BrowserWindow.ToRectangle(NativeView->get_bounds(NativeView)) : _requested);

    /// <summary>Shows or hides the view and its children. Electron's <c>view.setVisible()</c>.</summary>
    public void SetVisible(bool visible) => CefThread.InvokeOnUi(() =>
    {
        if (NativeView is not null) NativeView->set_visible(NativeView, visible ? 1 : 0);
        return true;
    });

    /// <summary>Whether the view is set to be shown. Electron's <c>view.getVisible()</c>.</summary>
    public bool GetVisible() => CefThread.InvokeOnUi(() => NativeView is not null && NativeView->is_visible(NativeView) != 0);

    /// <summary>Fills the view's background, e.g. <c>#RRGGBB</c> or <c>#AARRGGBB</c>; a <see cref="WebContentsView"/>'s page takes the
    /// colour as its own background too. Electron's <c>view.setBackgroundColor()</c>.</summary>
    /// <exception cref="FormatException">The colour is not a hex colour.</exception>
    public void SetBackgroundColor(string color)
    {
        ArgumentNullException.ThrowIfNull(color);
        var parsed = BrowserWindow.ParseColor(color);
        CefThread.InvokeOnUi(() =>
        {
            _backgroundColor = parsed;
            ApplyBackgroundColor();
            OnBackgroundColorSet(parsed);
            return true;
        });
    }

    /// <summary>UI thread: <see cref="SetBackgroundColor"/> was called with <paramref name="argb"/>.</summary>
    private protected virtual void OnBackgroundColorSet(uint argb) { }

    // UI thread: Electron refuses to add a view that is no longer usable.
    private protected virtual bool IsDestroyedChild => false;

    /// <summary>UI thread: the page below the children, if any.</summary>
    private protected cef_view_t* Page => _page;

    /// <summary>UI thread: an ARGB background, kept through theme changes as <see cref="SetBackgroundColor"/> keeps one.</summary>
    internal void SetBackgroundArgb(uint argb)
    {
        _backgroundColor = argb;
        ApplyBackgroundColor();
    }

    /// <summary>The background colour CEF has for the view, as ARGB. For tests.</summary>
    internal uint NativeBackgroundColor() => CefThread.InvokeOnUi(() => NativeView is null ? 0 : NativeView->get_background_color(NativeView));

    /// <summary>Whether CEF's children are exactly <see cref="Children"/>, in order. For tests.</summary>
    internal bool NativeChildrenMatch() => CefThread.InvokeOnUi(() =>
    {
        if (_panel is null) return true;
        View[] children;
        lock (_children) children = [.. _children];
        if ((int)_panel->get_child_view_count(_panel) != children.Length + Offset) return false;
        for (var i = 0; i < children.Length; i++)
        {
            var native = _panel->get_child_view_at(_panel, i + Offset);
            var other = children[i].NativeView;
            if (other is not null) NativeObject.AddRef(other);
            var same = native is not null && other is not null && native->is_same(native, other) != 0;
            if (native is null && other is not null) NativeObject.Release(other);
            NativeObject.Release(native);
            if (!same) return false;
        }
        return true;
    });

    internal static void RequireReady()
    {
        if (OrielApp.CurrentOrNull is not { IsReady: true }) throw new InvalidOperationException("Create views after OrielApp.Ready.");
    }

    // UI thread. Also used for a window's own page, which goes first.
    internal void InsertChild(View view, int index)
    {
        lock (_children) _children.Insert(index, view);
        view.Parent = this;
        NativeObject.AddRef(view.NativeView);
        _panel->add_child_view_at(_panel, view.NativeView, index + Offset);
        view.ApplyBounds();
    }

    // UI thread.
    internal void RemoveChild(View view)
    {
        lock (_children) _children.Remove(view);
        view.Parent = null;
        // Every view passed into CEF carries a reference, removal included.
        NativeObject.AddRef(view.NativeView);
        _panel->remove_child_view(_panel, view.NativeView);
    }

    // UI thread. The panel's fill layout has just sized every child to the whole panel, which is right for the page;
    // this puts each child view back.
    private void PlaceChildren(cef_rect_t bounds)
    {
        View[] children;
        lock (_children) children = [.. _children];
        foreach (var child in children) child.ApplyBounds(bounds);
    }

    private void ApplyBounds()
    {
        var parent = Parent is null ? null : Parent.NativeView;
        if (parent is null) return;
        ApplyBounds(parent->get_bounds(parent));
    }

    private void ApplyBounds(cef_rect_t parentBounds)
    {
        if (NativeView is null) return;
        var target = FillsParent
            ? new cef_rect_t { width = parentBounds.width, height = parentBounds.height }
            : new cef_rect_t { x = _requested.X, y = _requested.Y, width = _requested.Width, height = _requested.Height };
        var current = NativeView->get_bounds(NativeView);
        // Setting bounds lays the view out again; only change what moved. A fill layout may already have.
        if (current.x != target.x || current.y != target.y || current.width != target.width || current.height != target.height)
        {
            NativeView->set_bounds(NativeView, &target);
            // Chromium lays a resized view out later; Linux may never, leaving its page at the parent's full size.
            _panel->layout(_panel);
        }
        RaiseIfMoved(target);
    }

    private void RaiseIfMoved(cef_rect_t bounds)
    {
        if (bounds.x == _lastRaised.x && bounds.y == _lastRaised.y && bounds.width == _lastRaised.width && bounds.height == _lastRaised.height) return;
        _lastRaised = bounds;
        BoundsChanged?.Invoke(this, EventArgs.Empty);
    }

    // CEF gives a panel the theme's opaque colour whenever the theme applies (joining a window included); Electron's views
    // have no background unless set, and a themed one would hide a transparent window and what lies under a view.
    private void ApplyBackgroundColor()
    {
        if (NativeView is not null) NativeView->set_background_color(NativeView, _backgroundColor ?? 0);
    }
}
