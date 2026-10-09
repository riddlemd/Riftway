using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Oriel.Cef;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>The pure parts of Electron's Linux tray: Chromium's <c>StatusIconLinuxDbus</c> and <c>DbusMenu</c> encodings.</summary>
internal static class LinuxTrayRules
{
    internal const string ItemInterface = "org.kde.StatusNotifierItem";
    internal const string ItemInterfaceFreedesktop = "org.freedesktop.StatusNotifierItem";
    internal const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    /// <summary><c>SkUnPreMultiply::gTable[alpha]</c>: (255 &lt;&lt; 24) / alpha, rounded.</summary>
    internal static uint UnpremultiplyScale(int alpha) => alpha == 0 ? 0 : ((255u << 24) + (uint)(alpha >> 1)) / (uint)alpha;

    /// <summary><c>SkUnPreMultiply::ApplyScale</c>, wrapping as Skia's 32-bit arithmetic does.</summary>
    internal static byte ApplyScale(uint scale, int component) => (byte)(unchecked(scale * (uint)component + (1u << 23)) >> 24);

    /// <summary>Each pixel as <c>SkBitmap::getColor</c> gives it (unpremultiplied), in <paramref name="order"/>: indices
    /// into (A, R, G, B).</summary>
    private static byte[] Unpremultiplied(ImageBitmap bitmap, ReadOnlySpan<int> order)
    {
        var src = bitmap.Bgra;
        var dst = new byte[bitmap.Width * bitmap.Height * 4];
        Span<byte> argb = stackalloc byte[4];
        for (var i = 0; i < dst.Length; i += 4)
        {
            var a = src[i + 3];
            var scale = UnpremultiplyScale(a);
            argb[0] = a;
            argb[1] = ApplyScale(scale, src[i + 2]);
            argb[2] = ApplyScale(scale, src[i + 1]);
            argb[3] = ApplyScale(scale, src[i]);
            for (var c = 0; c < 4; c++) dst[i + c] = argb[order[c]];
        }
        return dst;
    }

    /// <summary><c>MakeDbusImage</c>'s pixels: ARGB32 in network byte order, row by row.</summary>
    internal static byte[] IconPixmap(ImageBitmap bitmap) => Unpremultiplied(bitmap, [0, 1, 2, 3]);

    /// <summary><c>gtk_util::GdkPixbufFromSkBitmap</c>'s pixels: unpremultiplied RGBA.</summary>
    internal static byte[] Rgba(ImageBitmap bitmap) => Unpremultiplied(bitmap, [1, 2, 3, 0]);

    /// <summary>Electron's <c>GetBestImageRep</c>: the representation with the largest scale, used as 1x; null for an
    /// empty image (a null <c>ImageSkia</c>).</summary>
    internal static ImageBitmap? BestRepresentation(NativeImage image)
    {
        var scales = image.GetScaleFactors();
        return scales.Count == 0 ? null : image.BitmapAt(scales.Max());
    }

    /// <summary>The <c>IconPixmap</c> value, <c>a(iiay)</c>: one image, empty for a null image.</summary>
    internal static LinuxDBusVariant IconPixmapValue(ImageBitmap? bitmap)
        => new("a(iiay)", new List<object?> { new object?[] { bitmap?.Width ?? 0, bitmap?.Height ?? 0, bitmap is { } b ? IconPixmap(b) : Array.Empty<byte>() } });

    /// <summary><c>MakeDbusToolTip</c>: no icon, the text as the title, no subtitle.</summary>
    internal static LinuxDBusVariant ToolTipValue(string text) => new("(sa(iiay)ss)", new object?[] { "", new List<object?>(), text, "" });

    /// <summary><c>ui::ConvertAcceleratorsFromWindowsStyle</c>: <c>&amp;</c> marks the mnemonic as GTK's <c>_</c>,
    /// <c>&amp;&amp;</c> is an ampersand, and an underscore is doubled.</summary>
    internal static string MenuLabel(string label)
    {
        var result = new StringBuilder(label.Length * 2);
        for (var i = 0; i < label.Length; i++)
        {
            if (label[i] == '_') result.Append("__");
            else if (label[i] == '&')
            {
                if (i + 1 < label.Length && label[i + 1] == '&')
                {
                    result.Append('&');
                    i++;
                }
                else result.Append('_');
            }
            else result.Append(label[i]);
        }
        return result.ToString();
    }

    /// <summary><c>GetDbusMenuShortcut</c> for an Electron accelerator: the modifiers then the key as the US layout's
    /// unshifted character; null when the key prints no character (Enter, arrows, function keys).</summary>
    internal static string[]? Shortcut(string accelerator)
    {
        var parsed = Accelerator.Parse(accelerator, macOS: false);
        var key = UsLayoutCharacter(parsed.KeyCode);
        if (key is null) return null;
        var parts = new List<string>();
        if (parsed.Modifiers.HasFlag(AcceleratorModifiers.Control)) parts.Add("Control");
        if (parsed.Modifiers.HasFlag(AcceleratorModifiers.Alt)) parts.Add("Alt");
        // Electron's "Plus" is VKEY_OEM_PLUS with Shift.
        if (parsed.Modifiers.HasFlag(AcceleratorModifiers.Shift) || parsed.MacKeyEquivalent == "+") parts.Add("Shift");
        if (parsed.Modifiers.HasFlag(AcceleratorModifiers.Command)) parts.Add("Super");
        parts.Add(key);
        return [.. parts];
    }

