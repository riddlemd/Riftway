using System.Runtime.Versioning;
using Oriel.Cef.Platform;

namespace Oriel;

/// <summary>A task in the jump list's Tasks category. Electron's <c>Task</c>.</summary>
public sealed record UserTask
{
    /// <summary>The program to run.</summary>
    public required string Program { get; init; }

    /// <summary>The program's command line.</summary>
    public string Arguments { get; init; } = "";

    /// <summary>The task's text in the jump list.</summary>
    public required string Title { get; init; }

    /// <summary>The task's tooltip; more than 260 characters drops the task.</summary>
    public string Description { get; init; } = "";

    /// <summary>The file holding the task's icon; the program's icon when null.</summary>
    public string? IconPath { get; init; }

    /// <summary>The icon's index in <see cref="IconPath"/>.</summary>
    public int IconIndex { get; init; }

    /// <summary>The program's working directory.</summary>
    public string? WorkingDirectory { get; init; }
}

/// <summary>What a jump list category holds. Electron's <c>JumpListCategory.type</c>.</summary>
public enum JumpListCategoryType
{
    /// <summary>The standard Tasks category: tasks and separators.</summary>
    Tasks,

    /// <summary>The shell's frequently opened files.</summary>
    Frequent,

    /// <summary>The shell's recently opened files.</summary>
    Recent,

    /// <summary>A named category of tasks and files.</summary>
    Custom,
}

/// <summary>What a jump list item is. Electron's <c>JumpListItem.type</c>.</summary>
public enum JumpListItemType
{
    /// <summary>Runs <see cref="JumpListItem.Program"/>.</summary>
    Task,

    /// <summary>A line between tasks; Tasks category only.</summary>
    Separator,

    /// <summary>Opens <see cref="JumpListItem.Path"/> with its registered handler.</summary>
    File,
}

/// <summary>A category of the jump list. Electron's <c>JumpListCategory</c>.</summary>
public sealed record JumpListCategory
{
    /// <summary>What the category holds.</summary>
    public JumpListCategoryType Type { get; init; }

    /// <summary>The heading of a <see cref="JumpListCategoryType.Custom"/> category; required there.</summary>
    public string? Name { get; init; }

    /// <summary>The items of a Tasks or Custom category; required for those, ignored for the others.</summary>
    public IReadOnlyList<JumpListItem>? Items { get; init; }
}

/// <summary>An item in a jump list category. Electron's <c>JumpListItem</c>.</summary>
public sealed record JumpListItem
{
    /// <summary>What the item is.</summary>
    public JumpListItemType Type { get; init; }

    /// <summary>The file a <see cref="JumpListItemType.File"/> item opens.</summary>
    public string? Path { get; init; }

    /// <summary>The program a <see cref="JumpListItemType.Task"/> runs.</summary>
    public string? Program { get; init; }

    /// <summary>The task's command line.</summary>
    public string Args { get; init; } = "";

    /// <summary>The task's text in the jump list.</summary>
    public string? Title { get; init; }

    /// <summary>The task's tooltip; more than 260 characters drops the task.</summary>
    public string Description { get; init; } = "";

    /// <summary>The file holding the task's icon.</summary>
    public string? IconPath { get; init; }

    /// <summary>The icon's index in <see cref="IconPath"/>.</summary>
    public int IconIndex { get; init; }

    /// <summary>The task's working directory.</summary>
    public string? WorkingDirectory { get; init; }
}

/// <summary>The jump list's limits. Electron's <c>app.getJumpListSettings()</c> result.</summary>
public sealed record JumpListSettings
{
    /// <summary>How many items the jump list shows at least; Windows' default is 10.</summary>
    public int MinItems { get; init; }

    /// <summary>Items the user removed from custom categories; adding them again fails, so they are skipped.</summary>
    public IReadOnlyList<JumpListItem> RemovedItems { get; init; } = [];
}

/// <summary>Electron's <c>JumpListResult</c>; <see cref="OrielApp.JumpListResultText"/> gives its string.</summary>
internal enum JumpListResult
{
    Ok,
    Error,
    InvalidSeparatorError,
    FileTypeRegistrationError,
    CustomCategoryAccessDeniedError,
}

