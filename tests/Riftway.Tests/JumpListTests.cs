using System.Text;
using Riftway.Cef.Platform;

namespace Riftway.Tests;

public sealed class JumpListTests
{
    private const int EAccessDenied = unchecked((int)0x80070005);
    private const int EFail = unchecked((int)0x80004005);

    private sealed class FakeItems(FakeBackend owner) : IJumpListItems
    {
        public List<string> Added { get; } = [];
        public List<JumpListItem> Tasks { get; } = [];

        public bool AddTask(JumpListItem task)
        {
            if (owner.FailTasks.Contains(task.Title!)) return false;
            Added.Add("task:" + task.Title);
            Tasks.Add(task);
            return true;
        }

        public bool AddSeparator()
        {
            Added.Add("separator");
            return true;
        }

        public bool AddFile(string path)
        {
            Added.Add("file:" + path);
            return true;
        }

        public void Dispose() { }
    }

    private sealed class FakeBackend : IJumpListBackend
    {
        public bool BeginOk { get; set; } = true;
        public bool CommitOk { get; set; } = true;
        public bool DeleteOk { get; set; } = true;
        public int MinItems { get; set; } = 7;
        public List<JumpListItem> Removed { get; } = [];
        public HashSet<string> FailTasks { get; } = [];
        public int UserTasksHr { get; set; }
        public Dictionary<string, int> CategoryHr { get; } = [];
        public int KnownHr { get; set; }
        public List<string> Calls { get; } = [];
        public List<FakeItems> Collections { get; } = [];

        public bool Begin(out int minItems, List<JumpListItem> removed)
        {
            Calls.Add("begin");
            minItems = MinItems;
            removed.AddRange(Removed);
            return BeginOk;
        }

        public bool Abort()
        {
            Calls.Add("abort");
            return true;
        }

        public bool Commit()
        {
            Calls.Add("commit");
            return CommitOk;
        }

        public bool Delete()
        {
            Calls.Add("delete");
            return DeleteOk;
        }

        public IJumpListItems? CreateItems()
        {
            var items = new FakeItems(this);
            Collections.Add(items);
            return items;
        }

        public int AddUserTasks(IJumpListItems items)
        {
            Calls.Add("tasks[" + string.Join(",", ((FakeItems)items).Added) + "]");
            return UserTasksHr;
        }

        public int AppendCategory(string name, IJumpListItems items)
        {
            Calls.Add(name + "[" + string.Join(",", ((FakeItems)items).Added) + "]");
            return CategoryHr.GetValueOrDefault(name);
        }

        public int AppendKnownCategory(JumpListCategoryType type)
        {
            Calls.Add("known:" + type);
            return KnownHr;
        }

        public void Dispose() { }
    }

    private sealed class FakeRecent : IRecentDocumentsBackend
    {
        public List<string> Added { get; } = [];
        public void Add(string path) => Added.Add(path);
        public int Cleared { get; private set; }
        public void Clear() => Cleared++;
        public IReadOnlyList<string> List() => Added;
    }

    private static JumpListItem Task(string title, string description = "") =>
        new() { Type = JumpListItemType.Task, Program = @"C:\app.exe", Title = title, Description = description };

    private static readonly JumpListItem s_separator = new() { Type = JumpListItemType.Separator };

    private static JumpListItem File(string path) => new() { Type = JumpListItemType.File, Path = path };

    private static JumpListCategory Custom(string name, params JumpListItem[] items) =>
        new() { Type = JumpListCategoryType.Custom, Name = name, Items = items };

    private static JumpListCategory Tasks(params JumpListItem[] items) => new() { Type = JumpListCategoryType.Tasks, Items = items };

    [Theory]
    [InlineData(nameof(JumpListResult.Ok), "ok")]
    [InlineData(nameof(JumpListResult.Error), "error")]
    [InlineData(nameof(JumpListResult.InvalidSeparatorError), "invalidSeparatorError")]
    [InlineData(nameof(JumpListResult.FileTypeRegistrationError), "fileTypeRegistrationError")]
    [InlineData(nameof(JumpListResult.CustomCategoryAccessDeniedError), "customCategoryAccessDeniedError")]
    public void ResultTextIsElectrons(string result, string text) =>
        Assert.Equal(text, RiftwayApp.JumpListResultText(Enum.Parse<JumpListResult>(result)));

    [Fact]
    public void NullCategoriesDeleteTheList()
    {
        var backend = new FakeBackend();
        Assert.Equal("ok", RiftwayApp.ApplyJumpList(backend, null));
        Assert.Equal(["delete"], backend.Calls);
    }

