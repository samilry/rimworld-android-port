// A libvulkan.so that sits between the engine and the system's Vulkan loader. It lives in the APK's library
// directory, so both the engine (NativeLibrary.Load("vulkan")) and our own libraries (dlopen("libvulkan.so")) get
// this one; it forwards everything to /system/lib64/libvulkan.so through vkGetInstanceProcAddr and only steps in
// for a few functions:
//   - surface capabilities: the transform is reported as identity ("identity"). The engine builds the swapchain
//     with the surface's current transform but keeps the window's width and height, which on Android (a rotated
//     display) makes the swapchain wrong; with identity the compositor rotates instead. Android then answers
//     "suboptimal" to every acquire and present, which is reported as success (the engine would rebuild the
//     swapchain each frame).
//   - memory: host-visible memory that is not host-coherent needs explicit flushes, which the engine (written for
//     desktop GPUs, whose host-visible memory is coherent) never does. Two remedies, chosen by options: "coherent"
//     hides such memory types; "flush" keeps them and flushes every mapping of them before each queue submission.
//   - swapchain creation, image acquisition and presentation are logged (the first few and every failure), and
//     the command-buffer work of a frame (render passes, draws, dispatches, viewports) is counted and logged for the
//     first "frames" frames and every 600th, so that the log tells what the renderer is doing.
// Options come from the PORT17_VULKAN environment variable (the host sets it from <game>/vulkan.txt), e.g.
// "identity=1 coherent=1 flush=0 frames=5".
#include <android/log.h>
#include <dlfcn.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#define VK_USE_PLATFORM_ANDROID_KHR
#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>

#define TAG "port17"
#define LOGI(...) __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__)
#define LOGW(...) __android_log_print(ANDROID_LOG_WARN, TAG, __VA_ARGS__)
#define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, TAG, __VA_ARGS__)
#define API __attribute__((visibility("default")))

static void* g_real;
static PFN_vkGetInstanceProcAddr g_gipa;
static PFN_vkGetDeviceProcAddr g_gdpa;
static VkInstance g_instance;
static VkDevice g_device;

static PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR real_GetSurfaceCaps;
static PFN_vkGetPhysicalDeviceSurfaceCapabilities2KHR real_GetSurfaceCaps2;
static PFN_vkCreateSwapchainKHR real_CreateSwapchain;
static PFN_vkAcquireNextImageKHR real_AcquireNextImage;
static PFN_vkQueuePresentKHR real_QueuePresent;
static PFN_vkCreateInstance real_CreateInstance;
static PFN_vkCreateDevice real_CreateDevice;
static PFN_vkCmdBeginRenderPass real_CmdBeginRenderPass;
static PFN_vkCmdBeginRenderPass2 real_CmdBeginRenderPass2;
static PFN_vkCmdBeginRendering real_CmdBeginRendering;
static PFN_vkCmdDraw real_CmdDraw;
static PFN_vkCmdDrawIndexed real_CmdDrawIndexed;
static PFN_vkCmdDrawIndirect real_CmdDrawIndirect;
static PFN_vkCmdDrawIndexedIndirect real_CmdDrawIndexedIndirect;
static PFN_vkCmdDispatch real_CmdDispatch;
static PFN_vkCmdSetViewport real_CmdSetViewport;
static PFN_vkCmdSetScissor real_CmdSetScissor;
static PFN_vkCmdBindPipeline real_CmdBindPipeline;
static PFN_vkCmdBlitImage real_CmdBlitImage;
static PFN_vkCmdCopyImage real_CmdCopyImage;
static PFN_vkCreateGraphicsPipelines real_CreateGraphicsPipelines;
static PFN_vkCreateComputePipelines real_CreateComputePipelines;
static PFN_vkGetPhysicalDeviceMemoryProperties real_GetMemoryProperties;
static PFN_vkGetPhysicalDeviceMemoryProperties2 real_GetMemoryProperties2;
static PFN_vkAllocateMemory real_AllocateMemory;
static PFN_vkFreeMemory real_FreeMemory;
static PFN_vkMapMemory real_MapMemory;
static PFN_vkUnmapMemory real_UnmapMemory;
static PFN_vkQueueSubmit real_QueueSubmit;
static PFN_vkQueueSubmit2 real_QueueSubmit2;
static PFN_vkWaitForFences real_WaitForFences;
static PFN_vkFlushMappedMemoryRanges real_FlushMappedMemoryRanges;
static PFN_vkInvalidateMappedMemoryRanges real_InvalidateMappedMemoryRanges;

// Options.
static int opt_identity = 1;
static int opt_coherentOnly = 1;
static int opt_flush = 0;
static int opt_logFrames = 5;

static long g_presents, g_acquires, g_presentFailures, g_acquireFailures;

