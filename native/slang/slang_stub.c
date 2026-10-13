// libslang.so stand-in. The engine compiles shaders through Slang only when a shader is not in the shipped
// SPIR-V cache (Assets.zip, shaders/*.spv-cache); the cache key includes the compiler's build tag, which is what
// this library reports. Compiling itself is not available on the phone (yet): session creation fails.
#include <stddef.h>
#define API __attribute__((visibility("default")))
#define SLANG_FAIL ((int)0x80004005)

API const char* spGetBuildTagString(void) { return "2026.19"; }
API int slang_createGlobalSession(int apiVersion, void** outGlobalSession) { (void)apiVersion; if (outGlobalSession) *outGlobalSession = NULL; return SLANG_FAIL; }
API int slang_createGlobalSession2(const void* desc, void** outGlobalSession) { (void)desc; if (outGlobalSession) *outGlobalSession = NULL; return SLANG_FAIL; }
API int slang_createGlobalSessionWithoutCoreModule(int apiVersion, void** outGlobalSession) { (void)apiVersion; if (outGlobalSession) *outGlobalSession = NULL; return SLANG_FAIL; }
API void slang_shutdown(void) {}
