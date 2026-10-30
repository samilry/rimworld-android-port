#define _GNU_SOURCE
// GLFW for the Android host: the GLFW 3.4 C API (the part a Vulkan application uses) on top of a NativeActivity.
// Built as libglfw.so; the engine's Brutal.Glfw binds it by name ("glfw" -> libglfw.so).
//
// Threads: the glue thread (android_main in host.c) receives the activity's commands and input events through our
// handlers and turns them into a queue of small records. The engine thread calls glfwPollEvents /
// glfwWaitEventsTimeout, which drain that queue and invoke the GLFW callbacks there, so every callback runs on the
// thread that polls, as with real GLFW. The window is the activity's ANativeWindow; the Vulkan surface comes from
// VK_KHR_android_surface. There is exactly one window and one monitor.
//
// Touch is reported as a mouse: the first finger is the left button and moves the cursor; a second finger down is a
// right click at its position. Real mice (USB, Bluetooth) are reported as mice. Keys are mapped to GLFW key codes;
// Back is Escape. A richer touch layer comes later, on the managed side.
#include <android/input.h>
#include <android/keycodes.h>
#include <android/log.h>
#include <android/native_window.h>
#include <dlfcn.h>
#include <errno.h>
#include <pthread.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <unistd.h>
#include <jni.h>
#define VK_USE_PLATFORM_ANDROID_KHR
#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>
#include "glfw_android.h"

#define TAG "port17"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, TAG, __VA_ARGS__)
#define API __attribute__((visibility("default")))

// ---- GLFW constants (glfw3.h, 3.4) ----
#define GLFW_TRUE 1
#define GLFW_FALSE 0
#define GLFW_RELEASE 0
#define GLFW_PRESS 1
#define GLFW_REPEAT 2
#define GLFW_KEY_UNKNOWN -1
#define GLFW_MOD_SHIFT 0x0001
#define GLFW_MOD_CONTROL 0x0002
#define GLFW_MOD_ALT 0x0004
#define GLFW_MOD_SUPER 0x0008
#define GLFW_MOD_CAPS_LOCK 0x0010
#define GLFW_MOD_NUM_LOCK 0x0020
#define GLFW_NO_ERROR 0
#define GLFW_NOT_INITIALIZED 0x00010001
#define GLFW_INVALID_ENUM 0x00010003
#define GLFW_INVALID_VALUE 0x00010004
#define GLFW_API_UNAVAILABLE 0x00010006
#define GLFW_PLATFORM_ERROR 0x00010008
#define GLFW_FEATURE_UNAVAILABLE 0x0001000C
#define GLFW_FOCUSED 0x00020001
#define GLFW_ICONIFIED 0x00020002
#define GLFW_RESIZABLE 0x00020003
#define GLFW_VISIBLE 0x00020004
#define GLFW_DECORATED 0x00020005
#define GLFW_AUTO_ICONIFY 0x00020006
#define GLFW_FLOATING 0x00020007
#define GLFW_MAXIMIZED 0x00020008
#define GLFW_CENTER_CURSOR 0x00020009
#define GLFW_TRANSPARENT_FRAMEBUFFER 0x0002000A
#define GLFW_HOVERED 0x0002000B
#define GLFW_FOCUS_ON_SHOW 0x0002000C
#define GLFW_MOUSE_PASSTHROUGH 0x0002000D
#define GLFW_CLIENT_API 0x00022001
#define GLFW_CONTEXT_CREATION_API 0x0002200B
#define GLFW_NO_API 0
#define GLFW_CURSOR 0x00033001
#define GLFW_STICKY_KEYS 0x00033002
#define GLFW_STICKY_MOUSE_BUTTONS 0x00033003
#define GLFW_LOCK_KEY_MODS 0x00033004
#define GLFW_RAW_MOUSE_MOTION 0x00033005
#define GLFW_CURSOR_NORMAL 0x00034001
#define GLFW_CONNECTED 0x00040001
#define GLFW_PLATFORM_NULL 0x00060005
#define GLFW_DONT_CARE -1

typedef struct GLFWmonitor GLFWmonitor;
typedef struct GLFWwindow GLFWwindow;
typedef struct GLFWcursor GLFWcursor;
typedef struct GLFWvidmode { int width, height, redBits, greenBits, blueBits, refreshRate; } GLFWvidmode;
typedef struct GLFWgammaramp { unsigned short* red; unsigned short* green; unsigned short* blue; unsigned int size; } GLFWgammaramp;
typedef struct GLFWimage { int width, height; unsigned char* pixels; } GLFWimage;
typedef struct GLFWgamepadstate { unsigned char buttons[15]; float axes[6]; } GLFWgamepadstate;
typedef struct GLFWallocator { void* allocate; void* reallocate; void* deallocate; void* user; } GLFWallocator;

typedef void (*GLFWerrorfun)(int, const char*);
typedef void (*GLFWwindowposfun)(GLFWwindow*, int, int);
typedef void (*GLFWwindowsizefun)(GLFWwindow*, int, int);
typedef void (*GLFWwindowclosefun)(GLFWwindow*);
typedef void (*GLFWwindowrefreshfun)(GLFWwindow*);
typedef void (*GLFWwindowfocusfun)(GLFWwindow*, int);
typedef void (*GLFWwindowiconifyfun)(GLFWwindow*, int);
typedef void (*GLFWwindowmaximizefun)(GLFWwindow*, int);
typedef void (*GLFWframebuffersizefun)(GLFWwindow*, int, int);
typedef void (*GLFWwindowcontentscalefun)(GLFWwindow*, float, float);
typedef void (*GLFWmousebuttonfun)(GLFWwindow*, int, int, int);
typedef void (*GLFWcursorposfun)(GLFWwindow*, double, double);
typedef void (*GLFWcursorenterfun)(GLFWwindow*, int);
typedef void (*GLFWscrollfun)(GLFWwindow*, double, double);
typedef void (*GLFWkeyfun)(GLFWwindow*, int, int, int, int);
typedef void (*GLFWcharfun)(GLFWwindow*, unsigned int);
typedef void (*GLFWcharmodsfun)(GLFWwindow*, unsigned int, int);
typedef void (*GLFWdropfun)(GLFWwindow*, int, const char**);
typedef void (*GLFWmonitorfun)(GLFWmonitor*, int);
typedef void (*GLFWjoystickfun)(int, int);
typedef void (*GLFWglproc)(void);
typedef void (*GLFWvkproc)(void);

// ---- the event queue, filled by the glue thread, drained by the engine thread ----
enum { EV_WINDOW_INIT = 1, EV_WINDOW_TERM, EV_RESIZE, EV_FOCUS, EV_CURSOR, EV_BUTTON, EV_SCROLL, EV_KEY, EV_CHAR, EV_CLOSE };

typedef struct Event
{
    int type;
    int a, b, c, d;
    double x, y;
} Event;

#define QUEUE_CAP 4096
static Event g_queue[QUEUE_CAP];
static int g_qhead, g_qtail;
static pthread_mutex_t g_lock = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t g_cond = PTHREAD_COND_INITIALIZER;

// ---- raw finger events for the managed touch layer (PortMobile) ----
// Every finger event is recorded here as well. With the managed mode on, this file no longer turns fingers into
// mouse events: the managed side reads them (glfwAndroidPollTouches) and injects what the engine should see.
typedef struct TouchEvent { int type; int id; float x, y; double time; } TouchEvent; // type: 1 down, 2 move, 3 up, 4 cancel, 5 the soft keyboard's action key
#define TOUCH_CAP 2048
static TouchEvent g_touches[TOUCH_CAP];
static int g_thead, g_ttail;
static int g_touchManaged;

static void record_touch(int type, int id, float x, float y)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    TouchEvent e = { type, id, x, y, ts.tv_sec + ts.tv_nsec / 1e9 };
    pthread_mutex_lock(&g_lock);
    int next = (g_ttail + 1) % TOUCH_CAP;
    if (next == g_thead) g_thead = (g_thead + 1) % TOUCH_CAP;
    g_touches[g_ttail] = e;
    g_ttail = next;
    pthread_mutex_unlock(&g_lock);
}

// Shared state (under g_lock): what the activity currently has.
static struct android_app* g_app;
static ANativeWindow* g_window;      // the activity's current window, NULL between TERM and INIT
static int g_width, g_height;        // its size in pixels
// The size the game renders at (the options' Resolution on a phone: 1080p, 720p), 0 for the window's own. The engine is
// told this size for its window and framebuffer; the Vulkan interposer gives it as the surface's current extent, and
// Android scales the smaller swapchain to the window. Fingers, mice and the camera hole come in the window's pixels and
// are scaled to it (g_scaleX / g_scaleY: the glue thread reads them for input, which it also handles).
static int g_renderWidth, g_renderHeight;
static double g_scaleX = 1.0, g_scaleY = 1.0;
static int g_focused = 1;
static int g_destroyed;
static float g_density = 1.0f;

// The size the engine is told (under g_lock): the render size when one is set, else the window's.
static int engine_width_locked(void) { return g_renderWidth > 0 ? g_renderWidth : g_width; }
static int engine_height_locked(void) { return g_renderHeight > 0 ? g_renderHeight : g_height; }

static void update_scale_locked(void)
{
    g_scaleX = g_width > 0 ? (double)engine_width_locked() / g_width : 1.0;
    g_scaleY = g_height > 0 ? (double)engine_height_locked() / g_height : 1.0;
}

// Engine-thread state (no lock needed; only the polling thread touches it).
static int g_initialized;
static int g_lastError;
static char g_lastErrorText[256];
static struct GLFWwindow { int dummy; } g_windowObject;
static struct GLFWmonitor { int dummy; } g_monitorObject;
static GLFWmonitor* g_monitors[1] = { &g_monitorObject };
static GLFWvidmode g_vidmode = { 1920, 1080, 8, 8, 8, 60 };
static struct GLFWcursor { int shape; } g_cursors[64];
static int g_cursorCount;
static int g_windowCreated;
static int g_shouldClose;
static int g_reportedFocused = 1;
static int g_reportedWidth, g_reportedHeight;
static int g_sizeAnnounced;   // the size callbacks were delivered at least once since the window was created
static double g_cursorX, g_cursorY;
static int g_mouseButtons[8];
static unsigned char g_keys[512];
static int g_cursorMode = GLFW_CURSOR_NORMAL;
static int g_stickyKeys, g_stickyButtons, g_lockKeyMods, g_rawMotion;
static void* g_windowUser;
static void* g_monitorUser;
static char g_title[256] = "Waylith";
static char* g_clipboard;
static struct timespec g_start;
static void* g_vulkan;
static int g_surfaceCreated; // a Vulkan surface was made on some window of this activity
static VkSurfaceKHR g_lastSurface;          // the last surface made here, and the window it was made on
static ANativeWindow* g_lastSurfaceWindow;
static PFN_vkGetInstanceProcAddr g_vkGetInstanceProcAddr;

