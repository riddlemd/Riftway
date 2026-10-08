using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>The kinds of message box; Electron's <c>electron::MessageBoxType</c>, in its order.</summary>
public enum WindowsMessageBoxType
{
    /// <summary>No icon.</summary>
    None = 0,

    /// <summary>The information icon.</summary>
    Information,

    /// <summary>The warning icon.</summary>
    Warning,

    /// <summary>The error icon.</summary>
    Error,

    /// <summary>The information icon: Windows has no question icon for task dialogs.</summary>
    Question,
}

/// <summary>A custom message box icon: 32-bit BGRA with straight alpha, top row first.</summary>
/// <param name="Bgra">Width*Height*4 bytes.</param>
/// <param name="Width">Pixels across.</param>
/// <param name="Height">Pixels down.</param>
public readonly record struct WindowsIconBitmap(byte[] Bgra, int Width, int Height);

/// <summary>What a message box shows; Electron's <c>electron::MessageBoxSettings</c> as Windows reads it.</summary>
public sealed record WindowsMessageBoxSettings
{
    /// <summary>The owner window, or 0 for none.</summary>
    public nint Parent { get; init; }

    /// <summary>Picks the icon when <see cref="Icon"/> is null.</summary>
    public WindowsMessageBoxType Type { get; init; }

    /// <summary>The button labels; empty shows the task dialog's own OK button.</summary>
    public IReadOnlyList<string> Buttons { get; init; } = [];

    /// <summary>The index of the button that starts focused; out of range leaves the first.</summary>
    public int DefaultId { get; init; } = -1;

    /// <summary>The index returned when the box is dismissed without a button of its own.</summary>
    public int CancelId { get; init; }

    /// <summary>Custom buttons stay plain buttons instead of command links, and labels like "OK" stay custom.</summary>
    public bool NoLink { get; init; }

    /// <summary>The window title; empty uses <see cref="AppName"/>.</summary>
    public string Title { get; init; } = "";

    /// <summary>Electron's <c>Browser::GetName()</c>, the title when <see cref="Title"/> is empty.</summary>
    public string AppName { get; init; } = "";

    /// <summary>The main text.</summary>
    public string Message { get; init; } = "";

    /// <summary>Extra text; when set, <see cref="Message"/> becomes the highlighted main instruction.</summary>
    public string Detail { get; init; } = "";

    /// <summary>The checkbox label; empty shows no checkbox.</summary>
    public string CheckboxLabel { get; init; } = "";

    /// <summary>Whether the checkbox starts checked.</summary>
    public bool CheckboxChecked { get; init; }

    /// <summary>A custom icon in place of the type's.</summary>
    public WindowsIconBitmap? Icon { get; init; }
}

/// <summary>Closes one message box from any thread; Electron's dialogs-map entry behind <c>CloseMessageBox</c>.</summary>
/// <remarks>The task dialog reports its window only once created, so a close that comes first is remembered and
/// applied then.</remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsMessageBoxCloser
{
    private const nint Reserve = -1;
    private const nint Cancel = -2;
    private const nint Done = -3;

    private readonly Lock _lock = new();
    private nint _hwnd = Reserve;

    /// <summary>Closes the box as its close button would, so it answers its cancel id; no-op once it has closed.</summary>
    public void Close()
    {
        lock (_lock)
        {
            if (_hwnd == Reserve) _hwnd = Cancel;
            else if (_hwnd > 0) WindowsMessageBox.PostClose(_hwnd);
        }
    }

    internal void Created(nint hwnd)
    {
        lock (_lock)
        {
            if (_hwnd == Cancel) WindowsMessageBox.PostClose(hwnd);
            else _hwnd = hwnd;
        }
    }

    internal void Finished()
    {
        lock (_lock) _hwnd = Done;
    }
}

/// <summary>A button the task dialog shows: its command id and label.</summary>
internal readonly record struct TaskDialogButton(int Id, string Text);

