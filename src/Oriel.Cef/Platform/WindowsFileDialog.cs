using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Oriel.Cef.Platform;

/// <summary>What to set on a Windows file dialog before it shows, in the order Electron's <c>ApplySettings</c> sets it.</summary>
/// <param name="Options">The <c>FOS_</c> flags for <c>IFileDialog::SetOptions</c>.</param>
/// <param name="FileName">The name box's text; empty for none.</param>
/// <param name="Title">The title; null or empty keeps the system's.</param>
/// <param name="OkButtonLabel">The accept button's label; null or empty keeps the system's.</param>
/// <param name="FileTypes">The file type choices as (name, <c>*.ext;*.ext</c> spec) pairs.</param>
/// <param name="FileTypeIndex">The one-based file type to select, or 0 to leave the selection alone.</param>
/// <param name="DefaultExtension">The extension to append to typed names, without the dot; null for none.</param>
/// <param name="Folder">The folder to open in; null, or one that does not exist, leaves the system's choice.</param>
public sealed record WindowsFileDialogSettings(
    uint Options,
    string FileName,
    string? Title,
    string? OkButtonLabel,
    IReadOnlyList<(string Name, string Spec)> FileTypes,
    int FileTypeIndex,
    string? DefaultExtension,
    string? Folder);

/// <summary>The shell's <c>IFileOpenDialog</c> and <c>IFileSaveDialog</c>, run the way Electron's
/// <c>file_dialog_win.cc</c> runs them.</summary>
/// <remarks>Each dialog runs on its own STA thread, as Electron's <c>dialog_thread</c> does, so the UI thread is
/// free to answer the owner window's messages while it is up.</remarks>
[SupportedOSPlatform("windows")]
public static unsafe class WindowsFileDialog
{
    private static readonly Guid s_clsidFileOpenDialog = new("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7");
    private static readonly Guid s_clsidFileSaveDialog = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");
    private static readonly Guid s_iidFileOpenDialog = new("d57c7288-d4ad-4768-be02-9d969532d960");
    private static readonly Guid s_iidFileSaveDialog = new("84bccd23-5fde-4cdb-aea4-af64b83d78ab");
    private static readonly Guid s_iidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid s_folderIdDownloads = new("374de290-123f-4565-9164-39c4925e467b");

    // IFileDialog vtable slots (IModalWindow::Show first, after IUnknown's three); ShObjIdl_core.h.
    private const int ShowSlot = 3;
    private const int SetFileTypesSlot = 4;
    private const int SetFileTypeIndexSlot = 5;
    private const int SetOptionsSlot = 9;
    private const int SetFolderSlot = 12;
    private const int SetFileNameSlot = 15;
    private const int SetTitleSlot = 17;
    private const int SetOkButtonLabelSlot = 18;
    private const int GetResultSlot = 20;
    private const int SetDefaultExtensionSlot = 22;
    private const int GetResultsSlot = 27; // IFileOpenDialog
    private const int GetDisplayNameSlot = 5; // IShellItem
    private const int GetCountSlot = 7; // IShellItemArray
    private const int GetItemAtSlot = 8; // IShellItemArray

    private const uint ClsctxAll = 0x17; // CComPtr::CoCreateInstance's default
    private const int SigdnFileSysPath = unchecked((int)0x80058000);