    [Fact]
    public void EmptyCategoriesCommitAnEmptyTransaction()
    {
        var backend = new FakeBackend();
        Assert.Equal("ok", RiftwayApp.ApplyJumpList(backend, []));
        Assert.Equal(["begin", "commit"], backend.Calls);
    }

    [Fact]
    public void RefusedDeleteIsError() => Assert.Equal("error", RiftwayApp.ApplyJumpList(new FakeBackend { DeleteOk = false }, null));

    [Fact]
    public void NoBackendIsErrorAndFalse()
    {
        Assert.Equal("error", RiftwayApp.ApplyJumpList(null, [Tasks(Task("a"))]));
        Assert.False(RiftwayApp.ApplyUserTasks(null, []));
    }

    [Fact]
    public void RefusedBeginIsErrorAndAddsNothing()
    {
        var backend = new FakeBackend { BeginOk = false };
        Assert.Equal("error", RiftwayApp.ApplyJumpList(backend, [Tasks(Task("a"))]));
        Assert.Equal(["begin"], backend.Calls);
    }

    [Fact]
    public void TasksTakeSeparatorsAndCommit()
    {
        var backend = new FakeBackend();
        Assert.Equal("ok", RiftwayApp.ApplyJumpList(backend, [Tasks(Task("a"), s_separator, Task("b"))]));
        Assert.Equal(["begin", "tasks[task:a,separator,task:b]", "commit"], backend.Calls);
    }

    [Fact]
    public void SeparatorInCustomCategoryIsInvalidButTheRestIsAdded()
    {
        var backend = new FakeBackend();
        Assert.Equal("invalidSeparatorError", RiftwayApp.ApplyJumpList(backend, [Custom("C", Task("a"), s_separator)]));
        Assert.Equal(["begin", "C[task:a]", "commit"], backend.Calls);
    }

    [Fact]
    public void CategoryWithOnlyASeparatorIsNotAppended()
    {
        var backend = new FakeBackend();
        Assert.Equal("invalidSeparatorError", RiftwayApp.ApplyJumpList(backend, [Custom("C", s_separator)]));
        Assert.Equal(["begin", "commit"], backend.Calls);
    }

    [Theory]
    [InlineData(EAccessDenied, "customCategoryAccessDeniedError")]
    [InlineData(unchecked((int)0x80040F03), "fileTypeRegistrationError")]
    [InlineData(EFail, "error")]
    public void AppendCategoryFailuresMapToElectronsCodes(int hr, string expected)
    {
        var backend = new FakeBackend();
        backend.CategoryHr["C"] = hr;
        Assert.Equal(expected, RiftwayApp.ApplyJumpList(backend, [Custom("C", File(@"C:\a.txt"))]));
        Assert.Contains("commit", backend.Calls);
    }

    [Fact]
    public void RefusedUserTasksIsError() =>
        Assert.Equal("error", RiftwayApp.ApplyJumpList(new FakeBackend { UserTasksHr = EFail }, [Tasks(Task("a"))]));

    [Fact]
    public void PartlyAddedCategoryIsErrorButStillAppended()
    {
        var backend = new FakeBackend();
        backend.FailTasks.Add("bad");
        Assert.Equal("error", RiftwayApp.ApplyJumpList(backend, [Custom("C", Task("bad"), Task("good"))]));
        Assert.Contains("C[task:good]", backend.Calls);
    }

    [Fact]
    public void DescriptionOver260DropsTheTask()
    {
        var backend = new FakeBackend();
        Assert.Equal("error", RiftwayApp.ApplyJumpList(backend, [Tasks(Task("long", new string('x', 261)), Task("ok", new string('x', 260)))]));
        Assert.Contains("tasks[task:ok]", backend.Calls);
    }

    [Theory]
    [InlineData(JumpListCategoryType.Frequent)]
    [InlineData(JumpListCategoryType.Recent)]
    public void KnownCategoriesAppendByType(JumpListCategoryType type)
    {
        var backend = new FakeBackend();
        Assert.Equal("ok", RiftwayApp.ApplyJumpList(backend, [new JumpListCategory { Type = type }]));
        Assert.Equal(["begin", "known:" + type, "commit"], backend.Calls);
    }

    [Fact]
    public void RefusedKnownCategoryIsError() =>
        Assert.Equal("error", RiftwayApp.ApplyJumpList(new FakeBackend { KnownHr = EFail }, [new JumpListCategory { Type = JumpListCategoryType.Recent }]));