    // DomCodeToUsLayoutCharacter(UsLayoutKeyboardCodeToDomCode(code), 0): the printable code map, unshifted.
    private static string? UsLayoutCharacter(int keyCode) => keyCode switch
    {
        >= 'A' and <= 'Z' => ((char)(keyCode + 32)).ToString(),
        >= '0' and <= '9' => ((char)keyCode).ToString(),
        0x20 => " ",
        0xBA => ";",
        0xBB => "=",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDC => "\\",
        0xDD => "]",
        0xDE => "'",
        _ => null,
    };

    /// <summary><c>base::nix::GetDesktopEnvironment</c>, by name (<c>OTHER</c> when none matches).</summary>
    internal static string DesktopEnvironment(Func<string, string?> environment)
    {
        if (environment("XDG_CURRENT_DESKTOP") is { } current)
        {
            foreach (var value in current.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                switch (value)
                {
                    case "Unity": return (environment("DESKTOP_SESSION") ?? "").Contains("gnome-fallback", StringComparison.Ordinal) ? "GNOME" : "UNITY";
                    case "Deepin": return "DEEPIN";
                    case "GNOME": return "GNOME";
                    case "X-Cinnamon": return "CINNAMON";
                    case "KDE": return (environment("KDE_SESSION_VERSION") ?? "") switch { "5" => "KDE5", "6" => "KDE6", _ => "KDE4" };
                    case "Pantheon": return "PANTHEON";
                    case "XFCE": return "XFCE";
                    case "UKUI": return "UKUI";
                    case "LXQt": return "LXQT";
                    case "COSMIC": return "COSMIC";
                }
            }
        }
        var session = environment("DESKTOP_SESSION") ?? "";
        if (session == "deepin") return "DEEPIN";
        if (session is "gnome" or "mate") return "GNOME";
        if (session is "kde4" or "kde-plasma") return "KDE4";
        if (session == "kde") return environment("KDE_SESSION_VERSION") is not null ? "KDE4" : "KDE3";
        if (session.Contains("xfce", StringComparison.Ordinal) || session == "xubuntu") return "XFCE";
        if (session == "ukui") return "UKUI";
        if (environment("GNOME_DESKTOP_SESSION_ID") is not null) return "GNOME";
        if (environment("KDE_FULL_SESSION") is not null) return environment("KDE_SESSION_VERSION") is not null ? "KDE4" : "KDE3";
        return "OTHER";
    }

    /// <summary><c>ShouldWriteIconToFile</c>: GNOME's AppIndicator extension and Pantheon's indicator take icons as
    /// files only.</summary>
    internal static bool ShouldWriteIconToFile(Func<string, string?> environment) => DesktopEnvironment(environment) is "GNOME" or "PANTHEON";

    /// <summary>The patch Electron carries (<c>IsSandboxedWithoutOwnName</c>): Flatpak and Snap forbid owning a name.</summary>
    internal static bool IsSandboxedWithoutOwnName(Func<string, string?> environment)
        => environment("FLATPAK_SANDBOX_DIR") is not null || environment("SNAP") is not null;

    // MenuGtk reads Electron's model: labels and types when built, state through the delegate.
    internal static IReadOnlyList<LinuxGtkMenuItem>? GtkModel(Menu? menu)
    {
        if (menu is null) return null;
        var items = menu.Items;
        var result = new List<LinuxGtkMenuItem>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var type = item.Type switch
            {
                MenuItemType.Separator => LinuxGtkMenuItemType.Separator,
                MenuItemType.Checkbox => LinuxGtkMenuItemType.Check,
                MenuItemType.Radio => LinuxGtkMenuItemType.Radio,
                MenuItemType.Submenu when item.Submenu is not null => LinuxGtkMenuItemType.Submenu,
                _ => LinuxGtkMenuItemType.Command,
            };
            var separator = type == LinuxGtkMenuItemType.Separator;
            result.Add(new LinuxGtkMenuItem
            {
                Type = type,
                Label = separator ? "" : LinuxTrayRules.MenuLabel(item.Label),
                GroupId = item.GroupId,
                Submenu = type == LinuxGtkMenuItemType.Submenu ? GtkModel(item.Submenu) : null,
                SubmenuWillShow = type == LinuxGtkMenuItemType.Submenu ? () => item.Submenu!.OnMenuWillShow() : null,
                IsEnabled = () => separator || item.Enabled,
                IsVisible = () => separator || item.Visible,
                IsChecked = () => item.Checked,
                Activated = separator ? null : (shift, ctrl, alt) => item.Run(BrowserWindow.GetFocusedWindow(), new KeyboardEvent { ShiftKey = shift, CtrlKey = ctrl, AltKey = alt }),
            });
        }
        return result;
    }

    /// <summary>The language of Chromium's application locale on Linux (<c>GetApplicationLocale</c>): the first of
    /// GLib's language names that has localisation data, else <c>en-US</c>. Only the language matters here: it alone
    /// sets the text direction.</summary>
    internal static string ApplicationLanguage(IEnumerable<string> candidates, Func<string, bool> hasLocaleData)
        => candidates.Select(c => c.Split('-')[0]).FirstOrDefault(hasLocaleData) ?? "en-US";

    /// <summary><c>DbusTextDirection()</c>, trailing space included: right to left as ICU orients the locale.</summary>
    internal static string TextDirection(string locale)
    {
        try
        {
            var name = locale.Split('@')[0];
            return CultureInfo.GetCultureInfo(name).TextInfo.IsRightToLeft ? "rtl " : "ltr";
        }
        catch (CultureNotFoundException) { return "ltr"; }
    }
}

/// <summary>A reply to an exported method: values of a signature, or a named error. A handler that gives none (null)
/// gets Chromium's <c>Failed</c> error.</summary>
internal readonly record struct DbusReply(string Signature, object?[] Values, string? ErrorName = null, string? ErrorMessage = null)
{
    internal static readonly DbusReply Empty = new("", []);
}

