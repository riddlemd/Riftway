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

/// <summary>A server or proxy asking for credentials; Electron's <c>authInfo</c>.</summary>
/// <param name="IsProxy">Whether a proxy asks, rather than the server.</param>
/// <param name="Scheme">The authentication scheme, lower case (e.g. <c>basic</c>).</param>
/// <param name="Host">The host asking.</param>
/// <param name="Port">Its port.</param>
/// <param name="Realm">The realm.</param>
public sealed record AuthInfo(bool IsProxy, string Scheme, string Host, int Port, string Realm);

/// <summary>Arguments for <see cref="WebContents.Login"/>, <see cref="OrielApp.Login"/> and
/// <see cref="ClientRequest.Login"/>. For the page and app events, set <see cref="CancelEventArgs.Cancel"/> (Electron's
/// <c>event.preventDefault()</c>) and call <see cref="Callback"/> with credentials; otherwise the login is cancelled,
/// as Electron's default. A <see cref="ClientRequest.Login"/> handler just calls <see cref="Callback"/>, as Electron's
/// has no event to prevent.</summary>
public sealed class LoginEventArgs : CancelEventArgs
{
    private readonly TaskCompletionSource<(string User, string Password)?> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal LoginEventArgs(WebContents? webContents, string url, AuthInfo authInfo)
    {
        WebContents = webContents;
        Url = url;
        AuthInfo = authInfo;
    }

    /// <summary>The page whose request was challenged; null for a <see cref="ClientRequest"/>.</summary>
    public WebContents? WebContents { get; }

    /// <summary>The URL that asked; Electron's <c>authenticationResponseDetails.url</c>.</summary>
    public string Url { get; }

    /// <summary>Who asks, and how.</summary>
    public AuthInfo AuthInfo { get; }

    /// <summary>Answers with credentials, or with none (null) to cancel; may be called later. Electron's
    /// <c>callback(username, password)</c>.</summary>
    public void Callback(string? username = null, string? password = null)
        => _answer.TrySetResult(username is null ? null : (username, password ?? ""));

    internal Task<(string User, string Password)?> Answer => Cancel ? _answer.Task : Task.FromResult<(string User, string Password)?>(null);

    // ClientRequest's answer: Electron's request login ignores preventDefault.
    internal Task<(string User, string Password)?> Answered => _answer.Task;
}

/// <summary>Arguments for <see cref="WebContents.SelectClientCertificate"/> and <see cref="OrielApp.SelectClientCertificate"/>.
/// Set <see cref="CancelEventArgs.Cancel"/> (Electron's <c>event.preventDefault()</c>) and call <see cref="Callback"/> to
/// choose; otherwise the first certificate is used, as Electron's default.</summary>
public sealed class SelectClientCertificateEventArgs : CancelEventArgs
{
    private readonly TaskCompletionSource<Certificate?> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal SelectClientCertificateEventArgs(WebContents webContents, string url, IReadOnlyList<Certificate> certificateList)
    {
        WebContents = webContents;
        Url = url;
        CertificateList = certificateList;
    }

    /// <summary>The page whose request asked.</summary>
    public WebContents WebContents { get; }

    /// <summary>The server, as <c>host:port</c>, as Electron gives it.</summary>
    public string Url { get; }

    /// <summary>The certificates the server accepts.</summary>
    public IReadOnlyList<Certificate> CertificateList { get; }

    /// <summary>Uses <paramref name="certificate"/> (one of <see cref="CertificateList"/>), or none when null; may be
    /// called later. Electron's <c>callback(certificate)</c>.</summary>
    public void Callback(Certificate? certificate = null) => _answer.TrySetResult(certificate);

    internal Task<int?> Answer => (Cancel ? _answer.Task : Task.FromResult(CertificateList.FirstOrDefault())).ContinueWith(t =>
    {
        var chosen = t.Result;
        for (var i = 0; i < CertificateList.Count; i++)
            if (chosen is not null && (ReferenceEquals(CertificateList[i], chosen) || CertificateList[i].Fingerprint == chosen.Fingerprint)) return (int?)i;
        return null;
    }, TaskScheduler.Default);
}

public sealed partial class WebContents
{
    /// <summary>A server asks the page for a client certificate. Electron's <c>select-client-certificate</c>; the app's
    /// <see cref="OrielApp.SelectClientCertificate"/> is raised after it, with the same arguments.</summary>
    public event EventHandler<SelectClientCertificateEventArgs>? SelectClientCertificate;

    // UI thread.
    private Task<int?> OnSelectClientCertificate(ClientCertificateRequest request)
    {
        var certificates = request.Certificates.Select(chain => Certificate.FromChain(chain)).OfType<Certificate>().ToList();
        var args = new SelectClientCertificateEventArgs(this, $"{request.Host}:{request.Port}", certificates);
        Raise(SelectClientCertificate, args, "select-client-certificate");
        Raise(OrielApp.CurrentOrNull is { } app ? app.RaiseSelectClientCertificate : null, args, "select-client-certificate");
        return args.Answer;
    }

    /// <summary>The page's navigation hit an invalid certificate. Electron's <c>certificate-error</c>; the app's
    /// <see cref="OrielApp.CertificateError"/> is raised after it, with the same arguments.</summary>
    public event EventHandler<CertificateErrorEventArgs>? CertificateError;

    /// <summary>A server or proxy asks for credentials. Electron's <c>login</c>; the app's <see cref="OrielApp.Login"/>
    /// is raised after it, with the same arguments.</summary>
    public event EventHandler<LoginEventArgs>? Login;

    // UI thread.
    private Task<bool> OnCertificateError(CertificateErrorInfo info)
    {
        var args = new CertificateErrorEventArgs(this, info.Url, WebRequest.ErrorName(info.Error), Certificate.FromChain(info.CertificateChain));
        Raise(CertificateError, args, "certificate-error");
        Raise(OrielApp.CurrentOrNull is { } app ? app.RaiseCertificateError : null, args, "certificate-error");
        return args.Answer;
    }

    // IO thread: Electron's listeners run on the main thread.
    private Task<(string User, string Password)?> OnLogin(AuthChallenge challenge)
    {
        var asked = new TaskCompletionSource<Task<(string User, string Password)?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        Oriel.Cef.CefThread.RunOnUi(() =>
        {
            var args = new LoginEventArgs(this, challenge.OriginUrl, new AuthInfo(challenge.IsProxy, challenge.Scheme, challenge.Host, challenge.Port, challenge.Realm));
            Raise(Login, args, "login");
            Raise(OrielApp.CurrentOrNull is { } app ? app.RaiseLogin : null, args, "login");
            asked.SetResult(args.Answer);
        });
        return asked.Task.Unwrap();
    }

    private void Raise<T>(EventHandler<T>? handler, T args, string name)
    {
        try { handler?.Invoke(this, args); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] A {name} handler failed: {e}"); }
    }
}
