// <App>.dll: the client DLL that CEF's bootstrap.exe, renamed to <App>.exe, loads in every process.
// bootstrap.exe holds the Windows sandbox broker, which CEF 154 offers no other way to link with MSVC.
// Child processes stay native, as Oriel.Subprocess.exe; the browser process hosts the app's .NET runtime.

#define ORIEL_SUBPROCESS_NO_MAIN
#include "../Oriel.Subprocess.Native/subprocess.c"

#include <shellapi.h>
#include <string.h>
#include <wchar.h>

#include "include/cef_sandbox_win.h"
#include "include/cef_version_info.h"

static void* g_sandbox_info;

// OrielApp passes this to cef_initialize; null outside the browser process.
__declspec(dllexport) void* oriel_sandbox_info(void) { return g_sandbox_info; }

static wchar_t g_hostfxr_errors[2048];

static void report_error(const wchar_t* message) {
  wchar_t text[4096];
  swprintf(text, sizeof(text) / sizeof(text[0]), L"%ls%ls%ls", message,
           g_hostfxr_errors[0] ? L"\n\n" : L"", g_hostfxr_errors);
  OutputDebugStringW(text);
  HANDLE err = GetStdHandle(STD_ERROR_HANDLE);
  if (err && err != INVALID_HANDLE_VALUE) {
    char utf8[8192];
    int size = WideCharToMultiByte(CP_UTF8, 0, text, -1, utf8, sizeof(utf8) - 1, NULL, NULL);
    if (size > 0) {
      utf8[size - 1] = '\n';
      DWORD written;
      WriteFile(err, utf8, (DWORD)size, &written, NULL);
      return;
    }
  }
  // A GUI launch has nowhere else to show it.
  MessageBoxW(NULL, text, L"Oriel", MB_OK | MB_ICONERROR);
}

typedef void* hostfxr_handle;
typedef struct {
  size_t size;
  const wchar_t* host_path;
  const wchar_t* dotnet_root;
} hostfxr_initialize_parameters;
typedef void(__cdecl* hostfxr_error_writer_fn)(const wchar_t* message);
typedef int(__cdecl* init_fn)(int, const wchar_t**, const hostfxr_initialize_parameters*, hostfxr_handle*);
typedef int(__cdecl* run_fn)(hostfxr_handle);
typedef int(__cdecl* close_fn)(hostfxr_handle);
typedef hostfxr_error_writer_fn(__cdecl* set_error_writer_fn)(hostfxr_error_writer_fn);

static void __cdecl collect_hostfxr_error(const wchar_t* message) {
  size_t used = wcslen(g_hostfxr_errors);
  swprintf(g_hostfxr_errors + used, sizeof(g_hostfxr_errors) / sizeof(g_hostfxr_errors[0]) - used, L"%ls%ls",
           used ? L"\n" : L"", message);
}

// The managed entry assembly: the one *.runtimeconfig.json beside the exe whose apphost the layout removed. Other
// .NET apps published alongside (a utility process's child) keep their <name>.exe, through which they are started.
// Its name cannot be the exe's, which this DLL's name has to match.
static int find_managed_app(const wchar_t* dir, wchar_t* app, size_t capacity) {
  wchar_t pattern[MAX_PATH];
  swprintf(pattern, MAX_PATH, L"%ls\\*.runtimeconfig.json", dir);
  WIN32_FIND_DATAW found;
  HANDLE search = FindFirstFileW(pattern, &found);
  if (search == INVALID_HANDLE_VALUE) return 0;
  int count = 0;
  do {
    size_t stem = wcslen(found.cFileName) - wcslen(L".runtimeconfig.json");
    wchar_t apphost[MAX_PATH];
    swprintf(apphost, MAX_PATH, L"%ls\\%.*ls.exe", dir, (int)stem, found.cFileName);
    if (GetFileAttributesW(apphost) != INVALID_FILE_ATTRIBUTES) continue;
    if (++count > 1) break;
    swprintf(app, capacity, L"%ls\\%.*ls.dll", dir, (int)stem, found.cFileName);
  } while (FindNextFileW(search, &found));
  FindClose(search);
  return count;
}