/// <summary>Chromium 154's <c>DbusMenu</c> (components/dbus/menu): an Oriel <see cref="Menu"/> as
/// <c>com.canonical.dbusmenu</c>, with its <c>org.freedesktop.DBus.Properties</c>. UI thread only.</summary>
internal sealed class DbusMenu
{
    internal const string Interface = "com.canonical.dbusmenu";

    private sealed class Item(int id, SortedDictionary<string, LinuxDBusVariant> properties, List<int> children, Menu? menu, Menu? containingMenu, int containingIndex)
    {
        public readonly int Id = id;
        public readonly SortedDictionary<string, LinuxDBusVariant> Properties = properties;
        public readonly List<int> Children = children;
        // The submenu this item opens, if any.
        public readonly Menu? Menu = menu;
        public readonly Menu? ContainingMenu = containingMenu;
        public readonly int ContainingIndex = containingIndex;
    }

    private readonly SortedDictionary<int, Item> _items = [];
    private readonly Action<string, string, object?[]> _emit;
    private readonly Action<MenuItem> _activate;
    private int _lastItemId;
    private uint _revision;

    /// <summary>Starts with an empty root; <paramref name="emit"/> sends a signal (member, signature, arguments) from the
    /// menu object, <paramref name="activate"/> runs an item as <c>ActivatedAt</c>.</summary>
    public DbusMenu(Action<string, string, object?[]> emit, Action<MenuItem> activate, string textDirection)
    {
        _emit = emit;
        _activate = activate;
        Properties = new(StringComparer.Ordinal)
        {
            ["IconThemePath"] = new("as", new List<object?>()),
            ["Status"] = new("s", "normal"),
            ["TextDirection"] = new("s", textDirection),
            ["Version"] = new("u", 3u),
        };
        SetModel(null, false);
    }

    /// <summary>The <c>com.canonical.dbusmenu</c> properties.</summary>
    public SortedDictionary<string, LinuxDBusVariant> Properties { get; }

    /// <summary>The layout revision.</summary>
    public uint Revision => _revision;

    /// <summary>Rebuilds every item from <paramref name="model"/>; ids keep counting up. Electron's tray never sends
    /// <c>ItemsPropertiesUpdated</c>: a changed item shows after the menu is set again.</summary>
    public void SetModel(Menu? model, bool sendSignal)
    {
        _items.Clear();
        var properties = new SortedDictionary<string, LinuxDBusVariant>(StringComparer.Ordinal);
        List<int> children = [];
        if (model is not null)
        {
            properties["children-display"] = new("s", "submenu");
            children = ConvertMenu(model);
        }
        _items[0] = new Item(0, properties, children, null, null, -1);
        if (sendSignal) _emit("LayoutUpdated", "ui", [++_revision, 0]);
    }

    /// <summary>Whether the menu object exports the member: <c>com.canonical.dbusmenu</c>'s methods and its properties
    /// object's. libdbus answers the others.</summary>
    public static bool Exports(string iface, string member) => (iface, member) switch
    {
        (Interface, "AboutToShow" or "AboutToShowGroup" or "Event" or "EventGroup" or "GetGroupProperties" or "GetLayout" or "GetProperty") => true,
        (LinuxTrayRules.PropertiesInterface, "Get" or "GetAll" or "Set") => true,
        _ => false,
    };

    /// <summary>Runs an exported method; null means Chromium's error reply.</summary>
    public DbusReply? Handle(string iface, string member, object?[] args) => (iface, member) switch
    {
        (Interface, "AboutToShow") => AboutToShow(args),
        (Interface, "AboutToShowGroup") => AboutToShowGroup(args),
        (Interface, "Event") => Event(args),
        (Interface, "EventGroup") => EventGroup(args),
        (Interface, "GetGroupProperties") => GetGroupProperties(args),
        (Interface, "GetLayout") => GetLayout(args),
        (Interface, "GetProperty") => GetProperty(args),
        (LinuxTrayRules.PropertiesInterface, "Get") => GetDbusProperty(args),
        (LinuxTrayRules.PropertiesInterface, "GetAll") => GetAllDbusProperties(args),
        // DbusProperties::OnSetProperty: not implemented, answered with an empty reply.
        (LinuxTrayRules.PropertiesInterface, "Set") => DbusReply.Empty,
        _ => null,
    };

    /// <summary><c>ComputeMenuPropertiesForMenuItem</c> for an Electron menu item: only values that differ from the
    /// defaults.</summary>
    internal static SortedDictionary<string, LinuxDBusVariant> PropertiesFor(MenuItem item)
    {
        var properties = new SortedDictionary<string, LinuxDBusVariant>(StringComparer.Ordinal);
        // A separator's command id is -1, which no Electron item has: no label, always enabled and visible.
        if (item.Type == MenuItemType.Separator)
        {
            properties["type"] = new("s", "separator");
            return properties;
        }
        if (item.Label.Length > 0) properties["label"] = new("s", LinuxTrayRules.MenuLabel(item.Label));
        if (!item.Enabled) properties["enabled"] = new("b", false);
        if (!item.Visible) properties["visible"] = new("b", false);
        // GetAcceleratorForCommandId asks without the role's default accelerator.
        if (item.ExplicitAccelerator is { } accelerator && LinuxTrayRules.Shortcut(accelerator) is { } parts)
            properties["shortcut"] = new("aas", new List<object?> { parts.Cast<object?>().ToList() });
        switch (item.Type)
        {
            case MenuItemType.Checkbox or MenuItemType.Radio:
                properties["toggle-type"] = new("s", item.Type == MenuItemType.Checkbox ? "checkmark" : "radio");
                properties["toggle-state"] = new("i", item.Checked ? 1 : 0);
                break;
            case MenuItemType.Submenu when item.Submenu is not null:
                properties["children-display"] = new("s", "submenu");
                break;
        }
        return properties;
    }

