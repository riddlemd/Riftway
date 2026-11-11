using Riftway;
using Riftway.Cef;

/// <summary>Focus on navigation as Electron's ReadyToCommitNavigation decides it: a window's own page takes focus when its
/// window is active; a WebContentsView's page and an inactive window's page do not. Focus is read from the view
/// (Chromium's HasFocus), so a page that lost focus to another view of its window reads as unfocused.</summary>
static class FocusNavigationTest
{
    internal static async Task RunAsync(MessageBoxTest.CheckFn check, TimeSpan step)
    {
        // Checked on Windows; macOS and Linux activation rules are not yet run.
        if (!OperatingSystem.IsWindows()) return;
        var dir = Directory.CreateTempSubdirectory("riftway-focus-navigation");
        var count = 0;
        string Page()
        {
            var path = Path.Combine(dir.FullName, $"page{count++}.html");
            File.WriteAllText(path, "<!doctype html><title>focus</title><input>");
            return new Uri(path).AbsoluteUri;
        }
        static bool Has(WebContents page) => CefThread.InvokeOnUi(page.ViewHasFocus);

        var win = new BrowserWindow(new BrowserWindowOptions { Title = "Riftway window test: focus on navigation", Width = 420, Height = 300 });
        BrowserWindow? other = null;
        try
        {
            await win.WebContents.LoadUrl(Page());
            win.Focus();
            await Task.Delay(800);
            check("focus on navigation: focusing the window focuses its page", win.IsFocused && Has(win.WebContents));

            // As Electron's spec moves focus to DevTools: another view of the same window takes it.
            var view = new WebContentsView();
            view.SetBounds(new Rectangle(0, 0, 200, 150));
            win.ContentView.AddChildView(view);
            await view.WebContents.LoadUrl(Page());
            view.WebContents.Focus();
            await Task.Delay(600);
            check("focus on navigation: focus moves to another view of the window", Has(view.WebContents) && !Has(win.WebContents));
            await win.WebContents.LoadUrl(Page());
            await Task.Delay(800);
            check("focus on navigation: the window's page takes focus back when it navigates", Has(win.WebContents) && !Has(view.WebContents),
                $"page {Has(win.WebContents)} view {Has(view.WebContents)}");

            await view.WebContents.LoadUrl(Page());
            await Task.Delay(800);
            check("focus on navigation: a WebContentsView's navigation leaves the window's page focused", Has(win.WebContents) && !Has(view.WebContents),
                $"page {Has(win.WebContents)} view {Has(view.WebContents)}");

            other = new BrowserWindow(new BrowserWindowOptions { Title = "Riftway window test: focus on navigation, other", Width = 300, Height = 200 });
            await other.WebContents.LoadUrl(Page());
            other.Focus();
            await Task.Delay(800);
            await win.WebContents.LoadUrl(Page());
            await Task.Delay(800);
            check("focus on navigation: an inactive window's navigation does not activate it", other.IsFocused && !win.IsFocused, $"window {win.IsFocused} other {other.IsFocused}");

            var refused = false;
            try { _ = new BrowserWindow(new BrowserWindowOptions { Show = false, WebPreferences = new WebPreferences { FocusOnNavigation = false } }); }
            catch (NotSupportedException) { refused = true; }
            check("focus on navigation: a window's page refuses FocusOnNavigation = false (drift W60)", refused);
        }
        finally
        {
            other?.Destroy();
            win.Destroy();
            try { dir.Delete(true); } catch (IOException) { }
        }
    }
}
