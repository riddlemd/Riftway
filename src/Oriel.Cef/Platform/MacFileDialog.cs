using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>How to set up an NSOpenPanel or NSSavePanel; Electron's <c>file_dialog::DialogSettings</c> after
/// its <c>SetupDialog</c> has resolved the default path.</summary>
public sealed class MacPanelSettings
{
    /// <summary><c>setTitle:</c> when not empty.</summary>
    public string Title { get; init; } = "";

    /// <summary><c>setMessage:</c> when not empty.</summary>
    public string Message { get; init; } = "";

    /// <summary><c>setPrompt:</c> when not empty.</summary>
    public string ButtonLabel { get; init; } = "";

    /// <summary><c>setNameFieldLabel:</c> when not empty.</summary>
    public string NameFieldLabel { get; init; } = "";

    /// <summary><c>setShowsTagField:</c>.</summary>
    public bool ShowsTagField { get; init; } = true;

    /// <summary><c>setDirectoryURL:</c> when set.</summary>
    public string? Directory { get; init; }

    /// <summary><c>setNameFieldStringValue:</c> when set.</summary>
    public string? FileName { get; init; }

    /// <summary>Each filter's name and file types; the types are already reduced to what AppKit understands.</summary>
    public IReadOnlyList<(string Name, IReadOnlyList<string> Types)> Filters { get; init; } = [];

    /// <summary><c>setCanChooseFiles:</c> (open panels only; always sent).</summary>
    public bool CanChooseFiles { get; init; }

    /// <summary><c>setCanChooseDirectories:YES</c> when true.</summary>
    public bool CanChooseDirectories { get; init; }

    /// <summary><c>setCanCreateDirectories:YES</c> when true.</summary>
    public bool CanCreateDirectories { get; init; }

    /// <summary><c>setAllowsMultipleSelection:YES</c> when true.</summary>
    public bool AllowsMultipleSelection { get; init; }

    /// <summary><c>setShowsHiddenFiles:YES</c> when true.</summary>
    public bool ShowsHiddenFiles { get; init; }

    /// <summary><c>setResolvesAliases:NO</c> when true.</summary>
    public bool NoResolveAliases { get; init; }

    /// <summary><c>setTreatsFilePackagesAsDirectories:YES</c> when true.</summary>
    public bool TreatsFilePackagesAsDirectories { get; init; }

    /// <summary><c>setCanSelectHiddenExtension:YES</c> when true (Electron's async save dialog).</summary>
    public bool CanSelectHiddenExtension { get; init; }

    /// <summary>What <c>setAllowedFileTypes:</c> gets for a filter's types: null (every type) when they are empty
    /// or include <c>*</c>.</summary>
    public static IReadOnlyList<string>? AllowedTypesOf(IReadOnlyList<string> types)
        => types.Count == 0 || types.Contains("*") ? null : types;
}