    // Electron inserts a submenu model only for a submenu item.
    private static Menu? SubmenuOf(MenuItem item) => item.Type == MenuItemType.Submenu ? item.Submenu : null;

    private List<int> ConvertMenu(Menu? menu)
    {
        List<int> ids = [];
        if (menu is null) return ids;
        var items = menu.Items;
        for (var i = 0; i < items.Count; i++)
        {
            var submenu = SubmenuOf(items[i]);
            // Children first: a submenu's items get lower ids than the item that opens it.
            var children = ConvertMenu(submenu);
            var id = NextItemId();
            _items[id] = new Item(id, PropertiesFor(items[i]), children, submenu, menu, i);
            ids.Add(id);
        }
        return ids;
    }

    private int NextItemId() => _lastItemId = _lastItemId == int.MaxValue ? 1 : _lastItemId + 1;

    private DbusReply? AboutToShow(object?[] args)
    {
        if (args is not [int id, ..] || !AboutToShowImpl(id)) return null;
        // No update needed.
        return new("b", [false]);
    }

    private DbusReply? AboutToShowGroup(object?[] args)
    {
        if (args is not [List<object?> ids, ..] || ids.Any(i => i is not int)) return null;
        var errors = ids.Cast<int>().Where(id => !AboutToShowImpl(id)).Cast<object?>().ToList();
        return new("aiai", [new List<object?>(), errors]);
    }

    private DbusReply? Event(object?[] args) => EventImpl(args, out _) ? DbusReply.Empty : null;

    private DbusReply? EventGroup(object?[] args)
    {
        if (args is not [List<object?> events, ..]) return null;
        var errors = new List<object?>();
        foreach (var e in events)
        {
            if (EventImpl(e as object?[] ?? [], out var idError)) continue;
            if (idError < 0) return null;
            errors.Add(idError);
        }
        return new("ai", [errors]);
    }

    private DbusReply? GetGroupProperties(object?[] args)
    {
        if (args is not [List<object?> ids, List<object?> names, ..] || ids.Any(i => i is not int) || names.Any(n => n is not string)) return null;
        var filter = names.Cast<string>().ToHashSet(StringComparer.Ordinal);
        var result = new List<object?>();
        object?[] Write(Item item) => [item.Id, Filtered(item.Properties, p => filter.Count == 0 || filter.Contains(p))];
        if (ids.Count == 0) result.AddRange(_items.Values.Select(Write));
        else foreach (var id in ids.Cast<int>()) if (_items.TryGetValue(id, out var item)) result.Add(Write(item));
        return new("a(ia{sv})", [result]);
    }

    private DbusReply? GetLayout(object?[] args)
    {
        if (args is not [int id, int depth, List<object?> names, ..] || depth < -1 || names.Any(n => n is not string)) return null;
        if (!_items.TryGetValue(id, out var item)) return null;
        return new("u(ia{sv}av)", [_revision, WriteMenuItem(item, depth, names.Cast<string>().ToList())]);
    }

    private DbusReply? GetProperty(object?[] args)
    {
        if (args is not [int id, string name, ..] || !_items.TryGetValue(id, out var item)) return null;
        if (item.Properties.TryGetValue(name, out var value)) return new("v", [value]);
        return DefaultPropertyValue(name) is { } fallback ? new("v", [fallback]) : null;
    }

    private DbusReply? GetDbusProperty(object?[] args)
    {
        if (args is [string iface, string name, ..] && iface == Interface && Properties.TryGetValue(name, out var value)) return new("v", [value]);
        return null;
    }

    private DbusReply? GetAllDbusProperties(object?[] args) => args switch
    {
        [string iface, ..] when iface == Interface => new("a{sv}", [Properties]),
        [LinuxTrayRules.PropertiesInterface, ..] => new("a{sv}", [new SortedDictionary<string, LinuxDBusVariant>(StringComparer.Ordinal)]),
        _ => null,
    };

    /// <summary><c>CreateDefaultPropertyValue</c>: what a property left out means; null for a name the spec lacks.</summary>
    internal static LinuxDBusVariant? DefaultPropertyValue(string name) => name switch
    {
        "type" => new("s", "standard"),
        "label" => new("s", ""),
        "enabled" => new("b", true),
        "visible" => new("b", true),
        "icon-name" => new("s", ""),
        "icon-data" => new("ay", Array.Empty<byte>()),
        "shortcut" => new("aas", new List<object?>()),
        "toggle-type" => new("s", ""),
        "toggle-state" => new("i", -1),
        "children-display" => new("s", ""),
        _ => null,
    };

    private bool AboutToShowImpl(int id)
    {
        if (!_items.TryGetValue(id, out var item)) return false;
        if (id == 0) return true;
        if (item.Menu is not { } menu) return false;
        // MenuWillShow on the submenu. Its properties were computed when the layout was built and are not recomputed,
        // so a radio item _menuWillShow checks shows only once the menu is set again, as in Chromium.
        menu.OnMenuWillShow();
        return true;
    }

    // DCHECKs aside, an event other than "clicked" (hovered, opened, closed) does nothing and succeeds.
    private bool EventImpl(object?[] args, out int idError)
    {
        idError = -1;
        if (args is not [int id, ..]) return false;
        if (!_items.TryGetValue(id, out var item))
        {
            idError = id;
            return false;
        }
        if (args is not [_, string type, ..]) return false;
        if (type == "clicked")
        {
            if (item.ContainingMenu is null) return false;
            // ActivatedAt(index): whatever item the menu holds there now; a separator runs nothing.
            var items = item.ContainingMenu.Items;
            if (item.ContainingIndex < items.Count && items[item.ContainingIndex] is { Type: not MenuItemType.Separator } target) _activate(target);
        }
        return true;
    }

