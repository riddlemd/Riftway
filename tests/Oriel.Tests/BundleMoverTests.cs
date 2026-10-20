using Oriel.Cef.Platform;

namespace Oriel.Tests;

public sealed class BundleMoverFlowTests
{
    private const string Bundle = "/Users/me/Downloads/Foo.app";
    private const string Destination = "/Applications/Foo.app";
    private readonly FakeOps _ops = new();
    private readonly List<BundleMoverConflictType> _asked = [];
    private int _quits;

    private bool Move(bool? answer = null)
        => OrielApp.MoveBundle(_ops, t => { _asked.Add(t); return answer; }, () => { _quits++; _ops.Log.Add("quit"); });

    [Fact]
    public void AnAppAlreadyInApplicationsStaysPut()
    {
        _ops.InApplications = true;
        Assert.True(Move());
        Assert.Empty(_ops.Log);
        Assert.Equal(0, _quits);
    }

    [Fact]
    public void AMoveCopiesTrashesTheOriginalRelaunchesAndQuits()
    {
        Assert.True(Move());
        Assert.Equal(["activate", $"copy {Bundle} -> {Destination}", $"deleteOrTrash {Bundle}", $"sh {OrielApp.RelaunchScript(42, Destination)}", "quit"], _ops.Log);
        Assert.Empty(_asked);
    }