    /// <summary>Runs <paramref name="show"/> on a new STA thread of its own; Electron's <c>dialog_thread::Run</c>.</summary>
    /// <remarks>A dedicated thread per dialog keeps a modal dialog in one window from holding up one in another.</remarks>
    public static Task<T> RunOnDialogThread<T>(Func<T> show)
    {
        ArgumentNullException.ThrowIfNull(show);
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.TrySetResult(show()); }
            catch (Exception e) { done.TrySetException(e); }
        }) { IsBackground = true, Name = "Oriel file dialog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    /// <summary>Waits for <paramref name="task"/> while dispatching this thread's window messages, as a modal dialog's
    /// own loop would; a <c>WM_QUIT</c> seen meanwhile is posted again afterwards.</summary>
    /// <remarks>For a UI thread that must keep answering an owner window's messages while a dialog on another thread
    /// is up.</remarks>
    public static void WaitPumping(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        var handle = ((IAsyncResult)task).AsyncWaitHandle.SafeWaitHandle.DangerousGetHandle();
        int? quit = null;
        Msg msg;
        while (!task.IsCompleted)
        {
            if (MsgWaitForMultipleObjectsEx(1, &handle, Infinite, QsAllInput, MwmoInputAvailable) == 0) break;
            while (PeekMessageW(&msg, 0, 0, 0, PmRemove) != 0)
            {
                if (msg.Message == WmQuit) { quit = (int)msg.WParam; continue; }
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
        }
        if (quit is { } code) PostQuitMessage(code);
    }

    /// <summary>Shows an open dialog owned by <paramref name="owner"/> (0 for none) and waits: true with the chosen
    /// paths, or false when cancelled or failed. Electron's <c>ShowOpenDialogSyncWithParent</c>. STA threads only.</summary>
    public static (bool Ok, List<string> Paths) ShowOpen(nint owner, WindowsFileDialogSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var paths = new List<string>();
        var clsid = s_clsidFileOpenDialog;
        var iid = s_iidFileOpenDialog;
        void* dialog;
        if (CoCreateInstance(&clsid, null, ClsctxAll, &iid, &dialog) < 0) return (false, paths);
        void* items = null;
        try
        {
            _ = ((delegate* unmanaged<void*, uint, int>)Slot(dialog, SetOptionsSlot))(dialog, settings.Options);
            Apply(dialog, settings);
            if (((delegate* unmanaged<void*, nint, int>)Slot(dialog, ShowSlot))(dialog, owner) < 0) return (false, paths);
            if (((delegate* unmanaged<void*, void**, int>)Slot(dialog, GetResultsSlot))(dialog, &items) < 0) return (false, paths);
            uint count;
            if (((delegate* unmanaged<void*, uint*, int>)Slot(items, GetCountSlot))(items, &count) < 0) return (false, paths);
            for (uint i = 0; i < count; i++)
            {
                void* item;
                if (((delegate* unmanaged<void*, uint, void**, int>)Slot(items, GetItemAtSlot))(items, i, &item) < 0) return (false, paths);
                var path = FileSystemPath(item);
                Release(item);
                if (path is null) return (false, paths);
                paths.Add(path);
            }
            return (true, paths);
        }
        finally
        {
            Release(items);
            Release(dialog);
        }
    }

    /// <summary>Shows a save dialog owned by <paramref name="owner"/> (0 for none) and waits: the chosen path, or null
    /// when cancelled or failed. Electron's <c>ShowSaveDialogSyncWithParent</c>. STA threads only.</summary>
    public static string? ShowSave(nint owner, WindowsFileDialogSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var clsid = s_clsidFileSaveDialog;
        var iid = s_iidFileSaveDialog;
        void* dialog;
        if (CoCreateInstance(&clsid, null, ClsctxAll, &iid, &dialog) < 0) return null;
        void* item = null;
        try
        {
            _ = ((delegate* unmanaged<void*, uint, int>)Slot(dialog, SetOptionsSlot))(dialog, settings.Options);
            Apply(dialog, settings);
            if (((delegate* unmanaged<void*, nint, int>)Slot(dialog, ShowSlot))(dialog, owner) < 0) return null;
            if (((delegate* unmanaged<void*, void**, int>)Slot(dialog, GetResultSlot))(dialog, &item) < 0) return null;
            return FileSystemPath(item);
        }
        finally
        {
            Release(item);
            Release(dialog);
        }
    }

    /// <summary>The Downloads known folder, Chromium's <c>DIR_DEFAULT_DOWNLOADS</c> on Windows; Documents\Downloads
    /// when the shell cannot say.</summary>
    public static string DownloadsFolder()
    {
        var id = s_folderIdDownloads;
        char* path;
        if (SHGetKnownFolderPath(&id, 0, 0, &path) >= 0)
        {
            var result = new string(path);
            CoTaskMemFree(path);
            return result;
        }
        CoTaskMemFree(path);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Downloads");
    }

    // Electron's ApplySettings after SetOptions: name, title, button, types, type index and extension, then folder.
    private static void Apply(void* dialog, WindowsFileDialogSettings settings)
    {
        fixed (char* name = settings.FileName)
            _ = ((delegate* unmanaged<void*, char*, int>)Slot(dialog, SetFileNameSlot))(dialog, name);
        if (!string.IsNullOrEmpty(settings.Title))
            fixed (char* title = settings.Title)
                _ = ((delegate* unmanaged<void*, char*, int>)Slot(dialog, SetTitleSlot))(dialog, title);
        if (!string.IsNullOrEmpty(settings.OkButtonLabel))
            fixed (char* label = settings.OkButtonLabel)
                _ = ((delegate* unmanaged<void*, char*, int>)Slot(dialog, SetOkButtonLabelSlot))(dialog, label);

        if (settings.FileTypes.Count > 0)
        {
            var specs = new FilterSpec[settings.FileTypes.Count];
            try
            {
                for (var i = 0; i < specs.Length; i++)
                    specs[i] = new FilterSpec
                    {
                        Name = (char*)Marshal.StringToHGlobalUni(settings.FileTypes[i].Name),
                        Spec = (char*)Marshal.StringToHGlobalUni(settings.FileTypes[i].Spec),
                    };
                fixed (FilterSpec* first = specs)
                    _ = ((delegate* unmanaged<void*, uint, FilterSpec*, int>)Slot(dialog, SetFileTypesSlot))(dialog, (uint)specs.Length, first);
            }
            finally
            {
                foreach (var spec in specs)
                {
                    Marshal.FreeHGlobal((nint)spec.Name);
                    Marshal.FreeHGlobal((nint)spec.Spec);
                }
            }
        }

        if (settings.FileTypeIndex > 0)
        {
            _ = ((delegate* unmanaged<void*, uint, int>)Slot(dialog, SetFileTypeIndexSlot))(dialog, (uint)settings.FileTypeIndex);
            fixed (char* extension = settings.DefaultExtension)
                _ = ((delegate* unmanaged<void*, char*, int>)Slot(dialog, SetDefaultExtensionSlot))(dialog, extension);
        }

        if (settings.Folder is null) return;
        var iid = s_iidShellItem;
        void* folder;
        fixed (char* directory = settings.Folder)
            if (SHCreateItemFromParsingName(directory, null, &iid, &folder) < 0) return;
        _ = ((delegate* unmanaged<void*, void*, int>)Slot(dialog, SetFolderSlot))(dialog, folder);
        Release(folder);
    }

    private static string? FileSystemPath(void* item)
    {
        char* name = null;
        if (((delegate* unmanaged<void*, int, char**, int>)Slot(item, GetDisplayNameSlot))(item, SigdnFileSysPath, &name) < 0) return null;
        var path = new string(name);
        CoTaskMemFree(name);
        return path;
    }

    private static void* Slot(void* com, int index) => (*(void***)com)[index];

    private static void Release(void* com)
    {
        if (com is not null) ((delegate* unmanaged<void*, uint>)Slot(com, 2))(com);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FilterSpec
    {
        public char* Name;
        public char* Spec;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    private const uint Infinite = 0xFFFFFFFF;
    private const uint QsAllInput = 0x04FF;
    private const uint MwmoInputAvailable = 0x4;
    private const uint PmRemove = 0x1;
    private const uint WmQuit = 0x12;

    [DllImport("ole32.dll")] private static extern int CoCreateInstance(Guid* clsid, void* outer, uint context, Guid* iid, void** result);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(void* memory);
    [DllImport("shell32.dll")] private static extern int SHCreateItemFromParsingName(char* path, void* bindContext, Guid* iid, void** result);
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(Guid* id, uint flags, nint token, char** path);
    [DllImport("user32.dll")] private static extern uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint milliseconds, uint wakeMask, uint flags);
    [DllImport("user32.dll")] private static extern int PeekMessageW(Msg* msg, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern int TranslateMessage(Msg* msg);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(Msg* msg);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
}