/// <summary>One jump list transaction on the shell: <c>ICustomDestinationList</c>.</summary>
internal interface IJumpListBackend : IDisposable
{
    bool Begin(out int minItems, List<JumpListItem> removed);
    bool Abort();
    bool Commit();
    bool Delete();
    IJumpListItems? CreateItems();
    int AddUserTasks(IJumpListItems items);
    int AppendCategory(string name, IJumpListItems items);
    int AppendKnownCategory(JumpListCategoryType type);
}

/// <summary>A collection of shell objects being built for one category.</summary>
internal interface IJumpListItems : IDisposable
{
    bool AddTask(JumpListItem task);
    bool AddSeparator();
    bool AddFile(string path);
}

public sealed partial class OrielApp
{
    private const int EAccessDenied = unchecked((int)0x80070005);
    private const int FileTypeNotRegistered = unchecked((int)0x80040F03);

    // Longer descriptions make the shell drop the task silently while reporting success.
    private const int MaxDescriptionLength = 260;

    /// <summary>Replaces the jump list's Tasks category with <paramref name="tasks"/>; empty clears it.
    /// Electron's <c>app.setUserTasks()</c>. False if the shell refused, and always on other OSes.</summary>
    /// <exception cref="ArgumentException">A task has no program or title.</exception>
    public bool SetUserTasks(IReadOnlyList<UserTask> tasks)
    {
        using var backend = CreateJumpListBackend();
        return ApplyUserTasks(backend, tasks);
    }

    /// <summary>Replaces the whole jump list; null or empty restores the shell's default. Electron's
    /// <c>app.setJumpList()</c>: returns <c>ok</c>, <c>error</c>, <c>invalidSeparatorError</c>,
    /// <c>fileTypeRegistrationError</c> or <c>customCategoryAccessDeniedError</c>; always <c>error</c> on other OSes.</summary>
    /// <remarks>Commits what it can even when a category fails; the result names the first non-generic failure.
    /// Items in <see cref="JumpListSettings.RemovedItems"/> are skipped.</remarks>
    /// <exception cref="ArgumentException">A category or item lacks a required field: Electron's <c>argumentError</c>.</exception>
    public string SetJumpList(IReadOnlyList<JumpListCategory>? categories)
    {
        using var backend = CreateJumpListBackend();
        return ApplyJumpList(backend, categories);
    }

    /// <summary>The jump list's minimum size and the items the user removed. Electron's
    /// <c>app.getJumpListSettings()</c>; 10 and none if the shell would not say.</summary>
    /// <exception cref="PlatformNotSupportedException">Not Windows; Electron has no such method there either.</exception>
    public JumpListSettings GetJumpListSettings()
    {
        using var backend = CreateJumpListBackend();
        return backend is null ? throw new PlatformNotSupportedException("Jump lists exist only on Windows.") : ReadJumpListSettings(backend);
    }

    internal static IJumpListBackend? CreateJumpListBackend() => OperatingSystem.IsWindows() ? WindowsJumpListBackend.Create() : null;

    /// <summary>Electron's <c>Browser::SetUserTasks</c>: per-task failures are ignored, only the commit counts.
    /// A null backend (no shell, or not Windows) is false.</summary>
    internal static bool ApplyUserTasks(IJumpListBackend? backend, IReadOnlyList<UserTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var items = new List<JumpListItem>(tasks.Count);
        foreach (var task in tasks)
        {
            if (string.IsNullOrEmpty(task.Program) || string.IsNullOrEmpty(task.Title))
                throw new ArgumentException("Every task needs a program and a title.", nameof(tasks));
            items.Add(new JumpListItem
            {
                Type = JumpListItemType.Task,
                Program = task.Program,
                Args = task.Arguments,
                Title = task.Title,
                Description = task.Description,
                IconPath = task.IconPath,
                IconIndex = task.IconIndex,
                WorkingDirectory = task.WorkingDirectory,
            });
        }
        if (backend is null || !backend.Begin(out _, [])) return false;
        AppendCategory(backend, new JumpListCategory { Type = JumpListCategoryType.Tasks }, items);
        return backend.Commit();
    }

