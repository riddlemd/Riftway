using System.Text.Json.Nodes;

namespace Oriel.Tests;

public sealed class WindowStateRulesTests
{
    private static readonly Rectangle WorkArea = new(0, 0, 1920, 1040);

    [Fact]
    public void AdjustToFit_ShrinksToTheTargetThenSlidesInside()
    {
        Assert.Equal(new Rectangle(0, 0, 1920, 1040), WindowStateRules.AdjustToFit(new Rectangle(-50, -20, 3000, 2000), WorkArea));
        Assert.Equal(new Rectangle(1520, 640, 400, 400), WindowStateRules.AdjustToFit(new Rectangle(1800, 900, 400, 400), WorkArea));
        Assert.Equal(new Rectangle(10, 20, 400, 300), WindowStateRules.AdjustToFit(new Rectangle(10, 20, 400, 300), WorkArea));
    }

    [Fact]
    public void PickDisplay_TakesTheLeastMovement_TheEarliestOnATie_AndThePrimaryWithNone()
    {
        Rectangle[] areas = [new(0, 0, 1920, 1040), new(1920, 0, 1280, 1024)];
        Assert.Equal(1, WindowStateRules.PickDisplay(new Rectangle(2000, 100, 800, 600), areas, 0));
        Assert.Equal(0, WindowStateRules.PickDisplay(new Rectangle(100, 100, 800, 600), areas, 1));
        Assert.Equal(0, WindowStateRules.PickDisplay(new Rectangle(100, 100, 800, 600), [WorkArea, WorkArea], 1));
        Assert.Equal(-1, WindowStateRules.PickDisplay(new Rectangle(100, 100, 800, 600), [], -1));
    }

    [Fact]
    public void RestoreBounds_KeepsBoundsThatFit()
    {
        Assert.Equal(new Rectangle(100, 120, 700, 500), WindowStateRules.RestoreBounds(new Rectangle(100, 120, 700, 500), WorkArea, WorkArea, mac: false));
    }

    [Fact]
    public void RestoreBounds_ZeroSize_IsNotRestored()
    {
        Assert.Null(WindowStateRules.RestoreBounds(new Rectangle(100, 100, 0, 500), WorkArea, WorkArea, mac: false));
        Assert.Null(WindowStateRules.RestoreBounds(new Rectangle(100, 100, 500, 0), WorkArea, WorkArea, mac: false));
    }

    [Fact]
    public void RestoreBounds_GrowsToTheMinimumVisibleSize()
    {
        Assert.Equal(new Rectangle(100, 100, 100, 100), WindowStateRules.RestoreBounds(new Rectangle(100, 100, 40, 60), WorkArea, WorkArea, mac: false));
    }

    [Fact]
    public void RestoreBounds_MovesTheTitleBarBelowTheWorkAreaTop()
    {
        var area = new Rectangle(0, 40, 1920, 1000);
        Assert.Equal(new Rectangle(100, 40, 700, 500), WindowStateRules.RestoreBounds(new Rectangle(100, 10, 700, 500), area, area, mac: false));
    }

    [Fact]
    public void RestoreBounds_KeepsAtLeast100DipsOnScreen_OffMac()
    {
        // Mostly off the right and bottom edges, same work area: clamped to leave 100x100 visible, size kept.
        Assert.Equal(new Rectangle(1820, 940, 700, 500), WindowStateRules.RestoreBounds(new Rectangle(1900, 1000, 700, 500), WorkArea, WorkArea, mac: false));
        // Mostly off the left edge.
        Assert.Equal(new Rectangle(-600, 100, 700, 500), WindowStateRules.RestoreBounds(new Rectangle(-900, 100, 700, 500), WorkArea, WorkArea, mac: false));
    }

    [Fact]
    public void RestoreBounds_FitsIntoASmallerWorkAreaThanTheSavedOne()
    {
        var saved = new Rectangle(0, 0, 2560, 1400);
        var small = new Rectangle(0, 0, 1280, 760);
        Assert.Equal(new Rectangle(0, 0, 1280, 760), WindowStateRules.RestoreBounds(new Rectangle(1200, 600, 1600, 900), saved, small, mac: false));
    }

    [Fact]
    public void RestoreBounds_SameWorkArea_DoesNotFit_OnlyClamps()
    {
        // Larger than the work area but saved against it: Electron only clamps the origin.
        Assert.Equal(new Rectangle(0, 0, 2400, 1200), WindowStateRules.RestoreBounds(new Rectangle(0, 0, 2400, 1200), WorkArea, WorkArea, mac: false));
    }

