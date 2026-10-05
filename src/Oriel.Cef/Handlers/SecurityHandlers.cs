using Oriel.Cef.Interop;

namespace Oriel.Cef.Handlers;

/// <summary>A navigation's invalid certificate: the error, the URL, and the certificate's DER bytes then its issuers'.</summary>
public sealed record CertificateErrorInfo(cef_errorcode_t Error, string Url, IReadOnlyList<byte[]> CertificateChain);

/// <summary>The part of <c>cef_request_handler_t</c> that asks the app about invalid certificates.</summary>
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

    private static IReadOnlyList<byte[]> ChainOf(cef_sslinfo_t* ssl)
    {
        var certificate = ssl->get_x509_certificate(ssl);
        if (certificate is null) return [];
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
        NativeObject.Release(certificate);
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
