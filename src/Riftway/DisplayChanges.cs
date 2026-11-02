namespace Riftway;

/// <summary>What happened to one display between two snapshots.</summary>
internal enum DisplayChangeKind
{
    /// <summary>Electron's <c>display-removed</c>.</summary>
    Removed,

    /// <summary>Electron's <c>display-added</c>.</summary>
    Added,

    /// <summary>Electron's <c>display-metrics-changed</c>.</summary>
    MetricsChanged,
}

/// <summary>One event a display change raises; <paramref name="Metrics"/> is <see cref="DisplayMetrics.None"/> for a
/// colour-space-only change, which Electron reports with an empty <c>changedMetrics</c>.</summary>
internal readonly record struct DisplayChange(DisplayChangeKind Kind, Display Display, DisplayMetrics Metrics);

/// <summary>Chromium's <c>DisplayChangeNotifier::NotifyDisplaysChanged</c> (ui/display/display_change_notifier.cc).</summary>
internal static class DisplayChanges
{
    /// <summary>The events in Chromium's order, matching displays by id: every removed display (old order), then each
    /// current display in turn as added or changed. <paramref name="compareRefreshRate"/> also counts a refresh rate change
    /// (macOS, where <c>ScreenMac</c> reports one).</summary>
    internal static List<DisplayChange> Between(IReadOnlyList<Display> before, IReadOnlyList<Display> after, bool compareRefreshRate = false)
    {
        var changes = new List<DisplayChange>();
        foreach (var old in before)
            if (!after.Any(d => d.Id == old.Id)) changes.Add(new(DisplayChangeKind.Removed, old, DisplayMetrics.None));
        foreach (var current in after)
        {
            var old = before.FirstOrDefault(d => d.Id == current.Id);
            if (old is null)
            {
                changes.Add(new(DisplayChangeKind.Added, current, DisplayMetrics.None));
                continue;
            }
            var metrics = (old.Bounds != current.Bounds ? DisplayMetrics.Bounds : 0)
                | (old.Rotation != current.Rotation ? DisplayMetrics.Rotation : 0)
                | (old.WorkArea != current.WorkArea ? DisplayMetrics.WorkArea : 0)
                | (old.ScaleFactor != current.ScaleFactor ? DisplayMetrics.ScaleFactor : 0);
            // Electron's changedMetrics names only the four above, but still emits for these.
            var colorSpaces = old.ColorSpace != current.ColorSpace || old.ColorDepth != current.ColorDepth || old.ColorSpacesKey != current.ColorSpacesKey
                || old.HdrMaxLuminanceRelative != current.HdrMaxLuminanceRelative;
            // base::IsApproximatelyEqual on Chromium's floats, within Display::kRefreshRateEpsilon.
            var refreshRate = compareRefreshRate && MathF.Abs((float)current.DisplayFrequency - (float)old.DisplayFrequency) > 0.01f;
            if (metrics != DisplayMetrics.None || colorSpaces || refreshRate) changes.Add(new(DisplayChangeKind.MetricsChanged, current, metrics));
        }
        return changes;
    }
}
