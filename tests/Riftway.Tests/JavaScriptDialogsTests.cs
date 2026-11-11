using Riftway.Cef;
using Riftway.Cef.Handlers;

namespace Riftway.Tests;

// Electron's -run-dialog handler (lib/browser/api/web-contents.ts): alert/confirm boxes, prompt unsupported, safeDialogs.
public sealed class JavaScriptDialogsTests
{
    private sealed class Boxes
    {
        public List<(MessageBoxOptions Options, TaskCompletionSource<MessageBoxResult> Result)> Shown { get; } = [];
        public bool Destroyed { get; set; }

        public JavaScriptDialogs Dialogs() => new(o =>
        {
            var tcs = new TaskCompletionSource<MessageBoxResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Shown.Add((o, tcs));
            return tcs.Task;
        }, () => Destroyed);
    }

    private sealed class Answers
    {
        private readonly TaskCompletionSource<bool?> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public void Answer(bool? ok) { Interlocked.Increment(ref Calls); _tcs.TrySetResult(ok); }
        public Task<bool?> Task => _tcs.Task;
    }

    private static readonly WebPreferences Plain = new();
    private static readonly WebPreferences Safe = new() { SafeDialogs = true };

    [Fact]
    public async Task Alert_IsAnOkBoxWithNoDefaultButton_AndAnswersTrue()
    {
        var boxes = new Boxes();
        var answers = new Answers();
        Assert.True(boxes.Dialogs().Run("https://a.test", JsDialogKind.Alert, "hi", Plain, answers.Answer));
        var (options, result) = Assert.Single(boxes.Shown);
        Assert.Equal(["OK"], options.Buttons);
        Assert.Equal(("hi", -1, 0, ""), (options.Message, options.DefaultId, options.CancelId!.Value, options.CheckboxLabel));
        result.SetResult(new MessageBoxResult(0, false));
        Assert.True(await answers.Task);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task Confirm_IsOkCancel_OkDefault_CancelCancels(int response, bool ok)
    {
        var boxes = new Boxes();
        var answers = new Answers();
        Assert.True(boxes.Dialogs().Run("https://a.test", JsDialogKind.Confirm, "sure?", Plain, answers.Answer));
        var (options, result) = Assert.Single(boxes.Shown);
        Assert.Equal(["OK", "Cancel"], options.Buttons);
        Assert.Equal((0, 1), (options.DefaultId, options.CancelId!.Value));
        result.SetResult(new MessageBoxResult(response, false));
        Assert.Equal(ok, await answers.Task);
    }

    [Fact]
    public void Prompt_IsNotSupported_AndShowsNothing()
    {
        var boxes = new Boxes();
        Assert.False(boxes.Dialogs().Run("https://a.test", JsDialogKind.Prompt, "name?", Plain, _ => Assert.Fail("answered")));
        Assert.Empty(boxes.Shown);
    }

    [Fact]
    public void DisableDialogs_ShowsNothing()
    {
        var boxes = new Boxes();
        Assert.False(boxes.Dialogs().Run("https://a.test", JsDialogKind.Alert, "hi", new WebPreferences { DisableDialogs = true }, _ => Assert.Fail("answered")));
        Assert.Empty(boxes.Shown);
    }

    [Fact]
    public void SafeDialogs_OffersTheCheckboxFromTheOriginsSecondDialog()
    {
        var boxes = new Boxes();
        var dialogs = boxes.Dialogs();
        dialogs.Run("https://a.test", JsDialogKind.Alert, "1", Safe, _ => { });
        dialogs.Run("https://a.test", JsDialogKind.Alert, "2", Safe, _ => { });
        dialogs.Run("https://b.test", JsDialogKind.Confirm, "b1", Safe, _ => { });
        Assert.Equal(["", JavaScriptDialogs.DefaultSafeDialogsMessage, ""], boxes.Shown.Select(s => s.Options.CheckboxLabel));
        Assert.Equal("Prevent this app from creating additional dialogs", JavaScriptDialogs.DefaultSafeDialogsMessage);
    }

    [Fact]
    public void SafeDialogsMessage_ReplacesTheLabel_AndEmptyKeepsElectrons()
    {
        var boxes = new Boxes();
        var dialogs = boxes.Dialogs();
        var custom = new WebPreferences { SafeDialogs = true, SafeDialogsMessage = "Stop it" };
        dialogs.Run("https://a.test", JsDialogKind.Alert, "1", custom, _ => { });
        dialogs.Run("https://a.test", JsDialogKind.Alert, "2", custom, _ => { });
        dialogs.Run("https://a.test", JsDialogKind.Alert, "3", custom with { SafeDialogsMessage = "" }, _ => { });
        Assert.Equal(["", "Stop it", JavaScriptDialogs.DefaultSafeDialogsMessage], boxes.Shown.Select(s => s.Options.CheckboxLabel));
    }

    [Fact]
    public void WithoutSafeDialogs_ThereIsNeverACheckbox_EvenWithAMessage()
    {
        var boxes = new Boxes();
        var dialogs = boxes.Dialogs();
        var prefs = new WebPreferences { SafeDialogsMessage = "Stop it" };
        for (var i = 0; i < 3; i++) dialogs.Run("https://a.test", JsDialogKind.Alert, "x", prefs, _ => { });
        Assert.All(boxes.Shown, s => Assert.Equal("", s.Options.CheckboxLabel));
    }

    [Fact]
    public async Task TickingTheCheckbox_StopsThatOriginOnly_ForGood()
    {
        var boxes = new Boxes();
        var dialogs = boxes.Dialogs();
        dialogs.Run("https://a.test", JsDialogKind.Alert, "1", Safe, _ => { });
        var answers = new Answers();
        dialogs.Run("https://a.test", JsDialogKind.Confirm, "2", Safe, answers.Answer);
        boxes.Shown[1].Result.SetResult(new MessageBoxResult(0, true));
        Assert.True(await answers.Task);
        Assert.False(dialogs.Run("https://a.test", JsDialogKind.Alert, "3", Safe, _ => Assert.Fail("answered")));
        Assert.False(dialogs.Run("https://a.test", JsDialogKind.Confirm, "4", Plain, _ => Assert.Fail("answered")));
        Assert.True(dialogs.Run("https://b.test", JsDialogKind.Alert, "b", Safe, _ => { }));
        Assert.Equal(3, boxes.Shown.Count);
    }

    [Fact]
    public async Task CancelAll_ClosesOpenBoxesUnanswered_AndKeepsTheCounts()
    {
        var boxes = new Boxes();
        var dialogs = boxes.Dialogs();
        var answers = new Answers();
        dialogs.Run("https://a.test", JsDialogKind.Confirm, "1", Safe, answers.Answer);
        var signal = boxes.Shown[0].Options.Signal;
        Assert.False(signal.IsCancellationRequested);
        dialogs.CancelAll();
        Assert.True(signal.IsCancellationRequested);
        // The box then closes with its cancel id, which must not reach the page.
        boxes.Shown[0].Result.SetResult(new MessageBoxResult(1, false));
        Assert.Null(await answers.Task);
        dialogs.Run("https://a.test", JsDialogKind.Alert, "2", Safe, _ => { });
        Assert.Equal(JavaScriptDialogs.DefaultSafeDialogsMessage, boxes.Shown[1].Options.CheckboxLabel);
    }

    [Fact]
    public async Task APageDestroyedWhileTheBoxIsOpen_GetsNoAnswer()
    {
        var boxes = new Boxes();
        var answers = new Answers();
        boxes.Dialogs().Run("https://a.test", JsDialogKind.Confirm, "1", Plain, answers.Answer);
        boxes.Destroyed = true;
        boxes.Shown[0].Result.SetResult(new MessageBoxResult(0, true));
        Assert.Null(await answers.Task);
        Assert.Equal(1, answers.Calls);
    }

    [Fact]
    public async Task ABoxThatFailsToShow_DropsTheDialog()
    {
        var answers = new Answers();
        var dialogs = new JavaScriptDialogs(_ => Task.FromException<MessageBoxResult>(new InvalidOperationException("no UI")), () => false);
        Assert.True(dialogs.Run("https://a.test", JsDialogKind.Alert, "1", Plain, answers.Answer));
        Assert.Null(await answers.Task);
    }

    [Theory]
    [InlineData("https://a.test/", "https://a.test/page", "https://a.test")]
    [InlineData("http://localhost:5000/", null, "http://localhost:5000")]
    [InlineData("file:///", "file:///C:/app/index.html", "file:///C:/app/index.html")]
    [InlineData("file:///", null, "file:///")]
    [InlineData("app://localhost/", "app://localhost/x", "app://localhost")]
    public void Origin_IsTheOriginWithoutAPath_OrAFilePagesUrl(string originUrl, string? frameUrl, string expected)
        => Assert.Equal(expected, JavaScriptDialogs.Origin(originUrl, frameUrl));
}
