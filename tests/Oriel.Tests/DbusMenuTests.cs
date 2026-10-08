using System.Text;
using Oriel.Cef.Platform;

namespace Oriel.Tests;

// Chromium 154's components/dbus/menu as Electron's Linux tray drives it.
public sealed class DbusMenuTests
{
    private readonly List<string> _signals = [];
    private readonly List<string> _ran = [];

    private DbusMenu NewMenu() => new((member, signature, args) => _signals.Add($"{member}:{signature}({string.Join(",", args)})"), item => _ran.Add(item.Label), "ltr");

    private static Menu Sample(Action<string>? log = null) =>
    [
        new MenuItem { Label = "&Show", Accelerator = "CmdOrCtrl+S" },
        new MenuItem { Type = MenuItemType.Separator, Label = "ignored" },
        new MenuItem { Label = "Pinned", Type = MenuItemType.Checkbox, Checked = true },
        new MenuItem { Label = "Off", Enabled = false },
        new MenuItem { Label = "Hidden", Visible = false },
        new MenuItem
        {
            Label = "More",
            Submenu =
            [
                new MenuItem { Label = "Deep_er" },
                new MenuItem { Label = "A", Type = MenuItemType.Radio, Checked = true },
                new MenuItem { Label = "B", Type = MenuItemType.Radio },
            ],
        },
        new MenuItem { Role = MenuItemRole.Quit, Label = "Quit" },
        new MenuItem { Label = "Typed", Type = MenuItemType.Submenu },
        new MenuItem(),
        // Electron inserts a check item for a checkbox, submenu or not.
        new MenuItem { Label = "C", Type = MenuItemType.Checkbox, Submenu = [new MenuItem { Label = "x" }] },
    ];

    [Fact]
    public void GetLayout_IsTheMenu_IdsDepthFirst_PropertiesOnlyWhenNotDefault()
    {
        var menu = NewMenu();
        menu.SetModel(Sample(), false);
        var reply = menu.Handle(DbusMenu.Interface, "GetLayout", [0, -1, new List<object?>()]);
        Assert.Equal("u(ia{sv}av)", reply!.Value.Signature);
        Assert.Equal(0u, reply.Value.Values[0]);
        Assert.Equal("0{children-display=submenu}[1{label=_Show,shortcut=[[Control,s]]}[],2{type=separator}[],3{label=Pinned,toggle-state=1,toggle-type=checkmark}[],"
            + "4{enabled=False,label=Off}[],5{label=Hidden,visible=False}[],9{children-display=submenu,label=More}[6{label=Deep__er}[],"
            + "7{label=A,toggle-state=1,toggle-type=radio}[],8{label=B,toggle-state=0,toggle-type=radio}[]],10{label=Quit}[],11{label=Typed}[],12{}[],13{label=C,toggle-state=0,toggle-type=checkmark}[]]",
            Layout(reply.Value.Values[1]));
        Assert.Empty(_signals);
    }

    [Fact]
    public void GetLayout_DepthAndFilter_AndFailures()
    {
        var menu = NewMenu();
        menu.SetModel(Sample(), false);
        Assert.Equal("9{label=More}[]", Layout(menu.Handle(DbusMenu.Interface, "GetLayout", [9, 0, new List<object?> { "label" }])!.Value.Values[1]));
        Assert.Equal("9{}[6{}[],7{toggle-state=1}[],8{toggle-state=0}[]]", Layout(menu.Handle(DbusMenu.Interface, "GetLayout", [9, 1, new List<object?> { "toggle-state" }])!.Value.Values[1]));
        Assert.Contains(",9{children-display=submenu}[],", Layout(menu.Handle(DbusMenu.Interface, "GetLayout", [0, 1, new List<object?> { "children-display" }])!.Value.Values[1]));
        Assert.Null(menu.Handle(DbusMenu.Interface, "GetLayout", [99, -1, new List<object?>()]));
        Assert.Null(menu.Handle(DbusMenu.Interface, "GetLayout", [0, -2, new List<object?>()]));
        Assert.Null(menu.Handle(DbusMenu.Interface, "GetLayout", ["0", -1, new List<object?>()]));
    }