    private object?[] WriteMenuItem(Item item, int depth, List<string> filter)
    {
        var children = new List<object?>();
        if (depth != 0)
            foreach (var child in item.Children)
                children.Add(new LinuxDBusVariant("(ia{sv}av)", WriteMenuItem(_items[child], depth == -1 ? -1 : depth - 1, filter)));
        return [item.Id, Filtered(item.Properties, p => filter.Count == 0 || filter.Contains(p)), children];
    }

    private static SortedDictionary<string, LinuxDBusVariant> Filtered(SortedDictionary<string, LinuxDBusVariant> properties, Func<string, bool> keep)
    {
        var result = new SortedDictionary<string, LinuxDBusVariant>(StringComparer.Ordinal);
        foreach (var (key, value) in properties) if (keep(key)) result[key] = value;
        return result;
    }
}

/// <summary>What a Linux status icon asks of its owner: Electron's <c>ui::StatusIconLinux::Delegate</c>
/// (<c>TrayIconLinux</c>). UI thread.</summary>
internal interface IStatusIconDelegate
{
    ImageBitmap? Image { get; }
    string ToolTip { get; }
    Menu? MenuModel { get; }
    void OnClick();
    void OnImplInitializationFailed();

    /// <summary>The host asked for the menu at a screen point (DIPs): the Views menu of <c>OnContextMenu</c>.</summary>
    void ShowContextMenu(Point at);

    /// <summary>The menu model was replaced: an open Views menu closes, as <c>menu_runner_.reset()</c>.</summary>
    void CloseContextMenu();
}

/// <summary>Chromium 154's <c>StatusIconLinuxDbus</c> with Electron v44.5.1's patches (the app's name in <c>Id</c>, a
/// unique name where a well-known one is refused, <c>/com/canonical/dbusmenu</c>): a StatusNotifierItem registered with
/// <c>org.kde.StatusNotifierWatcher</c>, its menu over DBusMenu. UI thread; D-Bus work on the bus's own thread.</summary>
[SupportedOSPlatform("linux")]
internal sealed class StatusNotifierItem
{
    internal const string WatcherService = "org.kde.StatusNotifierWatcher";
    internal const string WatcherInterface = "org.kde.StatusNotifierWatcher";
    internal const string WatcherPath = "/StatusNotifierWatcher";
    internal const string ItemPath = "/StatusNotifierItem";
    internal const string MenuPath = "/com/canonical/dbusmenu";
    private static readonly string[] s_itemInterfaces = [LinuxTrayRules.ItemInterface, LinuxTrayRules.ItemInterfaceFreedesktop];

    // dbus_thread_linux's shared session bus, which one icon at a time may use.
    private static LinuxDBus? s_sharedBus;
    private static bool s_sharedBusInUse;
    private static int s_serviceCount;

    private readonly IStatusIconDelegate _delegate;
    private readonly string _appName;
    private readonly LinuxDBus _bus;
    private readonly bool _sharedBus;
    private readonly bool _writeIconToFile;
    private readonly bool _sandboxed = LinuxTrayRules.IsSandboxedWithoutOwnName(Environment.GetEnvironmentVariable);
    private readonly SortedDictionary<string, LinuxDBusVariant> _properties = new(StringComparer.Ordinal);
    private DbusMenu? _menu;
    private bool _itemExported;
    private bool _ownsServiceName;
    private bool _destroyed;
    private int _serviceId;
    private string _serviceName = "";
    private string _uniqueName = "";
    private int _iconFileId;
    private string _iconFile = "";
    private Task _iconTask = Task.CompletedTask;

    public StatusNotifierItem(IStatusIconDelegate statusDelegate, string appName)
    {
        _delegate = statusDelegate;
        _appName = appName;
        if (!s_sharedBusInUse && !_sandboxed)
        {
            s_sharedBusInUse = true;
            _bus = s_sharedBus ??= LinuxDBus.Open(false, "Oriel D-Bus session");
            _sharedBus = true;
        }
        else _bus = LinuxDBus.Open(false, "Oriel D-Bus status icon");
        _writeIconToFile = LinuxTrayRules.ShouldWriteIconToFile(Environment.GetEnvironmentVariable);
        CheckStatusNotifierWatcherHasOwner();
    }

    /// <summary>The well-known or unique name the item is registered under; empty before. UI thread.</summary>
    internal string ServiceName => _serviceName;

    /// <summary>The temporary PNG of the icon when the desktop takes files; empty otherwise. UI thread.</summary>
    internal string IconFile => _iconFile;

    public void SetImage(ImageBitmap? image) => SetImageImpl(image, sendSignals: true);

    public void SetToolTip(string toolTip)
    {
        if (_properties.Count == 0) return;
        UpdateMenuImpl(_delegate.MenuModel, true);
        SetProperty("ToolTip", LinuxTrayRules.ToolTipValue(_delegate.ToolTip));
        if (_itemExported) foreach (var iface in s_itemInterfaces) Emit(ItemPath, iface, "NewToolTip", "");
    }

    public void UpdatePlatformContextMenu(Menu? model) => UpdateMenuImpl(model, true);

    /// <summary>Electron's destructor: the objects unexported and the name released on the shared bus, or the private
    /// connection closed.</summary>
    public void Destroy()
    {
        if (_destroyed) return;
        _destroyed = true;
        CleanupIconFile();
        _menu = null;
        _itemExported = false;
        _bus.Signal -= OnBusSignal;
        if (_sharedBus)
        {
            s_sharedBusInUse = false;
            var (owns, name) = (_ownsServiceName, _serviceName);
            _bus.Post(() =>
            {
                _bus.Unexport(ItemPath);
                _bus.Unexport(MenuPath);
                if (owns) _bus.ReleaseName(name);
            });
        }
        else _bus.Shutdown();
    }

