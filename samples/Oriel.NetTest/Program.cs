using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Oriel;

// End-to-end check of Oriel's net module and printing against a local raw HTTP server, driven from a worker
// thread. The exit code is the number of failures (-1 when the run never finishes).
// Linux, from the repo root (checks that need Windows print SKIP):
//   docker run --rm -v "$PWD":/src -v "$HOME/.cache/oriel/cef":/cef-cache --shm-size=1g oriel-linux-dev \
//     /src/eng/docker/test-linux.sh samples/Oriel.NetTest Oriel.NetTest
var app = OrielApp.Create("Oriel.NetTest");
var failures = -1;
var results = new List<string>();

void Check(string name, bool pass, object? detail = null)
{
    var line = $"{(pass ? "PASS" : "FAIL")}  {name}  [{detail}]";
    lock (results) results.Add(line);
    Console.WriteLine(line);
}

void Skip(string name, string reason)
{
    var line = $"SKIP  {name}  [{reason}]";
    lock (results) results.Add(line);
    Console.WriteLine(line);
}

// A section that throws fails as one check instead of ending the run.
async Task Section(string name, Func<Task> body)
{
    try { await body(); }
    catch (Exception e) { Check($"{name}: section completed", false, e.GetType().Name + ": " + e.Message); }
}

static async Task<Exception?> Throws(Func<Task> act)
{
    try { await act(); return null; }
    catch (Exception e) { return e; }
}

static Exception? ThrowsNow(Action act)
{
    try { act(); return null; }
    catch (Exception e) { return e; }
}

Protocol.RegisterSchemesAsPrivileged(new PrivilegedScheme("oriel-net", new SchemePrivileges
{
    Standard = true,
    Secure = true,
    SupportFetchApi = true,
    CorsEnabled = true,
}));

using var server = new TestServer();
var partialRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
const string PrintPage = """
    <!doctype html><html><head><title>print test</title><style>
    body { background: rgb(255, 0, 0); margin: 0; font: 16px sans-serif; }
    .page { height: 9in; } .page + .page { break-before: page; }
    </style></head><body>
    <div class="page">PAGE-ONE</div><div class="page">PAGE-TWO</div><div class="page">PAGE-THREE</div>
    </body></html>
    """;
const string CssPage = "<!doctype html><style>@page { size: 4in 4in; margin: 0 } body { margin: 0 }</style><p>css page</p>";

server.Route("/hello", (_, s) => TestServer.Respond(s, 200, "OK",
    [("Content-Type", "text/plain"), ("X-Custom", "a"), ("X-Multi", "1"), ("X-Multi", "2"), ("Set-Cookie", "a=1"), ("Set-Cookie", "b=2"),
        ("Content-Type", "text/ignored")],
    Encoding.UTF8.GetBytes("Hello World!")));
server.Route("/notfound", (_, s) => TestServer.Text(s, "missing", 404, "Not Found"));
server.Route("/echo", (r, s) => TestServer.Respond(s, 200, "OK", [("X-Method", r.Method)], r.Body));
server.Route("/headers", (r, s) => TestServer.Text(s, string.Join("\n", r.Headers.Select(h => $"{h.Key}: {h.Value}"))));
server.Route("/chunked", async (_, s) =>
{
    await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"));
    foreach (var part in new[] { "first-", "second-", "third" })
    {
        await s.WriteAsync(Encoding.ASCII.GetBytes($"{part.Length:x}\r\n{part}\r\n"));
        await s.FlushAsync();
        await Task.Delay(150);
    }
    await s.WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n"));
});
server.Route("/redirect302", (_, s) => TestServer.Respond(s, 302, "Found", [("Location", "/hello")], null));
server.Route("/chain", (_, s) => TestServer.Respond(s, 302, "Found", [("Location", "/chain2")], null));
server.Route("/chain2", (_, s) => TestServer.Respond(s, 301, "Moved Permanently", [("Location", "/hello")], null));
server.Route("/post302", (_, s) => TestServer.Respond(s, 302, "Found", [("Location", "/echo")], null));
server.Route("/post307", (_, s) => TestServer.Respond(s, 307, "Temporary Redirect", [("Location", "/echo")], null));
server.Route("/tofile", (_, s) => TestServer.Respond(s, 302, "Found", [("Location", "file:///C:/")], null));
server.Route("/auth", (r, s) => r.Header("Authorization") is { } auth
    ? TestServer.Text(s, "ok:" + auth)
    : TestServer.Text(s, "unauthenticated", 401, "Unauthorized", ("WWW-Authenticate", "Basic realm=\"Foo\"")));
server.Route("/partial", async (_, s) =>
{
    await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 4096\r\nConnection: close\r\n\r\n"));
    await s.WriteAsync(new byte[1024]);
    await s.FlushAsync();
    await Task.WhenAny(partialRelease.Task, Task.Delay(8000));
});
server.Route("/drop", (_, _) => Task.CompletedTask);
server.Route("/dropmid", async (_, s) =>
{
    await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2048\r\nConnection: close\r\n\r\n"));
    await s.WriteAsync(new byte[1024]);
    await s.FlushAsync();
    await Task.Delay(50);
});
server.Route("/loop", (_, s) => TestServer.Respond(s, 302, "Found", [("Location", "/loop")], null));
server.Route("/slow", async (_, s) =>
{
    await Task.Delay(3000);
    await TestServer.Text(s, "slow");
});
server.Route("/setcookie", (_, s) => TestServer.Text(s, "set", 200, "OK", ("Set-Cookie", "netc=1; Path=/")));
server.Route("/cookies", (r, s) => TestServer.Text(s, r.Header("Cookie") ?? ""));
server.Route("/nocontent", (_, s) => TestServer.Respond(s, 204, "No Content", null, null));
server.Route("/json", (_, s) => TestServer.Respond(s, 200, "OK", [("Content-Type", "application/json")], Encoding.UTF8.GetBytes("{\"a\":[1,2]}")));
server.Route("/print", (_, s) => TestServer.Respond(s, 200, "OK", [("Content-Type", "text/html")], Encoding.UTF8.GetBytes(PrintPage)));
server.Route("/csspage", (_, s) => TestServer.Respond(s, 200, "OK", [("Content-Type", "text/html")], Encoding.UTF8.GetBytes(CssPage)));

