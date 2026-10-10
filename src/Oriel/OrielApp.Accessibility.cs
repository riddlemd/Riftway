using Oriel.Cef;

namespace Oriel;

public sealed partial class OrielApp
{
    // Stock CEF: whether the app asked for complete mode, applied page by page.
    private volatile bool _accessibilityRequested;

    // Patch 0009: the flags of Electron's scoped_accessibility_mode_, 0 when it has none. UI thread.
    private int _accessibilityScoped;

    internal bool AccessibilityRequested => _accessibilityRequested;

    /// <summary>Accessibility support changed; the argument is <see cref="IsAccessibilitySupportEnabled"/>. Raised by
    /// <see cref="SetAccessibilitySupportEnabled"/> and <see cref="SetAccessibilitySupportFeatures"/>, and, with Oriel's
    /// CEF patch 0009, when an assistive technology makes Chromium turn accessibility on. Electron's
    /// <c>accessibility-support-changed</c>.</summary>
    /// <remarks>With stock CEF a screen reader starting is not seen (drift A28).</remarks>
    public event EventHandler<bool>? AccessibilitySupportChanged;

    /// <summary>Whether Chromium's complete accessibility mode (native APIs, web contents, inline text boxes, extended
    /// properties) is on for the process, whoever turned it on. Electron's <c>app.isAccessibilitySupportEnabled()</c>.</summary>
    /// <remarks>With stock CEF, only whether <see cref="SetAccessibilitySupportEnabled"/> turned it on (drift A28).</remarks>
    public bool IsAccessibilitySupportEnabled()
        => CefRuntime.IsPatched ? AccessibilityModes.IsComplete(ProcessAccessibilityMode()) : _accessibilityRequested;

    /// <summary>The features of the process's accessibility mode: <c>nativeAPIs</c>, <c>webContents</c>,
    /// <c>inlineTextBoxes</c>, <c>extendedProperties</c>, <c>html</c>, <c>labelImages</c>, <c>pdfPrinting</c> and
    /// <c>screenReader</c>. Electron's <c>app.getAccessibilitySupportFeatures()</c>.</summary>
    /// <remarks>With stock CEF, the complete mode's four when <see cref="SetAccessibilitySupportEnabled"/> turned it on,
    /// else none (drift A28).</remarks>
    public IReadOnlyList<string> GetAccessibilitySupportFeatures() => AccessibilityModes.Names(CefRuntime.IsPatched
        ? ProcessAccessibilityMode()
        : _accessibilityRequested ? AccessibilityModes.Complete : 0);

    /// <summary>Turns on exactly <paramref name="features"/> (names as <see cref="GetAccessibilitySupportFeatures"/>) as
    /// the app's own accessibility mode, in place of what this or <see cref="SetAccessibilitySupportEnabled"/> set
    /// before; none turns it off. What assistive technology turned on stays. Electron's
    /// <c>app.setAccessibilitySupportFeatures()</c>.</summary>
    /// <exception cref="InvalidOperationException">The app is not ready.</exception>
    /// <exception cref="ArgumentException">A name is not a feature.</exception>
    /// <exception cref="PlatformNotSupportedException">Stock CEF: it sets only on or off per page (drift A28).</exception>
    public void SetAccessibilitySupportFeatures(IEnumerable<string> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (!IsReady) throw new InvalidOperationException("app.setAccessibilitySupportFeatures() can only be called after app is ready");
        var mode = AccessibilityModes.Parse(features);
        if (!CefRuntime.IsPatched)
            throw new PlatformNotSupportedException("Accessibility features need Oriel's CEF patch 0009.");
        CefThread.InvokeOnUi(() =>
        {
            _accessibilityScoped = mode;
            return CefPatchedApi.SetProcessAccessibilityMode(mode);
        });
        AccessibilitySupportChanged?.Invoke(this, IsAccessibilitySupportEnabled());
    }