    [Fact]
    public void SetModel_SignalsLayoutUpdated_AndIdsKeepCounting()
    {
        var menu = NewMenu();
        menu.SetModel(Sample(), false);
        menu.SetModel(null, false);
        menu.SetModel(Sample(), true);
        Assert.Equal(["LayoutUpdated:ui(1,0)"], _signals);
        Assert.Equal(1u, menu.Revision);
        var root = (object?[])menu.Handle(DbusMenu.Interface, "GetLayout", [0, 1, new List<object?>()])!.Value.Values[1]!;
        Assert.Equal(new object?[] { 14, 15, 16, 17, 18, 22, 23, 24, 25, 26 }, ((List<object?>)root[2]!).Select(c => ((object?[])((LinuxDBusVariant)c!).Value!)[0]).ToArray());
    }

    [Fact]
    public void SetModel_Null_LeavesARootWithoutProperties()
    {
        var menu = NewMenu();
        menu.SetModel(null, false);
        Assert.Equal("0{}[]", Layout(menu.Handle(DbusMenu.Interface, "GetLayout", [0, -1, new List<object?>()])!.Value.Values[1]));
    }

    [Fact]
    public void GetGroupProperties_AskedIdsInOrder_OrEveryId()
    {
        var menu = NewMenu();
        menu.SetModel(Sample(), false);
        var some = menu.Handle(DbusMenu.Interface, "GetGroupProperties", [new List<object?> { 3, 99, 1 }, new List<object?> { "label", "toggle-state" }])!.Value;
        Assert.Equal("a(ia{sv})", some.Signature);
        Assert.Equal("3{label=Pinned,toggle-state=1};1{label=_Show}", Rows(some.Values[0]));
        var all = menu.Handle(DbusMenu.Interface, "GetGroupProperties", [new List<object?>(), new List<object?> { "type" }])!.Value;
        Assert.Equal("0{};1{};2{type=separator};3{};4{};5{};6{};7{};8{};9{};10{};11{};12{};13{}", Rows(all.Values[0]));
    }

    [Fact]
    public void GetProperty_StoredDefaultOrFailure()
    {
        var menu = NewMenu();
        menu.SetModel(Sample(), false);
        Assert.Equal(new LinuxDBusVariant("b", false), menu.Handle(DbusMenu.Interface, "GetProperty", [4, "enabled"])!.Value.Values[0]);
        Assert.Equal(new LinuxDBusVariant("i", -1), menu.Handle(DbusMenu.Interface, "GetProperty", [1, "toggle-state"])!.Value.Values[0]);
        Assert.Null(menu.Handle(DbusMenu.Interface, "GetProperty", [1, "bogus"]));
        Assert.Null(menu.Handle(DbusMenu.Interface, "GetProperty", [99, "label"]));
    }

    [Theory]
    [InlineData("type", "s", "standard")]
    [InlineData("label", "s", "")]
    [InlineData("enabled", "b", true)]
    [InlineData("visible", "b", true)]
    [InlineData("icon-name", "s", "")]
    [InlineData("toggle-type", "s", "")]
    [InlineData("toggle-state", "i", -1)]
    [InlineData("children-display", "s", "")]
    public void DefaultPropertyValue_IsChromiums(string name, string signature, object value)
        => Assert.Equal(new LinuxDBusVariant(signature, value), DbusMenu.DefaultPropertyValue(name));

    [Fact]
    public void DefaultPropertyValue_ArraysAndUnknown()
    {
        Assert.Equal(new LinuxDBusVariant("ay", Array.Empty<byte>()), DbusMenu.DefaultPropertyValue("icon-data"));
        Assert.Equal(new LinuxDBusVariant("aas", new List<object?>()), DbusMenu.DefaultPropertyValue("shortcut"));
        Assert.Null(DbusMenu.DefaultPropertyValue("bogus"));
    }