static GLFWerrorfun cb_error;
static GLFWmonitorfun cb_monitor;
static GLFWjoystickfun cb_joystick;
static GLFWwindowposfun cb_pos;
static GLFWwindowsizefun cb_size;
static GLFWwindowclosefun cb_close;
static GLFWwindowrefreshfun cb_refresh;
static GLFWwindowfocusfun cb_focus;
static GLFWwindowiconifyfun cb_iconify;
static GLFWwindowmaximizefun cb_maximize;
static GLFWframebuffersizefun cb_fbsize;
static GLFWwindowcontentscalefun cb_scale;
static GLFWmousebuttonfun cb_button;
static GLFWcursorposfun cb_cursor;
static GLFWcursorenterfun cb_enter;
static GLFWscrollfun cb_scroll;
static GLFWkeyfun cb_key;
static GLFWcharfun cb_char;
static GLFWcharmodsfun cb_charmods;
static GLFWdropfun cb_drop;

static void set_error(int code, const char* text)
{
    g_lastError = code;
    snprintf(g_lastErrorText, sizeof g_lastErrorText, "%s", text);
    LOGW("glfw error 0x%x: %s", code, text);
    if (cb_error) cb_error(code, text);
}

// ---- glue-thread side ----
static void push_locked(const Event* e)
{
    int next = (g_qtail + 1) % QUEUE_CAP;
    if (next == g_qhead)
    {
        // Full: drop the oldest. The engine was not polling for a long while (loading); old input is worthless.
        g_qhead = (g_qhead + 1) % QUEUE_CAP;
    }
    g_queue[g_qtail] = *e;
    g_qtail = next;
    pthread_cond_broadcast(&g_cond);
}

static void push(int type, int a, int b, int c, int d, double x, double y)
{
    Event e = { type, a, b, c, d, x, y };
    pthread_mutex_lock(&g_lock);
    push_locked(&e);
    pthread_mutex_unlock(&g_lock);
}

static void on_app_cmd(struct android_app* app, int32_t cmd)
{
    switch (cmd)
    {
    case APP_CMD_INIT_WINDOW:
    {
        pthread_mutex_lock(&g_lock);
        g_window = app->window;
        g_width = ANativeWindow_getWidth(app->window);
        g_height = ANativeWindow_getHeight(app->window);
        if (app->config) g_density = AConfiguration_getDensity(app->config) / 160.0f;
        update_scale_locked();
        LOGI("glfw: window %dx%d (density %.2f)%s", g_width, g_height, g_density, g_renderWidth > 0 ? ", rendering smaller" : "");
        if (g_surfaceCreated && g_vulkan)
        {
            // A new window after the surface was made on the old one: the Vulkan interposer (our libvulkan.so) makes the
            // engine recreate its surface and swapchain.
            void (*changed)(void) = (void (*)(void))dlsym(g_vulkan, "port17_vulkan_window_changed");
            if (changed) changed();
        }
        Event e = { EV_WINDOW_INIT, engine_width_locked(), engine_height_locked(), g_width, g_height, 0, 0 };
        push_locked(&e);
        pthread_mutex_unlock(&g_lock);
        break;
    }
    case APP_CMD_TERM_WINDOW:
    {
        pthread_mutex_lock(&g_lock);
        g_window = NULL;
        LOGI("glfw: window gone");
        Event t = { EV_WINDOW_TERM, 0, 0, 0, 0, 0, 0 };
        push_locked(&t);
        pthread_mutex_unlock(&g_lock);
        break;
    }
    case APP_CMD_WINDOW_RESIZED:
    case APP_CMD_CONFIG_CHANGED:
        if (app->window)
        {
            pthread_mutex_lock(&g_lock);
            int w = ANativeWindow_getWidth(app->window), h = ANativeWindow_getHeight(app->window);
            if (w != g_width || h != g_height)
            {
                g_width = w;
                g_height = h;
                update_scale_locked();
                LOGI("glfw: window resized %dx%d", w, h);
                Event r = { EV_RESIZE, engine_width_locked(), engine_height_locked(), w, h, 0, 0 };
                push_locked(&r);
            }
            pthread_mutex_unlock(&g_lock);
        }
        break;
    case APP_CMD_GAINED_FOCUS:
    {
        pthread_mutex_lock(&g_lock);
        g_focused = 1;
        Event f = { EV_FOCUS, 1, 0, 0, 0, 0, 0 };
        push_locked(&f);
        pthread_mutex_unlock(&g_lock);
        break;
    }
    case APP_CMD_LOST_FOCUS:
    {
        pthread_mutex_lock(&g_lock);
        g_focused = 0;
        Event l = { EV_FOCUS, 0, 0, 0, 0, 0, 0 };
        push_locked(&l);
        pthread_mutex_unlock(&g_lock);
        break;
    }
    case APP_CMD_DESTROY:
    {
        pthread_mutex_lock(&g_lock);
        g_destroyed = 1;
        Event d = { EV_CLOSE, 0, 0, 0, 0, 0, 0 };
        push_locked(&d);
        pthread_mutex_unlock(&g_lock);
        break;
    }
    default:
        break;
    }
}

static int key_from_android(int code)
{
    if (code >= AKEYCODE_A && code <= AKEYCODE_Z) return 'A' + (code - AKEYCODE_A);
    if (code >= AKEYCODE_0 && code <= AKEYCODE_9) return '0' + (code - AKEYCODE_0);
    if (code >= AKEYCODE_F1 && code <= AKEYCODE_F12) return 290 + (code - AKEYCODE_F1);
    if (code >= AKEYCODE_NUMPAD_0 && code <= AKEYCODE_NUMPAD_9) return 320 + (code - AKEYCODE_NUMPAD_0);
    switch (code)
    {
    case AKEYCODE_BACK: case AKEYCODE_ESCAPE: return 256;
    case AKEYCODE_ENTER: return 257;
    case AKEYCODE_TAB: return 258;
    case AKEYCODE_DEL: return 259;
    case AKEYCODE_INSERT: return 260;
    case AKEYCODE_FORWARD_DEL: return 261;
    case AKEYCODE_DPAD_RIGHT: return 262;
    case AKEYCODE_DPAD_LEFT: return 263;
    case AKEYCODE_DPAD_DOWN: return 264;
    case AKEYCODE_DPAD_UP: return 265;
    case AKEYCODE_PAGE_UP: return 266;
    case AKEYCODE_PAGE_DOWN: return 267;
    case AKEYCODE_MOVE_HOME: return 268;
    case AKEYCODE_MOVE_END: return 269;
    case AKEYCODE_CAPS_LOCK: return 280;
    case AKEYCODE_SCROLL_LOCK: return 281;
    case AKEYCODE_NUM_LOCK: return 282;
    case AKEYCODE_SYSRQ: return 283;
    case AKEYCODE_BREAK: return 284;
    case AKEYCODE_SPACE: return 32;
    case AKEYCODE_APOSTROPHE: return 39;
    case AKEYCODE_COMMA: return 44;
    case AKEYCODE_MINUS: return 45;
    case AKEYCODE_PERIOD: return 46;
    case AKEYCODE_SLASH: return 47;
    case AKEYCODE_SEMICOLON: return 59;
    case AKEYCODE_EQUALS: return 61;
    case AKEYCODE_LEFT_BRACKET: return 91;
    case AKEYCODE_BACKSLASH: return 92;
    case AKEYCODE_RIGHT_BRACKET: return 93;
    case AKEYCODE_GRAVE: return 96;
    case AKEYCODE_NUMPAD_DOT: return 330;
    case AKEYCODE_NUMPAD_DIVIDE: return 331;
    case AKEYCODE_NUMPAD_MULTIPLY: return 332;
    case AKEYCODE_NUMPAD_SUBTRACT: return 333;
    case AKEYCODE_NUMPAD_ADD: return 334;
    case AKEYCODE_NUMPAD_ENTER: return 335;
    case AKEYCODE_NUMPAD_EQUALS: return 336;
    case AKEYCODE_SHIFT_LEFT: return 340;
    case AKEYCODE_CTRL_LEFT: return 341;
    case AKEYCODE_ALT_LEFT: return 342;
    case AKEYCODE_META_LEFT: return 343;
    case AKEYCODE_SHIFT_RIGHT: return 344;
    case AKEYCODE_CTRL_RIGHT: return 345;
    case AKEYCODE_ALT_RIGHT: return 346;
    case AKEYCODE_META_RIGHT: return 347;
    case AKEYCODE_MENU: return 348;
    default: return GLFW_KEY_UNKNOWN;
    }
}

static int mods_from_meta(int meta)
{
    int mods = 0;
    if (meta & AMETA_SHIFT_ON) mods |= GLFW_MOD_SHIFT;
    if (meta & AMETA_CTRL_ON) mods |= GLFW_MOD_CONTROL;
    if (meta & AMETA_ALT_ON) mods |= GLFW_MOD_ALT;
    if (meta & AMETA_META_ON) mods |= GLFW_MOD_SUPER;
    if (meta & AMETA_CAPS_LOCK_ON) mods |= GLFW_MOD_CAPS_LOCK;
    if (meta & AMETA_NUM_LOCK_ON) mods |= GLFW_MOD_NUM_LOCK;
    return mods;
}

// A printable character for a key, for the char callback (no IME yet; US layout).
static unsigned int char_from_key(int glfwKey, int mods)
{
    int shift = (mods & GLFW_MOD_SHIFT) != 0;
    int caps = (mods & GLFW_MOD_CAPS_LOCK) != 0;
    if (glfwKey >= 'A' && glfwKey <= 'Z') return (shift ^ caps) ? (unsigned)glfwKey : (unsigned)(glfwKey + 32);
    if (glfwKey >= '0' && glfwKey <= '9')
    {
        static const char shifted[] = ")!@#$%^&*(";
        return shift ? (unsigned)shifted[glfwKey - '0'] : (unsigned)glfwKey;
    }
    if (glfwKey >= 320 && glfwKey <= 329) return (unsigned)('0' + (glfwKey - 320));
    switch (glfwKey)
    {
    case 32: return ' ';
    case 39: return shift ? '"' : '\'';
    case 44: return shift ? '<' : ',';
    case 45: return shift ? '_' : '-';
    case 46: return shift ? '>' : '.';
    case 47: return shift ? '?' : '/';
    case 59: return shift ? ':' : ';';
    case 61: return shift ? '+' : '=';
    case 91: return shift ? '{' : '[';
    case 92: return shift ? '|' : '\\';
    case 93: return shift ? '}' : ']';
    case 96: return shift ? '~' : '`';
    case 330: return '.';
    case 331: return '/';
    case 332: return '*';
    case 333: return '-';
    case 334: return '+';
    case 336: return '=';
    default: return 0;
    }
}

static int g_primaryPointerId = -1;   // the finger that is the cursor
static int g_secondaryPointerId = -1; // a second finger: right button
static int g_mouseButtonState;

#define TX(e, i) ((double)AMotionEvent_getX((e), (i)) * g_scaleX)
#define TY(e, i) ((double)AMotionEvent_getY((e), (i)) * g_scaleY)

