using System.Runtime.Versioning;
using Riftway.Cef.Platform;

namespace Riftway.Tests;

// Electron v44.5.1's LibnotifyNotification, and the D-Bus calls libnotify 0.8 makes for it.
public sealed class LinuxNotificationRulesTests
{
    [Theory]
    [InlineData("critical", 2)]
    [InlineData("low", 0)]
    [InlineData("normal", 1)]
    [InlineData("", 1)]
    [InlineData("Critical", 1)]
    public void Urgency_MapsToNotifyUrgency(string urgency, byte expected)
        => Assert.Equal(expected, LinuxNotificationRules.Urgency(urgency));

    [Theory]
    [InlineData("never", 0)]
    [InlineData("default", -1)]
    [InlineData("", -1)]
    public void ExpireTimeout_IsNeverOrTheServerDefault(string timeoutType, int expected)
        => Assert.Equal(expected, LinuxNotificationRules.ExpireTimeout(timeoutType));

    [Fact]
    public void SupportsActions_NeedsTheActionsCapability()
    {
        Assert.True(LinuxNotificationRules.SupportsActions(_ => null, () => new HashSet<string> { "body", "actions" }));
        Assert.False(LinuxNotificationRules.SupportsActions(_ => null, () => new HashSet<string> { "body" }));
    }

    [Fact]
    public void SupportsActions_UbuntuNotifierSkipsTheCapabilities()
    {
        var asked = false;
        Assert.False(LinuxNotificationRules.SupportsActions(name => name == "ELECTRON_USE_UBUNTU_NOTIFIER" ? "" : null, () =>
        {
            asked = true;
            return new HashSet<string> { "actions" };
        }));
        Assert.False(asked);
    }

    [Fact]
    public void Actions_AreTheDefaultActionAlone()
    {
        Assert.Equal(["default", "Show"], LinuxNotificationRules.Actions(true));
        Assert.Empty(LinuxNotificationRules.Actions(false));
    }

    [Theory]
    [InlineData("default", true, true)]
    [InlineData("default", false, false)]
    [InlineData("Default", true, false)]
    [InlineData("0", true, false)]
    public void IsClick_OnlyForTheRegisteredDefault(string action, bool registered, bool expected)
        => Assert.Equal(expected, LinuxNotificationRules.IsClick(action, registered));

    [Theory]
    [InlineData(1, 2, "image-data")]
    [InlineData(1, 3, "image-data")]
    [InlineData(2, 0, "image-data")]
    [InlineData(1, 1, "image_data")]
    [InlineData(1, 0, "icon_data")]
    [InlineData(0, 9, "icon_data")]
    public void ImageHintName_FollowsTheSpecVersion(int major, int minor, string expected)
        => Assert.Equal(expected, LinuxNotificationRules.ImageHintName(major, minor));

    [Theory]
    [InlineData("1.2", 1, 2)]
    [InlineData(" 2.0", 2, 0)]
    [InlineData("1", 1, 7)]
    [InlineData("1.x", 1, 7)]
    [InlineData("x.3", 5, 7)]
    [InlineData("", 5, 7)]
    public void ParseSpecVersion_IsSscanf(string spec, int major, int minor)
        => Assert.Equal((major, minor), LinuxNotificationRules.ParseSpecVersion(spec, 5, 7));

    [Fact]
    public void ImageData_IsAnRgbaPixbuf()
    {
        // Premultiplied BGRA (10, 64, 100) at alpha 128 unpremultiplies to RGBA (199, 128, 20, 128).
        var value = LinuxNotificationRules.ImageData(new ImageBitmap(2, 1, [10, 64, 100, 128, 0, 0, 255, 255]));
        Assert.Equal("(iiibiiay)", value.Signature);
        var fields = (object?[])value.Value!;
        Assert.Equal(new object?[] { 2, 1, 8, true, 8, 4 }, fields[..6]);
        Assert.Equal(new byte[] { 199, 128, 20, 128, 255, 0, 0, 255 }, fields[6]);
    }

    [Fact]
    public void IconBitmap_IsTheRepresentationClosestTo1x()
    {
        Assert.Null(LinuxNotificationRules.IconBitmap(null));
        Assert.Null(LinuxNotificationRules.IconBitmap(NativeImage.CreateEmpty()));
        var image = NativeImage.CreateFromBitmap(new byte[2 * 2 * 4], new NativeImageBitmapOptions { Width = 2, Height = 2, ScaleFactor = 2 });
        Assert.Equal(2, LinuxNotificationRules.IconBitmap(image)!.Value.Width);
        image.AddRepresentation(new AddRepresentationOptions { ScaleFactor = 1, Width = 1, Height = 1, Buffer = new byte[4] });
        Assert.Equal(1, LinuxNotificationRules.IconBitmap(image)!.Value.Width);
    }

