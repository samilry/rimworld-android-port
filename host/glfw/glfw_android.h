// The host's side of the Android GLFW shim (host/glfw/glfw_android.c, built as libglfw.so): the host hands over the
// android_app before the runtime starts; the shim installs its own command and input handlers on it.
#pragma once
#include "../android_native_app_glue.h"

#ifdef __cplusplus
extern "C" {
#endif

// Installs the shim on the app (onAppCmd/onInputEvent). Called on the glue thread before anything else.
void glfwAndroidAttach(struct android_app* app);

#ifdef __cplusplus
}
#endif
