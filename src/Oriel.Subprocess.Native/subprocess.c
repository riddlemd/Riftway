// Oriel's Chromium subprocess: every zygote, renderer, GPU and utility process runs this exe.
// Native rather than .NET because Chromium's Linux zygote CHECKs that its process is
// single-threaded, and every .NET process (NativeAOT included) starts runtime threads first.

#include <stdio.h>
#include <string.h>

#include "include/capi/cef_app_capi.h"
#include "include/cef_api_hash.h"

int main(int argc, char* argv[]) {
  // The first cef_api_hash call fixes the API version for the process.
  const char* hash = cef_api_hash(CEF_API_VERSION, 0);
  if (strcmp(hash, CEF_API_HASH_PLATFORM) != 0) {
    fprintf(stderr,
            "Oriel.Subprocess: libcef API hash %s does not match %s; the runtime and "
            "Oriel must come from the same CEF release.\n",
            hash, CEF_API_HASH_PLATFORM);
    return 1;
  }

  cef_main_args_t args = {argc, argv};
  return cef_execute_process(&args, NULL, NULL);
}
