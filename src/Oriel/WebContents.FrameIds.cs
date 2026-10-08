using Oriel.Cef;
using Oriel.Cef.Interop;

namespace Oriel;

public sealed unsafe partial class WebContents
{
    // Chromium keeps one visibility per page, as RenderFrameHost::GetVisibilityState reads it; the frames' preloads
    // report the document's. A page starts visible, even in a window created hidden.
    private string _visibilityState = "visible";

    internal string VisibilityState => CefThread.InvokeOnUi(() => _visibilityState);

    // UI thread: every frame CEF knows in this page, out-of-process ones included.
    private List<CefFrameInfo> ReadCefFrames()
    {
        var frames = new List<CefFrameInfo>();
        if (_browser is null) return frames;
        var list = CefNative.cef_string_list_alloc();
        _browser->get_frame_identifiers(_browser, list);
        for (nuint i = 0; i < CefNative.cef_string_list_size(list); i++)
        {
            cef_string_utf16_t value = default;
            if (CefNative.cef_string_list_value(list, i, &value) == 0) continue;
            var frame = _browser->get_frame_by_identifier(_browser, &value);
            CefString.Clear(&value);
            if (frame is null) continue;
            var parent = frame->get_parent(frame);
            frames.Add(new CefFrameInfo(
                CefString.ReadUserFree(frame->get_identifier(frame)),
                parent is null ? null : CefString.ReadUserFree(parent->get_identifier(parent)),
                CefString.ReadUserFree(frame->get_name(frame)),
                CefString.ReadUserFree(frame->get_url(frame)),
                frame->is_main(frame) != 0));
            NativeObject.Release(parent);
            NativeObject.Release(frame);
        }
        CefNative.cef_string_list_free(list);
        return frames;
    }

    // UI thread: CEF's identifier for a DevTools frame, kept so a frame that has gone reports its last one; null while unmatched.
    // DevTools and CEF share no id, so the frame is found from the main frame down by URL, name and creation order.
    internal string? CefIdentifierOf(FrameState frame)
    {
        if (frame.Detached || _browser is null) return frame.CefId;
        var chain = new List<FrameState> { frame };
        while (chain[^1].ParentId is { } parentId && _frames.TryGetValue(parentId, out var parent) && chain.Count < 64) chain.Add(parent);
        if (chain[^1].Id != _mainFrameId) return frame.CefId;
        var cef = ReadCefFrames();
        var id = cef.FirstOrDefault(f => f.IsMain).Id;
        for (var i = chain.Count - 2; i >= 0 && id is not null; i--)
        {
            var step = chain[i];
            var parentCef = id;
            List<CefFrameInfo> children = [.. cef.Where(c => c.ParentId == parentCef)];
            List<FrameState> siblings = [.. _frames.Values.Where(f => f.ParentId == step.ParentId && !f.Detached)];
            int LookAlikes(FrameState f) => siblings.Count(s => s.Url == f.Url && s.Name == f.Name && s.Created < f.Created);
            string? Seen(FrameState f) => FrameIdentity.MatchChild(children, f.Name, f.Url, LookAlikes(f));
            id = step.Url.Length > 0 ? Seen(step)
                : FrameIdentity.MatchUnseen(children, siblings.Where(s => s.Url.Length > 0).Select(Seen).OfType<string>().ToHashSet(),
                    FrameIdentity.Parse(parentCef)?.ProcessId ?? -1, LookAlikes(step));
        }
        return frame.CefId = id;
    }

    internal (int ProcessId, string FrameToken)? IdsOf(FrameState frame) => CefThread.InvokeOnUi(() => FrameIdentity.Parse(CefIdentifierOf(frame)));

    // UI thread.
    private WebFrameMain? FrameWithCefIdentifier(string identifier)
    {
        if (_browser is null) return null;
        var value = default(cef_string_utf16_t);
        CefString.Set(&value, identifier);
        var known = _browser->get_frame_by_identifier(_browser, &value);
        CefString.Clear(&value);
        if (known is null) return null;
        NativeObject.Release(known);
        foreach (var frame in _frames.Values.ToList())
            if (CefIdentifierOf(frame) == identifier) return WrapperOf(frame);
        return null;
    }

    internal static WebFrameMain? FromFrameToken(int processId, string frameToken)
    {
        // Chromium's Token::FromString takes upper-case hex only, so a lower-case token finds nothing.
        var identifier = $"{processId:X}-{frameToken}";
        if (FrameIdentity.Parse(identifier) is null) return null;
        foreach (var page in GetAllWebContents())
            if (CefThread.InvokeOnUi(() => page.FrameWithCefIdentifier(identifier)) is { } found) return found;
        return null;
    }
}
