// A few Windows user32 functions that PC mods call directly, for the phone (hooks/WindowsLibraries.cs loads this when an
// assembly asks for "user32" and nothing else answers). Character Editor copies a pawn with OpenClipboard,
// SetClipboardData(CF_UNICODETEXT, Marshal.StringToHGlobalUni(text)) and CloseClipboard: on the phone that was
// DllNotFoundException, on the PC it reaches the clipboard. Here the clipboard is the game's, which is the phone's
// (glfwSetClipboardString / glfwGetClipboardString in libglfw.so). MessageBox writes to the log and answers OK.
#include <android/log.h>
#include <dlfcn.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

#define LOG(...) __android_log_print(ANDROID_LOG_INFO, "port17", __VA_ARGS__)
#define CF_TEXT 1
#define CF_UNICODETEXT 13

static void* glfw(const char* name)
{
    static void* lib;
    if (!lib) lib = dlopen("libglfw.so", RTLD_NOW | RTLD_NOLOAD);
    if (!lib) lib = dlopen("libglfw.so", RTLD_NOW);
    return lib ? dlsym(lib, name) : NULL;
}

// UTF-16 (Windows' wchar_t) to a malloc'd UTF-8 string.
static char* from_utf16(const uint16_t* s)
{
    size_t n = 0;
    while (s[n]) n++;
    char* out = (char*)malloc(n * 3 + 1);
    if (!out) return NULL;
    size_t o = 0;
    for (size_t i = 0; i < n; i++)
    {
        uint32_t c = s[i];
        if (c >= 0xD800 && c < 0xDC00 && i + 1 < n && s[i + 1] >= 0xDC00 && s[i + 1] < 0xE000)
        {
            c = 0x10000 + ((c - 0xD800) << 10) + (s[i + 1] - 0xDC00);
            i++;
        }
        if (c < 0x80) out[o++] = (char)c;
        else if (c < 0x800) { out[o++] = (char)(0xC0 | (c >> 6)); out[o++] = (char)(0x80 | (c & 0x3F)); }
        else if (c < 0x10000) { out[o++] = (char)(0xE0 | (c >> 12)); out[o++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[o++] = (char)(0x80 | (c & 0x3F)); }
        else { out[o++] = (char)(0xF0 | (c >> 18)); out[o++] = (char)(0x80 | ((c >> 12) & 0x3F)); out[o++] = (char)(0x80 | ((c >> 6) & 0x3F)); out[o++] = (char)(0x80 | (c & 0x3F)); }
    }
    out[o] = 0;
    return out;
}

// UTF-8 to a malloc'd UTF-16 string.
static uint16_t* to_utf16(const char* s)
{
    size_t n = strlen(s);
    uint16_t* out = (uint16_t*)malloc((n + 1) * 2 * sizeof(uint16_t));
    if (!out) return NULL;
    size_t o = 0;
    for (size_t i = 0; i < n;)
    {
        uint32_t c = (unsigned char)s[i];
        int extra = c >= 0xF0 ? 3 : c >= 0xE0 ? 2 : c >= 0xC0 ? 1 : 0;
        if (extra) c &= (0x3F >> extra);
        i++;
        for (int k = 0; k < extra && i < n; k++, i++) c = (c << 6) | ((unsigned char)s[i] & 0x3F);
        if (c >= 0x10000) { c -= 0x10000; out[o++] = (uint16_t)(0xD800 + (c >> 10)); out[o++] = (uint16_t)(0xDC00 + (c & 0x3FF)); }
        else out[o++] = (uint16_t)c;
    }
    out[o] = 0;
    return out;
}

int OpenClipboard(void* owner) { (void)owner; return 1; }
int CloseClipboard(void) { return 1; }
int EmptyClipboard(void) { return 1; }

int IsClipboardFormatAvailable(unsigned int format)
{
    return format == CF_UNICODETEXT || format == CF_TEXT;
}

// The memory stays the caller's: Windows would own it from here on, and a mod never frees it.
void* SetClipboardData(unsigned int format, void* memory)
{
    void (*set)(void*, const char*) = (void (*)(void*, const char*))glfw("glfwSetClipboardString");
    if (!set || !memory) return NULL;
    if (format == CF_UNICODETEXT)
    {
        char* text = from_utf16((const uint16_t*)memory);
        if (text) { set(NULL, text); free(text); }
    }
    else if (format == CF_TEXT) set(NULL, (const char*)memory);
    else return NULL;
    return memory;
}

// A copy valid until the next call (Windows keeps its handle until the clipboard changes; a mod reads it at once).
void* GetClipboardData(unsigned int format)
{
    static void* last;
    const char* (*get)(void*) = (const char* (*)(void*))glfw("glfwGetClipboardString");
    const char* text = get ? get(NULL) : NULL;
    if (!text) return NULL;
    void* copy = format == CF_UNICODETEXT ? (void*)to_utf16(text) : format == CF_TEXT ? (void*)strdup(text) : NULL;
    if (copy)
    {
        free(last);
        last = copy;
    }
    return copy;
}

int MessageBoxW(void* owner, const uint16_t* text, const uint16_t* caption, unsigned int type)
{
    (void)owner; (void)type;
    char* t = text ? from_utf16(text) : NULL;
    char* c = caption ? from_utf16(caption) : NULL;
    LOG("MessageBox from a mod: %s: %s", c ? c : "", t ? t : "");
    free(t);
    free(c);
    return 1;
}

int MessageBoxA(void* owner, const char* text, const char* caption, unsigned int type)
{
    (void)owner; (void)type;
    LOG("MessageBox from a mod: %s: %s", caption ? caption : "", text ? text : "");
    return 1;
}
