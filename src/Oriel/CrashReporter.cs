using System.Reflection;
using System.Text;

namespace Oriel;

/// <summary>Options for <see cref="CrashReporter.Start"/>; Electron's <c>crashReporter.start(options)</c>.</summary>
public sealed record CrashReporterStartOptions
{
    /// <summary>Where reports are POSTed as multipart form data. Required unless <see cref="UploadToServer"/> is false.</summary>
    public string? SubmitURL { get; init; }

    /// <summary>The <c>_productName</c> field and CEF's <c>product</c> field. Defaults to <see cref="OrielApp.Name"/>.</summary>
    public string? ProductName { get; init; }

    /// <summary>Deprecated in Electron: becomes <c>globalExtra._companyName</c> unless that is set.</summary>
    public string? CompanyName { get; init; }

    /// <summary>Whether reports are sent to <see cref="SubmitURL"/>; when false they stay in the local database. Default true.</summary>
    public bool UploadToServer { get; init; } = true;

    /// <summary>macOS: keeps browser-process crashes away from the system crash reporter. No effect on Windows.</summary>
    public bool IgnoreSystemCrashHandler { get; init; }

    /// <summary>Limits uploads (Electron: one per hour). Default false.</summary>
    public bool RateLimit { get; init; }

    /// <summary>Gzip the upload. Default true; CEF's Crashpad always compresses.</summary>
    public bool Compress { get; init; } = true;

    /// <summary>Annotations sent with reports from the main process only; child processes do not inherit them.</summary>
    public IReadOnlyDictionary<string, string>? Extra { get; init; }

    /// <summary>Annotations sent with reports from every process.</summary>
    public IReadOnlyDictionary<string, string>? GlobalExtra { get; init; }
}

/// <summary>A report in the crash database; Electron's <c>CrashReport</c>.</summary>
/// <param name="Date">When it was uploaded; the Unix epoch for a report that has not been.</param>
/// <param name="Id">The id the crash server answered with; empty for a report that has not been uploaded.</param>
public sealed record CrashReport(DateTimeOffset Date, string Id);

/// <summary>Submits crash reports to a server; Electron's <c>crashReporter</c>, over CEF's Crashpad.</summary>
/// <remarks>CEF configures Crashpad from <c>crash_reporter.cfg</c> beside the executable: the main process reads it inside
/// <see cref="OrielApp.Run"/>, each child process as it starts. <see cref="Start"/> writes that file, so call it before
/// <see cref="OrielApp.Run"/>; started later, it takes effect from the next launch. The file stays, so later launches
/// report crashes until it is deleted.</remarks>
public static class CrashReporter
{
    /// <summary>Carries the global annotations to every child process; the native subprocess sets them as crash keys.</summary>
    internal const string ChildSwitch = "oriel-crash-keys";

    private static readonly Lock s_lock = new();
    private static readonly Dictionary<string, string> s_parameters = new(StringComparer.Ordinal);
    private static readonly List<string> s_parameterOrder = [];
    private static Dictionary<string, string> s_global = new(StringComparer.Ordinal);
    private static Dictionary<string, CrashKeySlot> s_liveSlots = new(StringComparer.Ordinal);
    private static string? s_configPath;
    private static bool s_started;
    private static bool s_uploadToServer;
    private static bool s_crashpadStarted;
    private static FileSystemWatcher? s_settingsWatcher;

    /// <summary>Whether <see cref="Start"/> has been called in this process.</summary>
    internal static bool IsStarted => s_started;

    /// <summary>Starts crash reporting. Later calls do nothing, as in Electron.</summary>
    /// <exception cref="ArgumentException"><see cref="CrashReporterStartOptions.UploadToServer"/> is true and there is no
    /// <see cref="CrashReporterStartOptions.SubmitURL"/>.</exception>
    public static void Start(CrashReporterStartOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var submitUrl = options.SubmitURL ?? "";
        if (options.UploadToServer && submitUrl.Length == 0)
            throw new ArgumentException("submitURL must be specified when uploadToServer is true");
        if (!options.Compress && options.UploadToServer)
            Console.Error.WriteLine("(oriel) Sending uncompressed crash reports is deprecated; CEF's Crashpad compresses them regardless.");

        lock (s_lock)
        {
            if (s_started) return;
            s_started = true;
            s_uploadToServer = options.UploadToServer;

            var productName = options.ProductName ?? AppName();
            var version = AppVersion();
            s_global = Amend(productName, version, options.CompanyName, options.GlobalExtra);
            foreach (var (key, value) in options.Extra ?? new Dictionary<string, string>()) SetParameter(key, value);

            s_configPath ??= CrashReporterConfig.PathFor(AppContext.BaseDirectory);
            var previous = ReadOwnedConfig(s_configPath);
            var slots = CrashReporterConfig.AssignSlots(
                s_global.Keys.Concat(s_parameterOrder).Concat(previous?.Select(s => s.Name) ?? []));
            // The bootstrap layout has no Oriel.Subprocess.exe: CEF then starts the handler from the main exe, as Chrome does.
            var externalHandler = OperatingSystem.IsWindows() && !Cef.Platform.BootstrapHost.IsHosted ? Cef.Platform.CefLayout.SubprocessName + ".exe" : null;
            // Without a URL the handler starts no upload thread: Chrome re-enables Crashpad's upload flag at will.
            WriteConfig(s_configPath, CrashReporterConfig.Build(
                AppName(), productName, version, options.UploadToServer ? submitUrl : "", options.RateLimit,
                options.IgnoreSystemCrashHandler, externalHandler, slots));

            if (s_crashpadStarted) ApplyToBrowserProcess();
        }
    }