/// <summary>Everything Electron's <c>ShowTaskDialogWstr</c> puts in <c>TASKDIALOGCONFIG</c>, worked out before the call.</summary>
internal sealed record TaskDialogPlan(
    uint Flags,
    uint CommonButtons,
    IReadOnlyList<TaskDialogButton> Buttons,
    int DefaultButton,
    string WindowTitle,
    nint MainIcon,
    string? MainInstruction,
    string Content,
    string? VerificationText,
    IReadOnlyDictionary<int, int> IdMap,
    IReadOnlySet<int> ButtonIds);

/// <summary>Message boxes as Electron's <c>shell/browser/ui/message_box_win.cc</c> shows them: a comctl32 v6
/// <c>TaskDialogIndirect</c>.</summary>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsMessageBox
{
    // Small command ids are Windows'; custom buttons start here.
    internal const int IdStart = 100;

    internal const uint TdfUseHiconMain = 0x2;
    internal const uint TdfAllowDialogCancellation = 0x8;
    internal const uint TdfUseCommandLinks = 0x10;
    internal const uint TdfVerificationFlagChecked = 0x100;
    internal const uint TdfPositionRelativeToWindow = 0x1000;
    internal const uint TdfSizeToContent = 0x1000000;

    internal const uint OkButton = 0x1, YesButton = 0x2, NoButton = 0x4, CancelButton = 0x8, RetryButton = 0x10, CloseButton = 0x20;
    internal const int IdOk = 1, IdCancel = 2, IdRetry = 4, IdYes = 6, IdNo = 7, IdClose = 8;

    // MAKEINTRESOURCEW(-1), (-2), (-3).
    internal const nint WarningIcon = 0xFFFF, ErrorIcon = 0xFFFE, InformationIcon = 0xFFFD;

    private const uint TdnCreated = 0;
    private const uint TdnButtonClicked = 2;

    /// <summary>Shows the box on the calling thread and waits: the index of the button chosen (or the cancel id) and
    /// whether the checkbox was checked. Electron's <c>ShowTaskDialogWstr</c>.</summary>
    /// <param name="settings">What to show.</param>
    /// <param name="closer">Lets another thread close the box; null when nothing will.</param>
    public static (int Response, bool CheckboxChecked) Show(WindowsMessageBoxSettings settings, WindowsMessageBoxCloser? closer)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var icon = settings.Icon is { } bitmap ? WindowsTaskbar.CreateIconHandle(bitmap.Bgra, bitmap.Width, bitmap.Height) : 0;
        var plan = PlanOf(settings, settings.Parent != 0 && IsWindowEnabled(settings.Parent) != 0, icon != 0);
        var strings = new List<nint>();
        var buttons = (TaskDialogButtonNative*)NativeMemory.AllocZeroed((nuint)Math.Max(1, plan.Buttons.Count), (nuint)sizeof(TaskDialogButtonNative));
        var state = GCHandle.Alloc(new CallbackState(closer, plan.ButtonIds));
        try
        {
            char* Text(string? text)
            {
                if (text is null) return null;
                var native = Marshal.StringToHGlobalUni(text);
                strings.Add(native);
                return (char*)native;
            }

            for (var i = 0; i < plan.Buttons.Count; i++)
                buttons[i] = new TaskDialogButtonNative { ButtonId = plan.Buttons[i].Id, ButtonText = Text(plan.Buttons[i].Text) };
            var config = new TaskDialogConfig
            {
                Size = (uint)sizeof(TaskDialogConfig),
                Parent = (plan.Flags & TdfPositionRelativeToWindow) != 0 ? settings.Parent : 0,
                Instance = GetModuleHandleW(null),
                Flags = plan.Flags,
                CommonButtons = plan.CommonButtons,
                WindowTitle = Text(plan.WindowTitle),
                MainIcon = icon != 0 ? icon : plan.MainIcon,
                MainInstruction = Text(plan.MainInstruction),
                Content = Text(plan.Content),
                ButtonCount = (uint)plan.Buttons.Count,
                Buttons = plan.Buttons.Count > 0 ? buttons : null,
                DefaultButton = plan.DefaultButton,
                VerificationText = Text(plan.VerificationText),
                Callback = &OnNotify,
                CallbackData = GCHandle.ToIntPtr(state),
            };

            int id = 0, verified = 0;
            var hr = CommonControls.TaskDialogIndirect(&config, &id, &verified);
            if (hr < 0) Console.Error.WriteLine($"[Oriel] TaskDialogIndirect failed: 0x{hr:X8}");
            return (ResponseOf(plan, id, settings.CancelId), verified != 0);
        }
        finally
        {
            closer?.Finished();
            state.Free();
            foreach (var s in strings) Marshal.FreeHGlobal(s);
            NativeMemory.Free(buttons);
            WindowsTaskbar.DestroyIconHandle(icon);
        }
    }

    /// <summary>Electron's <c>GetCommonID</c>: the common-button flag and command id for an ASCII-case-insensitive
    /// "ok", "yes", "no", "cancel", "retry" or "close"; (0, -1) for any other label.</summary>
    internal static (uint Button, int Id) CommonIdOf(string button)
        => AsciiLower(button) switch
        {
            "ok" => (OkButton, IdOk),
            "yes" => (YesButton, IdYes),
            "no" => (NoButton, IdNo),
            "cancel" => (CancelButton, IdCancel),
            "retry" => (RetryButton, IdRetry),
            "close" => (CloseButton, IdClose),
            _ => (0, -1),
        };

    /// <summary>The task dialog's configuration for <paramref name="settings"/>; Electron's <c>ShowTaskDialogWstr</c>
    /// up to the call.</summary>
    /// <param name="settings">What to show.</param>
    /// <param name="parentEnabled">The parent exists and is enabled; a parent already disabled by another modal box
    /// gets no owned box.</param>
    /// <param name="hasIcon">A custom icon replaces the type's.</param>
    internal static TaskDialogPlan PlanOf(WindowsMessageBoxSettings settings, bool parentEnabled, bool hasIcon)
    {
        var flags = TdfSizeToContent | TdfAllowDialogCancellation;
        if (parentEnabled) flags |= TdfPositionRelativeToWindow;

        var labels = settings.Buttons;
        var defaultButton = 0;
        if (settings.DefaultId >= 0 && settings.DefaultId < labels.Count)
        {
            defaultButton = !settings.NoLink && CommonIdOf(labels[settings.DefaultId]) is { Id: not -1 } common
                ? common.Id
                : IdStart + settings.DefaultId;
        }

        var title = settings.Title.Length == 0 ? settings.AppName : settings.Title;

        nint mainIcon = 0;
        if (hasIcon) flags |= TdfUseHiconMain;
        else
        {
            mainIcon = settings.Type switch
            {
                WindowsMessageBoxType.Information or WindowsMessageBoxType.Question => InformationIcon,
                WindowsMessageBoxType.Warning => WarningIcon,
                WindowsMessageBoxType.Error => ErrorIcon,
                _ => 0,
            };
        }

        // An empty detail leaves the message unhighlighted.
        string? mainInstruction = null;
        var content = settings.Message;
        if (settings.Detail.Length > 0)
        {
            mainInstruction = settings.Message;
            content = settings.Detail;
        }

        string? verification = null;
        if (settings.CheckboxLabel.Length > 0)
        {
            verification = settings.CheckboxLabel;
            if (settings.CheckboxChecked) flags |= TdfVerificationFlagChecked;
        }

        var idMap = new Dictionary<int, int>();
        uint commonButtons = 0;
        var buttons = new List<TaskDialogButton>();
        for (var i = 0; i < labels.Count; i++)
        {
            if (!settings.NoLink && CommonIdOf(labels[i]) is { Id: not -1 } common)
            {
                idMap[common.Id] = i;
                commonButtons |= common.Button;
            }
            else buttons.Add(new TaskDialogButton(IdStart + i, labels[i]));
        }
        if (buttons.Count > 0 && !settings.NoLink) flags |= TdfUseCommandLinks;

        var ids = new HashSet<int>(idMap.Keys);
        foreach (var b in buttons) ids.Add(b.Id);
        // With no buttons the task dialog shows its own OK, which must still answer.
        if (ids.Count == 0) ids.Add(IdOk);

        return new TaskDialogPlan(flags, commonButtons, buttons, defaultButton, title, mainIcon, mainInstruction, content, verification, idMap, ids);
    }

    /// <summary>The button index for the command id the dialog returned; Electron maps anything that is not one of
    /// its buttons (Esc, the close button, a failed call) to <paramref name="cancelId"/>.</summary>
    internal static int ResponseOf(TaskDialogPlan plan, int id, int cancelId)
    {
        if (plan.IdMap.TryGetValue(id, out var index)) return index;
        if (id >= IdStart && plan.ButtonIds.Contains(id)) return id - IdStart;
        return cancelId;
    }

    /// <summary>Whether a click on <paramref name="id"/> may close the box: clicks for ids it never created are
    /// injected by other software and ignored, but IDCANCEL (Esc, the close button) always counts.</summary>
    internal static bool AcceptsClick(IReadOnlySet<int> buttonIds, int id) => id == IdCancel || buttonIds.Contains(id);

    internal static void PostClose(nint hwnd) => PostMessageW(hwnd, 0x0010 /* WM_CLOSE */, 0, 0);

    private static string AsciiLower(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (chars[i] is >= 'A' and <= 'Z') chars[i] = (char)(chars[i] + 32);
        return new string(chars);
    }

    private sealed record CallbackState(WindowsMessageBoxCloser? Closer, IReadOnlySet<int> ButtonIds);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnNotify(nint hwnd, uint message, nint wParam, nint lParam, nint data)
    {
        try
        {
            if (GCHandle.FromIntPtr(data).Target is not CallbackState state) return 0;
            if (message == TdnCreated) state.Closer?.Created(hwnd);
            else if (message == TdnButtonClicked && !AcceptsClick(state.ButtonIds, (int)wParam)) return 1; // S_FALSE keeps it open
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Oriel] Message box callback failed: {e}");
        }
        return 0;
    }

    /// <summary><c>TASKDIALOG_BUTTON</c>; commctrl.h packs it to 1.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct TaskDialogButtonNative
    {
        public int ButtonId;
        public char* ButtonText;
    }

    /// <summary><c>TASKDIALOGCONFIG</c>; commctrl.h packs it to 1, so on 64-bit the pointers after <c>cbSize</c>
    /// sit unaligned (160 bytes in all). Each icon union is one pointer-sized field.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct TaskDialogConfig
    {
        public uint Size;
        public nint Parent;
        public nint Instance;
        public uint Flags;
        public uint CommonButtons;
        public char* WindowTitle;
        public nint MainIcon;
        public char* MainInstruction;
        public char* Content;
        public uint ButtonCount;
        public TaskDialogButtonNative* Buttons;
        public int DefaultButton;
        public uint RadioButtonCount;
        public TaskDialogButtonNative* RadioButtons;
        public int DefaultRadioButton;
        public char* VerificationText;
        public char* ExpandedInformation;
        public char* ExpandedControlText;
        public char* CollapsedControlText;
        public nint FooterIcon;
        public char* Footer;
        public delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint, int> Callback;
        public nint CallbackData;
        public uint Width;
    }

    [DllImport("user32.dll")] private static extern int IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern int PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("kernel32.dll")] private static extern nint GetModuleHandleW(char* name);
}

