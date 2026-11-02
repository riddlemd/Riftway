using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Riftway.Cef.Platform;

/// <summary>What a <see cref="LinuxGtkMenuItem"/> is, as <c>ui::MenuModel::ItemType</c>.</summary>
public enum LinuxGtkMenuItemType
{
    Command,
    Check,
    Radio,
    Separator,
    Submenu,
}

/// <summary>One entry of a menu model as Electron's <c>MenuGtk</c> reads it: the label (GTK mnemonic syntax) and type
/// when it builds the menu, the state through the callbacks when it refreshes or activates.</summary>
public sealed class LinuxGtkMenuItem
{
    public required LinuxGtkMenuItemType Type { get; init; }
    public string Label { get; init; } = "";
    public int GroupId { get; init; }
    public IReadOnlyList<LinuxGtkMenuItem>? Submenu { get; init; }
    public Func<bool> IsEnabled { get; init; } = () => true;
    public Func<bool> IsVisible { get; init; } = () => true;
    public Func<bool> IsChecked { get; init; } = () => false;

    /// <summary>A submenu item's <c>MenuWillShow</c> on its submenu model, which <c>BuildSubmenuFromModel</c> calls once
    /// the submenu is built.</summary>
    public Action? SubmenuWillShow { get; init; }

    /// <summary><c>ActivatedAt</c>, with the Shift, Control and Alt state of the button release that chose it.</summary>
    public Action<bool, bool, bool>? Activated { get; init; }
}

/// <summary>Electron's <c>StatusIconGtk</c> (an XEmbed <c>GtkStatusIcon</c>) with its <c>MenuGtk</c> context menu: the
/// tray Electron falls back to when no StatusNotifierItem host answers. UI thread (GTK's main thread), GTK 3 loaded.</summary>
[SupportedOSPlatform("linux")]
public sealed unsafe partial class LinuxStatusIconGtk : IDisposable
{
    private const string LibGtk = "libgtk-3.so.0";
    private const string LibGdk = "libgdk-3.so.0";
    private const string LibPixbuf = "libgdk_pixbuf-2.0.so.0";
    private const string LibGObject = "libgobject-2.0.so.0";
    private const string LibGLib = "libglib-2.0.so.0";
    private const int GdkButtonRelease = 7;
    private const uint GdkShiftMask = 1 << 0, GdkControlMask = 1 << 2, GdkMod1Mask = 1 << 3;

    private readonly Action _onClick;
    private GCHandle _self;
    private nint _icon;
    private nint _menu;
    private readonly Dictionary<nint, LinuxGtkMenuItem> _items = [];
    private readonly List<(nint Instance, ulong Id)> _menuSignals = [];
    private readonly List<ulong> _iconSignals = [];
    private bool _blockActivation;

    /// <summary><c>gtk_status_icon_new()</c>, with <paramref name="onClick"/> for its <c>activate</c> signal.</summary>
    public LinuxStatusIconGtk(Action onClick)
    {
        _onClick = onClick;
        _self = GCHandle.Alloc(this);
        _icon = gtk_status_icon_new();
        _iconSignals.Add(Connect(_icon, "activate", (nint)(delegate* unmanaged<nint, nint, void>)&OnActivate));
        _iconSignals.Add(Connect(_icon, "popup_menu", (nint)(delegate* unmanaged<nint, uint, uint, nint, void>)&OnPopupMenu));
    }

    /// <summary>The <c>GtkStatusIcon</c>; for tests.</summary>
    public nint Handle => _icon;

    /// <summary>The context menu's <c>GtkMenu</c>, or 0; for tests.</summary>
    public nint MenuHandle => _menu;

    /// <summary>Whether GTK 3 is there to make one: Chromium's <c>GtkUi</c> loaded it with a display.</summary>
    public static bool Available => LinuxGtkTheme.IsLoaded;

    /// <summary><c>SetImage</c>: unpremultiplied RGBA rows; an empty image is ignored, as a null <c>ImageSkia</c>.</summary>
    public void SetImage(int width, int height, byte[] rgba)
    {
        if (width == 0 || height == 0) return;
        // gtk_util::GdkPixbufFromSkBitmap: packed RGBA, 8 bits, with alpha.
        nint pixbuf;
        fixed (byte* data = rgba)
        {
            var bytes = g_bytes_new(data, (nuint)rgba.Length);
            pixbuf = gdk_pixbuf_new_from_bytes(bytes, 0, 1, 8, width, height, gdk_pixbuf_calculate_rowstride(0, 1, 8, width, height));
            g_bytes_unref(bytes);
        }
        gtk_status_icon_set_from_pixbuf(_icon, pixbuf);
        g_object_unref(pixbuf);
    }

    public void SetToolTip(string toolTip)
    {
        fixed (byte* text = Utf8(toolTip)) gtk_status_icon_set_tooltip_text(_icon, text);
    }

