using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Oriel;
using Oriel.Cef;
using Oriel.Cef.Platform;
using Oriel.Samples;

/// <summary>Linux Tray end to end, as a panel sees it: Electron's StatusNotifierItem and DBusMenu read back through a fake
/// StatusNotifierWatcher on the session bus, and the GtkStatusIcon fallback read back through GTK.</summary>
[SupportedOSPlatform("linux")]
static partial class LinuxTrayTest
{
    private const string Sni = "org.kde.StatusNotifierItem", SniFd = "org.freedesktop.StatusNotifierItem";
    private const string Props = "org.freedesktop.DBus.Properties", DbusMenu = "com.canonical.dbusmenu";
    private const string ItemPath = "/StatusNotifierItem", MenuPath = "/com/canonical/dbusmenu";

    internal static async Task RunAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        var red = Image(16, 16, 0, 0, 255, 255);
        // Premultiplied BGRA (10, 64, 100) at alpha 128: getColor gives R 199, G 128, B 20.
        var translucent = Image(2, 1, 10, 64, 100, 128);
        // The largest scale is the one Electron sends.
        var twoScales = Image(16, 16, 0, 0, 255, 255);
        twoScales.AddRepresentation(new AddRepresentationOptions { ScaleFactor = 2, Width = 32, Height = 32, Buffer = Bgra(32, 32, 255, 0, 0, 255) });

        await GtkFallbackAsync(check, step, red);

        using var fake = FakeStatusNotifierWatcher.Start();
        check("a fake StatusNotifierWatcher owns its name on the session bus", fake is not null, Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"));
        if (fake is null) return;
        await ItemAsync(check, step, fake, translucent, twoScales);
        await VariantsAsync(check, step, fake, red);
    }

    private static async Task GtkFallbackAsync(MessageBoxTest.CheckFn check, TimeSpan step, NativeImage image)
    {
        // No watcher on the bus: Electron's TrayIconLinux falls back to StatusIconGtk.
        var clicks = new List<TrayEventArgs>();
        var tray = new Tray(image);
        tray.Clicked += (_, e) => clicks.Add(e);
        tray.SetToolTip("GTK tip");
        var ran = new List<string>();
        var menu = new Menu
        {
            new MenuItem { Label = "&Open", Click = (_, _, _) => ran.Add("open") },
            new MenuItem { Type = MenuItemType.Separator },
            new MenuItem { Label = "Pin_ned", Type = MenuItemType.Checkbox, Checked = true },
            new MenuItem { Label = "Off", Enabled = false, Click = (_, _, _) => ran.Add("off") },
            new MenuItem { Label = "Hidden", Visible = false },
            new MenuItem { Label = "One", Type = MenuItemType.Radio, Checked = true },
            new MenuItem { Label = "Two", Type = MenuItemType.Radio },
            new MenuItem { Label = "More", Submenu = new Menu { new MenuItem { Label = "Deep" } } },
        };
        tray.SetContextMenu(menu);
        var backend = (LinuxTrayBackend)tray.Backend;
        LinuxStatusIconGtk? icon = null;
        check("with no StatusNotifierWatcher the tray falls back to a GtkStatusIcon, as Electron",
            Eventually(() => (icon = CefThread.InvokeOnUi(() => backend.GtkStatusIcon)) is not null, step), null);
        if (icon is null)
        {
            tray.Destroy();
            return;
        }
        var read = CefThread.InvokeOnUi(() => ReadIcon(icon.Handle));
        check("the GtkStatusIcon is visible with the image as an RGBA pixbuf and the tooltip",
            read is { Visible: true, Tip: "GTK tip", W: 16, Channels: 4, First: "255,0,0,255" }, read);

        var items = CefThread.InvokeOnUi(() => MenuRows(icon.MenuHandle));
        check("the GtkMenu is Electron's MenuGtk: mnemonics, check and radio state, disabled and hidden items, a submenu",
            string.Join(" | ", items) == "GtkMenuItem '_Open' on shown | GtkSeparatorMenuItem '' on shown | GtkCheckMenuItem 'Pin__ned' on shown checked | GtkMenuItem 'Off' off shown"
                + " | GtkMenuItem 'Hidden' on hidden | GtkRadioMenuItem 'One' on shown checked | GtkRadioMenuItem 'Two' on shown | GtkMenuItem 'More' on shown submenu",
            string.Join(" | ", items));

        CefThread.InvokeOnUi(() => Emit(icon.Handle, "activate"));
        check("the icon's activate signal raises Clicked with no bounds, position or keys", Eventually(() => clicks.Count == 1, step)
            && clicks[0] is { Bounds.Width: 0, Position.X: 0, Position.Y: 0, ShiftKey: false, CtrlKey: false, AltKey: false, MetaKey: false }, clicks.Count);

        var children = CefThread.InvokeOnUi(() => Children(icon.MenuHandle));
        CefThread.InvokeOnUi(() =>
        {
            gtk_menu_item_activate(children[0]);
            gtk_menu_item_activate(children[2]);
            gtk_menu_item_activate(children[3]);
            gtk_menu_item_activate(children[6]);
            return 0;
        });
        check("activating GTK items runs Electron's click: a checkbox toggles, a disabled item does not run, a radio takes the group",
            Eventually(() => ran.Count >= 1, step) && ran.SequenceEqual(["open"]) && !menu.Items[2].Checked && !menu.Items[5].Checked && menu.Items[6].Checked,
            $"{string.Join(",", ran)} pinned={menu.Items[2].Checked} one={menu.Items[5].Checked} two={menu.Items[6].Checked}");

        CefThread.InvokeOnUi(() => Emit(icon.Handle, "popup_menu", 3u, 0u));
        var shown = Eventually(() => CefThread.InvokeOnUi(() => gtk_widget_get_visible(icon.MenuHandle) != 0), step);
        CefThread.InvokeOnUi(() => { gtk_menu_popdown(icon.MenuHandle); return 0; });
        check("the icon's popup_menu signal pops the GtkMenu up", shown, null);

        tray.Destroy();
        check("Destroy drops the GtkStatusIcon", CefThread.InvokeOnUi(() => backend.GtkStatusIcon) is null && tray.IsDestroyed(), null);
        await Task.Delay(100);
    }

