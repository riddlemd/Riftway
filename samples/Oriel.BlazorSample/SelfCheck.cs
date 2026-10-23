using System.Text.Json.Nodes;

namespace Oriel.BlazorSample;

internal static class SelfCheck
{
    public static async Task<int> RunAsync(BrowserWindow window, SharedCounter counter)
    {
        var page = window.WebContents;
        var failures = 0;
        void Check(bool ok, string name)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
            if (!ok) failures++;
        }

        var rendered = await Poll(page, "document.querySelector('#count')?.textContent ?? ''", v => v == "0").ConfigureAwait(false);
        Check(rendered == "0", $"the component renders (#count = '{rendered}')");
        if (rendered != "0")
        {
            Console.WriteLine(await Poll(page, "location.href + ' | ' + typeof window.__orielBlazor + ' | ' + typeof window.external?.sendMessage + ' | ' + document.body.innerHTML.slice(0, 400)", _ => true).ConfigureAwait(false));
            return 1;
        }

        await page.ExecuteJavaScriptAsync("document.querySelector('#increment').click()").ConfigureAwait(false);
        var after = await Poll(page, "document.querySelector('#count')?.textContent ?? ''", v => v == "1").ConfigureAwait(false);
        Check(after == "1", $"a click reaches the component and re-renders (#count = '{after}')");

        await page.ExecuteJavaScriptAsync("document.querySelector('#interop').click()").ConfigureAwait(false);
        var title = await Poll(page, "document.title", v => v == "interop ok").ConfigureAwait(false);
        Check(title == "interop ok", $"JS interop from C# runs in the page (title = '{title}')");

        var echoed = await Poll(page, "document.querySelector('#echo')?.textContent ?? ''", v => v == "from js").ConfigureAwait(false);
        Check(echoed == "from js", $"the page calls a .NET method ([JSInvokable] echo = '{echoed}')");

        // A second window shares the app's services: one count, re-rendered in both.
        await page.ExecuteJavaScriptAsync("document.querySelector('#open').click()").ConfigureAwait(false);
        BrowserWindow? second = null;
        for (var i = 0; i < 100 && second is null; i++)
        {
            second = BrowserWindow.GetAllWindows().FirstOrDefault(w => w != window);
            if (second is null) await Task.Delay(100).ConfigureAwait(false);
        }
        Check(second is not null, "the page opens a second Blazor window");
        if (second is not null)
        {
            var other = second.WebContents;
            var seen = await Poll(other, "document.querySelector('#count')?.textContent ?? ''", v => v == "1").ConfigureAwait(false);
            Check(seen == "1", $"the second window renders the shared count (#count = '{seen}')");
            await other.ExecuteJavaScriptAsync("document.querySelector('#increment').click()").ConfigureAwait(false);
            var back = await Poll(page, "document.querySelector('#count')?.textContent ?? ''", v => v == "2").ConfigureAwait(false);
            Check(back == "2", $"a click in the second window re-renders the first (#count = '{back}')");

            var closed = new TaskCompletionSource();
            second.Closed += (_, _) => closed.TrySetResult();
            second.Close();
            await Task.WhenAny(closed.Task, Task.Delay(10_000)).ConfigureAwait(false);
            var listeners = counter.Listeners;
            for (var i = 0; i < 50 && listeners != 1; i++)
            {
                await Task.Delay(100).ConfigureAwait(false);
                listeners = counter.Listeners;
            }
            Check(listeners == 1, $"closing a window disposes its components ({listeners} listening)");
        }

        // A reload attaches a new page to the same renderer; the count survives and clicks still work.
        page.Reload();
        await Task.Delay(500).ConfigureAwait(false);
        var reloaded = await Poll(page, "document.querySelector('#count')?.textContent ?? ''", v => v == "2").ConfigureAwait(false);
        Check(reloaded == "2", $"after a reload the component renders again (#count = '{reloaded}')");
        await page.ExecuteJavaScriptAsync("document.querySelector('#increment').click()").ConfigureAwait(false);
        var afterReload = await Poll(page, "document.querySelector('#count')?.textContent ?? ''", v => v == "3").ConfigureAwait(false);
        Check(afterReload == "3", $"after a reload clicks still reach the component (#count = '{afterReload}')");

        if (OrielApp.Current.IsSandboxed)
        {
            var renderers = OrielApp.Current.GetAppMetrics().Where(m => m.Type == "Tab").ToList();
            Check(renderers.Count > 0 && renderers.All(m => m.Sandboxed == true),
                $"every renderer runs sandboxed ({string.Join(", ", renderers.Select(m => $"{m.Pid}:{m.Sandboxed}"))})");
        }

        Console.WriteLine(failures == 0 ? "SELF-CHECK OK" : $"SELF-CHECK FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }

    public static async Task ProbeAsync(BrowserWindow window, string path)
    {
        while (!window.IsDestroyed)
        {
            try
            {
                var node = await window.WebContents.ExecuteJavaScriptAsync("document.querySelector('h1')?.textContent ?? ''").ConfigureAwait(false);
                await File.WriteAllTextAsync(path, node?.ToString() ?? "").ConfigureAwait(false);
            }
            catch (Exception) { /* the page may be mid-navigation */ }
            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    private static async Task<string> Poll(WebContents page, string expression, Func<string, bool> done)
    {
        var value = "";
        for (var i = 0; i < 100; i++)
        {
            try
            {
                var node = await page.ExecuteJavaScriptAsync(expression).ConfigureAwait(false);
                value = node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToJsonString() ?? "";
                if (done(value)) return value;
            }
            catch (Exception) { /* the page may be mid-navigation */ }
            await Task.Delay(100).ConfigureAwait(false);
        }
        return value;
    }
}
