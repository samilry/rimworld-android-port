// libmonomodc.so: what MonoMod (inside 0Harmony.dll) P/Invokes from glibc's libc / libdl.so.2, on bionic.
// patch/Patch.cs points those P/Invokes here (module "monomodc", entry points "mm_<name>"). Differences handled:
//   - errno is __errno() here, not __errno_location();
//   - sysconf names are numbered differently (glibc's _SC_PAGESIZE is 30, bionic's _SC_XOPEN_ENH_I18N);
//   - there is no /tmp: MonoMod drops its exception-helper library at /tmp/mm-exhelper.so.XXXXXX with mkstemp and
//     dlopens that name, so the file is made under TMPDIR and the dlopen of the /tmp name is redirected;
//   - dlopen and friends come from libdl.so (and libc.so does not export them).
// One function is not glibc's: mm_clear_cache, which patch/Patch.cs has MonoMod's LinuxSystem.PatchData call.
#include <dlfcn.h>
#include <errno.h>
#include <fcntl.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <sys/utsname.h>
#include <unistd.h>
#include <pthread.h>
#include <android/log.h>

#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, "port17", __VA_ARGS__)
#define EXPORT __attribute__((visibility("default")))

EXPORT int* mm___errno_location(void) { return (int*)__errno(); }

EXPORT ssize_t mm_read(int fd, void* buf, size_t count) { return read(fd, buf, count); }
EXPORT ssize_t mm_write(int fd, const void* buf, size_t count) { return write(fd, buf, count); }
EXPORT int mm_pipe2(int* fds, int flags) { return pipe2(fds, flags); }
EXPORT int mm_close(int fd) { return close(fd); }
EXPORT void* mm_mmap(void* addr, size_t length, int prot, int flags, int fd, long offset) { return mmap(addr, length, prot, flags, fd, (off_t)offset); }
EXPORT int mm_munmap(void* addr, size_t length) { return munmap(addr, length); }
EXPORT int mm_mprotect(void* addr, size_t len, int prot) { return mprotect(addr, len, prot); }
EXPORT int mm_mincore(void* addr, size_t len, unsigned char* vec) { return mincore(addr, len, vec); }
EXPORT int mm_uname(struct utsname* buf) { return uname(buf); }

// MonoMod writes code (a detour's jump over a compiled method, the stubs it builds) with a plain copy, and its Linux
// system, made for x86-64, never flushes the instruction cache after it, where its Windows and macOS systems do. On
// arm64 a core may go on running the old instructions of a method patched after it had run. PatchData calls this after
// it wrote executable memory.
EXPORT void mm_clear_cache(void* start, size_t length) { __builtin___clear_cache((char*)start, (char*)start + length); }

// glibc's _SC_* numbering (MonoMod's SysconfName enum) to bionic's.
EXPORT long mm_sysconf(int glibcName)
{
    static const int table[] = {
        _SC_ARG_MAX, _SC_CHILD_MAX, _SC_CLK_TCK, _SC_NGROUPS_MAX, _SC_OPEN_MAX, _SC_STREAM_MAX, _SC_TZNAME_MAX,
        _SC_JOB_CONTROL, _SC_SAVED_IDS, _SC_REALTIME_SIGNALS, _SC_PRIORITY_SCHEDULING, _SC_TIMERS, _SC_ASYNCHRONOUS_IO,
        _SC_PRIORITIZED_IO, _SC_SYNCHRONIZED_IO, _SC_FSYNC, _SC_MAPPED_FILES, _SC_MEMLOCK, _SC_MEMLOCK_RANGE,
        _SC_MEMORY_PROTECTION, _SC_MESSAGE_PASSING, _SC_SEMAPHORES, _SC_SHARED_MEMORY_OBJECTS, _SC_AIO_LISTIO_MAX,
        _SC_AIO_MAX, _SC_AIO_PRIO_DELTA_MAX, _SC_DELAYTIMER_MAX, _SC_MQ_OPEN_MAX, _SC_MQ_PRIO_MAX, _SC_VERSION,
        _SC_PAGESIZE, _SC_RTSIG_MAX, _SC_SEM_NSEMS_MAX, _SC_SEM_VALUE_MAX, _SC_SIGQUEUE_MAX, _SC_TIMER_MAX,
    };
    if (glibcName < 0 || glibcName >= (int)(sizeof table / sizeof table[0])) { errno = EINVAL; return -1; }
    return sysconf(table[glibcName]);
}

// Files MonoMod asked for under /tmp, by the name it believes they have.
static pthread_mutex_t g_lock = PTHREAD_MUTEX_INITIALIZER;
static struct { char asked[256]; char real[512]; } g_redirects[8];
static int g_redirectCount;

EXPORT int mm_mkstemp(char* template)
{
    if (strncmp(template, "/tmp/", 5) != 0 || access("/tmp", W_OK) == 0) return mkstemp(template);
    const char* dir = getenv("TMPDIR");
    if (!dir || !*dir) dir = "/data/local/tmp";
    char real[512];
    snprintf(real, sizeof real, "%s/%s", dir, template + 5);
    int fd = mkstemp(real);
    if (fd < 0) return fd;
    // The caller's buffer keeps its /tmp name, with the same random suffix; a dlopen of that name is redirected.
    memcpy(template + 5, real + strlen(dir) + 1, strlen(template) - 5);
    pthread_mutex_lock(&g_lock);
    if (g_redirectCount < (int)(sizeof g_redirects / sizeof g_redirects[0])) {
        snprintf(g_redirects[g_redirectCount].asked, sizeof g_redirects[0].asked, "%s", template);
        snprintf(g_redirects[g_redirectCount].real, sizeof g_redirects[0].real, "%s", real);
        g_redirectCount++;
    }
    pthread_mutex_unlock(&g_lock);
    LOGI("monomodc: %s is really %s", template, real);
    return fd;
}

EXPORT void* mm_dlopen(const char* filename, int flags)
{
    if (filename) {
        pthread_mutex_lock(&g_lock);
        for (int i = 0; i < g_redirectCount; i++) {
            if (strcmp(g_redirects[i].asked, filename) == 0) { filename = g_redirects[i].real; break; }
        }
        pthread_mutex_unlock(&g_lock);
    }
    void* h = dlopen(filename, flags);
    if (!h) LOGI("monomodc: dlopen(%s) failed: %s", filename ? filename : "(self)", dlerror());
    return h;
}
EXPORT int mm_dlclose(void* handle) { return dlclose(handle); }
EXPORT void* mm_dlsym(void* handle, const char* symbol) { return dlsym(handle, symbol); }
EXPORT char* mm_dlerror(void) { return dlerror(); }
