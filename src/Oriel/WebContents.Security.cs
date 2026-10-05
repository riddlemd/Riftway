using System.ComponentModel;
using Oriel.Cef.Handlers;

namespace Oriel;

/// <summary>Arguments for <see cref="WebContents.CertificateError"/> and <see cref="OrielApp.CertificateError"/>. Set
/// <see cref="CancelEventArgs.Cancel"/> (Electron's <c>event.preventDefault()</c>) and call <see cref="Callback"/> to
/// decide; otherwise the certificate is refused.</summary>
public sealed class CertificateErrorEventArgs : CancelEventArgs
{
    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal CertificateErrorEventArgs(WebContents webContents, string url, string error, Certificate? certificate)
    {
        WebContents = webContents;
        Url = url;
        Error = error;
        Certificate = certificate;
    }

    /// <summary>The page that hit the error.</summary>
    public WebContents WebContents { get; }

    /// <summary>The URL whose certificate failed.</summary>
    public string Url { get; }

    /// <summary>The error, e.g. <c>net::ERR_CERT_AUTHORITY_INVALID</c>.</summary>
    public string Error { get; }

    /// <summary>The server's certificate.</summary>
    public Certificate? Certificate { get; }

    /// <summary>Whether the main frame's navigation hit it: always, since CEF reports only those (drift E26).</summary>
    public bool IsMainFrame => true;

    /// <summary>Trusts the certificate, or refuses it; may be called later. Electron's <c>callback(isTrusted)</c>.</summary>
    public void Callback(bool isTrusted) => _answer.TrySetResult(isTrusted);

    internal Task<bool> Answer => Cancel ? _answer.Task : Task.FromResult(false);
}

public sealed partial class WebContents
{
    /// <summary>The page's navigation hit an invalid certificate. Electron's <c>certificate-error</c>; the app's
    /// <see cref="OrielApp.CertificateError"/> is raised after it, with the same arguments.</summary>
    public event EventHandler<CertificateErrorEventArgs>? CertificateError;

    // UI thread.
    private Task<bool> OnCertificateError(CertificateErrorInfo info)
    {
        var args = new CertificateErrorEventArgs(this, info.Url, WebRequest.ErrorName(info.Error), Certificate.FromChain(info.CertificateChain));
        Raise(CertificateError, args, "certificate-error");
        Raise(OrielApp.CurrentOrNull is { } app ? app.RaiseCertificateError : null, args, "certificate-error");
        return args.Answer;
    }

    private void Raise<T>(EventHandler<T>? handler, T args, string name)
    {
        try { handler?.Invoke(this, args); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] A {name} handler failed: {e}"); }
    }
}