    [Fact]
    public void FromADiskImageTheOriginalStaysAndTheImageIsDetached()
    {
        _ops.Device = "/dev/disk4s1";
        Assert.True(Move());
        Assert.Equal(["activate", $"copy {Bundle} -> {Destination}", $"sh {OrielApp.RelaunchScript(42, Destination)}",
            "sh (/bin/sleep 5 && /usr/bin/hdiutil detach '/dev/disk4s1') &", "quit"], _ops.Log);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public void AnAppInTheWayIsTrashedUnlessTheHandlerSaysNo(bool? answer)
    {
        _ops.Existing.Add(Destination);
        Assert.True(Move(answer));
        Assert.Equal([BundleMoverConflictType.Exists], _asked);
        Assert.Equal(["activate", $"trash {Destination}", $"copy {Bundle} -> {Destination}", $"deleteOrTrash {Bundle}",
            $"sh {OrielApp.RelaunchScript(42, Destination)}", "quit"], _ops.Log);
    }

    [Fact]
    public void TheHandlerCanStopTheMove()
    {
        _ops.Existing.Add(Destination);
        Assert.False(Move(false));
        Assert.Equal(["activate"], _ops.Log);
    }

    [Fact]
    public void ARunningCopyIsBroughtForwardAndThisAppQuits()
    {
        _ops.Existing.Add(Destination);
        _ops.Running.Add(Destination);
        Assert.True(Move());
        Assert.Equal([BundleMoverConflictType.ExistsAndRunning], _asked);
        Assert.Equal(["activate", $"open {Destination}", "quit"], _ops.Log);
    }

    [Fact]
    public void ARunningCopyCanBeLeftAlone()
    {
        _ops.Existing.Add(Destination);
        _ops.Running.Add(Destination);
        Assert.False(Move(false));
        Assert.Equal(["activate"], _ops.Log);
    }

    [Fact]
    public void FailuresThrowElectronsMessages()
    {
        _ops.Existing.Add(Destination);
        _ops.TrashFails = true;
        Assert.Equal("Failed to delete existing application", Assert.Throws<InvalidOperationException>(() => Move()).Message);
        _ops.Existing.Clear();
        _ops.CopyFails = true;
        Assert.Equal("Failed to copy current bundle to the applications folder", Assert.Throws<InvalidOperationException>(() => Move()).Message);
        Assert.Equal(0, _quits);
    }

    [Fact]
    public void AnUnwritableApplicationsFolderNeedsAuthorization()
    {
        _ops.Unwritable.Add("/Applications");
        Assert.True(Move());
        Assert.Equal(["activate", $"authorized {Bundle} -> {Destination}", $"deleteOrTrash {Bundle}", $"sh {OrielApp.RelaunchScript(42, Destination)}", "quit"], _ops.Log);
    }

    [Fact]
    public void AnUnwritableAppInTheWayNeedsAuthorization()
    {
        _ops.Existing.Add(Destination);
        _ops.Unwritable.Add(Destination);
        Assert.True(Move());
        Assert.Contains($"authorized {Bundle} -> {Destination}", _ops.Log);
        Assert.Empty(_asked);
    }

    [Theory]
    [InlineData(AuthorizedInstallResult.Canceled, "User rejected the authorization request")]
    [InlineData(AuthorizedInstallResult.Failed, "Failed to copy to applications directory even with authorization")]
    public void AFailedAuthorizedInstallThrows(AuthorizedInstallResult result, string message)
    {
        _ops.Unwritable.Add("/Applications");
        _ops.Authorized = result;
        Assert.Equal(message, Assert.Throws<InvalidOperationException>(() => Move()).Message);
        Assert.Equal(0, _quits);
    }

    [Fact]
    public void ShellQuoted_EscapesSingleQuotes() => Assert.Equal(@"'it'\''s here'", OrielApp.ShellQuoted("it's here"));

    [Fact]
    public void RelaunchScript_WaitsClearsQuarantineAndOpens()
        => Assert.Equal("(while /bin/kill -0 7 >&/dev/null; do /bin/sleep 0.1; done; /usr/bin/xattr -d -r com.apple.quarantine '/A/B.app'; /usr/bin/open '/A/B.app') &",
            OrielApp.RelaunchScript(7, "/A/B.app"));

    private static string Plist(string images) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <plist version="1.0"><dict><key>framework</key><string>1</string><key>images</key><array>{images}</array></dict></plist>
        """;

    private static string Image(params string[] devs)
        => "<dict><key>system-entities</key><array>" + string.Concat(devs.Select(d => $"<dict><key>dev-entry</key><string>{d}</string></dict>")) + "</array></dict>";

    [Fact]
    public void DiskImageDevice_IsTheDeviceWhenAnImageListsIt()
    {
        Assert.Equal("/dev/disk5s1", OrielApp.DiskImageDevice(Plist(Image("/dev/disk4") + Image("/dev/disk5", "/dev/disk5s1")), "/dev/disk5s1"));
        Assert.Null(OrielApp.DiskImageDevice(Plist(Image("/dev/disk4")), "/dev/disk5s1"));
        Assert.Null(OrielApp.DiskImageDevice(Plist(""), "/dev/disk5s1"));
    }

    [Theory]
    [InlineData("<dict><key>other</key><string>x</string></dict>")]
    [InlineData("<string>not a dict</string>")]
    [InlineData("<dict><key>system-entities</key><array><string>x</string></array></dict>")]
    [InlineData("<dict><key>system-entities</key><array><dict><key>content-hint</key><string>x</string></dict></array></dict>")]
    [InlineData("<dict><key>system-entities</key><array><dict><key>dev-entry</key><integer>1</integer></dict></array></dict>")]
    public void DiskImageDevice_GivesUpAtAnEntryOfTheWrongShape(string firstImage)
    {
        Assert.Null(OrielApp.DiskImageDevice(Plist(firstImage + Image("/dev/disk5s1")), "/dev/disk5s1"));
    }

    [Theory]
    [InlineData("not xml")]
    [InlineData("<root/>")]
    [InlineData("<plist><array/></plist>")]
    [InlineData("<plist><dict><key>images</key><string>x</string></dict></plist>")]
    public void DiskImageDevice_IsNullForAnythingButAPlistWithImages(string xml) => Assert.Null(OrielApp.DiskImageDevice(xml, "/dev/disk1"));

    private sealed class FakeOps : IBundleMoverOps
    {
        public List<string> Log = [];
        public bool InApplications, TrashFails, CopyFails;
        public string? Device;
        public HashSet<string> Existing = [], Unwritable = [], Running = [];
        public AuthorizedInstallResult Authorized = AuthorizedInstallResult.Installed;

        public string BundlePath() => Bundle;
        public bool IsInApplicationsFolder(string bundlePath) => InApplications;
        public string? ContainingDiskImageDevice(string bundlePath) => Device;
        public string LocalApplicationsDirectory() => "/Applications";
        public bool IsWritable(string path) => !Unwritable.Contains(path);
        public bool Exists(string path) => Existing.Contains(path);
        public void ActivateApp() => Log.Add("activate");
        public AuthorizedInstallResult AuthorizedInstall(string source, string destination)
        {
            Log.Add($"authorized {source} -> {destination}");
            return Authorized;
        }
        public bool IsRunning(string bundlePath) => Running.Contains(bundlePath);
        public void OpenAndWait(string path) => Log.Add($"open {path}");
        public bool Trash(string path)
        {
            Log.Add($"trash {path}");
            return !TrashFails;
        }
        public bool Copy(string source, string destination)
        {
            Log.Add($"copy {source} -> {destination}");
            return !CopyFails;
        }
        public bool DeleteOrTrash(string path)
        {
            Log.Add($"deleteOrTrash {path}");
            return true;
        }
        public void RunShell(string script) => Log.Add($"sh {script}");
        public int ProcessId => 42;
    }
}

public sealed class MacBundleMoverTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("oriel-mover").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void TheLocalApplicationsDirectoryIsApplications()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal("/Applications", MacBundleMover.LocalApplicationsDirectory());
    }

    [Fact]
    public void TheRootVolumeHasNoMountDeviceAndNoDiskImage()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Null(MacBundleMover.MountDevice("/System/Library/CoreServices/Finder.app"));
        Assert.Null(MacBundleMoverOps.Instance.ContainingDiskImageDevice(Path.Combine(_dir, "X.app")));
    }

    [Fact]
    public void ADataVolumePathHasItsMountDevice()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.StartsWith("/dev/", MacBundleMover.MountDevice(Path.Combine(_dir, "X.app")));
    }

    [Fact]
    public void FilesAreCheckedCopiedAndDeleted()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var source = Directory.CreateDirectory(Path.Combine(_dir, "A.app")).FullName;
        File.WriteAllText(Path.Combine(source, "f"), "x");
        var copy = Path.Combine(_dir, "B.app");
        Assert.True(MacBundleMover.Exists(source));
        Assert.True(MacBundleMover.IsWritable(_dir));
        Assert.False(MacBundleMover.Exists(copy));
        Assert.True(MacBundleMover.Copy(source, copy));
        Assert.Equal("x", File.ReadAllText(Path.Combine(copy, "f")));
        Assert.False(MacBundleMover.Copy(source, copy));
        Assert.True(MacBundleMover.DeleteOrTrash(copy));
        Assert.False(Directory.Exists(copy));
        Assert.False(MacBundleMover.IsWritable("/System"));
    }

    [Fact]
    public void FinderIsRunningAndAMadeUpBundleIsNot()
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.True(MacBundleMover.IsRunning("/System/Library/CoreServices/Finder.app/"));
        Assert.False(MacBundleMover.IsRunning(Path.Combine(_dir, "Nope.app")));
    }

    [Theory]
    [InlineData("/tmp/a.app", "/tmp/b")]
    [InlineData("  ", "/tmp/b.app")]
    // Only inputs the checks refuse: anything else reaches AuthorizationCopyRights, which shows a password dialog.
    public void AuthorizedInstallRefusesAnythingButAnAppBundleWithoutAsking(string source, string destination)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Assert.Equal(AuthorizedInstallResult.Failed, MacBundleMover.AuthorizedInstall(source, destination));
    }
}
