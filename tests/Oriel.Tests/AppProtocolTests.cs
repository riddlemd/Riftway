#pragma warning disable CA1416 // The Windows-only helpers are exercised through a fake on every OS and gated per test otherwise.

using Microsoft.Win32;
using Oriel.Cef.Platform;

namespace Oriel.Tests;

internal sealed class FakeAppRegistry : IAppRegistryBackend
{
    public Dictionary<string, string> Protocols { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UrlProtocols { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, string> Assoc { get; } = [];
    public Dictionary<string, string> Run { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<(string Name, string Value)> MachineRun { get; } = [];
    public Dictionary<string, byte[]> Approved { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, byte[]> MachineApproved { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Calls { get; } = [];
    public bool WriteOk { get; set; } = true;
    public bool DeleteShellOk { get; set; } = true;
    public Exception? Failure { get; set; }

    private void Fail()
    {
        if (Failure is not null)
        {
            throw Failure;
        }
    }

    public string? GetProtocolCommand(string protocol) => Protocols.GetValueOrDefault(protocol);

    public bool WriteProtocol(string protocol, string command)
    {
        Fail();
        Calls.Add("write:" + protocol);
        if (WriteOk)
        {
            Protocols[protocol] = command;
        }

        return WriteOk;
    }

    public bool DeleteProtocolShell(string protocol)
    {
        Calls.Add("delete-shell:" + protocol);
        if (DeleteShellOk)
        {
            Protocols.Remove(protocol);
        }

        return DeleteShellOk;
    }

    public void CleanUpProtocol(string protocol)
    {
        Calls.Add("clean-up:" + protocol);
        Fail();
    }

    public bool HasUrlProtocolValue(string scheme)
    {
        Calls.Add("has-url-protocol:" + scheme);
        return UrlProtocols.Contains(scheme);
    }

    public string? AssocQuery(string scheme, int assocStr)
    {
        Calls.Add($"assoc:{scheme}:{assocStr}");
        return Assoc.GetValueOrDefault(assocStr);
    }

    public string? ReadRunValue(string name) => Run.GetValueOrDefault(name);

    public IReadOnlyList<(string Name, string Value)> EnumerateRun(bool machine)
        => machine ? MachineRun : Run.Select(p => (p.Key, p.Value)).ToList();

    public void SetRunEntry(string name, string command)
    {
        Calls.Add("set-run:" + name);
        Fail();
        Run[name] = command;
    }

    public void DeleteRunEntry(string name)
    {
        Calls.Add("delete-run:" + name);
        Run.Remove(name);
    }

    public byte[]? GetStartupApproved(bool machine, string name)
        => (machine ? MachineApproved : Approved).GetValueOrDefault(name);

    public void SetStartupApproved(string name, byte[] data)
    {
        Calls.Add("set-approved:" + name);
        Approved[name] = data;
    }

    public void DeleteStartupApproved(string name)
    {
        Calls.Add("delete-approved:" + name);
        Approved.Remove(name);
    }
}

public sealed class AppProtocolTests
{
    private const string Exe = @"C:\oriel-test\app.exe";
    private const string Spaced = @"C:\Program Files\app.exe";

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData("tab\there", "tab\there")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("tail\\", "\"tail\\\\\"")]
    [InlineData(@"C:\x\y", "\"C:\\x\\y\"")]
    [InlineData("a\\\\\"b", "\"a\\\\\\\\\\\"b\"")]
    public void AddQuoteForArg_QuotesOnlySpacesBackslashesAndQuotes(string arg, string expected)
        => Assert.Equal(expected, WindowsAppRegistry.AddQuoteForArg(arg));

    [Fact]
    public void ProtocolLaunchPath_QuotesWhatNeedsItAndEndsWithTheUrlPlaceholder()
        => Assert.Equal("\"C:\\oriel-test\\app.exe\" --x \"a b\" \"%1\"", WindowsAppRegistry.ProtocolLaunchPath(Exe, ["--x", "a b"]));

    [Fact]
    public void ProtocolLaunchPath_StripsSurroundingQuotesBeforeQuoting()
        => Assert.Equal("\"C:\\Program Files\\app.exe\" \"%1\"", WindowsAppRegistry.ProtocolLaunchPath("\"" + Spaced + "\"", null));

    [Fact]
    public void ProtocolLaunchPath_UsesAnEmptyPathAsGivenAndNullAsTheProcess()
    {
        Assert.Equal(" \"%1\"", WindowsAppRegistry.ProtocolLaunchPath("", null));
        Assert.Equal(WindowsAppRegistry.AddQuoteForArg(Environment.ProcessPath!) + " \"%1\"", WindowsAppRegistry.ProtocolLaunchPath(null, null));
    }

    [Theory]
    [InlineData("myapp://open/x", "myapp")]
    [InlineData("MyApp:x", "myapp")]
    [InlineData("  myapp:x", "myapp")]
    [InlineData("a+b.c-d:x", "a+b.c-d")]
    [InlineData("myapp", "")]
    [InlineData("", "")]
    [InlineData(":x", "")]
    [InlineData("1app:x", "")]
    [InlineData("my_app:x", "")]
    [InlineData(@"c:\x", "file")]
    public void SchemeOf_IsGurlsScheme(string url, string scheme)
        => Assert.Equal(scheme, WindowsAppRegistry.SchemeOf(url));

    [Theory]
    [InlineData("", false)]
    [InlineData("myapp", true)]
    [InlineData("a1+-.", true)]
    [InlineData("1app", false)]
    [InlineData("+app", false)]
    [InlineData("my app", false)]
    [InlineData("my_app", false)]
    [InlineData("a\\b", false)]
    [InlineData("é", false)]
    public void IsValidProtocolScheme_IsRfc3986(string protocol, bool valid)
        => Assert.Equal(valid, WindowsAppRegistry.IsValidProtocolScheme(protocol));

    [Fact]
    public void Set_WritesTheProtocolWithOurCommand()
    {
        FakeAppRegistry fake = new();

        Assert.True(WindowsAppRegistry.SetAsDefaultProtocolClient(fake, "myapp", Exe, ["--x"]));

        Assert.Equal("\"C:\\oriel-test\\app.exe\" --x \"%1\"", fake.Protocols["myapp"]);
    }

    [Fact]
    public void Set_InvalidScheme_FailsWithoutWriting()
    {
        FakeAppRegistry fake = new();

        Assert.False(WindowsAppRegistry.SetAsDefaultProtocolClient(fake, "", Exe, null));
        Assert.False(WindowsAppRegistry.SetAsDefaultProtocolClient(fake, "my_app", Exe, null));

        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void Set_WhenTheRegistryRefuses_ReturnsFalse()
    {
        Assert.False(WindowsAppRegistry.SetAsDefaultProtocolClient(new FakeAppRegistry { WriteOk = false }, "myapp", Exe, null));
        Assert.False(WindowsAppRegistry.SetAsDefaultProtocolClient(new FakeAppRegistry { Failure = new UnauthorizedAccessException() }, "myapp", Exe, null));
    }

    [Fact]
    public void IsDefault_IsAnExactCaseSensitiveMatch()
    {
        FakeAppRegistry fake = new();
        Assert.False(WindowsAppRegistry.IsDefaultProtocolClient(fake, "myapp", Exe, null));

        fake.Protocols["myapp"] = "\"C:\\oriel-test\\app.exe\" --x \"%1\"";
        Assert.True(WindowsAppRegistry.IsDefaultProtocolClient(fake, "myapp", Exe, ["--x"]));
        Assert.False(WindowsAppRegistry.IsDefaultProtocolClient(fake, "myapp", Exe.ToUpperInvariant(), ["--x"]));
        Assert.False(WindowsAppRegistry.IsDefaultProtocolClient(fake, "myapp", Exe, ["--y"]));
        Assert.False(WindowsAppRegistry.IsDefaultProtocolClient(fake, "my_app", Exe, ["--x"]));
    }

    [Fact]
    public void Remove_OursDeletesTheShellKeyThenCleansUp()
    {
        FakeAppRegistry fake = new();
        WindowsAppRegistry.SetAsDefaultProtocolClient(fake, "myapp", Exe, null);
        fake.Calls.Clear();

        Assert.True(WindowsAppRegistry.RemoveAsDefaultProtocolClient(fake, "myapp", Exe, null));

        Assert.Equal(["delete-shell:myapp", "clean-up:myapp"], fake.Calls);
    }

    [Fact]
    public void Remove_IsTrueWithoutTouchingAnythingWhenNotOursOrNotRegistered()
    {
        FakeAppRegistry fake = new();
        Assert.True(WindowsAppRegistry.RemoveAsDefaultProtocolClient(fake, "myapp", Exe, null));

        fake.Protocols["myapp"] = "\"C:\\other.exe\" \"%1\"";
        Assert.True(WindowsAppRegistry.RemoveAsDefaultProtocolClient(fake, "myapp", Exe, null));

        Assert.Empty(fake.Calls);
        Assert.True(fake.Protocols.ContainsKey("myapp"));
    }

    [Fact]
    public void Remove_InvalidSchemeOrRefusedDeleteIsFalse()
    {
        FakeAppRegistry fake = new();
        Assert.False(WindowsAppRegistry.RemoveAsDefaultProtocolClient(fake, "", Exe, null));

        WindowsAppRegistry.SetAsDefaultProtocolClient(fake, "myapp", Exe, null);
        fake.DeleteShellOk = false;
        fake.Calls.Clear();
        Assert.False(WindowsAppRegistry.RemoveAsDefaultProtocolClient(fake, "myapp", Exe, null));
        Assert.Equal(["delete-shell:myapp"], fake.Calls);
    }

    [Fact]
    public void Remove_AFailedCleanUpStillCountsAsRemoved()
    {
        FakeAppRegistry fake = new();
        WindowsAppRegistry.SetAsDefaultProtocolClient(fake, "myapp", Exe, null);
        fake.Failure = new InvalidOperationException();

        Assert.True(WindowsAppRegistry.RemoveAsDefaultProtocolClient(fake, "myapp", Exe, null));
    }

    [Fact]
    public void AppInfo_NeedsAUrlProtocolValueBeforeAskingTheShell()
    {
        FakeAppRegistry fake = new();
        fake.Assoc[WindowsAppRegistry.AssocstrFriendlyAppName] = "My App";

        Assert.Equal("", WindowsAppRegistry.GetAppInfoForProtocol(fake, "myapp://x", WindowsAppRegistry.AssocstrFriendlyAppName));
        Assert.Equal(["has-url-protocol:myapp"], fake.Calls);

        fake.UrlProtocols.Add("myapp");
        Assert.Equal("My App", WindowsAppRegistry.GetAppInfoForProtocol(fake, "MyApp://x", WindowsAppRegistry.AssocstrFriendlyAppName));
        Assert.Equal("assoc:myapp:4", fake.Calls[^1]);
    }

    [Fact]
    public void AppInfo_EmptyWhenTheQueryFailsOrThereIsNoScheme()
    {
        FakeAppRegistry fake = new();
        fake.UrlProtocols.Add("myapp");

        Assert.Equal("", WindowsAppRegistry.GetAppInfoForProtocol(fake, "myapp://x", WindowsAppRegistry.AssocstrExecutable));
        Assert.Equal("assoc:myapp:2", fake.Calls[^1]);
        int calls = fake.Calls.Count;
        Assert.Equal("", WindowsAppRegistry.GetAppInfoForProtocol(fake, "myapp", WindowsAppRegistry.AssocstrExecutable));
        Assert.Equal(calls, fake.Calls.Count);
    }

    [Fact]
    public async Task ApplicationInfo_WithoutANameRejectsAsElectronDoes()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => OrielApp.ApplicationInfoFor(Exe, ""));
        Assert.Equal("Unable to retrieve display name of app", e.Message);
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
            Assert.True(OrielApp.RemoveProtocolClient(protocol, @"C:\other.exe", null));
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
    public void RealRegistry_RemoveKeepsAClassKeyThatHasOtherValuesOrSubkeys()
    {
        string protocol = "oriel-test-" + Guid.NewGuid().ToString("N");
        string keyPath = @"Software\Classes\" + protocol;
        try
        {
            Assert.True(OrielApp.SetProtocolClient(protocol, Exe, null));
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)!)
            {
                key.SetValue("Extra", "kept");
            }

            Assert.True(OrielApp.RemoveProtocolClient(protocol, Exe, null));

            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath)!)
            {
                Assert.Equal(["Extra"], key.GetValueNames());
                Assert.Empty(key.GetSubKeyNames());
            }

            Registry.CurrentUser.DeleteSubKeyTree(keyPath);
            Assert.True(OrielApp.SetProtocolClient(protocol, Exe, null));
            Registry.CurrentUser.CreateSubKey(keyPath + @"\DefaultIcon").Dispose();

            Assert.True(OrielApp.RemoveProtocolClient(protocol, Exe, null));

            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath)!)
            {
                Assert.Empty(key.GetValueNames());
                Assert.Equal(["DefaultIcon"], key.GetSubKeyNames());
            }
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
    public async Task RealRegistry_AssociationQueryFindsTheAppBehindAUrlProtocolOnly()
    {
        string protocol = "oriel-test-" + Guid.NewGuid().ToString("N");
        string keyPath = @"Software\Classes\" + protocol;
        string notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        try
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                using RegistryKey cmd = key.CreateSubKey(@"shell\open\command");
                cmd.SetValue("", "\"" + notepad + "\" \"%1\"");
            }

            // A class without "URL Protocol" is a ProgID, not a protocol.
            Assert.Equal("", OrielApp.ProtocolNameFor(protocol + "://x"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => OrielApp.ProtocolInfoFor(protocol + "://x"));

            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)!)
            {
                key.SetValue("URL Protocol", "");
            }

            string name = OrielApp.ProtocolNameFor(protocol + "://x");
            Assert.NotEmpty(name);
            ApplicationInfoForProtocol info = await OrielApp.ProtocolInfoFor(protocol + "://x");
            Assert.Equal(notepad, info.Path, ignoreCase: true);
            Assert.Equal(name, info.Name);
            Assert.Equal((48, 48), info.Icon.GetSize());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [WindowsOnlyFact]
    public async Task RealRegistry_NoUrlProtocolRejectsWithElectronsPathMessage()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => OrielApp.ProtocolInfoFor("oriel-test-" + Guid.NewGuid().ToString("N") + "://x"));
        Assert.Equal("Unable to retrieve installation path to app", e.Message);
    }
}
