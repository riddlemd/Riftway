using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Riftway.Cef.Platform;

/// <summary>Electron v44's <c>WindowButtonsProxy</c>: moves a hidden title bar's traffic lights to a margin, gives their
/// container a custom height, and shows them only under the pointer for <c>customButtonsOnHover</c>. Main thread.</summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacWindowButtons : IDisposable
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    // NSTrackingMouseEnteredAndExited | NSTrackingActiveAlways | NSTrackingInVisibleRect.
    private const nuint TrackingOptions = 0x01 | 0x80 | 0x200;
    private const nuint FullScreenStyle = 1 << 14;
    private static readonly nint s_send = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly nint s_sendStret = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? 0 : NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend_stret");
    private static readonly Dictionary<nint, MacWindowButtons> s_byHoverView = [];

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public double X, Y, Width, Height;
    }

    private readonly nint _window;
    private readonly bool _rightToLeft;
    private readonly (double X, double Y) _defaultMargin;
    private (double X, double Y) _margin;
    private double _height;
    private bool _showOnHover;
    private bool _mouseInside;
    private nint _hoverView;
    private nint _trackingArea;

    private MacWindowButtons(nint window, bool rightToLeft)
    {
        _window = window;
        _rightToLeft = rightToLeft;
        _margin = _defaultMargin = CurrentMargin();
    }

    /// <summary><c>initWithWindow:</c> for the window of <paramref name="contentView"/>; null without one.</summary>
    public static MacWindowButtons? Create(nint contentView, bool rightToLeft)
    {
        var window = contentView == 0 ? 0 : Send(contentView, Sel("window"u8));
        return window == 0 ? null : new MacWindowButtons(window, rightToLeft);
    }

    /// <summary>The margin AppKit gave the buttons before any was set.</summary>
    public (double X, double Y) DefaultMargin => _defaultMargin;

    /// <summary><c>setVisible:</c>: hides or shows the whole title bar container.</summary>
    public void SetVisible(bool visible)
    {
        if (TitleBarContainer() is var container and not 0) SendBool(container, Sel("setHidden:"u8), !visible);
    }

    /// <summary><c>isVisible</c>: true without a container, as Electron.</summary>
    public bool IsVisible => TitleBarContainer() is var container && (container == 0 || SendGetBool(container, Sel("isHidden"u8)) == 0);

    /// <summary><c>setShowOnHover:</c>: a transparent view over the buttons tracks the pointer.</summary>
    public void SetShowOnHover(bool showOnHover)
    {
        var container = TitleBarContainer();
        if (container == 0) return;
        _showOnHover = showOnHover;
        if (showOnHover)
        {
            RemoveHoverView();
            _hoverView = Send(Send(HoverViewClass(), Sel("alloc"u8)), Sel("init"u8));
            s_byHoverView[_hoverView] = this;
            SendRect(_hoverView, Sel("setFrame:"u8), ButtonsBounds());
            Send(container, Sel("addSubview:"u8), _hoverView);
        }
        else RemoveHoverView();
        UpdateButtonsVisibility();
    }

    /// <summary><c>setMargin:</c>; null goes back to <see cref="DefaultMargin"/>.</summary>
    public void SetMargin((double X, double Y)? margin)
    {
        _margin = margin ?? _defaultMargin;
        Redraw();
    }

    /// <summary><c>setHeight:</c>: the container's height, used only when taller than the buttons need.</summary>
    public void SetHeight(double height)
    {
        _height = height;
        Redraw();
    }

    /// <summary><c>redraw</c>: lays the container and the buttons out again.</summary>
    public void Redraw()
    {
        var container = TitleBarContainer();
        if (container == 0) return;
        var (left, middle, right) = (LeftButton(), MiddleButton(), RightButton());
        var frame = Layout().Redraw(Frame(_window).Width, _margin.X, _margin.Y, _height, _rightToLeft);
        var bounds = Frame(container);
        bounds.Height = frame.ContainerHeight;
        bounds.Y = Frame(_window).Height - frame.ContainerHeight;
        SendRect(container, Sel("setFrame:"u8), bounds);
        SendPoint(left, Sel("setFrameOrigin:"u8), frame.LeftX, frame.ButtonY);
        SendPoint(middle, Sel("setFrameOrigin:"u8), frame.MiddleX, frame.ButtonY);
        SendPoint(right, Sel("setFrameOrigin:"u8), frame.RightX, frame.ButtonY);
        if (_hoverView != 0) SendRect(_hoverView, Sel("setFrame:"u8), ButtonsBounds());
    }

    /// <summary>Takes the hover view out of the title bar.</summary>
    public void Dispose() => RemoveHoverView();

    private MacWindowButtonsLayout Layout()
    {
        var left = Frame(LeftButton());
        var middle = Frame(MiddleButton());
        return new MacWindowButtonsLayout(left.Width, left.Height, middle.X - (left.X + left.Width), _defaultMargin.Y);
    }

    // getCurrentMargin.
    private (double X, double Y) CurrentMargin()
    {
        var container = TitleBarContainer();
        if (container == 0) return default;
        var left = Frame(LeftButton());
        var right = Frame(RightButton());
        double y;
        if (_height != 0)
        {
            y = (_height - left.Height) / 2;
            // Do not center buttons if height and button position specified.
            if (_margin.Y != _defaultMargin.Y) y = _height - left.Height - _margin.Y;
        }
        else y = (Frame(container).Height - left.Height) / 2;
        return (_rightToLeft ? Frame(_window).Width - (right.X + right.Width) : left.X, y);
    }

    private void UpdateButtonsVisibility()
    {
        // Always shown in full screen.
        var hidden = _showOnHover && !_mouseInside && ((nuint)Send(_window, Sel("styleMask"u8)) & FullScreenStyle) == 0;
        for (nuint kind = 0; kind <= 2; kind++)
        {
            var button = SendNUIntObject(_window, Sel("standardWindowButton:"u8), kind);
            if (button == 0) continue;
            SendBool(button, Sel("setHidden:"u8), hidden);
            SendBool(button, Sel("setNeedsDisplay:"u8), true);
        }
        // AppKit re-lays the container out when the buttons' hidden state changes (macOS 26).
        Redraw();
    }

    private void UpdateTrackingAreas()
    {
        if (_trackingArea != 0)
        {
            Send(_hoverView, Sel("removeTrackingArea:"u8), _trackingArea);
            Send(_trackingArea, Sel("release"u8));
        }
        _trackingArea = ((delegate* unmanaged<nint, nint, NSRect, nuint, nint, nint, nint>)s_send)(
            Send(Class("NSTrackingArea"u8), Sel("alloc"u8)), Sel("initWithRect:options:owner:userInfo:"u8), default, TrackingOptions, _hoverView, 0);
        Send(_hoverView, Sel("addTrackingArea:"u8), _trackingArea);
    }

    private void RemoveHoverView()
    {
        if (_hoverView == 0) return;
        s_byHoverView.Remove(_hoverView);
        Send(_hoverView, Sel("removeFromSuperview"u8));
        Send(_hoverView, Sel("release"u8));
        _hoverView = 0;
        if (_trackingArea != 0) Send(_trackingArea, Sel("release"u8));
        _trackingArea = 0;
    }

    private NSRect ButtonsBounds()
    {
        var left = Frame(LeftButton());
        var right = Frame(RightButton());
        return new NSRect { X = left.X, Y = left.Y, Width = right.X + right.Width - left.X, Height = left.Height };
    }

    private nint TitleBarContainer()
    {
        var left = LeftButton();
        var superview = left == 0 ? 0 : Send(left, Sel("superview"u8));
        return superview == 0 ? 0 : Send(superview, Sel("superview"u8));
    }

    // NSWindowCloseButton 0, NSWindowMiniaturizeButton 1, NSWindowZoomButton 2.
    private nint LeftButton() => SendNUIntObject(_window, Sel("standardWindowButton:"u8), _rightToLeft ? 2u : 0u);

    private nint MiddleButton() => SendNUIntObject(_window, Sel("standardWindowButton:"u8), 1);

    private nint RightButton() => SendNUIntObject(_window, Sel("standardWindowButton:"u8), _rightToLeft ? 0u : 2u);

    private static nint HoverViewClass()
    {
        fixed (byte* name = "RiftwayButtonsAreaHoverView"u8)
        {
            var cls = objc_getClass(name);
            if (cls != 0) return cls;
            cls = objc_allocateClassPair(Class("NSView"u8), name, 0);
            fixed (byte* types = "@@:{CGPoint=dd}"u8)
                class_addMethod(cls, Sel("hitTest:"u8), (delegate* unmanaged<nint, nint, double, double, nint>)&HitTest, types);
            fixed (byte* types = "v@:"u8)
                class_addMethod(cls, Sel("updateTrackingAreas"u8), (delegate* unmanaged<nint, nint, void>)&OnUpdateTrackingAreas, types);
            fixed (byte* types = "v@:@"u8)
            {
                class_addMethod(cls, Sel("mouseEntered:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnMouseEntered, types);
                class_addMethod(cls, Sel("mouseExited:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnMouseExited, types);
            }
            objc_registerClassPair(cls);
            return cls;
        }
    }

    // Clicks go through to the buttons underneath.
    [UnmanagedCallersOnly]
    private static nint HitTest(nint self, nint cmd, double x, double y) => 0;

    [UnmanagedCallersOnly]
    private static void OnUpdateTrackingAreas(nint self, nint cmd) => Guarded(self, b => b.UpdateTrackingAreas());

    [UnmanagedCallersOnly]
    private static void OnMouseEntered(nint self, nint cmd, nint e) => Guarded(self, b =>
    {
        b._mouseInside = true;
        b.UpdateButtonsVisibility();
    });

    [UnmanagedCallersOnly]
    private static void OnMouseExited(nint self, nint cmd, nint e) => Guarded(self, b =>
    {
        b._mouseInside = false;
        b.UpdateButtonsVisibility();
    });

    private static void Guarded(nint hoverView, Action<MacWindowButtons> action)
    {
        try
        {
            if (s_byHoverView.TryGetValue(hoverView, out var buttons)) action(buttons);
        }
        catch (Exception e) { Console.Error.WriteLine($"[Riftway] Unhandled exception in the window buttons: {e}"); }
    }

    private static NSRect Frame(nint view) => view == 0 ? default : GetRect(view, Sel("frame"u8));

    // NSRect is four doubles: arm64 returns it in registers, x64 through a hidden pointer (_stret).
    private static NSRect GetRect(nint obj, nint selector)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64) return ((delegate* unmanaged<nint, nint, NSRect>)s_send)(obj, selector);
        NSRect rect;
        ((delegate* unmanaged<NSRect*, nint, nint, void>)s_sendStret)(&rect, obj, selector);
        return rect;
    }


    private static void SendRect(nint receiver, nint selector, NSRect rect) => ((delegate* unmanaged<nint, nint, NSRect, void>)s_send)(receiver, selector, rect);

    private static void SendPoint(nint receiver, nint selector, double x, double y)
    {
        if (receiver != 0) ((delegate* unmanaged<nint, nint, double, double, void>)s_send)(receiver, selector, x, y);
    }

    private static void SendBool(nint receiver, nint selector, bool value) => ((delegate* unmanaged<nint, nint, byte, void>)s_send)(receiver, selector, value ? (byte)1 : (byte)0);

    private static byte SendGetBool(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, byte>)s_send)(receiver, selector);

    private static nint SendNUIntObject(nint receiver, nint selector, nuint value) => ((delegate* unmanaged<nint, nint, nuint, nint>)s_send)(receiver, selector, value);

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_send)(receiver, selector);

    private static nint Send(nint receiver, nint selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, nint>)s_send)(receiver, selector, arg);

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
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint name, void* imp, byte* types);
}
