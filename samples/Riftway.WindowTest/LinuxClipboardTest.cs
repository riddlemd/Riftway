using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Riftway;

/// <summary>The clipboard and the PRIMARY selection on Linux end to end. <c>x11clip.py</c>, a separate X client over
/// Xlib, reads what Riftway offers and owns selections Riftway reads.</summary>
static class LinuxClipboardTest
{
    private const string Os = "electron application/osclipboard;format=";
    private static readonly string[] s_textTargets = ["UTF8_STRING", "STRING", "TEXT", "text/plain", "text/plain;charset=utf-8"];
    private static readonly string[] s_ownTargets = ["TIMESTAMP", "TARGETS", "SAVE_TARGETS", "MULTIPLE"];
    private static readonly List<Process> s_owners = [];
    private static string s_script = "";

    internal static async Task RunAsync(MessageBoxTest.CheckFn check)
    {
        s_script = Path.Combine(Path.GetTempPath(), "riftway-x11clip-" + Path.GetRandomFileName() + ".py");
        using (var resource = typeof(LinuxClipboardTest).Assembly.GetManifestResourceStream("x11clip.py")!)
        using (var file = File.Create(s_script))
            resource.CopyTo(file);
        try { await ChecksAsync(check); }
        finally
        {
            foreach (var owner in s_owners) { try { owner.Kill(); } catch (InvalidOperationException) { } owner.Dispose(); }
            File.Delete(s_script);
        }
    }