    [Fact]
    public void FirstSpecificErrorWinsOverGenericAndLaterSpecific()
    {
        var backend = new FakeBackend { KnownHr = EFail };
        backend.CategoryHr["D"] = EAccessDenied;
        var result = RiftwayApp.ApplyJumpList(backend,
            [new JumpListCategory { Type = JumpListCategoryType.Recent }, Custom("D", File(@"C:\d.txt")), Custom("S", s_separator)]);
        Assert.Equal("customCategoryAccessDeniedError", result);
    }

    [Fact]
    public void RefusedCommitIsErrorUnlessSomethingMoreSpecificFailed()
    {
        Assert.Equal("error", RiftwayApp.ApplyJumpList(new FakeBackend { CommitOk = false }, [Tasks(Task("a"))]));
        Assert.Equal("invalidSeparatorError", RiftwayApp.ApplyJumpList(new FakeBackend { CommitOk = false }, [Custom("C", s_separator)]));
    }

    [Fact]
    public void RemovedItemsAreAddedAgain()
    {
        var backend = new FakeBackend();
        backend.Removed.Add(File(@"C:\GONE.txt"));
        backend.Removed.Add(Task("removed") with { Args = "--x" });
        var result = RiftwayApp.ApplyJumpList(backend,
            [Custom("C", File(@"c:\gone.txt"), File(@"C:\kept.txt"), Task("removed") with { Args = "--x" })]);
        Assert.Equal("ok", result);
        Assert.Contains(@"C[file:c:\gone.txt,file:C:\kept.txt,task:removed]", backend.Calls);
    }

    [Fact]
    public void ACustomCategoryWithoutANameReachesTheShellWithAnEmptyName()
    {
        var backend = new FakeBackend();
        Assert.Equal("ok", RiftwayApp.ApplyJumpList(backend, [new JumpListCategory { Type = JumpListCategoryType.Custom, Items = [File(@"C:\a.txt")] }]));
        Assert.Contains(@"[file:C:\a.txt]", backend.Calls);
    }

    [Fact]
    public void EmptyProgramAndPathAreAcceptedAsInElectron()
    {
        var backend = new FakeBackend();
        Assert.Equal("ok", RiftwayApp.ApplyJumpList(backend, [Custom("C", Task("t") with { Program = "" }, File(""))]));
        Assert.Contains("C[task:t,file:]", backend.Calls);
    }

    [Fact]
    public void CategoryWithNoItemsAddsNothing()
    {
        var backend = new FakeBackend();
        Assert.Equal("ok", RiftwayApp.ApplyJumpList(backend, [Tasks()]));
        Assert.Equal(["begin", "commit"], backend.Calls);
        Assert.Empty(backend.Collections);
    }

    public static TheoryData<JumpListCategory> InvalidCategories => new()
    {
        Custom("", Task("a")),
        new JumpListCategory { Type = JumpListCategoryType.Tasks, Name = "", Items = [Task("a")] },
        new JumpListCategory { Type = JumpListCategoryType.Tasks },
        new JumpListCategory { Type = JumpListCategoryType.Custom, Name = "C" },
        Tasks(Task("a") with { Title = null }),
        Tasks(Task("a") with { Program = null }),
        Custom("C", new JumpListItem { Type = JumpListItemType.File }),
    };