    private static async Task ItemAsync(MessageBoxTest.CheckFn check, TimeSpan step, FakeStatusNotifierWatcher fake, NativeImage translucent, NativeImage twoScales)
    {
        var clicks = new List<TrayEventArgs>();
        var ran = new List<string>();
        KeyboardEvent? showKeys = null;
        var menu = new Menu
        {
            new MenuItem { Label = "&Show", Accelerator = "CmdOrCtrl+S", Click = (_, _, k) => { ran.Add("show"); showKeys = k; } },
            new MenuItem { Type = MenuItemType.Separator },
            new MenuItem { Label = "Pinned", Type = MenuItemType.Checkbox, Checked = true },
            new MenuItem { Label = "Off", Enabled = false, Click = (_, _, _) => ran.Add("off") },
            new MenuItem { Label = "Hidden", Visible = false },
            new MenuItem
            {
                Label = "More",
                Submenu = new Menu
                {
                    new MenuItem { Label = "Deep_er && more" },
                    new MenuItem { Label = "A", Type = MenuItemType.Radio, Checked = true },
                    new MenuItem { Label = "B", Type = MenuItemType.Radio },
                },
            },
            new MenuItem { Label = "Zoom", Accelerator = "Alt+Plus" },
            new MenuItem { Label = "Refresh", Accelerator = "F5" },
            new MenuItem { Role = MenuItemRole.Quit },
        };

        var tray = new Tray(translucent);
        tray.Clicked += (_, e) => clicks.Add(e);
        tray.SetToolTip("Oriel tray");
        tray.SetContextMenu(menu);
        var expectedService = $"org.freedesktop.StatusNotifierItem-{Environment.ProcessId}-1";
        (string Service, string Sender) registration = ("", "");
        check("the item registers with the watcher under Electron's well-known name, its first service id",
            Eventually(() => fake.Registrations.TryPeek(out registration), step) && registration.Service == expectedService, registration);
        var service = registration.Service;
        var sender = registration.Sender;
        check("the item's connection owns that name", fake.HasOwner(service), service);

        // Properties, as Chromium's map holds them: sorted, with Electron's Id.
        var all = fake.Call(service, ItemPath, Props, "GetAll", "s", Sni);
        var expectedProperties = "AttentionIconName=<s>'' AttentionIconPixmap=<a(iiay)>[] AttentionMovieName=<s>'' Category=<s>'ApplicationStatus' IconPixmap=<a(iiay)>[(2,1,128 199 128 20 128 199 128 20)]"
            + " Id=<s>'Oriel.WindowTest_status_icon_1' ItemIsMenu=<b>false Menu=<o>'/com/canonical/dbusmenu' OverlayIconName=<s>'' OverlayIconPixmap=<a(iiay)>[] Status=<s>'Active' Title=<s>''"
            + " ToolTip=<(sa(iiay)ss)>('',[],'Oriel tray','') WindowId=<i>0";
        check("GetAll gives every SNI property in Chromium's order; IconPixmap is ARGB32 in network order, unpremultiplied as Skia's getColor",
            all is { Signature: "a{sv}", Values: [var dict] } && Properties(dict) == expectedProperties, all.Values is [var d] ? Properties(d) : all.Error);
        var freedesktop = fake.Call(service, ItemPath, Props, "GetAll", "s", SniFd);
        var anyInterface = fake.Call(service, ItemPath, Props, "GetAll", "s", "");
        var otherInterface = fake.Call(service, ItemPath, Props, "GetAll", "s", "org.example.Nope");
        check("GetAll answers for org.freedesktop.StatusNotifierItem and the empty interface, and fails for another",
            freedesktop.Values is [var f] && Properties(f) == expectedProperties && anyInterface.Values is [var e] && Properties(e) == expectedProperties
            && otherInterface.Error == "org.freedesktop.DBus.Error.Failed", $"{otherInterface.Error}");
        var title = fake.Call(service, ItemPath, Props, "Get", "ss", Sni, "Status");
        var missing = fake.Call(service, ItemPath, Props, "Get", "ss", Sni, "IconName");
        check("Get answers one property as a variant and fails for a property Chromium never set (IconName, in pixmap mode)",
            title is { Signature: "v", Values: [FakeVariant { Signature: "s", Value: "Active" }] } && missing.Error == "org.freedesktop.DBus.Error.Failed", missing.Error);

        // The menu, as Chromium's DbusMenu serialises it.
        var layout = fake.Call(service, MenuPath, DbusMenu, "GetLayout", "iias", 0, -1, new List<string>());
        const string ExpectedLayout = "0{children-display='submenu'}[1{label='_Show',shortcut=[[Control,s]]}[],2{type='separator'}[],3{label='Pinned',toggle-state=1,toggle-type='checkmark'}[],"
            + "4{enabled=false,label='Off'}[],5{label='Hidden',visible=false}[],9{children-display='submenu',label='More'}[6{label='Deep__er & more'}[],"
            + "7{label='A',toggle-state=1,toggle-type='radio'}[],8{label='B',toggle-state=0,toggle-type='radio'}[]],10{label='Zoom',shortcut=[[Alt,Shift,=]]}[],"
            + "11{label='Refresh'}[],12{label='Quit'}[]]";
        check("GetLayout matches the Menu: ids depth first, mnemonics as _, hidden and disabled items, toggles, shortcuts without role defaults",
            layout is { Signature: "u(ia{sv}av)", Values: [0u, var root] } && Layout(root) == ExpectedLayout, layout.Values is [_, var r] ? Layout(r) : layout.Error);
        var shallow = fake.Call(service, MenuPath, DbusMenu, "GetLayout", "iias", 9, 0, new List<string> { "label" });
        var oneLevel = fake.Call(service, MenuPath, DbusMenu, "GetLayout", "iias", 9, 1, new List<string> { "toggle-state" });
        var noItem = fake.Call(service, MenuPath, DbusMenu, "GetLayout", "iias", 99, -1, new List<string>());
        var badDepth = fake.Call(service, MenuPath, DbusMenu, "GetLayout", "iias", 0, -2, new List<string>());
        check("GetLayout honours depth and the property filter, and fails for an unknown id or a depth below -1",
            shallow.Values is [_, var s] && Layout(s) == "9{label='More'}[]" && oneLevel.Values is [_, var o] && Layout(o) == "9{}[6{}[],7{toggle-state=1}[],8{toggle-state=0}[]]"
            && noItem.Error == "org.freedesktop.DBus.Error.Failed" && badDepth.Error == "org.freedesktop.DBus.Error.Failed",
            $"{(shallow.Values is [_, var s2] ? Layout(s2) : shallow.Error)} {(oneLevel.Values is [_, var o2] ? Layout(o2) : oneLevel.Error)} {noItem.Error} {badDepth.Error}");
        var group = fake.Call(service, MenuPath, DbusMenu, "GetGroupProperties", "aias", new List<int> { 3, 99, 1 }, new List<string> { "label", "toggle-state" });
        var everything = fake.Call(service, MenuPath, DbusMenu, "GetGroupProperties", "aias", new List<int>(), new List<string> { "type" });
        check("GetGroupProperties gives the asked ids in order, skipping unknown ones, and every id when none is asked",
            group.Values is [List<object?> g] && string.Join(";", g.Select(Row)) == "3{label='Pinned',toggle-state=1};1{label='_Show'}"
            && everything.Values is [List<object?> all2] && string.Join(";", all2.Select(Row)) == "0{};1{};2{type='separator'};3{};4{};5{};6{};7{};8{};9{};10{};11{};12{}",
            $"{(group.Values is [List<object?> g2] ? string.Join(";", g2.Select(Row)) : group.Error)}");
        var stored = fake.Call(service, MenuPath, DbusMenu, "GetProperty", "is", 4, "enabled");
        var defaulted = fake.Call(service, MenuPath, DbusMenu, "GetProperty", "is", 1, "toggle-state");
        var unknown = fake.Call(service, MenuPath, DbusMenu, "GetProperty", "is", 1, "bogus");
        check("GetProperty gives a stored value, the spec's default for one left out, and fails for a name the spec lacks",
            stored.Values is [FakeVariant { Signature: "b", Value: false }] && defaulted.Values is [FakeVariant { Signature: "i", Value: -1 }] && unknown.Error == "org.freedesktop.DBus.Error.Failed", unknown.Error);
        var aboutSubmenu = fake.Call(service, MenuPath, DbusMenu, "AboutToShow", "i", 9);
        var aboutRoot = fake.Call(service, MenuPath, DbusMenu, "AboutToShow", "i", 0);
        var aboutLeaf = fake.Call(service, MenuPath, DbusMenu, "AboutToShow", "i", 1);
        var aboutGroup = fake.Call(service, MenuPath, DbusMenu, "AboutToShowGroup", "ai", new List<int> { 9, 1, 99 });
        check("AboutToShow answers false (no update) for the root and a submenu, fails for a leaf; AboutToShowGroup lists the failures",
            aboutSubmenu.Values is [false] && aboutRoot.Values is [false] && aboutLeaf.Error == "org.freedesktop.DBus.Error.Failed"
            && aboutGroup is { Signature: "aiai", Values: [List<object?> { Count: 0 }, List<object?> errors] } && errors.SequenceEqual(new object?[] { 1, 99 }), aboutLeaf.Error);
        var menuProps = fake.Call(service, MenuPath, Props, "GetAll", "s", DbusMenu);
        check("the menu object's properties: IconThemePath, Status, TextDirection, Version 3",
            menuProps.Values is [var mp] && Properties(mp) == "IconThemePath=<as>[] Status=<s>'normal' TextDirection=<s>'ltr' Version=<u>3", menuProps.Values is [var mp2] ? Properties(mp2) : menuProps.Error);

        // Event: Electron's click, without its enabled check, and no refresh of the layout.
        var empty = new FakeVariant("s", "");
        var clicked = fake.Call(service, MenuPath, DbusMenu, "Event", "isvu", 1, "clicked", empty, 0u);
        fake.Call(service, MenuPath, DbusMenu, "Event", "isvu", 3, "clicked", empty, 0u);
        fake.Call(service, MenuPath, DbusMenu, "Event", "isvu", 4, "clicked", empty, 0u);
        fake.Call(service, MenuPath, DbusMenu, "Event", "isvu", 8, "clicked", empty, 0u);
        var hovered = fake.Call(service, MenuPath, DbusMenu, "Event", "isvu", 1, "hovered", empty, 0u);
        var onRoot = fake.Call(service, MenuPath, DbusMenu, "Event", "isvu", 0, "clicked", empty, 0u);
        var onNothing = fake.Call(service, MenuPath, DbusMenu, "Event", "isvu", 99, "clicked", empty, 0u);
        check("Event clicked runs the item's click with no keys; a checkbox toggles, a radio takes its group, a disabled item still runs",
            clicked is { Signature: "", Values: [] } && Eventually(() => ran.Count >= 2, step) && ran.SequenceEqual(["show", "off"]) && showKeys is { ShiftKey: false, CtrlKey: false, AltKey: false, MetaKey: false, TriggeredByAccelerator: false }
            && !menu.Items[2].Checked && !menu.Items[5].Submenu!.Items[1].Checked && menu.Items[5].Submenu!.Items[2].Checked,
            $"{string.Join(",", ran)} pinned={menu.Items[2].Checked}");
        check("other events succeed and do nothing; clicking the root or an unknown id fails",
            hovered.Values is [] && hovered.Error == "" && onRoot.Error == "org.freedesktop.DBus.Error.Failed" && onNothing.Error == "org.freedesktop.DBus.Error.Failed" && ran.Count == 2, $"{onRoot.Error} {onNothing.Error}");
        var eventGroup = fake.Call(service, MenuPath, DbusMenu, "EventGroup", "a(isvu)", new List<object?[]> { new object?[] { 1, "clicked", empty, 0u }, new object?[] { 99, "clicked", empty, 0u } });
        check("EventGroup runs each event and lists the unknown ids", eventGroup is { Signature: "ai", Values: [List<object?> missingIds] } && missingIds.SequenceEqual(new object?[] { 99 })
            && Eventually(() => ran.Count == 3, step), eventGroup.Error);
        var stale = fake.Call(service, MenuPath, DbusMenu, "GetLayout", "iias", 3, 0, new List<string>());
        check("the layout keeps the old check state until the menu is set again, as Electron documents", stale.Values is [_, var st] && Layout(st) == "3{label='Pinned',toggle-state=1,toggle-type='checkmark'}[]", null);

        // Signals for setContextMenu, setToolTip and setImage, in Chromium's order.
        fake.Signals.Clear();
        tray.SetContextMenu(menu);
        check("SetContextMenu sends LayoutUpdated(revision 1, parent 0) and the new layout has the new state and fresh ids",
            Eventually(() => Mine(fake, sender).Count >= 1, step) && string.Join(" ", Mine(fake, sender)) == "/com/canonical/dbusmenu com.canonical.dbusmenu.LayoutUpdated(1,0)"
            && fake.Call(service, MenuPath, DbusMenu, "GetLayout", "iias", 0, 1, new List<string> { "toggle-state" }) is { Values: [1u, var fresh] }
            && Layout(fresh) == "0{}[13{}[],14{}[],15{toggle-state=0}[],16{}[],17{}[],21{}[],22{}[],23{}[],24{}[]]",
            string.Join(" ", Mine(fake, sender)));
        fake.Signals.Clear();
        tray.SetToolTip("New tip");
        string[] tipSignals = ["/com/canonical/dbusmenu com.canonical.dbusmenu.LayoutUpdated(2,0)",
            "/StatusNotifierItem org.freedesktop.DBus.Properties.PropertiesChanged('org.kde.StatusNotifierItem',[ToolTip=<(sa(iiay)ss)>('',[],'New tip','')],[])",
            "/StatusNotifierItem org.freedesktop.DBus.Properties.PropertiesChanged('org.freedesktop.StatusNotifierItem',[ToolTip=<(sa(iiay)ss)>('',[],'New tip','')],[])",
            "/StatusNotifierItem org.kde.StatusNotifierItem.NewToolTip()", "/StatusNotifierItem org.freedesktop.StatusNotifierItem.NewToolTip()"];
        check("SetToolTip rebuilds the menu (LayoutUpdated), then PropertiesChanged and NewToolTip on both interfaces",
            Eventually(() => Mine(fake, sender).Count >= 5, step) && Mine(fake, sender).SequenceEqual(tipSignals), string.Join(" | ", Mine(fake, sender)));
        fake.Signals.Clear();
        tray.SetToolTip("New tip");
        check("the same tooltip again sends no PropertiesChanged",
            Eventually(() => Mine(fake, sender).Count >= 3, step) && string.Join(" | ", Mine(fake, sender)) == "/com/canonical/dbusmenu com.canonical.dbusmenu.LayoutUpdated(3,0) | " + tipSignals[3] + " | " + tipSignals[4],
            string.Join(" | ", Mine(fake, sender)));
        fake.Signals.Clear();
        tray.SetImage(twoScales);
        check("SetImage sends PropertiesChanged(IconPixmap) and NewIcon on both interfaces, with the largest representation's pixels",
            Eventually(() => Mine(fake, sender).Count >= 4, step) && string.Join(" | ", Mine(fake, sender)) ==
                "/StatusNotifierItem org.freedesktop.DBus.Properties.PropertiesChanged('org.kde.StatusNotifierItem',[IconPixmap=<a(iiay)>[(32,32,4096 bytes)]],[])"
                + " | /StatusNotifierItem org.freedesktop.DBus.Properties.PropertiesChanged('org.freedesktop.StatusNotifierItem',[IconPixmap=<a(iiay)>[(32,32,4096 bytes)]],[])"
                + " | /StatusNotifierItem org.kde.StatusNotifierItem.NewIcon() | /StatusNotifierItem org.freedesktop.StatusNotifierItem.NewIcon()"
            && fake.Call(service, ItemPath, Props, "Get", "ss", Sni, "IconPixmap").Values is [FakeVariant { Value: List<object?> { Count: 1 } images }]
            && images[0] is object?[] { Length: 3 } first && first[0] is 32 && first[1] is 32 && first[2] is byte[] px && px.Take(4).SequenceEqual(new byte[] { 255, 0, 0, 255 }),
            string.Join(" | ", Mine(fake, sender)));

        // The item's methods: Electron's click for both activations, Views menu for ContextMenu.
        clicks.Clear();
        var activate = fake.Call(service, ItemPath, Sni, "Activate", "ii", 10, 20);
        fake.Call(service, ItemPath, SniFd, "SecondaryActivate", "ii", 10, 20);
        var scroll = fake.Call(service, ItemPath, Sni, "Scroll", "is", 1, "vertical");
        var token = fake.Call(service, ItemPath, Sni, "ProvideXdgActivationToken", "s", "token");
        check("Activate and SecondaryActivate raise Clicked with Electron's empty arguments; Scroll and the activation token do nothing",
            activate is { Error: "", Signature: "" } && scroll.Error == "" && token.Error == "" && Eventually(() => clicks.Count == 2, step)
            && clicks.All(c => c is { Bounds: { X: 0, Y: 0, Width: 0, Height: 0 }, Position: { X: 0, Y: 0 }, ShiftKey: false, CtrlKey: false, AltKey: false, MetaKey: false }), clicks.Count);
        var wrong = fake.Call(service, ItemPath, Sni, "Activate", "i", 10);
        var bogus = fake.Call(service, ItemPath, Sni, "Bogus", "");
        var setProperty = fake.Call(service, ItemPath, Props, "Set", "ss", Sni, "Title");
        check("wrong arguments get InvalidArgs; methods Chromium does not export (Bogus, Properties.Set) get libdbus's UnknownMethod",
            wrong.Error == "org.freedesktop.DBus.Error.InvalidArgs" && bogus.Error == "org.freedesktop.DBus.Error.UnknownMethod" && setProperty.Error == "org.freedesktop.DBus.Error.UnknownMethod",
            $"{wrong.Error} {bogus.Error} {setProperty.Error}");
        fake.Call(service, ItemPath, Sni, "ContextMenu", "ii", 300, 300);
        check("ContextMenu opens the context menu as a Views menu", Eventually(() => tray.MenuOpen, step), null);
        tray.PopUpContextMenu();
        tray.CloseContextMenu();
        check("CloseContextMenu does nothing on Linux, as Electron", tray.MenuOpen, null);
        tray.SetContextMenu(menu);
        check("setting the menu again closes it (menu_runner_.reset())", Eventually(() => !tray.MenuOpen, step), null);
        check("balloons, focus and bounds are Electron's Linux no-ops",
            Ok(() => { tray.DisplayBalloon(new DisplayBalloonOptions { Title = "t", Content = "c" }); tray.RemoveBalloon(); tray.Focus(); tray.SetTitle("t"); tray.SetIgnoreDoubleClickEvents(true); })
            && tray.GetBounds() == default && tray.GetTitle() == "" && !tray.GetIgnoreDoubleClickEvents(), null);

        // A new watcher gets the item again.
        var before = fake.Registrations.Count;
        fake.Release();
        fake.Own();
        check("a new owner of the watcher's name gets RegisterStatusNotifierItem again",
            Eventually(() => fake.Registrations.Count > before, step) && fake.Registrations.Last().Service == service, fake.Registrations.Count);

        // A second icon while the first lives: a private connection, the next id.
        var second = new Tray(translucent);
        (string Service, string Sender) secondRegistration = ("", "");
        check("a second icon registers as service 2 from a connection of its own",
            Eventually(() => fake.Registrations.Any(r => r.Service.EndsWith("-2", StringComparison.Ordinal)), step)
            && (secondRegistration = fake.Registrations.First(r => r.Service.EndsWith("-2", StringComparison.Ordinal))).Sender != sender, secondRegistration);
        second.Destroy();
        check("destroying it closes that connection: the name and the connection are gone",
            Eventually(() => !fake.HasOwner(secondRegistration.Service) && !fake.HasOwner(secondRegistration.Sender), step), null);

        tray.Destroy();
        check("Destroy releases the name and unexports the item and the menu from the shared connection",
            Eventually(() => !fake.HasOwner(service), step) && fake.HasOwner(sender)
            && fake.Call(sender, ItemPath, Props, "GetAll", "s", Sni).Error.StartsWith("org.freedesktop.DBus.Error.Unknown", StringComparison.Ordinal)
            && fake.Call(sender, MenuPath, DbusMenu, "GetLayout", "iias", 0, -1, new List<string>()).Error.StartsWith("org.freedesktop.DBus.Error.Unknown", StringComparison.Ordinal),
            fake.Call(sender, ItemPath, Props, "GetAll", "s", Sni).Error);
        await Task.Delay(100);
    }

