// The Android host of the 1.7 game: a NativeActivity that starts CoreCLR and runs the game's launcher assembly.
//
// Layout on the phone (external files dir of the app, <ext> = /sdcard/Android/data/<package>/files):
//   <ext>/runtime/*.dll   the .NET 10 framework assemblies (from the android-arm64 runtime pack)
//   <ext>/game/           the game's own assemblies, Assets.zip, Data/, Rendering/ (from the owner's Steam copy)
//   <ext>/game/args.txt   optional command line for the launcher, one argument per line
//   <ext>/home/           HOME: the game's save data lands under it
// The native libraries (libcoreclr.so, libclrjit.so, libSystem.*.so, our stubs) come with the APK. An APK built with
// PAYLOAD=1 also carries runtime/ and game/, unpacked on its first start (java_unpack_payload).
//
// Everything the runtime and the game print to stdout/stderr goes to logcat under the tag "port17".
#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif
#include <android/log.h>
#include <dirent.h>
#include <sched.h>
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

// The runtime writes its last words to logcat, not to stderr: on Android its fatal errors go through
// __android_log_write (tag DOTNET, error priority): "Stack overflow.", "Repeated 1000 times:", the frames, "Fatal error.",
// an unhandled exception. A tester's vivo lets an app read nothing of logcat from the run before, so two stack overflows
// of 2026-10-28 left no trace. Since Android 11 liblog lets a process see its own messages (__android_log_set_logger):
// DOTNET's errors are copied to stderr (logs/stderr.txt, kept as stderr-1.txt by the next start) and still reach logcat.
// Managed Console.Error goes the same way; the engine logs at info priority, which is not copied. At most 4 MB a run.
typedef void (*port_logger)(const struct __android_log_message*);
static port_logger logd_logger;
static volatile int copied_errors;

static void copy_runtime_errors(const struct __android_log_message* message)
{
    if (logd_logger) logd_logger(message);
    if (message->priority < ANDROID_LOG_ERROR || !message->tag || strcmp(message->tag, "DOTNET") != 0 || !message->message)
    {
        return;
    }
    size_t len = strlen(message->message);
    if (__sync_add_and_fetch(&copied_errors, (int)len + 1) > (4 << 20)) return;
    write(2, message->message, len);
    if (len == 0 || message->message[len - 1] != '\n') write(2, "\n", 1);
}

static void copy_runtime_log()
{
    void (*set_logger)(port_logger) = (void (*)(port_logger))dlsym(RTLD_DEFAULT, "__android_log_set_logger");
    logd_logger = (port_logger)dlsym(RTLD_DEFAULT, "__android_log_logd_logger");
    if (set_logger && logd_logger) set_logger(copy_runtime_errors);
    else fprintf(stderr, "game: the runtime's errors stay in logcat (no __android_log_set_logger before Android 11)\n");
}