    private static async Task ChecksAsync(MessageBoxTest.CheckFn check)
    {
        Check(check, "clipboard.selection exists on Linux", Clipboard.Selection is not null);

        // Text out: every text target, typed as itself, as SelectionOwner answers.
        await WriteText(false, "Riftway héllo ✓");
        var utf8 = Encoding.UTF8.GetBytes("Riftway héllo ✓");
        var reads = s_textTargets.Select(t => XRead("CLIPBOARD", t)).ToList();
        Check(check, "WriteTextAsync offers UTF-8 under all five of Chromium's text targets, each typed as itself",
            reads.Zip(s_textTargets).All(p => p.First.Ok && p.First.Type == p.Second && p.First.Format == 8 && p.First.Data.SequenceEqual(utf8)),
            string.Join(" | ", reads.Select(r => $"{r.Type}:{Convert.ToHexString(r.Data)}")));
        Check(check, "TARGETS lists Chromium's four protocol targets, then the text targets in its order",
            XTargets("CLIPBOARD").SequenceEqual([.. s_ownTargets, .. s_textTargets]), string.Join(",", XTargets("CLIPBOARD")));
        Check(check, "the owner is Riftway's own window, named as Chromium names its clipboard window", XOwnerName("CLIPBOARD") == "Chromium clipboard", XOwnerName("CLIPBOARD"));
        Check(check, "a clipboard text write also puts the text on PRIMARY, as ClipboardOzone does",
            XRead("PRIMARY", "UTF8_STRING").Data.SequenceEqual(utf8) && XTargets("PRIMARY").SequenceEqual([.. s_ownTargets, .. s_textTargets]), string.Join(",", XTargets("PRIMARY")));
        var stamp = XRead("CLIPBOARD", "TIMESTAMP");
        Check(check, "TIMESTAMP answers the server time ownership was taken at", stamp.Ok && stamp.Type == "INTEGER" && stamp.Format == 32 && BitConverter.ToUInt32(stamp.Data) != 0, $"{stamp.Type} {Convert.ToHexString(stamp.Data)}");
        var multiple = XJson("multiple", "CLIPBOARD", "UTF8_STRING", "text/html", "STRING");
        Check(check, "MULTIPLE converts each pair and refuses targets it does not hold",
            multiple.ValueKind == JsonValueKind.Object && multiple.GetProperty("UTF8_STRING").GetString() == Convert.ToHexStringLower(utf8) && multiple.GetProperty("text/html").ValueKind == JsonValueKind.Null
            && multiple.GetProperty("STRING").GetString() == Convert.ToHexStringLower(utf8), multiple.ToString());

        // Text in, from an owner that answers TARGETS and from one that does not.
        Own("CLIPBOARD", [("UTF8_STRING", "UTF8_STRING", Encoding.UTF8.GetBytes("from python é"))]);
        Check(check, "ReadTextAsync reads another client's UTF8_STRING", await ReadText(false) == "from python é", await ReadText(false));
        Check(check, "read() lists text/plain, then the owner's targets as raw formats in byte order",
            (await Types(false)).SequenceEqual(["text/plain", Os + "\"TARGETS\"", Os + "\"UTF8_STRING\""]), string.Join(",", await Types(false)));
        Own("CLIPBOARD", [("STRING", "STRING", [0x63, 0x61, 0x66, 0xE9])], answerTargets: false);
        Check(check, "an owner without TARGETS is probed for text, and STRING is read as UTF-8 as ClipboardOzone reads it",
            await ReadText(false) == "caf�" && (await Types(false)).SequenceEqual(["text/plain", Os + "\"STRING\""]), $"{await ReadText(false)} | {string.Join(",", await Types(false))}");

        // HTML both ways.
        await Put(false, new() { ["text/html"] = "<b>bold é</b>" });
        var html = XRead("CLIPBOARD", "text/html");
        Check(check, "Write(text/html) offers the markup as UTF-8 under text/html alone", html.Type == "text/html" && Encoding.UTF8.GetString(html.Data) == "<b>bold é</b>"
            && XTargets("CLIPBOARD").SequenceEqual([.. s_ownTargets, "text/html"]), $"{Encoding.UTF8.GetString(html.Data)} | {string.Join(",", XTargets("CLIPBOARD"))}");
        Check(check, "an HTML-only write leaves PRIMARY alone", XRead("PRIMARY", "UTF8_STRING").Data.SequenceEqual(utf8), Convert.ToHexString(XRead("PRIMARY", "UTF8_STRING").Data));
        Own("CLIPBOARD", [("text/html", "text/html", Encoding.UTF8.GetBytes("<i>italic ü</i>"))]);
        Check(check, "getType(text/html) reads another client's markup whole", await GetText(false, "text/html") == "<i>italic ü</i>", await GetText(false, "text/html"));

        // RTF both ways, bytes as they are.
        await Put(false, new() { ["text/rtf"] = @"{\rtf1\ansi Riftway {\b RTF}}" });
        var rtf = XRead("CLIPBOARD", "text/rtf");
        Check(check, "Write(text/rtf) offers the RTF bytes under text/rtf", rtf.Type == "text/rtf" && Encoding.UTF8.GetString(rtf.Data) == @"{\rtf1\ansi Riftway {\b RTF}}", Encoding.UTF8.GetString(rtf.Data));
        byte[] legacy = [0x7B, 0x5C, 0x72, 0x74, 0x66, 0x31, 0x20, 0xE9, 0x7D];
        Own("CLIPBOARD", [("text/rtf", "text/rtf", legacy)]);
        Check(check, "getType(text/rtf) reads another client's RTF bytes unconverted", (await GetBytes(false, "text/rtf")).SequenceEqual(legacy), Convert.ToHexString(await GetBytes(false, "text/rtf")));

        // Images both ways.
        await Put(false, new() { ["image/png"] = NativeImage.CreateFromBitmap(Solid(4, 4, 0, 200, 0), new NativeImageBitmapOptions { Width = 4, Height = 4 }).ToPng() });
        var outPng = XRead("CLIPBOARD", "image/png");
        var outImage = NativeImage.CreateFromBuffer(outPng.Data);
        var outPx = outImage.ToBitmap();
        Check(check, "Write(image/png) offers a PNG other clients decode", outPng.Type == "image/png" && outImage.GetSize() == (4, 4) && outPx.Length == 64 && outPx[1] == 200 && outPx[3] == 255,
            $"{outPng.Type} {outImage.GetSize()} {string.Join(",", outPx.Take(4))}");
        Check(check, "read() lists an image as image/png alone", (await Types(false)).SequenceEqual(["image/png"]), string.Join(",", await Types(false)));
        var png = NativeImage.CreateFromBitmap(Solid(2, 2, 10, 20, 30), new NativeImageBitmapOptions { Width = 2, Height = 2 }).ToPng();
        Own("CLIPBOARD", [("image/png", "image/png", png)]);
        Check(check, "getType(image/png) returns another client's PNG bytes as stored", (await GetBytes(false, "image/png")).SequenceEqual(png), (await GetBytes(false, "image/png")).Length);

        // Raw and web custom formats both ways.
        await Put(false, new() { ["web application/x-riftway"] = "payload", ["application/x-raw"] = new byte[] { 1, 2, 3, 250 }, [Os + "\"x-riftway-os\""] = new byte[] { 9 } });
        var map = XRead("CLIPBOARD", "application/web;type=\"custom/formatmap\"");
        Check(check, "Write(web type) offers application/web;type=\"custom/formatN\" and Chromium's JSON map, numbered after the raw formats",
            Encoding.UTF8.GetString(map.Data) == "{\"application/x-riftway\":\"application/web;type=\\\"custom/format2\\\"\"}"
            && Encoding.UTF8.GetString(XRead("CLIPBOARD", "application/web;type=\"custom/format2\"").Data) == "payload", Encoding.UTF8.GetString(map.Data));
        Check(check, "Write(raw and osclipboard types) offers the bytes under those names", XRead("CLIPBOARD", "application/x-raw").Data.SequenceEqual(new byte[] { 1, 2, 3, 250 })
            && XRead("CLIPBOARD", "x-riftway-os").Data.SequenceEqual(new byte[] { 9 }), string.Join(",", XTargets("CLIPBOARD")));
        Check(check, "read() lists the web type, then every other target as a raw format in byte order", (await Types(false)).SequenceEqual(["web application/x-riftway",
            Os + "\"application/web;type=\"custom/format2\"\"", Os + "\"application/web;type=\"custom/formatmap\"\"", Os + "\"application/x-raw\"", Os + "\"x-riftway-os\""]), string.Join(",", await Types(false)));
        Own("CLIPBOARD", [("application/web;type=\"custom/formatmap\"", "application/web;type=\"custom/formatmap\"", Encoding.UTF8.GetBytes("{\"text/x-other\":\"application/web;type=\\\"custom/format0\\\"\"}")),
            ("application/web;type=\"custom/format0\"", "application/web;type=\"custom/format0\"", [7, 7]), ("application/x-raw", "application/x-raw", [5, 6])]);
        Check(check, "another client's web custom format is listed and read by its web name", (await Types(false)).FirstOrDefault() == "web text/x-other" && (await GetBytes(false, "web text/x-other")).SequenceEqual(new byte[] { 7, 7 }),
            string.Join(",", await Types(false)));
        Check(check, "another client's raw format reads by its osclipboard name; its bare name is not listed, so getType rejects", (await GetBytes(false, Os + "\"application/x-raw\"")).SequenceEqual(new byte[] { 5, 6 })
            && await Has(false, Os + "\"application/x-raw\"") && $"{await Get(false, "application/x-raw")}" == "<The type 'application/x-raw' was not found in the ClipboardItem>",
            $"{Convert.ToHexString(await GetBytes(false, Os + "\"application/x-raw\""))} | {await Get(false, "application/x-raw")}");

        // Payloads over a request's size go incrementally (INCR), both ways.
        var big = new byte[3 * 1024 * 1024 + 7];
        for (var i = 0; i < big.Length; i++) big[i] = (byte)(i * 31);
        await Put(false, new() { ["application/x-big"] = big });
        var bigOut = XRead("CLIPBOARD", "application/x-big");
        Check(check, "a 3 MiB write reaches another client incrementally, whole", bigOut.Ok && bigOut.Data.SequenceEqual(big), bigOut.Data.Length);
        Own("CLIPBOARD", [("application/x-big", "application/x-big", big)], incr: 65536);
        Check(check, "a 3 MiB payload another client sends incrementally is read whole", (await GetBytes(false, Os + "\"application/x-big\"")).SequenceEqual(big), (await GetBytes(false, Os + "\"application/x-big\"")).Length);

        // Bookmarks: written as text/x-moz-url, never read back on Linux.
        await Put(false, new() { ["electron application/bookmark"] = new ClipboardBookmark("Riftway", "https://example.com/riftway") });
        var moz = XRead("CLIPBOARD", "text/x-moz-url");
        Check(check, "Write(bookmark) offers UTF-16 \"url\\ntitle\" under text/x-moz-url", Encoding.Unicode.GetString(moz.Data) == "https://example.com/riftway\nRiftway", Encoding.Unicode.GetString(moz.Data));
        Check(check, "read() lists no bookmark on Linux, only the raw x-moz-url, so getType(bookmark) rejects",
            (await Types(false)).SequenceEqual([Os + "\"text/x-moz-url\""]) && $"{await Get(false, "electron application/bookmark")}" == "<The type 'electron application/bookmark' was not found in the ClipboardItem>",
            $"{string.Join(",", await Types(false))} | {await Get(false, "electron application/bookmark")}");

        // Files both ways.
        await Put(false, new() { ["text/uri-list"] = "file:///tmp/a%20b\r\nhttps://not-a-file/" });
        var uris = XRead("CLIPBOARD", "text/uri-list");
        Check(check, "Write(text/uri-list) offers the file URLs alone, re-escaped", Encoding.UTF8.GetString(uris.Data) == "file:///tmp/a%20b", Encoding.UTF8.GetString(uris.Data));
        Own("CLIPBOARD", [("text/uri-list", "text/uri-list", Encoding.UTF8.GetBytes("file:///tmp/x%C3%A9\r\nfile:///tmp/y"))]);
        Check(check, "getType(text/uri-list) reads another client's files", await GetText(false, "text/uri-list") == "file:///tmp/x%C3%A9\r\nfile:///tmp/y" && (await Types(false)).First() == "text/uri-list",
            $"{await GetText(false, "text/uri-list")} | {string.Join(",", await Types(false))}");

        // Several types at once, has() and clear().
        await Put(false, new() { ["text/plain"] = "plain part", ["text/html"] = "<u>html part</u>", ["image/png"] = png });
        Check(check, "read() lists standard types in the byte order of their targets, then the other text targets",
            (await Types(false)).SequenceEqual(["text/plain", "image/png", "text/html", Os + "\"STRING\"", Os + "\"TEXT\"", Os + "\"UTF8_STRING\"", Os + "\"text/plain;charset=utf-8\""]), string.Join(",", await Types(false)));
        Check(check, "has answers per type", await Has(false, "text/html") && await Has(false, "image/png") && !await Has(false, "image/svg+xml") && !await Has(false, "text/rtf"));
        Check(check, "TARGETS puts text after the protocol targets and the rest by atom", XTargets("CLIPBOARD") is { Length: 11 } t && t[..9].SequenceEqual([.. s_ownTargets, .. s_textTargets])
            && t[9..].Order().SequenceEqual(["image/png", "text/html"]), string.Join(",", XTargets("CLIPBOARD")));
        Clipboard.Clear();
        Check(check, "Clear keeps the clipboard owned with nothing on it, as ClipboardOzone does", XTargets("CLIPBOARD").SequenceEqual(s_ownTargets) && XOwnerName("CLIPBOARD") == "Chromium clipboard"
            && (await Types(false)).Count == 0 && await ReadText(false) == "", string.Join(",", XTargets("CLIPBOARD")));
        await WriteText(false, "x");
        await WriteText(false, "");
        Check(check, "WriteTextAsync(\"\") empties the clipboard", (await Types(false)).Count == 0 && XTargets("CLIPBOARD").SequenceEqual(s_ownTargets), string.Join(",", XTargets("CLIPBOARD")));

        // PRIMARY is its own buffer.
        await WriteText(false, "clip");
        await WriteText(true, "primary only");
        Check(check, "selection.writeText owns PRIMARY and leaves CLIPBOARD alone", Encoding.UTF8.GetString(XRead("PRIMARY", "UTF8_STRING").Data) == "primary only"
            && Encoding.UTF8.GetString(XRead("CLIPBOARD", "UTF8_STRING").Data) == "clip" && await ReadText(true) == "primary only" && await ReadText(false) == "clip",
            $"{await ReadText(true)} | {await ReadText(false)}");
        Own("PRIMARY", [("UTF8_STRING", "UTF8_STRING", Encoding.UTF8.GetBytes("selected élsewhere"))]);
        Check(check, "selection.readText reads another client's PRIMARY", await ReadText(true) == "selected élsewhere" && await ReadText(false) == "clip", $"{await ReadText(true)} | {await ReadText(false)}");
        await Put(true, new() { ["text/html"] = "<b>sel</b>" });
        Check(check, "selection.write offers on PRIMARY only, and selection.read lists it", XTargets("PRIMARY").SequenceEqual([.. s_ownTargets, "text/html"])
            && (await Types(true)).SequenceEqual(["text/html"]) && await GetText(true, "text/html") == "<b>sel</b>" && (await Types(false)).First() == "text/plain",
            $"{string.Join(",", XTargets("PRIMARY"))} | {string.Join(",", await Types(true))}");
        Clipboard.Selection!.Clear();
        Check(check, "selection.clear empties PRIMARY only", XTargets("PRIMARY").SequenceEqual(s_ownTargets) && (await Types(true)).Count == 0 && await ReadText(false) == "clip",
            string.Join(",", XTargets("PRIMARY")));
        await Put(true, new() { ["web application/x-sel"] = "s" });
        Check(check, "selection.read does not list web custom formats, whose map Chromium reads from CLIPBOARD",
            (await Types(true)).SequenceEqual([Os + "\"application/web;type=\"custom/format0\"\"", Os + "\"application/web;type=\"custom/formatmap\"\""]) && !await Has(true, "web application/x-sel"),
            string.Join(",", await Types(true)));
        await Put(true, new() { ["application/x-sel-raw"] = new byte[] { 1 } });
        Check(check, "selection.read lists a raw format, but getType reads raw formats from CLIPBOARD, as ClipboardOzone::ReadData does",
            (await Types(true)).SequenceEqual([Os + "\"application/x-sel-raw\""]) && (await GetBytes(true, Os + "\"application/x-sel-raw\"")).Length == 0
            && XRead("PRIMARY", "application/x-sel-raw").Data.SequenceEqual(new byte[] { 1 }), $"{string.Join(",", await Types(true))} | {Convert.ToHexString(await GetBytes(true, Os + "\"application/x-sel-raw\""))}");
    }

    private static void Check(MessageBoxTest.CheckFn check, string name, bool pass, object? detail = null) => check("Linux clipboard: " + name, pass, detail);

    private static byte[] Solid(int width, int height, byte b, byte g, byte r)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, 255);
        return pixels;
    }

    // Wrapped so a mutant that throws fails its check instead of stopping the run.
    private static async Task<IReadOnlyList<string>> Types(bool primary)
    {
        try { return (await (primary ? Clipboard.Selection!.ReadAsync() : Clipboard.ReadAsync()))[0].Types; }
        catch (Exception e) { return ["<" + e.Message + ">"]; }
    }

    private static async Task<object> Get(bool primary, string type)
    {
        try { return await (await (primary ? Clipboard.Selection!.ReadAsync() : Clipboard.ReadAsync()))[0].GetTypeAsync(type); }
        catch (Exception e) { return "<" + e.Message + ">"; }
    }

    private static async Task<byte[]> GetBytes(bool primary, string type) => await Get(primary, type) as byte[] ?? [];
    private static async Task<string> GetText(bool primary, string type) => await Get(primary, type) is byte[] bytes ? Encoding.UTF8.GetString(bytes) : $"{await Get(primary, type)}";

    private static async Task Put(bool primary, Dictionary<string, object?> item)
    {
        try { await (primary ? Clipboard.Selection!.WriteAsync([new ClipboardItem(item)]) : Clipboard.WriteAsync([new ClipboardItem(item)])); }
        catch (Exception e) { Console.WriteLine("clipboard write threw: " + e.Message); }
    }

    private static async Task<string> ReadText(bool primary)
    {
        try { return await (primary ? Clipboard.Selection!.ReadTextAsync() : Clipboard.ReadTextAsync()); }
        catch (Exception e) { return "<" + e.Message + ">"; }
    }

    private static async Task WriteText(bool primary, string text)
    {
        try { await (primary ? Clipboard.Selection!.WriteTextAsync(text) : Clipboard.WriteTextAsync(text)); }
        catch (Exception e) { Console.WriteLine("clipboard write threw: " + e.Message); }
    }

    private static async Task<bool> Has(bool primary, string type)
    {
        try { return await (primary ? Clipboard.Selection!.HasAsync(type) : Clipboard.HasAsync(type)); }
        catch (Exception) { return false; }
    }

    private static (bool Ok, string? Type, int Format, byte[] Data) XRead(string selection, string target)
    {
        var r = XJson("read", selection, target);
        if (r.ValueKind != JsonValueKind.Object || !r.GetProperty("ok").GetBoolean()) return (false, null, 0, []);
        return (true, r.GetProperty("type").GetString(), r.GetProperty("format").GetInt32(), Convert.FromHexString(r.GetProperty("hex").GetString()!));
    }

    private static string[] XTargets(string selection)
        => XJson("targets", selection) is { ValueKind: JsonValueKind.Array } list ? [.. list.EnumerateArray().Select(e => e.GetString() ?? "")] : [];

    private static string? XOwnerName(string selection) => XJson("owner", selection) is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    private static JsonElement XJson(params string[] args)
    {
        var output = Python(args);
        try { return JsonDocument.Parse(output).RootElement.Clone(); }
        catch (JsonException) { return default; }
    }

    private static string Python(string[] args)
    {
        var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add(s_script);
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(20000)) { process.Kill(); return ""; }
        return output.Result;
    }

    // Starts an owner and returns once it holds the selection; it exits when another client takes it.
    private static void Own(string selection, (string Target, string Type, byte[] Data)[] items, bool answerTargets = true, int incr = 1 << 30)
    {
        var spec = Path.Combine(Path.GetTempPath(), "riftway-x11clip-" + Path.GetRandomFileName() + ".json");
        File.WriteAllText(spec, JsonSerializer.Serialize(new
        {
            items = items.Select(i => new { target = i.Target, type = i.Type, hex = Convert.ToHexString(i.Data) }),
            targets = answerTargets,
            incr,
        }));
        var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { s_script, "own", selection, spec }) psi.ArgumentList.Add(arg);
        var process = Process.Start(psi)!;
        s_owners.Add(process);
        var ready = process.StandardOutput.ReadLineAsync();
        if (!ready.Wait(10000) || ready.Result != "READY") Console.WriteLine($"x11clip owner of {selection} did not start: {(ready.IsCompleted ? ready.Result : "timeout")}");
        File.Delete(spec);
    }
}
