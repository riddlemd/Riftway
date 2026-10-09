using Oriel.Cef.Platform;

namespace Oriel.Tests;

// Electron v44's menu-will-show / menu-will-close and the _menuWillShow radio fix-up (lib/browser/api/menu.ts).
public sealed class MenuEventsTests
{
    private static MenuItem Radio(string label, bool on = false) => new() { Label = label, Type = MenuItemType.Radio, Checked = on };

    private static MenuItem Normal(string label) => new() { Label = label };

    private static MenuItem Separator() => new() { Type = MenuItemType.Separator };

    [Fact]
    public void GroupId_ANormalItemDoesNotSplitAGroup_ASeparatorDoes()
    {
        var a = Radio("a");
        var c = Radio("c");
        var d = Radio("d");
        _ = new Menu { a, Normal("b"), c, Separator(), d };

        Assert.Equal(a.GroupId, c.GroupId);
        Assert.NotEqual(c.GroupId, d.GroupId);
        Assert.NotEqual(0, a.GroupId);
    }

    [Fact]
    public void GroupId_AtPositionZeroJoinsTheGroupAfter_ElsewhereOnlyTheGroupBefore()
    {
        var b = Radio("b");
        var menu = new Menu { Normal("n"), b };

        // Inserted at 1, between "n" and "b": generateGroupId only looks back.
        var second = Radio("second");
        menu.Insert(1, second);
        Assert.NotEqual(b.GroupId, second.GroupId);

        var first = Radio("first");
        menu.Insert(0, first);
        Assert.Equal(second.GroupId, first.GroupId);
    }

    [Fact]
    public void GroupId_AtPositionZeroStopsAtASeparator()
    {
        var x = Radio("x");
        var menu = new Menu { Separator(), x };
        var first = Radio("first");
        menu.Insert(0, first);
        Assert.NotEqual(x.GroupId, first.GroupId);
    }

    [Fact]
    public void GroupId_IsZeroForOtherTypes()
    {
        var normal = Normal("n");
        var check = new MenuItem { Type = MenuItemType.Checkbox };
        _ = new Menu { normal, check };
        Assert.Equal(0, normal.GroupId);
        Assert.Equal(0, check.GroupId);
    }

    [Fact]
    public void Activate_ChecksTheRadioAndUnchecksItsGroupAcrossNormalItems()
    {
        var a = Radio("a", on: true);
        var c = Radio("c");
        var other = Radio("other", on: true);
        _ = new Menu { a, Normal("b"), c, Separator(), other };

        c.Activate(null);

        Assert.Equal([false, true, true], new[] { a.Checked, c.Checked, other.Checked });
    }

    [Fact]
    public void EnsureRadioGroupsChecked_ChecksTheFirstInsertedOfAGroupWithNoneChecked()
    {
        var later = Radio("later");
        var menu = new Menu { later };
        var earlier = Radio("earlier");
        // Same group, first in position but second in groupsMap.
        menu.Insert(0, earlier);

        var fixedUp = menu.EnsureRadioGroupsChecked();

        Assert.Equal([later], fixedUp);
        Assert.True(later.Checked);
        Assert.False(earlier.Checked);
    }

    [Fact]
    public void EnsureRadioGroupsChecked_LeavesAGroupWithACheckedItem()
    {
        var a = Radio("a");
        var b = Radio("b", on: true);
        var c = Radio("c");
        var d = Radio("d");
        var menu = new Menu { a, b, Separator(), c, d };

        var fixedUp = menu.EnsureRadioGroupsChecked();

        Assert.Equal([c], fixedUp);
        Assert.Equal([false, true, true, false], new[] { a.Checked, b.Checked, c.Checked, d.Checked });
        Assert.Empty(menu.EnsureRadioGroupsChecked());
    }