// The devices the last mouse event and the last letter or digit key from a hardware keyboard came from (-1: none yet;
// the soft keyboard's keys come from the virtual keyboard, -1). mobile/InputMode.cs counts a mouse or keyboard the
// system does not call external once it has been used (glfwAndroidLastDevices).
static volatile int g_mouseDevice = -1, g_keyDevice = -1;

API void glfwAndroidLastDevices(int* mouse, int* key)
{
    if (mouse) *mouse = g_mouseDevice;
    if (key) *key = g_keyDevice;
}

static int32_t on_input_event(struct android_app* app, AInputEvent* event)
{
    (void)app;
    int32_t type = AInputEvent_getType(event);
    if (type == AINPUT_EVENT_TYPE_KEY)
    {
        int code = AKeyEvent_getKeyCode(event);
        if (code == AKEYCODE_VOLUME_UP || code == AKEYCODE_VOLUME_DOWN || code == AKEYCODE_VOLUME_MUTE || code == AKEYCODE_POWER)
        {
            return 0;
        }
        int device = AInputEvent_getDeviceId(event);
        if (device >= 0 && AKeyEvent_getAction(event) == AKEY_EVENT_ACTION_DOWN
            && ((code >= AKEYCODE_A && code <= AKEYCODE_Z) || (code >= AKEYCODE_0 && code <= AKEYCODE_9)))
        {
            g_keyDevice = device;
        }
        int glfwKey = key_from_android(code);
        if (glfwKey == GLFW_KEY_UNKNOWN) return 0;
        int action = AKeyEvent_getAction(event);
        int mods = mods_from_meta(AKeyEvent_getMetaState(event));
        if (action == AKEY_EVENT_ACTION_DOWN)
        {
            int repeat = AKeyEvent_getRepeatCount(event);
            push(EV_KEY, glfwKey, code, repeat > 0 ? GLFW_REPEAT : GLFW_PRESS, mods, 0, 0);
            unsigned int ch = char_from_key(glfwKey, mods);
            if (ch && !(mods & (GLFW_MOD_CONTROL | GLFW_MOD_ALT | GLFW_MOD_SUPER))) push(EV_CHAR, (int)ch, mods, 0, 0, 0, 0);
        }
        else if (action == AKEY_EVENT_ACTION_UP)
        {
            push(EV_KEY, glfwKey, code, GLFW_RELEASE, mods, 0, 0);
        }
        return 1;
    }
    if (type != AINPUT_EVENT_TYPE_MOTION) return 0;

    int32_t source = AInputEvent_getSource(event);
    int32_t action = AMotionEvent_getAction(event);
    int32_t masked = action & AMOTION_EVENT_ACTION_MASK;
    size_t index = (size_t)((action & AMOTION_EVENT_ACTION_POINTER_INDEX_MASK) >> AMOTION_EVENT_ACTION_POINTER_INDEX_SHIFT);
    size_t count = AMotionEvent_getPointerCount(event);
    int mods = mods_from_meta(AMotionEvent_getMetaState(event));
    int isMouse = (source & AINPUT_SOURCE_MOUSE) == AINPUT_SOURCE_MOUSE;

    if (isMouse)
    {
        g_mouseDevice = AInputEvent_getDeviceId(event);
        double x = TX(event, 0), y = TY(event, 0);
        switch (masked)
        {
        case AMOTION_EVENT_ACTION_HOVER_MOVE:
        case AMOTION_EVENT_ACTION_MOVE:
            push(EV_CURSOR, 0, 0, 0, 0, x, y);
            break;
        case AMOTION_EVENT_ACTION_DOWN:
        case AMOTION_EVENT_ACTION_UP:
        case AMOTION_EVENT_ACTION_BUTTON_PRESS:
        case AMOTION_EVENT_ACTION_BUTTON_RELEASE:
        {
            int state = AMotionEvent_getButtonState(event);
            if (masked == AMOTION_EVENT_ACTION_UP) state = 0;
            int changed = state ^ g_mouseButtonState;
            push(EV_CURSOR, 0, 0, 0, 0, x, y);
            static const int map[3][2] = { { AMOTION_EVENT_BUTTON_PRIMARY, 0 }, { AMOTION_EVENT_BUTTON_SECONDARY, 1 }, { AMOTION_EVENT_BUTTON_TERTIARY, 2 } };
            for (int i = 0; i < 3; i++)
            {
                if (changed & map[i][0]) push(EV_BUTTON, map[i][1], (state & map[i][0]) ? GLFW_PRESS : GLFW_RELEASE, mods, 0, x, y);
            }
            g_mouseButtonState = state;
            break;
        }
        case AMOTION_EVENT_ACTION_SCROLL:
        {
            double v = AMotionEvent_getAxisValue(event, AMOTION_EVENT_AXIS_VSCROLL, 0);
            double h = AMotionEvent_getAxisValue(event, AMOTION_EVENT_AXIS_HSCROLL, 0);
            push(EV_CURSOR, 0, 0, 0, 0, x, y);
            push(EV_SCROLL, 0, 0, 0, 0, h, v);
            break;
        }
        default:
            break;
        }
        return 1;
    }

    // Raw finger events for the managed touch layer (every pointer of the event).
    switch (masked)
    {
    case AMOTION_EVENT_ACTION_DOWN: record_touch(1, AMotionEvent_getPointerId(event, 0), TX(event, 0), TY(event, 0)); break;
    case AMOTION_EVENT_ACTION_POINTER_DOWN: if (index < count) record_touch(1, AMotionEvent_getPointerId(event, index), TX(event, index), TY(event, index)); break;
    case AMOTION_EVENT_ACTION_MOVE: for (size_t i = 0; i < count; i++) record_touch(2, AMotionEvent_getPointerId(event, i), TX(event, i), TY(event, i)); break;
    case AMOTION_EVENT_ACTION_POINTER_UP: if (index < count) record_touch(3, AMotionEvent_getPointerId(event, index), TX(event, index), TY(event, index)); break;
    case AMOTION_EVENT_ACTION_UP: record_touch(3, AMotionEvent_getPointerId(event, 0), TX(event, 0), TY(event, 0)); break;
    case AMOTION_EVENT_ACTION_CANCEL: for (size_t i = 0; i < count; i++) record_touch(4, AMotionEvent_getPointerId(event, i), TX(event, i), TY(event, i)); break;
    default: break;
    }
    if (g_touchManaged) return 1;

    // Touch (and stylus): first finger = cursor + left button, second finger = right button.
    switch (masked)
    {
    case AMOTION_EVENT_ACTION_DOWN:
    {
        g_primaryPointerId = AMotionEvent_getPointerId(event, 0);
        double x = TX(event, 0), y = TY(event, 0);
        push(EV_CURSOR, 0, 0, 0, 0, x, y);
        push(EV_BUTTON, 0, GLFW_PRESS, mods, 0, x, y);
        break;
    }
    case AMOTION_EVENT_ACTION_POINTER_DOWN:
    {
        if (g_secondaryPointerId < 0 && index < count)
        {
            g_secondaryPointerId = AMotionEvent_getPointerId(event, index);
            double x = TX(event, index), y = TY(event, index);
            push(EV_CURSOR, 0, 0, 0, 0, x, y);
            push(EV_BUTTON, 1, GLFW_PRESS, mods, 0, x, y);
        }
        break;
    }
    case AMOTION_EVENT_ACTION_MOVE:
    {
        for (size_t i = 0; i < count; i++)
        {
            if (AMotionEvent_getPointerId(event, i) == g_primaryPointerId)
            {
                push(EV_CURSOR, 0, 0, 0, 0, TX(event, i), TY(event, i));
                break;
            }
        }
        break;
    }
    case AMOTION_EVENT_ACTION_POINTER_UP:
    {
        if (index < count)
        {
            int id = AMotionEvent_getPointerId(event, index);
            double x = TX(event, index), y = TY(event, index);
            if (id == g_secondaryPointerId)
            {
                push(EV_CURSOR, 0, 0, 0, 0, x, y);
                push(EV_BUTTON, 1, GLFW_RELEASE, mods, 0, x, y);
                g_secondaryPointerId = -1;
            }
            else if (id == g_primaryPointerId)
            {
                push(EV_CURSOR, 0, 0, 0, 0, x, y);
                push(EV_BUTTON, 0, GLFW_RELEASE, mods, 0, x, y);
                g_primaryPointerId = -1;
            }
        }
        break;
    }
    case AMOTION_EVENT_ACTION_UP:
    case AMOTION_EVENT_ACTION_CANCEL:
    {
        double x = TX(event, 0), y = TY(event, 0);
        if (masked == AMOTION_EVENT_ACTION_UP) push(EV_CURSOR, 0, 0, 0, 0, x, y);
        if (g_secondaryPointerId >= 0) push(EV_BUTTON, 1, GLFW_RELEASE, mods, 0, x, y);
        if (g_primaryPointerId >= 0) push(EV_BUTTON, 0, GLFW_RELEASE, mods, 0, x, y);
        g_primaryPointerId = g_secondaryPointerId = -1;
        break;
    }
    default:
        break;
    }
    return 1;
}

// ---- the managed touch layer's API (PortMobile, P/Invoke "glfw") ----
API void glfwAndroidSetTouchMode(int managed)
{
    pthread_mutex_lock(&g_lock);
    g_touchManaged = managed;
    g_primaryPointerId = g_secondaryPointerId = -1;
    pthread_mutex_unlock(&g_lock);
    LOGI("glfw: touch mode %s", managed ? "managed" : "mouse");
}

API int glfwAndroidPollTouches(TouchEvent* out, int max)
{
    int n = 0;
    pthread_mutex_lock(&g_lock);
    while (n < max && g_thead != g_ttail)
    {
        out[n++] = g_touches[g_thead];
        g_thead = (g_thead + 1) % TOUCH_CAP;
    }
    pthread_mutex_unlock(&g_lock);
    return n;
}

// Injected events join the queue like the activity's own and reach the engine at its next poll.
API void glfwAndroidInjectCursor(double x, double y) { push(EV_CURSOR, 0, 0, 0, 0, x, y); }
API void glfwAndroidInjectButton(int button, int action, int mods) { push(EV_BUTTON, button, action, mods, 0, 0, 0); }
API void glfwAndroidInjectScroll(double dx, double dy) { push(EV_SCROLL, 0, 0, 0, 0, dx, dy); }
API void glfwAndroidInjectKey(int key, int action, int mods) { push(EV_KEY, key, 0, action, mods, 0, 0); }
API void glfwAndroidInjectChar(unsigned int codepoint, int mods) { push(EV_CHAR, (int)codepoint, mods, 0, 0, 0, 0); }
API float glfwAndroidGetDensity(void) { return g_density; }