/// <summary>Native file panels, set up and run as Electron's <c>shell/browser/ui/file_dialog_mac.mm</c>.
/// Main thread only.</summary>
[SupportedOSPlatform("macos")]
public static unsafe class MacFileDialog
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const nint ModalResponseCancel = 0;
    private const nuint SecurityScope = 1 << 11;
    // BLOCK_HAS_SIGNATURE: AppKit may read the block's type encoding.
    private const int BlockHasSignature = 1 << 30;

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect
    {
        public double X, Y, Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Block
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public BlockDescriptor* Descriptor;
        public nint Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
        public byte* Signature;
    }

    private sealed record FormatPicker(nint Panel, nint Popup, nint Target, IReadOnlyList<IReadOnlyList<string>> Types);

    private static readonly nint s_msgSend = NativeLibrary.GetExport(NativeLibrary.Load(ObjC), "objc_msgSend");
    private static readonly nint s_stackBlock = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteStackBlock");
    private static readonly BlockDescriptor* s_descriptor = NewDescriptor();
    private static readonly Dictionary<nint, FormatPicker> s_pickers = [];
    private static nint s_targetClass;

    /// <summary>A retained NSOpenPanel set up as Electron's <c>SetupDialog</c> and <c>SetupOpenDialogForProperties</c>.
    /// Pass it to <see cref="Release"/> when done.</summary>
    public static nint CreateOpenPanel(MacPanelSettings settings)
    {
        var panel = Send(Send(Class("NSOpenPanel"u8), Sel("openPanel"u8)), Sel("retain"u8));
        Setup(panel, settings);
        SendBool(panel, Sel("setCanChooseFiles:"u8), settings.CanChooseFiles);
        if (settings.CanChooseDirectories) SendBool(panel, Sel("setCanChooseDirectories:"u8), true);
        if (settings.CanCreateDirectories) SendBool(panel, Sel("setCanCreateDirectories:"u8), true);
        if (settings.AllowsMultipleSelection) SendBool(panel, Sel("setAllowsMultipleSelection:"u8), true);
        if (settings.ShowsHiddenFiles) SendBool(panel, Sel("setShowsHiddenFiles:"u8), true);
        if (settings.NoResolveAliases) SendBool(panel, Sel("setResolvesAliases:"u8), false);
        if (settings.TreatsFilePackagesAsDirectories) SendBool(panel, Sel("setTreatsFilePackagesAsDirectories:"u8), true);
        return panel;
    }

    /// <summary>A retained NSSavePanel set up as Electron's <c>SetupDialog</c> and <c>SetupSaveDialogForProperties</c>.
    /// Pass it to <see cref="Release"/> when done.</summary>
    public static nint CreateSavePanel(MacPanelSettings settings)
    {
        var panel = Send(Send(Class("NSSavePanel"u8), Sel("savePanel"u8)), Sel("retain"u8));
        Setup(panel, settings);
        if (settings.CanCreateDirectories) SendBool(panel, Sel("setCanCreateDirectories:"u8), true);
        if (settings.ShowsHiddenFiles) SendBool(panel, Sel("setShowsHiddenFiles:"u8), true);
        if (settings.TreatsFilePackagesAsDirectories) SendBool(panel, Sel("setTreatsFilePackagesAsDirectories:"u8), true);
        if (settings.CanSelectHiddenExtension) SendBool(panel, Sel("setCanSelectHiddenExtension:"u8), true);
        return panel;
    }

    /// <summary>Shows the panel without blocking: a sheet on the window behind <paramref name="contentView"/>, or a
    /// free-standing panel when it is 0. <paramref name="completed"/> gets whether the user accepted.</summary>
    public static void Begin(nint panel, nint contentView, Action<bool> completed)
    {
        var window = WindowOf(contentView);
        var block = NewBlock(response => completed(response != ModalResponseCancel));
        if (window == 0)
            ((delegate* unmanaged<nint, nint, Block*, void>)s_msgSend)(panel, Sel("beginWithCompletionHandler:"u8), &block);
        else
            ((delegate* unmanaged<nint, nint, nint, Block*, void>)s_msgSend)(panel, Sel("beginSheetModalForWindow:completionHandler:"u8), window, &block);
    }

    /// <summary>Runs the panel modally and returns whether the user accepted: app-modal (<c>runModal</c>) when
    /// <paramref name="contentView"/> is 0, else a sheet with the window run modally, as Electron's <c>RunModalDialog</c>.</summary>
    public static bool RunModal(nint panel, nint contentView)
    {
        var window = WindowOf(contentView);
        if (window == 0) return Send(panel, Sel("runModal"u8)) != ModalResponseCancel;

        var chosen = ModalResponseCancel;
        var app = Send(Class("NSApplication"u8), Sel("sharedApplication"u8));
        var block = NewBlock(response =>
        {
            chosen = response;
            Send(app, Sel("stopModal"u8));
        });
        ((delegate* unmanaged<nint, nint, nint, Block*, void>)s_msgSend)(panel, Sel("beginSheetModalForWindow:completionHandler:"u8), window, &block);
        Send(app, Sel("runModalForWindow:"u8), window);
        return chosen != ModalResponseCancel;
    }

    /// <summary>An accepted open panel's paths, as Electron's <c>ReadDialogPathsWithBookmarks</c>: file URLs only,
    /// and only directories (or packages browsed as directories) when files cannot be chosen. With
    /// <paramref name="bookmarks"/>, each path's security-scoped bookmark is added there.</summary>
    public static IReadOnlyList<string> Paths(nint panel, List<string>? bookmarks = null)
    {
        var paths = new List<string>();
        var urls = Send(panel, Sel("URLs"u8));
        var canChooseFiles = SendGetBool(panel, Sel("canChooseFiles"u8));
        for (nint i = 0, n = Send(urls, Sel("count"u8)); i < n; i++)
        {
            var url = Send(urls, Sel("objectAtIndex:"u8), i);
            if (!SendGetBool(url, Sel("isFileURL"u8))) continue;
            if (Read(Send(url, Sel("path"u8))) is not { } path) continue;

            // AppKit can return files despite canChooseFiles NO (crbug.com/1357523).
            if (!canChooseFiles)
            {
                byte isDirectory = 0;
                var manager = Send(Class("NSFileManager"u8), Sel("defaultManager"u8));
                var exists = ((delegate* unmanaged<nint, nint, nint, byte*, byte>)s_msgSend)(manager, Sel("fileExistsAtPath:isDirectory:"u8), Send(url, Sel("path"u8)), &isDirectory) != 0;
                var workspace = Send(Class("NSWorkspace"u8), Sel("sharedWorkspace"u8));
                var packageAsDirectory = ((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(workspace, Sel("isFilePackageAtPath:"u8), Send(url, Sel("path"u8))) != 0
                    && SendGetBool(panel, Sel("treatsFilePackagesAsDirectories"u8));
                if (!exists || !(isDirectory != 0 || packageAsDirectory)) continue;
            }

            paths.Add(path);
            bookmarks?.Add(BookmarkOf(url));
        }
        return paths;
    }

    /// <summary>The save panel's URL path, as Electron's async save reads it: whatever the URL's scheme.</summary>
    public static string? UrlPath(nint panel)
    {
        var url = Send(panel, Sel("URL"u8));
        return url == 0 ? null : Read(Send(url, Sel("path"u8)));
    }

    /// <summary>A save panel's chosen path, or null when its URL is not a file URL.</summary>
    public static string? SavePath(nint panel)
    {
        var url = Send(panel, Sel("URL"u8));
        return url != 0 && SendGetBool(url, Sel("isFileURL"u8)) ? Read(Send(url, Sel("path"u8))) : null;
    }

    /// <summary>The save panel's security-scoped bookmark, as Electron's <c>GetBookmarkDataFromNSURL</c>: creates the
    /// file when missing, and gives "" when AppKit refuses.</summary>
    public static string SaveBookmark(nint panel) => BookmarkOf(Send(panel, Sel("URL"u8)));

    /// <summary>Releases a panel from <see cref="CreateOpenPanel"/> or <see cref="CreateSavePanel"/> and its format picker.</summary>
    public static void Release(nint panel)
    {
        if (s_pickers.Remove(panel, out var picker))
        {
            // The popup holds its target unretained; Electron's accessory view clears it the same way.
            Send(picker.Popup, Sel("setTarget:"u8), 0);
            Send(picker.Target, Sel("release"u8));
        }
        Send(panel, Sel("release"u8));
    }

    private static void Setup(nint panel, MacPanelSettings settings)
    {
        if (settings.Title.Length > 0) SendString(panel, Sel("setTitle:"u8), settings.Title);
        if (settings.ButtonLabel.Length > 0) SendString(panel, Sel("setPrompt:"u8), settings.ButtonLabel);
        if (settings.Message.Length > 0) SendString(panel, Sel("setMessage:"u8), settings.Message);
        if (settings.NameFieldLabel.Length > 0) SendString(panel, Sel("setNameFieldLabel:"u8), settings.NameFieldLabel);
        SendBool(panel, Sel("setShowsTagField:"u8), settings.ShowsTagField);

        // Before the name field: allowed types can rewrite its extension.
        if (settings.Filters.Count == 0) SendBool(panel, Sel("setAllowsOtherFileTypes:"u8), true);
        else SetAllowedFileTypes(panel, settings.Filters);

        // Without this the default name's extension is not used for the saved file.
        SendBool(panel, Sel("setExtensionHidden:"u8), false);

        if (settings.Directory is { } directory)
            Send(panel, Sel("setDirectoryURL:"u8), Send(Class("NSURL"u8), Sel("fileURLWithPath:"u8), NSString(directory)));
        if (settings.FileName is { } name) SendString(panel, Sel("setNameFieldStringValue:"u8), name);
    }

    private static void SetAllowedFileTypes(nint panel, IReadOnlyList<(string Name, IReadOnlyList<string> Types)> filters)
    {
        Send(panel, Sel("setAllowedFileTypes:"u8), NSArray(MacPanelSettings.AllowedTypesOf(filters[0].Types)));
        if (filters.Count <= 1) return;

        // Electron's ElectronAccessoryView: a "Format:" label and a popup of the filter names.
        var labelFrame = new NSRect { Width = 60, Height = 22 };
        var popupFrame = new NSRect { X = 50, Y = 2, Width = 140, Height = 22 };
        if (Environment.OSVersion.Version.Major >= 26)
        {
            labelFrame.Y = 4;
            popupFrame.Y = 6;
        }
        var accessory = SendRect(Send(Class("NSView"u8), Sel("alloc"u8)), Sel("initWithFrame:"u8), new NSRect { Width = 200, Height = 32 });
        var label = SendRect(Send(Class("NSTextField"u8), Sel("alloc"u8)), Sel("initWithFrame:"u8), labelFrame);
        SendBool(label, Sel("setEditable:"u8), false);
        SendString(label, Sel("setStringValue:"u8), "Format:");
        SendBool(label, Sel("setBordered:"u8), false);
        SendBool(label, Sel("setBezeled:"u8), false);
        SendBool(label, Sel("setDrawsBackground:"u8), false);

        var popup = ((delegate* unmanaged<nint, nint, NSRect, byte, nint>)s_msgSend)(
            Send(Class("NSPopUpButton"u8), Sel("alloc"u8)), Sel("initWithFrame:pullsDown:"u8), popupFrame, 0);
        var target = Send(Send(TargetClass(), Sel("alloc"u8)), Sel("init"u8));
        var names = Send(Class("NSMutableArray"u8), Sel("array"u8));
        foreach (var (name, _) in filters) Send(names, Sel("addObject:"u8), NSString(name));
        Send(popup, Sel("addItemsWithTitles:"u8), names);
        Send(popup, Sel("setTarget:"u8), target);
        Send(popup, Sel("setAction:"u8), Sel("selectFormat:"u8));

        Send(accessory, Sel("addSubview:"u8), label);
        Send(accessory, Sel("addSubview:"u8), popup);
        Send(panel, Sel("setAccessoryView:"u8), accessory);
        Send(label, Sel("release"u8));
        Send(popup, Sel("release"u8));
        Send(accessory, Sel("release"u8));
        s_pickers[panel] = new FormatPicker(panel, popup, target, [.. filters.Select(f => f.Types)]);
    }

    // Electron's PopUpButtonHandler selectFormat:.
    [UnmanagedCallersOnly]
    private static void OnSelectFormat(nint self, nint cmd, nint sender)
    {
        foreach (var picker in s_pickers.Values)
        {
            if (picker.Target != self) continue;
            var index = (int)Send(sender, Sel("indexOfSelectedItem"u8));
            if (index < 0 || index >= picker.Types.Count) return;
            Send(picker.Panel, Sel("setAllowedFileTypes:"u8), NSArray(MacPanelSettings.AllowedTypesOf(picker.Types[index])));
            return;
        }
    }

    private static nint TargetClass()
    {
        if (s_targetClass != 0) return s_targetClass;
        fixed (byte* name = "OrielFileFormatTarget"u8)
        {
            var cls = objc_getClass(name);
            if (cls == 0)
            {
                cls = objc_allocateClassPair(Class("NSObject"u8), name, 0);
                fixed (byte* types = "v@:@"u8)
                    class_addMethod(cls, Sel("selectFormat:"u8), (delegate* unmanaged<nint, nint, nint, void>)&OnSelectFormat, types);
                objc_registerClassPair(cls);
            }
            return s_targetClass = cls;
        }
    }

    private static string BookmarkOf(nint url)
    {
        var path = url == 0 ? 0 : Send(url, Sel("path"u8));
        if (path == 0) return "";
        var manager = Send(Class("NSFileManager"u8), Sel("defaultManager"u8));
        if (((delegate* unmanaged<nint, nint, nint, byte>)s_msgSend)(manager, Sel("fileExistsAtPath:"u8), path) == 0)
            ((delegate* unmanaged<nint, nint, nint, nint, nint, byte>)s_msgSend)(manager, Sel("createFileAtPath:contents:attributes:"u8), path, 0, 0);
        nint error = 0;
        var data = ((delegate* unmanaged<nint, nint, nuint, nint, nint, nint*, nint>)s_msgSend)(
            url, Sel("bookmarkDataWithOptions:includingResourceValuesForKeys:relativeToURL:error:"u8), SecurityScope, 0, 0, &error);
        if (error != 0 || data == 0) return "";
        return Read(Send(data, Sel("base64EncodedStringWithOptions:"u8), 0)) ?? "";
    }

    private static Block NewBlock(Action<nint> completed) => new()
    {
        Isa = s_stackBlock,
        Flags = BlockHasSignature,
        Invoke = (nint)(delegate* unmanaged<Block*, nint, void>)&OnCompleted,
        Descriptor = s_descriptor,
        Context = GCHandle.ToIntPtr(GCHandle.Alloc(completed)),
    };

    // AppKit copies the stack block before returning; the copy carries the same handle, invoked once.
    [UnmanagedCallersOnly]
    private static void OnCompleted(Block* block, nint response)
    {
        var handle = GCHandle.FromIntPtr(block->Context);
        var completed = (Action<nint>)handle.Target!;
        handle.Free();
        try { completed(response); }
        catch (Exception e) { Console.Error.WriteLine($"[Oriel] Unhandled exception in a file dialog's completion: {e}"); }
    }

    private static BlockDescriptor* NewDescriptor()
    {
        var descriptor = (BlockDescriptor*)NativeMemory.Alloc((nuint)sizeof(BlockDescriptor));
        descriptor->Reserved = 0;
        descriptor->Size = (nuint)sizeof(Block);
        descriptor->Signature = (byte*)Marshal.StringToCoTaskMemUTF8("v@?q");
        return descriptor;
    }

    private static nint WindowOf(nint contentView) => contentView == 0 ? 0 : Send(contentView, Sel("window"u8));

    private static nint NSString(string value)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try { return Send(Class("NSString"u8), Sel("stringWithUTF8String:"u8), utf8); }
        finally { Marshal.FreeCoTaskMem(utf8); }
    }

    private static nint NSArray(IReadOnlyList<string>? values)
    {
        if (values is null) return 0;
        var array = Send(Class("NSMutableArray"u8), Sel("array"u8));
        foreach (var value in values) Send(array, Sel("addObject:"u8), NSString(value));
        return array;
    }

    private static string? Read(nint nsString) => nsString == 0 ? null : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String"u8)));

    private static nint Class(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return objc_getClass(p);
    }

    private static nint Sel(ReadOnlySpan<byte> name)
    {
        fixed (byte* p = name) return sel_registerName(p);
    }

    private static nint Send(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, nint>)s_msgSend)(receiver, selector);

    private static nint Send(nint receiver, nint selector, nint arg) => ((delegate* unmanaged<nint, nint, nint, nint>)s_msgSend)(receiver, selector, arg);

    private static void SendString(nint receiver, nint selector, string value) => Send(receiver, selector, NSString(value));

    private static void SendBool(nint receiver, nint selector, bool value) => ((delegate* unmanaged<nint, nint, byte, void>)s_msgSend)(receiver, selector, value ? (byte)1 : (byte)0);

    private static bool SendGetBool(nint receiver, nint selector) => ((delegate* unmanaged<nint, nint, byte>)s_msgSend)(receiver, selector) != 0;

    private static nint SendRect(nint receiver, nint selector, NSRect rect) => ((delegate* unmanaged<nint, nint, NSRect, nint>)s_msgSend)(receiver, selector, rect);

    [DllImport(ObjC)] private static extern nint objc_getClass(byte* name);
    [DllImport(ObjC)] private static extern nint sel_registerName(byte* name);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, byte* name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint cls);
    [DllImport(ObjC)] private static extern byte class_addMethod(nint cls, nint name, void* imp, byte* types);
}
