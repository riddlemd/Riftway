// LD_PRELOAD shim for Linux VMs on Apple M4+ hosts, which expose SME without SVE.
// Chromium's libyuv dispatches its *_SME rows on HWCAP2_SME alone, but they are built with
// -march=armv9-a+sme (implies SVE), so their prologue runs a non-streaming SVE `cntd` and the
// renderer dies with SIGILL on the first software-converted video frame.
#define _GNU_SOURCE
#include <dlfcn.h>
#include <sys/auxv.h>

#define HWCAP2_SME_ALL ((1UL << 23) | (0x7fUL << 24) | (0x3fUL << 37))

unsigned long getauxval(unsigned long type) {
  static unsigned long (*real)(unsigned long);
  if (!real) real = (unsigned long (*)(unsigned long))dlsym(RTLD_NEXT, "getauxval");
  unsigned long value = real(type);
  return type == AT_HWCAP2 ? value & ~HWCAP2_SME_ALL : value;
}
