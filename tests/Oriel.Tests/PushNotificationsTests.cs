using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Oriel.Cef.Platform;

namespace Oriel.Tests;

[Collection(nameof(PushNotificationsCollection))]
public sealed class PushNotificationsTests : IDisposable
{
    private readonly Action _savedRegister = PushNotifications.Register;
    private int _registrations;

    public PushNotificationsTests() => PushNotifications.Register = () => _registrations++;

    public void Dispose()
    {
        PushNotifications.Register = _savedRegister;
        PushNotifications.Reject("test over");
    }

    [Fact]
    public void TheTokenIsLowercaseHex() => Assert.Equal("00ab7f", PushNotifications.TokenString([0x00, 0xAB, 0x7F]));

    [Fact]
    public async Task EveryWaitingRegistrationGetsTheToken()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var first = PushNotifications.RegisterForApnsNotificationsAsync();
        var second = PushNotifications.RegisterForApnsNotificationsAsync();
        Assert.Equal(2, _registrations);
        Assert.False(first.IsCompleted);
        PushNotifications.Resolve("abc");
        Assert.Equal("abc", await first);
        Assert.Equal("abc", await second);
        var third = PushNotifications.RegisterForApnsNotificationsAsync();
        PushNotifications.Resolve("later");
        Assert.Equal("later", await third);
    }

    [Fact]
    public async Task AFailureRejectsEveryWaitingRegistrationOnce()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var first = PushNotifications.RegisterForApnsNotificationsAsync();
        PushNotifications.Reject("3000 NSCocoaErrorDomain {}");
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        Assert.Equal("3000 NSCocoaErrorDomain {}", e.Message);
        var next = PushNotifications.RegisterForApnsNotificationsAsync();
        PushNotifications.Resolve("ok");
        Assert.Equal("ok", await next);
    }

    [Fact]
    public void ANotificationIsRaisedWithItsUserInfo()
    {
        JsonObject? seen = null;
        EventHandler<JsonObject> handler = (_, info) => seen = info;
        PushNotifications.ReceivedApnsNotification += handler;
        try
        {
            var info = new JsonObject { ["aps"] = new JsonObject { ["alert"] = "hi" } };
            PushNotifications.OnReceived(info);
            Assert.Same(info, seen);
        }
        finally { PushNotifications.ReceivedApnsNotification -= handler; }
    }

    [Fact]
    public void ElsewhereItIsNotSupported()
    {
        if (OperatingSystem.IsMacOS()) return;
        Assert.Throws<PlatformNotSupportedException>(() => { _ = PushNotifications.RegisterForApnsNotificationsAsync(); });
        Assert.Throws<PlatformNotSupportedException>(PushNotifications.UnregisterForApnsNotifications);
    }

    [Fact]
    public unsafe void TheErrorTextIsCodeDomainAndUserInfo()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var send = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libobjc.A.dylib"), "objc_msgSend");
        nint Cls(string n) { var b = System.Text.Encoding.UTF8.GetBytes(n + "\0"); fixed (byte* p = b) return objc_getClass(p); }
        nint Sel(string n) { var b = System.Text.Encoding.UTF8.GetBytes(n + "\0"); fixed (byte* p = b) return sel_registerName(p); }
        nint Str(string s) { fixed (char* p = s) return ((delegate* unmanaged<nint, nint, char*, nuint, nint>)send)(Cls("NSString"), Sel("stringWithCharacters:length:"), p, (nuint)s.Length); }
        var info = ((delegate* unmanaged<nint, nint, nint, nint, nint>)send)(Cls("NSDictionary"), Sel("dictionaryWithObject:forKey:"), Str("v"), Str("k"));
        var error = ((delegate* unmanaged<nint, nint, nint, nint, nint, nint>)send)(Cls("NSError"), Sel("errorWithDomain:code:userInfo:"), Str("NSCocoaErrorDomain"), 3000, info);
        var text = MacApp.RemoteNotificationError(error);
        Assert.StartsWith("3000 NSCocoaErrorDomain {", text);
        Assert.Contains("k = v;", text);
    }

    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern unsafe nint objc_getClass(byte* name);
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern unsafe nint sel_registerName(byte* name);
}

[CollectionDefinition(nameof(PushNotificationsCollection), DisableParallelization = true)]
public sealed class PushNotificationsCollection;