    [Fact]
    public void RestoreBounds_TinyWorkArea_Fits()
    {
        // Under 100 DIPs across, no origin keeps 100 visible (min_x >= max_x), so the bounds are fitted instead.
        var tiny = new Rectangle(0, 0, 80, 80);
        Assert.Equal(new Rectangle(0, 0, 80, 80), WindowStateRules.RestoreBounds(new Rectangle(0, 0, 100, 100), tiny, tiny, mac: false));
    }

    [Fact]
    public void RestoreBounds_Mac_FitsCompletely()
    {
        Assert.Equal(new Rectangle(1220, 540, 700, 500), WindowStateRules.RestoreBounds(new Rectangle(1900, 1000, 700, 500), WorkArea, WorkArea, mac: true));
    }

    [Fact]
    public void BoundsToSave_KeepsTheSavedBoundsOutsideTheNormalState()
    {
        var current = new Rectangle(0, 0, 1920, 1040);
        var existing = WindowStateRules.ToJson(new Rectangle(10, 20, 300, 200), false, false, false, WorkArea);
        Assert.Equal(new Rectangle(10, 20, 300, 200), WindowStateRules.BoundsToSave(current, isNormal: false, existing));
        Assert.Equal(current, WindowStateRules.BoundsToSave(current, isNormal: true, existing));
        Assert.Equal(current, WindowStateRules.BoundsToSave(current, isNormal: false, null));
        existing.Remove("bottom");
        Assert.Equal(current, WindowStateRules.BoundsToSave(current, isNormal: false, existing));
    }

    [Fact]
    public void ToJson_HasElectronsKeysAndEdges()
    {
        var json = WindowStateRules.ToJson(new Rectangle(10, 20, 300, 200), true, false, true, new Rectangle(0, 25, 1920, 1015));
        Assert.Equal(
            """{"left":10,"top":20,"right":310,"bottom":220,"maximized":true,"fullscreen":false,"kiosk":true,"workAreaLeft":0,"workAreaTop":25,"workAreaRight":1920,"workAreaBottom":1040}""",
            json.ToJsonString());
    }

    [Fact]
    public void Parse_ReadsBoundsAndWorkArea()
    {
        var json = (JsonObject)JsonNode.Parse("""{"bottom":220,"left":10,"right":310,"top":20,"workAreaBottom":1040,"workAreaLeft":0,"workAreaRight":1920,"workAreaTop":25}""")!;
        Assert.Equal(new SavedWindowState(new Rectangle(10, 20, 300, 200), new Rectangle(0, 25, 1920, 1015)), WindowStateRules.Parse(json));
    }

    [Theory]
    [InlineData("left")]
    [InlineData("top")]
    [InlineData("right")]
    [InlineData("bottom")]
    [InlineData("workAreaLeft")]
    [InlineData("workAreaTop")]
    [InlineData("workAreaRight")]
    [InlineData("workAreaBottom")]
    public void Parse_MissingOrNonIntegerValue_IsCorrupted(string key)
    {
        var json = WindowStateRules.ToJson(new Rectangle(10, 20, 300, 200), false, false, false, WorkArea);
        json.Remove(key);
        Assert.Null(WindowStateRules.Parse(json));
        json[key] = "5";
        Assert.Null(WindowStateRules.Parse(json));
        json[key] = 1.5;
        Assert.Null(WindowStateRules.Parse(json));
    }

    [Fact]
    public void Flag_IsTrueOnlyForABooleanTrue()
    {
        var json = new JsonObject { ["maximized"] = true, ["fullscreen"] = false, ["kiosk"] = "true" };
        Assert.True(WindowStateRules.Flag(json, "maximized"));
        Assert.False(WindowStateRules.Flag(json, "fullscreen"));
        Assert.False(WindowStateRules.Flag(json, "kiosk"));
        Assert.False(WindowStateRules.Flag(json, "missing"));
    }

    [Fact]
    public void IsInvalidDisplay_TheStandInOrNoSize()
    {
        Assert.True(WindowStateRules.IsInvalidDisplay(0xFF, new Rectangle(0, 0, 1920, 1080)));
        Assert.True(WindowStateRules.IsInvalidDisplay(1, new Rectangle(0, 0, 0, 1080)));
        Assert.True(WindowStateRules.IsInvalidDisplay(1, new Rectangle(0, 0, 1920, 0)));
        Assert.False(WindowStateRules.IsInvalidDisplay(1, new Rectangle(0, 0, 1920, 1080)));
    }

    [Fact]
    public void Persistence_TrueKeepsBoth_FalseKeepsNothing()
    {
        WindowStatePersistence? on = true;
        WindowStatePersistence? off = false;
        Assert.Equal(new WindowStatePersistence { Bounds = true, DisplayMode = true }, on);
        Assert.Null(off);
        Assert.Equal(new WindowStatePersistence(), new BrowserWindowOptions { WindowStatePersistence = true }.WindowStatePersistence);
    }
}