    private static async Task VariantsAsync(MessageBoxTest.CheckFn check, TimeSpan step, FakeStatusNotifierWatcher fake, NativeImage image)
    {
        // A watcher without a host: Chromium's step 2 fails, and Electron falls back to GTK.
        fake.HostRegistered = false;
        var count = fake.Registrations.Count;
        var hostless = new Tray(image);
        var backend = (LinuxTrayBackend)hostless.Backend;
        check("a watcher with no host registered sends the tray to the GTK fallback",
            Eventually(() => CefThread.InvokeOnUi(() => backend.GtkStatusIcon) is not null, step) && fake.Registrations.Count == count, null);
        hostless.Destroy();
        fake.HostRegistered = true;

        // A watcher that never answers: the call times out after libdbus's default 25 s, and Electron falls back.
        fake.IgnoreHostQuery = true;
        var hung = new Tray(image);
        var hungBackend = (LinuxTrayBackend)hung.Backend;
        var fellBack = Eventually(() => CefThread.InvokeOnUi(() => hungBackend.GtkStatusIcon) is not null, TimeSpan.FromSeconds(40));
        check("an unanswered watcher times out (NoReply, 25 s) and the tray falls back to GTK", fellBack && fake.Registrations.Count == count, null);
        hung.Destroy();
        fake.IgnoreHostQuery = false;

        // GNOME and Pantheon take the icon as a PNG file in a fresh temporary directory.
        Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", "ubuntu:GNOME");
        var gnome = new Tray(image);
        Environment.SetEnvironmentVariable("XDG_CURRENT_DESKTOP", null);
        (string Service, string Sender) registration = ("", "");
        var registered = Eventually(() => fake.Registrations.Skip(count).Any(), step);
        if (registered) registration = fake.Registrations.Skip(count).First();
        Dictionary<string, FakeVariant> keys = [];
        // The file is written off the UI thread; its properties follow.
        Eventually(() => registered && fake.Call(registration.Service, ItemPath, Props, "GetAll", "s", Sni).Values is [var dict]
            && (keys = FakeStatusNotifierWatcher.Dict(dict).ToDictionary(e => e.Key, e => e.Value)).ContainsKey("IconName"), step);
        var themePath = keys.TryGetValue("IconThemePath", out var tp) ? tp.Value as string ?? "" : "";
        var iconName = keys.TryGetValue("IconName", out var n) ? n.Value as string ?? "" : "";
        var file = Path.Combine(themePath, iconName + ".png");
        var png = File.Exists(file) ? NativeImage.CreateFromPath(file) : NativeImage.CreateEmpty();
        check("under GNOME the icon is a PNG in a new org.chromium.Chromium.* directory (IconThemePath, IconName), and no IconPixmap",
            registered && Path.GetFileName(themePath).StartsWith("org.chromium.Chromium.", StringComparison.Ordinal) && iconName == "status_icon_0"
            && !keys.ContainsKey("IconPixmap") && png.GetSize() == (16, 16) && png.ToBitmap().Take(4).SequenceEqual(image.ToBitmap().Take(4)),
            $"{themePath} {iconName} {string.Join(",", keys.Keys)}");
        fake.Signals.Clear();
        gnome.SetImage(image);
        string newTheme = "";
        check("SetImage writes status_icon_1 in another directory, deletes the old one, and sends NewIconThemePath and NewIcon",
            Eventually(() => Mine(fake, registration.Sender).Count >= 4, step)
            && Mine(fake, registration.Sender) is var signals && signals[0].StartsWith("/StatusNotifierItem org.kde.StatusNotifierItem.NewIconThemePath('", StringComparison.Ordinal)
            && signals[1] == "/StatusNotifierItem org.kde.StatusNotifierItem.NewIcon()" && signals[3] == "/StatusNotifierItem org.freedesktop.StatusNotifierItem.NewIcon()"
            && (newTheme = signals[0].Split('\'')[1]) != themePath && File.Exists(Path.Combine(newTheme, "status_icon_1.png")) && Eventually(() => !Directory.Exists(themePath), step),
            string.Join(" | ", Mine(fake, registration.Sender)));
        gnome.Destroy();
        check("Destroy deletes the icon's directory", Eventually(() => newTheme.Length > 0 && !Directory.Exists(newTheme), step), newTheme);

        // Flatpak and Snap: the unique name, on a private connection (Electron's patch).
        count = fake.Registrations.Count;
        Environment.SetEnvironmentVariable("SNAP", "/snap/oriel/1");
        var snap = new Tray(image);
        Environment.SetEnvironmentVariable("SNAP", null);
        (string Service, string Sender) snapRegistration = ("", "");
        check("in a Snap the item registers under its connection's unique name",
            Eventually(() => fake.Registrations.Skip(count).Any(), step) && (snapRegistration = fake.Registrations.Skip(count).First()).Service == snapRegistration.Sender
            && snapRegistration.Service.StartsWith(':'), snapRegistration);
        snap.Destroy();
        check("and destroying it closes that connection", Eventually(() => !fake.HasOwner(snapRegistration.Sender), step), null);
        await Task.Delay(100);
    }

