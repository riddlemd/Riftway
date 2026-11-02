using System.Runtime.InteropServices;
using Riftway;

/// <summary>macOS message boxes end to end: each NSAlert is read back from AppKit and answered as a user would.</summary>
static class MacMessageBoxTest
{
    internal static async Task RunAsync(BrowserWindow win, MessageBoxTest.CheckFn check, TimeSpan step)
    {
        win.Show();
        win.Focus();
        await Task.Delay(300);
        var view = win.GetNativeWindowHandle();

        // A sheet on the window, as Electron's ShowMessageBox with a window.
        var sheet = Dialog.ShowMessageBoxAsync(win, new MessageBoxOptions
        {
            Message = "Save changes?", Detail = "They will be lost.", Type = MessageBoxType.Warning, Title = "ignored title",
            Buttons = ["Save", "Cancel", "Don't Save"], DefaultId = 0, CheckboxLabel = "Remember", CheckboxChecked = true,
        });
        var shown = Wait(() => Sheet(view), step);
        var read = shown == 0 ? null : OnMain(() => Describe(shown));
        check("a message box with a window is a sheet on it, with the message and detail and no title",
            read is { } r && r.Texts.Contains("Save changes?") && r.Texts.Contains("They will be lost.") && !r.Texts.Contains("ignored title"), read?.Texts);
        check("Return chooses the default button and Escape the detected cancel id",
            read is { } b && b.Default == "Save" && b.Buttons.FirstOrDefault(x => x.Key == "\u001b")?.Title == "Cancel",
            read is null ? null : $"default {read.Default}; " + string.Join(",", read.Buttons.Select(x => $"{x.Title}|{Key(x.Key)}")));
        check("the checkbox is AppKit's suppression button, starting as given", read?.Buttons.FirstOrDefault(x => x.Title == "Remember") is { State: 1 }, read?.Buttons.FirstOrDefault(x => x.Title == "Remember"));
        if (shown != 0)
        {
            OnMain(() => Click(shown, "Remember"));
            OnMain(() => Click(shown, "Don't Save"));
        }
        var answer = await Within(sheet, step);
        check("clicking answers that button's index and the checkbox's state", answer is { Response: 2, CheckboxChecked: false }, answer);

        // Escape's button, and an abort, answer the cancel id.
        var escaped = Dialog.ShowMessageBoxAsync(win, new MessageBoxOptions { Message = "Quit?", Buttons = ["Quit", "No"] });
        var escapeSheet = Wait(() => Sheet(view), step);
        if (escapeSheet != 0) OnMain(() => ClickKey(escapeSheet, "\u001b"));
        check("the Escape button is the one labelled No", (await Within(escaped, step))?.Response == 1, escaped.IsCompleted ? escaped.Result : null);
        using (var abort = new CancellationTokenSource())
        {
            var aborted = Dialog.ShowMessageBoxAsync(win, new MessageBoxOptions { Message = "Abort me", Buttons = ["A", "B", "C"], CancelId = 2, Signal = abort.Token });
            Wait(() => Sheet(view), step);
            abort.Cancel();
            var abortAnswer = await Within(aborted, step);
            check("aborting the signal ends the sheet with the cancel id", abortAnswer?.Response == 2 && Wait(() => Sheet(view) == 0 ? 1 : 0, step) == 1, abortAnswer);
        }

        // Without a window: runModal, and AppKit's own OK when no buttons are given.
        var alone = Dialog.ShowMessageBoxAsync(new MessageBoxOptions { Message = "Alone", Buttons = ["One", "Two"], DefaultId = 1 });
        var modal = Wait(ModalWindow, step);
        var aloneRead = modal == 0 ? null : OnMain(() => Describe(modal));
        if (modal != 0) OnMain(() => Click(modal, "Two"));
        var aloneAnswer = await Within(alone, step);
        check("without a window the box runs modally, and Return moves to the default button", aloneRead?.Default == "Two" && aloneAnswer?.Response == 1,
            aloneRead is null ? null : $"default {aloneRead.Default}; answer {aloneAnswer}");
        var plain = Dialog.ShowMessageBoxAsync(new MessageBoxOptions { Message = "No buttons" });
        var plainWindow = Wait(ModalWindow, step);
        var plainRead = plainWindow == 0 ? null : OnMain(() => Describe(plainWindow));
        if (plainWindow != 0) OnMain(() => Click(plainWindow, "OK"));
        var plainAnswer = await Within(plain, step);
        // Electron makes the same AppKit calls, so AppKit's own OK answers 0 there too.
        check("no buttons shows AppKit's OK, which answers 0", plainRead?.Buttons.Any(x => x.Title == "OK") == true && plainAnswer?.Response == 0, plainAnswer);

        // ShowMessageBoxSync: a sheet run modally on a visible window; runModal without one; an empty title shows "(empty)".
        var syncOwned = Task.Run(() => Dialog.ShowMessageBoxSync(win, new MessageBoxOptions { Message = "Sync", Buttons = ["", "Go"] }));
        var syncSheet = Wait(() => Sheet(view), step);
        var syncRead = syncSheet == 0 ? null : OnMain(() => Describe(syncSheet));
        if (syncSheet != 0) OnMain(() => Click(syncSheet, "Go"));
        check("ShowMessageBoxSync with a window is a sheet, and an empty button title shows \"(empty)\"", await Within(syncOwned, step) == 1
            && syncRead?.Buttons.Any(x => x.Title == "(empty)") == true, syncRead is null ? null : string.Join(",", syncRead.Buttons.Select(x => x.Title)));
        using (var abort = new CancellationTokenSource())
        {
            var syncAlone = Task.Run(() => Dialog.ShowMessageBoxSync(new MessageBoxOptions { Message = "Sync alone", Buttons = ["X", "Cancel"], Signal = abort.Token }));
            Wait(ModalWindow, step);
            abort.Cancel();
            check("cancelling the signal from another thread stops a Sync box with the cancel id (drift MB2)", await Within(syncAlone, step) == 1, syncAlone.IsCompleted ? syncAlone.Result : null);
        }

        // ShowErrorBox: a critical alert with the title and content, run modally.
        var error = Task.Run(() => Dialog.ShowErrorBox("Broken", "Details here"));
        var errorWindow = Wait(ModalWindow, step);
        var errorRead = errorWindow == 0 ? null : OnMain(() => Describe(errorWindow));
        if (errorWindow != 0) OnMain(() => Click(errorWindow, "OK"));
        await Within(error, step);
        check("ShowErrorBox shows the title and content, with AppKit's OK", errorRead is { } e && e.Texts.Contains("Broken") && e.Texts.Contains("Details here")
            && error.IsCompletedSuccessfully, errorRead?.Texts);
    }

