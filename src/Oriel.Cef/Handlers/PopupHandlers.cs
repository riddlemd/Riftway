using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Oriel.Cef.Interop;

namespace Oriel.Cef.Handlers;

/// <summary>A page asked to open a window (<c>window.open</c>, a <c>target=_blank</c> link, a shift-click).</summary>
/// <param name="PopupId">CEF's id for this request, repeated by an abort.</param>
/// <param name="Url">The URL the new window would load.</param>
/// <param name="FrameName">The target name given to <c>window.open</c>, or empty.</param>
/// <param name="Disposition">How the page asked for it to open.</param>
/// <param name="UserGesture">Whether a user action started it.</param>
public sealed record PopupRequest(int PopupId, string Url, string FrameName, cef_window_open_disposition_t Disposition, bool UserGesture);

/// <summary>Decides a popup request: return null to block it, or the client the new browser should use.</summary>
public unsafe delegate cef_client_t* BeforePopupCallback(PopupRequest request, cef_popup_features_t* features, cef_browser_settings_t* settings);

/// <summary><c>cef_life_span_handler_t</c>: popups a page opens. Close callbacks keep CEF's defaults.</summary>
public sealed unsafe class LifeSpanHandler
{
    public LifeSpanHandler()
    {
        Native = NativeObject.Create<cef_life_span_handler_t>(this);
        Native->on_before_popup = &OnBeforePopup;
        Native->on_before_popup_aborted = &OnBeforePopupAborted;
    }

    public cef_life_span_handler_t* Native { get; }

    /// <summary>Runs on the UI thread before CEF creates a popup; null keeps CEF's default (allow, same client).</summary>
    public BeforePopupCallback? BeforePopup { get; init; }

    /// <summary>A popup that <see cref="BeforePopup"/> allowed will not be created after all: its id.</summary>
    public Action<int>? PopupAborted { get; init; }

    // *client arrives with a transfer reference the receiver releases; a replacement must carry one CEF takes over
    // (CEF's CppToC Unwrap/Wrap). Returns 1 to block the popup.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnBeforePopup(cef_life_span_handler_t* self, cef_browser_t* browser, cef_frame_t* frame, int popupId,
        cef_string_utf16_t* targetUrl, cef_string_utf16_t* targetFrameName, cef_window_open_disposition_t targetDisposition, int userGesture,
        cef_popup_features_t* popupFeatures, cef_window_info_t* windowInfo, cef_client_t** client, cef_browser_settings_t* settings,
        cef_dictionary_value_t** extraInfo, int* noJavascriptAccess)
    {
        var owner = NativeObject.Owner<LifeSpanHandler>(self);
        var block = false;
        if (owner.BeforePopup is { } decide)
        {
            var request = new PopupRequest(popupId, CefString.Read(targetUrl), CefString.Read(targetFrameName), targetDisposition, userGesture != 0);
            var chosen = Guard.Run(() => (nint)decide(request, popupFeatures, settings), 0);
            if (chosen == 0) block = true;
            else if (chosen != (nint)(*client))
            {
                NativeObject.Release(*client);
                NativeObject.AddRef((void*)chosen);
                *client = (cef_client_t*)chosen;
            }
            else NativeObject.Release(*client);
        }
        else NativeObject.Release(*client);
        NativeObject.Release(frame);
        NativeObject.Release(browser);
        return block ? 1 : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBeforePopupAborted(cef_life_span_handler_t* self, cef_browser_t* browser, int popupId)
    {
        var owner = NativeObject.Owner<LifeSpanHandler>(self);
        if (owner.PopupAborted is { } aborted) Guard.Run(() => aborted(popupId));
        NativeObject.Release(browser);
    }
}

/// <summary>One page in a browser's back/forward list.</summary>
/// <param name="Url">The page's URL.</param>
/// <param name="Title">The page's title.</param>
/// <param name="IsCurrent">Whether it is the page showing.</param>
public sealed record NavigationEntrySnapshot(string Url, string Title, bool IsCurrent);

/// <summary><c>cef_navigation_entry_visitor_t</c>: collects a browser's back/forward list. On the UI thread CEF visits
/// every entry before <c>get_navigation_entries</c> returns.</summary>
public sealed unsafe class NavigationEntryVisitor
{
    public NavigationEntryVisitor()
    {
        Native = NativeObject.Create<cef_navigation_entry_visitor_t>(this);
        Native->visit = &Visit;
    }

    public cef_navigation_entry_visitor_t* Native { get; }

    /// <summary>The entries visited so far, oldest first.</summary>
    public List<NavigationEntrySnapshot> Entries { get; } = [];

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Visit(cef_navigation_entry_visitor_t* self, cef_navigation_entry_t* entry, int current, int index, int total)
    {
        var owner = NativeObject.Owner<NavigationEntryVisitor>(self);
        if (entry->is_valid(entry) != 0)
            owner.Entries.Add(new NavigationEntrySnapshot(CefString.ReadUserFree(entry->get_url(entry)), CefString.ReadUserFree(entry->get_title(entry)), current != 0));
        NativeObject.Release(entry);
        return 1;
    }
}