    // The fake's view of signals from one connection, as "path interface.member(args)".
    private static List<string> Mine(FakeStatusNotifierWatcher fake, string sender)
        => [.. fake.Signals.Where(s => s.Sender == sender).Select(s => $"{s.Path} {s.Interface}.{s.Member}({string.Join(",", s.Args.Select(Value))})")];

    private static string Properties(object? dict) => string.Join(" ", FakeStatusNotifierWatcher.Dict(dict).Select(e => $"{e.Key}={Value(e.Value)}"));

    private static string Layout(object? item)
    {
        var fields = (object?[])item!;
        var children = ((List<object?>)fields[2]!).Select(c => Layout(((FakeVariant)c!).Value));
        return $"{fields[0]}{{{string.Join(",", FakeStatusNotifierWatcher.Dict(fields[1]).Select(e => $"{e.Key}={Value(e.Value.Value)}"))}}}[{string.Join(",", children)}]";
    }

    private static string Row(object? item)
    {
        var fields = (object?[])item!;
        return $"{fields[0]}{{{string.Join(",", FakeStatusNotifierWatcher.Dict(fields[1]).Select(e => $"{e.Key}={Value(e.Value.Value)}"))}}}";
    }

    private static string Value(object? value) => value switch
    {
        null => "null",
        string s => $"'{s}'",
        bool b => b ? "true" : "false",
        byte[] bytes => bytes.Length <= 8 ? string.Join(" ", bytes) : $"{bytes.Length} bytes",
        FakeVariant v => $"<{v.Signature}>{Value(v.Value)}",
        KeyValuePair<object?, object?> kv => $"{(kv.Key as string)}={Value(kv.Value)}",
        object?[] fields => $"({string.Join(",", fields.Select(Value))})",
        List<object?> items when items.All(i => i is string) && items.Count > 0 => $"[{string.Join(",", items)}]",
        List<object?> items => $"[{string.Join(",", items.Select(Value))}]",
        _ => value.ToString() ?? "",
    };