    private sealed record ButtonInfo(string Title, string Key, nint State);

    private sealed record AlertRead(IReadOnlyList<string> Texts, IReadOnlyList<ButtonInfo> Buttons, string Default);

    private static string Key(string key) => key switch { "\r" => "return", "\u001b" => "escape", _ => key };

    private static T OnMain<T>(Func<T> func) => MacMain.Invoke(func, TimeSpan.FromSeconds(5));

    private static void OnMain(Action action) => MacMain.Invoke(() => { action(); return 0; }, TimeSpan.FromSeconds(5));

    private static nint Wait(Func<nint> find, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var found = OnMain(find);
            if (found != 0) return found;
            Thread.Sleep(100);
        }
        return 0;
    }

    private static async Task<T?> Within<T>(Task<T> task, TimeSpan timeout) => await Task.WhenAny(task, Task.Delay(timeout)) == task ? await task : default;

    private static async Task Within(Task task, TimeSpan timeout) => await Task.WhenAny(task, Task.Delay(timeout));

    private static nint Sheet(nint contentView) => Send(Send(contentView, "window"), "attachedSheet");

    private static nint ModalWindow() => Send(Send(Class("NSApplication"), "sharedApplication"), "modalWindow");

    private static AlertRead Describe(nint window)
    {
        var texts = new List<string>();
        var buttons = new List<ButtonInfo>();
        void Walk(nint view)
        {
            if (IsKind(view, "NSButton")) buttons.Add(new ButtonInfo(Text(Send(view, "title")), Text(Send(view, "keyEquivalent")), Send(view, "state")));
            else if (IsKind(view, "NSTextField")) texts.Add(Text(Send(view, "stringValue")));
            var subviews = Send(view, "subviews");
            for (nint i = 0, n = Send(subviews, "count"); i < n; i++) Walk(SendArg(subviews, "objectAtIndex:", i));
        }
        Walk(Send(window, "contentView"));
        texts.Add(Text(Send(window, "title")));
        var defaultCell = Send(window, "defaultButtonCell");
        return new AlertRead(texts, buttons, defaultCell == 0 ? "" : Text(Send(defaultCell, "title")));
    }

    private static void Click(nint window, string title) => ClickWhere(window, b => Text(Send(b, "title")) == title);

    private static void ClickKey(nint window, string key) => ClickWhere(window, b => Text(Send(b, "keyEquivalent")) == key);

    private static void ClickWhere(nint window, Func<nint, bool> match)
    {
        nint Find(nint view)
        {
            if (IsKind(view, "NSButton") && match(view)) return view;
            var subviews = Send(view, "subviews");
            for (nint i = 0, n = Send(subviews, "count"); i < n; i++)
                if (Find(SendArg(subviews, "objectAtIndex:", i)) is var found and not 0) return found;
            return 0;
        }
        var button = Find(Send(window, "contentView"));
        if (button != 0) SendArg(button, "performClick:", 0);
    }

    private static bool IsKind(nint obj, string className) => (byte)SendArg(obj, "isKindOfClass:", Class(className)) != 0;

    private static string Text(nint nsString) => nsString == 0 ? "" : Marshal.PtrToStringUTF8(Send(nsString, "UTF8String")) ?? "";

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libobjc.A.dylib"), "objc_msgSend");

    private static unsafe nint Send(nint receiver, string selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, Sel(selector));

    private static unsafe nint SendArg(nint receiver, string selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, Sel(selector), arg);

    private static nint Class(string name) => objc_getClass(name);

    private static nint Sel(string name) => sel_registerName(name);

    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern nint objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib")] private static extern nint sel_registerName(string name);
}
