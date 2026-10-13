// MonoMod's native exception helper (exhelper_linux_arm64.so, embedded in 0Harmony.dll) rebuilt for Android: the
// original has no DT_NEEDED entries and wants _Unwind_* from glibc's libgcc_s, which bionic does not offer; this one
// links LLVM's libunwind statically (NDK default). The four exports and their contract are the original's:
//   eh_get_exception_ptr()    per-thread slot holding the pending native exception (_Unwind_Exception*), or 0
//   eh_has_exception()        whether the slot is set
//   eh_managed_to_native      trampoline: calls the native function in x9 with x0..x7 as given; a native exception
//                             escaping from it is caught here, stored in the slot, and 0 is returned to managed code
//   eh_native_to_managed      trampoline: clears the slot, calls the managed function in x9, and re-raises the stored
//                             exception into the native caller when the managed side caught one
// The trampolines live in exhelper_arm64.S; this file holds the slot and the personality routine.
#include <pthread.h>
#include <stdlib.h>
#include <stdint.h>
#include <unwind.h>

#define EXPORT __attribute__((visibility("default")))
#define HIDDEN __attribute__((visibility("hidden")))

static pthread_key_t eh_key;

__attribute__((constructor)) static void eh_init(void)
{
    pthread_key_create(&eh_key, free);
}

HIDDEN void** eh_slot(void)
{
    void** slot = (void**)pthread_getspecific(eh_key);
    if (!slot)
    {
        slot = (void**)calloc(1, sizeof *slot);
        pthread_setspecific(eh_key, slot);
    }
    return slot;
}

EXPORT int eh_has_exception(void)
{
    return *eh_slot() != NULL;
}

// Personality of eh_managed_to_native: the LSDA is one int32, the landing pad's offset from the LSDA itself. Any
// exception (foreign ones included: the runtime's C++ exceptions) is claimed; the landing pad gets the exception in x0.
HIDDEN _Unwind_Reason_Code eh_personality(int version, _Unwind_Action actions, uint64_t exceptionClass,
                                          struct _Unwind_Exception* exception, struct _Unwind_Context* context)
{
    (void)version; (void)exceptionClass;
    if (actions & _UA_FORCE_UNWIND)
    {
        return _URC_CONTINUE_UNWIND;
    }
    const int32_t* lsda = (const int32_t*)_Unwind_GetLanguageSpecificData(context);
    int32_t offset = lsda ? *lsda : 0;
    if (actions & _UA_SEARCH_PHASE)
    {
        return offset != 0 ? _URC_HANDLER_FOUND : _URC_CONTINUE_UNWIND;
    }
    if ((actions & _UA_HANDLER_FRAME) && offset != 0)
    {
        _Unwind_SetIP(context, (uintptr_t)lsda + (uintptr_t)(intptr_t)offset);
        _Unwind_SetGR(context, 0, (uintptr_t)exception);
        return _URC_INSTALL_CONTEXT;
    }
    return _URC_CONTINUE_UNWIND;
}
