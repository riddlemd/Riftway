using System.Runtime.Versioning;
using Riftway;
using Riftway.Cef;
using Riftway.Cef.Platform;
using Riftway.Samples;

/// <summary>Linux Notification end to end, as a notification server sees it: Electron's LibnotifyNotification read back
/// through a fake <c>org.freedesktop.Notifications</c> on the session bus.</summary>
[SupportedOSPlatform("linux")]
static class LinuxNotificationTest
{
    internal static Task RunAsync(MessageBoxTest.CheckFn check, TimeSpan step, string appName)
    {
        using var fake = FakeNotificationServer.Start();
        check("a fake notification server owns org.freedesktop.Notifications on the session bus", fake is not null, Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"));
        if (fake is null) return Task.CompletedTask;

        check("Notification.IsSupported is true", Notification.IsSupported());

        var events = new List<string>();
        string Events()
        {
            lock (events) return string.Join(",", events);
        }
        Notification Watched(Notification n)
        {
            void Add(string e)
            {
                lock (events) events.Add(e);
            }
            n.Shown += (_, _) => Add("show");
            n.Click += (_, _) => Add("click");
            n.Action += (_, e) => Add($"action {e.ActionIndex}");
            n.Reply += (_, r) => Add($"reply {r}");
            n.Closed += (_, r) => Add($"close {r}");
            n.Failed += (_, e) => Add($"failed '{e}'");
            return n;
        }
        void Clear()
        {
            lock (events) events.Clear();
        }

        // Premultiplied BGRA (10, 64, 100) at alpha 128: the pixbuf is RGBA (199, 128, 20, 128).
        var icon = NativeImage.CreateFromBitmap([10, 64, 100, 128, 10, 64, 100, 128], new NativeImageBitmapOptions { Width = 2, Height = 1 });
        var n = CefThread.InvokeOnUi(() => Watched(new Notification
        {
            Title = "Riftway title",
            Body = "Riftway body",
            IconImage = icon,
            Urgency = "critical",
            TimeoutType = "never",
            Silent = true,
            HasReply = true,
            Actions = [new NotificationAction { Text = "Ignored on Linux" }],
        }));
        var shownInside = CefThread.InvokeOnUi(() =>
        {
            n.Show();
            return Events();
        });
        check("Show raises show before it returns, as libnotify's synchronous Notify does", shownInside == "show", shownInside);
        check("Show asks libnotify's questions first: GetServerInformation, GetCapabilities, then Notify",
            string.Join(" ", fake.Methods) == "GetServerInformation GetCapabilities Notify", string.Join(" ", fake.Methods));
        var notify = fake.Notifies.LastOrDefault();
        check("Notify carries the app name, no replaced id or app icon, the title and body, and only the default action labelled Show",
            notify is { ReplacesId: 0, AppIcon: "", Summary: "Riftway title", Body: "Riftway body" } && notify.AppName == appName && string.Join("|", notify.Actions) == "default|Show",
            notify);
        var hints = notify is null ? "" : string.Join(" ", notify.Hints.Select(h => $"{h.Key}:{h.Value.Signature}"));
        check("Notify's hints are urgency, image-data, append, desktop-entry and sender-pid; no sound hint for silent",
            hints == "urgency:y image-data:(iiibiiay) append:s desktop-entry:s sender-pid:x", hints);
        string Hint(string key) => notify?.Hints.FirstOrDefault(h => h.Key == key).Value is { } v ? Format(v.Value) : "missing";
        var desktop = LinuxNotificationRules.XdgAppId(LinuxLauncherEntry.DesktopName());
        check("critical urgency is 2, the never timeout is 0, append is \"true\", desktop-entry is the desktop name without .desktop, sender-pid is the process",
            Hint("urgency") == "2" && notify!.Timeout == 0 && Hint("append") == "true" && Hint("desktop-entry") == desktop && Hint("sender-pid") == Environment.ProcessId.ToString(),
            $"{Hint("urgency")} {notify?.Timeout} {Hint("append")} {Hint("desktop-entry")}/{desktop} {Hint("sender-pid")}");
        check("image-data is the icon as an unpremultiplied RGBA pixbuf: 2x1, stride 8, alpha, 8 bits, 4 channels",
            Hint("image-data") == "2,1,8,True,8,4,[199,128,20,128,199,128,20,128]", Hint("image-data"));

        var id = notify?.Id ?? 0;
        fake.ActionInvoked(id, "0");
        fake.NotificationReplied(id, "typed");
        fake.ActionInvoked(id + 100, "default");
        check("an action key Electron did not register, an inline reply and another id's click raise nothing (no action or reply on Linux)",
            Stays(() => Events() == "show", step), Events());
        fake.ActionInvoked(id, "default");
        check("ActionInvoked \"default\" raises click and ends the notification",
            Eventually(() => Events() == "show,click", step) && !CefThread.InvokeOnUi(() => n.IsShown), Events());

        Clear();
        CefThread.InvokeOnUi(() =>
        {
            n.Show();
            return true;
        });
        id = fake.Notifies.LastOrDefault()?.Id ?? 0;
        check("a second Show sends only Notify: the server's information and capabilities are kept",
            string.Join(" ", fake.Methods) == "GetServerInformation GetCapabilities Notify Notify", string.Join(" ", fake.Methods));
        fake.NotificationClosed(id, 1);
        check("NotificationClosed raises close (expired as TimedOut) and ends the notification",
            Eventually(() => Events() == "show,close TimedOut", step) && !CefThread.InvokeOnUi(() => n.IsShown), Events());

        Clear();
        CefThread.InvokeOnUi(() =>
        {
            n.Show();
            return true;
        });
        id = fake.Notifies.LastOrDefault()?.Id ?? 0;
        CefThread.InvokeOnUi(() =>
        {
            n.Close();
            return true;
        });
        check("Close calls CloseNotification with the id, and the server's NotificationClosed raises nothing",
            fake.Closes.LastOrDefault() == id && Stays(() => Events() == "show", step), $"{string.Join(",", fake.Closes)} {Events()}");

        Clear();
        fake.RefuseNotify = true;
        var failedInside = CefThread.InvokeOnUi(() =>
        {
            n.Show();
            return Events();
        });
        check("a refused Notify raises failed with no text inside Show, and nothing is shown",
            failedInside == "failed ''" && !CefThread.InvokeOnUi(() => n.IsShown), failedInside);
        return Task.CompletedTask;
    }

    private static string Format(object? value) => value switch
    {
        byte[] bytes => "[" + string.Join(",", bytes) + "]",
        object?[] fields => string.Join(",", fields.Select(Format)),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };

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

    // Long enough for a signal to cross the bus and the UI thread.
    private static bool Stays(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Min(timeout.TotalMilliseconds, 1000));
        while (DateTime.UtcNow < deadline)
        {
            if (!condition()) return false;
            Thread.Sleep(50);
        }
        return condition();
    }
}
