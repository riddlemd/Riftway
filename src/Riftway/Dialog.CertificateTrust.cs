using Riftway.Cef;
using Riftway.Cef.Platform;

// The panel and the store run behind OperatingSystem checks the analyzer cannot follow into the lambdas.
#pragma warning disable CA1416

namespace Riftway;

public static partial class Dialog
{
    // Shows the trust panel: the window's content view or 0, the DER chain, the message and the end; replaceable so tests
    // never open it.
    internal static Action<nint, IReadOnlyList<byte[]>, string, Action> ShowTrustPanel { get; set; } = MacCertificateTrust.Show;

    // Adds a DER certificate to the current user's Root store (Windows asks the user first); replaceable so tests never
    // touch the real store.
    internal static Func<byte[], bool> AddToTrustedRootStore { get; set; } = WindowsCertificateTrust.AddToTrustedRootStore;

    /// <summary>Shows the certificate and asks the user whether to trust it. Electron's
    /// <c>dialog.showCertificateTrustDialog()</c>.</summary>
    /// <remarks>
    /// <para>macOS: a sheet on <paramref name="window"/>, or a panel of its own without one; the task completes when the
    /// user closes it.</para>
    /// <para>Windows, as Electron: no dialog of Riftway's own, and neither the window nor the message is used. When the
    /// certificate's chain (with its issuer, if set) fails only because its root is untrusted, the certificate is added
    /// to the current user's Root store, for which Windows shows its own security warning; the task completes when that
    /// ends, whether the user agreed or not. Any other chain, trusted or broken, leaves the store alone.</para>
    /// <para>Electron then tells Chromium the trust store changed, which CEF cannot, so Chromium may keep verification
    /// results it cached until the app restarts (drift MB7).</para>
    /// </remarks>
    /// <exception cref="ArgumentException">The certificate's data, or its issuer's, is not one certificate.</exception>
    /// <exception cref="PlatformNotSupportedException">On Linux, where Electron has no such dialog either.</exception>
    public static Task ShowCertificateTrustDialogAsync(BaseWindow? window, CertificateTrustDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Certificate);
        ArgumentNullException.ThrowIfNull(options.Message);
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("showCertificateTrustDialog is only available on macOS and Windows.");
        var chain = CertificateTrust.Chain(options.Certificate);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Show()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // certificate_trust_win.cc: the only cases Electron handles; its promise resolves either way.
                    if (WindowsCertificateTrust.ChainErrorStatus(chain) is { } status && WindowsCertificateTrust.ShouldAdd(status))
                        AddToTrustedRootStore(chain[0]);
                    done.TrySetResult();
                    return;
                }
                var view = window is null || window.IsDestroyed ? 0 : window.GetNativeWindowHandle();
                ShowTrustPanel(view, chain, options.Message, () => done.TrySetResult());
            }
            catch (Exception e) { done.TrySetException(e); }
        }
        if (RiftwayApp.CurrentOrNull is { IsReady: true }) CefThread.RunOnUi(Show);
        else Show();
        return done.Task;
    }

    /// <inheritdoc cref="ShowCertificateTrustDialogAsync(BaseWindow?, CertificateTrustDialogOptions)"/>
    public static Task ShowCertificateTrustDialogAsync(CertificateTrustDialogOptions options) => ShowCertificateTrustDialogAsync(null, options);
}