async Task<Recorder> Run(ClientRequest request, Action<Recorder>? setup = null, byte[]? body = null)
{
    var recorder = new Recorder(request);
    setup?.Invoke(recorder);
    if (body is not null) request.Write(body);
    request.End();
    return await recorder.Settle();
}

async Task NetChecks()
{
    await Section("basic GET", async () =>
    {
        var r = await Run(Net.Request(server.Url("/hello")));
        Check("GET: status 200 and message OK", r.Response is { StatusCode: 200, StatusMessage: "OK" }, $"{r.Response?.StatusCode} {r.Response?.StatusMessage}");
        Check("GET: body arrives", r.BodyText == "Hello World!", r.BodyText);
        Check("GET: Electron's event order (finish, close, response, data, end, response close)",
            r.Events == "finish,close,response,data,end,rclose", r.Events);
        var h = r.Response?.Headers;
        var raw0 = r.Response?.RawHeaders ?? [];
        Check("headers: lower-case names, repeated values joined", h?.GetValueOrDefault("x-multi") is ["1, 2"] && h.GetValueOrDefault("x-custom") is ["a"],
            h is null ? null : string.Join("; ", h.Select(kv => kv.Key + "=" + string.Join("|", kv.Value))));
        Check("headers: content-type keeps the first value", h?.GetValueOrDefault("content-type") is ["text/plain"], string.Join("|", h?.GetValueOrDefault("content-type") ?? []));
        Check("headers: set-cookie is not exposed (DRIFT L2, Electron lists every value)", h is not null && !h.ContainsKey("set-cookie") && !raw0.Contains("Set-Cookie"),
            string.Join("|", h?.GetValueOrDefault("set-cookie") ?? []));
        var raw = r.Response?.RawHeaders ?? [];
        var rawPairs = Enumerable.Range(0, raw.Count / 2).Select(i => (raw[2 * i], raw[2 * i + 1])).ToList();
        Check("rawHeaders: names as sent with their values", rawPairs.Contains(("X-Custom", "a")) && rawPairs.Count(p => p.Item1 == "X-Multi") == 2,
            string.Join(",", raw));
        var notFound = await Run(Net.Request(server.Url("/notfound")));
        Check("a 404 is a response with its body", notFound.Response?.StatusCode == 404 && notFound.BodyText == "missing" && notFound.Errors.Count == 0, notFound.Events);
    });

    await Section("request headers", async () =>
    {
        var request = Net.Request(server.Url("/headers"));
        request.SetHeader("X-Header-Name", "value");
        request.SetHeader("X-Removed", "gone");
        var caseless = request.GetHeader("X-HEADER-NAME");
        request.RemoveHeader("x-removed");
        var invalidName = ThrowsNow(() => request.SetHeader("bad name", "v"));
        var invalidValue = ThrowsNow(() => request.SetHeader("X-Bad", "a\nb"));
        var getNull = ThrowsNow(() => request.GetHeader(null!));
        request.Write("x");
        var afterWrite = ThrowsNow(() => request.SetHeader("X-Late", "1"));
        var removeAfterWrite = ThrowsNow(() => request.RemoveHeader("X-Header-Name"));
        var r = await Run(request);
        var seen = server.Last("/headers");
        Check("setHeader: the name's case goes on the wire", seen?.Headers.Any(kv => kv is { Key: "X-Header-Name", Value: "value" }) == true,
            seen is null ? null : string.Join(",", seen.Headers.Select(kv => kv.Key)));
        Check("getHeader ignores case", caseless == "value", caseless);
        Check("removeHeader drops the header", seen?.Header("X-Removed") is null);
        Check("setHeader rejects an invalid name with Electron's message", invalidName?.Message == "Invalid header name: 'bad name'", invalidName?.Message);
        Check("setHeader rejects an invalid value with Electron's message", invalidValue?.Message == "Invalid value for header 'X-Bad': 'a\nb'", invalidValue?.Message);
        Check("getHeader(null) throws Electron's message", getNull?.Message == "`name` is required for getHeader(name)", getNull?.Message);
        Check("setHeader after the first write throws", afterWrite?.Message == "Can't set headers after they are sent", afterWrite?.Message);
        Check("removeHeader after the first write throws", removeAfterWrite?.Message == "Can't remove headers after they are sent", removeAfterWrite?.Message);
        Check("headers request completed", r.Response?.StatusCode == 200, r.Events);

        var host = Net.Request(server.Url("/headers"));
        host.SetHeader("Host", "example.invalid");
        var hostRun = await Run(host);
        Check("restricted header Host: Chromium refuses the request", hostRun.Response is null && hostRun.Errors.Count == 1, hostRun.Events);
    });

    await Section("bodies", async () =>
    {
        var post = Net.Request(new ClientRequestOptions { Url = server.Url("/echo"), Method = "post" });
        var r = await Run(post, body: Encoding.UTF8.GetBytes("Hello body"));
        var seen = server.Last("/echo");
        Check("POST: method upper-cased and body delivered", seen is { Method: "POST" } && Encoding.UTF8.GetString(seen.Body) == "Hello body"
            && r.BodyText == "Hello body", $"{seen?.Method} {r.BodyText}");

        var chunked = Net.Request(new ClientRequestOptions { Url = server.Url("/echo"), Method = "POST" });
        chunked.ChunkedEncoding = true;
        var sent = new MemoryStream();
        for (var i = 0; i < 100; i++)
        {
            var chunk = Enumerable.Range(0, 1024).Select(b => (byte)((b + i) % 251)).ToArray();
            sent.Write(chunk);
            chunked.Write(chunk);
        }
        var setTwice = ThrowsNow(() => chunked.ChunkedEncoding = false);
        var c = await Run(chunked);
        var chunkedSeen = server.Last("/echo");
        Check("chunkedEncoding: the whole body arrives and echoes back", chunkedSeen?.Body.AsSpan().SequenceEqual(sent.ToArray()) == true
            && c.Body.AsSpan().SequenceEqual(sent.ToArray()), $"{chunkedSeen?.Body.Length} / {c.Body.Length}");
        Check("chunkedEncoding: sent with Content-Length (DRIFT L3, Electron sends Transfer-Encoding: chunked)",
            chunkedSeen is { Chunked: false } && chunkedSeen.Header("Content-Length") == "102400", chunkedSeen?.Header("Content-Length"));
        Check("chunkedEncoding can't be set after the request started", setTwice?.Message == "chunkedEncoding can only be set before the request is started", setTwice?.Message);
        var once = Net.Request(server.Url("/echo"));
        once.ChunkedEncoding = false;
        var again = ThrowsNow(() => once.ChunkedEncoding = true);
        Check("chunkedEncoding can only be set once", again?.Message == "chunkedEncoding can only be set once", again?.Message);

        var response = await Run(Net.Request(server.Url("/chunked")));
        Check("chunked response: body reassembled", response.BodyText == "first-second-third", response.BodyText);
        Check("chunked response: delivered in more than one data event", response.DataEvents > 1, response.DataEvents);
    });

    await Section("redirects", async () =>
    {
        var follow = await Run(Net.Request(server.Url("/redirect302")));
        Check("follow: ends at the target", follow.Response?.StatusCode == 200 && follow.BodyText == "Hello World!", follow.Events);
        Check("follow: redirect event still raised, before the response",
            follow.Events == "finish,close,redirect,response,data,end,rclose", follow.Events);
        Check("follow: redirect event details", follow.Redirects is [{ StatusCode: 302, Method: "GET" } d] && d.RedirectUrl == server.Url("/hello")
            && d.ResponseHeaders.GetValueOrDefault("location") is ["/hello"], follow.Redirects.FirstOrDefault());

        var manual = await Run(Net.Request(new ClientRequestOptions { Url = server.Url("/chain"), Redirect = RedirectMode.Manual }),
            rec => rec.Request.Redirect += (_, _) => rec.Request.FollowRedirect());
        Check("manual + followRedirect: each hop reported, chain followed",
            manual.Redirects.Select(x => x.RedirectUrl).SequenceEqual([server.Url("/chain2"), server.Url("/hello")])
            && manual.Redirects.Select(x => x.StatusCode).SequenceEqual([302, 301]) && manual.Response?.StatusCode == 200, manual.Events);

        var cancelled = await Run(Net.Request(new ClientRequestOptions { Url = server.Url("/redirect302"), Redirect = RedirectMode.Manual }));
        Check("manual without followRedirect: 'Redirect was cancelled', no response",
            cancelled.Events == "finish,close,redirect,error:Redirect was cancelled" && cancelled.Response is null, cancelled.Events);

        var error = await Run(Net.Request(new ClientRequestOptions { Url = server.Url("/redirect302"), Redirect = RedirectMode.Error }));
        Check("error mode: Electron's error, no redirect event",
            error.Events == "finish,close,error:Attempted to redirect, but redirect policy was 'error'", error.Events);

        var big = Enumerable.Range(0, 1 << 20).Select(i => (byte)(i % 253)).ToArray();
        var upload = Net.Request(new ClientRequestOptions { Url = server.Url("/echo"), Method = "POST" });
        var before = upload.GetUploadProgress();
        var reports = new List<UploadProgress>();
        var duringMatches = true;
        upload.UploadProgressChanged += (_, p) =>
        {
            reports.Add(p);
            duringMatches &= upload.GetUploadProgress() == p;
        };
        var u = await Run(upload, body: big);
        var after = upload.GetUploadProgress();
        Check("getUploadProgress before sending: inactive, not started, zeros", before == new UploadProgress(false, false, 0, 0), before);
        Check("upload-progress: reported, non-decreasing, getUploadProgress already updated inside the handler",
            reports.Count > 0 && duringMatches && reports.Zip(reports.Skip(1)).All(pair => pair.Second.Current >= pair.First.Current), $"{reports.Count} reports");
        Check("getUploadProgress after a 1 MB upload: started, current == total == 1048576, and active stays true (Electron never clears it)",
            after == new UploadProgress(true, true, 1 << 20, 1 << 20) && reports[^1] == after, after);
        Check("upload body arrived whole", u.Body.AsSpan().SequenceEqual(big), u.Body.Length);
        var plain = Net.Request(server.Url("/hello"));
        var plainReports = 0;
        plain.UploadProgressChanged += (_, _) => plainReports++;
        await Run(plain);
        Check("a request without a body never reports upload progress", plainReports == 0 && plain.GetUploadProgress() == new UploadProgress(false, false, 0, 0), plain.GetUploadProgress());

        var outside = ThrowsNow(() => Net.Request(server.Url("/hello")).FollowRedirect());
        Check("followRedirect outside a redirect throws", outside?.Message == "followRedirect() called, but was not waiting for a redirect", outside?.Message);

        var aborted = await Run(Net.Request(server.Url("/redirect302")), rec => rec.Request.Redirect += (_, _) => rec.Request.Abort());
        Check("abort in the redirect handler: abort, no response", aborted.Events == "finish,close,redirect,abort" && aborted.Response is null, aborted.Events);

        var post302 = await Run(Net.Request(new ClientRequestOptions { Url = server.Url("/post302"), Method = "POST" }), body: Encoding.UTF8.GetBytes("payload"));
        var seen302 = server.Last("/echo");
        Check("302 turns POST into GET without a body", seen302 is { Method: "GET", Body.Length: 0 } && post302.Response?.StatusCode == 200, $"{seen302?.Method} {seen302?.Body.Length}");
        var post307 = await Run(Net.Request(new ClientRequestOptions { Url = server.Url("/post307"), Method = "POST" }), body: Encoding.UTF8.GetBytes("payload"));
        var seen307 = server.Last("/echo");
        Check("307 keeps POST and its body", seen307 is { Method: "POST" } && Encoding.UTF8.GetString(seen307.Body) == "payload" && post307.BodyText == "payload",
            $"{seen307?.Method} {post307.BodyText}");

        var file = await Run(Net.Request(server.Url("/tofile")));
        Check("redirect to file: net::ERR_UNSAFE_REDIRECT, no redirect event", file.Events == "finish,close,error:net::ERR_UNSAFE_REDIRECT", file.Events);
        var loop = await Run(Net.Request(server.Url("/loop")));
        Check("redirect loop: net::ERR_TOO_MANY_REDIRECTS after 20 redirects", loop.Redirects.Count == 20
            && loop.Events.EndsWith("redirect,error:net::ERR_TOO_MANY_REDIRECTS", StringComparison.Ordinal), $"{loop.Redirects.Count} {loop.Errors.FirstOrDefault()?.Message}");
    });

    await Section("login", async () =>
    {
        LoginEventArgs? loginArgs = null;
        var ok = await Run(Net.Request(server.Url("/auth")), rec => rec.OnLogin = l => { loginArgs = l; l.Callback("user", "pass"); });
        Check("login: the request's URL, no WebContents", loginArgs is { WebContents: null } && loginArgs.Url == server.Url("/auth"), loginArgs?.Url);
        Check("login: raised with Electron's authInfo", ok.Logins is [{ Realm: "Foo", Scheme: "basic", IsProxy: false, Host: "127.0.0.1" } info] && info.Port == server.Port,
            ok.Logins.FirstOrDefault());
        Check("login: credentials reach the server", ok.Response?.StatusCode == 200 && ok.BodyText == "ok:Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes("user:pass")),
            ok.BodyText);
        Check("login: event order", ok.Events == "finish,close,login,response,data,end,rclose", ok.Events);

        // Chromium caches the credentials for the origin and realm; each case starts without them.
        await Session.DefaultSession.ClearAuthCacheAsync();
        var cancel = await Run(Net.Request(server.Url("/auth")), rec => rec.OnLogin = l => l.Callback());
        Check("login: empty callback gives the 401 and its body", cancel.Response?.StatusCode == 401 && cancel.BodyText == "unauthenticated", cancel.Events);

        await Session.DefaultSession.ClearAuthCacheAsync();
        var unhandled = await Run(Net.Request(server.Url("/auth")));
        Check("login: without a handler the 401 is the response", unhandled.Response?.StatusCode == 401, unhandled.Events);

        await Session.DefaultSession.ClearAuthCacheAsync();
        var omit = await Run(Net.Request(new ClientRequestOptions { Url = server.Url("/auth"), Credentials = RequestCredentials.Omit }),
            rec => rec.OnLogin = l => l.Callback("user", "pass"));
        Check("credentials omit: no login event, 401 with WWW-Authenticate", omit.Logins.Count == 0 && omit.Response?.StatusCode == 401
            && omit.Response.Headers.GetValueOrDefault("www-authenticate") is ["Basic realm=\"Foo\""], omit.Events);
    });

    await Section("abort and errors", async () =>
    {
        var before = Net.Request(server.Url("/echo?before"));
        var rec = new Recorder(before);
        var count = server.Requests.Count(x => x.Path == "/echo");
        before.Abort();
        before.Write("x");
        before.End();
        await rec.Settle();
        Check("abort before end: abort then close, nothing sent", rec.Events == "abort,close" && server.Requests.Count(x => x.Path == "/echo") == count, rec.Events);

        var mid = await Run(Net.Request(server.Url("/partial")), r2 => r2.Request.Response += (_, _) => r2.Request.Abort());
        partialRelease.TrySetResult();
        Check("abort after response: abort, response closes without end or error", mid.Events == "finish,close,response,abort,rclose", mid.Events);

        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var refused = await Run(Net.Request($"http://127.0.0.1:{closedPort}/"));
        Check("connection refused: net::ERR_CONNECTION_REFUSED after finish and close",
            refused.Events == "finish,close,error:net::ERR_CONNECTION_REFUSED", refused.Events);

        // .invalid never resolves (RFC 6761), so this fails in DNS on every machine.
        var noHost = await Run(Net.Request("http://oriel-nettest.invalid/"));
        Check("unresolvable host: net::ERR_NAME_NOT_RESOLVED after finish and close",
            noHost.Events == "finish,close,error:net::ERR_NAME_NOT_RESOLVED", noHost.Events);

        var midDrop = await Run(Net.Request(server.Url("/dropmid")));
        var midError = midDrop.Errors.FirstOrDefault()?.Message ?? "";
        Check("connection lost mid-body: request error, then response error and close", midError.StartsWith("net::ERR_", StringComparison.Ordinal)
            && midDrop.Events == $"finish,close,response,data,error:{midError},rerror:{midError},rclose", midDrop.Events);
        var late = Net.Request(server.Url("/hello"));
        var lateResponse = new TaskCompletionSource<IncomingMessage>();
        late.Response += (_, m) => lateResponse.TrySetResult(m);
        late.End();
        var message = await lateResponse.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(500);
        var lateBody = new MemoryStream();
        var lateEnd = new TaskCompletionSource();
        message.End += (_, _) => lateEnd.TrySetResult();
        message.Data += (_, chunk) => lateBody.Write(chunk);
        await Task.WhenAny(lateEnd.Task, Task.Delay(5000));
        Check("the body waits for a data handler added later", lateEnd.Task.IsCompleted && Encoding.UTF8.GetString(lateBody.ToArray()) == "Hello World!",
            Encoding.UTF8.GetString(lateBody.ToArray()));
        var dropped = await Run(Net.Request(server.Url("/drop")));
        Check("server closes without answering: net::ERR_EMPTY_RESPONSE", dropped.Errors.Select(e => e.Message).SequenceEqual(["net::ERR_EMPTY_RESPONSE"]), dropped.Events);

        Check("http(s) only", ThrowsNow(() => Net.Request("ftp://127.0.0.1/"))?.Message == "ClientRequest only supports http: and https: protocols");
        Check("same-origin needs an origin", ThrowsNow(() => Net.Request(new ClientRequestOptions { Url = server.Url("/"), Credentials = RequestCredentials.SameOrigin }))?.Message
            == "credentials: same-origin requires origin to be set");
        Check("a path with a space is refused", ThrowsNow(() => Net.Request(new ClientRequestOptions { Hostname = "127.0.0.1", Path = "/a b" }))?.Message
            == "Request path contains unescaped characters");
        var parts = await Run(Net.Request(new ClientRequestOptions { Hostname = "127.0.0.1", Port = server.Port, Path = "/hello?x=1" }));
        Check("URL from hostname, port and path", parts.Response?.StatusCode == 200 && server.Requests.Any(x => x.Path == "/hello"), parts.Events);
    });

    await Section("cookies", async () =>
    {
        var session = Session.FromPartition("nettest-cookies");
        await Run(Net.Request(new ClientRequestOptions { Url = server.Url("/setcookie"), Session = session }));
        var afterDefault = await session.Cookies.GetAsync(new CookieFilter { Name = "netc" });
        Check("default: a response's cookie is stored (DRIFT L1, Electron blocks cookies by default)", afterDefault.Count == 1, afterDefault.Count);
        await session.Cookies.RemoveAsync(server.Url("/"), "netc");
        await Run(Net.Request(new ClientRequestOptions { Url = server.Url("/setcookie"), Session = session, UseSessionCookies = true }));
        var stored = await session.Cookies.GetAsync(new CookieFilter { Name = "netc" });
        Check("useSessionCookies: a response's cookie is stored", stored.Count == 1, stored.Count);
        var sentDefault = await Run(Net.Request(new ClientRequestOptions { Url = server.Url("/cookies"), Session = session }));
        Check("default: the session's cookies are sent (DRIFT L1)", sentDefault.BodyText == "netc=1", sentDefault.BodyText);
        var omitted = await Run(Net.Request(new ClientRequestOptions { Url = server.Url("/cookies"), Session = session, Credentials = RequestCredentials.Omit }));
        Check("credentials omit: cookies not sent", omitted.BodyText == "", omitted.BodyText);
        var sentWith = await Run(Net.Request(new ClientRequestOptions { Url = server.Url("/cookies"), Session = session, UseSessionCookies = true }));
        Check("useSessionCookies: the session's cookies are sent", sentWith.BodyText == "netc=1", sentWith.BodyText);
        var include = await Run(Net.Request(new ClientRequestOptions { Url = server.Url("/cookies"), Session = session, Credentials = RequestCredentials.Include }));
        Check("credentials include: cookies sent", include.BodyText == "netc=1", include.BodyText);
        var other = await Run(Net.Request(new ClientRequestOptions { Url = server.Url("/cookies"), Partition = "nettest-other", UseSessionCookies = true }));
        Check("partition: another session's cookies stay there", other.BodyText == "", other.BodyText);
    });

    await Section("fetch", async () =>
    {
        var hello = await Net.FetchAsync(server.Url("/hello"));
        Check("fetch: status, ok, statusText", hello is { Status: 200, Ok: true, StatusText: "OK" }, hello.Status);
        Check("fetch: headers joined", hello.Headers.GetValueOrDefault("x-multi") == "1, 2" && hello.Headers.GetValueOrDefault("x-custom") == "a",
            string.Join("; ", hello.Headers.Select(kv => kv.Key + "=" + kv.Value)));
        Check("fetch: text()", await hello.TextAsync() == "Hello World!");
        var reread = await Throws(() => hello.TextAsync());
        Check("fetch: body can be read once", reread is InvalidOperationException, reread?.Message);
        var json = await (await Net.FetchAsync(server.Url("/json"))).JsonAsync();
        Check("fetch: json()", json?["a"]?[1]?.GetValue<int>() == 2, json?.ToJsonString());
        var posted = await Net.FetchAsync(server.Url("/echo"), new FetchInit { Method = "PUT", Body = Encoding.UTF8.GetBytes("put body"), Headers = new Dictionary<string, string> { ["X-Fetch"] = "1" } });
        var seen = server.Last("/echo");
        Check("fetch: method, body and headers sent", seen is { Method: "PUT" } && seen.Header("X-Fetch") == "1" && await posted.TextAsync() == "put body", seen?.Method);
        var empty = await Net.FetchAsync(server.Url("/nocontent"));
        Check("fetch: 204 has a null body", empty.Status == 204 && empty.Body is null);
        var manual = await Throws(() => Net.FetchAsync(server.Url("/redirect302"), new FetchInit { Redirect = RedirectMode.Manual }).WaitAsync(TimeSpan.FromSeconds(15)));
        Check("fetch: manual redirect rejects as Electron's does", manual?.Message == "Redirect was cancelled", manual?.Message);
        var followed = await Net.FetchAsync(server.Url("/redirect302"));
        Check("fetch: follows redirects", followed.Status == 200 && await followed.TextAsync() == "Hello World!");
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var closedPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var refused = await Throws(() => Net.FetchAsync($"http://127.0.0.1:{closedPort}/").WaitAsync(TimeSpan.FromSeconds(15)));
        Check("fetch: network error rejects with net::ERR_CONNECTION_REFUSED", refused is NetException { Message: "net::ERR_CONNECTION_REFUSED" }, refused?.Message);
        var noHost = await Throws(() => Net.FetchAsync("http://oriel-nettest.invalid/").WaitAsync(TimeSpan.FromSeconds(15)));
        Check("fetch: unresolvable host rejects with net::ERR_NAME_NOT_RESOLVED", noHost is NetException { Message: "net::ERR_NAME_NOT_RESOLVED" }, noHost?.Message);
        var preCancelled = await Throws(() => Net.FetchAsync(server.Url("/hello"), new FetchInit { CancellationToken = new CancellationToken(true) }));
        Check("fetch: an already-cancelled token rejects", preCancelled is OperationCanceledException, preCancelled?.GetType().Name);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var cancelled = await Throws(() => Net.FetchAsync(server.Url("/slow"), new FetchInit { CancellationToken = cts.Token }).WaitAsync(TimeSpan.FromSeconds(10)));
        Check("fetch: cancelling aborts the request", cancelled is OperationCanceledException, cancelled?.GetType().Name);
        var stream = (await Net.FetchAsync(server.Url("/chunked"))).Body!;
        var streamed = await new StreamReader(stream).ReadToEndAsync();
        Check("fetch: body stream", streamed == "first-second-third", streamed);

        Protocol.Handle("oriel-net", request => ProtocolResponse.Text($"handled {request.Method} {new Uri(request.Url).AbsolutePath}"));
        var custom = await Throws(async () =>
        {
            var response = await Net.FetchAsync("oriel-net://app/resource");
            var text = await response.TextAsync();
            Check("fetch reaches the session's protocol handler", response.Status == 200 && text == "handled GET /resource", text);
        });
        if (custom is not null) Check("fetch reaches the session's protocol handler", false, custom.Message);
        var memory = Session.FromPartition("nettest-protocol");
        memory.Protocol.Handle("oriel-net", _ => ProtocolResponse.Text("partition handler"));
        var partitionText = await Throws(async () =>
        {
            var text = await (await memory.FetchAsync("oriel-net://app/x")).TextAsync();
            Check("session.fetch uses that session's protocol handler", text == "partition handler", text);
        });
        if (partitionText is not null) Check("session.fetch uses that session's protocol handler", false, partitionText.Message);
        var request = await Throws(() => Task.FromResult(Net.Request("oriel-net://app/resource")));
        Check("net.request stays http(s) only for custom schemes", request?.Message == "ClientRequest only supports http: and https: protocols", request?.Message);

        var intercepting = Session.FromPartition("nettest-bypass");
        intercepting.Protocol.Handle("http", _ => ProtocolResponse.Text("intercepted"));
        try
        {
            var handled = await (await intercepting.FetchAsync(server.Url("/hello"))).TextAsync();
            Check("fetch: an http protocol handler serves the request", handled == "intercepted", handled);
            var bypassed = await (await intercepting.FetchAsync(server.Url("/hello"), new FetchInit { BypassCustomProtocolHandlers = true })).TextAsync();
            Check("fetch bypassCustomProtocolHandlers: the http handler is skipped, the server answers", bypassed == "Hello World!", bypassed);
            var viaRequest = await Run(Net.Request(new ClientRequestOptions { Url = server.Url("/hello"), Session = intercepting, BypassCustomProtocolHandlers = true }));
            var notBypassed = await Run(Net.Request(new ClientRequestOptions { Url = server.Url("/hello"), Session = intercepting }));
            Check("net.request bypassCustomProtocolHandlers: skipped with it, served without it",
                viaRequest.BodyText == "Hello World!" && notBypassed.BodyText == "intercepted", $"{viaRequest.BodyText} / {notBypassed.BodyText}");
            var after = await (await intercepting.FetchAsync(server.Url("/hello"))).TextAsync();
            Check("bypassCustomProtocolHandlers affects only its own request", after == "intercepted", after);
        }
        finally
        {
            intercepting.Protocol.Unhandle("http");
        }
        var unknownScheme = await Throws(() => Net.FetchAsync("oriel-net://app/resource", new FetchInit { BypassCustomProtocolHandlers = true }).WaitAsync(TimeSpan.FromSeconds(15)));
        Check("fetch bypassCustomProtocolHandlers on a handled custom scheme: net::ERR_UNKNOWN_URL_SCHEME",
            unknownScheme is NetException { Message: "net::ERR_UNKNOWN_URL_SCHEME" }, $"{unknownScheme?.GetType().Name}: {unknownScheme?.Message}");
    });

    await Section("online and resolve", async () =>
    {
        var online = Net.IsOnline();
        if (OperatingSystem.IsWindows())
        {
            // An independent reading: Windows' own connectivity hint through PowerShell's NetConnectionProfile.
            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -Command \"(Get-NetConnectionProfile | Measure-Object).Count\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var ps = Process.Start(psi)!;
            var profiles = int.TryParse((await ps.StandardOutput.ReadToEndAsync()).Trim(), out var n) ? n : -1;
            await ps.WaitForExitAsync();
            Check("isOnline agrees with Windows' connection profiles", online == (profiles > 0), $"isOnline={online} profiles={profiles}");
        }
        else
        {
            Skip("isOnline agrees with Windows' connection profiles", "Windows only");
        }
        Check("online property matches isOnline()", Net.Online == online);
        var resolved = await Net.ResolveHostAsync("localhost");
        Check("resolveHost resolves localhost", resolved.Endpoints.Any(e => e.Address is "127.0.0.1" or "::1"), string.Join(",", resolved.Endpoints));
        var noHost = await Throws(() => Net.ResolveHostAsync("oriel-nettest.invalid"));
        Check("resolveHost: an unresolvable host throws with net::ERR_NAME_NOT_RESOLVED's code (-105)", noHost is HostResolutionException { ErrorCode: -105 },
            $"{noHost?.GetType().Name}: {noHost?.Message}");
    });
}