/// <summary>comctl32 version 6, which alone has <c>TaskDialogIndirect</c>. Electron's exe manifest asks for it for
/// the whole process; Oriel's need not, so the call runs inside an activation context that asks for it.</summary>
[SupportedOSPlatform("windows")]
internal static unsafe class CommonControls
{
    private const string Manifest = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
          <dependency><dependentAssembly>
            <assemblyIdentity type="win32" name="Microsoft.Windows.Common-Controls" version="6.0.0.0" processorArchitecture="*" publicKeyToken="6595b64144ccf1df" language="*"/>
          </dependentAssembly></dependency>
        </assembly>
        """;

    private static readonly Lock s_lock = new();
    private static nint s_context;
    private static delegate* unmanaged[Stdcall]<WindowsMessageBox.TaskDialogConfig*, int*, int*, int*, int> s_taskDialogIndirect;

    /// <summary>Runs <c>TaskDialogIndirect</c> with comctl32 v6 active on this thread; its HRESULT.</summary>
    internal static int TaskDialogIndirect(WindowsMessageBox.TaskDialogConfig* config, int* button, int* verified)
    {
        var context = Context();
        nuint cookie = 0;
        var activated = context != -1 && ActivateActCtx(context, &cookie) != 0;
        try
        {
            var function = Resolve();
            if (function is null) return unchecked((int)0x80004005); // E_FAIL
            return function(config, button, null, verified);
        }
        finally
        {
            if (activated) DeactivateActCtx(0, cookie);
        }
    }

    // CreateActCtx reads the manifest while it builds the context, so the file is gone again right after.
    private static nint Context()
    {
        lock (s_lock)
        {
            if (s_context != 0) return s_context;
            var path = Path.Combine(Path.GetTempPath(), $"oriel-comctl6-{Environment.ProcessId}-{Guid.NewGuid():N}.manifest");
            try
            {
                File.WriteAllText(path, Manifest);
                fixed (char* source = path)
                {
                    var actCtx = new ActCtx { Size = (uint)sizeof(ActCtx), Source = source };
                    s_context = CreateActCtxW(&actCtx);
                }
                if (s_context == -1) Console.Error.WriteLine($"[Oriel] Could not activate comctl32 v6: error {GetLastError()}");
            }
            catch (IOException e)
            {
                s_context = -1;
                Console.Error.WriteLine($"[Oriel] Could not write the comctl32 v6 manifest: {e.Message}");
            }
            finally
            {
                try { File.Delete(path); } catch (IOException) { }
            }
            return s_context;
        }
    }

    // Loaded with the context active, so the name resolves to v6 even when v5 is already in the process.
    private static delegate* unmanaged[Stdcall]<WindowsMessageBox.TaskDialogConfig*, int*, int*, int*, int> Resolve()
    {
        lock (s_lock)
        {
            if (s_taskDialogIndirect is not null) return s_taskDialogIndirect;
            nint module;
            fixed (char* name = "comctl32.dll") module = LoadLibraryW(name);
            if (module == 0) return null;
            var proc = GetProcAddress(module, "TaskDialogIndirect"u8);
            s_taskDialogIndirect = (delegate* unmanaged[Stdcall]<WindowsMessageBox.TaskDialogConfig*, int*, int*, int*, int>)proc;
            if (proc == 0) Console.Error.WriteLine("[Oriel] comctl32 has no TaskDialogIndirect: version 6 is not active.");
            return s_taskDialogIndirect;
        }
    }

    private static nint GetProcAddress(nint module, ReadOnlySpan<byte> name)
    {
        Span<byte> zeroTerminated = stackalloc byte[name.Length + 1];
        name.CopyTo(zeroTerminated);
        zeroTerminated[^1] = 0;
        fixed (byte* p = zeroTerminated) return GetProcAddressNative(module, p);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActCtx
    {
        public uint Size;
        public uint Flags;
        public char* Source;
        public ushort ProcessorArchitecture;
        public ushort LangId;
        public char* AssemblyDirectory;
        public char* ResourceName;
        public char* ApplicationName;
        public nint Module;
    }

    [DllImport("kernel32.dll")] private static extern nint CreateActCtxW(ActCtx* actCtx);
    [DllImport("kernel32.dll")] private static extern uint GetLastError();
    [DllImport("kernel32.dll")] private static extern int ActivateActCtx(nint context, nuint* cookie);
    [DllImport("kernel32.dll")] private static extern int DeactivateActCtx(uint flags, nuint cookie);
    [DllImport("kernel32.dll")] private static extern nint LoadLibraryW(char* name);
    [DllImport("kernel32.dll", EntryPoint = "GetProcAddress")] private static extern nint GetProcAddressNative(nint module, byte* name);
}