// Per-frame counters (reset at each present; the render thread records and presents, other threads may record
// uploads, so the numbers are approximate but telling).
static int f_passes, f_rendering, f_draws, f_drawsIndexed, f_drawsIndirect, f_dispatches, f_pipelines, f_blits, f_copies;
static int f_viewportLogged, f_passLogged;
static long g_pipelinesCreated, g_computePipelinesCreated, g_pipelineFailures;

// Memory bookkeeping for the "flush" remedy.
static uint32_t g_nonCoherentTypes; // bit mask of host-visible types that are not coherent
#define MAX_MEMORY 8192
typedef struct Memory { VkDeviceMemory memory; uint32_t type; VkDeviceSize size; int mapped; VkDeviceSize mapOffset, mapSize; } Memory;
static Memory g_memory[MAX_MEMORY];
static int g_memoryCount;
static pthread_mutex_t g_memoryLock = PTHREAD_MUTEX_INITIALIZER;
static long g_flushes, g_flushedRanges;

static void read_options(void)
{
    const char* env = getenv("PORT17_VULKAN");
    if (!env) return;
    char copy[512];
    snprintf(copy, sizeof copy, "%s", env);
    for (char* tok = strtok(copy, " ,;\n"); tok; tok = strtok(NULL, " ,;\n"))
    {
        char* eq = strchr(tok, '=');
        if (!eq) continue;
        *eq = 0;
        int v = atoi(eq + 1);
        if (strcmp(tok, "identity") == 0) opt_identity = v;
        else if (strcmp(tok, "coherent") == 0) opt_coherentOnly = v;
        else if (strcmp(tok, "flush") == 0) opt_flush = v;
        else if (strcmp(tok, "frames") == 0) opt_logFrames = v;
    }
}

static int ensure(void)
{
    if (g_gipa) return 1;
    g_real = dlopen("/system/lib64/libvulkan.so", RTLD_NOW | RTLD_LOCAL);
    if (!g_real)
    {
        LOGE("vulkan interposer: cannot load the system loader: %s", dlerror());
        return 0;
    }
    g_gipa = (PFN_vkGetInstanceProcAddr)dlsym(g_real, "vkGetInstanceProcAddr");
    if (!g_gipa)
    {
        LOGE("vulkan interposer: the system loader has no vkGetInstanceProcAddr");
        return 0;
    }
    read_options();
    LOGI("vulkan interposer: forwarding to the system loader (identity=%d coherent=%d flush=%d frames=%d)", opt_identity, opt_coherentOnly, opt_flush, opt_logFrames);
    return 1;
}

static const char* result_name(VkResult r)
{
    switch (r)
    {
    case VK_SUCCESS: return "SUCCESS";
    case VK_NOT_READY: return "NOT_READY";
    case VK_TIMEOUT: return "TIMEOUT";
    case VK_SUBOPTIMAL_KHR: return "SUBOPTIMAL_KHR";
    case VK_ERROR_OUT_OF_DATE_KHR: return "ERROR_OUT_OF_DATE_KHR";
    case VK_ERROR_SURFACE_LOST_KHR: return "ERROR_SURFACE_LOST_KHR";
    case VK_ERROR_DEVICE_LOST: return "ERROR_DEVICE_LOST";
    case VK_ERROR_OUT_OF_HOST_MEMORY: return "ERROR_OUT_OF_HOST_MEMORY";
    case VK_ERROR_OUT_OF_DEVICE_MEMORY: return "ERROR_OUT_OF_DEVICE_MEMORY";
    case VK_ERROR_INITIALIZATION_FAILED: return "ERROR_INITIALIZATION_FAILED";
    case VK_ERROR_NATIVE_WINDOW_IN_USE_KHR: return "ERROR_NATIVE_WINDOW_IN_USE_KHR";
    case VK_ERROR_LAYER_NOT_PRESENT: return "ERROR_LAYER_NOT_PRESENT";
    case VK_ERROR_EXTENSION_NOT_PRESENT: return "ERROR_EXTENSION_NOT_PRESENT";
    case VK_ERROR_FEATURE_NOT_PRESENT: return "ERROR_FEATURE_NOT_PRESENT";
    default: return "?";
    }
}

