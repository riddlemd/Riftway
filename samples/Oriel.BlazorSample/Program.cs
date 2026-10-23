using Microsoft.Extensions.DependencyInjection;
using Oriel;
using Oriel.Blazor;
using Oriel.BlazorSample;

var app = OrielApp.Create();

var services = new ServiceCollection();
services.AddOrielBlazor();
services.AddSingleton<SharedCounter>();
services.AddSingleton<WindowOpener>();
var provider = services.BuildServiceProvider();

var exitAfterCheck = args.Contains("--self-check");

app.Ready += (_, _) =>
{
    var window = provider.GetRequiredService<WindowOpener>().Open();
    // --probe <file>: writes the page's heading every 500 ms, so a script can see a hot reload land.
    if (Array.IndexOf(args, "--probe") is var at and >= 0 && at + 1 < args.Length)
        _ = SelfCheck.ProbeAsync(window, args[at + 1]);
    if (!exitAfterCheck) return;
    window.WebContents.ConsoleMessage += (_, e) => Console.WriteLine($"console {e.Level}: {e.Message} ({e.SourceId}:{e.LineNumber})");
    window.WebContents.PreloadError += (_, e) => Console.WriteLine($"preload error {e.PreloadPath}: {e.Message}");
    // --self-check: click the counter from the page and read back what Blazor rendered, then quit with the result.
    _ = Task.Run(async () =>
    {
        var code = await SelfCheck.RunAsync(window, provider.GetRequiredService<SharedCounter>()).ConfigureAwait(false);
        Environment.ExitCode = code;
        app.Quit();
    });
};

return app.Run() is var exit && exit != 0 ? exit : Environment.ExitCode;