// <Exe>.oriel-app, written by the publish layout, names the exe's managed assembly: several Oriel apps can share one
// folder, and once their apphosts are gone the runtimeconfig scan cannot tell them apart.
static int read_app_marker(const wchar_t* exe, const wchar_t* dir, wchar_t* app, size_t capacity) {
  wchar_t marker[MAX_PATH];
  size_t stem = wcslen(exe) - 4;  // drop ".exe"
  if (swprintf(marker, MAX_PATH, L"%.*ls.oriel-app", (int)stem, exe) < 0) return 0;
  HANDLE file = CreateFileW(marker, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
  if (file == INVALID_HANDLE_VALUE) return 0;
  char name[MAX_PATH];
  DWORD read = 0;
  BOOL ok = ReadFile(file, name, sizeof(name) - 1, &read, NULL);
  CloseHandle(file);
  if (!ok) return 0;
  while (read > 0 && (name[read - 1] == '\r' || name[read - 1] == '\n' || name[read - 1] == ' ')) read--;
  name[read] = '\0';
  // A bare file name only: the marker must not point the browser process outside its own folder.
  if (read == 0 || strpbrk(name, "\\/:") != NULL) return 0;
  wchar_t wide[MAX_PATH];
  if (MultiByteToWideChar(CP_UTF8, 0, name, -1, wide, MAX_PATH) == 0) return 0;
  return swprintf(app, capacity, L"%ls\\%ls", dir, wide) > 0 && GetFileAttributesW(app) != INVALID_FILE_ATTRIBUTES;
}

static int run_dotnet(void) {
  wchar_t exe[MAX_PATH], dir[MAX_PATH], app[MAX_PATH], fxr[MAX_PATH];
  DWORD n = GetModuleFileNameW(NULL, exe, MAX_PATH);
  if (n == 0 || n >= MAX_PATH) { report_error(L"The app's path is too long."); return 1; }
  wcscpy_s(dir, MAX_PATH, exe);
  *wcsrchr(dir, L'\\') = L'\0';

  int apps = read_app_marker(exe, dir, app, MAX_PATH) ? 1 : find_managed_app(dir, app, MAX_PATH);
  if (apps != 1) {
    report_error(apps == 0 ? L"No <app>.runtimeconfig.json beside the exe: publish the app self-contained."
                           : L"More than one *.runtimeconfig.json without its own apphost beside the exe; keep only the app's.");
    return 1;
  }

  swprintf(fxr, MAX_PATH, L"%ls\\hostfxr.dll", dir);
  HMODULE lib = LoadLibraryExW(fxr, NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
  if (!lib) { report_error(L"No hostfxr.dll beside the exe: publish the app self-contained."); return 1; }
  init_fn init = (init_fn)(void*)GetProcAddress(lib, "hostfxr_initialize_for_dotnet_command_line");
  run_fn run = (run_fn)(void*)GetProcAddress(lib, "hostfxr_run_app");
  close_fn close = (close_fn)(void*)GetProcAddress(lib, "hostfxr_close");
  set_error_writer_fn set_error_writer = (set_error_writer_fn)(void*)GetProcAddress(lib, "hostfxr_set_error_writer");
  if (!init || !run || !close || !set_error_writer) { report_error(L"hostfxr.dll lacks the hosting exports."); return 1; }
  set_error_writer(collect_hostfxr_error);

  // hostfxr takes the managed app as argv[0], followed by the launch's own arguments.
  int argc;
  wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
  if (!argv) { report_error(L"The command line could not be parsed."); return 1; }
  argv[0] = app;

  hostfxr_initialize_parameters params = {sizeof(params), exe, dir};
  hostfxr_handle handle = NULL;
  int rc = init(argc, (const wchar_t**)argv, &params, &handle);
  LocalFree(argv);
  if (rc < 0 || !handle) {
    wchar_t message[128];
    swprintf(message, 128, L"The .NET runtime failed to start (0x%08x).", (unsigned)rc);
    report_error(message);
    if (handle) close(handle);
    return 1;
  }
  set_error_writer(NULL);
  rc = run(handle);
  close(handle);
  return rc;
}

// Chromium marks every child with --type=; switches end at a bare "--".
static int is_child_process(void) {
  int argc;
  wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
  if (!argv) return 0;
  int child = 0;
  for (int i = 1; i < argc && wcscmp(argv[i], L"--") != 0; i++) {
    if (wcsncmp(argv[i], L"--type=", 7) == 0) { child = 1; break; }
  }
  LocalFree(argv);
  return child;
}

// bootstrap.exe and libcef must agree on the sandbox ABI, as CEF's scoped library loader checks.
static int bootstrap_matches(const cef_version_info_t* info) {
  if (!info || info->size < CEF_VERSION_INFO_SIZE_WITH_SANDBOX_HASH) return 0;
  const char* hash = cef_api_hash(cef_api_version(), 3);
  return hash && strcmp(info->sandbox_compat_hash, hash) == 0;
}

int RunWinMain(HINSTANCE instance, LPWSTR command_line, int show, void* sandbox_info,
               cef_version_info_t* version_info) {
  (void)command_line; (void)show;
  int child = is_child_process();
  // The first cef_api_hash call fixes the process's API version. The browser process must fix the next version, which
  // CefRuntime.Load probes to detect Oriel's CEF patches (and then checks); a stock libcef serves it as 15400.
  int api_ok = child ? check_api_hash() : cef_api_hash(CEF_API_VERSION_NEXT, 0) != NULL || check_api_hash();
  if (!api_ok) { report_error(L"libcef.dll is not the CEF release Oriel was built for."); return 1; }
  if (!bootstrap_matches(version_info)) {
    report_error(L"The exe (CEF's bootstrap.exe) and libcef.dll come from different CEF releases.");
    return 1;
  }
  if (version_info->size >= CEF_VERSION_INFO_SIZE_WITH_INSTALLER_ERROR && version_info->installer_error_code != 0) {
    wchar_t message[1024];
    swprintf(message, 1024, L"CEF's bootstrap failed (%d): %hs", version_info->installer_error_code,
             version_info->installer_error_message ? version_info->installer_error_message : "");
    report_error(message);
    return 1;
  }

  if (child) {
    set_crash_keys(windows_switch(CRASH_KEYS_SWITCH));
    cef_main_args_t args = {instance};
    return cef_execute_process(&args, app_for(windows_switch(SCHEME_SWITCH)), sandbox_info);
  }

  g_sandbox_info = sandbox_info;
  return run_dotnet();
}