    /// <summary><c>UpdatePlatformContextMenu</c>: a new <c>MenuGtk</c> for the model, or none.</summary>
    public void SetMenu(IReadOnlyList<LinuxGtkMenuItem>? model)
    {
        DestroyMenu();
        if (model is null) return;
        _menu = g_object_ref_sink(gtk_menu_new());
        BuildSubmenu(model, _menu);
        Refresh(_menu);
    }

    public void SetVisible(bool visible) => gtk_status_icon_set_visible(_icon, visible ? 1 : 0);

    public void Dispose()
    {
        if (_icon == 0) return;
        foreach (var id in _iconSignals) g_signal_handler_disconnect(_icon, id);
        _iconSignals.Clear();
        DestroyMenu();
        g_object_unref(_icon);
        _icon = 0;
        _self.Free();
    }

    private void DestroyMenu()
    {
        if (_menu == 0) return;
        foreach (var (instance, id) in _menuSignals) g_signal_handler_disconnect(instance, id);
        _menuSignals.Clear();
        _items.Clear();
        gtk_widget_destroy(_menu);
        g_object_unref(_menu);
        _menu = 0;
    }

    // menu_util.cc BuildSubmenuFromModel. Accelerators are left off: Electron passes no GtkAccelGroup, which
    // gtk_widget_add_accelerator refuses, and item icons wait on MenuItem.icon (DRIFT M3).
    private void BuildSubmenu(IReadOnlyList<LinuxGtkMenuItem> model, nint menu)
    {
        var radioGroups = new Dictionary<int, nint>();
        foreach (var item in model)
        {
            nint widget;
            fixed (byte* label = Utf8(item.Label))
            {
                switch (item.Type)
                {
                    case LinuxGtkMenuItemType.Separator:
                        widget = gtk_separator_menu_item_new();
                        break;
                    case LinuxGtkMenuItemType.Check:
                        widget = gtk_check_menu_item_new_with_mnemonic(label);
                        break;
                    case LinuxGtkMenuItemType.Radio when radioGroups.TryGetValue(item.GroupId, out var first):
                        widget = gtk_radio_menu_item_new_with_mnemonic_from_widget(first, label);
                        break;
                    case LinuxGtkMenuItemType.Radio:
                        widget = gtk_radio_menu_item_new_with_mnemonic(0, label);
                        radioGroups[item.GroupId] = widget;
                        break;
                    default:
                        widget = gtk_menu_item_new_with_mnemonic(label);
                        break;
                }
            }
            var connectToActivate = true;
            if (item.Type == LinuxGtkMenuItemType.Submenu)
            {
                var submenu = gtk_menu_new();
                BuildSubmenu(item.Submenu ?? [], submenu);
                gtk_menu_item_set_submenu(widget, submenu);
                Refresh(submenu);
                item.SubmenuWillShow?.Invoke();
                connectToActivate = false;
            }
            _items[widget] = item;
            if (connectToActivate) _menuSignals.Add((widget, Connect(widget, "activate", (nint)(delegate* unmanaged<nint, nint, void>)&OnMenuItemActivated)));
            if (item.IsVisible()) gtk_widget_show(widget);
            gtk_menu_shell_append(menu, widget);
        }
    }

    // menu_util.cc SetMenuItemInfo over every child; the labels are not dynamic, so only state is refreshed.
    private void Refresh(nint menu)
    {
        var children = gtk_container_get_children(menu);
        for (var node = children; node != null; node = node->Next)
        {
            var widget = node->Data;
            if (!_items.TryGetValue(widget, out var item) || item.Type == LinuxGtkMenuItemType.Separator) continue;
            if (item.Type is LinuxGtkMenuItemType.Check or LinuxGtkMenuItemType.Radio)
            {
                // Setting the state fires activate; that must not run the item.
                _blockActivation = true;
                gtk_check_menu_item_set_active(widget, item.IsChecked() ? 1 : 0);
                _blockActivation = false;
            }
            gtk_widget_set_sensitive(widget, item.IsEnabled() ? 1 : 0);
            if (item.IsVisible()) gtk_widget_show(widget);
            else gtk_widget_hide(widget);
            var submenu = gtk_menu_item_get_submenu(widget);
            if (submenu != 0) Refresh(submenu);
        }
        g_list_free(children);
    }

    private ulong Connect(nint instance, string signal, nint callback)
    {
        fixed (byte* name = Utf8(signal)) return g_signal_connect_data(instance, name, callback, GCHandle.ToIntPtr(_self), 0, 0);
    }

    private static LinuxStatusIconGtk? From(nint data) => GCHandle.FromIntPtr(data).Target as LinuxStatusIconGtk;

    [UnmanagedCallersOnly]
    private static void OnActivate(nint icon, nint data)
    {
        try { From(data)?._onClick(); }
        catch (Exception e) when (e is not OutOfMemoryException) { Console.Error.WriteLine($"[Riftway] A tray click threw: {e}"); }
    }

    [UnmanagedCallersOnly]
    private static void OnPopupMenu(nint icon, uint button, uint time, nint data)
    {
        if (From(data) is not { _menu: not 0 } self) return;
        gtk_menu_popup(self._menu, 0, 0, NativeLibrary.GetExport(NativeLibrary.Load(LibGtk), "gtk_status_icon_position_menu"), self._icon, button, time);
    }

