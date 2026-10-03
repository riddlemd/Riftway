using Oriel;

var app = OrielApp.Create();

// Electron's command-line switch of the same name.
if (args.Contains("--enable-sandbox")) app.EnableSandbox();

app.Ready += (_, _) =>
{
    var window = new BrowserWindow(new BrowserWindowOptions { Width = 1200, Height = 800, Title = "Oriel Sample" });
    // Switches (--x) belong to Chromium; the first other argument is the page to open.
    window.LoadUrl(args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "https://example.com");
};

return app.Run();
