using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>The first view in a tree, depth first, that a predicate takes; for finding Chromium's views under CEF's.</summary>
public static class ViewSearch
{
    /// <summary>The first of <paramref name="root"/> and its descendants, depth first, that <paramref name="match"/>
    /// takes; 0 for none.</summary>
    public static nint First(nint root, Func<nint, IReadOnlyList<nint>> children, Func<nint, bool> match)
    {
        if (root == 0) return 0;
        if (match(root)) return root;
        foreach (var child in children(root))
            if (First(child, children, match) is var found and not 0) return found;
        return 0;
    }
}

/// <summary>Electron v44's <c>WebContents::ShowDefinitionForSelection</c>: Chromium's
/// <c>RenderWidgetHostNSViewBridge::ShowDictionaryOverlayForSelection</c> on the page's
/// <c>RenderWidgetHostViewCocoa</c>, found under the browser's view. Main thread.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacLookUp
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private static readonly nint s_send = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRange
    {
        public nuint Location, Length;
    }

    /// <summary>Shows the Look Up panel for the page's selection; does nothing when the page has no Chromium view yet.</summary>
    public static void ShowDefinitionForSelection(nint browserView)
    {
        var cocoa = Class("RenderWidgetHostViewCocoa"u8);
        if (cocoa == 0) return;
        var view = ViewSearch.First(browserView, Subviews, v => ((delegate* unmanaged<nint, nint, nint, byte>)s_send)(v, Sel("isKindOfClass:"u8), cocoa) != 0);
        if (view == 0) return;
        var range = ((delegate* unmanaged<nint, nint, NSRange>)s_send)(view, Sel("selectedRange"u8));
        ((delegate* unmanaged<nint, nint, NSRange, void>)s_send)(view, Sel("showLookUpDictionaryOverlayFromRange:"u8), range);
    }

    private static IReadOnlyList<nint> Subviews(nint view)
    {
        var array = ((delegate* unmanaged<nint, nint, nint>)s_send)(view, Sel("subviews"u8));
        var count = array == 0 ? 0 : (int)((delegate* unmanaged<nint, nint, nint>)s_send)(array, Sel("count"u8));
        var result = new nint[count];
        for (var i = 0; i < count; i++) result[i] = ((delegate* unmanaged<nint, nint, nint, nint>)s_send)(array, Sel("objectAtIndex:"u8), i);
        return result;
    }

    private static nint Class(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_getClass(p);
    }

    private static nint Sel(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return sel_registerName(p);
    }

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
}
