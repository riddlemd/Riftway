using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Oriel.Cef;
using Oriel.Cef.Handlers;
using Oriel.Cef.Interop;
using Reason = Oriel.Cef.Interop.cef_download_interrupt_reason_t;

namespace Oriel.Tests;

public sealed unsafe class DownloadItemTests
{
    private const string Url = "https://oriel.test/report.txt";

    private static DownloadSnapshot Snapshot(string url = Url, bool complete = false, bool canceled = false, bool interrupted = false,
        bool paused = false, Reason reason = Reason.CEF_DOWNLOAD_INTERRUPT_REASON_NONE, string suggested = "report.txt")
        => new(7, url, url, suggested, "attachment", "text/plain", "", 100, 10, 5, 10, 0, 0,
            !complete && !canceled && !interrupted, complete, canceled, interrupted, paused, reason);

    // Records what the item asks of CEF's download callback.
    private sealed class Calls
    {
        public List<string> Made { get; } = [];
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCancel(cef_download_item_callback_t* self) => NativeObject.Owner<Calls>(self).Made.Add("cancel");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPause(cef_download_item_callback_t* self) => NativeObject.Owner<Calls>(self).Made.Add("pause");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnResume(cef_download_item_callback_t* self) => NativeObject.Owner<Calls>(self).Made.Add("resume");

    private static DownloadControl Control(Calls calls)
    {
        var native = NativeObject.Create<cef_download_item_callback_t>(calls);
        native->cancel = &OnCancel;
        native->pause = &OnPause;
        native->resume = &OnResume;
        return new DownloadControl(native);
    }

    private static (DownloadItem Item, List<(string Event, DownloadState State)> Events) Item(DownloadSnapshot? first = null)
    {
        var item = new DownloadItem(first ?? Snapshot(), new BeforeDownload(null));
        var events = new List<(string, DownloadState)>();
        item.Updated += (_, e) => events.Add(("updated", e.State));
        item.Done += (_, e) => events.Add(("done", e.State));
        return (item, events);
    }

    [Theory]
    [InlineData(Reason.CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_TIMEOUT, false, "ImmediateContinue")]
    [InlineData(Reason.CEF_DOWNLOAD_INTERRUPT_REASON_FILE_TRANSIENT_ERROR, false, "ImmediateContinue")]
    [InlineData(Reason.CEF_DOWNLOAD_INTERRUPT_REASON_SERVER_CONTENT_LENGTH_MISMATCH, true, "UserContinue")]
    [InlineData(Reason.CEF_DOWNLOAD_INTERRUPT_REASON_SERVER_NO_RANGE, false, "ImmediateRestart")]
    [InlineData(Reason.CEF_DOWNLOAD_INTERRUPT_REASON_FILE_TOO_SHORT, true, "UserRestart")]
    [InlineData(Reason.CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_FAILED, false, "UserContinue")]
    [InlineData(Reason.CEF_DOWNLOAD_INTERRUPT_REASON_CRASH, false, "UserContinue")]
    [InlineData(Reason.CEF_DOWNLOAD_INTERRUPT_REASON_FILE_NO_SPACE, false, "UserContinue")]
    [InlineData(Reason.CEF_DOWNLOAD_INTERRUPT_REASON_FILE_ACCESS_DENIED, false, "UserRestart")]
    [InlineData(Reason.CEF_DOWNLOAD_INTERRUPT_REASON_SERVER_FORBIDDEN, false, "Invalid")]
    [InlineData(Reason.CEF_DOWNLOAD_INTERRUPT_REASON_USER_CANCELED, true, "Invalid")]
    [InlineData(Reason.CEF_DOWNLOAD_INTERRUPT_REASON_NONE, false, "Invalid")]
    public void ResumeModeOf_FollowsChromiumsTable(Reason reason, bool userActionRequired, string expected)
        => Assert.Equal(Enum.Parse<DownloadResumeMode>(expected), DownloadRules.ResumeModeOf(Url, reason, false, userActionRequired));

    [Fact]
    public void ResumeModeOf_RestartRequiredTurnsAContinueIntoARestart()
    {
        Assert.Equal(DownloadResumeMode.ImmediateRestart, DownloadRules.ResumeModeOf(Url, Reason.CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_TIMEOUT, true, false));
        Assert.Equal(DownloadResumeMode.UserRestart, DownloadRules.ResumeModeOf(Url, Reason.CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_FAILED, true, false));
    }

