// Riftway's Chromium subprocess: every zygote, renderer, GPU and utility process runs this exe.
// Native rather than .NET because Chromium's Linux zygote CHECKs that its process is
// single-threaded, and every .NET process (NativeAOT included) starts runtime threads first.

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#endif

#if defined(__linux__) && defined(__aarch64__)
#include <dlfcn.h>
#include <sys/auxv.h>
#endif

#include "include/capi/cef_app_capi.h"
#include "include/capi/cef_command_line_capi.h"
#include "include/capi/cef_crash_util_capi.h"
#include "include/cef_api_hash.h"

#ifdef __APPLE__
// macOS: Chromium's sandbox must be entered before the framework loads, so the framework is not linked: it is opened
// after cef_sandbox_initialize, and every CEF function this file calls goes through a pointer resolved from it.
#include <dlfcn.h>
#include <limits.h>
#include <mach-o/dyld.h>

#define RIFTWAY_CEF_FUNCTIONS(X) \
  X(cef_api_hash) X(cef_execute_process) X(cef_string_utf8_to_utf16) X(cef_string_utf16_to_utf8) \
  X(cef_string_utf16_clear) X(cef_string_utf8_clear) X(cef_string_userfree_utf16_free) \
  X(cef_set_crash_key_value) X(cef_command_line_get_global)
#define RIFTWAY_DECLARE(name) static __typeof__(&name) p_##name;
RIFTWAY_CEF_FUNCTIONS(RIFTWAY_DECLARE)
#define cef_api_hash p_cef_api_hash
#define cef_execute_process p_cef_execute_process
#define cef_string_utf8_to_utf16 p_cef_string_utf8_to_utf16
#define cef_string_utf16_to_utf8 p_cef_string_utf16_to_utf8
#define cef_string_utf16_clear p_cef_string_utf16_clear
#define cef_string_utf8_clear p_cef_string_utf8_clear
#define cef_string_userfree_utf16_free p_cef_string_userfree_utf16_free
#define cef_set_crash_key_value p_cef_set_crash_key_value
#define cef_command_line_get_global p_cef_command_line_get_global

static void* g_sandbox;