    [Theory]
    [InlineData("app.desktop", "app")]
    [InlineData("app", "app")]
    [InlineData("a.desktop.desktop", "a.desktop")]
    [InlineData(null, null)]
    public void XdgAppId_DropsTheDesktopSuffix(string? name, string? expected)
        => Assert.Equal(expected, LinuxNotificationRules.XdgAppId(name));

    private static string Format(List<(string Key, LinuxDBusVariant Value)> hints) => string.Join(" ", hints.Select(h => $"{h.Key}={h.Value}"));

    [Fact]
    public void Hints_InElectronsOrder()
    {
        var image = ("image-data", new LinuxDBusVariant("s", "img"));
        Assert.Equal("urgency=<y>2 image-data=<s>'img' append=<s>'true' desktop-entry=<s>'app' sender-pid=<x>42",
            Format(LinuxNotificationRules.Hints(2, image, new HashSet<string> { "append", "x-canonical-append" }, "app", 42)));
    }

    [Fact]
    public void Hints_CanonicalAppendOnlyWithoutAppend()
    {
        Assert.Equal("urgency=<y>1 x-canonical-append=<s>'true' sender-pid=<x>7",
            Format(LinuxNotificationRules.Hints(1, null, new HashSet<string> { "x-canonical-append" }, null, 7)));
        Assert.Equal("urgency=<y>0 sender-pid=<x>7", Format(LinuxNotificationRules.Hints(0, null, new HashSet<string>(), null, 7)));
    }

    [Fact]
    public void NotifyArgs_AreANewNotificationWithoutAnAppIcon()
    {
        var hints = new List<(string, LinuxDBusVariant)>();
        var args = LinuxNotificationRules.NotifyArgs("App", "Title", "Body", ["default", "Show"], hints, 0);
        Assert.Equal(new object?[] { "App", 0u, "", "Title", "Body" }, args[..5]);
        Assert.Equal(["default", "Show"], (string[])args[5]!);
        Assert.Same(hints, args[6]);
        Assert.Equal(0, args[7]);
        Assert.Equal(8, LinuxDBusValues.Split(LinuxNotificationRules.NotifySignature).Count);
    }

    [Theory]
    [InlineData(1u, NotificationCloseReason.TimedOut)]
    [InlineData(2u, NotificationCloseReason.UserCanceled)]
    [InlineData(3u, NotificationCloseReason.ApplicationHidden)]
    [InlineData(4u, NotificationCloseReason.UserCanceled)]
    public void CloseReason_MapsTheSpecsReasons(uint reason, NotificationCloseReason expected)
        => Assert.Equal(expected, LinuxNotificationRules.CloseReason(reason));
}

// The backend through Notification, with the service's answers faked: the calls libnotify would make, in its order.
[SupportedOSPlatform("linux")]
[Collection("Notification")]
public sealed class LinuxNotificationBackendTests : IDisposable
{
    private readonly INotificationBackend _saved = Notification.Backend;
    private readonly Func<bool> _savedReady = Notification.IsAppReady;
    private readonly LinuxNotificationBackend _backend;
    private readonly List<(string Method, object?[] Args)> _calls = [];
    private readonly List<string> _events = [];
    private readonly List<string> _tokens = [];
    private readonly Dictionary<string, string?> _environment = [];
    private uint _nextId = 40;

    private object?[]? _serverInformation = ["srv", "vendor", "1", "1.2"];
    private object?[]? _capabilities = [new List<object?> { "actions", "append", "body" }];
    private bool _refuseNotify;

    public LinuxNotificationBackendTests()
    {
        _backend = new LinuxNotificationBackend(Call, name => _environment.GetValueOrDefault(name), () => "app.desktop", _tokens.Add, () => "App");
        Notification.Backend = _backend;
        Notification.IsAppReady = () => true;
    }

    public void Dispose()
    {
        Notification.Backend = _saved;
        Notification.IsAppReady = _savedReady;
    }

    private object?[]? Call(string method, string signature, object?[] args)
    {
        _calls.Add((method, args));
        return method switch
        {
            "GetServerInformation" => _serverInformation,
            "GetCapabilities" => _capabilities,
            "Notify" => _refuseNotify ? null : [++_nextId],
            "CloseNotification" => [],
            _ => null,
        };
    }

    private string Methods() => string.Join(" ", _calls.Select(c => c.Method));

