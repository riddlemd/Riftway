using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Riftway;

/// <summary>The clipboard on macOS end to end. Other processes (<c>pbcopy</c>, <c>pbpaste</c> and JXA scripts with
/// AppKit's NSPasteboard) read and write the pasteboards independently of Riftway.</summary>
static class MacClipboardTest
{
    private const string Find = "$.NSPasteboard.pasteboardWithName($.NSPasteboardNameFind)";

    internal static async Task RunAsync(MessageBoxTest.CheckFn check)
    {
        // Both pasteboards are saved item by item, every type, and put back at the end.
        var saved = SavePasteboard("$.NSPasteboard.generalPasteboard");
        var savedFind = SavePasteboard(Find);
        try { await ChecksAsync(check); }
        finally
        {
            RestorePasteboard("$.NSPasteboard.generalPasteboard", saved);
            RestorePasteboard(Find, savedFind);
        }
    }

    private static async Task ChecksAsync(MessageBoxTest.CheckFn check)
    {
        // A find text left by the user would be listed in every read().
        await Put(new() { ["electron application/findtext"] = "" });

        await WriteText("Riftway héllo ✓");
        Check(check, "WriteTextAsync reaches other apps", Run("pbpaste") == "Riftway héllo ✓", Run("pbpaste"));
        Run("pbcopy", "from pbcopy é");
        Check(check, "ReadTextAsync reads another app's text", await ReadText() == "from pbcopy é", await ReadText());

        await Put(new() { ["text/html"] = "<b>bold é</b>" });
        Check(check, "Write(text/html) puts public.html with Chromium's charset tag, and nothing else", JsString("public.html") == "<meta charset='utf-8'><b>bold é</b>" && !Types().Contains("public.utf8-plain-text"),
            $"{JsString("public.html")} | {string.Join(",", Types())}");
        JsSetString("public.html", "<i>italic ü</i>");
        Check(check, "getType(text/html) reads another app's markup whole", await GetText("text/html") == "<i>italic ü</i>", await GetText("text/html"));
        // Safari puts only RTF; Chromium converts it to HTML with NSAttributedString, done here again by AppKit in JXA.
        JsSetString("public.rtf", @"{\rtf1\ansi from {\i RTF}}");
        var converted = Js("const d = pb.dataForType('public.rtf'); const a = $.NSAttributedString.alloc.initWithRTFDocumentAttributes(d, null); "
            + "const h = a.dataFromRangeDocumentAttributesError($.NSMakeRange(0, a.length), $({ DocumentType: 'NSHTML' }), null); B($.NSString.alloc.initWithDataEncoding(h, 4))");
        var html = await GetText("text/html");
        var at = Enumerable.Range(0, Math.Min(html.Length, converted.Length)).FirstOrDefault(i => html[i] != converted[i]);
        Check(check, "an RTF-only pasteboard reads as text/html converted by NSAttributedString", (await Types2()).Take(2).SequenceEqual(["text/html", "text/rtf"]) && converted.Contains("from") && html == converted,
            $"{string.Join(",", await Types2())} | {html.Length} vs {converted.Length} at {at}: {html[Math.Max(0, at - 20)..Math.Min(html.Length, at + 40)]} vs {converted[Math.Max(0, at - 20)..Math.Min(converted.Length, at + 40)]}");

        await Put(new() { ["text/rtf"] = @"{\rtf1\ansi Riftway {\b RTF}}" });
        Check(check, "Write(text/rtf) puts the RTF bytes under public.rtf for other apps", JsString("public.rtf") == @"{\rtf1\ansi Riftway {\b RTF}}" && Run("pbpaste") == "Riftway RTF",
            $"{JsString("public.rtf")} | {Run("pbpaste")}");
        // "{\rtf1 é}" with é as the windows-1252 byte 0xE9: macOS reads RTF bytes as they are.
        byte[] legacy = [0x7B, 0x5C, 0x72, 0x74, 0x66, 0x31, 0x20, 0xE9, 0x7D];
        JsSetData("public.rtf", legacy);
        Check(check, "getType(text/rtf) reads another app's RTF bytes unconverted, as ClipboardMac does", (await GetBytes("text/rtf")).SequenceEqual(legacy), Convert.ToHexString(await GetBytes("text/rtf")));

        // Images: a 4x4 green square out, read back by AppKit; PNG and TIFF-only images in.
        var green = new byte[4 * 4 * 4];
        for (var i = 0; i < green.Length; i += 4) (green[i], green[i + 1], green[i + 2], green[i + 3]) = (0, 200, 0, 255);
        await Put(new() { ["image/png"] = NativeImage.CreateFromBitmap(green, new NativeImageBitmapOptions { Width = 4, Height = 4 }).ToPng() });
        var seen = Js("const r = $.NSBitmapImageRep.imageRepWithData(pb.dataForType('public.png')); const c = r.colorAtXY(1, 1); "
            + "r.pixelsWide + 'x' + r.pixelsHigh + ' ' + [c.redComponent, c.greenComponent, c.blueComponent].map(v => Math.round(v * 255)).join(',')");
        Check(check, "Write(image/png) puts a PNG item other apps decode", seen == "4x4 0,200,0", seen);
        Check(check, "read() lists an image as image/png, then only AppKit's legacy aliases as raw formats",
            (await Types2()) is { Count: > 0 } imageTypes && imageTypes[0] == "image/png" && imageTypes.Skip(1).All(t => t.StartsWith("electron application/osclipboard;format=\"", StringComparison.Ordinal) && !t.Contains("public.")), $"{string.Join(",", await Types2())} | {string.Join(",", Types())}");
        var png = NativeImage.CreateFromBitmap(Solid(2, 2, 10, 20, 30), new NativeImageBitmapOptions { Width = 2, Height = 2 }).ToPng();
        JsSetData("public.png", png);
        Check(check, "getType(image/png) returns another app's PNG bytes as stored", (await GetBytes("image/png")).SequenceEqual(png), (await GetBytes("image/png")).Length);
        Js("pb.clearContents; const r = $.NSBitmapImageRep.alloc.initWithBitmapDataPlanesPixelsWidePixelsHighBitsPerSampleSamplesPerPixelHasAlphaIsPlanarColorSpaceNameBytesPerRowBitsPerPixel(null, 3, 2, 8, 4, true, false, $.NSDeviceRGBColorSpace, 12, 32); "
            + "for (let x = 0; x < 3; x++) for (let y = 0; y < 2; y++) r.setColorAtXY($.NSColor.colorWithDeviceRedGreenBlueAlpha(1, 0, 0, 1), x, y); pb.setDataForType(r.TIFFRepresentation, 'public.tiff'); ''");
        var image = NativeImage.CreateFromBuffer(await GetBytes("image/png"));
        var px = image.ToBitmap();
        Check(check, "getType(image/png) encodes another app's TIFF-only image through NSImage", image.GetSize() == (3, 2) && px.Length == 24 && px[0] < 30 && px[1] < 30 && px[2] > 220 && px[3] == 255,
            $"{image.GetSize()} {string.Join(",", px.Take(4))} | {string.Join(",", await Types2())}");

        // Bookmark: Chromium's URL item, with the title and Safari's URLs-with-titles list.
        await Put(new() { ["electron application/bookmark"] = new ClipboardBookmark("Riftway", "https://example.com/riftway") });
        var urlTitles = Js("JSON.stringify(ObjC.deepUnwrap(pb.propertyListForType('dyn.ah62d4rv4gu8zs3pcnzme2641rf4guzdmsv0gn64uqm10c6xenv61a3k')))");
        Check(check, "Write(bookmark) puts the URL, title, text and WebKit's URL list as Chromium does",
            JsString("public.url") == "https://example.com/riftway" && JsString("public.url-name") == "Riftway" && JsString("public.utf8-plain-text") == "https://example.com/riftway"
            && urlTitles == "[[\"https://example.com/riftway\"],[\"Riftway\"]]" && !Types().Contains("public.file-url"),
            $"{JsString("public.url")} | {JsString("public.url-name")} | {urlTitles} | {string.Join(",", Types())}");
        var bookmarkTypes = await Types2();
        Check(check, "read() lists the text, the URL types as raw formats, then the bookmark",
            bookmarkTypes.FirstOrDefault() == "text/plain" && bookmarkTypes.LastOrDefault() == "electron application/bookmark"
            && bookmarkTypes.Contains("electron application/osclipboard;format=\"public.url\"") && bookmarkTypes.Contains("electron application/osclipboard;format=\"public.url-name\"")
            && !bookmarkTypes.Contains("electron application/osclipboard;format=\"public.utf8-plain-text\""), string.Join(",", bookmarkTypes));
        Check(check, "getType(bookmark) reads the title and URL", await Get("electron application/bookmark") is ClipboardBookmark { Title: "Riftway", Url: "https://example.com/riftway" }, await Get("electron application/bookmark"));
        await Put(new() { ["electron application/bookmark"] = new ClipboardBookmark("", "MyApp://Bücher.example/a/../b") });
        Check(check, "Write(bookmark) stores GURL's canonical URL and no title type when the title is empty (CEF)",
            JsString("public.url") == "myapp://B%C3%BCcher.example/b" && !Types().Contains("public.url-name"), $"{JsString("public.url")} | {string.Join(",", Types())}");
        Js("pb.clearContents; pb.setStringForType('https://other.example/p', 'public.url'); pb.setStringForType('Other ✓', 'public.url-name'); ''");
        Check(check, "getType(bookmark) reads another app's URL and title", await Get("electron application/bookmark") is ClipboardBookmark { Title: "Other ✓", Url: "https://other.example/p" },
            await Get("electron application/bookmark"));
        Js("pb.clearContents; pb.setStringForType('Only a title', 'public.url-name'); ''");
        Check(check, "a title alone lists the bookmark, as Electron's ReadURL check does", (await Types2()).Contains("electron application/bookmark"), string.Join(",", await Types2()));

        // One item with several types; has and the type list agree with another app.
        await Put(new() { ["text/plain"] = "plain part", ["text/html"] = "<u>html part</u>", ["text/rtf"] = @"{\rtf1 rtf part}" });
        var several = await Types2();
        Check(check, "Write puts text, HTML and RTF together", await ReadText() == "plain part" && await GetText("text/html") == "<meta charset='utf-8'><u>html part</u>"
            && (await GetText("text/rtf")).Contains("rtf part") && Run("pbpaste") == "plain part", $"{await ReadText()} | {await GetText("text/html")}");
        Check(check, "read() lists them in Chromium's order, other pasteboard types after as raw formats",
            several.Take(3).SequenceEqual(["text/plain", "text/html", "text/rtf"]) && several.Skip(3).All(t => t.StartsWith("electron application/osclipboard;", StringComparison.Ordinal)),
            $"{string.Join(",", several)} | {string.Join(",", Types())}");
        Check(check, "has answers per type", await Has("text/html") && !await Has("image/png"));

        // Raw pasteboard types and W3C custom formats, as other apps see them.
        await Put(new() { ["electron application/osclipboard;format=\"com.riftway.test\""] = new byte[] { 1, 2, 3, 250 } });
        Check(check, "Write(osclipboard) puts the bytes under that pasteboard type", JsBytes("com.riftway.test").SequenceEqual(new byte[] { 1, 2, 3, 250 }) && Types().SequenceEqual(["com.riftway.test"]),
            $"{Convert.ToHexString(JsBytes("com.riftway.test"))} | {string.Join(",", Types())}");
        Check(check, "read() lists it back and getType reads the bytes", (await Types2()).SequenceEqual(["electron application/osclipboard;format=\"com.riftway.test\""])
            && (await GetBytes("electron application/osclipboard;format=\"com.riftway.test\"")).SequenceEqual(new byte[] { 1, 2, 3, 250 }), string.Join(",", await Types2()));
        await Put(new() { ["web application/x-riftway"] = "payload" });
        var map = JsString("org.w3.web-custom-format.map");
        var custom = JsString("org.w3.web-custom-format.type-0");
        Check(check, "Write(web type) stores org.w3.web-custom-format.type-0 and Chromium's map", map == "{\"application/x-riftway\":\"org.w3.web-custom-format.type-0\"}" && custom == "payload", $"{map} | {custom}");
        var webTypes = await Types2();
        Check(check, "read() lists the web type first, then the raw W3C types, and getType reads it",
            webTypes.SequenceEqual(["web application/x-riftway", "electron application/osclipboard;format=\"org.w3.web-custom-format.map\"", "electron application/osclipboard;format=\"org.w3.web-custom-format.type-0\""])
            && await GetText("web application/x-riftway") == "payload", string.Join(",", webTypes));

        // Files: text/uri-list is the copied-files format both ways.
        var dir = Path.Combine(Path.GetTempPath(), "riftway-clipboard-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        var shown = Path.Combine(dir, "riftway clipboard é.txt");
        // An image file, so AppKit could read the copied files as an image too.
        var other = Path.Combine(dir, "other.png");
        File.WriteAllText(shown, "x");
        File.WriteAllBytes(other, png);
        await Put(new() { ["text/uri-list"] = new Uri(shown).AbsoluteUri + "\r\n" + new Uri(other).AbsoluteUri });
        var copied = Js("JSON.stringify(ObjC.deepUnwrap(pb.readObjectsForClassesOptions($([$.NSURL]), $())).map(u => u))");
        var files = Js("const a = pb.readObjectsForClassesOptions($([$.NSURL]), $()); const o = []; for (let i = 0; i < a.count; i++) o.push(a.objectAtIndex(i).path.js); B($(o.join('|')))");
        Check(check, "Write(text/uri-list) copies one file URL item per file for other apps",
            files.Normalize() == shown.Normalize() + "|" + other && Js("pb.pasteboardItems.count") == "2", $"{files} | {copied}");
        Js($"pb.clearContents; pb.writeObjects($([$.NSURL.fileURLWithPath({Q(other)}), $.NSURL.fileURLWithPath({Q(shown)})])); ''");
        var uris = await GetText("text/uri-list");
        Check(check, "getType(text/uri-list) reads files another app copied, in decomposed form, and hides their icon",
            uris == "file://" + other + "\r\nfile://" + shown.Replace(" ", "%20").Normalize(NormalizationForm.FormD).Replace("e\u0301", "e%CC%81") && !(await Types2()).Contains("image/png"),
            $"{uris} | {string.Join(",", await Types2())}");
        Directory.Delete(dir, recursive: true);

        await WriteText("");
        Check(check, "WriteTextAsync(\"\") empties the pasteboard, as Chromium does", Types().Count == 0 && (await Types2()).Count == 0, string.Join(",", Types()));
        await WriteText("x");
        try { Clipboard.Clear(); } catch (Exception e) { Console.WriteLine("clipboard clear threw: " + e.Message); }
        Check(check, "Clear empties it for every app", (await Types2()).Count == 0 && Types().Count == 0, string.Join(",", Types()));

        // The find pasteboard: a separate pasteboard, written at once and listed while it holds text.
        await Put(new() { ["electron application/findtext"] = "Riftway find é" });
        var find = Js($"const s = {Find}.stringForType('public.utf8-plain-text'); s.isNil() ? '<nil>' : B(s)");
        Check(check, "Write(findtext) reaches the find pasteboard other apps read", find == "Riftway find é", find);
        Check(check, "read() lists findtext, which Clear leaves alone, and getType reads it", (await Types2()).SequenceEqual(["electron application/findtext"]) && await Has("electron application/findtext")
            && await GetText("electron application/findtext") == "Riftway find é", string.Join(",", await Types2()));
        Js($"const f = {Find}; f.clearContents; f.writeObjects($([$('from another app')])); ''");
        Check(check, "getType(findtext) reads Chromium's copy, which another app's find text replaces only when the app becomes active",
            await GetText("electron application/findtext") == "Riftway find é", await GetText("electron application/findtext"));
        await Put(new() { ["electron application/findtext"] = "" });
        Check(check, "an empty find text is not listed", (await Types2()).Count == 0, string.Join(",", await Types2()));
    }

    private static void Check(MessageBoxTest.CheckFn check, string name, bool pass, object? detail = null) => check("macOS clipboard: " + name, pass, detail);

    private static byte[] Solid(int width, int height, byte b, byte g, byte r)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, 255);
        return pixels;
    }

    // Wrapped so a mutant that throws fails its check instead of stopping the run.
    private static async Task<IReadOnlyList<string>> Types2() { try { return (await Clipboard.ReadAsync())[0].Types; } catch (Exception e) { return ["<" + e.Message + ">"]; } }
    private static async Task<object> Get(string type) { try { return await (await Clipboard.ReadAsync())[0].GetTypeAsync(type); } catch (Exception e) { return "<" + e.Message + ">"; } }
    private static async Task<byte[]> GetBytes(string type) => await Get(type) as byte[] ?? [];
    private static async Task<string> GetText(string type) => await Get(type) is byte[] bytes ? Encoding.UTF8.GetString(bytes) : $"{await Get(type)}";
    private static async Task Put(Dictionary<string, object?> item) { try { await Clipboard.WriteAsync([new ClipboardItem(item)]); } catch (Exception e) { Console.WriteLine("clipboard write threw: " + e.Message); } }
    private static async Task<string> ReadText() { try { return await Clipboard.ReadTextAsync(); } catch (Exception e) { return "<" + e.Message + ">"; } }
    private static async Task WriteText(string text) { try { await Clipboard.WriteTextAsync(text); } catch (Exception e) { Console.WriteLine("clipboard write threw: " + e.Message); } }
    private static async Task<bool> Has(string type) { try { return await Clipboard.HasAsync(type); } catch (Exception) { return false; } }

    private static string Q(string text) => JsonSerializer.Serialize(text);

    // The general pasteboard's types as AppKit lists them.
    private static List<string> Types() => [.. Js("ObjC.deepUnwrap(pb.types).join('\\n')").Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    private static string JsString(string type) => Js($"const s = pb.stringForType({Q(type)}); s.isNil() ? '<nil>' : B(s)");

    private static byte[] JsBytes(string type) => Convert.FromBase64String(Js($"const d = pb.dataForType({Q(type)}); d.isNil() ? '' : d.base64EncodedStringWithOptions(0).js"));

    private static void JsSetString(string type, string text) => Js($"pb.clearContents; pb.setStringForType({Q(text)}, {Q(type)}); ''");

    private static void JsSetData(string type, byte[] data)
        => Js($"pb.clearContents; pb.setDataForType($.NSData.alloc.initWithBase64EncodedStringOptions({Q(Convert.ToBase64String(data))}, 0), {Q(type)}); ''");

    // Every item of a pasteboard as {type: base64}.
    private static string SavePasteboard(string pasteboard)
        => Js($"const p = {pasteboard}; const out = []; const items = p.pasteboardItems; for (let i = 0; i < items.count; i++) {{ const it = items.objectAtIndex(i); const o = {{}}; "
            + "const t = it.types; for (let j = 0; j < t.count; j++) { const d = it.dataForType(t.objectAtIndex(j)); if (!d.isNil()) o[t.objectAtIndex(j).js] = d.base64EncodedStringWithOptions(0).js; } out.push(o); } JSON.stringify(out)");

    private static void RestorePasteboard(string pasteboard, string json)
        => Js($"const p = {pasteboard}; p.clearContents; const items = $.NSMutableArray.array; for (const o of JSON.parse({Q(json)})) {{ const it = $.NSPasteboardItem.alloc.init; "
            + "for (const t in o) it.setDataForType($.NSData.alloc.initWithBase64EncodedStringOptions(o[t], 0), t); items.addObject(it); } if (items.count) p.writeObjects(items); ''");

    // A JXA script with `pb` (the general pasteboard) and B (an NSString as base64 UTF-8, decoded here) in scope.
    private static string Js(string body)
    {
        const string Prelude = "ObjC.import('AppKit'); const pb = $.NSPasteboard.generalPasteboard; const B = s => '\\u0001' + s.dataUsingEncoding(4).base64EncodedStringWithOptions(0).js; ";
        var output = Run("osascript", null, "-l", "JavaScript", "-e", Prelude + body);
        return output.StartsWith('\u0001') ? Encoding.UTF8.GetString(Convert.FromBase64String(output[1..])) : output;
    }

    private static string Run(string file, string? input = null, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null };
        psi.Environment["LANG"] = "en_US.UTF-8";
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        if (input is not null)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) Console.WriteLine($"{file} failed: {error.Trim()}");
        return output.EndsWith('\n') ? output[..^1] : output;
    }

    private static string Run(string file, string input) => Run(file, input, []);
}
