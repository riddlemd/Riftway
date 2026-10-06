namespace Oriel.Tests;

public sealed class TrayTests
{
    [Theory]
    [InlineData(0x0201u, TrayEvent.Click)]
    [InlineData(0x0203u, TrayEvent.DoubleClick)]
    [InlineData(0x0207u, TrayEvent.MiddleClick)]
    [InlineData(0x0209u, TrayEvent.MiddleClick)]
    [InlineData(0x0204u, TrayEvent.RightClick)]
    [InlineData(0x007Bu, TrayEvent.RightClick)]
    [InlineData(0x0200u, TrayEvent.MouseMove)]
    [InlineData(0x0402u, TrayEvent.BalloonShow)]
    [InlineData(0x0405u, TrayEvent.BalloonClick)]
    [InlineData(0x0404u, TrayEvent.BalloonClosed)]
    [InlineData(0x0206u, TrayEvent.None)]
    [InlineData(0x0403u, TrayEvent.None)]
    [InlineData(0x0401u, TrayEvent.None)]
    [InlineData(0x0400u, TrayEvent.None)]
    [InlineData(0x0202u, TrayEvent.None)]
    [InlineData(0x0205u, TrayEvent.None)]
    [InlineData(0x0208u, TrayEvent.None)]
    // TrayEvent is internal; a public test method takes it boxed.
    public void EventOf_MapsVersion0Messages_AsElectron(uint message, object e) => Assert.Equal(e, TrayRules.EventOf(message));

    private static readonly Rectangle Icon = new(100, 200, 32, 40);

    [Fact]
    public void ArgsOf_GivesEachEventElectronsArguments()
    {
        var cursor = new Point(110, 210);
        Assert.Equal((Icon, cursor), TrayRules.ArgsOf(TrayEvent.Click, Icon, cursor));
        Assert.Equal((Icon, default(Point)), TrayRules.ArgsOf(TrayEvent.DoubleClick, Icon, cursor));
        Assert.Equal((Icon, default(Point)), TrayRules.ArgsOf(TrayEvent.MiddleClick, Icon, cursor));
        Assert.Equal((Icon, default(Point)), TrayRules.ArgsOf(TrayEvent.RightClick, Icon, cursor));
        Assert.Equal((default(Rectangle), cursor), TrayRules.ArgsOf(TrayEvent.MouseMove, Icon, cursor));
        Assert.Equal((default(Rectangle), new Point(5, 5)), TrayRules.ArgsOf(TrayEvent.MouseEnter, Icon, new Point(5, 5)));
        Assert.Equal((default(Rectangle), new Point(5, 5)), TrayRules.ArgsOf(TrayEvent.MouseLeave, Icon, new Point(5, 5)));
        Assert.Null(TrayRules.ArgsOf(TrayEvent.BalloonShow, Icon, cursor));
    }

    [Fact]
    public void ArgsOf_DropsAMoveOutsideTheBounds() => Assert.Null(TrayRules.ArgsOf(TrayEvent.MouseMove, Icon, new Point(5, 5)));

    [Theory]
    [InlineData(100, 200, true)]
    [InlineData(131, 239, true)]
    [InlineData(99, 210, false)]
    [InlineData(132, 210, false)]
    [InlineData(110, 199, false)]
    [InlineData(110, 240, false)]
    public void Contains_ExcludesTheRightAndBottomEdges(int x, int y, bool inside) => Assert.Equal(inside, TrayRules.Contains(Icon, new Point(x, y)));

    [Fact]
    public void Contains_AnEmptyRectangleHoldsNothing() => Assert.False(TrayRules.Contains(default, default));

    private sealed class FakeIcon(uint id, Rectangle bounds, List<string> log) : ITrayHoverTarget
    {
        public uint Id => id;
        public Rectangle GetBounds() => bounds;
        public void Entered() => log.Add($"enter {id}");
        public void Exited() => log.Add($"leave {id}");
    }

    private sealed class Rig
    {
        public readonly List<string> Log = [];
        public Point Cursor;
        public Action? Tick;
        public readonly TrayHoverDetector Detector;

        public Rig() => Detector = new TrayHoverDetector(() => Cursor, tick => { Tick = tick; Log.Add("start"); }, () => { Tick = null; Log.Add("stop"); });

        public FakeIcon Icon(uint id, Rectangle bounds) => new(id, bounds, Log);
    }

