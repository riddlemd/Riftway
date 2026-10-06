using Oriel.Cef;
using Oriel.Cef.Interop;

namespace Oriel;

/// <summary>Custom scheme handlers for one session's pages. Electron's <c>ses.protocol</c>.</summary>
/// <remarks>The default session's is the static <see cref="Protocol"/>. Schemes still need
/// <see cref="Protocol.RegisterSchemesAsPrivileged"/> for privileges, which apply to every session.</remarks>
public sealed unsafe class SessionProtocol
{
    private readonly Session _session;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SchemeHandlerFactory> _handlers = new(StringComparer.Ordinal);

    internal SessionProtocol(Session session)
    {
        _session = session;
        // Every session's pages need the sync IPC scheme; the default session's is registered with CEF's global context.
        if (!session.IsDefault) Register(SyncIpc.Scheme, SyncIpc.Factory);
    }

    /// <summary>Serves requests to <paramref name="scheme"/> in this session. Electron's <c>protocol.handle()</c>.</summary>
    /// <exception cref="InvalidOperationException">The scheme is already handled.</exception>
    public void Handle(string scheme, Func<ProtocolRequest, CancellationToken, Task<ProtocolResponse>> handler)
    {
        if (_session.IsDefault)
        {
            Protocol.Handle(scheme, handler);
            return;
        }
        ArgumentNullException.ThrowIfNull(scheme);
        ArgumentNullException.ThrowIfNull(handler);
        var factory = new SchemeHandlerFactory(handler);
        lock (_gate)
        {
            if (!_handlers.TryAdd(scheme, factory)) throw new InvalidOperationException($"'{scheme}' is already handled.");
            Register(scheme, factory);
        }
    }

    /// <summary>Serves requests to <paramref name="scheme"/> in this session synchronously.</summary>
    /// <exception cref="InvalidOperationException">The scheme is already handled.</exception>
    public void Handle(string scheme, Func<ProtocolRequest, ProtocolResponse> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Handle(scheme, (request, _) => Task.FromResult(handler(request)));
    }

    /// <summary>Stops serving <paramref name="scheme"/> in this session. Electron's <c>protocol.unhandle()</c>.</summary>
    public void Unhandle(string scheme)
    {
        if (_session.IsDefault)
        {
            Protocol.Unhandle(scheme);
            return;
        }
        ArgumentNullException.ThrowIfNull(scheme);
        lock (_gate)
        {
            if (_handlers.Remove(scheme)) Register(scheme, null);
        }
    }

    /// <summary>Whether <paramref name="scheme"/> has a handler in this session. Electron's <c>protocol.isProtocolHandled()</c>.</summary>
    public bool IsProtocolHandled(string scheme)
    {
        if (_session.IsDefault) return Protocol.IsProtocolHandled(scheme);
        lock (_gate) return _handlers.ContainsKey(scheme);
    }

    // A null factory unregisters the scheme.
    private void Register(string scheme, SchemeHandlerFactory? factory)
    {
        var context = _session.Native;
        cef_string_utf16_t name = default;
        cef_string_utf16_t domain = default;
        CefString.Set(&name, scheme);
        if (factory is not null) NativeObject.AddRef(factory.Native);
        if (context->register_scheme_handler_factory(context, &name, &domain, factory is null ? null : factory.Native) == 0)
            Console.Error.WriteLine($"[Oriel] CEF refused a handler for scheme '{scheme}'.");
        CefString.Clear(&name);
    }
}