    private static NativeImage Image(int width, int height, byte b, byte g, byte r, byte a)
        => NativeImage.CreateFromBitmap(Bgra(width, height, b, g, r, a), new NativeImageBitmapOptions { Width = width, Height = height });

    private static byte[] Bgra(int width, int height, byte b, byte g, byte r, byte a)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, a);
        return pixels;
    }

    private static bool Eventually(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    private static bool Ok(Action action)
    {
        action();
        return true;
    }

    // GTK, on CEF's UI thread (GTK's main thread).
    private static unsafe (bool Visible, string? Tip, int W, int Channels, string First) ReadIcon(nint icon)
    {
        var pixbuf = gtk_status_icon_get_pixbuf(icon);
        var pixels = pixbuf == 0 ? [] : new ReadOnlySpan<byte>(gdk_pixbuf_get_pixels(pixbuf), 4).ToArray();
        return (gtk_status_icon_get_visible(icon) != 0, Marshal.PtrToStringUTF8(gtk_status_icon_get_tooltip_text(icon)),
            pixbuf == 0 ? 0 : gdk_pixbuf_get_width(pixbuf), pixbuf == 0 ? 0 : gdk_pixbuf_get_n_channels(pixbuf), string.Join(",", pixels));
    }

    private static unsafe List<nint> Children(nint container)
    {
        var list = new List<nint>();
        var children = gtk_container_get_children(container);
        for (var node = children; node != null; node = node->Next) list.Add(node->Data);
        g_list_free(children);
        return list;
    }

    private static unsafe List<string> MenuRows(nint menu) => [.. Children(menu).Select(w =>
    {
        var type = Marshal.PtrToStringUTF8(g_type_name(**(nuint**)w));
        var label = type == "GtkSeparatorMenuItem" ? "" : Marshal.PtrToStringUTF8(gtk_menu_item_get_label(w));
        var row = new StringBuilder($"{type} '{label}' {(gtk_widget_get_sensitive(w) != 0 ? "on" : "off")} {(gtk_widget_get_visible(w) != 0 ? "shown" : "hidden")}");
        if (type is "GtkCheckMenuItem" or "GtkRadioMenuItem" && gtk_check_menu_item_get_active(w) != 0) row.Append(" checked");
        if (type != "GtkSeparatorMenuItem" && gtk_menu_item_get_submenu(w) != 0) row.Append(" submenu");
        return row.ToString();
    })];

    [StructLayout(LayoutKind.Sequential)]
    private struct GValue
    {
        public nuint Type;
        public long Data0;
        public long Data1;
    }

    // g_signal_emitv: the instance, then each uint argument; no return value.
    private static unsafe int Emit(nint instance, string signal, params uint[] args)
    {
        var values = stackalloc GValue[1 + args.Length];
        _ = g_value_init(&values[0], **(nuint**)instance);
        g_value_set_object(&values[0], instance);
        for (var i = 0; i < args.Length; i++)
        {
            // G_TYPE_UINT.
            _ = g_value_init(&values[1 + i], 7 << 2);
            g_value_set_uint(&values[1 + i], args[i]);
        }
        uint id;
        fixed (byte* name = Encoding.UTF8.GetBytes(signal + "\0")) id = g_signal_lookup(name, **(nuint**)instance);
        g_signal_emitv(values, id, 0, null);
        for (var i = 0; i <= args.Length; i++) g_value_unset(&values[i]);
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct GList
    {
        public nint Data;
        public GList* Next;
        public GList* Prev;
    }

    [LibraryImport("libgtk-3.so.0")] private static partial nint gtk_status_icon_get_pixbuf(nint icon);
    [LibraryImport("libgtk-3.so.0")] private static partial int gtk_status_icon_get_visible(nint icon);
    [LibraryImport("libgtk-3.so.0")] private static partial nint gtk_status_icon_get_tooltip_text(nint icon);
    [LibraryImport("libgtk-3.so.0")] private static unsafe partial GList* gtk_container_get_children(nint container);
    [LibraryImport("libgtk-3.so.0")] private static partial nint gtk_menu_item_get_label(nint item);
    [LibraryImport("libgtk-3.so.0")] private static partial nint gtk_menu_item_get_submenu(nint item);
    [LibraryImport("libgtk-3.so.0")] private static partial void gtk_menu_item_activate(nint item);
    [LibraryImport("libgtk-3.so.0")] private static partial int gtk_check_menu_item_get_active(nint item);
    [LibraryImport("libgtk-3.so.0")] private static partial int gtk_widget_get_sensitive(nint widget);
    [LibraryImport("libgtk-3.so.0")] private static partial int gtk_widget_get_visible(nint widget);
    [LibraryImport("libgtk-3.so.0")] private static partial void gtk_menu_popdown(nint menu);
    [LibraryImport("libgdk_pixbuf-2.0.so.0")] private static unsafe partial byte* gdk_pixbuf_get_pixels(nint pixbuf);
    [LibraryImport("libgdk_pixbuf-2.0.so.0")] private static partial int gdk_pixbuf_get_width(nint pixbuf);
    [LibraryImport("libgdk_pixbuf-2.0.so.0")] private static partial int gdk_pixbuf_get_n_channels(nint pixbuf);
    [LibraryImport("libglib-2.0.so.0")] private static unsafe partial void g_list_free(GList* list);
    [LibraryImport("libgobject-2.0.so.0")] private static partial nint g_type_name(nuint type);
    [LibraryImport("libgobject-2.0.so.0")] private static unsafe partial GValue* g_value_init(GValue* value, nuint type);
    [LibraryImport("libgobject-2.0.so.0")] private static unsafe partial void g_value_set_object(GValue* value, nint obj);
    [LibraryImport("libgobject-2.0.so.0")] private static unsafe partial void g_value_set_uint(GValue* value, uint v);
    [LibraryImport("libgobject-2.0.so.0")] private static unsafe partial void g_value_unset(GValue* value);
    [LibraryImport("libgobject-2.0.so.0")] private static unsafe partial uint g_signal_lookup(byte* name, nuint type);
    [LibraryImport("libgobject-2.0.so.0")] private static unsafe partial void g_signal_emitv(GValue* values, uint id, uint detail, GValue* result);
}