    [Theory]
    [MemberData(nameof(InvalidCategories))]
    public void MissingRequiredFieldsThrowBeforeTouchingTheShell(JumpListCategory category)
    {
        var backend = new FakeBackend();
        Assert.Throws<ArgumentException>(() => RiftwayApp.ApplyJumpList(backend, [category]));
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public void KnownCategoriesNeedNoItems() =>
        Assert.Equal("ok", RiftwayApp.ApplyJumpList(new FakeBackend(), [new JumpListCategory { Type = JumpListCategoryType.Frequent }]));

    [Fact]
    public void UserTasksMapToTaskItemsAndReturnTheCommit()
    {
        var backend = new FakeBackend();
        backend.FailTasks.Add("bad");
        UserTask[] tasks = [new() { Program = "p", Title = "bad" }, new() { Program = "p", Title = "good" }];
        Assert.True(RiftwayApp.ApplyUserTasks(backend, tasks));
        Assert.Equal(["begin", "tasks[task:good]", "commit"], backend.Calls);
        Assert.False(RiftwayApp.ApplyUserTasks(new FakeBackend { CommitOk = false }, tasks));
    }

    [Fact]
    public void UserTaskFieldsReachTheShellUnderElectronsItemNames()
    {
        var backend = new FakeBackend();
        RiftwayApp.ApplyUserTasks(backend, [new UserTask
        {
            Program = "p", Arguments = "a", Title = "t", Description = "d", IconPath = "i", IconIndex = 2, WorkingDirectory = "w",
        }]);
        Assert.Equal(new JumpListItem
        {
            Type = JumpListItemType.Task, Program = "p", Args = "a", Title = "t", Description = "d", IconPath = "i", IconIndex = 2, WorkingDirectory = "w",
        }, Assert.Single(Assert.Single(backend.Collections).Tasks));
    }

    [Fact]
    public void EmptyUserTasksCommitAnEmptyList()
    {
        var backend = new FakeBackend();
        Assert.True(RiftwayApp.ApplyUserTasks(backend, []));
        Assert.Equal(["begin", "commit"], backend.Calls);
    }

    [Fact]
    public void UserTasksFailWhenBeginIsRefused()
    {
        var backend = new FakeBackend { BeginOk = false };
        Assert.False(RiftwayApp.ApplyUserTasks(backend, [new UserTask { Program = "p", Title = "t" }]));
        Assert.Equal(["begin"], backend.Calls);
    }

    [Fact]
    public void UserTaskWithoutTitleThrows() =>
        Assert.Throws<ArgumentException>(() => RiftwayApp.ApplyUserTasks(new FakeBackend(), [new UserTask { Program = "p", Title = "" }]));

    [Fact]
    public void SettingsReadThenAbort()
    {
        var backend = new FakeBackend { MinItems = 12 };
        backend.Removed.Add(File(@"C:\r.txt"));
        var settings = RiftwayApp.ReadJumpListSettings(backend);
        Assert.Equal(12, settings.MinItems);
        Assert.Equal([File(@"C:\r.txt")], settings.RemovedItems);
        Assert.Equal(["begin", "abort"], backend.Calls);
    }

    [Fact]
    public void SettingsDefaultTo10WhenBeginIsRefused()
    {
        var backend = new FakeBackend { BeginOk = false, MinItems = 3 };
        backend.Removed.Add(File(@"C:\r.txt"));
        var settings = RiftwayApp.ReadJumpListSettings(backend);
        Assert.Equal(10, settings.MinItems);
        Assert.Empty(settings.RemovedItems);
        Assert.Equal(["begin"], backend.Calls);
    }

    [Fact]
    public void RemovedDestinationsMapToElectronItems()
    {
        Assert.Equal(File(@"C:\f.txt"), RiftwayApp.ToJumpListItem(new WindowsDestination { IsFile = true, Path = @"C:\f.txt" }));
        var task = RiftwayApp.ToJumpListItem(new WindowsDestination
        {
            Path = "p", Arguments = "a", Title = "t", Description = "d", IconPath = "i", IconIndex = 3, WorkingDirectory = "w",
        });
        Assert.Equal(new JumpListItem
        {
            Type = JumpListItemType.Task, Program = "p", Args = "a", Title = "t", Description = "d", IconPath = "i", IconIndex = 3, WorkingDirectory = "w",
        }, task);
    }

    [Fact]
    public void RecentDocumentPassesAnyPathThroughAndOnlyRejectsNull()
    {
        var recent = new FakeRecent();
        Assert.Throws<ArgumentNullException>(() => RiftwayApp.AddRecentDocument(recent, null!));
        RiftwayApp.AddRecentDocument(recent, "");
        RiftwayApp.AddRecentDocument(recent, @"C:\doc.txt");
        Assert.Equal(["", @"C:\doc.txt"], recent.Added);
        RiftwayApp.AddRecentDocument(null, @"C:\doc.txt");
    }

    // The real shell: it persists the list as %APPDATA%\...\CustomDestinations\*.customDestinations-ms, with titles in UTF-16.
    private static readonly string s_customDestinations = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Recent\CustomDestinations");

    private static string NewestListFile() =>
        new DirectoryInfo(s_customDestinations).GetFiles("*.customDestinations-ms").OrderByDescending(f => f.LastWriteTimeUtc).First().FullName;

    private static bool AnyListContains(string text) =>
        Directory.Exists(s_customDestinations) && Directory.GetFiles(s_customDestinations, "*.customDestinations-ms").Any(f => FileContains(f, text));

    private static bool FileContains(string path, string text) =>
        System.IO.File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.Unicode.GetBytes(text)) >= 0;

    private static bool RealUserTasks(IReadOnlyList<UserTask> tasks)
    {
        using var backend = RiftwayApp.CreateJumpListBackend();
        return RiftwayApp.ApplyUserTasks(backend, tasks);
    }