// Calls a "void name(boolean)" method of the activity (host/java MainActivity) from whatever thread this is.
static int call_activity(const char* name, int value)
{
    if (!g_app || !g_app->activity || !g_app->activity->vm) return 0;
    JavaVM* vm = g_app->activity->vm;
    JNIEnv* env = NULL;
    int attached = 0;
    if ((*vm)->GetEnv(vm, (void**)&env, JNI_VERSION_1_6) != JNI_OK)
    {
        if ((*vm)->AttachCurrentThread(vm, &env, NULL) != JNI_OK) return 0;
        attached = 1;
    }
    jobject activity = g_app->activity->clazz;
    jclass cls = (*env)->GetObjectClass(env, activity);
    jmethodID method = (*env)->GetMethodID(env, cls, name, "(Z)V");
    int ok = 0;
    if (method) { (*env)->CallVoidMethod(env, activity, method, (jboolean)(value != 0)); ok = 1; }
    else LOGW("glfw: no %s on the activity", name);
    if ((*env)->ExceptionCheck(env)) { (*env)->ExceptionDescribe(env); (*env)->ExceptionClear(env); LOGW("glfw: %s threw", name); ok = 0; }
    (*env)->DeleteLocalRef(env, cls);
    if (attached) (*vm)->DetachCurrentThread(vm);
    return ok;
}

// A method of the activity that takes nothing and returns a String: copied into out (empty when it fails); returns its length.
static int call_activity_string(const char* name, char* out, int size)
{
    if (!out || size <= 0) return 0;
    out[0] = 0;
    if (!g_app || !g_app->activity || !g_app->activity->vm) return 0;
    JavaVM* vm = g_app->activity->vm;
    JNIEnv* env = NULL;
    int attached = 0;
    if ((*vm)->GetEnv(vm, (void**)&env, JNI_VERSION_1_6) != JNI_OK)
    {
        if ((*vm)->AttachCurrentThread(vm, &env, NULL) != JNI_OK) return 0;
        attached = 1;
    }
    jobject activity = g_app->activity->clazz;
    jclass cls = (*env)->GetObjectClass(env, activity);
    jmethodID method = (*env)->GetMethodID(env, cls, name, "()Ljava/lang/String;");
    if (method)
    {
        jstring text = (jstring)(*env)->CallObjectMethod(env, activity, method);
        if (text && !(*env)->ExceptionCheck(env))
        {
            const char* chars = (*env)->GetStringUTFChars(env, text, NULL);
            if (chars)
            {
                snprintf(out, (size_t)size, "%s", chars);
                (*env)->ReleaseStringUTFChars(env, text, chars);
            }
        }
        if (text) (*env)->DeleteLocalRef(env, text);
    }
    if ((*env)->ExceptionCheck(env)) { (*env)->ExceptionDescribe(env); (*env)->ExceptionClear(env); out[0] = 0; }
    (*env)->DeleteLocalRef(env, cls);
    if (attached) (*vm)->DetachCurrentThread(vm);
    return (int)strlen(out);
}

// The display cutout (the camera's hole), through the activity's cutoutRects(): "left,top,right,bottom;..." in window
// pixels for the screen's present turn, "" without one. Copied into out; returns its length (mobile/Cutout.cs).
API int glfwAndroidCutout(char* out, int size)
{
    char window[512];
    if (!out || size <= 0) return 0;
    out[0] = 0;
    if (call_activity_string("cutoutRects", window, sizeof window) <= 0) return 0;
    pthread_mutex_lock(&g_lock);
    double sx = g_scaleX, sy = g_scaleY;
    pthread_mutex_unlock(&g_lock);
    // In the pixels the game draws in (the activity gives the window's).
    int length = 0;
    char* rest = NULL;
    for (char* rect = strtok_r(window, ";", &rest); rect && length < size - 1; rect = strtok_r(NULL, ";", &rest))
    {
        int l, t, r, b;
        if (sscanf(rect, "%d,%d,%d,%d", &l, &t, &r, &b) != 4) continue;
        length += snprintf(out + length, (size_t)(size - length), "%s%d,%d,%d,%d", length > 0 ? ";" : "",
                           (int)(l * sx), (int)(t * sy), (int)(r * sx + 0.999), (int)(b * sy + 0.999));
    }
    if (length >= size) length = size - 1;
    return length;
}

// The window's own size: the screen's resolution, whatever the game renders at.
API void glfwAndroidViewSize(int* width, int* height)
{
    pthread_mutex_lock(&g_lock);
    if (width) *width = g_width;
    if (height) *height = g_height;
    pthread_mutex_unlock(&g_lock);
}

// The engine sets a resolution (the options' Resolution, Screen.SetResolution): on the phone that is the size the game
// renders at. Only sizes of the window's shape, smaller than it, are taken; the window's own size goes back to it. Anything
// else (a PC's 1920x1080 from old prefs, the engine's default) leaves it as it is.
static void request_render_size(int width, int height)
{
    pthread_mutex_lock(&g_lock);
    int ww = g_width, wh = g_height;
    pthread_mutex_unlock(&g_lock);
    if (width <= 0 || height <= 0 || ww <= 0 || wh <= 0) return;
    int rw = 0, rh = 0;
    if (width != ww || height != wh)
    {
        double shape = (double)width / height, screen = (double)ww / wh;
        if (width > ww || height > wh || shape < screen * 0.98 || shape > screen * 1.02)
        {
            LOGI("glfw: resolution %dx%d not taken (the window is %dx%d)", width, height, ww, wh);
            return;
        }
        rw = width;
        rh = height;
    }
    pthread_mutex_lock(&g_lock);
    if (rw == g_renderWidth && rh == g_renderHeight)
    {
        pthread_mutex_unlock(&g_lock);
        return;
    }
    g_renderWidth = rw;
    g_renderHeight = rh;
    update_scale_locked();
    if (g_vulkan)
    {
        void (*told)(uint32_t, uint32_t) = (void (*)(uint32_t, uint32_t))dlsym(g_vulkan, "port17_vulkan_render_size");
        if (told) told((uint32_t)rw, (uint32_t)rh);
    }
    LOGI("glfw: rendering at %dx%d for a window of %dx%d", engine_width_locked(), engine_height_locked(), g_width, g_height);
    Event r = { EV_RESIZE, engine_width_locked(), engine_height_locked(), g_width, g_height, 0, 0 };
    push_locked(&r);
    pthread_mutex_unlock(&g_lock);
}

// The external mice and keyboards, through the activity's inputDevices(): "<bits>|<names>", bit 1 a mouse, bit 2 a
// keyboard (mobile/InputMode.cs). Copied into out; returns its length.
API int glfwAndroidInputDevices(char* out, int size)
{
    return call_activity_string("inputDevices", out, size);
}

// Whether the soft keyboard is forced up for a text field (1, the default) or left to the input method (0: a hardware
// keyboard is plugged in), through the activity's softKeyboardForced(boolean).
API void glfwAndroidKeyboardForced(int forced)
{
    if (call_activity("softKeyboardForced", forced)) LOGI("glfw: soft keyboard %s", forced ? "forced up for text fields" : "left to the input method");
}

// The soft keyboard, through the activity's showKeyboard(boolean): the NDK's ANativeActivity_showSoftInput does nothing
// on this phone.
API void glfwAndroidShowKeyboard(int show)
{
    if (call_activity("showKeyboard", show)) LOGI("glfw: soft keyboard %s", show ? "shown" : "hidden");
}

// The system's file chooser for the mod import, through the activity's pickFiles(boolean folder): archives (several) or
// one folder. What was picked is copied to files/ModImport by the activity, which reports in files/ModImport.state.
API int glfwAndroidPickFiles(int folder)
{
    int ok = call_activity("pickFiles", folder);
    LOGI("glfw: file chooser for %s %s", folder ? "a folder" : "archives", ok ? "asked for" : "could not be opened");
    return ok;
}

// The game's restart, through the activity's restartGame(boolean): the activity's task is started afresh and this process
// is killed. Returns 0 when the activity could not be asked (the caller then goes on as on a PC).
API int glfwAndroidRestart(void)
{
    LOGI("glfw: restart of the game asked for");
    return call_activity("restartGame", 1);
}

// ---- Android's performance hints and frame rate (for mobile/Sched.cs) ----
// A hint session tells the system which threads make a frame, how long a frame may take and how long it took, for the
// frequency governor to act on. The functions are in libandroid.so from Android 13; found at run time.
static void* g_hintSession;
static int (*g_hintReport)(void*, int64_t);
static int (*g_hintUpdateTarget)(void*, int64_t);
static void (*g_hintClose)(void*);

// Opens (target > 0) or closes (target <= 0) a session for the two threads. Returns 1 when a session is open.
API int glfwAndroidPerfHint(int mainTid, int renderTid, int64_t targetNanos)
{
    if (g_hintSession)
    {
        if (targetNanos > 0 && g_hintUpdateTarget) { g_hintUpdateTarget(g_hintSession, targetNanos); return 1; }
        if (g_hintClose) g_hintClose(g_hintSession);
        g_hintSession = NULL;
        LOGI("glfw: performance hint session closed");
    }
    if (targetNanos <= 0) return 0;
    void* lib = dlopen("libandroid.so", RTLD_NOW);
    void* (*getManager)(void) = lib ? (void* (*)(void))dlsym(lib, "APerformanceHint_getManager") : NULL;
    void* (*createSession)(void*, const int32_t*, size_t, int64_t) = lib ? (void* (*)(void*, const int32_t*, size_t, int64_t))dlsym(lib, "APerformanceHint_createSession") : NULL;
    g_hintReport = lib ? (int (*)(void*, int64_t))dlsym(lib, "APerformanceHint_reportActualWorkDuration") : NULL;
    g_hintUpdateTarget = lib ? (int (*)(void*, int64_t))dlsym(lib, "APerformanceHint_updateTargetWorkDuration") : NULL;
    g_hintClose = lib ? (void (*)(void*))dlsym(lib, "APerformanceHint_closeSession") : NULL;
    void* manager = getManager ? getManager() : NULL;
    if (!manager || !createSession || !g_hintReport)
    {
        LOGI("glfw: no performance hint manager on this system");
        return 0;
    }
    int32_t tids[2] = { mainTid, renderTid };
    g_hintSession = createSession(manager, tids, renderTid > 0 ? 2 : 1, targetNanos);
    LOGI("glfw: performance hint session for threads %d and %d, target %lld ns: %s", mainTid, renderTid, (long long)targetNanos, g_hintSession ? "open" : "refused");
    return g_hintSession != NULL;
}

