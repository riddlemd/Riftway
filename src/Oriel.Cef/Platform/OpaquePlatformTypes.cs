namespace Oriel.Cef.Interop;

// These three differ in layout per OS, so generated signatures take them as opaque pointers and
// callers pass the matching per-OS struct (cef_main_args_posix_t, cef_window_info_mac_t, ...).

/// <summary>Opaque stand-in for <c>cef_main_args_t</c>; see the per-OS structs.</summary>
public struct cef_main_args_t;

/// <summary>Opaque stand-in for <c>cef_window_info_t</c>; see the per-OS structs.</summary>
public struct cef_window_info_t;

/// <summary>Opaque stand-in for <c>cef_accelerated_paint_info_t</c>; see the per-OS structs.</summary>
public struct cef_accelerated_paint_info_t;