    private static string RealJumpList(IReadOnlyList<JumpListCategory>? categories)
    {
        using var backend = RiftwayApp.CreateJumpListBackend();
        return RiftwayApp.ApplyJumpList(backend, categories);
    }

    [WindowsOnlyFact]
    public void RealUserTasksReachTheShellAndClear()
    {
        var first = "RiftwayTaskA-" + Guid.NewGuid().ToString("N");
        var second = "RiftwayTaskB-" + Guid.NewGuid().ToString("N");
        var program = Environment.ProcessPath!;
        try
        {
            Assert.True(RealUserTasks([
                new UserTask { Program = program, Title = first, Description = "first" },
                new UserTask { Program = program, Arguments = "--second", Title = second },
            ]));
            var file = NewestListFile();
            Assert.True(FileContains(file, first));
            Assert.True(FileContains(file, second));

            Assert.True(RealUserTasks([]));
            Assert.False(AnyListContains(first));
        }
        finally { RealJumpList(null); }
        Assert.False(AnyListContains(first) || AnyListContains(second));
    }

    [WindowsOnlyFact]
    public void RealJumpListReachesTheShellAndDeletes()
    {
        var task = "RiftwayJumpTask-" + Guid.NewGuid().ToString("N");
        var category = "RiftwayCategory-" + Guid.NewGuid().ToString("N");
        var custom = "RiftwayCustomTask-" + Guid.NewGuid().ToString("N");
        var program = Environment.ProcessPath!;
        try
        {
            Assert.Equal("ok", RealJumpList([Tasks(new JumpListItem { Type = JumpListItemType.Task, Program = program, Title = task }, s_separator)]));
            Assert.True(FileContains(NewestListFile(), task));
            var result = RealJumpList([
                Tasks(new JumpListItem { Type = JumpListItemType.Task, Program = program, Title = task }, s_separator),
                Custom(category, new JumpListItem { Type = JumpListItemType.Task, Program = program, Args = "--custom", Title = custom }),
            ]);
            // The rest of the list is committed even when the shell denies the custom category.
            var tracking = TracksRecentDocuments();
            Assert.Equal(tracking ? "ok" : "customCategoryAccessDeniedError", result);
            var file = NewestListFile();
            Assert.True(FileContains(file, task));
            Assert.Equal(tracking, FileContains(file, category));
            Assert.Equal(tracking, FileContains(file, custom));

            Assert.Equal("ok", RealJumpList(null));
            Assert.False(AnyListContains(task));
        }
        finally { RealJumpList(null); }
        Assert.False(AnyListContains(task) || AnyListContains(category) || AnyListContains(custom));
    }

    // With "Show recently opened items" off, the shell denies every custom category before looking at its items.
    private static bool TracksRecentDocuments()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        return key?.GetValue("Start_TrackDocs") is not 0;
    }

    [WindowsOnlyFact]
    public void RealUnregisteredFileTypeNeedsRegistration()
    {
        var path = Path.Combine(Path.GetTempPath(), "riftway-jumplist-" + Guid.NewGuid().ToString("N") + ".riftwayjltest");
        System.IO.File.WriteAllText(path, "x");
        var expected = TracksRecentDocuments() ? "fileTypeRegistrationError" : "customCategoryAccessDeniedError";
        try { Assert.Equal(expected, RealJumpList([Custom("Riftway files", File(path))])); }
        finally
        {
            RealJumpList(null);
            System.IO.File.Delete(path);
        }
    }

    [WindowsOnlyFact]
    public void RealSettingsReportAMinimum()
    {
        using var backend = RiftwayApp.CreateJumpListBackend();
        Assert.NotNull(backend);
        Assert.True(RiftwayApp.ReadJumpListSettings(backend).MinItems > 0);
    }

    // Written with WScript.Shell, so the read-back does not share Riftway's IShellLink code.
    [WindowsOnlyFact]
    public void RealRecentDocumentsListTheTargetsOfTheRecentFoldersShortcuts()
    {
        if (!OperatingSystem.IsWindows()) return;
        var target = Path.Combine(Path.GetTempPath(), "riftway-recent-" + Guid.NewGuid().ToString("N") + ".txt");
        var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Recent), Path.GetFileName(target) + ".lnk");
        System.IO.File.WriteAllText(target, "x");
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.TargetPath = target;
            shortcut.Save();

            var documents = RiftwayApp.CreateRecentDocumentsBackend()!.List();

            Assert.Single(documents, d => string.Equals(d, target, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            System.IO.File.Delete(link);
            System.IO.File.Delete(target);
        }
    }
}