    /// <summary>Electron's <c>App::SetJumpList</c> over <paramref name="backend"/>; null is not Windows.</summary>
    internal static string ApplyJumpList(IJumpListBackend? backend, IReadOnlyList<JumpListCategory>? categories)
    {
        if (categories is not null) ValidateJumpList(categories);
        if (backend is null) return JumpListResultText(JumpListResult.Error);
        if (categories is null || categories.Count == 0)
            return JumpListResultText(backend.Delete() ? JumpListResult.Ok : JumpListResult.Error);

        var removed = new List<JumpListItem>();
        if (!backend.Begin(out _, removed)) return JumpListResultText(JumpListResult.Error);
        var result = JumpListResult.Ok;
        foreach (var category in categories)
        {
            var latest = category.Type switch
            {
                JumpListCategoryType.Frequent or JumpListCategoryType.Recent =>
                    backend.AppendKnownCategory(category.Type) < 0 ? JumpListResult.Error : JumpListResult.Ok,
                _ => AppendCategory(backend, category, [.. category.Items!.Where(i => !IsRemoved(i, removed))]),
            };
            // Only one code can be returned, so the first specific one wins over a generic error.
            if (result is JumpListResult.Ok or JumpListResult.Error && latest != JumpListResult.Ok) result = latest;
        }
        if (!backend.Commit() && result == JumpListResult.Ok) result = JumpListResult.Error;
        return JumpListResultText(result);
    }

    /// <summary>Electron's <c>App::GetJumpListSettings</c>: opens a transaction only to read it, then aborts.</summary>
    internal static JumpListSettings ReadJumpListSettings(IJumpListBackend backend)
    {
        var removed = new List<JumpListItem>();
        if (!backend.Begin(out var minItems, removed)) return new JumpListSettings { MinItems = 10 };
        backend.Abort();
        return new JumpListSettings { MinItems = minItems, RemovedItems = removed };
    }

    /// <summary>The fields Electron's converters require; a missing one throws as Electron's <c>argumentError</c> does.</summary>
    /// <remarks>A Custom category with no name is rejected too, where Electron would hand the shell an empty name.</remarks>
    internal static void ValidateJumpList(IReadOnlyList<JumpListCategory> categories)
    {
        foreach (var category in categories)
        {
            if (category.Name is "" || category.Type == JumpListCategoryType.Custom && category.Name is null)
                throw new ArgumentException("A custom category needs a non-empty name.", nameof(categories));
            if (category.Type is not (JumpListCategoryType.Tasks or JumpListCategoryType.Custom)) continue;
            if (category.Items is null)
                throw new ArgumentException($"A {category.Type} category needs items.", nameof(categories));
            foreach (var item in category.Items)
            {
                if (item.Type == JumpListItemType.Task && (string.IsNullOrEmpty(item.Program) || item.Title is null))
                    throw new ArgumentException("A task needs a program and a title.", nameof(categories));
                if (item.Type == JumpListItemType.File && string.IsNullOrEmpty(item.Path))
                    throw new ArgumentException("A file item needs a path.", nameof(categories));
            }
        }
    }

    /// <summary>Electron's <c>JumpList::AppendCategory</c>: adds what it can, then reports one result.</summary>
    internal static JumpListResult AppendCategory(IJumpListBackend backend, JumpListCategory category, IReadOnlyList<JumpListItem> items)
    {
        if (items.Count == 0) return JumpListResult.Ok;
        using var collection = backend.CreateItems();
        if (collection is null) return JumpListResult.Error;

        var result = JumpListResult.Ok;
        var appended = 0;
        foreach (var item in items)
        {
            switch (item.Type)
            {
                case JumpListItemType.Task:
                    if (item.Description.Length <= MaxDescriptionLength && collection.AddTask(item)) appended++;
                    break;
                case JumpListItemType.Separator:
                    if (category.Type != JumpListCategoryType.Tasks) result = JumpListResult.InvalidSeparatorError;
                    else if (collection.AddSeparator()) appended++;
                    break;
                case JumpListItemType.File:
                    if (collection.AddFile(item.Path!)) appended++;
                    break;
            }
        }
        if (appended == 0) return result;
        if (appended < items.Count && result == JumpListResult.Ok) result = JumpListResult.Error;

        if (category.Type == JumpListCategoryType.Tasks)
        {
            if (backend.AddUserTasks(collection) < 0 && result == JumpListResult.Ok) result = JumpListResult.Error;
            return result;
        }
        var hr = backend.AppendCategory(category.Name!, collection);
        if (hr >= 0) return result;
        return hr switch
        {
            FileTypeNotRegistered => JumpListResult.FileTypeRegistrationError,
            EAccessDenied => JumpListResult.CustomCategoryAccessDeniedError,
            _ => result == JumpListResult.Ok ? JumpListResult.Error : result,
        };
    }