    private object?[] NotifyArgs() => _calls.Last(c => c.Method == "Notify").Args;

    private string Hints() => string.Join(" ", ((List<(string Key, LinuxDBusVariant Value)>)NotifyArgs()[6]!)
        .Where(h => h.Key != "sender-pid").Select(h => $"{h.Key}={h.Value}"));

    private Notification Watched(Notification? n = null)
    {
        n ??= new Notification { Title = "T", Body = "B" };
        n.Shown += (_, _) => _events.Add("shown");
        n.Click += (_, _) => _events.Add("click");
        n.Action += (_, e) => _events.Add($"action {e.ActionIndex}");
        n.Reply += (_, r) => _events.Add($"reply {r}");
        n.Closed += (_, r) => _events.Add($"closed {r}");
        n.Failed += (_, e) => _events.Add($"failed '{e}'");
        return n;
    }

    [Fact]
    public void Show_CallsTheServiceInLibnotifysOrder()
    {
        var n = Watched(new Notification { Title = "T", Body = "B", Urgency = "critical", TimeoutType = "never", HasReply = true, Actions = [new NotificationAction { Text = "Go" }] });
        n.Show();
        Assert.Equal("GetServerInformation GetCapabilities Notify", Methods());
        var args = NotifyArgs();
        Assert.Equal(new object?[] { "App", 0u, "", "T", "B" }, args[..5]);
        Assert.Equal(["default", "Show"], (string[])args[5]!);
        Assert.Equal("urgency=<y>2 append=<s>'true' desktop-entry=<s>'app'", Hints());
        Assert.Contains(((List<(string Key, LinuxDBusVariant Value)>)args[6]!), h => h.Key == "sender-pid" && h.Value.Value is long pid && pid == Environment.ProcessId);
        Assert.Equal(0, args[7]);
        Assert.True(n.IsShown);
        Assert.Equal(["shown"], _events);
    }

    [Fact]
    public void Show_KeepsTheProxyAndCapabilities()
    {
        Watched().Show();
        Watched().Show();
        Assert.Equal("GetServerInformation GetCapabilities Notify Notify", Methods());
        Assert.Equal(-1, NotifyArgs()[7]);
    }

    [Fact]
    public void Show_AsksAgainForEmptyCapabilities()
    {
        _capabilities = [new List<object?>()];
        Watched().Show();
        Watched().Show();
        Assert.Equal("GetServerInformation GetCapabilities GetCapabilities Notify GetCapabilities GetCapabilities Notify", Methods());
        Assert.Empty((string[])NotifyArgs()[5]!);
        Assert.Equal("urgency=<y>1 desktop-entry=<s>'app'", Hints());
    }

    [Fact]
    public void Show_UbuntuNotifierSendsNoActions()
    {
        _environment["ELECTRON_USE_UBUNTU_NOTIFIER"] = "1";
        Watched().Show();
        Assert.Empty((string[])NotifyArgs()[5]!);
        Assert.Equal("urgency=<y>1 append=<s>'true' desktop-entry=<s>'app'", Hints());
    }

    [Fact]
    public void Show_ImageHintNameIsFixedBeforeTheProxyWhenActionsAreNotAsked()
    {
        // libnotify names the image hint from a spec version it only learns with the proxy.
        _environment["ELECTRON_USE_UBUNTU_NOTIFIER"] = "1";
        var icon = NativeImage.CreateFromBitmap(new byte[4], new NativeImageBitmapOptions { Width = 1, Height = 1 });
        Watched(new Notification { IconImage = icon }).Show();
        Assert.StartsWith("urgency=<y>1 icon_data=<(iiibiiay)>[1, 1, 4, true, 8, 4, [4 bytes]]", Hints());
        Watched(new Notification { IconImage = icon }).Show();
        Assert.StartsWith("urgency=<y>1 image-data=", Hints());
    }

    [Theory]
    [InlineData("1.1", "image_data")]
    [InlineData("1.2", "image-data")]
    public void Show_ImageHintFollowsTheServersSpecVersion(string spec, string hint)
    {
        _serverInformation = ["srv", "vendor", "1", spec];
        var icon = NativeImage.CreateFromBitmap(new byte[4], new NativeImageBitmapOptions { Width = 1, Height = 1 });
        Watched(new Notification { IconImage = icon }).Show();
        Assert.StartsWith($"urgency=<y>1 {hint}=", Hints());
    }

    [Fact]
    public void Show_EmptyIconSendsNoImage()
    {
        Watched(new Notification { IconImage = NativeImage.CreateEmpty() }).Show();
        Assert.Equal("urgency=<y>1 append=<s>'true' desktop-entry=<s>'app'", Hints());
    }