    // Step 0 and 1: is there a watcher?
    private void CheckStatusNotifierWatcherHasOwner() => _bus.Post(() =>
        _bus.CallAsync("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameHasOwner", "s", [WatcherService],
            (reply, _) => CefThread.PostUi(() => OnNameHasOwnerResponse(reply is [bool owned] ? owned : null))));

    private void OnNameHasOwnerResponse(bool? owned)
    {
        if (_destroyed) return;
        if (owned != true)
        {
            _delegate.OnImplInitializationFailed();
            return;
        }
        _bus.Post(() => _bus.CallAsync(WatcherService, WatcherPath, LinuxTrayRules.PropertiesInterface, "Get", "ss", [WatcherInterface, "IsStatusNotifierHostRegistered"],
            (reply, _) => CefThread.PostUi(() => OnHostRegisteredResponse(reply is [LinuxDBusVariant v] ? v is { Signature: "b", Value: true } : null))));
    }

    // Step 2: a host is there; export the item and its menu.
    private void OnHostRegisteredResponse(bool? registered)
    {
        if (_destroyed) return;
        if (registered != true)
        {
            _delegate.OnImplInitializationFailed();
            return;
        }
        _serviceId = ++s_serviceCount;
        _serviceName = $"org.freedesktop.StatusNotifierItem-{Environment.ProcessId}-{_serviceId}";
        _itemExported = true;
        _menu = new DbusMenu((member, signature, args) => Emit(MenuPath, DbusMenu.Interface, member, signature, args),
            item => item.Run(BrowserWindow.GetFocusedWindow(), new KeyboardEvent()),
            LinuxTrayRules.TextDirection(LinuxTrayRules.ApplicationLanguage(OrielApp.LinuxLanguages(Environment.GetEnvironmentVariable), HasLocaleData)));
        UpdateMenuImpl(_delegate.MenuModel, false);

        SetProperty("ItemIsMenu", new("b", false), false);
        SetProperty("WindowId", new("i", 0), false);
        SetProperty("Menu", new("o", new LinuxDBusObjectPath(MenuPath)), false);
        SetProperty("AttentionIconName", new("s", ""), false);
        SetProperty("AttentionMovieName", new("s", ""), false);
        SetProperty("Category", new("s", "ApplicationStatus"), false);
        SetProperty("Id", new("s", $"{_appName}_status_icon_{_serviceId}"), false);
        SetProperty("OverlayIconName", new("s", ""), false);
        SetProperty("Status", new("s", "Active"), false);
        SetProperty("Title", new("s", ""), false);
        SetProperty("AttentionIconPixmap", new("a(iiay)", new List<object?>()), false);
        SetProperty("OverlayIconPixmap", new("a(iiay)", new List<object?>()), false);
        SetProperty("ToolTip", LinuxTrayRules.ToolTipValue(_delegate.ToolTip), false);
        if (_delegate.Image is { } image) SetImageImpl(image, sendSignals: false);

        _bus.Post(() =>
        {
            // Only the menu's exports gate initialisation, as DbusMenu's barrier does; the item's are not checked.
            _bus.Export(ItemPath, OnItemCall);
            var exported = _bus.Export(MenuPath, OnMenuCall);
            var unique = _bus.UniqueName;
            CefThread.PostUi(() => OnInitialized(exported, unique));
        });
    }

    private void OnInitialized(bool success, string uniqueName)
    {
        if (_destroyed) return;
        if (!success)
        {
            _delegate.OnImplInitializationFailed();
            return;
        }
        _uniqueName = uniqueName;
        // ObjectProxy::SetNameOwnerChangedCallback for the watcher.
        _bus.Post(() =>
        {
            _bus.Signal += OnBusSignal;
            _bus.AddMatch($"type='signal',interface='org.freedesktop.DBus',member='NameOwnerChanged',path='/org/freedesktop/DBus',sender='org.freedesktop.DBus',arg0='{WatcherService}'");
        });
        if (_sandboxed)
        {
            RegisterWithUniqueName();
            return;
        }
        var name = _serviceName;
        _bus.Post(() =>
        {
            var owned = _bus.RequestName(name);
            CefThread.PostUi(() => OnOwnershipAcquired(owned));
        });
    }

    private void OnOwnershipAcquired(bool success)
    {
        if (_destroyed) return;
        if (!success)
        {
            // Electron's patch: the watcher takes a unique name as well.
            RegisterWithUniqueName();
            return;
        }
        _ownsServiceName = true;
        RegisterStatusNotifierItem();
    }

    private void RegisterWithUniqueName()
    {
        _serviceName = _uniqueName;
        if (_serviceName.Length == 0)
        {
            _delegate.OnImplInitializationFailed();
            return;
        }
        RegisterStatusNotifierItem();
    }

    // Step 5.
    private void RegisterStatusNotifierItem()
    {
        var name = _serviceName;
        _bus.Post(() => _bus.CallAsync(WatcherService, WatcherPath, WatcherInterface, "RegisterStatusNotifierItem", "s", [name],
            (reply, _) => CefThread.PostUi(() => OnRegistered(reply is { Length: 0 }))));
    }

    private void OnRegistered(bool success)
    {
        if (!_destroyed && !success) _delegate.OnImplInitializationFailed();
    }

