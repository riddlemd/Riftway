using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Oriel.Cef.Platform;

namespace Oriel.Tests;

// Host-side macOS checks that need no window, activation or prompt. Every write uses a key unique to the run and is
// removed again; registered defaults live only in this process's registration domain.
public sealed partial class SystemPreferencesMacHostTests : IClassFixture<SystemPreferencesMacHostTests.DomainFile>
{
    private static readonly string s_prefix = $"OrielTest.SystemPreferences.{Guid.NewGuid():N}.";

    /// <summary>The test host's own defaults domain file: cfprefsd leaves an empty plist once the last key is removed,
    /// deleted after the test host exits when these tests created it.</summary>
    public sealed class DomainFile : IDisposable
    {
        private readonly string _plist = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Preferences", System.Diagnostics.Process.GetCurrentProcess().ProcessName + ".plist");
        private readonly bool _existed;

        public DomainFile() => _existed = File.Exists(_plist);

        public void Dispose()
        {
            if (_existed || !OperatingSystem.IsMacOS()) return;
            // cfprefsd writes the emptied domain lazily, up to some seconds after this process exits, so a shell in a
            // session of its own (the runner kills the host's process group) waits for the empty file and deletes it.
            const string script = """
                while kill -0 "$1" 2>/dev/null; do sleep 0.2; done
                i=0
                while [ $i -lt 120 ]; do
                  if [ -f "$2" ] && [ "$(/usr/bin/plutil -convert json -o - "$2" 2>/dev/null)" = "{}" ]; then rm -f "$2"; exit 0; fi
                  sleep 0.5; i=$((i + 1))
                done
                """;
            using var cleanup = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/perl",
                ["-MPOSIX", "-e", "fork and exit; POSIX::setsid(); open STDIN, '<', '/dev/null'; open STDOUT, '>', '/dev/null'; open STDERR, '>', '/dev/null'; exec @ARGV",
                 "/bin/sh", "-c", script, "sh", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), _plist]))!;
            cleanup.WaitForExit();
        }
    }

    [GeneratedRegex("^[0-9A-F]{8}$")]
    private static partial Regex Rgba();

    [Fact]
    public void AccentAndColours_AreRgbaHex()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var accent = SystemPreferences.GetAccentColor();
        Assert.Matches(Rgba(), accent);
        // controlAccentColor is opaque.
        Assert.EndsWith("FF", accent, StringComparison.Ordinal);
        var window = SystemPreferences.GetColor("window-background");
        Assert.StartsWith("#", window, StringComparison.Ordinal);
        Assert.Matches(Rgba(), window[1..]);
        var blue = SystemPreferences.GetSystemColor("blue");
        Assert.Matches(Rgba(), blue[1..]);
        Assert.NotEqual(blue, SystemPreferences.GetSystemColor("red"));
    }

    [Fact]
    public void UnknownColourNames_ThrowElectronsMessages()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.StartsWith("Unknown color: window", Assert.Throws<ArgumentException>(() => SystemPreferences.GetColor("window")).Message, StringComparison.Ordinal);
        Assert.StartsWith("Unknown system color: grey", Assert.Throws<ArgumentException>(() => SystemPreferences.GetSystemColor("grey")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UserDefaults_RoundTripEachTypeAndRemove()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var keys = new List<string>();
        string Key(string name)
        {
            keys.Add(s_prefix + name);
            return s_prefix + name;
        }
        try
        {
            SystemPreferences.SetUserDefault(Key("s"), UserDefaultType.String, "héllo");
            Assert.Equal("héllo", SystemPreferences.GetUserDefault(s_prefix + "s", UserDefaultType.String));
            SystemPreferences.SetUserDefault(Key("b"), UserDefaultType.Boolean, true);
            Assert.Equal(true, SystemPreferences.GetUserDefault(s_prefix + "b", UserDefaultType.Boolean));
            SystemPreferences.SetUserDefault(Key("i"), UserDefaultType.Integer, -42);
            Assert.Equal(-42, SystemPreferences.GetUserDefault(s_prefix + "i", UserDefaultType.Integer));
            SystemPreferences.SetUserDefault(Key("f"), UserDefaultType.Float, 0.1);
            Assert.Equal((double)0.1f, SystemPreferences.GetUserDefault(s_prefix + "f", UserDefaultType.Float));
            SystemPreferences.SetUserDefault(Key("d"), UserDefaultType.Double, 0.1);
            Assert.Equal(0.1, SystemPreferences.GetUserDefault(s_prefix + "d", UserDefaultType.Double));
            SystemPreferences.SetUserDefault(Key("u"), UserDefaultType.Url, "https://example.com/a?b=c");
            Assert.Equal("https://example.com/a?b=c", SystemPreferences.GetUserDefault(s_prefix + "u", UserDefaultType.Url));

            SystemPreferences.SetUserDefault(Key("a"), UserDefaultType.Array, new object?[] { "x", 3, 2.5, true, new object?[] { 1 } });
            var array = Assert.IsType<JsonArray>(SystemPreferences.GetUserDefault(s_prefix + "a", UserDefaultType.Array));
            Assert.Equal("[\"x\",3,2.5,true,[1]]", array.ToJsonString());
            SystemPreferences.SetUserDefault(Key("o"), UserDefaultType.Dictionary, new Dictionary<string, object?> { ["z"] = 1, ["a"] = "b", ["big"] = 1099511627776.0 });
            var dict = Assert.IsType<JsonObject>(SystemPreferences.GetUserDefault(s_prefix + "o", UserDefaultType.Dictionary));
            // Sorted keys as base::DictValue; the double stays a double.
            Assert.Equal("{\"a\":\"b\",\"big\":1099511627776,\"z\":1}", dict.ToJsonString());
            Assert.Equal(1099511627776.0, dict["big"]!.GetValue<double>());

            // integerForKey: of a 64-bit value reaches .NET as V8's int32 would.
            MacSystemPreferences.SetInteger(Key("wide"), (1L << 32) + 5);
            Assert.Equal(5, SystemPreferences.GetUserDefault(s_prefix + "wide", UserDefaultType.Integer));
            // stringForKey: turns a number into its text.
            Assert.Equal("-42", SystemPreferences.GetUserDefault(s_prefix + "i", UserDefaultType.String));

            foreach (var key in keys) SystemPreferences.RemoveUserDefault(key);
            Assert.Equal("", SystemPreferences.GetUserDefault(s_prefix + "s", UserDefaultType.String));
            Assert.Equal(false, SystemPreferences.GetUserDefault(s_prefix + "b", UserDefaultType.Boolean));
            Assert.Equal(0, SystemPreferences.GetUserDefault(s_prefix + "i", UserDefaultType.Integer));
            Assert.Equal("", SystemPreferences.GetUserDefault(s_prefix + "u", UserDefaultType.Url));
            Assert.Empty(Assert.IsType<JsonArray>(SystemPreferences.GetUserDefault(s_prefix + "a", UserDefaultType.Array)));
            Assert.Empty(Assert.IsType<JsonObject>(SystemPreferences.GetUserDefault(s_prefix + "o", UserDefaultType.Dictionary)));
            Assert.Null(SystemPreferences.GetUserDefault(s_prefix + "s", (UserDefaultType)99));
        }
        finally
        {
            foreach (var key in keys) MacSystemPreferences.Remove(key);
        }
    }

    [Fact]
    public void SetUserDefault_RefusesAMismatchWithoutWriting()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var key = s_prefix + "refused";
        try
        {
            var e = Assert.Throws<ArgumentException>(() => SystemPreferences.SetUserDefault(key, UserDefaultType.Integer, 1.5));
            Assert.StartsWith("Unable to convert value to: integer", e.Message, StringComparison.Ordinal);
            Assert.Throws<ArgumentOutOfRangeException>(() => SystemPreferences.SetUserDefault(key, (UserDefaultType)42, 1));
            Assert.Equal("", SystemPreferences.GetUserDefault(key, UserDefaultType.String));
        }
        finally
        {
            MacSystemPreferences.Remove(key);
        }
    }

    [Fact]
    public void RegisterDefaults_FillsUnsetKeysAndRefusesNull()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var key = s_prefix + "registered";
        SystemPreferences.RegisterDefaults(new Dictionary<string, object?> { [key] = "fallback", [key + ".n"] = 7 });
        Assert.Equal("fallback", SystemPreferences.GetUserDefault(key, UserDefaultType.String));
        Assert.Equal(7, SystemPreferences.GetUserDefault(key + ".n", UserDefaultType.Integer));
        Assert.Throws<ArgumentException>(() => SystemPreferences.RegisterDefaults(new Dictionary<string, object?> { [key + ".null"] = null, [key + ".x"] = "x" }));
        Assert.Equal("", SystemPreferences.GetUserDefault(key + ".x", UserDefaultType.String));
    }

    [Fact]
    public void LocalNotifications_ReachSubscribersUntilUnsubscribed()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var name = s_prefix + "local";
        var got = new List<SubscribedNotification>();
        var id = SystemPreferences.SubscribeLocalNotification(name, got.Add);
        var any = new List<SubscribedNotification>();
        var anyId = SystemPreferences.SubscribeLocalNotification(null, n => { if (n.Event == name) any.Add(n); });
        try
        {
            SystemPreferences.PostLocalNotification(name, new Dictionary<string, object?> { ["n"] = 1, ["s"] = "x", ["l"] = new object?[] { true, 2.5 } });
            var n = Assert.Single(got);
            Assert.Equal(name, n.Event);
            Assert.Equal("", n.Object);
            Assert.Equal("{\"l\":[true,2.5],\"n\":1,\"s\":\"x\"}", n.UserInfo.ToJsonString());
            Assert.Single(any);

            SystemPreferences.UnsubscribeLocalNotification(id);
            SystemPreferences.PostLocalNotification(name, new Dictionary<string, object?>());
            Assert.Single(got);
            Assert.Equal(2, any.Count);
            Assert.Empty(any[1].UserInfo);
        }
        finally { SystemPreferences.UnsubscribeLocalNotification(anyId); }
        Assert.NotEqual(id, anyId);
    }

    [Fact]
    public void Unsubscribe_FromAnotherCentreForgetsTheIdButKeepsTheCallback()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var name = s_prefix + "quirk";
        var count = 0;
        var id = SystemPreferences.SubscribeLocalNotification(name, _ => count++);
        SystemPreferences.UnsubscribeNotification(id);
        SystemPreferences.PostLocalNotification(name, new Dictionary<string, object?>());
        Assert.Equal(1, count);
        // The id is gone, so the right centre cannot remove it any more, as in Electron.
        SystemPreferences.UnsubscribeLocalNotification(id);
        SystemPreferences.PostLocalNotification(name, new Dictionary<string, object?>());
        Assert.Equal(2, count);
    }

    [Fact]
    public void WorkspaceNotifications_RoundTrip()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var name = s_prefix + "workspace";
        var got = new List<SubscribedNotification>();
        var id = SystemPreferences.SubscribeWorkspaceNotification(name, got.Add);
        try
        {
            SystemPreferences.PostWorkspaceNotification(name, new Dictionary<string, object?> { ["k"] = "v" });
            Assert.Equal("{\"k\":\"v\"}", Assert.Single(got).UserInfo.ToJsonString());
            // A local post never reaches the workspace centre.
            SystemPreferences.PostLocalNotification(name, new Dictionary<string, object?>());
            Assert.Single(got);
        }
        finally { SystemPreferences.UnsubscribeWorkspaceNotification(id); }
    }

    [Fact]
    public void IsTrustedAccessibilityClient_WithoutPromptMatchesAXIsProcessTrusted()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(MacHotKeys.IsTrustedAccessibilityClient(), SystemPreferences.IsTrustedAccessibilityClient(false));
    }

    [Fact]
    public void AnimationSettings_ScrollFollowsNSScrollAnimationEnabled()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var settings = SystemPreferences.GetAnimationSettings();
        Assert.Equal(!settings.PrefersReducedMotion, settings.ShouldRenderRichAnimation);
        Assert.Equal(SystemPreferences.GetUserDefault("NSScrollAnimationEnabled", UserDefaultType.Boolean), settings.ScrollAnimationsEnabledBySystem);
        // The app domain overrides the global one, so both values can be forced and then removed again.
        var global = Assert.IsType<bool>(SystemPreferences.GetUserDefault("NSScrollAnimationEnabled", UserDefaultType.Boolean));
        try
        {
            SystemPreferences.SetUserDefault("NSScrollAnimationEnabled", UserDefaultType.Boolean, !global);
            Assert.Equal(!global, SystemPreferences.GetAnimationSettings().ScrollAnimationsEnabledBySystem);
        }
        finally { SystemPreferences.RemoveUserDefault("NSScrollAnimationEnabled"); }
        Assert.Equal(global, SystemPreferences.GetAnimationSettings().ScrollAnimationsEnabledBySystem);
    }

    [Fact]
    public void MediaAccessStatus_ScreenIsGrantedOrDeniedAndInvalidThrows()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Contains(SystemPreferences.GetMediaAccessStatus(MediaAccessType.Screen), new[] { MediaAccessStatus.Granted, MediaAccessStatus.Denied });
        Assert.Throws<ArgumentOutOfRangeException>(() => SystemPreferences.GetMediaAccessStatus((MediaAccessType)7));
        Assert.IsType<ArgumentOutOfRangeException>(SystemPreferences.AskForMediaAccessAsync(MediaAccessType.Screen).Exception?.InnerException);
        Assert.IsType<ArgumentException>(SystemPreferences.PromptTouchIDAsync("").Exception?.InnerException);
    }
}
