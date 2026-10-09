namespace Oriel;

/// <summary>Whether a window takes input: the app's <c>SetEnabled</c> and the count of shown modal children, as
/// Electron's <c>NativeWindowViews</c> keeps them (<c>is_enabled_</c>, <c>num_modal_children_</c>).</summary>
internal sealed class WindowEnabledState
{
    private bool _requested = true;
    private uint _modalChildren;

    /// <summary>What the app last asked for through <c>SetEnabled</c>.</summary>
    public bool Requested => _requested;

    /// <summary>The state in force, which <c>isEnabled()</c> reports.</summary>
    public bool Enabled { get; private set; } = true;

    /// <summary>Linux: whether libcef applies <see cref="Enabled"/> in Views itself (CEF patch 0002), with no X11 input
    /// cover.</summary>
    public bool BlockedInViews { get; set; }

    /// <summary>Linux: whether the keyboard handlers must drop keys, which pass the stock-CEF input cover.</summary>
    public bool DropsKeys => !Enabled && !BlockedInViews;

    /// <summary>Returns whether <see cref="Enabled"/> changed.</summary>
    public bool SetEnabled(bool enable)
    {
        _requested = enable;
        return Update();
    }

    /// <summary>Returns whether <see cref="Enabled"/> changed.</summary>
    public bool IncrementChildModals()
    {
        _modalChildren++;
        return Update();
    }

    /// <summary>Returns whether <see cref="Enabled"/> changed.</summary>
    public bool DecrementChildModals()
    {
        // Electron decrements on both hide and destroy, so a hidden modal that then closes must not underflow.
        if (_modalChildren > 0) _modalChildren--;
        return Update();
    }

    private bool Update()
    {
        var should = _requested && _modalChildren == 0;
        if (should == Enabled) return false;
        Enabled = should;
        return true;
    }
}