    [Fact]
    public void Hover_AMoveOutsideTheIcon_DoesNothing()
    {
        var rig = new Rig { Cursor = new Point(5, 5) };
        rig.Detector.MouseMove(rig.Icon(2, Icon));
        Assert.Empty(rig.Log);
    }

    [Fact]
    public void Hover_TheFirstMoveInside_Enters_AndLaterMovesDoNot()
    {
        var rig = new Rig { Cursor = new Point(110, 210) };
        var icon = rig.Icon(2, Icon);
        rig.Detector.MouseMove(icon);
        rig.Detector.MouseMove(icon);
        Assert.Equal(["enter 2", "start"], rig.Log);
    }

    [Fact]
    public void Hover_TheTimer_LeavesOnceThePointerIsOff_ThenAMoveEntersAgain()
    {
        var rig = new Rig { Cursor = new Point(110, 210) };
        var icon = rig.Icon(2, Icon);
        rig.Detector.MouseMove(icon);
        rig.Tick!();
        Assert.Equal(["enter 2", "start"], rig.Log);
        rig.Cursor = new Point(5, 5);
        rig.Tick!();
        Assert.Equal(["enter 2", "start", "stop", "leave 2"], rig.Log);
        rig.Detector.IconRemoved(icon);
        Assert.Equal(["enter 2", "start", "stop", "leave 2"], rig.Log);
        rig.Cursor = new Point(110, 210);
        rig.Detector.MouseMove(icon);
        Assert.Equal(["enter 2", "start", "stop", "leave 2", "enter 2", "start"], rig.Log);
    }

    [Fact]
    public void Hover_MovingOntoAnotherIcon_LeavesTheFirst()
    {
        var rig = new Rig { Cursor = new Point(110, 210) };
        rig.Detector.MouseMove(rig.Icon(2, Icon));
        rig.Cursor = new Point(140, 210);
        rig.Detector.MouseMove(rig.Icon(3, new Rectangle(132, 200, 32, 40)));
        Assert.Equal(["enter 2", "start", "stop", "leave 2", "enter 3", "start"], rig.Log);
    }

    [Fact]
    public void Hover_RemovingTheEnteredIcon_LeavesIt_AndOthersAreIgnored()
    {
        var rig = new Rig { Cursor = new Point(110, 210) };
        var icon = rig.Icon(2, Icon);
        rig.Detector.MouseMove(icon);
        rig.Detector.IconRemoved(rig.Icon(3, Icon));
        Assert.Equal(["enter 2", "start"], rig.Log);
        rig.Detector.IconRemoved(icon);
        Assert.Equal(["enter 2", "start", "stop", "leave 2"], rig.Log);
    }

    [Theory]
    [InlineData(BalloonIconType.None, false, false, false, 0x00u)]
    [InlineData(BalloonIconType.Info, true, false, false, 0x21u)]
    [InlineData(BalloonIconType.Warning, false, true, false, 0x12u)]
    [InlineData(BalloonIconType.Error, false, false, true, 0x83u)]
    [InlineData(BalloonIconType.Custom, true, true, true, 0xB4u)]
    public void BalloonFlags_CombineTypeAndOptions(BalloonIconType type, bool large, bool noSound, bool quiet, uint flags)
        => Assert.Equal(flags, TrayRules.BalloonFlags(new DisplayBalloonOptions { IconType = type, LargeIcon = large, NoSound = noSound, RespectQuietTime = quiet }));

    [Fact]
    public void BalloonFlags_AnIconAlone_MeansCustom_AndLargeIsTheDefault()
    {
        var icon = NativeImage.CreateEmpty();
        Assert.Equal(0x24u, TrayRules.BalloonFlags(new DisplayBalloonOptions { Icon = icon }));
        Assert.Equal(0x21u, TrayRules.BalloonFlags(new DisplayBalloonOptions { Icon = icon, IconType = BalloonIconType.Info }));
        Assert.Equal(0x20u, TrayRules.BalloonFlags(new DisplayBalloonOptions()));
    }

    [Fact]
    public void DisplayBalloonOptions_DefaultToElectrons()
    {
        var o = new DisplayBalloonOptions();
        Assert.True(o.LargeIcon);
        Assert.False(o.NoSound);
        Assert.False(o.RespectQuietTime);
        Assert.Equal("", o.Title);
    }
}