    [Fact]
    public void Event_Clicked_ActivatesTheItemAtItsIndex_OthersDoNothing()
    {
        var menu = NewMenu();
        menu.SetModel(Sample(), false);
        Assert.Equal(DbusReply.Empty, menu.Handle(DbusMenu.Interface, "Event", [4, "clicked", new LinuxDBusVariant("s", ""), 0u]));
        Assert.Equal(DbusReply.Empty, menu.Handle(DbusMenu.Interface, "Event", [8, "clicked", new LinuxDBusVariant("s", ""), 0u]));
        Assert.Equal(DbusReply.Empty, menu.Handle(DbusMenu.Interface, "Event", [1, "hovered", new LinuxDBusVariant("s", ""), 0u]));
        // A separator is command -1 in Electron: nothing runs.
        Assert.Equal(DbusReply.Empty, menu.Handle(DbusMenu.Interface, "Event", [2, "clicked", new LinuxDBusVariant("s", ""), 0u]));
        Assert.Equal(["Off", "B"], _ran);
        Assert.Null(menu.Handle(DbusMenu.Interface, "Event", [0, "clicked", new LinuxDBusVariant("s", ""), 0u]));
        Assert.Null(menu.Handle(DbusMenu.Interface, "Event", [99, "clicked", new LinuxDBusVariant("s", ""), 0u]));
        Assert.Null(menu.Handle(DbusMenu.Interface, "Event", [1]));
        Assert.Equal(["Off", "B"], _ran);
    }

    [Fact]
    public void Event_Clicked_RunsWhateverTheMenuHoldsAtTheIndexNow()
    {
        var model = Sample();
        var menu = NewMenu();
        menu.SetModel(model, false);
        model.Insert(0, new MenuItem { Label = "Inserted" });
        menu.Handle(DbusMenu.Interface, "Event", [1, "clicked", new LinuxDBusVariant("s", ""), 0u]);
        Assert.Equal(["Inserted"], _ran);
    }

    [Fact]
    public void EventGroup_ListsUnknownIds_AndFailsOnOtherErrors()
    {
        var menu = NewMenu();
        menu.SetModel(Sample(), false);
        var reply = menu.Handle(DbusMenu.Interface, "EventGroup", [new List<object?> { new object?[] { 1, "clicked", null, 0u }, new object?[] { 99, "clicked", null, 0u } }])!.Value;
        Assert.Equal("ai", reply.Signature);
        Assert.Equal(new List<object?> { 99 }, reply.Values[0]);
        Assert.Null(menu.Handle(DbusMenu.Interface, "EventGroup", [new List<object?> { new object?[] { 0, "clicked", null, 0u } }]));
        Assert.Equal(["&Show"], _ran);
    }

    [Fact]
    public void AboutToShow_FalseForRootAndSubmenus_FailsForLeaves()
    {
        var menu = NewMenu();
        menu.SetModel(Sample(), false);
        Assert.Equal(("b", false), menu.Handle(DbusMenu.Interface, "AboutToShow", [9]) is { } r9 ? (r9.Signature, r9.Values[0]) : default);
        Assert.Equal(("b", false), menu.Handle(DbusMenu.Interface, "AboutToShow", [0]) is { } r0 ? (r0.Signature, r0.Values[0]) : default);
        Assert.Null(menu.Handle(DbusMenu.Interface, "AboutToShow", [1]));
        Assert.Null(menu.Handle(DbusMenu.Interface, "AboutToShow", [99]));
        var group = menu.Handle(DbusMenu.Interface, "AboutToShowGroup", [new List<object?> { 9, 1, 99 }])!.Value;
        Assert.Equal("aiai", group.Signature);
        Assert.Equal(new List<object?>(), group.Values[0]);
        Assert.Equal(new List<object?> { 1, 99 }, group.Values[1]);
    }

