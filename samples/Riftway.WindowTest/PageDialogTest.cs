using System.Text.Json.Nodes;
using Riftway;

/// <summary>A page's own alert, confirm and prompt as Electron shows them: Riftway's message box, modal to the page's
/// window, with the safeDialogs checkbox from an origin's second dialog. Windows: each box is read back from the live task
/// dialog and answered as a user would.</summary>
static class PageDialogTest
{
    internal static async Task RunAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("riftway-page-dialogs");
        var page = Path.Combine(dir.FullName, "page.html");
        var other = Path.Combine(dir.FullName, "other.html");
        File.WriteAllText(page, "<!doctype html><title>page dialogs</title><p>page</p>");
        File.WriteAllText(other, "<!doctype html><title>other</title><p>other</p>");
        var win = new BrowserWindow(new BrowserWindowOptions
        {
            Title = "Riftway window test: page dialogs",
            Width = 420,
            Height = 300,
            WebPreferences = new WebPreferences { SafeDialogs = true, SafeDialogsMessage = "Riftway stop dialogs" },
        });
        try
        {
            await win.WebContents.LoadUrl(new Uri(page).AbsoluteUri);
            var owner = win.GetNativeWindowHandle();

            var first = win.WebContents.ExecuteJavaScriptAsync("alert('page alert one'), 'after alert'", userGesture: true);
            var h = WaitForContent("page alert one", step);
            var els = h == 0 ? [] : WinTaskDialog.Elements(h);
            check("page dialogs: alert opens Riftway's message box with the page's text", h != 0, Describe(els));
            check("page dialogs: alert's box has only OK, no checkbox the first time",
                WinTaskDialog.Buttons(h).Select(b => WinTaskDialog.Plain(b.Text)).SequenceEqual(["OK"]) && WinTaskDialog.IsChecked(h) is null,
                string.Join(",", WinTaskDialog.Buttons(h).Select(b => b.Text)));
            check("page dialogs: the box is owned by the page's window, which it disables",
                WinTaskDialog.Owner(h) == owner && !WinTaskDialog.IsEnabled(owner), WinTaskDialog.Owner(h));
            Click(h, "OK");
            check("page dialogs: OK lets alert return", Text(await Within(first, step)) == "after alert", Text(first.IsCompleted ? first.Result : null));

            var second = win.WebContents.ExecuteJavaScriptAsync("confirm('page confirm two')", userGesture: true);
            h = WaitForContent("page confirm two", step);
            els = h == 0 ? [] : WinTaskDialog.Elements(h);
            check("page dialogs: confirm is OK and Cancel, OK the default",
                WinTaskDialog.Buttons(h).Select(b => WinTaskDialog.Plain(b.Text)).SequenceEqual(["OK", "Cancel"])
                && WinTaskDialog.Buttons(h).Where(WinTaskDialog.IsDefault).Select(b => WinTaskDialog.Plain(b.Text)).SingleOrDefault() == "OK",
                string.Join(",", WinTaskDialog.Buttons(h).Select(b => $"{b.Text}:{b.Style:X}")));
            check("page dialogs: the origin's second dialog offers SafeDialogsMessage's checkbox, unchecked",
                els.Any(e => e.AutomationId == "VerificationCheckBox" && e.Name == "Riftway stop dialogs") && WinTaskDialog.IsChecked(h) == false, Describe(els));
            Click(h, "Cancel");
            check("page dialogs: Cancel makes confirm false", Text(await Within(second, step)) == "false", Text(second.IsCompleted ? second.Result : null));

            var third = win.WebContents.ExecuteJavaScriptAsync("confirm('page confirm three')", userGesture: true);
            h = WaitForContent("page confirm three", step);
            WinTaskDialog.ToggleCheckbox(h);
            Eventually(() => WinTaskDialog.IsChecked(h) == true, step);
            Click(h, "OK");
            check("page dialogs: OK with the checkbox ticked makes confirm true", Text(await Within(third, step)) == "true", Text(third.IsCompleted ? third.Result : null));

            var blocked = await Within(win.WebContents.ExecuteJavaScriptAsync("[alert('page blocked'), confirm('page blocked')].join()", userGesture: true), step);
            check("page dialogs: once ticked, the origin gets no more boxes (alert returns, confirm false)",
                Text(blocked) == ",false" && WaitForContent("page blocked", TimeSpan.FromMilliseconds(700)) == 0, Text(blocked));

            // prompt is unsupported, as in Electron: null at once, no box, on a page whose origin is not blocked.
            await win.WebContents.LoadUrl(new Uri(other).AbsoluteUri);
            var prompted = await Within(win.WebContents.ExecuteJavaScriptAsync("String(prompt('page prompt', 'x'))", userGesture: true), step);
            check("page dialogs: prompt returns null without a box", Text(prompted) == "null" && WaitForContent("page prompt", TimeSpan.FromMilliseconds(700)) == 0, Text(prompted));

            // Closing the page cancels its open box unanswered (Chromium's CancelDialogs, Electron's -cancel-dialogs).
            var closing = new BrowserWindow(new BrowserWindowOptions { Title = "Riftway window test: page dialog closing", Width = 300, Height = 200 });
            await closing.WebContents.LoadUrl(new Uri(other).AbsoluteUri);
            var closingOwner = closing.GetNativeWindowHandle();
            _ = closing.WebContents.ExecuteJavaScriptAsync("alert('page left open')", userGesture: true);
            h = WaitForContent("page left open", step);
            check("page dialogs: another window's page gets its own box (no checkbox: its own first dialog)", h != 0 && WinTaskDialog.Owner(h) == closingOwner && WinTaskDialog.IsChecked(h) is null, h);
            closing.Destroy();
            check("page dialogs: destroying the page's window closes its open box", h != 0 && Eventually(() => !WinTaskDialog.Exists(h), step), h);
        }
        finally
        {
            WinTaskDialog.CloseAll();
            win.Destroy();
            try { dir.Delete(true); } catch (IOException) { }
        }
    }

    private static string? Text(JsonNode? node) => node?.ToJsonString().Trim('"');

    // This process's visible task dialog whose content is exactly text, once its buttons are there; 0 if none in time.
    private static nint WaitForContent(string text, TimeSpan timeout)
    {
        nint found = 0;
        Eventually(() =>
        {
            foreach (var h in WinTaskDialog.All())
                if (WinTaskDialog.Buttons(h).Count > 0 && WinTaskDialog.Elements(h).Any(e => e.AutomationId == "ContentText" && e.Name.TrimEnd('\r', '\n') == text))
                {
                    found = h;
                    return true;
                }
            return false;
        }, timeout);
        if (found != 0) Thread.Sleep(200);
        return found;
    }

    private static void Click(nint dialog, string label)
    {
        if (WinTaskDialog.Buttons(dialog).FirstOrDefault(b => WinTaskDialog.Plain(b.Text) == label) is { Hwnd: not 0 } button)
            WinTaskDialog.Click(dialog, button.Hwnd);
    }

    private static string Describe(List<WinTaskDialog.Element> elements)
        => string.Join("; ", elements.Where(e => e.AutomationId.Length > 0).Select(e => $"{e.AutomationId}={e.Name}"));

    private static async Task<JsonNode?> Within(Task<JsonNode?> task, TimeSpan step)
        => await Task.WhenAny(task, Task.Delay(step)) == task ? task.Result : null;

    private static bool Eventually(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }
}