    // Bus thread. A new watcher (after a lock screen, a shell restart) gets the item again.
    private void OnBusSignal(LinuxDBusSignal signal)
    {
        if (signal is { Interface: "org.freedesktop.DBus", Member: "NameOwnerChanged", Args: [WatcherService, string, string newOwner] } && newOwner.Length > 0)
            CefThread.PostUi(() => { if (!_destroyed) RegisterStatusNotifierItem(); });
    }

    // CEF's locales/<tag>.pak beside the app; without the folder every language counts.
    private static bool HasLocaleData(string language)
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "locales");
        if (!Directory.Exists(folder)) return true;
        return Directory.EnumerateFiles(folder, "*.pak").Select(Path.GetFileNameWithoutExtension).Any(tag => tag == language || tag!.StartsWith(language + "-", StringComparison.Ordinal));
    }

    // Bus thread: Chromium's method table for the item; the call itself runs on the UI thread.
    private bool OnItemCall(LinuxDBusMethodCall call)
    {
        var known = call.Interface is LinuxTrayRules.ItemInterface or LinuxTrayRules.ItemInterfaceFreedesktop
            ? call.Member is "Activate" or "ContextMenu" or "Scroll" or "SecondaryActivate" or "ProvideXdgActivationToken"
            : call.Interface == LinuxTrayRules.PropertiesInterface && call.Member is "Get" or "GetAll";
        if (!known) return false;
        CefThread.PostUi(() => Answer(call, _destroyed ? null : HandleItemCall(call)));
        return true;
    }

    private bool OnMenuCall(LinuxDBusMethodCall call)
    {
        if (!DbusMenu.Exports(call.Interface, call.Member)) return false;
        CefThread.PostUi(() => Answer(call, _menu?.Handle(call.Interface, call.Member, call.Args)));
        return true;
    }

    private void Answer(LinuxDBusMethodCall call, DbusReply? reply)
    {
        if (reply is { ErrorName: { } error } failed) _bus.ReplyError(call, error, failed.ErrorMessage ?? "");
        else if (reply is { } r) _bus.Reply(call, r.Signature, r.Values);
        else _bus.ReplyError(call, "org.freedesktop.DBus.Error.Failed", $"error occurred in {call.Member}");
    }

    private DbusReply? HandleItemCall(LinuxDBusMethodCall call)
    {
        if (call.Interface == LinuxTrayRules.PropertiesInterface)
        {
            if (call.Member == "Get")
            {
                if (call.Args is not [string iface, string name, ..] || !s_itemInterfaces.Contains(iface) || !_properties.TryGetValue(name, out var value)) return null;
                return new("v", [value]);
            }
            // GetAll: an empty interface name asks for every interface.
            if (call.Args is not [string all, ..] || !(s_itemInterfaces.Contains(all) || all.Length == 0)) return null;
            return new("a{sv}", [_properties]);
        }
        // dbus_utils::ExportMethod: arguments of another signature get InvalidArgs.
        var expected = call.Member switch { "Scroll" => "is", "ProvideXdgActivationToken" => "s", _ => "ii" };
        if (call.Signature != expected) return new("", [], "org.freedesktop.DBus.Error.InvalidArgs", "Invalid arguments");
        switch (call.Member)
        {
            // gnome-shell's AppIndicator activates on a double click; a middle click (SecondaryActivate) also counts.
            case "Activate" or "SecondaryActivate":
                _delegate.OnClick();
                break;
            case "ContextMenu":
                _delegate.ShowContextMenu(new Point((int)call.Args[0]!, (int)call.Args[1]!));
                break;
            // Stock CEF drops the token (DRIFT T5); the reply is the same either way, as Chromium's.
            case "ProvideXdgActivationToken":
                LinuxCefPatches.ProvideActivationToken((string)call.Args[0]!);
                break;
            // Scroll is ignored.
        }
        return DbusReply.Empty;
    }

    private void UpdateMenuImpl(Menu? model, bool sendSignal)
    {
        if (_menu is null) return;
        _menu.SetModel(null, false);
        _menu.SetModel(model ?? new Menu(), sendSignal);
        _delegate.CloseContextMenu();
    }

    private void SetImageImpl(ImageBitmap? image, bool sendSignals)
    {
        if (_properties.Count == 0) return;
        if (_writeIconToFile)
        {
            var id = _iconFileId++;
            var png = image is { Width: > 0 } bitmap ? ImageCodec.EncodePng(bitmap) : [];
            _iconTask = _iconTask.ContinueWith(_ =>
            {
                var path = WriteIconFile(id, png);
                CefThread.PostUi(() => OnIconFileWritten(path));
            }, TaskScheduler.Default);
            return;
        }
        SetProperty("IconPixmap", LinuxTrayRules.IconPixmapValue(image), sendSignals);
        if (sendSignals && _itemExported) foreach (var iface in s_itemInterfaces) Emit(ItemPath, iface, "NewIcon", "");
    }

    // A new directory and name each time: some hosts update only when both change.
    private static string WriteIconFile(int id, byte[] png)
    {
        string directory;
        try { directory = Directory.CreateTempSubdirectory("org.chromium.Chromium.").FullName; }
        catch (IOException) { return ""; }
        var path = Path.Combine(directory, $"status_icon_{id}.png");
        try
        {
            File.WriteAllBytes(path, png);
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            return "";
        }
    }

    private void OnIconFileWritten(string path)
    {
        if (_destroyed)
        {
            if (path.Length > 0) DeleteDirectory(Path.GetDirectoryName(path)!);
            return;
        }
        CleanupIconFile();
        _iconFile = path;
        if (path.Length == 0) return;
        var directory = Path.GetDirectoryName(path)!;
        SetProperty("IconThemePath", new("s", directory), false);
        SetProperty("IconName", new("s", Path.GetFileNameWithoutExtension(path)), false);
        if (!_itemExported) return;
        foreach (var iface in s_itemInterfaces)
        {
            Emit(ItemPath, iface, "NewIconThemePath", "s", directory);
            Emit(ItemPath, iface, "NewIcon", "");
        }
    }

    private void CleanupIconFile()
    {
        if (_iconFile.Length == 0) return;
        var directory = Path.GetDirectoryName(_iconFile)!;
        _iconTask = _iconTask.ContinueWith(_ => DeleteDirectory(directory), TaskScheduler.Default);
    }

    private static void DeleteDirectory(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void SetProperty(string name, LinuxDBusVariant value, bool emitSignal = true)
    {
        var changed = !_properties.TryGetValue(name, out var old) || !old.Equals(value);
        _properties[name] = value;
        if (emitSignal && changed) PropertyUpdated(name);
    }

    private void PropertyUpdated(string name)
    {
        if (!_itemExported) return;
        var changed = new SortedDictionary<string, LinuxDBusVariant>(StringComparer.Ordinal) { [name] = _properties[name] };
        foreach (var iface in s_itemInterfaces)
            Emit(ItemPath, LinuxTrayRules.PropertiesInterface, "PropertiesChanged", "sa{sv}as", iface, changed, new List<object?>());
    }

    private void Emit(string path, string iface, string member, string signature, params object?[] args)
        => _bus.Post(() => _bus.Emit(path, iface, member, signature, args));
}