    /// <summary>Whether the user removed <paramref name="item"/>: a file by path, a task by program and arguments.</summary>
    internal static bool IsRemoved(JumpListItem item, IReadOnlyList<JumpListItem> removed) => item.Type switch
    {
        JumpListItemType.File => removed.Any(r => r.Type == JumpListItemType.File && string.Equals(r.Path, item.Path, StringComparison.OrdinalIgnoreCase)),
        JumpListItemType.Task => removed.Any(r => r.Type == JumpListItemType.Task
            && string.Equals(r.Program, item.Program, StringComparison.OrdinalIgnoreCase) && r.Args == item.Args),
        _ => false,
    };

    /// <summary>Electron's string for <paramref name="result"/>.</summary>
    internal static string JumpListResultText(JumpListResult result) => result switch
    {
        JumpListResult.Ok => "ok",
        JumpListResult.InvalidSeparatorError => "invalidSeparatorError",
        JumpListResult.FileTypeRegistrationError => "fileTypeRegistrationError",
        JumpListResult.CustomCategoryAccessDeniedError => "customCategoryAccessDeniedError",
        _ => "error",
    };

    /// <summary>The shell's removed destination as Electron reports it.</summary>
    internal static JumpListItem ToJumpListItem(WindowsDestination destination) => destination.IsFile
        ? new JumpListItem { Type = JumpListItemType.File, Path = destination.Path }
        : new JumpListItem
        {
            Type = JumpListItemType.Task,
            Program = destination.Path,
            Args = destination.Arguments,
            Title = destination.Title,
            Description = destination.Description,
            IconPath = destination.IconPath,
            IconIndex = destination.IconIndex,
            WorkingDirectory = destination.WorkingDirectory,
        };
}

/// <summary>The jump list of the process's explicit AppUserModelID, or of the shell's default id for it.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsJumpListBackend : IJumpListBackend
{
    private readonly nint _list;
    private readonly string? _appId;
    private readonly bool _uninitialize;

    private WindowsJumpListBackend(nint list, string? appId, bool uninitialize) => (_list, _appId, _uninitialize) = (list, appId, uninitialize);

    /// <summary>A transaction-ready list, or null if the shell has none.</summary>
    public static WindowsJumpListBackend? Create()
    {
        var uninitialize = WindowsJumpList.InitializeCom();
        var appId = WindowsJumpList.ExplicitAppUserModelId();
        var list = WindowsJumpList.CreateDestinationList(appId);
        if (list != 0) return new WindowsJumpListBackend(list, appId, uninitialize);
        if (uninitialize) WindowsJumpList.UninitializeCom();
        return null;
    }

    public bool Begin(out int minItems, List<JumpListItem> removed)
    {
        var destinations = new List<WindowsDestination>();
        var ok = WindowsJumpList.BeginList(_list, out minItems, destinations) >= 0;
        removed.AddRange(destinations.Select(OrielApp.ToJumpListItem));
        return ok;
    }

    public bool Abort() => WindowsJumpList.AbortList(_list) >= 0;

    public bool Commit() => WindowsJumpList.CommitList(_list) >= 0;

    public bool Delete() => WindowsJumpList.DeleteList(_list, _appId) >= 0;

    public IJumpListItems? CreateItems() => WindowsJumpList.CreateCollection() is var c and not 0 ? new Items(c) : null;

    public int AddUserTasks(IJumpListItems items) => WindowsJumpList.AddUserTasks(_list, ((Items)items).Handle);

    public int AppendCategory(string name, IJumpListItems items) => WindowsJumpList.AppendCategory(_list, name, ((Items)items).Handle);

    public int AppendKnownCategory(JumpListCategoryType type) =>
        WindowsJumpList.AppendKnownCategory(_list, type == JumpListCategoryType.Frequent ? 1 : 2);

    public void Dispose()
    {
        WindowsJumpList.Release(_list);
        if (_uninitialize) WindowsJumpList.UninitializeCom();
    }

    private sealed class Items(nint handle) : IJumpListItems
    {
        public nint Handle { get; } = handle;

        public bool AddTask(JumpListItem task) => WindowsJumpList.AddLink(Handle, task.Program!, task.Args, task.WorkingDirectory,
            task.Description, task.IconPath, task.IconIndex, task.Title!);

        public bool AddSeparator() => WindowsJumpList.AddSeparator(Handle);

        public bool AddFile(string path) => WindowsJumpList.AddFile(Handle, path);

        public void Dispose() => WindowsJumpList.Release(Handle);
    }
}