    /// <summary>Turns Chromium's complete accessibility mode on or off as the app's own mode; pages made later get it
    /// too. Enabling keeps a mode <see cref="SetAccessibilitySupportFeatures"/> set. Electron's
    /// <c>app.setAccessibilitySupportEnabled()</c>.</summary>
    /// <remarks>With stock CEF it sets each page's mode outright, so turning it off also turns off what a screen reader
    /// turned on (drift A28).</remarks>
    /// <exception cref="InvalidOperationException">The app is not ready.</exception>
    public void SetAccessibilitySupportEnabled(bool enabled)
    {
        if (!IsReady) throw new InvalidOperationException("app.setAccessibilitySupportEnabled() can only be called after app is ready");
        if (CefRuntime.IsPatched)
        {
            CefThread.InvokeOnUi(() =>
            {
                var next = AccessibilityModes.ScopedAfterEnable(_accessibilityScoped, enabled);
                if (next != _accessibilityScoped) CefPatchedApi.SetProcessAccessibilityMode(next);
                _accessibilityScoped = next;
                return true;
            });
        }
        else
        {
            var changed = AccessibilityTransition(_accessibilityRequested, enabled);
            _accessibilityRequested = enabled;
            if (changed is { } apply)
                foreach (var page in WebContents.GetAllWebContents()) page.SetAccessibilityState(apply);
        }
        AccessibilitySupportChanged?.Invoke(this, IsAccessibilitySupportEnabled());
    }

    // Electron keeps one scoped mode: enabling twice does not stack, and the event fires on every call.
    internal static bool? AccessibilityTransition(bool current, bool requested) => current == requested ? null : requested;

    /// <summary><see cref="IsAccessibilitySupportEnabled"/> and <see cref="SetAccessibilitySupportEnabled"/>. Electron's
    /// <c>app.accessibilitySupportEnabled</c>.</summary>
    public bool AccessibilitySupportEnabled
    {
        get => IsAccessibilitySupportEnabled();
        set => SetAccessibilitySupportEnabled(value);
    }

    private static int ProcessAccessibilityMode() => CefThread.InvokeOnUi(() => CefPatchedApi.GetProcessAccessibilityMode() ?? 0);

    private void OnAccessibilityModeChanged(int mode)
    {
        try { AccessibilitySupportChanged?.Invoke(this, AccessibilityModes.IsComplete(mode)); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] An app accessibility-support-changed handler failed: {e}"); }
    }
}

/// <summary>Electron's accessibility feature names over Chromium's <c>ui::AXMode</c> flags.</summary>
internal static class AccessibilityModes
{
    // ui::kAXModeComplete: native APIs, web contents, inline text boxes, extended properties.
    internal const int Complete = 1 | 2 | 4 | 8;

    // In Electron's getAccessibilitySupportFeatures order.
    private static readonly (string Name, int Flag)[] s_features =
    [
        ("nativeAPIs", 1),
        ("webContents", 2),
        ("inlineTextBoxes", 4),
        ("extendedProperties", 8),
        ("html", 16),
        ("labelImages", 64),
        ("pdfPrinting", 128),
        ("screenReader", 1024),
    ];

    internal static bool IsComplete(int mode) => (mode & Complete) == Complete;

    internal static string[] Names(int mode) => [.. s_features.Where(f => (mode & f.Flag) != 0).Select(f => f.Name)];

    internal static int Parse(IEnumerable<string> features)
    {
        var mode = 0;
        foreach (var name in features)
        {
            var match = Array.FindIndex(s_features, f => f.Name == name);
            if (match < 0) throw new ArgumentException("Unknown accessibility feature: " + name, nameof(features));
            mode |= s_features[match].Flag;
        }
        return mode;
    }

    // App::SetAccessibilitySupportEnabled: off drops the scoper; on makes a complete one only when there is none.
    internal static int ScopedAfterEnable(int current, bool enabled) => !enabled ? 0 : current == 0 ? Complete : current;
}