// The session as Android 16 makes it (APerformanceHint_createSessionUsingConfig): flags 1 marks it as the game's
// graphics pipeline, 2 ties it to the game's window, 4 lets the system time the frames itself from that window (then
// nothing need be reported). Returns 1 when a session is open; 0 also when this system has not the call.
API int glfwAndroidPerfHintConfig(int mainTid, int renderTid, int64_t targetNanos, int flags)
{
    glfwAndroidPerfHint(0, 0, 0);
    if (targetNanos <= 0) return 0;
    void* lib = dlopen("libandroid.so", RTLD_NOW);
    if (!lib) return 0;
    void* (*getManager)(void) = (void* (*)(void))dlsym(lib, "APerformanceHint_getManager");
    void* (*create)(void) = (void* (*)(void))dlsym(lib, "ASessionCreationConfig_create");
    void (*release)(void*) = (void (*)(void*))dlsym(lib, "ASessionCreationConfig_release");
    int (*setTids)(void*, const int32_t*, size_t) = (int (*)(void*, const int32_t*, size_t))dlsym(lib, "ASessionCreationConfig_setTids");
    int (*setTarget)(void*, int64_t) = (int (*)(void*, int64_t))dlsym(lib, "ASessionCreationConfig_setTargetWorkDurationNanos");
    int (*setEfficient)(void*, bool) = (int (*)(void*, bool))dlsym(lib, "ASessionCreationConfig_setPreferPowerEfficiency");
    int (*setGraphics)(void*, bool) = (int (*)(void*, bool))dlsym(lib, "ASessionCreationConfig_setGraphicsPipeline");
    int (*setSurfaces)(void*, ANativeWindow**, size_t, void**, size_t) = (int (*)(void*, ANativeWindow**, size_t, void**, size_t))dlsym(lib, "ASessionCreationConfig_setNativeSurfaces");
    int (*setAutoTiming)(void*, bool, bool) = (int (*)(void*, bool, bool))dlsym(lib, "ASessionCreationConfig_setUseAutoTiming");
    int (*createSession)(void*, void*, void**) = (int (*)(void*, void*, void**))dlsym(lib, "APerformanceHint_createSessionUsingConfig");
    g_hintReport = (int (*)(void*, int64_t))dlsym(lib, "APerformanceHint_reportActualWorkDuration");
    g_hintUpdateTarget = (int (*)(void*, int64_t))dlsym(lib, "APerformanceHint_updateTargetWorkDuration");
    g_hintClose = (void (*)(void*))dlsym(lib, "APerformanceHint_closeSession");
    void* manager = getManager ? getManager() : NULL;
    if (!manager || !create || !setTids || !setTarget || !createSession)
    {
        LOGI("glfw: no session config on this system (Android 16)");
        return 0;
    }
    void* config = create();
    int32_t tids[2] = { mainTid, renderTid };
    setTids(config, tids, renderTid > 0 ? 2 : 1);
    setTarget(config, targetNanos);
    if (setEfficient) setEfficient(config, false);
    int graphics = (flags & 1) && setGraphics ? setGraphics(config, true) : -1;
    ANativeWindow* window = g_window;
    int surfaces = (flags & 2) && window && setSurfaces ? setSurfaces(config, &window, 1, NULL, 0) : -1;
    int timing = (flags & 4) && setAutoTiming ? setAutoTiming(config, true, true) : -1;
    void* session = NULL;
    int r = createSession(manager, config, &session);
    if (release) release(config);
    g_hintSession = r == 0 ? session : NULL;
    LOGI("glfw: performance hint session (config, flags %d: graphics %d, surface %d, auto timing %d) for threads %d and %d, target %lld ns: %d %s",
         flags, graphics, surfaces, timing, mainTid, renderTid, (long long)targetNanos, r, g_hintSession ? "open" : "refused");
    return g_hintSession != NULL;
}

API void glfwAndroidPerfReport(int64_t actualNanos)
{
    if (g_hintSession && g_hintReport && actualNanos > 0) g_hintReport(g_hintSession, actualNanos);
}

// The frame rate the game means to draw at, told to the compositor (ANativeWindow_setFrameRate, Android 11): the
// display may then run at a multiple of it, and the phone's game service hears of it. 0 takes the vote back.
API int glfwAndroidSetFrameRate(float framesPerSecond)
{
    ANativeWindow* window = g_window;
    void* lib = dlopen("libandroid.so", RTLD_NOW);
    int32_t (*setFrameRate)(ANativeWindow*, float, int8_t) = lib ? (int32_t (*)(ANativeWindow*, float, int8_t))dlsym(lib, "ANativeWindow_setFrameRate") : NULL;
    if (!window || !setFrameRate) return 0;
    static float last = -1.0f;
    int32_t r = setFrameRate(window, framesPerSecond, 0);
    if (framesPerSecond != last) LOGI("glfw: frame rate %.0f told to the compositor: %d", framesPerSecond, (int)r);
    last = framesPerSecond;
    return r == 0;
}

// The display's refresh rate at the moment (Display.getRefreshRate through the activity), 0 when it cannot be asked.
API float glfwAndroidDisplayRate(void)
{
    if (!g_app || !g_app->activity || !g_app->activity->vm) return 0.0f;
    JavaVM* vm = g_app->activity->vm;
    JNIEnv* env = NULL;
    int attached = 0;
    if ((*vm)->GetEnv(vm, (void**)&env, JNI_VERSION_1_6) != JNI_OK)
    {
        if ((*vm)->AttachCurrentThread(vm, &env, NULL) != JNI_OK) return 0.0f;
        attached = 1;
    }
    float rate = 0.0f;
    jobject activity = g_app->activity->clazz;
    jclass cls = (*env)->GetObjectClass(env, activity);
    jmethodID getManager = (*env)->GetMethodID(env, cls, "getWindowManager", "()Landroid/view/WindowManager;");
    jobject manager = getManager ? (*env)->CallObjectMethod(env, activity, getManager) : NULL;
    if (manager)
    {
        jclass managerClass = (*env)->GetObjectClass(env, manager);
        jmethodID getDisplay = (*env)->GetMethodID(env, managerClass, "getDefaultDisplay", "()Landroid/view/Display;");
        jobject display = getDisplay ? (*env)->CallObjectMethod(env, manager, getDisplay) : NULL;
        if (display)
        {
            jclass displayClass = (*env)->GetObjectClass(env, display);
            jmethodID getRate = (*env)->GetMethodID(env, displayClass, "getRefreshRate", "()F");
            if (getRate) rate = (*env)->CallFloatMethod(env, display, getRate);
            (*env)->DeleteLocalRef(env, displayClass);
            (*env)->DeleteLocalRef(env, display);
        }
        (*env)->DeleteLocalRef(env, managerClass);
        (*env)->DeleteLocalRef(env, manager);
    }
    if ((*env)->ExceptionCheck(env)) { (*env)->ExceptionClear(env); rate = 0.0f; }
    (*env)->DeleteLocalRef(env, cls);
    if (attached) (*vm)->DetachCurrentThread(vm);
    return rate;
}

// The game is about to quit ("quit to desktop": Application.Quit, then the runtime's own exit). Left to itself the
// process ends in exit(), whose handlers and destructors pull things from under the threads Android's own interface
// code still runs: one of them aborts (SIGABRT in hwuiTask1) and the phone books a crash for an ordinary quit. So the
// activity is asked to leave the screen, and a handler registered now, which exit() therefore runs first, ends the
// process at once. What the runtime does on its way out (flushing its files) has happened by then.
static void exit_now(void)
{
    _exit(0);
}

API void glfwAndroidPrepareExit(void)
{
    static int prepared;
    if (prepared) return;
    prepared = 1;
    int asked = call_activity("finishGame", 1);
    atexit(exit_now);
    LOGI("glfw: the game is quitting (activity %s)", asked ? "asked to finish" : "could not be asked");
}

// ---- text from the soft keyboard (TextInputView, JNI) ----
JNIEXPORT void JNICALL Java_com_local_waylith_port_TextInputView_nativeCommit(JNIEnv* env, jclass cls, jstring text)
{
    (void)cls;
    if (!text) return;
    const jchar* chars = (*env)->GetStringChars(env, text, NULL);
    jsize n = (*env)->GetStringLength(env, text);
    for (jsize i = 0; i < n; i++)
    {
        unsigned int cp = chars[i];
        if (cp >= 0xD800 && cp <= 0xDBFF && i + 1 < n && chars[i + 1] >= 0xDC00 && chars[i + 1] <= 0xDFFF)
        {
            cp = 0x10000 + ((cp - 0xD800) << 10) + (chars[i + 1] - 0xDC00);
            i++;
        }
        push(EV_CHAR, (int)cp, 0, 0, 0, 0, 0);
    }
    (*env)->ReleaseStringChars(env, text, chars);
    LOGI("glfw: ime committed %d chars", (int)n);
}

JNIEXPORT void JNICALL Java_com_local_waylith_port_TextInputView_nativeKey(JNIEnv* env, jclass cls, jint keyCode, jint action)
{
    (void)env; (void)cls;
    int glfwKey = key_from_android(keyCode);
    if (glfwKey == GLFW_KEY_UNKNOWN) return;
    push(EV_KEY, glfwKey, keyCode, action == 0 ? GLFW_PRESS : GLFW_RELEASE, 0, 0, 0);
}

// The soft keyboard's action key (Done, or the Enter some keyboards send instead): with the managed touch layer on, it
// decides what it means (typing is finished); without it, it is the Enter key as before.
JNIEXPORT void JNICALL Java_com_local_waylith_port_TextInputView_nativeEditorAction(JNIEnv* env, jclass cls)
{
    (void)env; (void)cls;
    if (g_touchManaged)
    {
        record_touch(5, -1, 0.0f, 0.0f);
        return;
    }
    push(EV_KEY, key_from_android(AKEYCODE_ENTER), AKEYCODE_ENTER, GLFW_PRESS, 0, 0, 0);
    push(EV_KEY, key_from_android(AKEYCODE_ENTER), AKEYCODE_ENTER, GLFW_RELEASE, 0, 0, 0);
}

API void glfwAndroidAttach(struct android_app* app)
{
    g_app = app;
    app->onAppCmd = on_app_cmd;
    app->onInputEvent = on_input_event;
    if (app->window)
    {
        // Attached after the window already came (should not happen, the host attaches first; kept for safety).
        on_app_cmd(app, APP_CMD_INIT_WINDOW);
    }
    LOGI("glfw: attached to the activity");
}

