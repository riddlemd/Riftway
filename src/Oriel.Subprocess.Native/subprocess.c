// Oriel's Chromium subprocess: every zygote, renderer, GPU and utility process runs this exe.
// Native rather than .NET because Chromium's Linux zygote CHECKs that its process is
// single-threaded, and every .NET process (NativeAOT included) starts runtime threads first.

#include <stdio.h>
#include <string.h>

#ifdef _WIN32
#include <windows.h>
#endif

#include "include/capi/cef_app_capi.h"
#include "include/cef_api_hash.h"

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

#ifdef _WIN32
// A GUI-subsystem entry: a console exe launched by a GUI app opens a console window per process.
int WINAPI wWinMain(HINSTANCE instance, HINSTANCE prev, PWSTR command_line, int show) {
  (void)prev; (void)command_line; (void)show;
  if (!check_api_hash()) return 1;
  cef_main_args_t args = {instance};
  return cef_execute_process(&args, NULL, NULL);
}
#else
int main(int argc, char* argv[]) {
  if (!check_api_hash()) return 1;
  cef_main_args_t args = {argc, argv};
  return cef_execute_process(&args, NULL, NULL);
}
#endif