async Task PrintChecks()
{
    var win = new BrowserWindow(new BrowserWindowOptions { Show = false, Width = 800, Height = 600 });
    try
    {
        await win.LoadUrl(server.Url("/print"));
        var wc = win.WebContents;

        await Section("printToPDF output", async () =>
        {
            var pdf = new Pdf(await wc.PrintToPDFAsync());
            Check("printToPDF: a PDF", pdf.IsPdf, Encoding.ASCII.GetString(pdf.Bytes.AsSpan(0, Math.Min(8, pdf.Bytes.Length))));
            Check("printToPDF: three pages", pdf.PageCount == 3, pdf.PageCount);
            Check("printToPDF: Letter portrait by default (612 x 792 pt)", pdf.MediaBoxes.Count > 0 && pdf.MediaBoxes.All(b => Near(b, 612, 792)), Boxes(pdf));
            Check("printToPDF: no background by default", !pdf.HasFill("1 0 0"));
            var text = pdf.Text();
            Check("printToPDF: page text decodes", text.Contains("PAGE-ONE") && text.Contains("PAGE-THREE"), Trim(text));

            var a4 = new Pdf(await wc.PrintToPDFAsync(new PrintToPdfOptions { PageSize = "a4", Landscape = true }));
            Check("printToPDF: A4 landscape (842 x 595 pt)", a4.MediaBoxes.Count > 0 && a4.MediaBoxes.All(b => Near(b, 842, 595)), Boxes(a4));
            var custom = new Pdf(await wc.PrintToPDFAsync(new PrintToPdfOptions { PageSize = PageSize.Custom(5, 7) }));
            Check("printToPDF: custom size in inches (360 x 504 pt)", custom.MediaBoxes.Count > 0 && custom.MediaBoxes.All(b => Near(b, 360, 504)), Boxes(custom));

            var range = new Pdf(await wc.PrintToPDFAsync(new PrintToPdfOptions { PageRanges = "2" }));
            Check("printToPDF: pageRanges picks pages", range.PageCount == 1 && range.Text().Contains("PAGE-TWO") && !range.Text().Contains("PAGE-ONE"), range.PageCount);

            var background = new Pdf(await wc.PrintToPDFAsync(new PrintToPdfOptions { PrintBackground = true }));
            Check("printToPDF: printBackground paints the page background", background.HasFill("1 0 0"));

            var header = new Pdf(await wc.PrintToPDFAsync(new PrintToPdfOptions
            {
                DisplayHeaderFooter = true,
                HeaderTemplate = "<div style=\"font-size:12px\">ORIELHEADER</div>",
                FooterTemplate = "<div style=\"font-size:12px\">ORIELFOOTER <span class=\"pageNumber\"></span>/<span class=\"totalPages\"></span></div>",
                Margins = new PdfMargins { Top = 1, Bottom = 1 },
            }));
            var headerText = header.Text();
            Check("printToPDF: header and footer templates are printed", headerText.Contains("ORIELHEADER") && headerText.Contains("ORIELFOOTER 3/3"), Trim(headerText));
            // The reader itself: glyph code 1 is "A", then "P" after a font switch, "X" in a form drawn with Do; 1-byte codes give
            // "Z" (a 1-byte codespace) and "W" (a simple font without one), as Linux Chromium's Type3 header and footer font.
            var synthetic = new Pdf(System.Text.Encoding.Latin1.GetBytes(string.Join("\n",
                "%PDF-1.4",
                "1 0 obj <</Type /Page /Resources <</Font <</F1 3 0 R /F3 9 0 R /F6 11 0 R /F7 13 0 R>> /XObject <</X1 5 0 R>>>> /Contents 4 0 R>> endobj",
                "3 0 obj <</Type /Font /ToUnicode 6 0 R>> endobj",
                "6 0 obj <</Length 0>>stream", "begincmap 1 beginbfrange <0001> <0002> <0041> endbfrange endcmap", "endstream endobj",
                "4 0 obj <</Length 0>>stream", "BT /F1 12 Tf <00010002> Tj /F3 12 Tf <0001> Tj /F6 12 Tf <41> Tj /F7 12 Tf <42> Tj ET /X1 Do", "endstream endobj",
                "5 0 obj <</Type /XObject /Subtype /Form /Resources <</Font <</F2 7 0 R>>>>>>stream", "BT /F2 9 Tf <00010002> Tj ET", "endstream endobj",
                "7 0 obj <</Type /Font /ToUnicode 8 0 R>> endobj", "9 0 obj <</Type /Font /ToUnicode 10 0 R>> endobj",
                "8 0 obj <</Length 0>>stream", "begincmap 1 beginbfrange <0001> <0002> <0058> endbfrange endcmap", "endstream endobj",
                "10 0 obj <</Length 0>>stream", "begincmap 1 beginbfrange <0001> <0001> <0050> endbfrange endcmap", "endstream endobj",
                "11 0 obj <</Type /Font /Subtype /Type0 /ToUnicode 12 0 R>> endobj", "13 0 obj <</Type /Font /Subtype /TrueType /ToUnicode 14 0 R>> endobj",
                "12 0 obj <</Length 0>>stream", "begincmap 1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <41> <005A> endbfchar endcmap", "endstream endobj",
                "14 0 obj <</Length 0>>stream", "begincmap 1 beginbfchar <42> <0057> endbfchar endcmap", "endstream endobj",
                "%%EOF")));
            Check("the test's PDF reader decodes each font with its own map and code length, and follows form XObjects", synthetic.Text().Contains("ABPZWXY"), Trim(synthetic.Text()));
            var plain = pdf.Text();
            Check("printToPDF: no header without displayHeaderFooter", !plain.Contains("ORIELHEADER"));

            await win.LoadUrl(server.Url("/csspage"));
            var css = new Pdf(await wc.PrintToPDFAsync(new PrintToPdfOptions { PreferCSSPageSize = true }));
            Check("printToPDF: preferCSSPageSize uses @page size (288 x 288 pt)", css.MediaBoxes.Count > 0 && css.MediaBoxes.All(b => Near(b, 288, 288)), Boxes(css));
            var ignored = new Pdf(await wc.PrintToPDFAsync());
            Check("printToPDF: @page size ignored by default", ignored.MediaBoxes.All(b => Near(b, 612, 792)), Boxes(ignored));
            await win.LoadUrl(server.Url("/print"));

            var tagged = new Pdf(await wc.PrintToPDFAsync(new PrintToPdfOptions { GenerateTaggedPDF = true }));
            Check("printToPDF: generateTaggedPDF adds a structure tree", Encoding.Latin1.GetString(tagged.Bytes).Contains("/StructTreeRoot")
                && !Encoding.Latin1.GetString(pdf.Bytes).Contains("/StructTreeRoot"));
            var jobs = await Task.WhenAll(wc.PrintToPDFAsync(), wc.PrintToPDFAsync(new PrintToPdfOptions { PageRanges = "1" }));
            Check("printToPDF: concurrent jobs are queued, not refused", new Pdf(jobs[0]).PageCount == 3 && new Pdf(jobs[1]).PageCount == 1);
        });

        await Section("printToPDF validation", async () =>
        {
            async Task Expect(string name, PrintToPdfOptions options, string message, Type type)
            {
                var error = await Throws(() => wc.PrintToPDFAsync(options));
                Check($"printToPDF rejects {name}", error?.GetType() == type && error.Message == message, $"{error?.GetType().Name}: {error?.Message}");
            }
            await Expect("an unknown pageSize", new() { PageSize = "A9" }, "Invalid pageSize A9", typeof(ArgumentException));
            await Expect("margins over the page", new() { Margins = new PdfMargins { Top = 20 } }, "margins must be less than or equal to pageSize", typeof(ArgumentException));
            await Expect("a scale over 2", new() { Scale = 3 }, "Invalid print parameters: scale is outside of [0.1 - 2] range", typeof(ArgumentException));
            await Expect("a negative margin", new() { Margins = new PdfMargins { Left = -1 } }, "Invalid print parameters: left margin is negative", typeof(ArgumentException));
            await Expect("an empty content area", new() { Margins = new PdfMargins { Left = 4.3, Right = 4.3 } },
                "Invalid print parameters: invalid print parameters: content area is empty", typeof(ArgumentException));
            await Expect("a page range syntax error", new() { PageRanges = "abc" }, "Failed to generate PDF: Page range syntax error", typeof(InvalidOperationException));
            await Expect("an inverted page range", new() { PageRanges = "3-1" }, "Failed to generate PDF: Page range is invalid (start > end)", typeof(InvalidOperationException));
            await Expect("a range past the last page", new() { PageRanges = "7" }, "Failed to generate PDF: Page range exceeds page count", typeof(InvalidOperationException));
        });

        await Section("print and printers", async () =>
        {
            Check("print rejects an unsupported pageSize", ThrowsNow(() => wc.Print(new PrintOptions { PageSize = "a4" }))?.Message == "Unsupported pageSize: a4");
            Check("print rejects pageSize with usePrinterDefaultPageSize",
                ThrowsNow(() => wc.Print(new PrintOptions { PageSize = "A4", UsePrinterDefaultPageSize = true }))?.Message == "usePrinterDefaultPageSize cannot be combined with pageSize");
            Check("print rejects a size without height", ThrowsNow(() => wc.Print(new PrintOptions { PageSize = PageSize.Custom(100000, 0) }))?.Message
                == "height and width properties are required for pageSize");
            var tiny = ThrowsNow(() => wc.Print(new PrintOptions { PageSize = PageSize.Custom(352, 100000) }));
            Check("print rejects a side of 352 microns", tiny is ArgumentOutOfRangeException && tiny.Message == "height and width properties must be minimum 352 microns.", tiny?.Message);
            var printers = await wc.GetPrintersAsync();
            if (!OperatingSystem.IsWindows())
            {
                // Off Windows the printer checks are not made (DRIFT Z4), so a deviceName would open the dialog.
                Skip("print: an unknown deviceName fails with Electron's reason", "Windows only (DRIFT Z4)");
                Skip("getPrintersAsync lists the system's printers", "Windows only (DRIFT Z4)");
                Check("getPrintersAsync off Windows: an empty list (DRIFT Z4)", printers.Count == 0, printers.Count);
                return;
            }
            var outcome = new TaskCompletionSource<(bool, string)>();
            wc.Print(new PrintOptions { DeviceName = "No Such Printer 42" }, (ok, reason) => outcome.TrySetResult((ok, reason)));
            var result = await outcome.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check("print: an unknown deviceName fails with Electron's reason", result == (false, "Invalid deviceName provided"), result);

            var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -Command \"Get-CimInstance Win32_Printer | ForEach-Object { $_.Name }\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var ps = Process.Start(psi)!;
            var expected = (await ps.StandardOutput.ReadToEndAsync()).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Order().ToList();
            await ps.WaitForExitAsync();
            Check("getPrintersAsync lists the system's printers", printers.Select(p => p.Name).Order().SequenceEqual(expected),
                $"{string.Join("|", printers.Select(p => p.Name))} vs {string.Join("|", expected)}");
            Check("getPrintersAsync: displayName is the name, description is a string", printers.All(p => p.DisplayName == p.Name && p.Description is not null));
            if (printers.FirstOrDefault() is { } first)
            {
                var silent = new TaskCompletionSource<(bool, string)>();
                wc.Print(new PrintOptions { Silent = true, DeviceName = first.Name }, (ok, reason) => silent.TrySetResult((ok, reason)));
                var silentResult = await silent.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Check("print: silent printing reports failed (DRIFT Z2)", silentResult == (false, "failed"), silentResult);
            }
        });
    }
    finally
    {
        win.Close();
    }
}

