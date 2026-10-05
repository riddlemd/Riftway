using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>The shell's recent documents for this app.</summary>
internal interface IRecentDocumentsBackend
{
    void Add(string path);
    void Clear();
}

public sealed partial class OrielApp
{
    /// <summary>Adds <paramref name="path"/> to the recent documents, which the jump list's Recent category shows.
    /// Electron's <c>app.addRecentDocument()</c>. Paths the shell cannot parse are ignored; no-op on other OSes.</summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> is null or empty.</exception>
    public void AddRecentDocument(string path) => AddRecentDocument(CreateRecentDocumentsBackend(), path);

    /// <summary>Empties this app's recent and frequent documents. Electron's <c>app.clearRecentDocuments()</c>;
    /// no-op on other OSes.</summary>
    public void ClearRecentDocuments() => CreateRecentDocumentsBackend()?.Clear();

    internal static IRecentDocumentsBackend? CreateRecentDocumentsBackend() => OperatingSystem.IsWindows() ? new WindowsRecentDocuments() : null;

    internal static void AddRecentDocument(IRecentDocumentsBackend? backend, string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        backend?.Add(path);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class WindowsRecentDocuments : IRecentDocumentsBackend
    {
        public void Add(string path) => WithCom(appId => WindowsJumpList.AddRecentDocument(path, appId));

        public void Clear() => WithCom(appId => WindowsJumpList.RemoveAllDestinations(appId));

        private static void WithCom(Action<string?> action)
        {
            var uninitialize = WindowsJumpList.InitializeCom();
            try { action(WindowsJumpList.ExplicitAppUserModelId()); }
            finally
            {
                if (uninitialize) WindowsJumpList.UninitializeCom();
            }
        }
    }
}