// ---- engine-thread side: dispatching ----
static void dispatch(const Event* e)
{
    GLFWwindow* w = &g_windowObject;
    switch (e->type)
    {
    case EV_WINDOW_INIT:
    case EV_RESIZE:
        if (e->a != g_reportedWidth || e->b != g_reportedHeight || !g_sizeAnnounced)
        {
            g_reportedWidth = e->a;
            g_reportedHeight = e->b;
            g_vidmode.width = e->c > 0 ? e->c : e->a;
            g_vidmode.height = e->d > 0 ? e->d : e->b;
            if (g_windowCreated)
            {
                // Like GLFW when a window is shown: the size and the framebuffer size are announced. The managed
                // window only learns its framebuffer size from this callback, and treats 0x0 as minimized.
                g_sizeAnnounced = 1;
                if (cb_size) cb_size(w, e->a, e->b);
                if (cb_fbsize) cb_fbsize(w, e->a, e->b);
            }
        }
        if (g_windowCreated && cb_refresh && e->type == EV_WINDOW_INIT) cb_refresh(w);
        break;
    case EV_WINDOW_TERM:
        // No window: the framebuffer is 0x0, which the engine treats as minimized and stops rendering; the size is
        // announced again with the next window.
        g_sizeAnnounced = 0;
        if (g_windowCreated)
        {
            if (cb_size) cb_size(w, 0, 0);
            if (cb_fbsize) cb_fbsize(w, 0, 0);
        }
        break;
    case EV_FOCUS:
        if (g_reportedFocused != e->a)
        {
            g_reportedFocused = e->a;
            if (!e->a)
            {
                // Like GLFW: releasing everything when focus goes away.
                for (int k = 0; k < 512; k++)
                {
                    if (g_keys[k]) { g_keys[k] = 0; if (cb_key) cb_key(w, k, 0, GLFW_RELEASE, 0); }
                }
                for (int b = 0; b < 8; b++)
                {
                    if (g_mouseButtons[b]) { g_mouseButtons[b] = 0; if (cb_button) cb_button(w, b, GLFW_RELEASE, 0); }
                }
            }
            if (g_windowCreated && cb_focus) cb_focus(w, e->a);
        }
        break;
    case EV_CURSOR:
        g_cursorX = e->x;
        g_cursorY = e->y;
        if (cb_cursor) cb_cursor(w, e->x, e->y);
        break;
    case EV_BUTTON:
        if (e->a >= 0 && e->a < 8) g_mouseButtons[e->a] = e->b == GLFW_PRESS;
        if (cb_button) cb_button(w, e->a, e->b, e->c);
        break;
    case EV_SCROLL:
        if (cb_scroll) cb_scroll(w, e->x, e->y);
        break;
    case EV_KEY:
        if (e->a >= 0 && e->a < 512) g_keys[e->a] = e->c != GLFW_RELEASE;
        if (cb_key) cb_key(w, e->a, e->b, e->c, e->d);
        break;
    case EV_CHAR:
        if (cb_char) cb_char(w, (unsigned int)e->a);
        if (cb_charmods) cb_charmods(w, (unsigned int)e->a, e->b);
        break;
    case EV_CLOSE:
        g_shouldClose = 1;
        if (cb_close) cb_close(w);
        break;
    }
}

static void drain(void)
{
    for (;;)
    {
        Event e;
        pthread_mutex_lock(&g_lock);
        if (g_qhead == g_qtail)
        {
            pthread_mutex_unlock(&g_lock);
            return;
        }
        e = g_queue[g_qhead];
        g_qhead = (g_qhead + 1) % QUEUE_CAP;
        pthread_mutex_unlock(&g_lock);
        dispatch(&e);
    }
}

static void timespec_after(struct timespec* ts, double seconds)
{
    clock_gettime(CLOCK_REALTIME, ts);
    double whole = (double)(long)seconds;
    ts->tv_sec += (long)whole;
    ts->tv_nsec += (long)((seconds - whole) * 1e9);
    if (ts->tv_nsec >= 1000000000L) { ts->tv_sec++; ts->tv_nsec -= 1000000000L; }
}

// Waits (with g_lock held by the caller) until the activity has a window, up to the timeout. Returns 1 if it has.
static int wait_for_window_locked(double seconds)
{
    struct timespec until;
    timespec_after(&until, seconds);
    while (g_window == NULL && !g_destroyed)
    {
        if (pthread_cond_timedwait(&g_cond, &g_lock, &until) == ETIMEDOUT) break;
    }
    return g_window != NULL;
}

// ---- GLFW API ----
API int glfwInit(void)
{
    if (!g_initialized)
    {
        clock_gettime(CLOCK_MONOTONIC, &g_start);
        g_initialized = 1;
        pthread_mutex_lock(&g_lock);
        g_reportedWidth = engine_width_locked();
        g_reportedHeight = engine_height_locked();
        g_reportedFocused = g_focused;
        if (g_width > 0) { g_vidmode.width = g_width; g_vidmode.height = g_height; }
        pthread_mutex_unlock(&g_lock);
        LOGI("glfwInit (Android shim)");
    }
    return GLFW_TRUE;
}

API void glfwTerminate(void) { g_initialized = 0; g_windowCreated = 0; }
API void glfwInitHint(int hint, int value) { (void)hint; (void)value; }
API void glfwInitAllocator(const GLFWallocator* allocator) { (void)allocator; }
API void glfwInitVulkanLoader(void* loader) { (void)loader; }
API void glfwGetVersion(int* major, int* minor, int* rev) { if (major) *major = 3; if (minor) *minor = 4; if (rev) *rev = 0; }
API const char* glfwGetVersionString(void) { return "3.4.0 Android (port17 shim)"; }

API int glfwGetError(const char** description)
{
    int code = g_lastError;
    if (description) *description = code ? g_lastErrorText : NULL;
    g_lastError = 0;
    return code;
}

API GLFWerrorfun glfwSetErrorCallback(GLFWerrorfun f) { GLFWerrorfun old = cb_error; cb_error = f; return old; }
API int glfwGetPlatform(void) { return GLFW_PLATFORM_NULL; }
API int glfwPlatformSupported(int platform) { return platform == GLFW_PLATFORM_NULL; }

// Monitors
API GLFWmonitor** glfwGetMonitors(int* count) { if (count) *count = 1; return g_monitors; }
API GLFWmonitor* glfwGetPrimaryMonitor(void) { return &g_monitorObject; }
API void glfwGetMonitorPos(GLFWmonitor* m, int* x, int* y) { (void)m; if (x) *x = 0; if (y) *y = 0; }
API void glfwGetMonitorWorkarea(GLFWmonitor* m, int* x, int* y, int* w, int* h) { (void)m; if (x) *x = 0; if (y) *y = 0; if (w) *w = g_vidmode.width; if (h) *h = g_vidmode.height; }
API void glfwGetMonitorPhysicalSize(GLFWmonitor* m, int* w, int* h)
{
    (void)m;
    float dpi = g_density * 160.0f;
    if (w) *w = (int)(g_vidmode.width / dpi * 25.4f);
    if (h) *h = (int)(g_vidmode.height / dpi * 25.4f);
}
API void glfwGetMonitorContentScale(GLFWmonitor* m, float* x, float* y) { (void)m; if (x) *x = 1.0f; if (y) *y = 1.0f; }
API const char* glfwGetMonitorName(GLFWmonitor* m) { (void)m; return "Android display"; }
API void glfwSetMonitorUserPointer(GLFWmonitor* m, void* p) { (void)m; g_monitorUser = p; }
API void* glfwGetMonitorUserPointer(GLFWmonitor* m) { (void)m; return g_monitorUser; }
API GLFWmonitorfun glfwSetMonitorCallback(GLFWmonitorfun f) { GLFWmonitorfun old = cb_monitor; cb_monitor = f; return old; }
API const GLFWvidmode* glfwGetVideoModes(GLFWmonitor* m, int* count) { (void)m; if (count) *count = 1; return &g_vidmode; }
API const GLFWvidmode* glfwGetVideoMode(GLFWmonitor* m) { (void)m; return &g_vidmode; }
API void glfwSetGamma(GLFWmonitor* m, float gamma) { (void)m; (void)gamma; }
API const GLFWgammaramp* glfwGetGammaRamp(GLFWmonitor* m) { (void)m; set_error(GLFW_FEATURE_UNAVAILABLE, "gamma ramps are not available"); return NULL; }
API void glfwSetGammaRamp(GLFWmonitor* m, const GLFWgammaramp* ramp) { (void)m; (void)ramp; }

// Window
API void glfwDefaultWindowHints(void) {}
API void glfwWindowHint(int hint, int value) { (void)hint; (void)value; }
API void glfwWindowHintString(int hint, const char* value) { (void)hint; (void)value; }

API GLFWwindow* glfwCreateWindow(int width, int height, const char* title, GLFWmonitor* monitor, GLFWwindow* share)
{
    (void)monitor; (void)share;
    if (!g_initialized) { set_error(GLFW_NOT_INITIALIZED, "GLFW is not initialized"); return NULL; }
    if (g_windowCreated) { set_error(GLFW_PLATFORM_ERROR, "only one window is available on Android"); return NULL; }
    pthread_mutex_lock(&g_lock);
    int have = wait_for_window_locked(20.0);
    if (have)
    {
        g_reportedWidth = engine_width_locked();
        g_reportedHeight = engine_height_locked();
        g_vidmode.width = g_width;
        g_vidmode.height = g_height;
        g_reportedFocused = g_focused;
    }
    pthread_mutex_unlock(&g_lock);
    if (!have)
    {
        set_error(GLFW_PLATFORM_ERROR, "the activity has no window");
        return NULL;
    }
    if (title) snprintf(g_title, sizeof g_title, "%s", title);
    g_windowCreated = 1;
    g_shouldClose = 0;
    g_sizeAnnounced = 0;
    push(EV_RESIZE, g_reportedWidth, g_reportedHeight, 0, 0, 0, 0);
    LOGI("glfwCreateWindow: using the activity window %dx%d (asked for %dx%d)", g_reportedWidth, g_reportedHeight, width, height);
    return &g_windowObject;
}

API void glfwDestroyWindow(GLFWwindow* w) { (void)w; g_windowCreated = 0; }
API int glfwWindowShouldClose(GLFWwindow* w) { (void)w; return g_shouldClose; }
API void glfwSetWindowShouldClose(GLFWwindow* w, int value) { (void)w; g_shouldClose = value; }
API const char* glfwGetWindowTitle(GLFWwindow* w) { (void)w; return g_title; }
API void glfwSetWindowTitle(GLFWwindow* w, const char* title) { (void)w; if (title) snprintf(g_title, sizeof g_title, "%s", title); }
API void glfwSetWindowIcon(GLFWwindow* w, int count, const GLFWimage* images) { (void)w; (void)count; (void)images; }
API void glfwGetWindowPos(GLFWwindow* w, int* x, int* y) { (void)w; if (x) *x = 0; if (y) *y = 0; }
API void glfwSetWindowPos(GLFWwindow* w, int x, int y) { (void)w; (void)x; (void)y; }
API void glfwGetWindowSize(GLFWwindow* w, int* width, int* height) { (void)w; if (width) *width = g_reportedWidth; if (height) *height = g_reportedHeight; }
API void glfwSetWindowSizeLimits(GLFWwindow* w, int a, int b, int c, int d) { (void)w; (void)a; (void)b; (void)c; (void)d; }
API void glfwSetWindowAspectRatio(GLFWwindow* w, int n, int d) { (void)w; (void)n; (void)d; }
API void glfwSetWindowSize(GLFWwindow* w, int width, int height) { (void)w; request_render_size(width, height); }
API void glfwGetFramebufferSize(GLFWwindow* w, int* width, int* height) { (void)w; if (width) *width = g_reportedWidth; if (height) *height = g_reportedHeight; }
API void glfwGetWindowFrameSize(GLFWwindow* w, int* l, int* t, int* r, int* b) { (void)w; if (l) *l = 0; if (t) *t = 0; if (r) *r = 0; if (b) *b = 0; }
API void glfwGetWindowContentScale(GLFWwindow* w, float* x, float* y) { (void)w; if (x) *x = 1.0f; if (y) *y = 1.0f; }
API float glfwGetWindowOpacity(GLFWwindow* w) { (void)w; return 1.0f; }
API void glfwSetWindowOpacity(GLFWwindow* w, float o) { (void)w; (void)o; }
API void glfwIconifyWindow(GLFWwindow* w) { (void)w; }
API void glfwRestoreWindow(GLFWwindow* w) { (void)w; }
API void glfwMaximizeWindow(GLFWwindow* w) { (void)w; }
API void glfwShowWindow(GLFWwindow* w) { (void)w; }
API void glfwHideWindow(GLFWwindow* w) { (void)w; }
API void glfwFocusWindow(GLFWwindow* w) { (void)w; }
API void glfwRequestWindowAttention(GLFWwindow* w) { (void)w; }
API GLFWmonitor* glfwGetWindowMonitor(GLFWwindow* w) { (void)w; return &g_monitorObject; }
API void glfwSetWindowMonitor(GLFWwindow* w, GLFWmonitor* m, int x, int y, int width, int height, int rate) { (void)w; (void)m; (void)x; (void)y; (void)rate; request_render_size(width, height); }

