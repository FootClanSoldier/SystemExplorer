#pragma once

#include <stdint.h>

/*
 * Minimal subset of the Godot 4.6 GDExtension C ABI required by the
 * System Explorer CodeServiceBootstrap.
 *
 * These definitions mirror the Godot 4.6.3 gdextension_interface data for
 * entry/init callbacks only. Keep this header intentionally small: it is not
 * a replacement for the full Godot GDExtension interface header.
 */

#ifdef __cplusplus
extern "C" {
#endif

typedef uint8_t GDExtensionBool;
typedef void *GDExtensionClassLibraryPtr;

typedef enum {
    GDEXTENSION_INITIALIZATION_CORE = 0,
    GDEXTENSION_INITIALIZATION_SERVERS = 1,
    GDEXTENSION_INITIALIZATION_SCENE = 2,
    GDEXTENSION_INITIALIZATION_EDITOR = 3,
    GDEXTENSION_MAX_INITIALIZATION_LEVEL = 4,
} GDExtensionInitializationLevel;

typedef void (*GDExtensionInitializeCallback)(
    void *p_userdata,
    GDExtensionInitializationLevel p_level
);

typedef void (*GDExtensionDeinitializeCallback)(
    void *p_userdata,
    GDExtensionInitializationLevel p_level
);

typedef struct {
    GDExtensionInitializationLevel minimum_initialization_level;
    void *userdata;
    GDExtensionInitializeCallback initialize;
    GDExtensionDeinitializeCallback deinitialize;
} GDExtensionInitialization;

typedef void (*GDExtensionInterfaceFunctionPtr)();
typedef GDExtensionInterfaceFunctionPtr (*GDExtensionInterfaceGetProcAddress)(
    const char *p_function_name
);

#ifdef __cplusplus
}
#endif