    [Theory]
    [InlineData("http://oriel.test/a")]
    [InlineData("https://oriel.test/a")]
    public void ResumeModeOf_HttpAndHttpsResume(string url)
        => Assert.Equal(DownloadResumeMode.UserContinue, DownloadRules.ResumeModeOf(url, Reason.CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_FAILED, false, false));

    [Theory]
    [InlineData("file:///tmp/a")]
    [InlineData("data:text/plain,a")]
    [InlineData("httpx://oriel.test/a")]
    public void ResumeModeOf_OtherSchemesNeverResume(string url)
        => Assert.Equal(DownloadResumeMode.Invalid, DownloadRules.ResumeModeOf(url, Reason.CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_FAILED, false, false));

    [Fact]
    public void IsDone_CompletedAndCancelledAre_ProgressingIsNot()
    {
        Assert.True(DownloadRules.IsDone(Snapshot(complete: true)));
        Assert.True(DownloadRules.IsDone(Snapshot(canceled: true)));
        Assert.False(DownloadRules.IsDone(Snapshot()));
    }

    [Fact]
    public void IsDone_AnInterruptionIsDoneOnlyWhenItCannotResume()
    {
        Assert.False(DownloadRules.IsDone(Snapshot(interrupted: true, reason: Reason.CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_FAILED)));
        Assert.False(DownloadRules.IsDone(Snapshot(interrupted: true, reason: Reason.CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_TIMEOUT)));
        Assert.True(DownloadRules.IsDone(Snapshot(interrupted: true, reason: Reason.CEF_DOWNLOAD_INTERRUPT_REASON_SERVER_FORBIDDEN)));
        Assert.True(DownloadRules.IsDone(Snapshot("file:///tmp/a", interrupted: true, reason: Reason.CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_FAILED)));
    }

    [Fact]
    public void CanResume_ARunningDownloadOnlyWhilePaused()
    {
        Assert.True(DownloadRules.CanResume(Snapshot(paused: true)));
        Assert.False(DownloadRules.CanResume(Snapshot()));
        Assert.False(DownloadRules.CanResume(Snapshot(complete: true, paused: true)));
        Assert.False(DownloadRules.CanResume(Snapshot(canceled: true, paused: true)));
    }

    [Fact]
    public void CanResume_AnInterruptionOnlyWhenTheUserMustResumeIt()
    {
        Assert.True(DownloadRules.CanResume(Snapshot(interrupted: true, reason: Reason.CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_FAILED)));
        Assert.True(DownloadRules.CanResume(Snapshot(interrupted: true, reason: Reason.CEF_DOWNLOAD_INTERRUPT_REASON_FILE_FAILED)));
        // Chromium resumes these itself, unless the user paused the download.
        Assert.False(DownloadRules.CanResume(Snapshot(interrupted: true, reason: Reason.CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_TIMEOUT)));
        Assert.True(DownloadRules.CanResume(Snapshot(interrupted: true, paused: true, reason: Reason.CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_TIMEOUT)));
        Assert.False(DownloadRules.CanResume(Snapshot(interrupted: true, paused: true, reason: Reason.CEF_DOWNLOAD_INTERRUPT_REASON_SERVER_FORBIDDEN)));
    }

    [Fact]
    public void OnUpdated_AResumableInterruptionIsAnUpdate_ThenDoneComesOnceAtTheEnd()
    {
        var (item, events) = Item();
        item.SetSavePath("/tmp/report.txt");
        item.Start(false, null);
        item.OnUpdated(Snapshot(interrupted: true, reason: Reason.CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_FAILED), Control(new Calls()));
        Assert.True(item.CanResume());
        item.OnUpdated(Snapshot(), Control(new Calls()));
        item.OnUpdated(Snapshot(complete: true), Control(new Calls()));
        Assert.Equal([("updated", DownloadState.Interrupted), ("updated", DownloadState.Progressing), ("done", DownloadState.Completed)], events);
    }

    [Fact]
    public void OnUpdated_AnInterruptionThatCannotResumeIsDone()
    {
        var events = new List<(string, DownloadState)>();
        var finished = 0;
        var item = new DownloadItem(Snapshot(), new BeforeDownload(null)) { Finished = _ => finished++ };
        item.Done += (_, e) => events.Add(("done", e.State));
        item.Updated += (_, e) => events.Add(("updated", e.State));
        item.SetSavePath("/tmp/report.txt");
        item.Start(false, null);
        item.OnUpdated(Snapshot(interrupted: true, reason: Reason.CEF_DOWNLOAD_INTERRUPT_REASON_SERVER_FORBIDDEN), Control(new Calls()));
        Assert.Equal([("done", DownloadState.Interrupted)], events);
        Assert.Equal(1, finished);
    }