    // MenuGtk::OnMenuItemActivated, then menu_util's ExecuteCommand.
    [UnmanagedCallersOnly]
    private static void OnMenuItemActivated(nint widget, nint data)
    {
        try
        {
            if (From(data) is not { } self || self._blockActivation || !self._items.TryGetValue(widget, out var item)) return;
            // Radio items also get activate as they are deselected.
            if (item.Type == LinuxGtkMenuItemType.Radio && gtk_check_menu_item_get_active(widget) == 0) return;
            // Hotkeys can activate a disabled item.
            if (!item.IsEnabled()) return;
            uint state = 0;
            var e = gtk_get_current_event();
            if (e != 0)
            {
                // GdkEventButton: type at 0, state at 48 on 64-bit.
                if (*(int*)e == GdkButtonRelease) state = *(uint*)(e + 48);
                gdk_event_free(e);
            }
            item.Activated?.Invoke((state & GdkShiftMask) != 0, (state & GdkControlMask) != 0, (state & GdkMod1Mask) != 0);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { Console.Error.WriteLine($"[Riftway] A tray menu item threw: {e}"); }
    }

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    [StructLayout(LayoutKind.Sequential)]
    private struct GList
    {
        public nint Data;
        public GList* Next;
        public GList* Prev;
    }

    [LibraryImport(LibGtk)] private static partial nint gtk_status_icon_new();
    [LibraryImport(LibGtk)] private static partial void gtk_status_icon_set_from_pixbuf(nint icon, nint pixbuf);
    [LibraryImport(LibGtk)] private static partial void gtk_status_icon_set_tooltip_text(nint icon, byte* text);
    [LibraryImport(LibGtk)] private static partial void gtk_status_icon_set_visible(nint icon, int visible);
    [LibraryImport(LibGtk)] private static partial nint gtk_menu_new();
    [LibraryImport(LibGtk)] private static partial void gtk_menu_popup(nint menu, nint parentShell, nint parentItem, nint positionFunc, nint data, uint button, uint time);
    [LibraryImport(LibGtk)] private static partial nint gtk_separator_menu_item_new();
    [LibraryImport(LibGtk)] private static partial nint gtk_check_menu_item_new_with_mnemonic(byte* label);
    [LibraryImport(LibGtk)] private static partial nint gtk_radio_menu_item_new_with_mnemonic(nint group, byte* label);
    [LibraryImport(LibGtk)] private static partial nint gtk_radio_menu_item_new_with_mnemonic_from_widget(nint group, byte* label);
    [LibraryImport(LibGtk)] private static partial nint gtk_menu_item_new_with_mnemonic(byte* label);
    [LibraryImport(LibGtk)] private static partial void gtk_menu_item_set_submenu(nint item, nint submenu);
    [LibraryImport(LibGtk)] private static partial nint gtk_menu_item_get_submenu(nint item);
    [LibraryImport(LibGtk)] private static partial void gtk_menu_shell_append(nint shell, nint child);
    [LibraryImport(LibGtk)] private static partial void gtk_check_menu_item_set_active(nint item, int active);
    [LibraryImport(LibGtk)] private static partial int gtk_check_menu_item_get_active(nint item);
    [LibraryImport(LibGtk)] private static partial void gtk_widget_set_sensitive(nint widget, int sensitive);
    [LibraryImport(LibGtk)] private static partial void gtk_widget_show(nint widget);
    [LibraryImport(LibGtk)] private static partial void gtk_widget_hide(nint widget);
    [LibraryImport(LibGtk)] private static partial void gtk_widget_destroy(nint widget);
    [LibraryImport(LibGtk)] private static partial GList* gtk_container_get_children(nint container);
    [LibraryImport(LibGtk)] private static partial nint gtk_get_current_event();
    [LibraryImport(LibGdk)] private static partial void gdk_event_free(nint e);
    [LibraryImport(LibPixbuf)] private static partial nint gdk_pixbuf_new_from_bytes(nint bytes, int colorspace, int hasAlpha, int bitsPerSample, int width, int height, int rowstride);
    [LibraryImport(LibPixbuf)] private static partial int gdk_pixbuf_calculate_rowstride(int colorspace, int hasAlpha, int bitsPerSample, int width, int height);
    [LibraryImport(LibGLib)] private static partial nint g_bytes_new(void* data, nuint size);
    [LibraryImport(LibGLib)] private static partial void g_bytes_unref(nint bytes);
    [LibraryImport(LibGLib)] private static partial void g_list_free(GList* list);
    [LibraryImport(LibGObject)] private static partial nint g_object_ref_sink(nint obj);
    [LibraryImport(LibGObject)] private static partial void g_object_unref(nint obj);
    [LibraryImport(LibGObject)] private static partial ulong g_signal_connect_data(nint instance, byte* signal, nint handler, nint data, nint destroy, int flags);
    [LibraryImport(LibGObject)] private static partial void g_signal_handler_disconnect(nint instance, ulong id);
}