API int glfwGetWindowAttrib(GLFWwindow* w, int attrib)
{
    (void)w;
    switch (attrib)
    {
    case GLFW_FOCUSED: return g_reportedFocused;
    case GLFW_VISIBLE: return 1;
    case GLFW_HOVERED: return 1;
    case GLFW_MAXIMIZED: return 1;
    case GLFW_ICONIFIED: case GLFW_RESIZABLE: case GLFW_DECORATED: case GLFW_FLOATING: case GLFW_AUTO_ICONIFY:
    case GLFW_TRANSPARENT_FRAMEBUFFER: case GLFW_FOCUS_ON_SHOW: case GLFW_MOUSE_PASSTHROUGH: return 0;
    case GLFW_CLIENT_API: return GLFW_NO_API;
    default: return 0;
    }
}
API void glfwSetWindowAttrib(GLFWwindow* w, int attrib, int value) { (void)w; (void)attrib; (void)value; }
API void glfwSetWindowUserPointer(GLFWwindow* w, void* p) { (void)w; g_windowUser = p; }
API void* glfwGetWindowUserPointer(GLFWwindow* w) { (void)w; return g_windowUser; }

#define SETCB(name, type, var) API type name(GLFWwindow* w, type f) { (void)w; type old = var; var = f; return old; }
SETCB(glfwSetWindowPosCallback, GLFWwindowposfun, cb_pos)
SETCB(glfwSetWindowSizeCallback, GLFWwindowsizefun, cb_size)
SETCB(glfwSetWindowCloseCallback, GLFWwindowclosefun, cb_close)
SETCB(glfwSetWindowRefreshCallback, GLFWwindowrefreshfun, cb_refresh)
SETCB(glfwSetWindowFocusCallback, GLFWwindowfocusfun, cb_focus)
SETCB(glfwSetWindowIconifyCallback, GLFWwindowiconifyfun, cb_iconify)
SETCB(glfwSetWindowMaximizeCallback, GLFWwindowmaximizefun, cb_maximize)
SETCB(glfwSetFramebufferSizeCallback, GLFWframebuffersizefun, cb_fbsize)
SETCB(glfwSetWindowContentScaleCallback, GLFWwindowcontentscalefun, cb_scale)
SETCB(glfwSetMouseButtonCallback, GLFWmousebuttonfun, cb_button)
SETCB(glfwSetCursorPosCallback, GLFWcursorposfun, cb_cursor)
SETCB(glfwSetCursorEnterCallback, GLFWcursorenterfun, cb_enter)
SETCB(glfwSetScrollCallback, GLFWscrollfun, cb_scroll)
SETCB(glfwSetKeyCallback, GLFWkeyfun, cb_key)
SETCB(glfwSetCharCallback, GLFWcharfun, cb_char)
SETCB(glfwSetCharModsCallback, GLFWcharmodsfun, cb_charmods)
SETCB(glfwSetDropCallback, GLFWdropfun, cb_drop)

// Events
API void glfwPollEvents(void) { drain(); }
API void glfwWaitEvents(void)
{
    pthread_mutex_lock(&g_lock);
    while (g_qhead == g_qtail) pthread_cond_wait(&g_cond, &g_lock);
    pthread_mutex_unlock(&g_lock);
    drain();
}
API void glfwWaitEventsTimeout(double timeout)
{
    struct timespec until;
    timespec_after(&until, timeout);
    pthread_mutex_lock(&g_lock);
    while (g_qhead == g_qtail)
    {
        if (pthread_cond_timedwait(&g_cond, &g_lock, &until) == ETIMEDOUT) break;
    }
    pthread_mutex_unlock(&g_lock);
    drain();
}
API void glfwPostEmptyEvent(void)
{
    pthread_mutex_lock(&g_lock);
    pthread_cond_broadcast(&g_cond);
    pthread_mutex_unlock(&g_lock);
}

// Input
API int glfwGetInputMode(GLFWwindow* w, int mode)
{
    (void)w;
    switch (mode)
    {
    case GLFW_CURSOR: return g_cursorMode;
    case GLFW_STICKY_KEYS: return g_stickyKeys;
    case GLFW_STICKY_MOUSE_BUTTONS: return g_stickyButtons;
    case GLFW_LOCK_KEY_MODS: return g_lockKeyMods;
    case GLFW_RAW_MOUSE_MOTION: return g_rawMotion;
    default: set_error(GLFW_INVALID_ENUM, "invalid input mode"); return 0;
    }
}
API void glfwSetInputMode(GLFWwindow* w, int mode, int value)
{
    (void)w;
    switch (mode)
    {
    case GLFW_CURSOR: g_cursorMode = value; break;
    case GLFW_STICKY_KEYS: g_stickyKeys = value; break;
    case GLFW_STICKY_MOUSE_BUTTONS: g_stickyButtons = value; break;
    case GLFW_LOCK_KEY_MODS: g_lockKeyMods = value; break;
    case GLFW_RAW_MOUSE_MOTION: g_rawMotion = value; break;
    default: set_error(GLFW_INVALID_ENUM, "invalid input mode"); break;
    }
}
API int glfwRawMouseMotionSupported(void) { return GLFW_FALSE; }
API const char* glfwGetKeyName(int key, int scancode) { (void)key; (void)scancode; return NULL; }
API int glfwGetKeyScancode(int key) { return key; }
API int glfwGetKey(GLFWwindow* w, int key) { (void)w; return (key >= 0 && key < 512 && g_keys[key]) ? GLFW_PRESS : GLFW_RELEASE; }
API int glfwGetMouseButton(GLFWwindow* w, int button) { (void)w; return (button >= 0 && button < 8 && g_mouseButtons[button]) ? GLFW_PRESS : GLFW_RELEASE; }
API void glfwGetCursorPos(GLFWwindow* w, double* x, double* y) { (void)w; if (x) *x = g_cursorX; if (y) *y = g_cursorY; }
API void glfwSetCursorPos(GLFWwindow* w, double x, double y) { (void)w; g_cursorX = x; g_cursorY = y; }

API GLFWcursor* glfwCreateCursor(const GLFWimage* image, int xhot, int yhot)
{
    (void)image; (void)xhot; (void)yhot;
    if (g_cursorCount >= 64) g_cursorCount = 0;
    g_cursors[g_cursorCount].shape = 0;
    return &g_cursors[g_cursorCount++];
}
API GLFWcursor* glfwCreateStandardCursor(int shape)
{
    if (g_cursorCount >= 64) g_cursorCount = 0;
    g_cursors[g_cursorCount].shape = shape;
    return &g_cursors[g_cursorCount++];
}
API void glfwDestroyCursor(GLFWcursor* c) { (void)c; }
API void glfwSetCursor(GLFWwindow* w, GLFWcursor* c) { (void)w; (void)c; }

// Joysticks: none.
API int glfwJoystickPresent(int jid) { (void)jid; return GLFW_FALSE; }
API const float* glfwGetJoystickAxes(int jid, int* count) { (void)jid; if (count) *count = 0; return NULL; }
API const unsigned char* glfwGetJoystickButtons(int jid, int* count) { (void)jid; if (count) *count = 0; return NULL; }
API const unsigned char* glfwGetJoystickHats(int jid, int* count) { (void)jid; if (count) *count = 0; return NULL; }
API const char* glfwGetJoystickName(int jid) { (void)jid; return NULL; }
API const char* glfwGetJoystickGUID(int jid) { (void)jid; return NULL; }
API void glfwSetJoystickUserPointer(int jid, void* p) { (void)jid; (void)p; }
API void* glfwGetJoystickUserPointer(int jid) { (void)jid; return NULL; }
API int glfwJoystickIsGamepad(int jid) { (void)jid; return GLFW_FALSE; }
API GLFWjoystickfun glfwSetJoystickCallback(GLFWjoystickfun f) { GLFWjoystickfun old = cb_joystick; cb_joystick = f; return old; }
API int glfwUpdateGamepadMappings(const char* s) { (void)s; return GLFW_TRUE; }
API const char* glfwGetGamepadName(int jid) { (void)jid; return NULL; }
API int glfwGetGamepadState(int jid, GLFWgamepadstate* state) { (void)jid; (void)state; return GLFW_FALSE; }

// Calls a method of the activity with one byte[] argument (in: UTF-8 text, or none when NULL) from whatever thread this
// is. sig is its JNI signature; it returns void ("V"), boolean ("Z", into *flag) or byte[] ("[B": a malloc'd
// NUL-terminated copy, or NULL for a null array). Returns that copy (byte[] methods) or NULL.
static char* activity_bytes(const char* name, const char* sig, const char* in, int* flag)
{
    if (flag) *flag = 0;
    if (!g_app || !g_app->activity || !g_app->activity->vm) return NULL;
    JavaVM* vm = g_app->activity->vm;
    JNIEnv* env = NULL;
    int attached = 0;
    if ((*vm)->GetEnv(vm, (void**)&env, JNI_VERSION_1_6) != JNI_OK)
    {
        if ((*vm)->AttachCurrentThread(vm, &env, NULL) != JNI_OK) return NULL;
        attached = 1;
    }
    char* out = NULL;
    jobject activity = g_app->activity->clazz;
    jclass cls = (*env)->GetObjectClass(env, activity);
    jmethodID method = (*env)->GetMethodID(env, cls, name, sig);
    jbyteArray arg = NULL;
    if (in)
    {
        jsize n = (jsize)strlen(in);
        arg = (*env)->NewByteArray(env, n);
        if (arg) (*env)->SetByteArrayRegion(env, arg, 0, n, (const jbyte*)in);
    }
    char kind = sig[strlen(sig) - 1];
    if (!method) LOGW("glfw: no %s on the activity", name);
    else if (kind == 'V') (*env)->CallVoidMethod(env, activity, method, arg);
    else if (kind == 'Z') { jboolean b = (*env)->CallBooleanMethod(env, activity, method, arg); if (flag) *flag = b ? 1 : 0; }
    else
    {
        jbyteArray result = (jbyteArray)(in ? (*env)->CallObjectMethod(env, activity, method, arg) : (*env)->CallObjectMethod(env, activity, method));
        if (result && !(*env)->ExceptionCheck(env))
        {
            jsize n = (*env)->GetArrayLength(env, result);
            out = (char*)malloc((size_t)n + 1);
            if (out)
            {
                (*env)->GetByteArrayRegion(env, result, 0, n, (jbyte*)out);
                out[n] = 0;
            }
        }
        if (result) (*env)->DeleteLocalRef(env, result);
    }
    if ((*env)->ExceptionCheck(env)) { (*env)->ExceptionDescribe(env); (*env)->ExceptionClear(env); LOGW("glfw: %s threw", name); free(out); out = NULL; if (flag) *flag = 0; }
    if (arg) (*env)->DeleteLocalRef(env, arg);
    (*env)->DeleteLocalRef(env, cls);
    if (attached) (*vm)->DetachCurrentThread(vm);
    return out;
}