    /// <summary>The most recently uploaded report, or null; Electron's <c>getLastCrashReport()</c>.</summary>
    public static CrashReport? GetLastCrashReport() => Latest(GetUploadedReports());

    /// <summary>Electron's choice: the latest upload date wins, ties keep list order (Array.prototype.sort is stable).</summary>
    internal static CrashReport? Latest(IEnumerable<CrashReport> reports) => reports.OrderByDescending(r => r.Date).FirstOrDefault();

    /// <summary>Every report in the crash database, newest first, uploaded or not; Electron's <c>getUploadedReports()</c>.</summary>
    /// <remarks>Empty until the application runs: the database is Crashpad's, reached through CEF's chrome_elf.</remarks>
    public static IReadOnlyList<CrashReport> GetUploadedReports()
        => s_started && s_crashpadStarted ? CrashReporterNative.GetReports() : [];

    /// <summary>Whether reports are uploaded; Electron's <c>getUploadToServer()</c>. False before <see cref="Start"/>.</summary>
    public static bool GetUploadToServer()
    {
        lock (s_lock) return s_uploadToServer;
    }

    /// <summary>Turns uploading on or off; Electron's <c>setUploadToServer()</c>. Ignored before <see cref="Start"/>.</summary>
    /// <remarks>Turning it off holds Crashpad's upload flag (settings.dat) cleared for the rest of the run. Turning it on
    /// after starting with <see cref="CrashReporterStartOptions.UploadToServer"/> false uploads nothing until a launch
    /// starts with it true: that launch's handler had no URL.</remarks>
    public static void SetUploadToServer(bool uploadToServer)
    {
        lock (s_lock)
        {
            if (!s_started) return;
            s_uploadToServer = uploadToServer;
            if (s_crashpadStarted) EnforceUploadFlag();
        }
    }

    /// <summary>Adds or replaces an annotation for reports from the main process; Electron's <c>addExtraParameter()</c>.</summary>
    /// <remarks>Keys of 40 bytes or more are ignored with a warning, as in Electron. A key that was not declared when the
    /// process started is added to the cfg and reported from the next launch on.</remarks>
    public static void AddExtraParameter(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        lock (s_lock)
        {
            if (!SetParameter(key, value)) return;
            if (s_liveSlots.ContainsKey(key))
            {
                if (s_crashpadStarted) CrashReporterNative.SetCrashKey(key, value);
            }
            else if (s_started && s_configPath is not null) DeclareForNextLaunch(s_configPath, key);
        }
    }

    /// <summary>Removes an annotation added by <see cref="AddExtraParameter"/>; Electron's <c>removeExtraParameter()</c>.</summary>
    public static void RemoveExtraParameter(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (s_lock)
        {
            if (!s_parameters.Remove(key)) return;
            s_parameterOrder.Remove(key);
            if (s_crashpadStarted && s_liveSlots.ContainsKey(key)) CrashReporterNative.SetCrashKey(key, null);
        }
    }

    /// <summary>The main process's annotations from <c>extra</c> and <see cref="AddExtraParameter"/>; Electron's <c>getParameters()</c>.</summary>
    public static IReadOnlyDictionary<string, string> GetParameters()
    {
        lock (s_lock) return new Dictionary<string, string>(s_parameters, StringComparer.Ordinal);
    }

    /// <summary>Called by <see cref="OrielApp.Run"/> once cef_execute_process has read the cfg and started Crashpad for this
    /// process: the cfg now on disk is the one this launch uses. Applies keys and consent to the main process.</summary>
    internal static void OnCrashpadStarted()
    {
        lock (s_lock)
        {
            ReadLiveSlots();
            s_crashpadStarted = true;
            if (OperatingSystem.IsWindows() && Cef.Platform.BootstrapHost.IsHosted) CrashReporterNative.RestoreCrashpadExceptionFilter();
            if (s_started) ApplyToBrowserProcess();
        }
    }

    /// <summary>Takes the cfg on disk as the one this launch's processes read: only its [CrashKeys] are recorded.</summary>
    internal static void ReadLiveSlots()
    {
        lock (s_lock)
        {
            s_configPath ??= CrashReporterConfig.PathFor(AppContext.BaseDirectory);
            s_liveSlots = ReadSlots(s_configPath);
        }
    }

