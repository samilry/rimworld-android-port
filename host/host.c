// The Android host of the 1.7 game: a NativeActivity that starts CoreCLR and runs the game's launcher assembly.
//
// Layout on the phone (external files dir of the app, <ext> = /sdcard/Android/data/<package>/files):
//   <ext>/runtime/*.dll   the .NET 10 framework assemblies (from the android-arm64 runtime pack)
//   <ext>/game/           the game's own assemblies, Assets.zip, Data/, Rendering/ (from the owner's Steam copy)
//   <ext>/game/args.txt   optional command line for the launcher, one argument per line
//   <ext>/home/           HOME: the game's save data lands under it
// The native libraries (libcoreclr.so, libclrjit.so, libSystem.*.so, our stubs) come with the APK.
//
// Everything the runtime and the game print to stdout/stderr goes to logcat under the tag "port17".
#include <android/log.h>
#include <dirent.h>
#include <dlfcn.h>
#include <errno.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>
#include "android_native_app_glue.h"

#define TAG "port17"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, TAG, __VA_ARGS__)

typedef int (*coreclr_initialize_fn)(const char* exePath, const char* appDomainFriendlyName, int propertyCount,
                                     const char** propertyKeys, const char** propertyValues, void** hostHandle, unsigned int* domainId);
typedef int (*coreclr_execute_assembly_fn)(void* hostHandle, unsigned int domainId, int argc, const char** argv,
                                           const char* managedAssemblyPath, unsigned int* exitCode);
typedef int (*coreclr_shutdown_fn)(void* hostHandle, unsigned int domainId);

static struct android_app* g_app;

// stdout and stderr of the whole process are read by a thread and written to logcat line by line.
static void* stdio_pump(void* arg)
{
    int fd = (int)(intptr_t)arg;
    char buf[4096];
    char line[4096];
    size_t n = 0;
    for (;;)
    {
        ssize_t got = read(fd, buf, sizeof buf);
        if (got <= 0) break;
        for (ssize_t i = 0; i < got; i++)
        {
            char c = buf[i];
            if (c == '\n' || n == sizeof line - 1)
            {
                line[n] = 0;
                if (n > 0) __android_log_write(ANDROID_LOG_INFO, TAG, line);
                n = 0;
            }
            else if (c != '\r')
            {
                line[n++] = c;
            }
        }
    }
    return NULL;
}

static void redirect_stdio(void)
{
    int fds[2];
    if (pipe(fds) != 0) return;
    dup2(fds[1], 1);
    dup2(fds[1], 2);
    setvbuf(stdout, NULL, _IOLBF, 0);
    setvbuf(stderr, NULL, _IONBF, 0);
    pthread_t t;
    pthread_create(&t, NULL, stdio_pump, (void*)(intptr_t)fds[0]);
    pthread_detach(t);
}

static int ends_with(const char* s, const char* suffix)
{
    size_t a = strlen(s), b = strlen(suffix);
    return a >= b && strcmp(s + a - b, suffix) == 0;
}

static int file_exists(const char* path)
{
    struct stat st;
    return stat(path, &st) == 0;
}

// Appends "dir/*.dll" to the list, ':' separated. Files whose name also exists in skipDir are left out, so that the
// game folder's copies of framework assemblies do not shadow the runtime's.
static void add_assemblies(char* list, size_t cap, const char* dir, const char* skipDir, int* count)
{
    DIR* d = opendir(dir);
    if (!d)
    {
        LOGE("cannot list %s: %s", dir, strerror(errno));
        return;
    }
    struct dirent* e;
    while ((e = readdir(d)) != NULL)
    {
        if (!ends_with(e->d_name, ".dll")) continue;
        if (skipDir)
        {
            char other[1024];
            snprintf(other, sizeof other, "%s/%s", skipDir, e->d_name);
            if (file_exists(other)) continue;
        }
        size_t len = strlen(list);
        snprintf(list + len, cap - len, "%s%s/%s", len ? ":" : "", dir, e->d_name);
        (*count)++;
    }
    closedir(d);
}

static int read_args(const char* path, const char** argv, int max, char* storage, size_t cap)
{
    FILE* f = fopen(path, "r");
    if (!f) return 0;
    int argc = 0;
    size_t used = 0;
    char line[512];
    while (argc < max && fgets(line, sizeof line, f))
    {
        size_t n = strlen(line);
        while (n > 0 && (line[n - 1] == '\n' || line[n - 1] == '\r' || line[n - 1] == ' ')) line[--n] = 0;
        if (n == 0 || line[0] == '#') continue;
        if (used + n + 1 > cap) break;
        memcpy(storage + used, line, n + 1);
        argv[argc++] = storage + used;
        used += n + 1;
    }
    fclose(f);
    return argc;
}