    [Fact]
    public void Properties_AreTheMenusFour_ForItsInterfaceOnly()
    {
        var menu = NewMenu();
        var all = menu.Handle(LinuxTrayRules.PropertiesInterface, "GetAll", [DbusMenu.Interface])!.Value;
        Assert.Equal("a{sv}", all.Signature);
        Assert.Equal("IconThemePath=<as>[] Status=<s>'normal' TextDirection=<s>'ltr' Version=<u>3",
            string.Join(" ", ((SortedDictionary<string, LinuxDBusVariant>)all.Values[0]!).Select(p => $"{p.Key}={p.Value}")));
        Assert.Empty((SortedDictionary<string, LinuxDBusVariant>)menu.Handle(LinuxTrayRules.PropertiesInterface, "GetAll", [LinuxTrayRules.PropertiesInterface])!.Value.Values[0]!);
        Assert.Null(menu.Handle(LinuxTrayRules.PropertiesInterface, "GetAll", ["org.example"]));
        Assert.Equal(new LinuxDBusVariant("u", 3u), menu.Handle(LinuxTrayRules.PropertiesInterface, "Get", [DbusMenu.Interface, "Version"])!.Value.Values[0]);
        Assert.Null(menu.Handle(LinuxTrayRules.PropertiesInterface, "Get", ["org.example", "Version"]));
        Assert.Null(menu.Handle(LinuxTrayRules.PropertiesInterface, "Get", [DbusMenu.Interface, "Nope"]));
        Assert.Equal(DbusReply.Empty, menu.Handle(LinuxTrayRules.PropertiesInterface, "Set", [DbusMenu.Interface, "Version", new LinuxDBusVariant("u", 4u)]));
    }

    [Theory]
    [InlineData("com.canonical.dbusmenu", "GetLayout", true)]
    [InlineData("com.canonical.dbusmenu", "EventGroup", true)]
    [InlineData("com.canonical.dbusmenu", "Bogus", false)]
    [InlineData("org.freedesktop.DBus.Properties", "Set", true)]
    [InlineData("org.freedesktop.DBus.Properties", "GetLayout", false)]
    public void Exports_ChromiumsMethodTable(string iface, string member, bool expected) => Assert.Equal(expected, DbusMenu.Exports(iface, member));

    [Fact]
    public void GtkModel_IsElectronsMenuGtkModel()
    {
        var model = LinuxTrayRules.GtkModel(
        [
            new MenuItem { Label = "&Open" },
            new MenuItem { Type = MenuItemType.Separator, Label = "x" },
            new MenuItem { Label = "R1", Type = MenuItemType.Radio },
            new MenuItem { Label = "R2", Type = MenuItemType.Radio },
            new MenuItem { Type = MenuItemType.Separator, Visible = false },
            new MenuItem { Label = "R3", Type = MenuItemType.Radio, Checked = true, Enabled = false, Visible = false },
            new MenuItem { Label = "C", Type = MenuItemType.Checkbox },
            new MenuItem { Label = "S", Submenu = [new MenuItem { Label = "Deep" }] },
            new MenuItem { Label = "T", Type = MenuItemType.Submenu },
        ])!;
        Assert.Equal("Command:_Open Separator: Radio:R1 Radio:R2 Separator: Radio:R3 Check:C Submenu:S Command:T",
            string.Join(" ", model.Select(i => $"{i.Type}:{i.Label}")));
        // Radio items carry Electron's groupId: a separator starts a new group.
        Assert.Equal(model[2].GroupId, model[3].GroupId);
        Assert.NotEqual(model[3].GroupId, model[5].GroupId);
        Assert.NotEqual(0, model[5].GroupId);
        Assert.True(model[5].IsChecked() && !model[5].IsEnabled() && !model[5].IsVisible());
        Assert.True(model[1].IsEnabled() && model[1].IsVisible() && model[1].Activated is null);
        // Electron never hides a separator: its command id is -1.
        Assert.True(model[4].IsVisible());
        Assert.Equal("Deep", Assert.Single(model[7].Submenu!).Label);
        Assert.Null(LinuxTrayRules.GtkModel(null));
    }

    [Fact]
    public void Run_IgnoresEnabled_WhereActivateChecksIt()
    {
        var clicks = 0;
        var item = new MenuItem { Label = "Off", Enabled = false, Type = MenuItemType.Checkbox, Click = (_, _, _) => clicks++ };
        item.Activate(null);
        Assert.Equal((0, false), (clicks, item.Checked));
        item.Run(null);
        Assert.Equal((1, true), (clicks, item.Checked));
    }