    [Fact]
    public void Show_WithoutAServerFailsInsideShow()
    {
        _serverInformation = null;
        var n = Watched();
        n.Show();
        Assert.Equal("GetServerInformation GetServerInformation GetServerInformation", Methods());
        Assert.False(n.IsShown);
        Assert.Equal(["failed ''"], _events);
    }

    [Fact]
    public void Show_RefusedNotifyFails()
    {
        _refuseNotify = true;
        var n = Watched();
        n.Show();
        Assert.False(n.IsShown);
        Assert.Equal(["failed ''"], _events);
    }

    [Fact]
    public void DefaultAction_IsTheClickAndEndsIt()
    {
        var n = Watched();
        n.Show();
        _backend.OnSignal("ActionInvoked", [_nextId + 1, "default"]);
        _backend.OnSignal("ActionInvoked", [_nextId, "Show"]);
        _backend.OnSignal("ActionInvoked", [_nextId, "0"]);
        _backend.OnSignal("NotificationReplied", [_nextId, "hi"]);
        Assert.Equal(["shown"], _events);
        _backend.OnSignal("ActionInvoked", [_nextId, "default"]);
        Assert.Equal(["shown", "click"], _events);
        Assert.False(n.IsShown);
        _backend.OnSignal("ActionInvoked", [_nextId, "default"]);
        _backend.OnSignal("NotificationClosed", [_nextId, 2u]);
        Assert.Equal(["shown", "click"], _events);
    }

    [Fact]
    public void DefaultAction_WithoutTheCapabilityIsNotAClick()
    {
        _capabilities = [new List<object?> { "body" }];
        Watched().Show();
        _backend.OnSignal("ActionInvoked", [_nextId, "default"]);
        Assert.Equal(["shown"], _events);
    }

    [Fact]
    public void ActivationToken_GoesToTheClickThatFollows()
    {
        Watched().Show();
        _backend.OnSignal("ActivationToken", [_nextId, "tok"]);
        _backend.OnSignal("ActivationToken", [_nextId + 1, "other"]);
        Assert.Empty(_tokens);
        _backend.OnSignal("ActionInvoked", [_nextId, "default"]);
        Assert.Equal(["tok"], _tokens);
    }

    [Fact]
    public void NotificationClosed_RaisesClosedOnceAndEndsIt()
    {
        var n = Watched();
        n.Show();
        var id = _nextId;
        _backend.OnSignal("NotificationClosed", [id, LinuxNotificationRules.ReasonUnset]);
        _backend.OnSignal("NotificationClosed", [id + 1, 1u]);
        Assert.True(n.IsShown);
        _backend.OnSignal("NotificationClosed", [id, 1u]);
        _backend.OnSignal("NotificationClosed", [id, 2u]);
        _backend.OnSignal("NotificationClosed", [0u, 2u]);
        Assert.False(n.IsShown);
        Assert.Equal(["shown", "closed TimedOut"], _events);
        n.Close();
        Assert.DoesNotContain(_calls, c => c.Method == "CloseNotification");
    }

    [Fact]
    public void Close_CallsCloseNotificationAndRaisesNothing()
    {
        var n = Watched();
        n.Show();
        var id = _nextId;
        n.Close();
        Assert.Equal((object?[])[id], _calls.Single(c => c.Method == "CloseNotification").Args);
        _backend.OnSignal("NotificationClosed", [id, 3u]);
        Assert.False(n.IsShown);
        Assert.Equal(["shown"], _events);
    }

    [Fact]
    public void ShowAgain_ClosesTheFirstAndSendsANewOne()
    {
        var n = Watched();
        n.Show();
        var first = _nextId;
        n.Show();
        Assert.Equal("GetServerInformation GetCapabilities Notify CloseNotification Notify", Methods());
        Assert.Equal((object?[])[first], _calls.Single(c => c.Method == "CloseNotification").Args);
        Assert.Equal(0u, NotifyArgs()[1]);
        _backend.OnSignal("NotificationClosed", [first, 3u]);
        Assert.True(n.IsShown);
        Assert.Equal(["shown", "shown"], _events);
    }

    [Fact]
    public void AppName_IsFixedByTheFirstPrepare()
    {
        var names = new Queue<string>(["First", "Second"]);
        var backend = new LinuxNotificationBackend(Call, _ => null, () => null, _ => { }, names.Dequeue);
        Notification.Backend = backend;
        backend.Prepare();
        Watched().Show();
        Assert.Equal("First", NotifyArgs()[0]);
        Assert.DoesNotContain("desktop-entry", Hints());
    }
}
