using Riftway.Cef.Interop;

namespace Riftway.Cef.Handlers;

/// <summary>A navigation's invalid certificate: the error, the URL, and the certificate's DER bytes then its issuers'.</summary>
public sealed record CertificateErrorInfo(cef_errorcode_t Error, string Url, IReadOnlyList<byte[]> CertificateChain);

/// <summary>A frame about to navigate: the target, whether a server redirected there, CEF's transition type, and the frame's
/// name and current URL then its ancestors', the main frame last.</summary>
public sealed record BrowseRequest(string Url, bool IsRedirect, bool UserGesture, uint Transition, IReadOnlyList<(string Name, string Url)> FramePath);

/// <summary>A server asking for a client certificate: the server, and each candidate's DER bytes then its issuers'.</summary>
public sealed record ClientCertificateRequest(bool IsProxy, string Host, int Port, IReadOnlyList<IReadOnlyList<byte[]>> Certificates);

/// <summary>A server or proxy asking for credentials.</summary>
public sealed record AuthChallenge(string OriginUrl, bool IsProxy, string Host, int Port, string Realm, string Scheme);

/// <summary>The parts of <c>cef_request_handler_t</c> that ask the app about security: invalid certificates and logins.</summary>
public static unsafe class SecurityHandlers
{
    /// <summary>UI thread: CEF's <c>on_certificate_error</c>. The task's result trusts the certificate.</summary>
    public static int OnCertificateError(Func<CertificateErrorInfo, Task<bool>>? decide, cef_browser_t* browser, cef_errorcode_t error,
        cef_string_utf16_t* url, cef_sslinfo_t* ssl, cef_callback_t* callback)
    {
        var info = new CertificateErrorInfo(error, CefString.Read(url), ChainOf(ssl));
        NativeObject.Release(ssl);
        NativeObject.Release(browser);
        // No callback: the error cannot be overridden.
        if (decide is null || callback is null)
        {
            if (callback is not null) NativeObject.Release(callback);
            return 0;
        }
        var answer = Guard.Run(() => decide(info), Task.FromResult(false));
        answer.ContinueWith(t => CefThread.RunOnUi(() =>
        {
            if (t.IsCompletedSuccessfully && t.Result) callback->cont(callback);
            else callback->cancel(callback);
            NativeObject.Release(callback);
        }), TaskScheduler.Default);
        return 1;
    }

    /// <summary>IO thread: CEF's <c>get_auth_credentials</c>. The task's result is the user name and password, or null
    /// to cancel.</summary>
    public static int GetAuthCredentials(Func<AuthChallenge, Task<(string User, string Password)?>>? decide, cef_browser_t* browser,
        cef_string_utf16_t* originUrl, int isProxy, cef_string_utf16_t* host, int port, cef_string_utf16_t* realm, cef_string_utf16_t* scheme,
        cef_auth_callback_t* callback)
    {
        var challenge = new AuthChallenge(CefString.Read(originUrl), isProxy != 0, CefString.Read(host), port, CefString.Read(realm), CefString.Read(scheme));
        NativeObject.Release(browser);
        if (decide is null)
        {
            NativeObject.Release(callback);
            return 0;
        }
        var answer = Guard.Run(() => decide(challenge), Task.FromResult<(string User, string Password)?>(null));
        answer.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result is { } credentials)
            {
                cef_string_utf16_t user = default, password = default;
                CefString.Set(&user, credentials.User);
                CefString.Set(&password, credentials.Password);
                callback->cont(callback, &user, &password);
                CefString.Clear(&user);
                CefString.Clear(&password);
            }
            else callback->cancel(callback);
            NativeObject.Release(callback);
        }, TaskScheduler.Default);
        return 1;
    }

    /// <summary>UI thread: CEF's <c>on_select_client_certificate</c>. The task's result is the index of the certificate
    /// to use, or null for none.</summary>
    public static int OnSelectClientCertificate(Func<ClientCertificateRequest, Task<int?>>? decide, cef_browser_t* browser, int isProxy,
        cef_string_utf16_t* host, int port, nuint count, cef_x509_certificate_t** certificates, cef_select_client_certificate_callback_t* callback)
    {
        NativeObject.Release(browser);
        // Each certificate arrives with a reference, kept until the answer; the chosen one gets another for CEF.
        var natives = new nint[(int)count];
        var chains = new List<IReadOnlyList<byte[]>>((int)count);
        for (var i = 0; i < (int)count; i++)
        {
            natives[i] = (nint)certificates[i];
            chains.Add(ChainOfCertificate(certificates[i]));
        }
        var request = new ClientCertificateRequest(isProxy != 0, CefString.Read(host), port, chains);
        var answer = decide is null ? Task.FromResult<int?>(count > 0 ? 0 : null) : Guard.Run(() => decide(request), Task.FromResult<int?>(null));
        answer.ContinueWith(t => CefThread.RunOnUi(() =>
        {
            var chosen = t.IsCompletedSuccessfully && t.Result is { } index && index >= 0 && index < natives.Length ? (cef_x509_certificate_t*)natives[index] : null;
            if (chosen is not null) NativeObject.AddRef(chosen);
            callback->select(callback, chosen);
            NativeObject.Release(callback);
            foreach (var native in natives) NativeObject.Release((cef_x509_certificate_t*)native);
        }), TaskScheduler.Default);
        return 1;
    }

    private static IReadOnlyList<byte[]> ChainOf(cef_sslinfo_t* ssl)
    {
        var certificate = ssl->get_x509_certificate(ssl);
        if (certificate is null) return [];
        var chain = ChainOfCertificate(certificate);
        NativeObject.Release(certificate);
        return chain;
    }

    // The certificate's DER bytes then its issuers'; the caller keeps its reference.
    private static IReadOnlyList<byte[]> ChainOfCertificate(cef_x509_certificate_t* certificate)
    {
        var chain = new List<byte[]> { Bytes(certificate->get_derencoded(certificate)) };
        var count = certificate->get_issuer_chain_size(certificate);
        if (count > 0)
        {
            var issuers = new cef_binary_value_t*[(int)count];
            fixed (cef_binary_value_t** list = issuers)
            {
                var size = count;
                certificate->get_derencoded_issuer_chain(certificate, &size, list);
                for (nuint i = 0; i < size; i++) chain.Add(Bytes(list[i]));
            }
        }
        return chain;
    }

    // Takes the value's reference.
    private static byte[] Bytes(cef_binary_value_t* value)
    {
        if (value is null) return [];
        var bytes = new byte[(int)value->get_size(value)];
        fixed (byte* data = bytes) value->get_data(value, data, (nuint)bytes.Length, 0);
        NativeObject.Release(value);
        return bytes;
    }
}
