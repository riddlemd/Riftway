// Oriel's Chromium subprocess: every zygote, renderer, GPU and utility process runs this exe.
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
#include "include/capi/cef_crash_util_capi.h"
#include "include/cef_api_hash.h"

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
// "--oriel-custom-schemes=app:89,media:25" (name:cef_scheme_options_t). See SchemeSwitch.cs.
#define SCHEME_SWITCH "--oriel-custom-schemes="

static const char* g_schemes;

// CrashReporter's global annotations, which Electron sends from every process, arrive as the hex of
// UTF-8 "key\0value\0..." (CrashReporterConfig.EncodePairs); CEF only records keys its cfg declared.
#define CRASH_KEYS_SWITCH "--oriel-crash-keys="

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
            "Oriel.Subprocess: libcef API hash %s does not match %s; the runtime and "
            "Oriel must come from the same CEF release.\n",
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

static cef_app_t* app_for(const char* schemes) {
  static cef_app_t app;
  if (!schemes || !*schemes) return NULL;

  // A copy: Chromium overwrites argv to set process titles before CEF asks for the schemes.
  size_t size = strlen(schemes) + 1;
  char* copy = (char*)malloc(size);
  if (!copy) return NULL;
  memcpy(copy, schemes, size);
  g_schemes = copy;
  app.base.size = sizeof(cef_app_t);
  app.base.add_ref = app_add_ref;
  app.base.release = app_release;
  app.base.has_one_ref = app_has_ref;
  app.base.has_at_least_one_ref = app_has_ref;
  app.on_register_custom_schemes = register_schemes;
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
  if (!check_api_hash()) return 1;

  const char* schemes = NULL;
  for (int i = 1; i < argc; i++) {
    if (strncmp(argv[i], SCHEME_SWITCH, strlen(SCHEME_SWITCH)) == 0)
      schemes = argv[i] + strlen(SCHEME_SWITCH);
    else if (strncmp(argv[i], CRASH_KEYS_SWITCH, strlen(CRASH_KEYS_SWITCH)) == 0)
      set_crash_keys(argv[i] + strlen(CRASH_KEYS_SWITCH));
  }

  cef_main_args_t args = {argc, argv};
  return cef_execute_process(&args, app_for(schemes), NULL);
}
#endif