// stdout goes to logcat through the pump; stderr goes straight to a file as well, because the runtime's last words
// before it aborts the process (an unhandled exception, a fatal error) would otherwise be lost with the process.
static void redirect_stdio(const char* ext)
{
    // Once a process (should the activity start again in it, its stderr.txt is not cut short or rotated away).
    static int done;
    if (done) return;
    done = 1;
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
    // The run before keeps its file as stderr-1.txt: a report is exported after the restart, and on phones that let an
    // app read nothing of logcat (a tester's vivo) the runtime's report of a stack overflow is nowhere else.
    char prev[1100];
    snprintf(prev, sizeof prev, "%s/stderr-1.txt", dir);
    rename(path, prev);
    FILE* f = fopen(path, "w");
    if (f)
    {
        dup2(fileno(f), 2);
        setvbuf(stderr, NULL, _IONBF, 0);
        copy_runtime_log();
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

// Calls Payload.ensure(activity) on the Java side (host/java/.../Payload.java): an APK built with the game's files
// inside unpacks them into <ext> when they are not there yet or are from another build. Null back: nothing to do or
// done; otherwise the problem (the player has been shown it).
static void java_unpack_payload(struct android_app* app)
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
    jstring name = (*env)->NewStringUTF(env, "com.local.waylith.port.Payload");
    jclass payload = (jclass)(*env)->CallObjectMethod(env, loader, loadClass, name);
    if ((*env)->ExceptionCheck(env) || payload == NULL)
    {
        (*env)->ExceptionDescribe(env);
        (*env)->ExceptionClear(env);
        LOGE("Payload class not found");
        return;
    }
    jmethodID ensure = (*env)->GetStaticMethodID(env, payload, "ensure", "(Landroid/app/Activity;)Ljava/lang/String;");
    jstring problem = (jstring)(*env)->CallStaticObjectMethod(env, payload, ensure, activity);
    if ((*env)->ExceptionCheck(env))
    {
        (*env)->ExceptionDescribe(env);
        (*env)->ExceptionClear(env);
        LOGE("unpacking the game's files threw");
        return;
    }
    if (problem != NULL)
    {
        const char* text = (*env)->GetStringUTFChars(env, problem, NULL);
        LOGE("the game's files: %s", text);
        (*env)->ReleaseStringUTFChars(env, problem, text);
    }
}

// The first line of a small text file, or "" (trailing spaces and line ends cut).
static void first_line(const char* path, char* out, size_t cap)
{
    out[0] = 0;
    FILE* f = fopen(path, "r");
    if (!f) return;
    if (!fgets(out, (int)cap, f)) out[0] = 0;
    fclose(f);
    size_t n = strlen(out);
    while (n > 0 && (out[n - 1] == '\n' || out[n - 1] == '\r' || out[n - 1] == ' ')) out[--n] = 0;
}

// The tester's APK (PAYLOAD=port host/build.sh) carries no game: <ext>/setup/PortSetup.dll makes the player's own copy ready.
static int is_tester(const char* ext)
{
    char path[1100];
    snprintf(path, sizeof path, "%s/setup/PortSetup.dll", ext);
    return file_exists(path);
}

// <ext>/game/port-setup.txt says which rules made the game ready last (the first line of <ext>/setup/version.txt).
static int needs_setup(const char* ext)
{
    char path[1100], want[256], have[256];
    if (!is_tester(ext)) return 0;
    snprintf(path, sizeof path, "%s/setup/version.txt", ext);
    first_line(path, want, sizeof want);
    snprintf(path, sizeof path, "%s/game/port-setup.txt", ext);
    first_line(path, have, sizeof have);
    return strcmp(want, have) != 0;
}

// A boolean method of MainActivity without arguments, called on this thread: prepareGame() (the import dialogs, copying
// the player's game folder, and running SetupService: setup/PortSetup.dll, in a process of its own, as the runtime can
// start only once in this one; true when the game is ready) and checkGameVersion() (a game newer than the port was made
// for: the player is told to update the APK; true to start). False when the player left (the activity is finishing then).
static int java_activity_bool(struct android_app* app, const char* method)
{
    JavaVM* vm = app->activity->vm;
    JNIEnv* env = NULL;
    if ((*vm)->AttachCurrentThread(vm, &env, NULL) != JNI_OK || env == NULL)
    {
        LOGE("could not attach to the Java VM");
        return 0;
    }
    jobject activity = app->activity->clazz;
    jclass activityClass = (*env)->GetObjectClass(env, activity);
    jmethodID prepare = (*env)->GetMethodID(env, activityClass, method, "()Z");
    if (prepare == NULL)
    {
        (*env)->ExceptionClear(env);
        LOGE("MainActivity.%s missing", method);
        return 0;
    }
    jboolean ready = (*env)->CallBooleanMethod(env, activity, prepare);
    if ((*env)->ExceptionCheck(env))
    {
        (*env)->ExceptionDescribe(env);
        (*env)->ExceptionClear(env);
        return 0;
    }
    return ready ? 1 : 0;
}

// What the runtime's PAL does first on every thread it starts (CPalThread::ThreadEntry): the process's CPU affinity
// (sched_getaffinity of the main thread) set on the new thread (sched_setaffinity(0, ...)). Its failure failed
// coreclr_initialize on a tester's HyperOS phone (0x8007054F; host/patch-coreclr.js now lets the runtime go on): done
// here once on a thread of our own, the result in the log and stderr, to see what such a phone refuses.
static void* probe_affinity_thread(void* arg)
{
    int* result = (int*)arg;
    cpu_set_t set;
    CPU_ZERO(&set);
    result[0] = sched_getaffinity(getpid(), sizeof set, &set) == 0 ? 0 : errno;
    result[2] = CPU_COUNT(&set);
    result[3] = 0;
    for (int c = 0; c < 32; c++)
    {
        if (CPU_ISSET(c, &set)) result[3] |= 1 << c;
    }
    cpu_set_t own;
    CPU_ZERO(&own);
    sched_getaffinity(0, sizeof own, &own);
    result[4] = 0;
    for (int c = 0; c < 32; c++)
    {
        if (CPU_ISSET(c, &own)) result[4] |= 1 << c;
    }
    result[1] = result[0] != 0 ? -1 : (sched_setaffinity(0, sizeof set, &set) == 0 ? 0 : errno);
    return NULL;
}

static void probe_affinity(const char* who)
{
    int result[5] = { 0 };
    pthread_t thread;
    if (pthread_create(&thread, NULL, probe_affinity_thread, result) != 0)
    {
        return;
    }
    pthread_join(thread, NULL);
    char text[256];
    snprintf(text, sizeof text, "%s: affinity of the process 0x%x (%d CPUs, get %s), a new thread's 0x%x; setting it on the thread: %s",
        who, result[3], result[2], result[0] == 0 ? "ok" : strerror(result[0]), result[4],
        result[1] == 0 ? "ok" : result[1] < 0 ? "not tried" : strerror(result[1]));
    LOGI("%s", text);
    fprintf(stderr, "%s\n", text);
}

// SetupService.runSetup(ext, root, libdir), in the setup process: the runtime with the framework and setup/ only, running
// setup/PortSetup.dll (setup/Setup.cs) on the imported game folder (root, "" to prepare the kept assemblies again).
// Returns its exit code, or a negative number when the runtime did not start (-2: coreclr_initialize failed, its
// HRESULT in setup/init-error.txt). The runtime's own words go to logs/setup-stderr.txt. tmp: the TMPDIR (as the game
// has); safe: a second try after -2 (a tester's Redmi Note 13 Pro, 2026-10-20), with the collector's heap limited (it
// reserves a range of the address space by the limit instead of by the machine) and without W^X double mapping.
JNIEXPORT jint JNICALL Java_com_local_waylith_port_SetupService_runSetup(JNIEnv* env, jclass cls, jstring jext, jstring jroot, jstring jlibdir, jstring jtmp, jboolean safe)
{
    (void)cls;
    const char* ext = (*env)->GetStringUTFChars(env, jext, NULL);
    const char* root = (*env)->GetStringUTFChars(env, jroot, NULL);
    const char* libdir = (*env)->GetStringUTFChars(env, jlibdir, NULL);
    const char* tmp = (*env)->GetStringUTFChars(env, jtmp, NULL);
    char runtime[1100], setup[1100], setupSlash[1100], entry[1200], home[1100], path[1200];
    snprintf(runtime, sizeof runtime, "%s/runtime", ext);
    snprintf(setup, sizeof setup, "%s/setup", ext);
    snprintf(setupSlash, sizeof setupSlash, "%s/setup/", ext);
    snprintf(entry, sizeof entry, "%s/PortSetup.dll", setup);
    snprintf(home, sizeof home, "%s/home", ext);
    snprintf(path, sizeof path, "%s/logs", ext);
    mkdir(path, 0775);
    snprintf(path, sizeof path, "%s/logs/setup-stderr.txt", ext);
    FILE* err = fopen(path, safe ? "a" : "w");
    if (err)
    {
        dup2(fileno(err), 2);
        setvbuf(stderr, NULL, _IONBF, 0);
    }
    mkdir(home, 0775);
    mkdir(tmp, 0775);
    setenv("HOME", home, 1);
    setenv("TMPDIR", tmp, 1);
    if (safe)
    {
        setenv("DOTNET_GCHeapHardLimit", "0x60000000", 1);
        setenv("DOTNET_EnableWriteXorExecute", "0", 1);
    }
    fprintf(stderr, "setup: starting the runtime%s\n", safe ? " (second try: GCHeapHardLimit 1.5 GB, no W^X)" : "");
    snprintf(path, sizeof path, "%s/setup/init-error.txt", ext);
    unlink(path);
    static char tpa[1 << 18];
    tpa[0] = 0;
    int count = 0;
    add_assemblies(tpa, sizeof tpa, runtime, NULL, &count);
    add_assemblies(tpa, sizeof tpa, setup, runtime, &count);
    const char* keys[] = { "TRUSTED_PLATFORM_ASSEMBLIES", "APP_PATHS", "NATIVE_DLL_SEARCH_DIRECTORIES", "APP_CONTEXT_BASE_DIRECTORY", "RUNTIME_IDENTIFIER", "System.Globalization.Invariant" };
    const char* values[] = { tpa, setup, libdir, setupSlash, "android-arm64", "true" };
    int result = -1;
    char coreclrPath[1100];
    snprintf(coreclrPath, sizeof coreclrPath, "%s/libcoreclr.so", libdir);
    void* lib = dlopen(coreclrPath, RTLD_NOW | RTLD_GLOBAL);
    coreclr_initialize_fn init = lib ? (coreclr_initialize_fn)dlsym(lib, "coreclr_initialize") : NULL;
    coreclr_execute_assembly_fn execute = lib ? (coreclr_execute_assembly_fn)dlsym(lib, "coreclr_execute_assembly") : NULL;
    if (!init || !execute)
    {
        LOGE("setup: the runtime is not there (%s)", lib ? "entry points missing" : dlerror());
    }
    else
    {
        void* host = NULL;
        unsigned int domain = 0;
        probe_affinity("setup");
        int rc = init(entry, "port17-setup", (int)(sizeof keys / sizeof keys[0]), keys, values, &host, &domain);
        if (rc < 0)
        {
            LOGE("setup: coreclr_initialize failed: 0x%08x", rc);
            fprintf(stderr, "setup: coreclr_initialize failed: 0x%08x\n", rc);
            FILE* f = fopen(path, "w");
            if (f)
            {
                fprintf(f, "0x%08x", rc);
                fclose(f);
            }
            result = -2;
        }
        else
        {
            const char* argv[] = { ext, root };
            unsigned int exitCode = 0;
            rc = execute(host, domain, 2, argv, entry, &exitCode);
            result = rc < 0 ? -3 : (int)exitCode;
            LOGI("setup: PortSetup returned %d", result);
        }
    }
    if (result == -1)
    {
        fprintf(stderr, "setup: the runtime is not there (%s)\n", lib ? "entry points missing" : dlerror());
    }
    (*env)->ReleaseStringUTFChars(env, jext, ext);
    (*env)->ReleaseStringUTFChars(env, jroot, root);
    (*env)->ReleaseStringUTFChars(env, jlibdir, libdir);
    (*env)->ReleaseStringUTFChars(env, jtmp, tmp);
    return result;
}

static void* run_runtime(void* arg)
{
    struct android_app* app = (struct android_app*)arg;
    const char* ext = app->activity->externalDataPath;
    const char* internal = app->activity->internalDataPath;
    pthread_attr_t self;
    size_t stack = 0;
    if (pthread_getattr_np(pthread_self(), &self) == 0)
    {
        pthread_attr_getstacksize(&self, &stack);
        pthread_attr_destroy(&self);
    }
    LOGI("main thread stack %zu KB", stack >> 10);
    fprintf(stderr, "game: main thread stack %zu KB\n", stack >> 10);

    const char* libdir = lib_dir();
    char runtime[1024], game[1024], gameSlash[1024], home[1024], tmp[1024];
    snprintf(runtime, sizeof runtime, "%s/runtime", ext);
    snprintf(game, sizeof game, "%s/game", ext);
    snprintf(gameSlash, sizeof gameSlash, "%s/game/", ext);
    snprintf(home, sizeof home, "%s/home", ext);
    snprintf(tmp, sizeof tmp, "%s/tmp", internal);
    mkdir(home, 0775);
    mkdir(tmp, 0775);
    java_unpack_payload(app);
    if (needs_setup(ext))
    {
        LOGI("the game is not prepared (tester's APK): asking the player for it");
        if (!java_activity_bool(app, "prepareGame"))
        {
            LOGI("no game: the player left");
            return NULL;
        }
    }
    if (is_tester(ext) && !java_activity_bool(app, "checkGameVersion"))
    {
        LOGI("the game is newer than the port: the player left to update");
        return NULL;
    }
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
    // The tiered compiler's profile-guided optimisation stays on (the runtime's default, as on the PC). Weighed
    // 2026-10-23 on the OnePlus. Without it: after a load at triple speed the frame rate stayed low for 4-6 s instead
    // of 10-12 s (137 mods, a 275 map), the main menu came after 49-50 s instead of 58, and patched methods already
    // copied into compiled code before their patch (hooks/InlineWatch.cs) were 7 places instead of 36. But a tick of a
    // late game (vanilla, 8.7 years, 22 000 things, ultrafast) took 2.05-2.09 M cycles without it against 1.82-1.93 M
    // with it, about 9 % more (3 % of the frame in the middle game). The user chose the speed of the late game.
    // runtime.env DOTNET_TieredPGO=0 still turns it off. Removing the 100 ms call counting delay
    // (TC_CallCountingDelayMs=0) is no way out: it lets optimised code in while the mods patch (89 such places).
    // A memory fault in managed code (an access violation that is no null reference) is an exception the game's own
    // try/catch gets, as with Ludeon's runtime (WaylithClr.CatchableAccessViolations in the PC game's runtimeconfig); the
    // stock runtime ends the process instead ("Fatal error. System.AccessViolationException"). A 1.6 mod (Shella
    // Backgrounds) does List.Add through a wrong reference in the options window: on the PC the window logs an error, on
    // a tester's vivo the game closed every time. Tried with mobile.txt avTest=1 (Entry.AccessViolationTest).
    setenv("DOTNET_legacyCorruptedStateExceptionsPolicy", "1", 1);
    // The stack of a thread the game or a mod starts without giving a size (new Thread(...)): the loading thread of a
    // long event (LongEventHandler, loading a save), the XML loader's threads, the thread pool's. The runtime leaves
    // them bionic's 1 MB; on the PC they get RimWorld.exe's 1.5 MB (the PE header's reserve applies to every such
    // thread), on Linux 8 MB. A tester's vivo with 148 mods overflowed the loading thread's 1008 KB in World.FinalizeInit
    // (2026-10-28 040232; the main thread, 16 MB since 10-27, was waiting). 16 MB as the main thread: address space only.
    // The runtime's name is Thread_DefaultStackSize (DefaultStackSize alone is not read).
    setenv("DOTNET_Thread_DefaultStackSize", "0x1000000", 1);
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
    probe_affinity("game");
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
        argv[0] = "-NoCrashDialog";
        argc = 1;
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
    // The game's main thread. Bionic's default stack is 1 MB; the PC's RimWorld.exe gives its main thread 1.5 MB (the PE
    // header's reserve), Linux 8 MB. Recursion as deep as the PC takes overflowed here first and ended the game at once.
    // 16 MB, reserved address space only: what is not touched takes no memory.
    pthread_t t;
    pthread_attr_t attr;
    pthread_attr_init(&attr);
    if (pthread_attr_setstacksize(&attr, 16u << 20) != 0)
    {
        LOGI("main thread: the 16 MB stack was refused, the default is used");
    }
    if (pthread_create(&t, &attr, run_runtime, app) != 0)
    {
        pthread_create(&t, NULL, run_runtime, app);
    }
    pthread_attr_destroy(&attr);
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