    [Fact]
    public void EnsureRadioGroupsChecked_IsQuiet()
    {
        var a = Radio("a");
        var menu = new Menu { a };
        var changes = 0;
        menu.Changed += () => changes++;

        menu.EnsureRadioGroupsChecked();

        Assert.True(a.Checked);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void OnMenuWillShow_FixesRadioGroupsBeforeRaisingTheEvent()
    {
        var a = Radio("a");
        var menu = new Menu { a, Radio("b") };
        object? sender = null;
        var checkedWhenRaised = false;
        menu.MenuWillShow += (s, e) =>
        {
            sender = s;
            checkedWhenRaised = a.Checked;
        };

        var fixedUp = menu.OnMenuWillShow();

        Assert.Same(menu, sender);
        Assert.True(checkedWhenRaised);
        Assert.Equal([a], fixedUp);
    }

    [Fact]
    public void OnMenuWillClose_RaisesTheEvent()
    {
        var menu = new Menu { Normal("x") };
        object? sender = null;
        menu.MenuWillClose += (s, _) => sender = s;
        menu.OnMenuWillClose();
        Assert.Same(menu, sender);
    }

    private static (Menu Root, Menu Sub, Menu Deep, List<string> Log) Tree()
    {
        var deep = new Menu { Normal("deep") };
        var sub = new Menu { Normal("s"), new MenuItem { Label = "Deep", Submenu = deep } };
        var root = new Menu { Normal("r"), new MenuItem { Label = "Sub", Submenu = sub } };
        var log = new List<string>();
        foreach (var (menu, name) in new[] { (root, "root"), (sub, "sub"), (deep, "deep") })
        {
            menu.MenuWillShow += (_, _) => log.Add("show " + name);
            menu.MenuWillClose += (_, _) => log.Add("close " + name);
        }
        return (root, sub, deep, log);
    }

    [Fact]
    public void MenuRun_ASubmenuClosingRaisesOnItsOwnMenu()
    {
        var (root, sub, _, log) = Tree();
        var run = new MenuRun(root);
        run.WillShow(root);
        run.WillShow(sub);

        Assert.False(run.Closed(sub, isRoot: false));

        Assert.Equal(["show root", "show sub", "close sub"], log);
    }

    [Fact]
    public void MenuRun_TheTopClosingClosesEveryOpenMenuTopDown_ThenIgnoresTheirLateReports()
    {
        var (root, sub, deep, log) = Tree();
        var run = new MenuRun(root);
        run.WillShow(root);
        run.WillShow(sub);
        run.WillShow(deep);

        Assert.True(run.Closed(root, isRoot: true));
        Assert.False(run.Closed(deep, isRoot: false));
        Assert.False(run.Closed(sub, isRoot: false));

        Assert.Equal(["show root", "show sub", "show deep", "close root", "close sub", "close deep"], log);
    }

    [Fact]
    public void MenuRun_AMenuThatNeverShowedDoesNotReportClosing()
    {
        var (root, sub, _, log) = Tree();
        var run = new MenuRun(root);
        run.WillShow(root);

        run.Closed(sub, isRoot: false);
        run.Closed(null, isRoot: false);

        Assert.Equal(["show root"], log);
    }

    [Fact]
    public void MenuRun_AMenuShownAgainWhileOnThePathClosesOnce()
    {
        var (root, sub, _, log) = Tree();
        var run = new MenuRun(root);
        run.WillShow(root);
        run.WillShow(sub);
        run.WillShow(sub);

        run.Closed(root, isRoot: true);

        Assert.Equal(["show root", "show sub", "show sub", "close root", "close sub"], log);
    }

    [Fact]
    public void MenuRun_AReleasedRunReportsNoClose()
    {
        var (root, sub, _, log) = Tree();
        var run = new MenuRun(root);
        run.WillShow(root);
        run.WillShow(sub);

        run.Release();

        Assert.False(run.Closed(root, isRoot: true));
        Assert.False(run.Closed(sub, isRoot: false));
        Assert.Equal(["show root", "show sub"], log);
    }

    [Fact]
    public void MenuRun_WillShowReturnsTheItemsTheFixUpChecked()
    {
        var a = Radio("a");
        var sub = new Menu { a, Radio("b") };
        var root = new Menu { new MenuItem { Label = "Sub", Submenu = sub } };
        var run = new MenuRun(root);

        Assert.Empty(run.WillShow(root));
        Assert.Equal([a], run.WillShow(sub));
    }

    [Fact]
    public void Resolve_IsTheParentOfTheFirstItemThatIsNotASeparator()
    {
        var (root, sub, deep, _) = Tree();
        Assert.Same(sub, MenuRun.Resolve(root, [-1, sub.Items[0].CommandId]));
        Assert.Same(deep, MenuRun.Resolve(root, [deep.Items[0].CommandId]));
        Assert.Same(root, MenuRun.Resolve(root, [root.Items[1].CommandId, sub.Items[0].CommandId]));
    }

    [Fact]
    public void Resolve_FindsAnEmptySubmenuByItsMarker_AndNothingForUnknownIds()
    {
        var empty = new Menu();
        var root = new Menu { new MenuItem { Label = "Empty", Submenu = empty } };
        var marker = empty.MarkerCommandId;

        Assert.Same(empty, MenuRun.Resolve(root, [marker]));
        Assert.Equal(marker, empty.MarkerCommandId);
        Assert.Null(MenuRun.Resolve(root, [int.MaxValue]));
        Assert.Null(MenuRun.Resolve(root, [-1]));
        Assert.Null(MenuRun.Resolve(root, []));
    }

    [Fact]
    public void MacMenuController_EveryOpeningMenuRaisesOnTheTopMenu_OnlyTheTopLevelClosingEndsIt()
    {
        var (root, _, _, log) = Tree();
        const nint bar = 1, file = 2, recent = 3;
        var removed = new List<nint>();
        var controller = new MacMenuController(root, bar);

        controller.WillOpen(file, removed.Add);
        controller.WillOpen(recent, removed.Add);
        controller.DidClose(recent, file);
        Assert.Equal(["show root", "show root"], log);

        controller.DidClose(file, bar);
        // Already closed: AppKit's later reports do nothing.
        controller.DidClose(file, bar);

        Assert.Equal(["show root", "show root", "close root"], log);
        Assert.Equal([file, recent], removed);
    }

    [Fact]
    public void MacMenuController_TheTopMenuItselfClosingEndsIt()
    {
        var (root, _, _, log) = Tree();
        const nint top = 7;
        var controller = new MacMenuController(root, top);

        controller.WillOpen(top, _ => { });
        controller.DidClose(top, 0);

        Assert.Equal(["show root", "close root"], log);
    }

    [Fact]
    public void DbusMenu_AboutToShow_RaisesOnTheSubmenuOnly_AndLeavesTheLayout()
    {
        var a = Radio("a");
        var sub = new Menu { a, Radio("b") };
        var root = new Menu { Normal("r"), new MenuItem { Label = "Sub", Submenu = sub } };
        var log = new List<string>();
        root.MenuWillShow += (_, _) => log.Add("root");
        sub.MenuWillShow += (_, _) => log.Add("sub");
        var dbus = new DbusMenu((_, _, _) => { }, _ => { }, "ltr");
        dbus.SetModel(root, false);
        // Ids: r=1, then the submenu's items before the item that opens it: a=2, b=3, Sub=4.
        var before = dbus.Handle(DbusMenu.Interface, "GetLayout", [4, -1, new List<object?> { "toggle-state" }]);

        dbus.Handle(DbusMenu.Interface, "AboutToShow", [0]);
        Assert.Empty(log);
        var reply = dbus.Handle(DbusMenu.Interface, "AboutToShow", [4])!.Value;

        Assert.Equal(("b", (object?)false), (reply.Signature, reply.Values[0]));
        Assert.Equal(["sub"], log);
        Assert.True(a.Checked);
        // Chromium computed the properties when it built the layout; AboutToShow answers "no update needed".
        var after = dbus.Handle(DbusMenu.Interface, "GetLayout", [4, -1, new List<object?> { "toggle-state" }]);
        Assert.Equal(Describe(before!.Value.Values[1]), Describe(after!.Value.Values[1]));
    }

    [Fact]
    public void DbusMenu_AboutToShowGroup_RaisesForEachSubmenu()
    {
        var sub = new Menu { Normal("s") };
        var root = new Menu { new MenuItem { Label = "Sub", Submenu = sub } };
        var shown = 0;
        sub.MenuWillShow += (_, _) => shown++;
        var dbus = new DbusMenu((_, _, _) => { }, _ => { }, "ltr");
        dbus.SetModel(root, false);

        dbus.Handle(DbusMenu.Interface, "AboutToShowGroup", [new List<object?> { 2, 0, 1, 2 }]);

        Assert.Equal(2, shown);
    }

    [Fact]
    public void GtkModel_SubmenuWillShowRaisesOnTheSubmenu()
    {
        var a = Radio("a");
        var sub = new Menu { a };
        var shown = 0;
        sub.MenuWillShow += (_, _) => shown++;
        var model = LinuxTrayRules.GtkModel([Normal("r"), new MenuItem { Label = "Sub", Submenu = sub }])!;

        Assert.Null(model[0].SubmenuWillShow);
        model[1].SubmenuWillShow!();

        Assert.Equal(1, shown);
        Assert.True(a.Checked);
    }

    private static string Describe(object? value) => value switch
    {
        object?[] array => "[" + string.Join(",", array.Select(Describe)) + "]",
        System.Collections.IEnumerable list and not string => "[" + string.Join(",", list.Cast<object?>().Select(Describe)) + "]",
        LinuxDBusVariant v => Describe(v.Value),
        _ => value?.ToString() ?? "null",
    };
}
