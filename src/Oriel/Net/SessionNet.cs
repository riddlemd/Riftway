namespace Oriel;

public sealed partial class Session
{
    /// <summary>Sends a request from this session, as the page's <c>fetch()</c> would but over the session's network
    /// stack and protocol handlers. Electron's <c>ses.fetch()</c>.</summary>
    /// <exception cref="NetException">The request failed (Chromium's <c>net::ERR_…</c>), or Electron refused it.</exception>
    public Task<FetchResponse> FetchAsync(string input, FetchInit? init = null) => NetFetch.FetchAsync(this, input, init);
}
