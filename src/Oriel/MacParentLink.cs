using System.Runtime.Versioning;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>The AppKit calls <see cref="MacParentLink"/> makes, on windows named by their content view; a seam for tests.</summary>
internal interface IMacChildWindows
{
    /// <summary>Whether <paramref name="child"/>'s NSWindow has <paramref name="parent"/>'s as its <c>parentWindow</c>.</summary>
    bool IsChildOf(nint child, nint parent);

    void AddChildWindow(nint parent, nint child);

    void RemoveChildWindow(nint parent, nint child);

    long GetLevel(nint window);

    void SetLevel(nint window, long level);

    void OrderOut(nint window);
}

[SupportedOSPlatform("macos")]
internal sealed class MacChildWindows : IMacChildWindows
{
    internal static readonly MacChildWindows Instance = new();

    public bool IsChildOf(nint child, nint parent) => MacWindow.ParentWindow(child) is var p && p != 0 && p == MacWindow.WindowOf(parent);

    public void AddChildWindow(nint parent, nint child) => MacWindow.AddChildWindow(parent, child);

    public void RemoveChildWindow(nint parent, nint child) => MacWindow.RemoveChildWindow(parent, child);

    public long GetLevel(nint window) => MacWindow.GetLevel(window);

    public void SetLevel(nint window, long level) => MacWindow.SetLevel(window, level);

    public void OrderOut(nint window) => MacWindow.OrderOut(window);
}

/// <summary>One window's place in Electron's macOS parent links (<c>NativeWindowMac</c>: <c>InternalSetParentWindow</c>,
/// <c>AttachChildren</c>, <c>DetachChildren</c>, <c>RemoveChildFromParentWindow</c>). A child is attached as an AppKit
/// child window only while it wants to be visible. UI thread only.</summary>
/// <remarks>The handle is the window's content view, or 0 before it exists or once it is gone. A modal window is a
/// sheet and takes no part, as in Electron.</remarks>
internal sealed class MacParentLink(IMacChildWindows native, Func<nint> handle, bool isModal)
{
    private readonly List<MacParentLink> _children = [];

    internal MacParentLink? Parent { get; private set; }

    internal IReadOnlyList<MacParentLink> Children => _children;

    /// <summary>Electron's <c>wants_to_be_visible</c>: set by show, cleared by hide.</summary>
    internal bool WantsToBeVisible { get; private set; }

    /// <summary>Electron's <c>InternalSetParentWindow</c>; <paramref name="attach"/> adds the AppKit child window now.</summary>
    internal void SetParent(MacParentLink? newParent, bool attach)
    {
        if (isModal) return;
        if (attach && newParent is not null && native.IsChildOf(handle(), newParent.Handle)) return;
        RemoveFromParent();
        if (newParent is not null)
        {
            newParent._children.Add(this);
            if (attach) newParent.AttachChildren();
        }
        Parent = newParent;
    }

    /// <summary>Electron's <c>Show</c>: re-attaches to the parent before the window is ordered in.</summary>
    internal void Show()
    {
        WantsToBeVisible = true;
        if (Parent is { } parent) SetParent(parent, attach: true);
    }

    /// <summary>Electron's <c>Hide</c>: orders the children out and detaches from the parent before the window is
    /// ordered out, or the parent could order it back in.</summary>
    internal void Hide()
    {
        WantsToBeVisible = false;
        foreach (var child in _children) native.OrderOut(child.Handle);
        if (Parent is { } parent) SetParent(parent, attach: false);
    }

    /// <summary>Electron's <c>windowWillMiniaturize:</c> <c>DetachChildren</c>: the children ordered out first, so the
    /// minimize does not take them along.</summary>
    internal void WillMiniaturize()
    {
        foreach (var child in _children) native.OrderOut(child.Handle);
    }

    /// <summary><c>windowDidMiniaturize:</c>: no longer wanting to be visible.</summary>
    internal void DidMiniaturize() => WantsToBeVisible = false;

    /// <summary><c>windowDidDeminiaturize:</c>: wanting to be visible again, with the children that want it attached.</summary>
    internal void DidDeminiaturize()
    {
        WantsToBeVisible = true;
        AttachChildren();
    }

    /// <summary>Drops the links when the window is gone, without AppKit calls: its NSWindow no longer exists.</summary>
    internal void Forget()
    {
        Parent?._children.Remove(this);
        Parent = null;
        foreach (var child in _children) child.Parent = null;
        _children.Clear();
    }

    private nint Handle => handle();

    // Electron's RemoveChildFromParentWindow.
    private void RemoveFromParent()
    {
        if (Parent is null || isModal) return;
        Parent._children.Remove(this);
        native.RemoveChildWindow(Parent.Handle, Handle);
        Parent = null;
    }

    private void AttachChildren()
    {
        foreach (var child in _children)
        {
            if (!child.WantsToBeVisible || native.IsChildOf(child.Handle, Handle)) continue;
            // Adding a child window resets the parent's level.
            var level = native.GetLevel(Handle);
            native.AddChildWindow(Handle, child.Handle);
            native.SetLevel(Handle, level);
        }
    }
}