// ---- surface / swapchain ----
static void fix_caps(VkSurfaceCapabilitiesKHR* caps, const char* who)
{
    static int logged;
    if (!logged)
    {
        logged = 1;
        LOGI("vulkan: %s: current %ux%u (min %ux%u max %ux%u), images %u..%u, transform 0x%x of 0x%x, alpha 0x%x, usage 0x%x",
             who, caps->currentExtent.width, caps->currentExtent.height, caps->minImageExtent.width, caps->minImageExtent.height,
             caps->maxImageExtent.width, caps->maxImageExtent.height, caps->minImageCount, caps->maxImageCount,
             caps->currentTransform, caps->supportedTransforms, caps->supportedCompositeAlpha, caps->supportedUsageFlags);
    }
    if (opt_identity)
    {
        caps->currentTransform = VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
        caps->supportedTransforms |= VK_SURFACE_TRANSFORM_IDENTITY_BIT_KHR;
    }
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_GetSurfaceCaps(VkPhysicalDevice pd, VkSurfaceKHR surface, VkSurfaceCapabilitiesKHR* caps)
{
    VkResult r = real_GetSurfaceCaps(pd, surface, caps);
    if (r == VK_SUCCESS) fix_caps(caps, "surface capabilities");
    else LOGW("vulkan: vkGetPhysicalDeviceSurfaceCapabilitiesKHR: %s", result_name(r));
    return r;
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_GetSurfaceCaps2(VkPhysicalDevice pd, const VkPhysicalDeviceSurfaceInfo2KHR* info, VkSurfaceCapabilities2KHR* caps)
{
    VkResult r = real_GetSurfaceCaps2(pd, info, caps);
    if (r == VK_SUCCESS) fix_caps(&caps->surfaceCapabilities, "surface capabilities 2");
    else LOGW("vulkan: vkGetPhysicalDeviceSurfaceCapabilities2KHR: %s", result_name(r));
    return r;
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_CreateSwapchain(VkDevice device, const VkSwapchainCreateInfoKHR* info, const VkAllocationCallbacks* alloc, VkSwapchainKHR* out)
{
    VkResult r = real_CreateSwapchain(device, info, alloc, out);
    LOGI("vulkan: swapchain %ux%u, format %d, %u images, transform 0x%x, alpha 0x%x, present mode %d, usage 0x%x, old %s: %s",
         info->imageExtent.width, info->imageExtent.height, (int)info->imageFormat, info->minImageCount, info->preTransform,
         info->compositeAlpha, (int)info->presentMode, info->imageUsage, info->oldSwapchain ? "yes" : "no", result_name(r));
    return r;
}

// With the identity transform forced, Android answers "suboptimal" to every acquire and present (the compositor
// rotates). The engine would rebuild the swapchain on every frame for that, so it is reported as success.
static VkResult quiet_suboptimal(VkResult r, const char* what)
{
    static int logged;
    if (r == VK_SUBOPTIMAL_KHR && opt_identity)
    {
        if (!logged) { logged = 1; LOGI("vulkan: %s reports SUBOPTIMAL_KHR (rotated display); reported as success from now on", what); }
        return VK_SUCCESS;
    }
    return r;
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_AcquireNextImage(VkDevice device, VkSwapchainKHR swapchain, uint64_t timeout, VkSemaphore semaphore, VkFence fence, uint32_t* index)
{
    VkResult r = quiet_suboptimal(real_AcquireNextImage(device, swapchain, timeout, semaphore, fence, index), "acquire");
    g_acquires++;
    if (r != VK_SUCCESS)
    {
        g_acquireFailures++;
        if (g_acquireFailures <= 10 || g_acquireFailures % 500 == 0) LOGW("vulkan: acquire #%ld: %s (%ld not success so far)", g_acquires, result_name(r), g_acquireFailures);
    }
    else if (g_acquires <= 3)
    {
        LOGI("vulkan: acquire #%ld: image %u", g_acquires, *index);
    }
    return r;
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_QueuePresent(VkQueue queue, const VkPresentInfoKHR* info)
{
    VkResult r = quiet_suboptimal(real_QueuePresent(queue, info), "present");
    g_presents++;
    if (r != VK_SUCCESS)
    {
        g_presentFailures++;
        if (g_presentFailures <= 10 || g_presentFailures % 500 == 0) LOGW("vulkan: present #%ld: %s (%ld not success so far)", g_presents, result_name(r), g_presentFailures);
    }
    if (g_presents <= opt_logFrames || g_presents % 600 == 0)
    {
        LOGI("vulkan: present #%ld %s (image %u): %d render passes, %d dynamic renderings, %d draws, %d indexed, %d indirect, %d dispatches, %d pipeline binds, %d blits, %d copies; pipelines so far %ld graphics + %ld compute (%ld failed); flushes %ld (%ld ranges)",
             g_presents, result_name(r), info->pImageIndices ? info->pImageIndices[0] : 0u, f_passes, f_rendering, f_draws, f_drawsIndexed, f_drawsIndirect, f_dispatches, f_pipelines, f_blits, f_copies,
             g_pipelinesCreated, g_computePipelinesCreated, g_pipelineFailures, g_flushes, g_flushedRanges);
    }
    f_passes = f_rendering = f_draws = f_drawsIndexed = f_drawsIndirect = f_dispatches = f_pipelines = f_blits = f_copies = 0;
    f_viewportLogged = f_passLogged = 0;
    return r;
}

// ---- command recording, counted ----
static VKAPI_ATTR void VKAPI_CALL hook_CmdBeginRenderPass(VkCommandBuffer cb, const VkRenderPassBeginInfo* info, VkSubpassContents contents)
{
    f_passes++;
    if (g_presents < opt_logFrames && f_passLogged < 8)
    {
        f_passLogged++;
        const VkClearValue* c = info->clearValueCount ? info->pClearValues : NULL;
        LOGI("vulkan:   pass %d: area %d,%d %ux%u, %u clear values%s%.2f %.2f %.2f %.2f", f_passes, info->renderArea.offset.x, info->renderArea.offset.y,
             info->renderArea.extent.width, info->renderArea.extent.height, info->clearValueCount, c ? ", first " : "", c ? c->color.float32[0] : 0.0, c ? c->color.float32[1] : 0.0, c ? c->color.float32[2] : 0.0, c ? c->color.float32[3] : 0.0);
    }
    real_CmdBeginRenderPass(cb, info, contents);
}

static VKAPI_ATTR void VKAPI_CALL hook_CmdBeginRenderPass2(VkCommandBuffer cb, const VkRenderPassBeginInfo* info, const VkSubpassBeginInfo* sub)
{
    f_passes++;
    if (g_presents < opt_logFrames && f_passLogged < 8)
    {
        f_passLogged++;
        LOGI("vulkan:   pass2 %d: area %d,%d %ux%u, %u clear values", f_passes, info->renderArea.offset.x, info->renderArea.offset.y, info->renderArea.extent.width, info->renderArea.extent.height, info->clearValueCount);
    }
    real_CmdBeginRenderPass2(cb, info, sub);
}

static VKAPI_ATTR void VKAPI_CALL hook_CmdBeginRendering(VkCommandBuffer cb, const VkRenderingInfo* info)
{
    f_rendering++;
    if (g_presents < opt_logFrames && f_passLogged < 8)
    {
        f_passLogged++;
        LOGI("vulkan:   rendering %d: area %d,%d %ux%u, %u color attachments", f_rendering, info->renderArea.offset.x, info->renderArea.offset.y, info->renderArea.extent.width, info->renderArea.extent.height, info->colorAttachmentCount);
    }
    real_CmdBeginRendering(cb, info);
}

static VKAPI_ATTR void VKAPI_CALL hook_CmdDraw(VkCommandBuffer cb, uint32_t v, uint32_t i, uint32_t fv, uint32_t fi) { f_draws++; real_CmdDraw(cb, v, i, fv, fi); }
static VKAPI_ATTR void VKAPI_CALL hook_CmdDrawIndexed(VkCommandBuffer cb, uint32_t ic, uint32_t inst, uint32_t fi, int32_t vo, uint32_t fin) { f_drawsIndexed++; real_CmdDrawIndexed(cb, ic, inst, fi, vo, fin); }
static VKAPI_ATTR void VKAPI_CALL hook_CmdDrawIndirect(VkCommandBuffer cb, VkBuffer b, VkDeviceSize o, uint32_t c, uint32_t s) { f_drawsIndirect++; real_CmdDrawIndirect(cb, b, o, c, s); }
static VKAPI_ATTR void VKAPI_CALL hook_CmdDrawIndexedIndirect(VkCommandBuffer cb, VkBuffer b, VkDeviceSize o, uint32_t c, uint32_t s) { f_drawsIndirect++; real_CmdDrawIndexedIndirect(cb, b, o, c, s); }
static VKAPI_ATTR void VKAPI_CALL hook_CmdDispatch(VkCommandBuffer cb, uint32_t x, uint32_t y, uint32_t z) { f_dispatches++; real_CmdDispatch(cb, x, y, z); }
static VKAPI_ATTR void VKAPI_CALL hook_CmdBindPipeline(VkCommandBuffer cb, VkPipelineBindPoint bp, VkPipeline p) { f_pipelines++; real_CmdBindPipeline(cb, bp, p); }
static VKAPI_ATTR void VKAPI_CALL hook_CmdBlitImage(VkCommandBuffer cb, VkImage s, VkImageLayout sl, VkImage d, VkImageLayout dl, uint32_t n, const VkImageBlit* r, VkFilter f) { f_blits++; real_CmdBlitImage(cb, s, sl, d, dl, n, r, f); }
static VKAPI_ATTR void VKAPI_CALL hook_CmdCopyImage(VkCommandBuffer cb, VkImage s, VkImageLayout sl, VkImage d, VkImageLayout dl, uint32_t n, const VkImageCopy* r) { f_copies++; real_CmdCopyImage(cb, s, sl, d, dl, n, r); }

static VKAPI_ATTR void VKAPI_CALL hook_CmdSetViewport(VkCommandBuffer cb, uint32_t first, uint32_t count, const VkViewport* v)
{
    if (g_presents < opt_logFrames && f_viewportLogged < 2 && count > 0)
    {
        f_viewportLogged++;
        LOGI("vulkan:   viewport %.0f,%.0f %.0fx%.0f depth %.2f..%.2f", v[0].x, v[0].y, v[0].width, v[0].height, v[0].minDepth, v[0].maxDepth);
    }
    real_CmdSetViewport(cb, first, count, v);
}

static VKAPI_ATTR void VKAPI_CALL hook_CmdSetScissor(VkCommandBuffer cb, uint32_t first, uint32_t count, const VkRect2D* s)
{
    if (g_presents < opt_logFrames && f_viewportLogged == 1 && count > 0)
    {
        f_viewportLogged++;
        LOGI("vulkan:   scissor %d,%d %ux%u", s[0].offset.x, s[0].offset.y, s[0].extent.width, s[0].extent.height);
    }
    real_CmdSetScissor(cb, first, count, s);
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_CreateGraphicsPipelines(VkDevice device, VkPipelineCache cache, uint32_t count, const VkGraphicsPipelineCreateInfo* infos, const VkAllocationCallbacks* alloc, VkPipeline* out)
{
    VkResult r = real_CreateGraphicsPipelines(device, cache, count, infos, alloc, out);
    g_pipelinesCreated += count;
    if (r != VK_SUCCESS) { g_pipelineFailures++; LOGW("vulkan: vkCreateGraphicsPipelines (%u): %s", count, result_name(r)); }
    return r;
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_CreateComputePipelines(VkDevice device, VkPipelineCache cache, uint32_t count, const VkComputePipelineCreateInfo* infos, const VkAllocationCallbacks* alloc, VkPipeline* out)
{
    VkResult r = real_CreateComputePipelines(device, cache, count, infos, alloc, out);
    g_computePipelinesCreated += count;
    if (r != VK_SUCCESS) { g_pipelineFailures++; LOGW("vulkan: vkCreateComputePipelines (%u): %s", count, result_name(r)); }
    return r;
}

// ---- memory ----
static void fix_memory_properties(VkPhysicalDeviceMemoryProperties* props)
{
    static int logged;
    uint32_t coherent = 0;
    g_nonCoherentTypes = 0;
    for (uint32_t i = 0; i < props->memoryTypeCount; i++)
    {
        VkMemoryPropertyFlags f = props->memoryTypes[i].propertyFlags;
        if ((f & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT) && (f & VK_MEMORY_PROPERTY_HOST_COHERENT_BIT)) coherent++;
        if ((f & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT) && !(f & VK_MEMORY_PROPERTY_HOST_COHERENT_BIT)) g_nonCoherentTypes |= 1u << i;
    }
    for (uint32_t i = 0; i < props->memoryTypeCount; i++)
    {
        VkMemoryPropertyFlags f = props->memoryTypes[i].propertyFlags;
        int strip = opt_coherentOnly && coherent > 0 && (f & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT) && !(f & VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
        if (!logged)
        {
            LOGI("vulkan: memory type %u: heap %u flags 0x%x%s%s%s%s%s%s", i, props->memoryTypes[i].heapIndex, f,
                 (f & VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT) ? " device-local" : "", (f & VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT) ? " host-visible" : "",
                 (f & VK_MEMORY_PROPERTY_HOST_COHERENT_BIT) ? " coherent" : "", (f & VK_MEMORY_PROPERTY_HOST_CACHED_BIT) ? " cached" : "",
                 (f & VK_MEMORY_PROPERTY_LAZILY_ALLOCATED_BIT) ? " lazy" : "", strip ? " -> hidden (not coherent)" : "");
        }
        if (strip) props->memoryTypes[i].propertyFlags = f & ~(VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_CACHED_BIT);
    }
    if (!logged)
    {
        for (uint32_t h = 0; h < props->memoryHeapCount; h++) LOGI("vulkan: memory heap %u: %llu MiB flags 0x%x", h, (unsigned long long)(props->memoryHeaps[h].size >> 20), props->memoryHeaps[h].flags);
        logged = 1;
    }
}

static VKAPI_ATTR void VKAPI_CALL hook_GetMemoryProperties(VkPhysicalDevice pd, VkPhysicalDeviceMemoryProperties* props)
{
    real_GetMemoryProperties(pd, props);
    fix_memory_properties(props);
}

static VKAPI_ATTR void VKAPI_CALL hook_GetMemoryProperties2(VkPhysicalDevice pd, VkPhysicalDeviceMemoryProperties2* props)
{
    real_GetMemoryProperties2(pd, props);
    fix_memory_properties(&props->memoryProperties);
}

static Memory* find_memory_locked(VkDeviceMemory memory)
{
    for (int i = 0; i < g_memoryCount; i++) if (g_memory[i].memory == memory) return &g_memory[i];
    return NULL;
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_AllocateMemory(VkDevice device, const VkMemoryAllocateInfo* info, const VkAllocationCallbacks* alloc, VkDeviceMemory* out)
{
    VkResult r = real_AllocateMemory(device, info, alloc, out);
    if (r == VK_SUCCESS && opt_flush && (g_nonCoherentTypes & (1u << info->memoryTypeIndex)))
    {
        pthread_mutex_lock(&g_memoryLock);
        if (g_memoryCount < MAX_MEMORY)
        {
            Memory* m = &g_memory[g_memoryCount++];
            memset(m, 0, sizeof *m);
            m->memory = *out;
            m->type = info->memoryTypeIndex;
            m->size = info->allocationSize;
        }
        else
        {
            LOGW("vulkan: too many non-coherent allocations to track");
        }
        pthread_mutex_unlock(&g_memoryLock);
    }
    return r;
}

static VKAPI_ATTR void VKAPI_CALL hook_FreeMemory(VkDevice device, VkDeviceMemory memory, const VkAllocationCallbacks* alloc)
{
    if (opt_flush && memory)
    {
        pthread_mutex_lock(&g_memoryLock);
        Memory* m = find_memory_locked(memory);
        if (m) *m = g_memory[--g_memoryCount];
        pthread_mutex_unlock(&g_memoryLock);
    }
    real_FreeMemory(device, memory, alloc);
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_MapMemory(VkDevice device, VkDeviceMemory memory, VkDeviceSize offset, VkDeviceSize size, VkMemoryMapFlags flags, void** data)
{
    VkResult r = real_MapMemory(device, memory, offset, size, flags, data);
    if (r == VK_SUCCESS && opt_flush)
    {
        pthread_mutex_lock(&g_memoryLock);
        Memory* m = find_memory_locked(memory);
        if (m) { m->mapped = 1; m->mapOffset = offset; m->mapSize = size; }
        pthread_mutex_unlock(&g_memoryLock);
    }
    return r;
}

static VKAPI_ATTR void VKAPI_CALL hook_UnmapMemory(VkDevice device, VkDeviceMemory memory)
{
    if (opt_flush)
    {
        pthread_mutex_lock(&g_memoryLock);
        Memory* m = find_memory_locked(memory);
        if (m) m->mapped = 0;
        pthread_mutex_unlock(&g_memoryLock);
    }
    real_UnmapMemory(device, memory);
}

// Flushes (or invalidates) every mapping of non-coherent memory, whole mapped range.
static void sync_mapped(int invalidate)
{
    if (!opt_flush || !g_device) return;
    VkMappedMemoryRange ranges[256];
    uint32_t n = 0;
    pthread_mutex_lock(&g_memoryLock);
    for (int i = 0; i < g_memoryCount && n < 256; i++)
    {
        Memory* m = &g_memory[i];
        if (!m->mapped) continue;
        VkMappedMemoryRange* r = &ranges[n++];
        memset(r, 0, sizeof *r);
        r->sType = VK_STRUCTURE_TYPE_MAPPED_MEMORY_RANGE;
        r->memory = m->memory;
        r->offset = m->mapOffset;
        r->size = VK_WHOLE_SIZE;
    }
    pthread_mutex_unlock(&g_memoryLock);
    if (n == 0) return;
    if (invalidate) { if (real_InvalidateMappedMemoryRanges) real_InvalidateMappedMemoryRanges(g_device, n, ranges); }
    else { if (real_FlushMappedMemoryRanges) real_FlushMappedMemoryRanges(g_device, n, ranges); g_flushes++; g_flushedRanges += n; }
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_QueueSubmit(VkQueue queue, uint32_t count, const VkSubmitInfo* submits, VkFence fence)
{
    sync_mapped(0);
    return real_QueueSubmit(queue, count, submits, fence);
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_QueueSubmit2(VkQueue queue, uint32_t count, const VkSubmitInfo2* submits, VkFence fence)
{
    sync_mapped(0);
    return real_QueueSubmit2(queue, count, submits, fence);
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_WaitForFences(VkDevice device, uint32_t count, const VkFence* fences, VkBool32 all, uint64_t timeout)
{
    VkResult r = real_WaitForFences(device, count, fences, all, timeout);
    if (r == VK_SUCCESS) sync_mapped(1);
    return r;
}

// ---- instance / device ----
static VKAPI_ATTR VkResult VKAPI_CALL hook_CreateInstance(const VkInstanceCreateInfo* info, const VkAllocationCallbacks* alloc, VkInstance* out)
{
    VkResult r = real_CreateInstance(info, alloc, out);
    if (r == VK_SUCCESS) g_instance = *out;
    char ext[1024] = "";
    for (uint32_t i = 0; i < info->enabledExtensionCount && strlen(ext) < 900; i++)
    {
        strncat(ext, info->ppEnabledExtensionNames[i], sizeof ext - strlen(ext) - 2);
        strncat(ext, " ", sizeof ext - strlen(ext) - 1);
    }
    char layers[512] = "";
    for (uint32_t i = 0; i < info->enabledLayerCount && strlen(layers) < 400; i++)
    {
        strncat(layers, info->ppEnabledLayerNames[i], sizeof layers - strlen(layers) - 2);
        strncat(layers, " ", sizeof layers - strlen(layers) - 1);
    }
    LOGI("vulkan: instance created (%s), api 0x%x, extensions: %s; layers: %s", result_name(r), info->pApplicationInfo ? info->pApplicationInfo->apiVersion : 0u, ext, layers[0] ? layers : "none");
    return r;
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_CreateDevice(VkPhysicalDevice pd, const VkDeviceCreateInfo* info, const VkAllocationCallbacks* alloc, VkDevice* out)
{
    VkResult r = real_CreateDevice(pd, info, alloc, out);
    if (r == VK_SUCCESS)
    {
        g_device = *out;
        if (g_gdpa)
        {
            real_FlushMappedMemoryRanges = (PFN_vkFlushMappedMemoryRanges)g_gdpa(g_device, "vkFlushMappedMemoryRanges");
            real_InvalidateMappedMemoryRanges = (PFN_vkInvalidateMappedMemoryRanges)g_gdpa(g_device, "vkInvalidateMappedMemoryRanges");
        }
    }
    char ext[2048] = "";
    for (uint32_t i = 0; i < info->enabledExtensionCount && strlen(ext) < 1900; i++)
    {
        strncat(ext, info->ppEnabledExtensionNames[i], sizeof ext - strlen(ext) - 2);
        strncat(ext, " ", sizeof ext - strlen(ext) - 1);
    }
    LOGI("vulkan: device created (%s), extensions: %s", result_name(r), ext);
    return r;
}

// The aliases below share the typedef names: map them for the HOOK macro.
#define real_CreateSwapchainKHR real_CreateSwapchain
#define hook_CreateSwapchainKHR hook_CreateSwapchain
#define real_AcquireNextImageKHR real_AcquireNextImage
#define hook_AcquireNextImageKHR hook_AcquireNextImage
#define real_QueuePresentKHR real_QueuePresent
#define hook_QueuePresentKHR hook_QueuePresent

// Device-level functions we stand in for; looked up through either vkGetDeviceProcAddr or vkGetInstanceProcAddr.
static PFN_vkVoidFunction device_hook(const char* name, PFN_vkVoidFunction real)
{
#define HOOK(n) if (strcmp(name, "vk" #n) == 0) { real_##n = (PFN_vk##n)real; return (PFN_vkVoidFunction)hook_##n; }
    HOOK(CreateSwapchainKHR)
    HOOK(AcquireNextImageKHR)
    HOOK(QueuePresentKHR)
    HOOK(CmdBeginRenderPass)
    HOOK(CmdBeginRenderPass2)
    HOOK(CmdBeginRendering)
    HOOK(CmdDraw)
    HOOK(CmdDrawIndexed)
    HOOK(CmdDrawIndirect)
    HOOK(CmdDrawIndexedIndirect)
    HOOK(CmdDispatch)
    HOOK(CmdSetViewport)
    HOOK(CmdSetScissor)
    HOOK(CmdBindPipeline)
    HOOK(CmdBlitImage)
    HOOK(CmdCopyImage)
    HOOK(CreateGraphicsPipelines)
    HOOK(CreateComputePipelines)
    HOOK(AllocateMemory)
    HOOK(FreeMemory)
    HOOK(MapMemory)
    HOOK(UnmapMemory)
    HOOK(QueueSubmit)
    HOOK(QueueSubmit2)
    HOOK(WaitForFences)
#undef HOOK
    if (strcmp(name, "vkCmdBeginRenderPass2KHR") == 0) { real_CmdBeginRenderPass2 = (PFN_vkCmdBeginRenderPass2)real; return (PFN_vkVoidFunction)hook_CmdBeginRenderPass2; }
    if (strcmp(name, "vkCmdBeginRenderingKHR") == 0) { real_CmdBeginRendering = (PFN_vkCmdBeginRendering)real; return (PFN_vkVoidFunction)hook_CmdBeginRendering; }
    if (strcmp(name, "vkQueueSubmit2KHR") == 0) { real_QueueSubmit2 = (PFN_vkQueueSubmit2)real; return (PFN_vkVoidFunction)hook_QueueSubmit2; }
    return NULL;
}

static VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL hook_GetDeviceProcAddr(VkDevice device, const char* name)
{
    PFN_vkVoidFunction real = g_gdpa(device, name);
    if (!real) return NULL;
    PFN_vkVoidFunction hook = device_hook(name, real);
    return hook ? hook : real;
}

API VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL vkGetInstanceProcAddr(VkInstance instance, const char* name)
{
    if (!ensure() || !name) return NULL;
    PFN_vkVoidFunction real = g_gipa(instance, name);
    if (!real) return NULL;
    if (instance && !g_instance) g_instance = instance;
    if (strcmp(name, "vkGetInstanceProcAddr") == 0) return (PFN_vkVoidFunction)vkGetInstanceProcAddr;
    if (strcmp(name, "vkCreateInstance") == 0) { real_CreateInstance = (PFN_vkCreateInstance)real; return (PFN_vkVoidFunction)hook_CreateInstance; }
    if (strcmp(name, "vkCreateDevice") == 0) { real_CreateDevice = (PFN_vkCreateDevice)real; return (PFN_vkVoidFunction)hook_CreateDevice; }
    if (strcmp(name, "vkGetDeviceProcAddr") == 0) { g_gdpa = (PFN_vkGetDeviceProcAddr)real; return (PFN_vkVoidFunction)hook_GetDeviceProcAddr; }
    if (strcmp(name, "vkGetPhysicalDeviceSurfaceCapabilitiesKHR") == 0) { real_GetSurfaceCaps = (PFN_vkGetPhysicalDeviceSurfaceCapabilitiesKHR)real; return (PFN_vkVoidFunction)hook_GetSurfaceCaps; }
    if (strcmp(name, "vkGetPhysicalDeviceSurfaceCapabilities2KHR") == 0) { real_GetSurfaceCaps2 = (PFN_vkGetPhysicalDeviceSurfaceCapabilities2KHR)real; return (PFN_vkVoidFunction)hook_GetSurfaceCaps2; }
    if (strcmp(name, "vkGetPhysicalDeviceMemoryProperties") == 0) { real_GetMemoryProperties = (PFN_vkGetPhysicalDeviceMemoryProperties)real; return (PFN_vkVoidFunction)hook_GetMemoryProperties; }
    if (strcmp(name, "vkGetPhysicalDeviceMemoryProperties2") == 0 || strcmp(name, "vkGetPhysicalDeviceMemoryProperties2KHR") == 0) { real_GetMemoryProperties2 = (PFN_vkGetPhysicalDeviceMemoryProperties2)real; return (PFN_vkVoidFunction)hook_GetMemoryProperties2; }
    PFN_vkVoidFunction hook = device_hook(name, real);
    return hook ? hook : real;
}

// The global entry points, for anyone linking by name.
API VKAPI_ATTR VkResult VKAPI_CALL vkCreateInstance(const VkInstanceCreateInfo* info, const VkAllocationCallbacks* alloc, VkInstance* out)
{
    PFN_vkCreateInstance f = (PFN_vkCreateInstance)vkGetInstanceProcAddr(NULL, "vkCreateInstance");
    return f ? f(info, alloc, out) : VK_ERROR_INITIALIZATION_FAILED;
}

API VKAPI_ATTR VkResult VKAPI_CALL vkEnumerateInstanceExtensionProperties(const char* layer, uint32_t* count, VkExtensionProperties* props)
{
    PFN_vkEnumerateInstanceExtensionProperties f = (PFN_vkEnumerateInstanceExtensionProperties)vkGetInstanceProcAddr(NULL, "vkEnumerateInstanceExtensionProperties");
    return f ? f(layer, count, props) : VK_ERROR_INITIALIZATION_FAILED;
}

API VKAPI_ATTR VkResult VKAPI_CALL vkEnumerateInstanceLayerProperties(uint32_t* count, VkLayerProperties* props)
{
    PFN_vkEnumerateInstanceLayerProperties f = (PFN_vkEnumerateInstanceLayerProperties)vkGetInstanceProcAddr(NULL, "vkEnumerateInstanceLayerProperties");
    return f ? f(count, props) : VK_ERROR_INITIALIZATION_FAILED;
}

API VKAPI_ATTR VkResult VKAPI_CALL vkEnumerateInstanceVersion(uint32_t* version)
{
    PFN_vkEnumerateInstanceVersion f = (PFN_vkEnumerateInstanceVersion)vkGetInstanceProcAddr(NULL, "vkEnumerateInstanceVersion");
    if (!f) { *version = VK_API_VERSION_1_0; return VK_SUCCESS; }
    return f(version);
}
