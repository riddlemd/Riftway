namespace Riftway;

public static partial class Screen
{
    // One event per call: Electron emits each from its own posted task.
    private static void Raise(DisplayChange change)
    {
        EventHandler<DisplayEventArgs>? onAdded, onRemoved;
        EventHandler<DisplayMetricsChangedEventArgs>? onChanged;
        lock (s_gate) (onAdded, onRemoved, onChanged) = (s_added, s_removed, s_metricsChanged);
        switch (change.Kind)
        {
            case DisplayChangeKind.Removed: onRemoved?.Invoke(null, new DisplayEventArgs(change.Display)); break;
            case DisplayChangeKind.Added: onAdded?.Invoke(null, new DisplayEventArgs(change.Display)); break;
            default: onChanged?.Invoke(null, new DisplayMetricsChangedEventArgs(change.Display, change.Metrics)); break;
        }
    }
}
