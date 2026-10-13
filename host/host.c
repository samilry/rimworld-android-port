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
static char g_libdir[1024];

// The directory the APK's native libraries were extracted to (where this library lives).
static const char* lib_dir(void)
{
    if (g_libdir[0] == 0)
    {
        Dl_info info;
        if (dladdr((void*)lib_dir, &info) && info.dli_fname)
        {
            strncpy(g_libdir, info.dli_fname, sizeof g_libdir - 1);
            char* slash = strrchr(g_libdir, '/');
            if (slash) *slash = 0;
        }
    }
    return g_libdir;
}

// Loads the Android GLFW shim (host/glfw, libglfw.so) and hands it the activity, so that it sees the window and
// the input before the engine asks for them. The runtime later loads the same file by name and gets this handle.
static void attach_glfw(struct android_app* app)
{
    char path[1100];
    snprintf(path, sizeof path, "%s/libglfw.so", lib_dir());
    void* lib = dlopen(path, RTLD_NOW | RTLD_GLOBAL);
    if (!lib)
    {
        LOGE("dlopen %s failed: %s", path, dlerror());
        return;
    }
    void (*attach)(struct android_app*) = (void (*)(struct android_app*))dlsym(lib, "glfwAndroidAttach");
    if (!attach)
    {
        LOGE("glfwAndroidAttach missing in %s", path);
        return;
    }
    attach(app);
}

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