static void* run_runtime(void* arg)
{
    struct android_app* app = (struct android_app*)arg;
    const char* ext = app->activity->externalDataPath;
    const char* internal = app->activity->internalDataPath;

    Dl_info info;
    char libdir[1024] = "";
    if (dladdr((void*)run_runtime, &info) && info.dli_fname)
    {
        strncpy(libdir, info.dli_fname, sizeof libdir - 1);
        char* slash = strrchr(libdir, '/');
        if (slash) *slash = 0;
    }
    char runtime[1024], game[1024], gameSlash[1024], home[1024], tmp[1024];
    snprintf(runtime, sizeof runtime, "%s/runtime", ext);
    snprintf(game, sizeof game, "%s/game", ext);
    snprintf(gameSlash, sizeof gameSlash, "%s/game/", ext);
    snprintf(home, sizeof home, "%s/home", ext);
    snprintf(tmp, sizeof tmp, "%s/tmp", internal);
    mkdir(home, 0775);
    mkdir(tmp, 0775);
    LOGI("libs %s", libdir);
    LOGI("runtime %s", runtime);
    LOGI("game %s", game);
    setenv("HOME", home, 1);
    setenv("TMPDIR", tmp, 1);
    setenv("XDG_CONFIG_HOME", home, 1);
    setenv("XDG_DATA_HOME", home, 1);
    chdir(game);

    static char tpa[1 << 20];
    tpa[0] = 0;
    int count = 0;
    add_assemblies(tpa, sizeof tpa, runtime, NULL, &count);
    int runtimeCount = count;
    add_assemblies(tpa, sizeof tpa, game, runtime, &count);
    LOGI("trusted assemblies: %d from the runtime, %d from the game", runtimeCount, count - runtimeCount);

    char nativeDirs[2200];
    snprintf(nativeDirs, sizeof nativeDirs, "%s:%s", libdir, game);

    // Our startup hook (hooks/StartupHook.cs) runs before the launcher's Main when it is present.
    char hooks[1100];
    snprintf(hooks, sizeof hooks, "%s/PortHooks.dll", game);
    if (!file_exists(hooks)) hooks[0] = 0;
    else LOGI("startup hook %s", hooks);

    const char* keys[] = {
        "STARTUP_HOOKS",
        "TRUSTED_PLATFORM_ASSEMBLIES",
        "APP_PATHS",
        "APP_NI_PATHS",
        "NATIVE_DLL_SEARCH_DIRECTORIES",
        "PLATFORM_RESOURCE_ROOTS",
        "APP_CONTEXT_BASE_DIRECTORY",
        "RUNTIME_IDENTIFIER",
        "System.Globalization.Invariant",
        "System.GC.Server",
        "System.GC.Concurrent",
        "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization",
        "System.Reflection.Metadata.MetadataUpdater.IsSupported",
    };
    const char* values[] = {
        hooks,
        tpa,
        game,
        game,
        nativeDirs,
        game,
        gameSlash,
        "android-arm64",
        "true",
        "false",
        "true",
        "true",
        "false",
    };
    int propertyCount = (int)(sizeof keys / sizeof keys[0]);

    char coreclrPath[1100];
    snprintf(coreclrPath, sizeof coreclrPath, "%s/libcoreclr.so", libdir);
    void* lib = dlopen(coreclrPath, RTLD_NOW | RTLD_GLOBAL);
    if (!lib)
    {
        LOGE("dlopen libcoreclr.so failed: %s", dlerror());
        return NULL;
    }
    coreclr_initialize_fn init = (coreclr_initialize_fn)dlsym(lib, "coreclr_initialize");
    coreclr_execute_assembly_fn execute = (coreclr_execute_assembly_fn)dlsym(lib, "coreclr_execute_assembly");
    coreclr_shutdown_fn shutdown = (coreclr_shutdown_fn)dlsym(lib, "coreclr_shutdown");
    if (!init || !execute)
    {
        LOGE("coreclr entry points missing");
        return NULL;
    }

    char exePath[1100];
    snprintf(exePath, sizeof exePath, "%s/RimWorld.dll", game);
    void* host = NULL;
    unsigned int domain = 0;
    int rc = init(exePath, "port17", propertyCount, keys, values, &host, &domain);
    if (rc < 0)
    {
        LOGE("coreclr_initialize failed: 0x%08x", rc);
        return NULL;
    }
    LOGI("coreclr initialized (domain %u)", domain);

    const char* argv[32];
    static char argStorage[8192];
    char argsPath[1100];
    snprintf(argsPath, sizeof argsPath, "%s/args.txt", game);
    int argc = read_args(argsPath, argv, 32, argStorage, sizeof argStorage);
    if (argc == 0)
    {
        argv[0] = "-Headless";
        argv[1] = "-NoCrashDialog";
        argv[2] = "-Verbose";
        argc = 3;
    }
    for (int i = 0; i < argc; i++) LOGI("arg %d: %s", i, argv[i]);

    unsigned int exitCode = 0;
    rc = execute(host, domain, argc, argv, exePath, &exitCode);
    if (rc < 0)
    {
        LOGE("coreclr_execute_assembly failed: 0x%08x", rc);
    }
    else
    {
        LOGI("launcher returned, exit code %u", exitCode);
    }
    if (shutdown) shutdown(host, domain);
    LOGI("runtime finished");
    return NULL;
}

void android_main(struct android_app* app)
{
    g_app = app;
    redirect_stdio();
    LOGI("port17 host starting (internal %s, external %s)", app->activity->internalDataPath, app->activity->externalDataPath);
    pthread_t t;
    pthread_create(&t, NULL, run_runtime, app);
    pthread_detach(t);
    for (;;)
    {
        int events;
        struct android_poll_source* source;
        while (ALooper_pollAll(250, NULL, &events, (void**)&source) >= 0)
        {
            if (source) source->process(app, source);
            if (app->destroyRequested)
            {
                LOGI("activity destroyed");
                return;
            }
        }
    }
}
