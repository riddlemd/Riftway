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
    /// <summary>Adds <paramref name="path"/> to the recent documents under the AppUserModelID, which the jump list's
    /// Recent category shows. Electron's <c>app.addRecentDocument()</c>: paths the shell cannot parse are ignored; no-op
    /// on other OSes.</summary>
    public void AddRecentDocument(string path) => AddRecentDocument(CreateRecentDocumentsBackend(), path);

    /// <summary>Empties the recent documents: <c>SHAddToRecentDocs(SHARD_APPIDINFO, NULL)</c>, which clears the user's
    /// whole Recent list, not only this app's. Electron's <c>app.clearRecentDocuments()</c>; no-op on other OSes.</summary>
    public void ClearRecentDocuments() => CreateRecentDocumentsBackend()?.Clear();

    /// <summary>The targets of the shortcuts in the user's Recent folder, from every app. Electron's
    /// <c>app.getRecentDocuments()</c>; empty on other OSes.</summary>
    public IReadOnlyList<string> GetRecentDocuments() => CreateRecentDocumentsBackend()?.List() ?? [];

    internal static IRecentDocumentsBackend? CreateRecentDocumentsBackend() => OperatingSystem.IsWindows() ? new WindowsRecentDocuments() : null;

    internal static void AddRecentDocument(IRecentDocumentsBackend? backend, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        backend?.Add(path);
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