// stdout goes to logcat through the pump; stderr goes straight to a file as well, because the runtime's last words
// before it aborts the process (an unhandled exception, a fatal error) would otherwise be lost with the process.
static void redirect_stdio(const char* ext)
{
    int fds[2];
    if (pipe(fds) != 0) return;
    dup2(fds[1], 1);
    setvbuf(stdout, NULL, _IOLBF, 0);
    pthread_t t;
    pthread_create(&t, NULL, stdio_pump, (void*)(intptr_t)fds[0]);
    pthread_detach(t);

    char dir[1024], path[1100];
    snprintf(dir, sizeof dir, "%s/logs", ext);
    mkdir(dir, 0775);
    snprintf(path, sizeof path, "%s/stderr.txt", dir);
    FILE* f = fopen(path, "w");
    if (f)
    {
        dup2(fileno(f), 2);
        setvbuf(stderr, NULL, _IONBF, 0);
    }
    else
    {
        dup2(fds[1], 2);
    }
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

// Calls the Java side (host/java/.../Boot.java): Boot.load(path) loads a native library through Java, so that its
// JNI_OnLoad runs with the application's class loader. The runtime's crypto shim needs that (it works through
// Java's crypto classes). The Boot class is found through the activity's class loader, because a thread we
// attached ourselves only sees the system classes through FindClass.
static void java_load_library(struct android_app* app, const char* path)
{
    JavaVM* vm = app->activity->vm;
    JNIEnv* env = NULL;
    if ((*vm)->AttachCurrentThread(vm, &env, NULL) != JNI_OK || env == NULL)
    {
        LOGE("could not attach to the Java VM");
        return;
    }
    jobject activity = app->activity->clazz;
    jclass activityClass = (*env)->GetObjectClass(env, activity);
    jmethodID getClassLoader = (*env)->GetMethodID(env, activityClass, "getClassLoader", "()Ljava/lang/ClassLoader;");
    jobject loader = (*env)->CallObjectMethod(env, activity, getClassLoader);
    jclass loaderClass = (*env)->FindClass(env, "java/lang/ClassLoader");
    jmethodID loadClass = (*env)->GetMethodID(env, loaderClass, "loadClass", "(Ljava/lang/String;)Ljava/lang/Class;");
    jstring bootName = (*env)->NewStringUTF(env, "com.local.waylith.port.Boot");
    jclass boot = (jclass)(*env)->CallObjectMethod(env, loader, loadClass, bootName);
    if ((*env)->ExceptionCheck(env) || boot == NULL)
    {
        (*env)->ExceptionDescribe(env);
        (*env)->ExceptionClear(env);
        LOGE("Boot class not found");
        return;
    }
    jmethodID load = (*env)->GetStaticMethodID(env, boot, "load", "(Ljava/lang/String;)Ljava/lang/String;");
    jstring jpath = (*env)->NewStringUTF(env, path);
    jstring problem = (jstring)(*env)->CallStaticObjectMethod(env, boot, load, jpath);
    if (problem != NULL)
    {
        const char* text = (*env)->GetStringUTFChars(env, problem, NULL);
        LOGE("Java could not load %s: %s", path, text);
        (*env)->ReleaseStringUTFChars(env, problem, text);
    }
    else
    {
        LOGI("loaded through Java: %s", path);
    }
}

static void* run_runtime(void* arg)
{
    struct android_app* app = (struct android_app*)arg;
    const char* ext = app->activity->externalDataPath;
    const char* internal = app->activity->internalDataPath;

    const char* libdir = lib_dir();
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

    // Options for the Vulkan interposer (host/vulkan), from <game>/vulkan.txt: "key=value" per line.
    {
        const char* lines[32];
        static char storage[2048];
        char path[1100];
        snprintf(path, sizeof path, "%s/vulkan.txt", game);
        int n = read_args(path, lines, 32, storage, sizeof storage);
        if (n > 0)
        {
            char joined[2048] = "";
            for (int i = 0; i < n; i++)
            {
                size_t len = strlen(joined);
                snprintf(joined + len, sizeof joined - len, "%s%s", len ? " " : "", lines[i]);
            }
            setenv("PORT17_VULKAN", joined, 1);
            LOGI("vulkan options: %s", joined);
        }
    }

    // The collector's youngest generation. The runtime sizes its smallest budget by the processor's largest cache,
    // which it does not find on this kind of phone: it falls back to 256 KiB, and a game that allocates 20 MB a
    // second is then stopped for a collection 80 times a second (a tenth of the time, measured with 57 colonists at
    // triple speed; a PC's 16 MB cache gives it a 12 MB budget). 32 MiB here: about one collection a second in that
    // scene, 10 ms each; 16 MiB and 64 MiB measured the same frame rate, 256 MiB stops for 35 ms at a time.
    // Only the environment can set it. <game>/runtime.env
    // ("NAME=value" per line) comes after this and may say otherwise, or set anything else the runtime reads there.
    setenv("DOTNET_GCgen0size", "0x2000000", 1);
    {
        const char* lines[32];
        static char storage[4096];
        char path[1100];
        snprintf(path, sizeof path, "%s/runtime.env", game);
        int n = read_args(path, lines, 32, storage, sizeof storage);
        for (int i = 0; i < n; i++)
        {
            char* eq = strchr((char*)lines[i], '=');
            if (!eq || lines[i][0] == '#') continue;
            *eq = 0;
            if (eq[1]) setenv(lines[i], eq + 1, 1);
            else unsetenv(lines[i]);
            LOGI("environment: %s=%s", lines[i], eq + 1);
        }
    }

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
        "System.Globalization.PredefinedCulturesOnly",
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
        "false",
        "true",
        "true",
        "false",
    };
    int propertyCount = (int)(sizeof keys / sizeof keys[0]);

    // Extra runtime properties from <game>/runtime.properties ("key=value" per line), for experiments.
    const char* allKeys[64];
    const char* allValues[64];
    for (int i = 0; i < propertyCount; i++)
    {
        allKeys[i] = keys[i];
        allValues[i] = values[i];
    }
    static char extraStorage[8192];
    const char* extraLines[32];
    char propsPath[1100];
    snprintf(propsPath, sizeof propsPath, "%s/runtime.properties", game);
    int extraCount = read_args(propsPath, extraLines, 32, extraStorage, sizeof extraStorage);
    for (int i = 0; i < extraCount && propertyCount < 64; i++)
    {
        char* eq = strchr((char*)extraLines[i], '=');
        if (!eq) continue;
        *eq = 0;
        allKeys[propertyCount] = extraLines[i];
        allValues[propertyCount] = eq + 1;
        LOGI("property %s=%s", extraLines[i], eq + 1);
        propertyCount++;
    }

    char cryptoPath[1100];
    snprintf(cryptoPath, sizeof cryptoPath, "%s/libSystem.Security.Cryptography.Native.Android.so", libdir);
    java_load_library(app, cryptoPath);

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
    int rc = init(exePath, "port17", propertyCount, allKeys, allValues, &host, &domain);
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
    redirect_stdio(app->activity->externalDataPath);
    LOGI("port17 host starting (internal %s, external %s)", app->activity->internalDataPath, app->activity->externalDataPath);
    attach_glfw(app);
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
