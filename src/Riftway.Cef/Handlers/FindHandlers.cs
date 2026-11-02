using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Riftway.Cef.Interop;

namespace Riftway.Cef.Handlers;

/// <summary>One report on a find in the page: matches so far, the active one (from 1), where it is in the view, and
/// whether the search has finished.</summary>
public sealed record FindResult(int Matches, int ActiveMatchOrdinal, cef_rect_t Selection, bool FinalUpdate);

/// <summary><c>cef_find_handler_t</c>: reports what <c>find</c> found.</summary>
public sealed unsafe class FindHandler
{
    public FindHandler()
    {
        Native = NativeObject.Create<cef_find_handler_t>(this);
        Native->on_find_result = &OnFindResult;
    }

    public cef_find_handler_t* Native { get; }

    /// <summary>UI thread.</summary>
    public Action<FindResult>? Result { get; init; }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFindResult(cef_find_handler_t* self, cef_browser_t* browser, int identifier, int count, cef_rect_t* selection,
        int activeMatchOrdinal, int finalUpdate)
    {
        var result = new FindResult(count, activeMatchOrdinal, selection is null ? default : *selection, finalUpdate != 0);
        if (NativeObject.Owner<FindHandler>(self).Result is { } report) Guard.Run(() => report(result));
        NativeObject.Release(browser);
    }
}
