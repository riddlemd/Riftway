using Riftway.Cef;
using Riftway.Cef.Platform;

// The panel runs behind an OperatingSystem.IsMacOS() check the analyzer cannot follow into the lambda.
#pragma warning disable CA1416

namespace Riftway;

public static partial class Dialog
{
    // Shows the trust panel: the window's content view or 0, the DER chain, the message and the end; replaceable so tests
    // never open it.
    internal static Action<nint, IReadOnlyList<byte[]>, string, Action> ShowTrustPanel { get; set; } = MacCertificateTrust.Show;

    /// <summary>Shows the certificate and asks the user whether to trust it: a sheet on <paramref name="window"/>, or a
    /// panel of its own without one. The task completes when the user closes it. Electron's
    /// <c>dialog.showCertificateTrustDialog()</c>.</summary>
    /// <remarks>macOS. Electron then tells Chromium the trust store changed, which CEF cannot, so Chromium may keep
    /// verification results it cached until the app restarts (drift MB7).</remarks>
    /// <exception cref="ArgumentException">The certificate's data, or its issuer's, is not one certificate.</exception>
    /// <exception cref="PlatformNotSupportedException">Not on macOS; Windows is not done yet.</exception>
    public static Task ShowCertificateTrustDialogAsync(BaseWindow? window, CertificateTrustDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Certificate);
        ArgumentNullException.ThrowIfNull(options.Message);
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("showCertificateTrustDialog is macOS only here; Windows is not done yet.");
        var chain = CertificateTrust.Chain(options.Certificate);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Show()
        {
            var view = window is null || window.IsDestroyed ? 0 : window.GetNativeWindowHandle();
            try { ShowTrustPanel(view, chain, options.Message, () => done.TrySetResult()); }
            catch (Exception e) { done.TrySetException(e); }
        }
        if (RiftwayApp.CurrentOrNull is { IsReady: true }) CefThread.RunOnUi(Show);
        else Show();
        return done.Task;
    }

    /// <inheritdoc cref="ShowCertificateTrustDialogAsync(BaseWindow?, CertificateTrustDialogOptions)"/>
    public static Task ShowCertificateTrustDialogAsync(CertificateTrustDialogOptions options) => ShowCertificateTrustDialogAsync(null, options);
}
