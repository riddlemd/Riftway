using System.Text.Json.Nodes;
using Oriel;

// The utility process Oriel.UtilityTest forks. args[0] picks a start-up behaviour; afterwards it answers commands
// sent on the parent port. Main returns at once: a connected parent port keeps the process alive, as in Electron.
var mode = args.Length > 0 ? args[0] : "echo";
switch (mode)
{
    case "print":
        Console.Out.WriteLine("out-line");
        Console.Out.Flush();
        Console.Error.WriteLine("err-line");
        Console.Error.Flush();
        ParentPort.PostMessage("printed");
        break;
    case "late":
        // Connected, but listening only later: what the parent sent meanwhile must wait, not be lost.
        ParentPort.PostMessage("connected");
        Thread.Sleep(1000);
        break;
    case "spin":
        ParentPort.PostMessage(new JsonObject { ["pid"] = Environment.ProcessId });
        break;
    case "info":
        // The first use connects, which clears the channel variables the report then reads.
        ParentPort.PostMessage("connected");
        ParentPort.PostMessage(new JsonObject
        {
            ["args"] = new JsonArray([.. args.Select(a => (JsonNode?)a)]),
            ["cwd"] = Environment.CurrentDirectory,
            ["var"] = Environment.GetEnvironmentVariable("UT_VAR"),
            ["parentVar"] = Environment.GetEnvironmentVariable("UT_PARENT_VAR"),
            ["pipeVarCleared"] = Environment.GetEnvironmentVariable("ORIEL_UTILITY_PIPE") is null,
        });
        break;
}

ParentPort.Message += (_, e) =>
{
    var cmd = e.Data is JsonObject o && o["cmd"] is JsonValue v ? v.GetValue<string>() : null;
    switch (cmd)
    {
        case "echo":
            ParentPort.PostMessage(new JsonObject { ["echo"] = e.Data!["v"]?.DeepClone(), ["ports"] = e.Ports.Count });
            break;
        case "port":
            // Echoes on the port and reports when the main side closes its end.
            var port = e.Ports[0];
            port.Message += (_, m) => port.PostMessage("child:" + m.Data?.GetValue<string>());
            port.Closed += (_, _) => ParentPort.PostMessage("port-closed");
            port.Start();
            ParentPort.PostMessage("port-ready");
            break;
        case "close-port":
            e.Ports[0].Close();
            break;
        case "make-channel":
            // A channel made here, one end sent to the main process through a port it sent.
            var via = e.Ports[0];
            var channel = new MessageChannelMain();
            channel.Port1.Message += (_, m) => channel.Port1.PostMessage("child-made:" + m.Data?.GetValue<string>());
            channel.Port1.Start();
            via.PostMessage("here", [channel.Port2]);
            break;
        case "page":
            var page = e.Ports[0];
            page.Message += (_, m) => ParentPort.PostMessage(new JsonObject { ["pageSaid"] = m.Data?.DeepClone() });
            page.Start();
            page.PostMessage("hello-page");
            break;
        case "hold":
            ParentPort.PostMessage("holding");
            break;
        case "exit":
            Environment.Exit(e.Data!["code"]!.GetValue<int>());
            break;
        case "throw":
            new Thread(() => throw new InvalidOperationException("child exploded")).Start();
            break;
    }
};