static bool Near((double Width, double Height) box, double width, double height) => Math.Abs(box.Width - width) <= 1.5 && Math.Abs(box.Height - height) <= 1.5;
static string Boxes(Pdf pdf) => string.Join(" ", pdf.MediaBoxes.Select(b => $"{b.Width}x{b.Height}"));
static string Trim(string text) => text.Replace('\n', ' ') is var t && t.Length > 160 ? t[..160] : text.Replace('\n', ' ');

app.Ready += (_, _) => _ = Task.Run(async () =>
{
    try
    {
        var only = Environment.GetEnvironmentVariable("ORIEL_NETTEST_ONLY");
        if (only is null or "net") await NetChecks();
        if (only is null or "print") await PrintChecks();
    }
    catch (Exception e) { Check("run completed", false, e); }
    lock (results) failures = results.Count(r => r.StartsWith("FAIL"));
    lock (results) Console.WriteLine($"RESULT: {failures} failure(s) of {results.Count} checks ({results.Count(r => r.StartsWith("SKIP"))} skipped)");
    Console.Out.Flush();
    Environment.Exit(failures);
});

_ = Task.Delay(TimeSpan.FromSeconds(int.Parse(Environment.GetEnvironmentVariable("ORIEL_TEST_TIMEOUT") ?? "240"))).ContinueWith(_ =>
{
    Console.WriteLine("FAIL  run did not finish in time");
    Console.Out.Flush();
    Environment.Exit(-1);
}, TaskScheduler.Default);

app.Run();
return failures;