    [Fact]
    public void Resume_OfAnInterruptedDownload_GoesToCef()
    {
        var (item, _) = Item();
        item.SetSavePath("/tmp/report.txt");
        item.Start(false, null);
        var calls = new Calls();
        item.OnUpdated(Snapshot(interrupted: true, reason: Reason.CEF_DOWNLOAD_INTERRUPT_REASON_NETWORK_FAILED), Control(calls));
        item.RequestPause(false);
        Assert.Equal(["resume"], calls.Made);
    }

    [Fact]
    public void PauseAndCancel_AfterTheDecision_GoStraightToCef()
    {
        var (item, _) = Item();
        item.SetSavePath("/tmp/report.txt");
        item.Start(false, null);
        var calls = new Calls();
        item.OnUpdated(Snapshot(), Control(calls));
        item.RequestPause(true);
        item.RequestCancel();
        Assert.Equal(["pause", "cancel"], calls.Made);
    }

    [Fact]
    public void Pause_DuringWillDownload_WaitsForTheDecision_ThenIsAskedOnce()
    {
        var (item, _) = Item();
        var early = new Calls();
        item.Adopt(Control(early));
        item.SetSavePath("/tmp/report.txt");
        item.RequestPause(true);
        Assert.Empty(early.Made);
        item.Start(false, null);
        var first = new Calls();
        item.OnUpdated(Snapshot(), Control(first));
        var second = new Calls();
        item.OnUpdated(Snapshot(), Control(second));
        Assert.Equal(["pause"], first.Made);
        Assert.Empty(second.Made);
    }

    [Fact]
    public void Pause_AfterTheDecision_UsesTheControlCefGaveBeforeWillDownload()
    {
        var (item, _) = Item();
        var early = new Calls();
        item.Adopt(Control(early));
        item.SetSavePath("/tmp/report.txt");
        item.Start(false, null);
        item.RequestPause(true);
        Assert.Equal(["pause"], early.Made);
    }

    [Fact]
    public void Cancel_BeforeCefGivesAControl_IsAskedOnTheFirstReport()
    {
        var (item, _) = Item();
        item.SetSavePath("/tmp/report.txt");
        item.Start(false, null);
        item.RequestCancel();
        var calls = new Calls();
        item.OnUpdated(Snapshot(), Control(calls));
        Assert.Equal(["cancel"], calls.Made);
    }

    [Fact]
    public void Cancel_DuringWillDownload_DoesNotTouchCefsControl()
    {
        var (item, _) = Item();
        var early = new Calls();
        item.Adopt(Control(early));
        item.RequestCancel();
        item.Start(false, null);
        Assert.Empty(early.Made);
    }

    [Fact]
    public void GetFilename_KeepsTheGeneratedName_WhateverThePathOrLaterReports()
    {
        var (item, _) = Item(Snapshot(suggested: "report.txt"));
        item.SetSavePath("/tmp/elsewhere.bin");
        item.Start(false, null);
        item.OnUpdated(Snapshot(suggested: ""), Control(new Calls()));
        Assert.Equal("report.txt", item.GetFilename());
    }

    [Fact]
    public void SavePath_IsEmptyUntilSet_AndKeepsWhatIsSetAfterwards()
    {
        var (item, _) = Item();
        Assert.Equal("", item.GetSavePath());
        item.SavePath = "/tmp/a.txt";
        Assert.Equal("/tmp/a.txt", item.GetSavePath());
        item.Start(false, null);
        item.SetSavePath("/tmp/b.txt");
        Assert.Equal("/tmp/b.txt", item.SavePath);
    }

    [Fact]
    public void ValidatorsGestureAndInitiator_AreBlankWithStockCef()
    {
        var (item, _) = Item();
        Assert.False(item.HasUserGesture());
        Assert.Equal("", item.GetLastModifiedTime());
        Assert.Equal("", item.GetETag());
        Assert.Equal("", item.GetInitiatorOrigin());
    }

    [Fact]
    public void ValidatorsGestureAndInitiator_ComeFromThePatchedFields()
    {
        var extras = new DownloadItemExtras(true, "Wed, 21 Oct 2015 07:28:00 GMT", "\"33a64df5\"", "https://oriel.test", null);
        var (item, _) = Item(Snapshot() with { Extras = extras });
        Assert.True(item.HasUserGesture());
        Assert.Equal("Wed, 21 Oct 2015 07:28:00 GMT", item.GetLastModifiedTime());
        Assert.Equal("\"33a64df5\"", item.GetETag());
        Assert.Equal("https://oriel.test", item.GetInitiatorOrigin());
    }