    private static string Layout(object? value)
    {
        var fields = (object?[])value!;
        var children = ((List<object?>)fields[2]!).Select(c => Layout(((LinuxDBusVariant)c!).Value));
        return $"{fields[0]}{{{Props(fields[1])}}}[{string.Join(",", children)}]";
    }

    private static string Rows(object? value) => string.Join(";", ((List<object?>)value!).Select(r => (object?[])r!).Select(r => $"{r[0]}{{{Props(r[1])}}}"));

    private static string Props(object? dict) => string.Join(",", ((SortedDictionary<string, LinuxDBusVariant>)dict!).Select(p => $"{p.Key}={Plain(p.Value.Value)}"));

    private static string Plain(object? value) => value switch
    {
        List<object?> list => "[" + string.Join(",", list.Select(Plain)) + "]",
        _ => value?.ToString() ?? "null",
    };
}

public sealed class LinuxDBusValuesTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("s", "s")]
    [InlineData("u(ia{sv}av)", "u|(ia{sv}av)")]
    [InlineData("sa{sv}as", "s|a{sv}|as")]
    [InlineData("aiai", "ai|ai")]
    [InlineData("a(isvu)", "a(isvu)")]
    [InlineData("(sa(iiay)ss)", "(sa(iiay)ss)")]
    [InlineData("aasx", "aas|x")]
    public void Split_GivesCompleteTypes(string signature, string expected) => Assert.Equal(expected, string.Join("|", LinuxDBusValues.Split(signature)));

    [Theory]
    [InlineData("a")]
    [InlineData("(ii")]
    [InlineData("a{sv")]
    [InlineData("z")]
    public void Split_RejectsBrokenSignatures(string signature) => Assert.Throws<ArgumentException>(() => LinuxDBusValues.Split(signature));

    [Fact]
    public void Variant_ComparesByValue()
    {
        Assert.Equal(new LinuxDBusVariant("ay", new byte[] { 1, 2 }), new LinuxDBusVariant("ay", new byte[] { 1, 2 }));
        Assert.NotEqual(new LinuxDBusVariant("ay", new byte[] { 1, 2 }), new LinuxDBusVariant("ay", new byte[] { 1, 3 }));
        Assert.NotEqual(new LinuxDBusVariant("i", 1), new LinuxDBusVariant("u", 1));
        Assert.Equal(new LinuxDBusVariant("(sa(iiay)ss)", new object?[] { "", new List<object?>(), "t", "" }), new LinuxDBusVariant("(sa(iiay)ss)", new object?[] { "", new List<object?>(), "t", "" }));
        Assert.NotEqual(new LinuxDBusVariant("(sa(iiay)ss)", new object?[] { "", new List<object?>(), "t", "" }), new LinuxDBusVariant("(sa(iiay)ss)", new object?[] { "", new List<object?>(), "u", "" }));
        Assert.NotEqual(new LinuxDBusVariant("as", new List<object?> { "a" }), new LinuxDBusVariant("as", new List<object?> { "a", "b" }));
        var a = new SortedDictionary<string, LinuxDBusVariant> { ["k"] = new("s", "v") };
        var b = new SortedDictionary<string, LinuxDBusVariant> { ["k"] = new("s", "w") };
        Assert.False(LinuxDBusValues.DeepEquals(a, b));
        Assert.True(LinuxDBusValues.DeepEquals(a, new SortedDictionary<string, LinuxDBusVariant> { ["k"] = new("s", "v") }));
        Assert.True(LinuxDBusValues.DeepEquals((1, "x"), (1, "x")));
        Assert.False(LinuxDBusValues.DeepEquals((1, "x"), (1, "y")));
    }

    [Fact]
    public void Format_IsReadable()
        => Assert.Equal("<a(iiay)>[(1, 1, [2 bytes])]", new LinuxDBusVariant("a(iiay)", new List<object?> { (1, 1, new byte[2]) }).ToString());

    static LinuxDBusValuesTests() => _ = Encoding.UTF8;
}
