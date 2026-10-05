using System.Buffers.Binary;

namespace Oriel.Tests;

public sealed class CrashReporterConfigTests
{
    private static readonly CrashKeySlot[] Slots = [new("process_type", "small"), new("extra_key", "large")];

    private static string Build(string submitUrl = "http://127.0.0.1/submit", bool rateLimit = false, string? handler = "Oriel.Subprocess.exe")
        => CrashReporterConfig.Build("App", "Product", "1.2.3", submitUrl, rateLimit, false, handler, Slots);

    [Fact]
    public void Build_StartsWithTheOwnershipMarker()
        => Assert.StartsWith(CrashReporterConfig.Marker + "\n[Config]\n", Build(), StringComparison.Ordinal);

    [Fact]
    public void Build_MapsTheOptionsOntoCefKeys()
    {
        var cfg = Build();

        Assert.Contains("\nProductName=Product\n", cfg, StringComparison.Ordinal);
        Assert.Contains("\nProductVersion=1.2.3\n", cfg, StringComparison.Ordinal);
        Assert.Contains("\nAppName=App\n", cfg, StringComparison.Ordinal);
        Assert.Contains("\nExternalHandler=Oriel.Subprocess.exe\n", cfg, StringComparison.Ordinal);
        Assert.Contains("\nServerURL=http://127.0.0.1/submit\n", cfg, StringComparison.Ordinal);
        Assert.Contains("\nMaxDatabaseSizeInMb=128\nMaxDatabaseAgeInDays=365\n", cfg, StringComparison.Ordinal);
        Assert.EndsWith("[CrashKeys]\nprocess_type=small\nextra_key=large\n", cfg, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WithoutUrlOrHandler_OmitsThem()
    {
        var cfg = Build(submitUrl: "", handler: null);

        Assert.DoesNotContain("ServerURL", cfg, StringComparison.Ordinal);
        Assert.DoesNotContain("ExternalHandler", cfg, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RateLimitOff_DisablesCefsLimit()
    {
        var cfg = Build(rateLimit: false);

        Assert.Contains("\nRateLimitEnabled=false\n", cfg, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxUploadsPerDay", cfg, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RateLimitOn_AllowsTwentyFourUploadsADay()
        => Assert.Contains("\nRateLimitEnabled=true\nMaxUploadsPerDay=24\n", Build(rateLimit: true), StringComparison.Ordinal);

    [Fact]
    public void Build_KeepsEachValueOnOneLine()
        => Assert.Contains("\nProductName=a b\n",
            CrashReporterConfig.Build("App", "a\nb", "1", "", false, false, null, []), StringComparison.Ordinal);

    [Fact]
    public void AssignSlots_PutsTheFixedKeysFirstAsSmall_ThenTheRestLarge()
        => Assert.Equal(
            [new("process_type", "small"), new("platform", "small"), new("a", "large"), new("b", "large")],
            CrashReporterConfig.AssignSlots(["a", "b"]));

    [Fact]
    public void AssignSlots_SpillsToMediumThenSmall_AndDropsTheRest()
    {
        var keys = Enumerable.Range(0, 80).Select(i => $"k{i}").ToList();

        var slots = CrashReporterConfig.AssignSlots(keys);

        Assert.Equal(26, slots.Count(s => s.Size == "large"));
        Assert.Equal(26, slots.Count(s => s.Size == "medium"));
        Assert.Equal(26, slots.Count(s => s.Size == "small"));
        Assert.Equal(new CrashKeySlot("k26", "medium"), slots[2 + 26]);
        Assert.Equal(new CrashKeySlot("k52", "small"), slots[2 + 52]);
        Assert.DoesNotContain(slots, s => s.Name == "k76");
    }

    [Fact]
    public void AssignSlots_SkipsDuplicatesAndUndeclarableNames()
        => Assert.Equal(["process_type", "platform", "a"],
            CrashReporterConfig.AssignSlots(["a", "a", "platform", "b=c", "#d"]).Select(s => s.Name));

    [Theory]
    [InlineData("")]
    [InlineData(" lead")]
    [InlineData("trail ")]
    [InlineData("a=b")]
    [InlineData("a\nb")]
    [InlineData("#comment")]
    [InlineData("[section")]
    public void IsDeclarable_RejectsWhatCefsParserWouldMangle(string key) => Assert.False(CrashReporterConfig.IsDeclarable(key));

    [Fact]
    public void IsDeclarable_AcceptsElectronsKeys() => Assert.True(CrashReporterConfig.IsDeclarable("_productName"));

    [Fact]
    public void ReadSlots_ReadsOnlyTheCrashKeysSection()
    {
        const string cfg = "[Config]\nProductName=large\n[CrashKeys]\n# c=small\n a = small \nb=huge\nc=medium\n[Other]\nd=large\n";

        Assert.Equal([new("a", "small"), new("c", "medium")], CrashReporterConfig.ReadSlots(cfg));
    }

    [Fact]
    public void ReadSlots_RoundTripsBuild() => Assert.Equal(Slots, CrashReporterConfig.ReadSlots(Build()));

    [Fact]
    public void Truncate_KeepsWholeCharactersWithinTheByteLimit()
    {
        Assert.Equal("abc", CrashReporterConfig.Truncate("abc", 3));
        Assert.Equal("ab", CrashReporterConfig.Truncate("abc", 2));
        Assert.Equal("a", CrashReporterConfig.Truncate("aé", 2));
        Assert.Equal("a", CrashReporterConfig.Truncate("a\U0001F600", 4));
        Assert.Equal("a\U0001F600", CrashReporterConfig.Truncate("a\U0001F600", 5));
    }

    [Fact]
    public void SlotSizes_AreCefs() => Assert.Equal([64, 256, 1024],
        new[] { "small", "medium", "large" }.Select(s => new CrashKeySlot("k", s).MaxBytes));

    [Fact]
    public void EncodePairs_ThenDecode_RoundTripsAnyText()
    {
        KeyValuePair<string, string>[] pairs = [new("k=1", "v a\"l"), new("ü", ""), new("x", "\U0001F600")];

        Assert.Equal(pairs, CrashReporterConfig.DecodePairs(CrashReporterConfig.EncodePairs(pairs)));
    }

    [Fact]
    public void EncodePairs_IsUppercaseHexOfNulSeparatedUtf8()
        => Assert.Equal("6B0076006B320000", CrashReporterConfig.EncodePairs([new("k", "v"), new("k2", "")]));

    [Theory]
    [InlineData(8)]
    [InlineData(19)]
    public void EncodePairs_StopsBeforeTheLimit(int maxChars)
        => Assert.Equal("6B007600", CrashReporterConfig.EncodePairs([new("k", "v"), new("k2", "v2")], maxChars));

    [Fact]
    public void PathFor_IsBesideTheExecutable()
    {
        if (OperatingSystem.IsMacOS()) return;
        Assert.Equal(Path.Combine("dir", "crash_reporter.cfg"), CrashReporterConfig.PathFor("dir"));
    }
}

public sealed class CrashReporterTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("oriel-crash-").FullName;
    private string Cfg => Path.Combine(_directory, "crash_reporter.cfg");

    public CrashReporterTests() => CrashReporter.Reset(Cfg);

    public void Dispose()
    {
        CrashReporter.Reset();
        Directory.Delete(_directory, recursive: true);
    }

    private static CrashReporterStartOptions Options(bool upload = true) => new()
    {
        SubmitURL = "http://127.0.0.1/submit",
        ProductName = "Product",
        CompanyName = "Company",
        UploadToServer = upload,
        Extra = new Dictionary<string, string> { ["extra_key"] = "extra" },
        GlobalExtra = new Dictionary<string, string> { ["global_key"] = "global" },
    };

    [Fact]
    public void Start_WithoutSubmitUrl_ThrowsElectronsErrorAndDoesNotStart()
    {
        var error = Assert.Throws<ArgumentException>(() => CrashReporter.Start(new CrashReporterStartOptions()));

        Assert.Equal("submitURL must be specified when uploadToServer is true", error.Message);
        Assert.False(CrashReporter.IsStarted);
        Assert.False(File.Exists(Cfg));
    }

    [Fact]
    public void Start_WithoutSubmitUrl_IsFineWhenNotUploading()
    {
        CrashReporter.Start(new CrashReporterStartOptions { UploadToServer = false });

        Assert.True(CrashReporter.IsStarted);
    }

    [Fact]
    public void Start_DeclaresGlobalAndExtraKeys()
        => StartAndAssertSlots(["process_type", "platform", "_productName", "_version", "_companyName", "global_key", "extra_key"]);

    private void StartAndAssertSlots(string[] expected)
    {
        CrashReporter.Start(Options());

        Assert.Equal(expected, CrashReporterConfig.ReadSlots(File.ReadAllText(Cfg)).Select(s => s.Name));
    }

    [Fact]
    public void Start_KeepsKeysDeclaredInAnOwnedCfg()
    {
        File.WriteAllText(Cfg, CrashReporterConfig.Marker + "\n[CrashKeys]\nlearned=large\n");

        StartAndAssertSlots(["process_type", "platform", "_productName", "_version", "_companyName", "global_key", "extra_key", "learned"]);
    }

    [Fact]
    public void Start_IgnoresKeysOfACfgItDidNotWrite()
    {
        File.WriteAllText(Cfg, "[CrashKeys]\nshipped=large\n");

        StartAndAssertSlots(["process_type", "platform", "_productName", "_version", "_companyName", "global_key", "extra_key"]);
    }

    [Fact]
    public void Start_UploadToServerFalse_LeavesTheUrlOut()
    {
        CrashReporter.Start(Options(upload: false));

        Assert.DoesNotContain("ServerURL", File.ReadAllText(Cfg), StringComparison.Ordinal);
    }

    [Fact]
    public void Start_UploadToServerTrue_WritesTheUrl()
    {
        CrashReporter.Start(Options());

        Assert.Contains("\nServerURL=http://127.0.0.1/submit\n", File.ReadAllText(Cfg), StringComparison.Ordinal);
    }

    [Fact]
    public void Start_Twice_KeepsTheFirst()
    {
        CrashReporter.Start(Options());
        CrashReporter.Start(Options() with { ProductName = "Other" });

        Assert.Contains("\nProductName=Product\n", File.ReadAllText(Cfg), StringComparison.Ordinal);
    }

    [Fact]
    public void Amend_PutsProductAndVersionUnderGlobalExtra()
    {
        var amended = CrashReporter.Amend("P", "1.0", "C", new Dictionary<string, string> { ["_productName"] = "Override", ["k"] = "v" });

        Assert.Equal(new Dictionary<string, string> { ["_productName"] = "Override", ["_version"] = "1.0", ["_companyName"] = "C", ["k"] = "v" }, amended);
    }

    [Fact]
    public void Amend_LeavesAGivenCompanyNameAlone()
        => Assert.Equal("Given", CrashReporter.Amend("P", "1", "C", new Dictionary<string, string> { ["_companyName"] = "Given" })["_companyName"]);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Amend_WithoutCompanyName_AddsNone(string? companyName)
        => Assert.False(CrashReporter.Amend("P", "1", companyName, null).ContainsKey("_companyName"));

    [Fact]
    public void Parameters_AddReplaceRemove()
    {
        CrashReporter.AddExtraParameter("a", "1");
        CrashReporter.AddExtraParameter("b", "2");
        CrashReporter.AddExtraParameter("a", "3");
        CrashReporter.RemoveExtraParameter("b");

        Assert.Equal(new Dictionary<string, string> { ["a"] = "3" }, CrashReporter.GetParameters());
    }

    [Fact]
    public void AddExtraParameter_IgnoresKeysOfFortyBytesOrMore()
    {
        CrashReporter.AddExtraParameter(new string('k', 39), "kept");
        CrashReporter.AddExtraParameter(new string('k', 40), "ignored");

        Assert.Equal([new string('k', 39)], CrashReporter.GetParameters().Keys);
    }

    [Fact]
    public void AddExtraParameter_BeforeStart_IsDeclaredByStart()
    {
        CrashReporter.AddExtraParameter("early", "1");
        CrashReporter.Start(Options());

        Assert.Contains("\nearly=large\n", File.ReadAllText(Cfg), StringComparison.Ordinal);
        Assert.Equal("1", CrashReporter.GetParameters()["early"]);
    }

    [Fact]
    public void AddExtraParameter_UndeclaredAfterStart_IsDeclaredForTheNextLaunch()
    {
        CrashReporter.Start(Options());
        CrashReporter.AddExtraParameter("late", "1");

        Assert.EndsWith("\nlate=large\n", File.ReadAllText(Cfg), StringComparison.Ordinal);
    }

    [Fact]
    public void AddExtraParameter_UndeclaredBeforeStart_LeavesTheCfgAlone()
    {
        File.WriteAllText(Cfg, CrashReporterConfig.Marker + "\n[CrashKeys]\n");
        CrashReporter.AddExtraParameter("late", "1");

        Assert.DoesNotContain("late", File.ReadAllText(Cfg), StringComparison.Ordinal);
    }

    [Fact]
    public void UploadToServer_FollowsStartAndSet()
    {
        CrashReporter.SetUploadToServer(true);
        Assert.False(CrashReporter.GetUploadToServer());

        CrashReporter.Start(Options());
        Assert.True(CrashReporter.GetUploadToServer());

        CrashReporter.SetUploadToServer(false);
        Assert.False(CrashReporter.GetUploadToServer());
    }

    [Fact]
    public void UploadToServer_StartedFalse_IsFalse()
    {
        CrashReporter.Start(Options(upload: false));

        Assert.False(CrashReporter.GetUploadToServer());
    }

    [Fact]
    public void GetUploadedReports_BeforeTheRuntime_IsEmpty()
    {
        CrashReporter.Start(Options());

        Assert.Empty(CrashReporter.GetUploadedReports());
        Assert.Null(CrashReporter.GetLastCrashReport());
    }

    [Fact]
    public void ChildSwitchValue_BeforeStart_IsNull() => Assert.Null(CrashReporter.ChildSwitchValue("renderer"));

    [Fact]
    public void ChildSwitchValue_CarriesTypePlatformAndLiveGlobalKeys_NotExtra()
    {
        CrashReporter.Start(Options());
        CrashReporter.ReadLiveSlots();

        var pairs = CrashReporterConfig.DecodePairs(CrashReporter.ChildSwitchValue("gpu-process")!);

        Assert.Equal(
            [new("process_type", "gpu-process"), new("platform", CrashReporterConfig.Platform), new("_productName", "Product"),
             new("_version", CrashReporter.AppVersion()), new("_companyName", "Company"), new("global_key", "global")],
            pairs);
    }

    [Fact]
    public void ChildSwitchValue_SkipsGlobalKeysTheLaunchDidNotDeclare()
    {
        File.WriteAllText(Cfg, "[CrashKeys]\nglobal_key=small\n");
        CrashReporter.ReadLiveSlots();
        CrashReporter.Start(Options() with { GlobalExtra = new Dictionary<string, string> { ["global_key"] = new string('x', 100) } });

        var pairs = CrashReporterConfig.DecodePairs(CrashReporter.ChildSwitchValue("renderer")!);

        Assert.Equal(["process_type", "platform", "global_key"], pairs.Select(p => p.Key));
        Assert.Equal(new string('x', 64), pairs[2].Value);
    }

    [Fact]
    public void Latest_IsTheNewestUpload_TiesKeepOrder()
    {
        var epoch = new CrashReport(DateTimeOffset.UnixEpoch, "");
        var old = new CrashReport(DateTimeOffset.FromUnixTimeSeconds(100), "old");
        var recent = new CrashReport(DateTimeOffset.FromUnixTimeSeconds(200), "new");

        Assert.Equal(recent, CrashReporter.Latest([epoch, old, recent]));
        Assert.Same(epoch, CrashReporter.Latest([epoch, new CrashReport(DateTimeOffset.UnixEpoch, "second")]));
        Assert.Null(CrashReporter.Latest([]));
    }

    [Theory]
    [InlineData("1.2.3+abcdef", "1.2.3")]
    [InlineData("2.0.0-beta", "2.0.0-beta")]
    [InlineData(null, "4.5.6.7")]
    public void VersionFrom_DropsBuildMetadata(string? informational, string expected)
        => Assert.Equal(expected, CrashReporter.VersionFrom(informational, new Version(4, 5, 6, 7)));
}

public sealed unsafe class CrashReporterNativeTests : IDisposable
{
    private readonly string _path = Path.Combine(Directory.CreateTempSubdirectory("oriel-settings-").FullName, "settings.dat");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);

    // CEF's crashpad settings.dat: magic, version 2, options, padding, last upload, client id, next upload, backoff.
    private void WriteSettings(uint options, uint version = 2, uint magic = CrashReporterNative.SettingsMagic)
    {
        var bytes = new byte[56];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, magic);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), version);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), options);
        for (var i = 16; i < bytes.Length; i++) bytes[i] = (byte)i;
        File.WriteAllBytes(_path, bytes);
    }

    [Fact]
    public void SettingsMagic_IsCPdsLittleEndian() => Assert.Equal("sdPC"u8.ToArray(), BitConverter.GetBytes(CrashReporterNative.SettingsMagic));

    [Theory]
    [InlineData(1u, true)]
    [InlineData(0u, false)]
    [InlineData(2u, false)]
    public void ReadUploadsEnabled_ReadsBitZero(uint options, bool expected)
    {
        WriteSettings(options);

        Assert.Equal(expected, CrashReporterNative.ReadUploadsEnabled(_path));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void ReadUploadsEnabled_AcceptsUpstreamAndCefVersions(uint version)
    {
        WriteSettings(1, version);

        Assert.True(CrashReporterNative.ReadUploadsEnabled(_path));
    }

    [Fact]
    public void ReadUploadsEnabled_RejectsOtherFiles()
    {
        WriteSettings(1, version: 3);
        Assert.Null(CrashReporterNative.ReadUploadsEnabled(_path));

        WriteSettings(1, magic: 0);
        Assert.Null(CrashReporterNative.ReadUploadsEnabled(_path));

        File.Delete(_path);
        Assert.Null(CrashReporterNative.ReadUploadsEnabled(_path));
    }

    [Fact]
    public void WriteUploadsEnabled_FlipsOnlyThatBit()
    {
        WriteSettings(1 | 4);
        var before = File.ReadAllBytes(_path);

        Assert.True(CrashReporterNative.WriteUploadsEnabled(_path, false));

        var after = File.ReadAllBytes(_path);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(8)));
        Assert.Equal(before.AsSpan(0, 8).ToArray(), after.AsSpan(0, 8).ToArray());
        Assert.Equal(before.AsSpan(12).ToArray(), after.AsSpan(12).ToArray());

        Assert.True(CrashReporterNative.WriteUploadsEnabled(_path, true));
        Assert.Equal(5u, BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(_path).AsSpan(8)));
    }

    [Fact]
    public void WriteUploadsEnabled_LeavesAMatchingFileUntouched()
    {
        WriteSettings(0);
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_path, stamp);

        Assert.True(CrashReporterNative.WriteUploadsEnabled(_path, false));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(_path));
    }

    [Fact]
    public void WriteUploadsEnabled_RefusesOtherFiles()
    {
        WriteSettings(1, magic: 0);
        Assert.False(CrashReporterNative.WriteUploadsEnabled(_path, false));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(_path).AsSpan(8)));

        File.Delete(_path);
        Assert.False(CrashReporterNative.WriteUploadsEnabled(_path, false));
    }

    [Fact]
    public void ToCrashReport_Uploaded_HasTheServerIdAndUploadTime()
    {
        var report = new CrashReporterNative.Report { UploadTime = 1_700_000_000, State = CrashReporterNative.Uploaded };
        "server-id"u8.CopyTo(new Span<byte>(report.RemoteId, 64));

        Assert.Equal(new CrashReport(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), "server-id"), CrashReporterNative.ToCrashReport(report));
    }

    [Theory]
    [InlineData(0)] // NotUploaded
    [InlineData(1)] // Pending
    [InlineData(2)] // Pending_UserRequested
    public void ToCrashReport_NotUploaded_IsTheEpochWithNoId(int state)
    {
        var report = new CrashReporterNative.Report { UploadTime = 5, State = state };
        "stale"u8.CopyTo(new Span<byte>(report.RemoteId, 64));

        Assert.Equal(new CrashReport(DateTimeOffset.UnixEpoch, ""), CrashReporterNative.ToCrashReport(report));
    }

    [Fact]
    public void Report_HasCrashpadsLayout() => Assert.Equal(152, sizeof(CrashReporterNative.Report));
}
