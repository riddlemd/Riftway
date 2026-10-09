using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>The shell's recent documents for this app.</summary>
internal interface IRecentDocumentsBackend
{
    void Add(string path);
    void Clear();
    IReadOnlyList<string> List();
}

public sealed partial class OrielApp
{
    /// <summary>Adds <paramref name="path"/> to the recent documents: under the AppUserModelID on Windows, which the jump
    /// list's Recent category shows; NSDocumentController's list on macOS, which the Dock menu and File › Open Recent
    /// show. Electron's <c>app.addRecentDocument()</c>: paths that cannot be parsed are ignored; no-op on Linux.</summary>
    public void AddRecentDocument(string path) => AddRecentDocument(CreateRecentDocumentsBackend(), path);

    /// <summary>Empties the recent documents: on Windows <c>SHAddToRecentDocs(SHARD_APPIDINFO, NULL)</c>, which clears the
    /// user's whole Recent list, not only this app's; on macOS the app's own list. Electron's
    /// <c>app.clearRecentDocuments()</c>; no-op on Linux.</summary>
    public void ClearRecentDocuments() => CreateRecentDocumentsBackend()?.Clear();

    /// <summary>Windows: the targets of the shortcuts in the user's Recent folder, from every app; macOS: the app's own
    /// recent documents, newest first. Electron's <c>app.getRecentDocuments()</c>; empty on Linux.</summary>
    public IReadOnlyList<string> GetRecentDocuments() => CreateRecentDocumentsBackend()?.List() ?? [];

    internal static IRecentDocumentsBackend? CreateRecentDocumentsBackend()
        => OperatingSystem.IsWindows() ? new WindowsRecentDocuments() : OperatingSystem.IsMacOS() ? new MacRecentDocuments() : null;

    internal static void AddRecentDocument(IRecentDocumentsBackend? backend, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        backend?.Add(path);
    }

    // NSDocumentController is main-thread only; before Run the caller is on the main thread.
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private sealed class MacRecentDocuments : IRecentDocumentsBackend
    {
        public void Add(string path)
        {
            if (!OnMain(() => MacApp.NoteRecentDocument(path))) Console.Error.WriteLine($"[Oriel] Failed to convert file path {path} to NSURL");
        }

        public void Clear() => OnMain(() =>
        {
            MacApp.ClearRecentDocuments();
            return true;
        });

        public IReadOnlyList<string> List() => OnMain(MacApp.RecentDocuments);

        private static T OnMain<T>(Func<T> func) => CurrentOrNull is { IsReady: true } ? Oriel.Cef.CefThread.InvokeOnUi(func) : func();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class WindowsRecentDocuments : IRecentDocumentsBackend
    {
        public void Add(string path) => WithCom(() => WindowsJumpList.AddRecentDocument(path, WindowsAppRegistry.AppUserModelId()));

        public void Clear() => WindowsJumpList.ClearRecentDocuments();

        public IReadOnlyList<string> List()
        {
            IReadOnlyList<string> documents = [];
            WithCom(() => documents = WindowsJumpList.RecentDocuments());
            return documents;
        }

        private static void WithCom(Action action)
        {
            var uninitialize = WindowsJumpList.InitializeCom();
            try { action(); }
            finally
            {
                if (uninitialize) WindowsJumpList.UninitializeCom();
            }
        }
    }
}
