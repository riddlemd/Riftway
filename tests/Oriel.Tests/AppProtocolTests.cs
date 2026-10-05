#pragma warning disable CA1416 // The Windows-only helpers are exercised through a fake on every OS and gated per test otherwise.

using Microsoft.Win32;
using Oriel.Cef.Platform;

namespace Oriel.Tests;

internal sealed class FakeAppRegistry : IAppRegistryBackend
{
    public Dictionary<string, string> Protocols { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Run { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, byte[]> Approved { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Calls { get; } = [];
    public string? FriendlyName { get; set; }
    public string? LastScheme { get; private set; }
    public Exception? Failure { get; set; }

    private void Fail()
    {
        if (Failure is not null)
        {
            throw Failure;
        }
    }

    public string? GetProtocolCommand(string protocol) => Protocols.GetValueOrDefault(protocol);

    public void WriteProtocol(string protocol, string command)
    {
        Fail();
        Calls.Add("write:" + protocol);
        Protocols[protocol] = command;
    }

    public void DeleteProtocolTree(string protocol)
    {
        Fail();
        Calls.Add("delete:" + protocol);
        Protocols.Remove(protocol);
    }

    public string? GetFriendlyAppName(string scheme)
    {
        LastScheme = scheme;
        return FriendlyName;
    }

    public IReadOnlyDictionary<string, string> GetRunEntries() => Run;

    public void SetRunEntry(string name, string command)
    {
        Fail();
        Run[name] = command;
    }

    public void DeleteRunEntry(string name)
    {
        Fail();
        Run.Remove(name);
    }

    public byte[]? GetStartupApproved(string name) => Approved.GetValueOrDefault(name);

    public void SetStartupApproved(string name, byte[] data) => Approved[name] = data;

    public void DeleteStartupApproved(string name) => Approved.Remove(name);
}

public sealed class AppProtocolTests
{
    private const string Exe = @"C:\oriel-test\app.exe";

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("tail\\", "tail\\")]
    [InlineData("a b\\", "\"a b\\\\\"")]
    public void QuoteArg_QuotesWhatTheShellWouldSplit(string arg, string expected)
        => Assert.Equal(expected, WindowsAppRegistry.QuoteArg(arg));

    [Theory]
    [InlineData("plain")]
    [InlineData("two words")]
    [InlineData("")]
    [InlineData("a\"b")]
    [InlineData("a b\\")]
    [InlineData("x\\\\\"y z")]
    public void ParseCommand_ReadsBackWhatBuildCommandWrote(string arg)
    {
        var (path, args) = WindowsAppRegistry.ParseCommand(WindowsAppRegistry.BuildCommand(Exe, [arg, "last"]));

        Assert.Equal(Exe, path);
        Assert.Equal([arg, "last"], args);
    }

    [Fact]
    public void ParseCommand_AcceptsAnUnquotedPath()
    {
        var (path, args) = WindowsAppRegistry.ParseCommand(@"C:\x\a.exe --flag");

        Assert.Equal(@"C:\x\a.exe", path);
        Assert.Equal(["--flag"], args);
    }

    [Fact]
    public void BuildProtocolCommand_QuotesThePathAndEndsWithTheUrlPlaceholder()
        => Assert.Equal("\"C:\\oriel-test\\app.exe\" --x \"a b\" \"%1\"",
            WindowsAppRegistry.BuildProtocolCommand(Exe, ["--x", "a b"]));

    [Theory]
    [InlineData("myapp://open/x", "myapp")]
    [InlineData("myapp:", "myapp")]
    [InlineData("myapp", "myapp")]
    [InlineData("", "")]
    [InlineData(":x", "")]
    public void SchemeOf_IsThePartBeforeTheFirstColon(string url, string scheme)
        => Assert.Equal(scheme, WindowsAppRegistry.SchemeOf(url));

    [Theory]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("a\\b", false)]
    [InlineData("a/b", false)]
    [InlineData("myapp", true)]
    public void IsValidProtocol_RejectsEmptyAndPathLikeNames(string protocol, bool valid)
        => Assert.Equal(valid, WindowsAppRegistry.IsValidProtocol(protocol));

    [Fact]
    public void Set_WritesTheProtocolWithOurCommand()
    {
        FakeAppRegistry fake = new();

        Assert.True(WindowsAppRegistry.SetAsDefaultProtocolClient(fake, "myapp", Exe, ["--x"]));

        Assert.Equal("\"C:\\oriel-test\\app.exe\" --x \"%1\"", fake.Protocols["myapp"]);
    }

    [Fact]
    public void Set_DefaultsThePathToTheRunningProcess()
    {
        FakeAppRegistry fake = new();

        Assert.True(WindowsAppRegistry.SetAsDefaultProtocolClient(fake, "myapp", null, null));

        Assert.StartsWith("\"" + Environment.ProcessPath + "\" ", fake.Protocols["myapp"]);
    }

    [Fact]
    public void Set_EmptyProtocol_FailsWithoutWriting()
    {
        FakeAppRegistry fake = new();

        Assert.False(WindowsAppRegistry.SetAsDefaultProtocolClient(fake, "", Exe, null));

        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void Set_WhenTheRegistryRefuses_ReturnsFalse()
    {
        FakeAppRegistry fake = new() { Failure = new UnauthorizedAccessException() };

        Assert.False(WindowsAppRegistry.SetAsDefaultProtocolClient(fake, "myapp", Exe, null));
    }

    [Fact]
    public void IsDefault_MatchesOurCommandIgnoringCase()
    {
        FakeAppRegistry fake = new();
        fake.Protocols["myapp"] = "\"C:\\ORIEL-TEST\\APP.EXE\" --x \"%1\"";

        Assert.True(WindowsAppRegistry.IsDefaultProtocolClient(fake, "myapp", Exe, ["--x"]));
    }

    [Fact]
    public void IsDefault_FalseForOtherArgsOtherPathOrNothingRegistered()
    {
        FakeAppRegistry fake = new();
        Assert.False(WindowsAppRegistry.IsDefaultProtocolClient(fake, "myapp", Exe, null));

        fake.Protocols["myapp"] = WindowsAppRegistry.BuildProtocolCommand(Exe, ["--x"]);

        Assert.False(WindowsAppRegistry.IsDefaultProtocolClient(fake, "myapp", Exe, ["--y"]));
        Assert.False(WindowsAppRegistry.IsDefaultProtocolClient(fake, "myapp", @"C:\other.exe", ["--x"]));
        Assert.True(WindowsAppRegistry.IsDefaultProtocolClient(fake, "myapp", Exe, ["--x"]));
    }

    [Fact]
    public void Remove_DeletesTheProtocolWhenItIsOurs()
    {
        FakeAppRegistry fake = new();
        WindowsAppRegistry.SetAsDefaultProtocolClient(fake, "myapp", Exe, null);

        Assert.True(WindowsAppRegistry.RemoveAsDefaultProtocolClient(fake, "myapp", Exe, null));

        Assert.False(fake.Protocols.ContainsKey("myapp"));
    }

    [Fact]
    public void Remove_LeavesAnotherAppsProtocolAlone()
    {
        FakeAppRegistry fake = new();
        fake.Protocols["myapp"] = "\"C:\\other.exe\" \"%1\"";

        Assert.False(WindowsAppRegistry.RemoveAsDefaultProtocolClient(fake, "myapp", Exe, null));

        Assert.True(fake.Protocols.ContainsKey("myapp"));
    }

    [Fact]
    public void Remove_WhenTheDeleteFails_ReturnsFalse()
    {
        FakeAppRegistry fake = new();
        WindowsAppRegistry.SetAsDefaultProtocolClient(fake, "myapp", Exe, null);
        fake.Failure = new IOException();

        Assert.False(WindowsAppRegistry.RemoveAsDefaultProtocolClient(fake, "myapp", Exe, null));
    }

    [Fact]
    public void GetApplicationName_QueriesTheSchemeAndReturnsTheName()
    {
        FakeAppRegistry fake = new() { FriendlyName = "My App" };

        Assert.Equal("My App", WindowsAppRegistry.GetApplicationNameForProtocol(fake, "myapp://x"));
        Assert.Equal("myapp", fake.LastScheme);
    }

    [Fact]
    public void GetApplicationName_EmptyWhenNoAppOrNoScheme()
    {
        FakeAppRegistry fake = new() { FriendlyName = "My App" };

        Assert.Equal("", WindowsAppRegistry.GetApplicationNameForProtocol(fake, ""));
        Assert.Null(fake.LastScheme);

        fake.FriendlyName = null;
        Assert.Equal("", WindowsAppRegistry.GetApplicationNameForProtocol(fake, "myapp://x"));
    }

    [WindowsOnlyFact]
    public void RealRegistry_SetWritesTheClassKeyAndRemoveDeletesIt()
    {
        string protocol = "oriel-test-" + Guid.NewGuid().ToString("N");
        string keyPath = @"Software\Classes\" + protocol;
        try
        {
            Assert.True(OrielApp.SetProtocolClient(protocol, Exe, ["--x"]));

            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath)!)
            {
                Assert.Equal("URL:" + protocol, key.GetValue(""));
                Assert.Equal("", key.GetValue("URL Protocol"));
                using RegistryKey cmd = key.OpenSubKey(@"shell\open\command")!;
                Assert.Equal("\"C:\\oriel-test\\app.exe\" --x \"%1\"", cmd.GetValue(""));
            }

            Assert.True(OrielApp.IsProtocolClient(protocol, Exe, ["--x"]));
            Assert.False(OrielApp.IsProtocolClient(protocol, Exe, null));
            Assert.False(OrielApp.RemoveProtocolClient(protocol, @"C:\other.exe", null));
            Assert.NotNull(Registry.CurrentUser.OpenSubKey(keyPath));

            Assert.True(OrielApp.RemoveProtocolClient(protocol, Exe, ["--x"]));
            Assert.Null(Registry.CurrentUser.OpenSubKey(keyPath));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [WindowsOnlyFact]
    public void RealRegistry_AnUnregisteredSchemeHasNoApplicationName()
        => Assert.Equal("", OrielApp.ProtocolNameFor("oriel-test-" + Guid.NewGuid().ToString("N") + "://x"));

    [WindowsOnlyFact]
    public void RealRegistry_AssociationQueryFindsTheAppBehindAProtocol()
    {
        string protocol = "oriel-test-" + Guid.NewGuid().ToString("N");
        string keyPath = @"Software\Classes\" + protocol;
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                key.SetValue("", "URL:" + protocol);
                key.SetValue("URL Protocol", "");
                using RegistryKey cmd = key.CreateSubKey(@"shell\open\command");
                cmd.SetValue("", "\"" + Path.Combine(Environment.SystemDirectory, "notepad.exe") + "\" \"%1\"");
            }

            Assert.NotEmpty(OrielApp.ProtocolNameFor(protocol + "://x"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }
}