    // Chrome's metrics manager sets the flag again whenever it updates its services (CEF enforces consent by policy),
    // so a cleared flag is re-cleared each time settings.dat changes.
    private static void EnforceUploadFlag()
    {
        if (CrashReporterNative.SettingsPath() is not { } path) return;
        CrashReporterNative.WriteUploadsEnabled(path, s_uploadToServer);
        if (s_uploadToServer || s_settingsWatcher is not null) return;
        s_settingsWatcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        s_settingsWatcher.Changed += (_, _) =>
        {
            lock (s_lock)
            {
                if (!s_uploadToServer) CrashReporterNative.WriteUploadsEnabled(path, false);
            }
        };
        s_settingsWatcher.EnableRaisingEvents = true;
    }

    /// <summary>The value of <see cref="ChildSwitch"/> for a child of type <paramref name="processType"/>, or null when not started.</summary>
    internal static string? ChildSwitchValue(string processType)
    {
        lock (s_lock)
        {
            if (!s_started) return null;
            var pairs = new List<KeyValuePair<string, string>>
            {
                new("process_type", processType),
                new("platform", CrashReporterConfig.Platform),
            };
            foreach (var (key, value) in s_global)
                if (s_liveSlots.TryGetValue(key, out var slot)) pairs.Add(new(key, CrashReporterConfig.Truncate(value, slot.MaxBytes)));
            return CrashReporterConfig.EncodePairs(pairs);
        }
    }

    /// <summary>Electron's amendment: <c>_productName</c> and <c>_version</c>, then <c>globalExtra</c> over them.</summary>
    internal static Dictionary<string, string> Amend(string productName, string version, string? companyName,
        IReadOnlyDictionary<string, string>? globalExtra)
    {
        var amended = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["_productName"] = productName,
            ["_version"] = version,
        };
        if (companyName is { Length: > 0 }) amended["_companyName"] = companyName;
        foreach (var (key, value) in globalExtra ?? new Dictionary<string, string>()) amended[key] = value;
        return amended;
    }

    /// <summary>For tests: forgets every start, parameter and path.</summary>
    internal static void Reset(string? configPath = null)
    {
        lock (s_lock)
        {
            s_started = false;
            s_uploadToServer = false;
            s_parameters.Clear();
            s_parameterOrder.Clear();
            s_global = new(StringComparer.Ordinal);
            s_liveSlots = new(StringComparer.Ordinal);
            s_crashpadStarted = false;
            s_configPath = configPath;
        }
    }

    private static bool SetParameter(string key, string value)
    {
        if (Encoding.UTF8.GetByteCount(key) >= CrashReporterConfig.MaxKeyNameBytes)
        {
            Console.Error.WriteLine($"(oriel) The crash key name, '{key}', is longer than {CrashReporterConfig.MaxKeyNameBytes} bytes, ignoring it.");
            return false;
        }
        if (!s_parameters.ContainsKey(key)) s_parameterOrder.Add(key);
        s_parameters[key] = value;
        return true;
    }

    private static void ApplyToBrowserProcess()
    {
        CrashReporterNative.SetCrashKey("process_type", "browser");
        CrashReporterNative.SetCrashKey("platform", CrashReporterConfig.Platform);
        foreach (var (key, value) in s_global)
            if (s_liveSlots.ContainsKey(key)) CrashReporterNative.SetCrashKey(key, value);
        foreach (var (key, value) in s_parameters)
            if (s_liveSlots.ContainsKey(key)) CrashReporterNative.SetCrashKey(key, value);
    }

    private static Dictionary<string, CrashKeySlot> ReadSlots(string path)
    {
        var slots = new Dictionary<string, CrashKeySlot>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(path))
                foreach (var slot in CrashReporterConfig.ReadSlots(File.ReadAllText(path))) slots.TryAdd(slot.Name, slot);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return slots;
    }

    private static List<CrashKeySlot>? ReadOwnedConfig(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            return text.StartsWith(CrashReporterConfig.Marker, StringComparison.Ordinal) ? CrashReporterConfig.ReadSlots(text) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void DeclareForNextLaunch(string path, string key)
    {
        if (!CrashReporterConfig.IsDeclarable(key) || ReadOwnedConfig(path) is not { } slots || slots.Any(s => s.Name == key)) return;
        var size = CrashReporterConfig.AssignSlots(slots.Select(s => s.Name).Append(key)).FirstOrDefault(s => s.Name == key);
        if (size.Name is null) return;
        WriteConfig(path, File.ReadAllText(path).TrimEnd('\n') + "\n" + key + "=" + size.Size + "\n");
    }

    // A child process may read the cfg at any moment, so it is replaced whole rather than rewritten in place.
    private static void WriteConfig(string path, string text)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == text) return;
            var temp = path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"(oriel) Could not write {path} ({e.Message}); ship it with the app to enable crash reporting.");
        }
    }

    private static string AppName()
        => OrielApp.CurrentOrNull?.Name ?? Assembly.GetEntryAssembly()?.GetName().Name ?? "Oriel";

    /// <summary>The crash reporter's <c>_version</c>: Electron's <c>app.getVersion()</c>.</summary>
    internal static string AppVersion() => OrielApp.CurrentOrNull?.Version ?? OrielApp.VersionOf(Assembly.GetEntryAssembly());
}