// The helper is <App>.app/Contents/Frameworks/<App> Helper (<type>).app/Contents/MacOS/<exe>; the framework sits
// beside the helper apps. Returns 0 when the sandbox or the framework cannot be loaded.
static int mac_load(int argc, char* argv[]) {
  char exe[PATH_MAX], real[PATH_MAX], path[PATH_MAX];
  uint32_t size = sizeof exe;
  if (_NSGetExecutablePath(exe, &size) != 0 || !realpath(exe, real)) return 0;
  char* slash = strrchr(real, '/');
  if (!slash) return 0;
  *slash = '\0';
  const char* framework = "../../../Chromium Embedded Framework.framework";

  // CEF's helper does the same: the sandbox library decides from the switches whether this process is sandboxed.
  snprintf(path, sizeof path, "%s/%s/Libraries/libcef_sandbox.dylib", real, framework);
  void* sandbox = dlopen(path, RTLD_LAZY | RTLD_LOCAL);
  void* (*initialize)(int, char**) = sandbox ? (void* (*)(int, char**))dlsym(sandbox, "cef_sandbox_initialize") : NULL;
  if (!initialize) {
    fprintf(stderr, "Riftway.Subprocess: cannot load %s: %s\n", path, dlerror());
    return 0;
  }
  g_sandbox = initialize(argc, argv);
  if (!g_sandbox) {
    fprintf(stderr, "Riftway.Subprocess: cef_sandbox_initialize failed\n");
    return 0;
  }

  snprintf(path, sizeof path, "%s/%s/Chromium Embedded Framework", real, framework);
  void* cef = dlopen(path, RTLD_LAZY | RTLD_LOCAL);
  if (!cef) {
    fprintf(stderr, "Riftway.Subprocess: cannot load %s: %s\n", path, dlerror());
    return 0;
  }
#define RIFTWAY_RESOLVE(name) \
  if (!(p_##name = (__typeof__(p_##name))dlsym(cef, #name))) { \
    fprintf(stderr, "Riftway.Subprocess: %s missing from the framework\n", #name); \
    return 0; \
  }
  RIFTWAY_CEF_FUNCTIONS(RIFTWAY_RESOLVE)
  return 1;
}
#endif

#if defined(__linux__) && defined(__aarch64__)
#define HWCAP_SVE_BIT (1UL << 22)
#define HWCAP2_SME_ALL ((1UL << 23) | (0x7fUL << 24) | (0x3fUL << 37))

// Chromium's libyuv picks its SME rows on HWCAP2_SME alone, but they are built for armv9-a and open
// with an SVE instruction, so CPUs with SME and no SVE (Linux VMs on Apple M4+) crash the renderer
// with SIGILL on video. Defined in the exe, this interposes libcef.so's getauxval; glibc's own calls
// don't go through it.
unsigned long getauxval(unsigned long type) {
  static unsigned long (*real)(unsigned long);
  if (!real) real = (unsigned long (*)(unsigned long))dlsym(RTLD_NEXT, "getauxval");
  unsigned long value = real(type);
  if (type == AT_HWCAP2 && !(real(AT_HWCAP) & HWCAP_SVE_BIT)) value &= ~HWCAP2_SME_ALL;
  return value;
}
#endif

// Custom schemes must be registered identically in every process; the browser passes them as
// "--riftway-custom-schemes=app:89,media:25" (name:cef_scheme_options_t). See SchemeSwitch.cs.
#define SCHEME_SWITCH "--riftway-custom-schemes="

static const char* g_schemes;

// CrashReporter's global annotations, which Electron sends from every process, arrive as the hex of
// UTF-8 "key\0value\0..." (CrashReporterConfig.EncodePairs); CEF only records keys its cfg declared.
#define CRASH_KEYS_SWITCH "--riftway-crash-keys="

static int hex_digit(char c) {
  if (c >= '0' && c <= '9') return c - '0';
  if (c >= 'a' && c <= 'f') return c - 'a' + 10;
  return c - 'A' + 10;
}

static void set_crash_key(const char* key, size_t key_length, const char* value, size_t value_length) {
#ifdef _WIN32
  // cef_set_crash_key_value does nothing before cef_execute_process initialises CEF, and that call does
  // not return in a subprocess; chrome_elf, which loads with the exe, already holds the keys.
  typedef int(__cdecl * set_crash_key_value_t)(const char*, size_t, const char*, size_t);
  HMODULE elf = GetModuleHandleW(L"chrome_elf.dll");
  set_crash_key_value_t set = elf ? (set_crash_key_value_t)(void*)GetProcAddress(elf, "SetCrashKeyValueImpl") : NULL;
  if (set) {
    set(key, key_length, value, value_length);
    return;
  }
#endif
  cef_string_t name = {0};
  cef_string_t text = {0};
  cef_string_utf8_to_utf16(key, key_length, &name);
  cef_string_utf8_to_utf16(value, value_length, &text);
  cef_set_crash_key_value(&name, &text);
  cef_string_clear(&name);
  cef_string_clear(&text);
}

static void set_crash_keys(const char* hex) {
  if (!hex) return;
  size_t length = strspn(hex, "0123456789abcdefABCDEF") / 2;
  char* bytes = (char*)malloc(length + 1);
  if (!bytes) return;
  for (size_t i = 0; i < length; i++)
    bytes[i] = (char)((hex_digit(hex[2 * i]) << 4) | hex_digit(hex[2 * i + 1]));
  bytes[length] = '\0';

  size_t pos = 0;
  while (pos < length) {
    const char* key = bytes + pos;
    size_t key_length = strlen(key);
    size_t value_pos = pos + key_length + 1;
    if (value_pos >= length) break;
    const char* value = bytes + value_pos;
    size_t value_length = strlen(value);
    set_crash_key(key, key_length, value, value_length);
    pos = value_pos + value_length + 1;
  }
  free(bytes);
}

static int check_api_hash(void) {
  // The first cef_api_hash call fixes the API version for the process.
  const char* hash = cef_api_hash(CEF_API_VERSION, 0);
  if (strcmp(hash, CEF_API_HASH_PLATFORM) != 0) {
    fprintf(stderr,
            "Riftway.Subprocess: libcef API hash %s does not match %s; the runtime and "
            "Riftway must come from the same CEF release.\n",
            hash, CEF_API_HASH_PLATFORM);
    return 0;
  }
  return 1;
}

// The app object lives for the whole process, so reference counting is a no-op.
static void CEF_CALLBACK app_add_ref(cef_base_ref_counted_t* self) { (void)self; }
static int CEF_CALLBACK app_release(cef_base_ref_counted_t* self) { (void)self; return 0; }
static int CEF_CALLBACK app_has_ref(cef_base_ref_counted_t* self) { (void)self; return 1; }

static void CEF_CALLBACK register_schemes(cef_app_t* self, cef_scheme_registrar_t* registrar) {
  (void)self;
  const char* entry = g_schemes;
  while (entry && *entry) {
    const char* comma = strchr(entry, ',');
    const char* end = comma ? comma : entry + strlen(entry);
    const char* colon = memchr(entry, ':', (size_t)(end - entry));

    // Malformed entries are skipped, as SchemeSwitch.Parse does.
    if (colon && colon > entry) {
      char* digits_end;
      long options = strtol(colon + 1, &digits_end, 10);
      if (digits_end == end && digits_end > colon + 1) {
        cef_string_t name = {0};
        cef_string_utf8_to_utf16(entry, (size_t)(colon - entry), &name);
        registrar->add_custom_scheme(registrar, &name, (int)options);
        cef_string_clear(&name);
      }
    }
    entry = comma ? comma + 1 : end;
  }
}

#ifndef _WIN32
// Without chrome_elf, crash keys only stick once cef_execute_process has started crash reporting (after the
// zygote fork on Linux), and a renderer is the only child CEF calls back after that. The switch is read from
// the process's own command line, which a zygote-forked renderer has just been given.
static void CEF_CALLBACK on_web_kit_initialized(cef_render_process_handler_t* self) {
  (void)self;
  cef_command_line_t* line = cef_command_line_get_global();
  if (!line) return;
  cef_string_t name = {0};
  cef_string_utf8_to_utf16("riftway-crash-keys", strlen("riftway-crash-keys"), &name);
  cef_string_userfree_t value = line->get_switch_value(line, &name);
  cef_string_clear(&name);
  if (value) {
    cef_string_utf8_t hex = {0};
    cef_string_utf16_to_utf8(value->str, value->length, &hex);
    set_crash_keys(hex.str);
    cef_string_utf8_clear(&hex);
    cef_string_userfree_free(value);
  }
  line->base.release(&line->base);
}

static cef_render_process_handler_t* CEF_CALLBACK get_render_process_handler(cef_app_t* self) {
  (void)self;
  static cef_render_process_handler_t handler;
  handler.base.size = sizeof(cef_render_process_handler_t);
  handler.base.add_ref = app_add_ref;
  handler.base.release = app_release;
  handler.base.has_one_ref = app_has_ref;
  handler.base.has_at_least_one_ref = app_has_ref;
  handler.on_web_kit_initialized = on_web_kit_initialized;
  return &handler;
}
#endif

static cef_app_t* app_for(const char* schemes) {
  static cef_app_t app;
  int has_schemes = schemes && *schemes;
#ifdef _WIN32
  if (!has_schemes) return NULL;
#endif

  if (has_schemes) {
    // A copy: Chromium overwrites argv to set process titles before CEF asks for the schemes.
    size_t size = strlen(schemes) + 1;
    char* copy = (char*)malloc(size);
    if (!copy) return NULL;
    memcpy(copy, schemes, size);
    g_schemes = copy;
    app.on_register_custom_schemes = register_schemes;
  }
  app.base.size = sizeof(cef_app_t);
  app.base.add_ref = app_add_ref;
  app.base.release = app_release;
  app.base.has_one_ref = app_has_ref;
  app.base.has_at_least_one_ref = app_has_ref;
#ifndef _WIN32
  app.get_render_process_handler = get_render_process_handler;
#endif
  return &app;
}

#ifdef _WIN32
// The value of switch |prefix| from the process's own command line, as UTF-8; NULL when absent.
static const char* windows_switch(const char* prefix) {
  const wchar_t* line = GetCommandLineW();
  int size = WideCharToMultiByte(CP_UTF8, 0, line, -1, NULL, 0, NULL, NULL);
  char* utf8 = size > 0 ? (char*)malloc((size_t)size) : NULL;
  if (!utf8) return NULL;
  WideCharToMultiByte(CP_UTF8, 0, line, -1, utf8, size, NULL, NULL);

  char* value = strstr(utf8, prefix);
  if (!value) return NULL;
  value += strlen(prefix);
  value[strcspn(value, " \"")] = '\0';
  return value;
}
#endif

// Riftway.Host.Native includes this file for its child-process path and brings its own entry point.
#ifndef RIFTWAY_SUBPROCESS_NO_MAIN
#ifdef _WIN32
// A GUI-subsystem entry: a console exe launched by a GUI app opens a console window per process.
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE prev, PWSTR command_line, int show) {
  (void)prev; (void)command_line; (void)show;
  if (!check_api_hash()) return 1;
  set_crash_keys(windows_switch(CRASH_KEYS_SWITCH));
  cef_main_args_t args = {instance};
  return cef_execute_process(&args, app_for(windows_switch(SCHEME_SWITCH)), NULL);
}
#else
int main(int argc, char* argv[]) {
#ifdef __APPLE__
  if (!mac_load(argc, argv)) return 1;
#endif
  if (!check_api_hash()) return 1;

  const char* schemes = NULL;
  for (int i = 1; i < argc; i++) {
    if (strncmp(argv[i], SCHEME_SWITCH, strlen(SCHEME_SWITCH)) == 0)
      schemes = argv[i] + strlen(SCHEME_SWITCH);
  }

  cef_main_args_t args = {argc, argv};
  int code = cef_execute_process(&args, app_for(schemes), NULL);
#ifdef __APPLE__
  void (*destroy)(void*) = (void (*)(void*))dlsym(RTLD_DEFAULT, "cef_sandbox_destroy");
  if (destroy) destroy(g_sandbox);
#endif
  return code;
}
#endif
#endif  // RIFTWAY_SUBPROCESS_NO_MAIN
