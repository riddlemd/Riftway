using System.Text;
using Oriel.Cef.Platform;

namespace Oriel.Tests;

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
        public void Clear() { }
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
        Assert.Equal(text, OrielApp.JumpListResultText(Enum.Parse<JumpListResult>(result)));

    [Fact]
    public void NullCategoriesDeleteTheList()
    {
        var backend = new FakeBackend();
        Assert.Equal("ok", OrielApp.ApplyJumpList(backend, null));
        Assert.Equal(["delete"], backend.Calls);
    }

    [Fact]
    public void EmptyCategoriesDeleteTheList()
    {
        var backend = new FakeBackend();
        Assert.Equal("ok", OrielApp.ApplyJumpList(backend, []));
        Assert.Equal(["delete"], backend.Calls);
    }

    [Fact]
    public void RefusedDeleteIsError() => Assert.Equal("error", OrielApp.ApplyJumpList(new FakeBackend { DeleteOk = false }, null));

    [Fact]
    public void NoBackendIsErrorAndFalse()
    {
        Assert.Equal("error", OrielApp.ApplyJumpList(null, [Tasks(Task("a"))]));
        Assert.False(OrielApp.ApplyUserTasks(null, []));
    }

    [Fact]
    public void RefusedBeginIsErrorAndAddsNothing()
    {
        var backend = new FakeBackend { BeginOk = false };
        Assert.Equal("error", OrielApp.ApplyJumpList(backend, [Tasks(Task("a"))]));
        Assert.Equal(["begin"], backend.Calls);
    }

    [Fact]
    public void TasksTakeSeparatorsAndCommit()
    {
        var backend = new FakeBackend();
        Assert.Equal("ok", OrielApp.ApplyJumpList(backend, [Tasks(Task("a"), s_separator, Task("b"))]));
        Assert.Equal(["begin", "tasks[task:a,separator,task:b]", "commit"], backend.Calls);
    }

    [Fact]
    public void SeparatorInCustomCategoryIsInvalidButTheRestIsAdded()
    {
        var backend = new FakeBackend();
        Assert.Equal("invalidSeparatorError", OrielApp.ApplyJumpList(backend, [Custom("C", Task("a"), s_separator)]));
        Assert.Equal(["begin", "C[task:a]", "commit"], backend.Calls);
    }

    [Fact]
    public void CategoryWithOnlyASeparatorIsNotAppended()
    {
        var backend = new FakeBackend();
        Assert.Equal("invalidSeparatorError", OrielApp.ApplyJumpList(backend, [Custom("C", s_separator)]));
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
        Assert.Equal(expected, OrielApp.ApplyJumpList(backend, [Custom("C", File(@"C:\a.txt"))]));
        Assert.Contains("commit", backend.Calls);
    }

    [Fact]
    public void RefusedUserTasksIsError() =>
        Assert.Equal("error", OrielApp.ApplyJumpList(new FakeBackend { UserTasksHr = EFail }, [Tasks(Task("a"))]));

    [Fact]
    public void PartlyAddedCategoryIsErrorButStillAppended()
    {
        var backend = new FakeBackend();
        backend.FailTasks.Add("bad");
        Assert.Equal("error", OrielApp.ApplyJumpList(backend, [Custom("C", Task("bad"), Task("good"))]));
        Assert.Contains("C[task:good]", backend.Calls);
    }

    [Fact]
    public void DescriptionOver260DropsTheTask()
    {
        var backend = new FakeBackend();
        Assert.Equal("error", OrielApp.ApplyJumpList(backend, [Tasks(Task("long", new string('x', 261)), Task("ok", new string('x', 260)))]));
        Assert.Contains("tasks[task:ok]", backend.Calls);
    }

    [Theory]
    [InlineData(JumpListCategoryType.Frequent)]
    [InlineData(JumpListCategoryType.Recent)]
    public void KnownCategoriesAppendByType(JumpListCategoryType type)
    {
        var backend = new FakeBackend();
        Assert.Equal("ok", OrielApp.ApplyJumpList(backend, [new JumpListCategory { Type = type }]));
        Assert.Equal(["begin", "known:" + type, "commit"], backend.Calls);
    }

    [Fact]
    public void RefusedKnownCategoryIsError() =>
        Assert.Equal("error", OrielApp.ApplyJumpList(new FakeBackend { KnownHr = EFail }, [new JumpListCategory { Type = JumpListCategoryType.Recent }]));

    [Fact]
    public void FirstSpecificErrorWinsOverGenericAndLaterSpecific()
    {
        var backend = new FakeBackend { KnownHr = EFail };
        backend.CategoryHr["D"] = EAccessDenied;
        var result = OrielApp.ApplyJumpList(backend,
            [new JumpListCategory { Type = JumpListCategoryType.Recent }, Custom("D", File(@"C:\d.txt")), Custom("S", s_separator)]);
        Assert.Equal("customCategoryAccessDeniedError", result);
    }

    [Fact]
    public void RefusedCommitIsErrorUnlessSomethingMoreSpecificFailed()
    {
        Assert.Equal("error", OrielApp.ApplyJumpList(new FakeBackend { CommitOk = false }, [Tasks(Task("a"))]));
        Assert.Equal("invalidSeparatorError", OrielApp.ApplyJumpList(new FakeBackend { CommitOk = false }, [Custom("C", s_separator)]));
    }

    [Fact]
    public void RemovedItemsAreSkipped()
    {
        var backend = new FakeBackend();
        backend.Removed.Add(File(@"C:\GONE.txt"));
        backend.Removed.Add(Task("removed") with { Args = "--x" });
        var result = OrielApp.ApplyJumpList(backend,
            [Custom("C", File(@"c:\gone.txt"), File(@"C:\kept.txt"), Task("removed") with { Args = "--x" }, Task("other args") with { Args = "--y" })]);
        Assert.Equal("ok", result);
        Assert.Contains(@"C[file:C:\kept.txt,task:other args]", backend.Calls);
    }

    [Fact]
    public void CategoryWithNoItemsAddsNothing()
    {
        var backend = new FakeBackend();
        Assert.Equal("ok", OrielApp.ApplyJumpList(backend, [Tasks()]));
        Assert.Equal(["begin", "commit"], backend.Calls);
        Assert.Empty(backend.Collections);
    }

    public static TheoryData<JumpListCategory> InvalidCategories => new()
    {
        Custom("", Task("a")),
        new JumpListCategory { Type = JumpListCategoryType.Custom, Items = [Task("a")] },
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
        Assert.Throws<ArgumentException>(() => OrielApp.ApplyJumpList(backend, [category]));
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public void KnownCategoriesNeedNoItems() =>
        Assert.Equal("ok", OrielApp.ApplyJumpList(new FakeBackend(), [new JumpListCategory { Type = JumpListCategoryType.Frequent }]));

    [Fact]
    public void UserTasksMapToTaskItemsAndReturnTheCommit()
    {
        var backend = new FakeBackend();
        backend.FailTasks.Add("bad");
        UserTask[] tasks = [new() { Program = "p", Title = "bad" }, new() { Program = "p", Title = "good" }];
        Assert.True(OrielApp.ApplyUserTasks(backend, tasks));
        Assert.Equal(["begin", "tasks[task:good]", "commit"], backend.Calls);
        Assert.False(OrielApp.ApplyUserTasks(new FakeBackend { CommitOk = false }, tasks));
    }

    [Fact]
    public void UserTaskFieldsReachTheShellUnderElectronsItemNames()
    {
        var backend = new FakeBackend();
        OrielApp.ApplyUserTasks(backend, [new UserTask
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
        Assert.True(OrielApp.ApplyUserTasks(backend, []));
        Assert.Equal(["begin", "commit"], backend.Calls);
    }

    [Fact]
    public void UserTasksFailWhenBeginIsRefused()
    {
        var backend = new FakeBackend { BeginOk = false };
        Assert.False(OrielApp.ApplyUserTasks(backend, [new UserTask { Program = "p", Title = "t" }]));
        Assert.Equal(["begin"], backend.Calls);
    }

    [Fact]
    public void UserTaskWithoutTitleThrows() =>
        Assert.Throws<ArgumentException>(() => OrielApp.ApplyUserTasks(new FakeBackend(), [new UserTask { Program = "p", Title = "" }]));

    [Fact]
    public void SettingsReadThenAbort()
    {
        var backend = new FakeBackend { MinItems = 12 };
        backend.Removed.Add(File(@"C:\r.txt"));
        var settings = OrielApp.ReadJumpListSettings(backend);
        Assert.Equal(12, settings.MinItems);
        Assert.Equal([File(@"C:\r.txt")], settings.RemovedItems);
        Assert.Equal(["begin", "abort"], backend.Calls);
    }

    [Fact]
    public void SettingsDefaultTo10WhenBeginIsRefused()
    {
        var backend = new FakeBackend { BeginOk = false, MinItems = 3 };
        backend.Removed.Add(File(@"C:\r.txt"));
        var settings = OrielApp.ReadJumpListSettings(backend);
        Assert.Equal(10, settings.MinItems);
        Assert.Empty(settings.RemovedItems);
        Assert.Equal(["begin"], backend.Calls);
    }

    [Fact]
    public void RemovedDestinationsMapToElectronItems()
    {
        Assert.Equal(File(@"C:\f.txt"), OrielApp.ToJumpListItem(new WindowsDestination { IsFile = true, Path = @"C:\f.txt" }));
        var task = OrielApp.ToJumpListItem(new WindowsDestination
        {
            Path = "p", Arguments = "a", Title = "t", Description = "d", IconPath = "i", IconIndex = 3, WorkingDirectory = "w",
        });
        Assert.Equal(new JumpListItem
        {
            Type = JumpListItemType.Task, Program = "p", Args = "a", Title = "t", Description = "d", IconPath = "i", IconIndex = 3, WorkingDirectory = "w",
        }, task);
    }

    [Fact]
    public void RecentDocumentNeedsAPath()
    {
        var recent = new FakeRecent();
        Assert.Throws<ArgumentException>(() => OrielApp.AddRecentDocument(recent, ""));
        OrielApp.AddRecentDocument(recent, @"C:\doc.txt");
        Assert.Equal([@"C:\doc.txt"], recent.Added);
        OrielApp.AddRecentDocument(null, @"C:\doc.txt");
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
        using var backend = OrielApp.CreateJumpListBackend();
        return OrielApp.ApplyUserTasks(backend, tasks);
    }

    private static string RealJumpList(IReadOnlyList<JumpListCategory>? categories)
    {
        using var backend = OrielApp.CreateJumpListBackend();
        return OrielApp.ApplyJumpList(backend, categories);
    }

    [WindowsOnlyFact]
    public void RealUserTasksReachTheShellAndClear()
    {
        var first = "OrielTaskA-" + Guid.NewGuid().ToString("N");
        var second = "OrielTaskB-" + Guid.NewGuid().ToString("N");
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
        var task = "OrielJumpTask-" + Guid.NewGuid().ToString("N");
        var category = "OrielCategory-" + Guid.NewGuid().ToString("N");
        var custom = "OrielCustomTask-" + Guid.NewGuid().ToString("N");
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
        var path = Path.Combine(Path.GetTempPath(), "oriel-jumplist-" + Guid.NewGuid().ToString("N") + ".orieljltest");
        System.IO.File.WriteAllText(path, "x");
        var expected = TracksRecentDocuments() ? "fileTypeRegistrationError" : "customCategoryAccessDeniedError";
        try { Assert.Equal(expected, RealJumpList([Custom("Oriel files", File(path))])); }
        finally
        {
            RealJumpList(null);
            System.IO.File.Delete(path);
        }
    }

    [WindowsOnlyFact]
    public void RealSettingsReportAMinimum()
    {
        using var backend = OrielApp.CreateJumpListBackend();
        Assert.NotNull(backend);
        Assert.True(OrielApp.ReadJumpListSettings(backend).MinItems > 0);
    }
}