// Clipboard: the phone's (MainActivity.setClipboardUtf8 / clipboardUtf8IfChanged; until 2026-10-27 only this process's,
// where the PC's is the system's). The last text is kept here for the pointer glfwGetClipboardString returns, and stands
// when the phone's clipboard has not changed since it was read.
API void glfwSetClipboardString(GLFWwindow* w, const char* s)
{
    (void)w;
    free(g_clipboard);
    g_clipboard = s ? strdup(s) : NULL;
    activity_bytes("setClipboardUtf8", "([B)V", s ? s : "", NULL);
}
API const char* glfwGetClipboardString(GLFWwindow* w)
{
    (void)w;
    char* fresh = activity_bytes("clipboardUtf8IfChanged", "()[B", NULL, NULL);
    if (fresh)
    {
        free(g_clipboard);
        g_clipboard = fresh;
    }
    return g_clipboard ? g_clipboard : "";
}

// A link opened in the phone's app for it (MainActivity.openUrlUtf8), for mobile/OpenUrl.cs: 1 when an app was asked.
API int glfwAndroidOpenUrl(const char* url)
{
    int ok = 0;
    if (url && *url) activity_bytes("openUrlUtf8", "([B)Z", url, &ok);
    return ok;
}

// A picture the gallery should list (MainActivity.scanMediaUtf8), for mobile/Screenshots.cs: a file an app creates in
// Pictures by path is not in the phone's media store until it is scanned. 1 when the scan was asked.
API int glfwAndroidScanMedia(const char* path)
{
    int ok = 0;
    if (path && *path) activity_bytes("scanMediaUtf8", "([B)Z", path, &ok);
    return ok;
}

// Time
API double glfwGetTime(void)
{
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (double)(now.tv_sec - g_start.tv_sec) + (double)(now.tv_nsec - g_start.tv_nsec) / 1e9;
}
API void glfwSetTime(double t) { (void)t; }
API uint64_t glfwGetTimerValue(void)
{
    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    return (uint64_t)now.tv_sec * 1000000000ULL + (uint64_t)now.tv_nsec;
}
API uint64_t glfwGetTimerFrequency(void) { return 1000000000ULL; }

// OpenGL: not available.
API void glfwMakeContextCurrent(GLFWwindow* w) { (void)w; }
API GLFWwindow* glfwGetCurrentContext(void) { return NULL; }
API void glfwSwapBuffers(GLFWwindow* w) { (void)w; }
API void glfwSwapInterval(int i) { (void)i; }
API int glfwExtensionSupported(const char* e) { (void)e; return GLFW_FALSE; }
API GLFWglproc glfwGetProcAddress(const char* name) { (void)name; return NULL; }

// Vulkan
static int load_vulkan(void)
{
    if (g_vulkan) return 1;
    // Our own libvulkan.so (the interposer next to this library), by full path: by name the linker would hand back
    // the system's, which the process already has loaded.
    Dl_info self;
    char path[512];
    if (dladdr((void*)&load_vulkan, &self) && self.dli_fname)
    {
        snprintf(path, sizeof path, "%s", self.dli_fname);
        char* slash = strrchr(path, '/');
        if (slash) { snprintf(slash + 1, sizeof path - (slash + 1 - path), "libvulkan.so"); g_vulkan = dlopen(path, RTLD_NOW | RTLD_LOCAL); }
    }
    if (!g_vulkan) g_vulkan = dlopen("libvulkan.so", RTLD_NOW | RTLD_LOCAL);
    if (g_vulkan && dladdr(dlsym(g_vulkan, "vkGetInstanceProcAddr"), &self)) LOGI("glfw: Vulkan from %s", self.dli_fname);
    if (!g_vulkan)
    {
        LOGE("glfw: libvulkan.so: %s", dlerror());
        return 0;
    }
    g_vkGetInstanceProcAddr = (PFN_vkGetInstanceProcAddr)dlsym(g_vulkan, "vkGetInstanceProcAddr");
    return g_vkGetInstanceProcAddr != NULL;
}

API int glfwVulkanSupported(void) { return load_vulkan() ? GLFW_TRUE : GLFW_FALSE; }

API const char** glfwGetRequiredInstanceExtensions(uint32_t* count)
{
    static const char* extensions[] = { "VK_KHR_surface", "VK_KHR_android_surface" };
    if (!load_vulkan())
    {
        set_error(GLFW_API_UNAVAILABLE, "Vulkan is not available");
        if (count) *count = 0;
        return NULL;
    }
    if (count) *count = 2;
    return extensions;
}

API GLFWvkproc glfwGetInstanceProcAddress(VkInstance instance, const char* name)
{
    if (!load_vulkan()) return NULL;
    return (GLFWvkproc)g_vkGetInstanceProcAddr(instance, name);
}

API int glfwGetPhysicalDevicePresentationSupport(VkInstance instance, VkPhysicalDevice device, uint32_t family)
{
    (void)instance; (void)device; (void)family;
    return GLFW_TRUE;
}

API VkResult glfwCreateWindowSurface(VkInstance instance, GLFWwindow* w, const VkAllocationCallbacks* allocator, VkSurfaceKHR* surface)
{
    (void)w;
    if (surface) *surface = VK_NULL_HANDLE;
    if (!load_vulkan())
    {
        set_error(GLFW_API_UNAVAILABLE, "Vulkan is not available");
        return VK_ERROR_INITIALIZATION_FAILED;
    }
    PFN_vkCreateAndroidSurfaceKHR create = (PFN_vkCreateAndroidSurfaceKHR)g_vkGetInstanceProcAddr(instance, "vkCreateAndroidSurfaceKHR");
    if (!create)
    {
        set_error(GLFW_API_UNAVAILABLE, "vkCreateAndroidSurfaceKHR is missing (VK_KHR_android_surface not enabled)");
        return VK_ERROR_EXTENSION_NOT_PRESENT;
    }
    pthread_mutex_lock(&g_lock);
    int have = wait_for_window_locked(10.0);
    ANativeWindow* window = g_window;
    pthread_mutex_unlock(&g_lock);
    if (!have)
    {
        set_error(GLFW_PLATFORM_ERROR, "the activity has no window to create a surface on");
        return VK_ERROR_SURFACE_LOST_KHR;
    }
    VkAndroidSurfaceCreateInfoKHR info;
    memset(&info, 0, sizeof info);
    info.sType = VK_STRUCTURE_TYPE_ANDROID_SURFACE_CREATE_INFO_KHR;
    info.window = window;
    VkResult result = create(instance, &info, allocator, surface);
    if (result == VK_ERROR_NATIVE_WINDOW_IN_USE_KHR && g_lastSurface && g_lastSurfaceWindow == window && g_vulkan)
    {
        // The engine's previous surface still holds this same window (it destroys the old one only after making the
        // new one): the Vulkan interposer releases it now, and skips the engine's destroy of it later.
        void (*release)(VkInstance, VkSurfaceKHR, const VkAllocationCallbacks*) =
            (void (*)(VkInstance, VkSurfaceKHR, const VkAllocationCallbacks*))dlsym(g_vulkan, "port17_vulkan_release_surface");
        if (release)
        {
            LOGI("glfw: window %p is still held by surface %p; releasing that one and trying again", (void*)window, (void*)g_lastSurface);
            release(instance, g_lastSurface, allocator);
            g_lastSurface = VK_NULL_HANDLE;
            result = create(instance, &info, allocator, surface);
        }
    }
    if (result != VK_SUCCESS)
    {
        char text[128];
        snprintf(text, sizeof text, "vkCreateAndroidSurfaceKHR failed: %d", (int)result);
        set_error(GLFW_PLATFORM_ERROR, text);
    }
    else
    {
        LOGI("glfw: Vulkan surface created on window %p", (void*)window);
        g_surfaceCreated = 1;
        g_lastSurface = *surface;
        g_lastSurfaceWindow = window;
    }
    return result;
}

// Native access: nothing to give.
API void* glfwGetWin32Adapter(GLFWmonitor* m) { (void)m; return NULL; }
API void* glfwGetWin32Monitor(GLFWmonitor* m) { (void)m; return NULL; }
API void* glfwGetWin32Window(GLFWwindow* w) { (void)w; return NULL; }
API void* glfwGetWGLContext(GLFWwindow* w) { (void)w; return NULL; }
API void* glfwGetCocoaMonitor(GLFWmonitor* m) { (void)m; return NULL; }
API void* glfwGetCocoaWindow(GLFWwindow* w) { (void)w; return NULL; }
API void* glfwGetCocoaView(GLFWwindow* w) { (void)w; return NULL; }
API void* glfwGetNSGLContext(GLFWwindow* w) { (void)w; return NULL; }
API void* glfwGetX11Display(void) { return NULL; }
API unsigned long glfwGetX11Adapter(GLFWmonitor* m) { (void)m; return 0; }
API unsigned long glfwGetX11Monitor(GLFWmonitor* m) { (void)m; return 0; }
API unsigned long glfwGetX11Window(GLFWwindow* w) { (void)w; return 0; }
API void glfwSetX11SelectionString(const char* s) { (void)s; }
API const char* glfwGetX11SelectionString(void) { return NULL; }
API void* glfwGetGLXContext(GLFWwindow* w) { (void)w; return NULL; }
API unsigned long glfwGetGLXWindow(GLFWwindow* w) { (void)w; return 0; }
API void* glfwGetWaylandDisplay(void) { return NULL; }
API void* glfwGetWaylandMonitor(GLFWmonitor* m) { (void)m; return NULL; }
API void* glfwGetWaylandWindow(GLFWwindow* w) { (void)w; return NULL; }
API void* glfwGetEGLDisplay(void) { return NULL; }
API void* glfwGetEGLContext(GLFWwindow* w) { (void)w; return NULL; }
API void* glfwGetEGLSurface(GLFWwindow* w) { (void)w; return NULL; }
API int glfwGetOSMesaColorBuffer(GLFWwindow* w, int* a, int* b, int* c, void** d) { (void)w; (void)a; (void)b; (void)c; (void)d; return GLFW_FALSE; }
API int glfwGetOSMesaDepthBuffer(GLFWwindow* w, int* a, int* b, int* c, void** d) { (void)w; (void)a; (void)b; (void)c; (void)d; return GLFW_FALSE; }
API void* glfwGetOSMesaContext(GLFWwindow* w) { (void)w; return NULL; }