/// <summary>Electron's <c>TrayIconLinux</c>: a StatusNotifierItem, falling back to a <c>GtkStatusIcon</c> when no host
/// answers, and to nothing after that. Balloons, focus, bounds and popping the menu up from code are Electron's
/// no-ops on Linux. UI thread.</summary>
[SupportedOSPlatform("linux")]
internal sealed class LinuxTrayBackend(Tray owner) : ITrayBackend, IStatusIconDelegate
{
    private enum Kind { Dbus, Gtk, None }

    private StatusNotifierItem? _dbus;
    private LinuxStatusIconGtk? _gtk;
    private Kind _kind;
    private ImageBitmap? _image;
    private string _toolTip = "";
    private Menu? _menu;

    /// <summary>The StatusNotifierItem while it is the icon; for tests.</summary>
    internal StatusNotifierItem? StatusNotifierItem => _kind == Kind.Dbus ? _dbus : null;

    /// <summary>The GTK fallback while it is the icon; for tests.</summary>
    internal LinuxStatusIconGtk? GtkStatusIcon => _kind == Kind.Gtk ? _gtk : null;

    public bool PopsUpMenus => false;

    public void Create(NativeImage image, Guid? guid)
    {
        _dbus = new StatusNotifierItem(this, OrielApp.CurrentOrNull?.Name ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "Oriel");
        _kind = Kind.Dbus;
        SetImage(image);
    }

    public void SetImage(NativeImage image)
    {
        _image = LinuxTrayRules.BestRepresentation(image);
        if (_kind == Kind.Dbus) _dbus!.SetImage(_image);
        else if (_kind == Kind.Gtk && _image is { } bitmap) _gtk!.SetImage(bitmap.Width, bitmap.Height, LinuxTrayRules.Rgba(bitmap));
    }

    public void SetToolTip(string toolTip)
    {
        _toolTip = toolTip;
        if (_kind == Kind.Dbus) _dbus!.SetToolTip(toolTip);
        else if (_kind == Kind.Gtk) _gtk!.SetToolTip(toolTip);
    }

    public void SetContextMenu(Menu? menu)
    {
        _menu = menu;
        if (_kind == Kind.Dbus) _dbus!.UpdatePlatformContextMenu(menu);
        else if (_kind == Kind.Gtk) _gtk!.SetMenu(LinuxTrayRules.GtkModel(menu));
    }

    public void DisplayBalloon(DisplayBalloonOptions options) { }
    public void RemoveBalloon() { }
    public void Focus() { }
    public bool ActivateForMenu() => false;
    public Rectangle GetBounds() => default;

    public void Destroy()
    {
        _dbus?.Destroy();
        _dbus = null;
        _gtk?.Dispose();
        _gtk = null;
        _kind = Kind.None;
    }

    ImageBitmap? IStatusIconDelegate.Image => _image;
    string IStatusIconDelegate.ToolTip => _toolTip;
    Menu? IStatusIconDelegate.MenuModel => _menu;

    // TrayIconLinux::OnClick: no bounds, position or keys.
    void IStatusIconDelegate.OnClick() => owner.RaiseActivated();

    void IStatusIconDelegate.ShowContextMenu(Point at) => owner.ShowShellContextMenu(at);

    void IStatusIconDelegate.CloseContextMenu() => owner.CloseShellContextMenu();

    void IStatusIconDelegate.OnImplInitializationFailed()
    {
        switch (_kind)
        {
            case Kind.Dbus:
                _dbus!.Destroy();
                _dbus = null;
                // Electron always has GTK; without Chromium's GTK there is nothing to fall back to.
                if (!LinuxStatusIconGtk.Available)
                {
                    _kind = Kind.None;
                    _menu = null;
                    return;
                }
                _gtk = new LinuxStatusIconGtk(owner.RaiseActivated);
                _kind = Kind.Gtk;
                // StatusIconGtk::OnSetDelegate.
                if (_image is { } bitmap) _gtk.SetImage(bitmap.Width, bitmap.Height, LinuxTrayRules.Rgba(bitmap));
                _gtk.SetToolTip(_toolTip);
                _gtk.SetMenu(LinuxTrayRules.GtkModel(_menu));
                _gtk.SetVisible(true);
                return;
            case Kind.Gtk:
                _gtk!.Dispose();
                _gtk = null;
                _kind = Kind.None;
                _menu = null;
                return;
        }
    }
}