    [Fact]
    public void Validators_FollowTheLatestReport()
    {
        var (item, _) = Item();
        item.OnUpdated(Snapshot() with { Extras = new DownloadItemExtras(false, "", "\"v2\"", "", null) }, Control(new Calls()));
        Assert.Equal("\"v2\"", item.GetETag());
    }

    private static readonly List<(int Process, string Token)> s_lookups = [];

    private static WebFrameMain? RecordLookup(int process, string token)
    {
        s_lookups.Add((process, token));
        return null;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("A-0123456789abcdef0123456789ABCDEF")]
    public void DownloadFrame_LooksNothingUpWithoutAFrameIdentifier(string? identifier)
    {
        s_lookups.Clear();
        Assert.Null(Session.DownloadFrame(identifier, RecordLookup));
        Assert.Empty(s_lookups);
    }

    [Fact]
    public void DownloadFrame_LooksUpTheIdentifiersProcessAndToken()
    {
        s_lookups.Clear();
        Session.DownloadFrame("1F-0123456789ABCDEF0123456789ABCDEF", RecordLookup);
        Assert.Equal([(31, "0123456789ABCDEF0123456789ABCDEF")], s_lookups);
    }

    [Fact]
    public void DialogOptionsFor_TitlesWithTheUrl_AndStartsAtTheGeneratedPath()
    {
        var options = DownloadRules.DialogOptionsFor(new SaveDialogOptions(), Url, "/dl/report.txt", false, _ => null);
        Assert.Equal(Url, options.Title);
        Assert.Equal("/dl/report.txt", options.DefaultPath);
        Assert.Empty(options.Filters);
    }

    [Fact]
    public void DialogOptionsFor_KeepsTheAppsTitlePathAndFilters()
    {
        var filters = new[] { new FileFilter("Mine", "x") };
        var options = DownloadRules.DialogOptionsFor(new SaveDialogOptions { Title = "Save it", DefaultPath = "/a/b.bin", Filters = filters },
            Url, "/dl/report.txt", true, _ => "Binary");
        Assert.Equal("Save it", options.Title);
        Assert.Equal("/a/b.bin", options.DefaultPath);
        Assert.Same(filters, options.Filters);
    }

    [Fact]
    public void DialogOptionsFor_Windows_FiltersOnTheDefaultPathsExtension()
    {
        string? asked = null;
        var options = DownloadRules.DialogOptionsFor(new SaveDialogOptions { DefaultPath = @"C:\a\b.pdf" }, Url, "", true, e => { asked = e; return "PDF Document"; });
        Assert.Equal(".pdf", asked);
        Assert.Equal([new FileFilter("PDF Document", "pdf"), new FileFilter("All Files", "*")], options.Filters, FilterComparer.Instance);
    }

    [Fact]
    public void DialogOptionsFor_Windows_NoExtensionNoFilters()
        => Assert.Empty(DownloadRules.DialogOptionsFor(new SaveDialogOptions(), Url, @"C:\dl\README", true, _ => "x").Filters);

    [Theory]
    [InlineData(null, "TXT File")]
    [InlineData("", "*.txt")]
    [InlineData("Text Document", "Text Document")]
    public void FiltersFor_DescribesFromTheRegistry_ElseTheExtension(string? registry, string description)
        => Assert.Equal([new FileFilter(description, "txt"), new FileFilter("All Files", "*")], DownloadRules.FiltersFor(".txt", _ => registry), FilterComparer.Instance);

    [Theory]
    [InlineData("/a/b.tar.gz", ".gz")]
    [InlineData("/a/b", "")]
    [InlineData("/a.d/b", "")]
    [InlineData(@"C:\a.d\b", "")]
    [InlineData(@"C:\a\b.TXT", ".TXT")]
    [InlineData("/a/.bashrc", ".bashrc")]
    [InlineData("/a/b.", ".")]
    [InlineData("/a/..", "")]
    public void FinalExtension_IsFromTheBaseNamesLastDot(string path, string extension)
        => Assert.Equal(extension, DownloadRules.FinalExtension(path));

    private sealed class FilterComparer : IEqualityComparer<FileFilter>
    {
        public static readonly FilterComparer Instance = new();
        public bool Equals(FileFilter? x, FileFilter? y) => x!.Name == y!.Name && x.Extensions.SequenceEqual(y.Extensions);
        public int GetHashCode(FileFilter obj) => obj.Name.GetHashCode();
    }
}
