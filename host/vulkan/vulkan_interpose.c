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
//   - descriptor pools ("pools", on unless pools=0): the engine sizes its pools for a desktop driver, where a pool is
//     bookkeeping in ordinary memory: a pool of 262 144 textures for every mesh renderer, pools of 2 048 sets for
//     every frame in flight. Adreno's driver backs a pool with graphics memory when it is created, about 23 KiB a
//     set: 360 MiB for each of the five material pools, 2.3 GiB in all with 106 mods in a map, none of it in the
//     engine's own accounts, all of it counted by the phone's watchdog, which kills an app at 6 GiB. A large pool is
//     created here as a sixteenth of what was asked for and grows by further sixteenths when an allocation finds
//     it full; the engine keeps the handle of the first part and never sees the others.
//   - with "memlog", every allocation of device memory of 32 MiB and more is logged with the total the engine holds,
//     to compare with what the kernel counts for the process (/sys/class/kgsl/kgsl/proc/<pid>/gpumem_mapped);
//   - swapchain creation, image acquisition and presentation are logged (the first few and every failure), and
//     the command-buffer work of a frame (render passes, draws, dispatches, viewports) is counted and logged for the
//     first "frames" frames and every 600th, so that the log tells what the renderer is doing.
// Options come from the PORT17_VULKAN environment variable (the host sets it from <game>/vulkan.txt), e.g.
// "identity=1 coherent=1 flush=0 frames=5".
#include <android/log.h>
#include <dlfcn.h>
#include <pthread.h>
#include <unistd.h>
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
// The lines that say what the driver gave the engine also go to the game's stderr (logs/stderr.txt, which the crash
// report takes): some phones let an app read none of its own logcat (a tester's Honor, 2026-10-23), and then a crash
// during the start had nothing to tell which Vulkan call was missing.
#define LOGK(...) do { __android_log_print(ANDROID_LOG_INFO, TAG, __VA_ARGS__); fprintf(stderr, "vulkan interposer: "); fprintf(stderr, __VA_ARGS__); fputc('\n', stderr); } while (0)
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
// A command for an image without a handle is left out (see "no image, no command" below).
static void note_null_image(const char* what);
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
static int opt_barriers = -1; // "barriers=1": a full barrier opens every command buffer; "barriers=0": never; default: on ARM Mali
static int opt_depth24 = -1; // "depth24=1": the engine's D32_SFLOAT_S8_UINT is always D24_UNORM_S8_UINT; "depth24=0": never; default: where the driver cannot use it
static VkFormat g_depthFrom, g_depthTo; // the depth format below (choose_depth_format)
static VkFormat depth_format(VkFormat format);
static int opt_noDebugUtils; // "debugutils=0": VK_EXT_debug_utils is hidden from the engine (to try the stand-ins below on a driver that has it)
static int opt_debugTest; // "debugtest=1": a driver advice and an ordinary message go to the engine's messenger as it is made (see filter_DebugMessage)
static int opt_dropDebugTest; // "dropdebug=1": VK_EXT_debug_utils counts as missing when the instance is made (to try hook_CreateInstance's leaving out on a driver that has it)
// Debug extensions the engine asked for and the driver does not have, left out of the instance (hook_CreateInstance):
// their functions are the stand-ins that do nothing.
static int g_droppedDebugReport, g_droppedDebugUtils;

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

// Descriptor pools built in parts (see the head of the file). A pool the engine created large is a chain of real
// pools, known to the engine by the handle of the first.
static int opt_pools = 1;
#define POOL_PARTS 16            // a part is this fraction of what the engine asked for
#define POOL_LARGE 16384         // pools of fewer descriptors are left as they are
#define POOL_MAX_SIZES 16
typedef struct PoolChain
{
    VkDescriptorPool key;        // the first real pool: the engine's handle
    VkDescriptorPoolCreateFlags flags;
    uint32_t maxSets, sizeCount;
    VkDescriptorPoolSize sizes[POOL_MAX_SIZES]; // as the engine asked
    VkDescriptorPool* parts;     // parts[0] == key
    int partCount, partCap, current;
    int hasFullPart;             // a part of the full size was added for a set no sixteenth can hold
    struct PoolChain* next;
} PoolChain;
#define POOL_BUCKETS 64
static PoolChain* g_poolChains[POOL_BUCKETS];
// For chains whose sets are freed one by one (FREE_DESCRIPTOR_SET): the part every set came from.
typedef struct SetHome { VkDescriptorSet set; VkDescriptorPool part; PoolChain* chain; struct SetHome* next; } SetHome;
#define SET_BUCKETS 16384
static SetHome* g_setHomes[SET_BUCKETS];
static pthread_mutex_t g_poolLock = PTHREAD_MUTEX_INITIALIZER;
static long g_poolChainsMade, g_poolPartsAdded;
static PFN_vkCreateDescriptorPool real_CreateDescriptorPool;
static PFN_vkDestroyDescriptorPool real_DestroyDescriptorPool;
static PFN_vkResetDescriptorPool real_ResetDescriptorPool;
static PFN_vkAllocateDescriptorSets real_AllocateDescriptorSets;
static PFN_vkFreeDescriptorSets real_FreeDescriptorSets;

static uint32_t handle_hash(uint64_t handle) { return (uint32_t)((handle >> 4) * 2654435761u); }

static PoolChain* find_chain(VkDescriptorPool pool)
{
    pthread_mutex_lock(&g_poolLock);
    PoolChain* chain = g_poolChains[handle_hash((uint64_t)pool) % POOL_BUCKETS];
    while (chain && chain->key != pool) chain = chain->next;
    pthread_mutex_unlock(&g_poolLock);
    return chain;
}

static VkResult create_part(VkDevice device, PoolChain* chain, int full, const VkAllocationCallbacks* alloc, VkDescriptorPool* out)
{
    VkDescriptorPoolSize sizes[POOL_MAX_SIZES];
    for (uint32_t i = 0; i < chain->sizeCount; i++)
    {
        sizes[i] = chain->sizes[i];
        if (!full) sizes[i].descriptorCount = (sizes[i].descriptorCount + POOL_PARTS - 1) / POOL_PARTS;
        if (sizes[i].descriptorCount == 0) sizes[i].descriptorCount = 1;
    }
    VkDescriptorPoolCreateInfo info = { VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO };
    info.flags = chain->flags;
    info.maxSets = full ? chain->maxSets : (chain->maxSets + POOL_PARTS - 1) / POOL_PARTS;
    if (info.maxSets == 0) info.maxSets = 1;
    info.poolSizeCount = chain->sizeCount;
    info.pPoolSizes = sizes;
    return real_CreateDescriptorPool(device, &info, alloc, out);
}

static int add_part(VkDevice device, PoolChain* chain, int full)
{
    if (chain->partCount == chain->partCap)
    {
        int cap = chain->partCap ? chain->partCap * 2 : 8;
        VkDescriptorPool* grown = (VkDescriptorPool*)realloc(chain->parts, cap * sizeof *grown);
        if (!grown) return 0;
        chain->parts = grown;
        chain->partCap = cap;
    }
    VkDescriptorPool part;
    if (create_part(device, chain, full, NULL, &part) != VK_SUCCESS) return 0;
    chain->parts[chain->partCount++] = part;
    if (full) chain->hasFullPart = 1;
    long added = __sync_add_and_fetch(&g_poolPartsAdded, 1);
    if (added <= 20 || added % 100 == 0)
    {
        LOGI("vulkan: a descriptor pool of %u sets grows to %d parts%s (%ld parts added so far)", chain->maxSets, chain->partCount, full ? ", this one of the full size" : "", added);
    }
    return 1;
}

static void forget_sets_of(PoolChain* chain)
{
    pthread_mutex_lock(&g_poolLock);
    for (int b = 0; b < SET_BUCKETS; b++)
    {
        SetHome** link = &g_setHomes[b];
        while (*link)
        {
            SetHome* home = *link;
            if (home->chain == chain) { *link = home->next; free(home); }
            else link = &home->next;
        }
    }
    pthread_mutex_unlock(&g_poolLock);
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_CreateDescriptorPool(VkDevice device, const VkDescriptorPoolCreateInfo* info, const VkAllocationCallbacks* alloc, VkDescriptorPool* out)
{
    uint64_t descriptors = 0;
    for (uint32_t i = 0; i < info->poolSizeCount; i++) descriptors += info->pPoolSizes[i].descriptorCount;
    // Pools with anything unusual about them (extension structures, more kinds than fit) stay as they are.
    if (!opt_pools || descriptors < POOL_LARGE || info->pNext || info->poolSizeCount == 0 || info->poolSizeCount > POOL_MAX_SIZES || info->maxSets < POOL_PARTS)
    {
        return real_CreateDescriptorPool(device, info, alloc, out);
    }
    PoolChain* chain = (PoolChain*)calloc(1, sizeof *chain);
    if (!chain) return real_CreateDescriptorPool(device, info, alloc, out);
    chain->flags = info->flags;
    chain->maxSets = info->maxSets;
    chain->sizeCount = info->poolSizeCount;
    memcpy(chain->sizes, info->pPoolSizes, info->poolSizeCount * sizeof(VkDescriptorPoolSize));
    chain->parts = (VkDescriptorPool*)malloc(8 * sizeof(VkDescriptorPool));
    chain->partCap = chain->parts ? 8 : 0;
    VkResult r = chain->parts ? create_part(device, chain, 0, alloc, out) : VK_ERROR_OUT_OF_HOST_MEMORY;
    if (r != VK_SUCCESS)
    {
        free(chain->parts);
        free(chain);
        return real_CreateDescriptorPool(device, info, alloc, out);
    }
    chain->key = *out;
    chain->parts[0] = *out;
    chain->partCount = 1;
    pthread_mutex_lock(&g_poolLock);
    uint32_t bucket = handle_hash((uint64_t)chain->key) % POOL_BUCKETS;
    chain->next = g_poolChains[bucket];
    g_poolChains[bucket] = chain;
    pthread_mutex_unlock(&g_poolLock);
    long made = __sync_add_and_fetch(&g_poolChainsMade, 1);
    if (made <= 12 || made % 50 == 0)
    {
        LOGI("vulkan: descriptor pool of %u sets and %llu descriptors is built in parts of a sixteenth (%ld such pools so far)", info->maxSets, (unsigned long long)descriptors, made);
    }
    return VK_SUCCESS;
}

static VkResult allocate_sets_unlocked(VkDevice device, const VkDescriptorSetAllocateInfo* info, VkDescriptorSet* sets)
{
    PoolChain* chain = opt_pools ? find_chain(info->descriptorPool) : NULL;
    if (!chain) return real_AllocateDescriptorSets(device, info, sets);
    VkDescriptorSetAllocateInfo mine = *info;
    VkResult r = VK_ERROR_OUT_OF_POOL_MEMORY;
    // The current part first, then the others (sets freed since, or a reset of the pool, leave room in earlier ones).
    for (int tried = 0; tried < chain->partCount; tried++)
    {
        int index = (chain->current + tried) % chain->partCount;
        mine.descriptorPool = chain->parts[index];
        r = real_AllocateDescriptorSets(device, &mine, sets);
        if (r != VK_ERROR_OUT_OF_POOL_MEMORY && r != VK_ERROR_FRAGMENTED_POOL)
        {
            if (r == VK_SUCCESS) chain->current = index;
            goto done;
        }
    }
    // All parts are full. One more, as long as the pool is not yet what the engine asked for: after that the engine
    // gets the refusal a whole pool would have given (it has its own answer to it).
    if (chain->partCount < POOL_PARTS + chain->hasFullPart && add_part(device, chain, 0))
    {
        chain->current = chain->partCount - 1;
        mine.descriptorPool = chain->parts[chain->current];
        r = real_AllocateDescriptorSets(device, &mine, sets);
        // A part just made that cannot hold the sets: they are larger than a sixteenth of the pool. One part of the
        // full size settles that.
        if ((r == VK_ERROR_OUT_OF_POOL_MEMORY || r == VK_ERROR_FRAGMENTED_POOL) && !chain->hasFullPart && add_part(device, chain, 1))
        {
            chain->current = chain->partCount - 1;
            mine.descriptorPool = chain->parts[chain->current];
            r = real_AllocateDescriptorSets(device, &mine, sets);
        }
    }
done:
    if (r == VK_SUCCESS && (chain->flags & VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT))
    {
        pthread_mutex_lock(&g_poolLock);
        for (uint32_t i = 0; i < info->descriptorSetCount; i++)
        {
            SetHome* home = (SetHome*)malloc(sizeof *home);
            if (!home) continue;
            uint32_t bucket = handle_hash((uint64_t)sets[i]) % SET_BUCKETS;
            home->set = sets[i];
            home->part = mine.descriptorPool;
            home->chain = chain;
            home->next = g_setHomes[bucket];
            g_setHomes[bucket] = home;
        }
        pthread_mutex_unlock(&g_poolLock);
    }
    return r;
}

static VkResult free_sets_unlocked(VkDevice device, VkDescriptorPool pool, uint32_t count, const VkDescriptorSet* sets)
{
    PoolChain* chain = opt_pools ? find_chain(pool) : NULL;
    if (!chain) return real_FreeDescriptorSets(device, pool, count, sets);
    for (uint32_t i = 0; i < count; i++)
    {
        if (!sets[i]) continue;
        VkDescriptorPool part = chain->key;
        pthread_mutex_lock(&g_poolLock);
        SetHome** link = &g_setHomes[handle_hash((uint64_t)sets[i]) % SET_BUCKETS];
        while (*link && !((*link)->set == sets[i] && (*link)->chain == chain)) link = &(*link)->next;
        if (*link)
        {
            SetHome* home = *link;
            part = home->part;
            *link = home->next;
            free(home);
        }
        pthread_mutex_unlock(&g_poolLock);
        real_FreeDescriptorSets(device, part, 1, &sets[i]);
    }
    return VK_SUCCESS;
}

static VkResult reset_pool_unlocked(VkDevice device, VkDescriptorPool pool, VkDescriptorPoolResetFlags flags)
{
    PoolChain* chain = opt_pools ? find_chain(pool) : NULL;
    if (!chain) return real_ResetDescriptorPool(device, pool, flags);
    VkResult r = VK_SUCCESS;
    for (int i = 0; i < chain->partCount; i++)
    {
        VkResult one = real_ResetDescriptorPool(device, chain->parts[i], flags);
        if (one != VK_SUCCESS) r = one;
    }
    chain->current = 0;
    if (chain->flags & VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT) forget_sets_of(chain);
    return r;
}

// ---- one thread at a time in a descriptor pool ----
// Vulkan leaves it to the program to keep two threads out of one descriptor pool at the same time. The engine's
// camera baker (portraits, pawn icons; on the game's thread) allocates from the frame resources the render thread is
// recording with. A driver that does not guard its pools itself may hand both the same set: one icon is then drawn with
// the other's texture (the suspicion for the icons flickering on Mali). Every allocation, free and reset goes through
// one lock here; when a thread has to wait for another one that holds the same pool, that is counted and logged.
static pthread_mutex_t g_descriptorLock = PTHREAD_MUTEX_INITIALIZER;
static VkDescriptorPool g_descriptorHolder;
static int g_descriptorHolderTid;
static long g_samePoolWaits, g_otherPoolWaits;

static void descriptor_lock(VkDescriptorPool pool)
{
    if (pthread_mutex_trylock(&g_descriptorLock) != 0)
    {
        VkDescriptorPool held = g_descriptorHolder;
        int heldBy = g_descriptorHolderTid;
        if (held == pool)
        {
            long n = __sync_add_and_fetch(&g_samePoolWaits, 1);
            if (n <= 10 || n % 1000 == 0) LOGI("vulkan: two threads in descriptor pool %p at once (thread %d waits for %d; %ld times so far)", (void*)pool, gettid(), heldBy, n);
        }
        else
        {
            __sync_add_and_fetch(&g_otherPoolWaits, 1);
        }
        pthread_mutex_lock(&g_descriptorLock);
    }
    g_descriptorHolder = pool;
    g_descriptorHolderTid = gettid();
}

static void descriptor_unlock(void)
{
    g_descriptorHolder = VK_NULL_HANDLE;
    pthread_mutex_unlock(&g_descriptorLock);
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_AllocateDescriptorSets(VkDevice device, const VkDescriptorSetAllocateInfo* info, VkDescriptorSet* sets)
{
    descriptor_lock(info->descriptorPool);
    VkResult r = allocate_sets_unlocked(device, info, sets);
    descriptor_unlock();
    return r;
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_FreeDescriptorSets(VkDevice device, VkDescriptorPool pool, uint32_t count, const VkDescriptorSet* sets)
{
    descriptor_lock(pool);
    VkResult r = free_sets_unlocked(device, pool, count, sets);
    descriptor_unlock();
    return r;
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_ResetDescriptorPool(VkDevice device, VkDescriptorPool pool, VkDescriptorPoolResetFlags flags)
{
    descriptor_lock(pool);
    VkResult r = reset_pool_unlocked(device, pool, flags);
    descriptor_unlock();
    return r;
}

static VKAPI_ATTR void VKAPI_CALL hook_DestroyDescriptorPool(VkDevice device, VkDescriptorPool pool, const VkAllocationCallbacks* alloc)
{
    PoolChain* chain = (opt_pools && pool) ? find_chain(pool) : NULL;
    if (!chain)
    {
        real_DestroyDescriptorPool(device, pool, alloc);
        return;
    }
    pthread_mutex_lock(&g_poolLock);
    PoolChain** link = &g_poolChains[handle_hash((uint64_t)chain->key) % POOL_BUCKETS];
    while (*link && *link != chain) link = &(*link)->next;
    if (*link) *link = chain->next;
    pthread_mutex_unlock(&g_poolLock);
    if (chain->flags & VK_DESCRIPTOR_POOL_CREATE_FREE_DESCRIPTOR_SET_BIT) forget_sets_of(chain);
    for (int i = 1; i < chain->partCount; i++) real_DestroyDescriptorPool(device, chain->parts[i], NULL);
    real_DestroyDescriptorPool(device, chain->key, alloc);
    free(chain->parts);
    free(chain);
}

// For "memlog": what the engine has allocated through vkAllocateMemory, by handle (open addressing, never shrinks).
static int opt_memlog;
#define MEMLOG_SLOTS 65536
static struct { VkDeviceMemory memory; VkDeviceSize size; } g_memlog[MEMLOG_SLOTS];
static VkDeviceSize g_memlogTotal, g_memlogPeak;
static int g_memlogCount;
static pthread_mutex_t g_memlogLock = PTHREAD_MUTEX_INITIALIZER;

static void memlog_add(VkDeviceMemory memory, const VkMemoryAllocateInfo* info)
{
    const char* what = "";
    for (const VkBaseInStructure* next = (const VkBaseInStructure*)info->pNext; next; next = next->pNext)
    {
        if (next->sType == VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO)
        {
            const VkMemoryDedicatedAllocateInfo* dedicated = (const VkMemoryDedicatedAllocateInfo*)next;
            what = dedicated->image ? ", dedicated to an image" : dedicated->buffer ? ", dedicated to a buffer" : "";
        }
    }
    pthread_mutex_lock(&g_memlogLock);
    uint32_t slot = (uint32_t)(((uintptr_t)memory >> 4) * 2654435761u) % MEMLOG_SLOTS;
    for (int i = 0; i < MEMLOG_SLOTS && g_memlog[slot].memory; i++) slot = (slot + 1) % MEMLOG_SLOTS;
    if (!g_memlog[slot].memory)
    {
        g_memlog[slot].memory = memory;
        g_memlog[slot].size = info->allocationSize;
    }
    g_memlogTotal += info->allocationSize;
    g_memlogCount++;
    int newPeak = g_memlogTotal > g_memlogPeak + (256u << 20);
    if (g_memlogTotal > g_memlogPeak) { if (newPeak) g_memlogPeak = g_memlogTotal; }
    VkDeviceSize total = g_memlogTotal;
    int count = g_memlogCount;
    pthread_mutex_unlock(&g_memlogLock);
    if (info->allocationSize >= (32u << 20) || newPeak)
    {
        LOGI("vulkan: memory +%.1f MiB (type %u%s); the engine holds %.0f MiB in %d allocations", info->allocationSize / 1048576.0, info->memoryTypeIndex, what, total / 1048576.0, count);
    }
}

static void memlog_remove(VkDeviceMemory memory)
{
    VkDeviceSize size = 0, total;
    pthread_mutex_lock(&g_memlogLock);
    uint32_t slot = (uint32_t)(((uintptr_t)memory >> 4) * 2654435761u) % MEMLOG_SLOTS;
    for (int i = 0; i < MEMLOG_SLOTS; i++, slot = (slot + 1) % MEMLOG_SLOTS)
    {
        if (g_memlog[slot].memory == memory)
        {
            size = g_memlog[slot].size;
            // A tombstone (size 0, handle kept non-null) would do for correctness; handles are reused by the driver, so the slot is cleared and the probe chain after it re-inserted.
            g_memlog[slot].memory = VK_NULL_HANDLE;
            g_memlog[slot].size = 0;
            uint32_t next = (slot + 1) % MEMLOG_SLOTS;
            while (g_memlog[next].memory)
            {
                VkDeviceMemory m = g_memlog[next].memory;
                VkDeviceSize sz = g_memlog[next].size;
                g_memlog[next].memory = VK_NULL_HANDLE;
                g_memlog[next].size = 0;
                uint32_t to = (uint32_t)(((uintptr_t)m >> 4) * 2654435761u) % MEMLOG_SLOTS;
                while (g_memlog[to].memory) to = (to + 1) % MEMLOG_SLOTS;
                g_memlog[to].memory = m;
                g_memlog[to].size = sz;
                next = (next + 1) % MEMLOG_SLOTS;
            }
            break;
        }
        if (!g_memlog[slot].memory) break;
    }
    g_memlogTotal -= size;
    if (size) g_memlogCount--;
    total = g_memlogTotal;
    int count = g_memlogCount;
    pthread_mutex_unlock(&g_memlogLock);
    if (size >= (32u << 20))
    {
        LOGI("vulkan: memory -%.1f MiB; the engine holds %.0f MiB in %d allocations", size / 1048576.0, total / 1048576.0, count);
    }
}

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
        else if (strcmp(tok, "memlog") == 0) opt_memlog = v;
        else if (strcmp(tok, "pools") == 0) opt_pools = v;
        else if (strcmp(tok, "debugutils") == 0) opt_noDebugUtils = !v;
        else if (strcmp(tok, "debugtest") == 0) opt_debugTest = v;
        else if (strcmp(tok, "dropdebug") == 0) opt_dropDebugTest = v;
        else if (strcmp(tok, "barriers") == 0) opt_barriers = v;
        else if (strcmp(tok, "depth24") == 0) opt_depth24 = v;
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

// ---- the window going away and coming back (Home, lock screen) ----
// Android destroys the activity's window in the background and makes a new one afterwards; the engine's surface
// (and swapchain) belong to the dead window and presenting to them shows nothing. The GLFW shim reports the new
// window here; from then on acquires and presents on the swapchain of the old surface answer SURFACE_LOST, which
// the engine handles by destroying the surface, asking GLFW for a new one and rebuilding the swapchain.
static VkSurfaceKHR g_lastSwapchainSurface, g_staleSurface;
static struct { VkSwapchainKHR swapchain; VkSurfaceKHR surface; } g_swapchainSurfaces[8];
static PFN_vkDestroySurfaceKHR real_DestroySurface;

// A swapchain's handle is an address: once a swapchain is destroyed, a new one can come back with the same handle.
// The entry of a destroyed swapchain is dropped, and a handle already listed is updated, not listed twice; otherwise
// the lookup below found the old entry with its old surface first, took the swapchain on the dead window for a good
// one, and the engine went on presenting to a window that was gone: a black screen after switching back to the game
// (now and then: when a handle came back; seen on a OnePlus, Adreno 825, and reported on an Adreno 830).
static void remember_swapchain(VkSwapchainKHR swapchain, VkSurfaceKHR surface)
{
    for (int i = 0; i < 8; i++) if (g_swapchainSurfaces[i].swapchain == swapchain) { g_swapchainSurfaces[i].surface = surface; return; }
    for (int i = 0; i < 8; i++) if (!g_swapchainSurfaces[i].swapchain) { g_swapchainSurfaces[i].swapchain = swapchain; g_swapchainSurfaces[i].surface = surface; return; }
    static int next;
    g_swapchainSurfaces[next].swapchain = swapchain;
    g_swapchainSurfaces[next].surface = surface;
    next = (next + 1) % 8;
}

static PFN_vkDestroySwapchainKHR real_DestroySwapchain;

static VKAPI_ATTR void VKAPI_CALL hook_DestroySwapchain(VkDevice device, VkSwapchainKHR swapchain, const VkAllocationCallbacks* alloc)
{
    for (int i = 0; i < 8; i++) if (swapchain && g_swapchainSurfaces[i].swapchain == swapchain) { g_swapchainSurfaces[i].swapchain = VK_NULL_HANDLE; g_swapchainSurfaces[i].surface = VK_NULL_HANDLE; }
    real_DestroySwapchain(device, swapchain, alloc);
}

static int swapchain_is_stale(VkSwapchainKHR swapchain)
{
    if (!g_staleSurface) return 0;
    for (int i = 0; i < 8; i++) if (g_swapchainSurfaces[i].swapchain == swapchain) return g_swapchainSurfaces[i].surface == g_staleSurface;
    return 0;
}

API void port17_vulkan_window_changed(void)
{
    if (g_lastSwapchainSurface)
    {
        g_staleSurface = g_lastSwapchainSurface;
        LOGI("vulkan: the window changed; surface %p is stale, the engine will be told SURFACE_LOST", (void*)g_staleSurface);
    }
}

// The engine makes the new surface before it destroys the old one (WindowRenderContext.ReplaceSurface). When Android
// hands back the same window and the old surface still holds it, the new one fails with NATIVE_WINDOW_IN_USE (seen
// on a Redmi K80 Ultra, HyperOS, 2.5 s after the start): the GLFW shim then has the old surface released here and
// tries again; the engine's own destroy of that handle afterwards is skipped (it may even be the new surface's
// handle by then, the loader reusing the freed memory).
static VkSurfaceKHR g_releasedSurfaces[4];

API void port17_vulkan_release_surface(VkInstance instance, VkSurfaceKHR surface, const VkAllocationCallbacks* alloc)
{
    if (!surface || !real_DestroySurface) return;
    if (surface == g_staleSurface) g_staleSurface = VK_NULL_HANDLE;
    if (surface == g_lastSwapchainSurface) g_lastSwapchainSurface = VK_NULL_HANDLE;
    for (int i = 0; i < 4; i++) if (!g_releasedSurfaces[i]) { g_releasedSurfaces[i] = surface; break; }
    LOGI("vulkan: surface %p released early: its window is wanted for a new surface", (void*)surface);
    real_DestroySurface(instance, surface, alloc);
}

static VKAPI_ATTR void VKAPI_CALL hook_DestroySurface(VkInstance instance, VkSurfaceKHR surface, const VkAllocationCallbacks* alloc)
{
    for (int i = 0; i < 4; i++)
    {
        if (surface && g_releasedSurfaces[i] == surface)
        {
            g_releasedSurfaces[i] = VK_NULL_HANDLE;
            LOGI("vulkan: destroy of the early released surface %p skipped", (void*)surface);
            return;
        }
    }
    if (surface && surface == g_staleSurface) { LOGI("vulkan: stale surface %p destroyed", (void*)surface); g_staleSurface = VK_NULL_HANDLE; }
    if (surface == g_lastSwapchainSurface) g_lastSwapchainSurface = VK_NULL_HANDLE;
    real_DestroySurface(instance, surface, alloc);
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_CreateSwapchain(VkDevice device, const VkSwapchainCreateInfoKHR* info, const VkAllocationCallbacks* alloc, VkSwapchainKHR* out)
{
    VkResult r = real_CreateSwapchain(device, info, alloc, out);
    if (r == VK_SUCCESS) { remember_swapchain(*out, info->surface); g_lastSwapchainSurface = info->surface; }
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
    if (swapchain_is_stale(swapchain)) { static int n; if (n++ < 3) LOGI("vulkan: acquire on the stale surface: SURFACE_LOST"); return VK_ERROR_SURFACE_LOST_KHR; }
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
    if (info->swapchainCount > 0 && swapchain_is_stale(info->pSwapchains[0]))
    {
        for (uint32_t i = 0; info->pResults && i < info->swapchainCount; i++) info->pResults[i] = VK_ERROR_SURFACE_LOST_KHR;
        LOGI("vulkan: present on the stale surface: SURFACE_LOST");
        return VK_ERROR_SURFACE_LOST_KHR;
    }
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
static VKAPI_ATTR void VKAPI_CALL hook_CmdBlitImage(VkCommandBuffer cb, VkImage s, VkImageLayout sl, VkImage d, VkImageLayout dl, uint32_t n, const VkImageBlit* r, VkFilter f) { f_blits++; if (!s || !d) { note_null_image("vkCmdBlitImage"); return; } real_CmdBlitImage(cb, s, sl, d, dl, n, r, f); }
static VKAPI_ATTR void VKAPI_CALL hook_CmdCopyImage(VkCommandBuffer cb, VkImage s, VkImageLayout sl, VkImage d, VkImageLayout dl, uint32_t n, const VkImageCopy* r) { f_copies++; if (!s || !d) { note_null_image("vkCmdCopyImage"); return; } real_CmdCopyImage(cb, s, sl, d, dl, n, r); }

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
    // The engine's own structures, changed for the call and put back after it (the depth format above).
    VkPipelineRenderingCreateInfo* changed[16];
    VkFormat was[16][2];
    int changes = 0;
    for (uint32_t i = 0; g_depthTo != VK_FORMAT_UNDEFINED && infos && i < count && changes < 16; i++)
    {
        for (VkBaseOutStructure* next = (VkBaseOutStructure*)infos[i].pNext; next && changes < 16; next = next->pNext)
        {
            if (next->sType != VK_STRUCTURE_TYPE_PIPELINE_RENDERING_CREATE_INFO) continue;
            VkPipelineRenderingCreateInfo* rendering = (VkPipelineRenderingCreateInfo*)next;
            if (rendering->depthAttachmentFormat != g_depthFrom && rendering->stencilAttachmentFormat != g_depthFrom) continue;
            changed[changes] = rendering;
            was[changes][0] = rendering->depthAttachmentFormat;
            was[changes][1] = rendering->stencilAttachmentFormat;
            rendering->depthAttachmentFormat = depth_format(rendering->depthAttachmentFormat);
            rendering->stencilAttachmentFormat = depth_format(rendering->stencilAttachmentFormat);
            changes++;
        }
    }
    VkResult r = real_CreateGraphicsPipelines(device, cache, count, infos, alloc, out);
    for (int i = 0; i < changes; i++)
    {
        changed[i]->depthAttachmentFormat = was[i][0];
        changed[i]->stencilAttachmentFormat = was[i][1];
    }
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
    if (opt_memlog)
    {
        if (r == VK_SUCCESS) memlog_add(*out, info);
        else LOGW("vulkan: memory of %.1f MiB (type %u) refused: %d", info->allocationSize / 1048576.0, info->memoryTypeIndex, r);
    }
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
    if (opt_memlog && memory) memlog_remove(memory);
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
// Whether the driver (with the system's loader) offers an instance extension.
static int instance_extension_available(const char* name)
{
    if (opt_dropDebugTest && strcmp(name, "VK_EXT_debug_utils") == 0) return 0;
    PFN_vkEnumerateInstanceExtensionProperties enumerate = (PFN_vkEnumerateInstanceExtensionProperties)g_gipa(NULL, "vkEnumerateInstanceExtensionProperties");
    uint32_t n = 0;
    if (!enumerate || enumerate(NULL, &n, NULL) != VK_SUCCESS) return 1;
    VkExtensionProperties* all = (VkExtensionProperties*)calloc(n ? n : 1, sizeof *all);
    int found = 0;
    if (enumerate(NULL, &n, all) >= 0)
    {
        for (uint32_t i = 0; i < n && !found; i++) found = strcmp(all[i].extensionName, name) == 0;
    }
    free(all);
    return found;
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_CreateInstance(const VkInstanceCreateInfo* info, const VkAllocationCallbacks* alloc, VkInstance* out)
{
    // The debug extensions are not needed to run: one the driver does not have is left out instead of failing the whole
    // instance. 1.7.4976's engine asks for VK_EXT_debug_report (4985's does not), which a vivo's Mali driver (Dimensity
    // 8200, Android 13) does not have: VK_ERROR_EXTENSION_NOT_PRESENT, and the game closed a second after it started.
    VkInstanceCreateInfo filtered = *info;
    const char* kept[64];
    if (info->enabledExtensionCount <= 64)
    {
        uint32_t keptCount = 0;
        for (uint32_t i = 0; i < info->enabledExtensionCount; i++)
        {
            const char* name = info->ppEnabledExtensionNames[i];
            int report = strcmp(name, "VK_EXT_debug_report") == 0, utils = strcmp(name, "VK_EXT_debug_utils") == 0;
            if ((report || utils) && !instance_extension_available(name))
            {
                if (report) g_droppedDebugReport = 1;
                if (utils) g_droppedDebugUtils = 1;
                LOGK("vulkan: the driver has no %s: left out of the instance, its functions do nothing", name);
                continue;
            }
            kept[keptCount++] = name;
        }
        filtered.enabledExtensionCount = keptCount;
        filtered.ppEnabledExtensionNames = kept;
    }
    VkResult r = real_CreateInstance(&filtered, alloc, out);
    if (r == VK_SUCCESS) g_instance = *out;
    char ext[1024] = "";
    for (uint32_t i = 0; i < filtered.enabledExtensionCount && strlen(ext) < 900; i++)
    {
        strncat(ext, filtered.ppEnabledExtensionNames[i], sizeof ext - strlen(ext) - 2);
        strncat(ext, " ", sizeof ext - strlen(ext) - 1);
    }
    char layers[512] = "";
    for (uint32_t i = 0; i < info->enabledLayerCount && strlen(layers) < 400; i++)
    {
        strncat(layers, info->ppEnabledLayerNames[i], sizeof layers - strlen(layers) - 2);
        strncat(layers, " ", sizeof layers - strlen(layers) - 1);
    }
    LOGK("vulkan: instance created (%s), api 0x%x, extensions: %s; layers: %s", result_name(r), info->pApplicationInfo ? info->pApplicationInfo->apiVersion : 0u, ext, layers[0] ? layers : "none");
    return r;
}

// ---- Mali: a full barrier at the start of every primary command buffer ----
// The engine renders pawns and other things into shared atlas textures (CameraTextureBaker, its own command buffer
// and submission, waited for with a fence) and the interface samples those atlases in the frame's command buffers,
// with no barrier between the two submissions. Adreno gets away with it; on Mali (a tablet with the Xring O1 /
// Immortalis-G925) the icons of pawns and animals in lists showed other sprites or nothing, different ones every frame:
// the frame read stale or half-written atlas contents. A memory barrier from all earlier commands to all later ones
// at the start of each primary command buffer orders every submission after the ones before it and makes their
// writes visible. vulkan.txt: barriers=1 always, barriers=0 never.
static int g_barriers;
static long g_barriersAdded;
static PFN_vkBeginCommandBuffer real_BeginCommandBuffer;
static PFN_vkCmdPipelineBarrier g_cmdPipelineBarrier;

static VKAPI_ATTR VkResult VKAPI_CALL hook_BeginCommandBuffer(VkCommandBuffer cb, const VkCommandBufferBeginInfo* info)
{
    VkResult r = real_BeginCommandBuffer(cb, info);
    // (The engine asks for vkGetDeviceProcAddr only after it made the device: the barrier function is taken when the
    // engine looks it up, see device_hook, or here.)
    if (g_barriers && !g_cmdPipelineBarrier && g_gdpa && g_device) g_cmdPipelineBarrier = (PFN_vkCmdPipelineBarrier)g_gdpa(g_device, "vkCmdPipelineBarrier");
    // Primary command buffers only: a secondary one continuing a render pass cannot take a barrier like this.
    if (r == VK_SUCCESS && g_barriers && g_cmdPipelineBarrier && info && !info->pInheritanceInfo && !(info->flags & VK_COMMAND_BUFFER_USAGE_RENDER_PASS_CONTINUE_BIT))
    {
        VkMemoryBarrier barrier;
        memset(&barrier, 0, sizeof barrier);
        barrier.sType = VK_STRUCTURE_TYPE_MEMORY_BARRIER;
        barrier.srcAccessMask = VK_ACCESS_MEMORY_WRITE_BIT;
        barrier.dstAccessMask = VK_ACCESS_MEMORY_READ_BIT | VK_ACCESS_MEMORY_WRITE_BIT;
        g_cmdPipelineBarrier(cb, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, 0, 1, &barrier, 0, NULL, 0, NULL);
        long n = __sync_add_and_fetch(&g_barriersAdded, 1);
        if (n == 1 || n % 10000 == 0) LOGI("vulkan: %ld command buffers opened with a full barrier", n);
    }
    return r;
}

// ---- depth format ----
// The engine makes every depth buffer D32_SFLOAT_S8_UINT (EngineApplicationOptions.DepthFormat, the window's depth with
// SAMPLED | DEPTH_STENCIL_ATTACHMENT, optimal tiling) without asking whether the driver can. A tester's Mali-G57 MC3
// (Dimensity 720, Vulkan 1.1.117, driver r21p0, 2026-10-24) refused it: "VMA failed to allocate image
// 'RenderContext.Depth[0]' ... ErrorInitializationFailed", and the game closed before its first frame. Where the driver
// lacks either use for that format, it becomes D24_UNORM_S8_UINT (24-bit depth, the same 8-bit stencil) everywhere it
// is named: images, views, render passes, and the formats a pipeline for dynamic rendering is made for.
static VkFormat g_depthFrom = VK_FORMAT_UNDEFINED, g_depthTo = VK_FORMAT_UNDEFINED;
static PFN_vkCreateImage real_CreateImage;
static PFN_vkCreateImageView real_CreateImageView;
static PFN_vkCreateRenderPass real_CreateRenderPass;
static PFN_vkCreateRenderPass2 real_CreateRenderPass2;
static long g_depthImages;

static void choose_depth_format(VkPhysicalDevice pd)
{
    PFN_vkGetPhysicalDeviceFormatProperties getFormat = (PFN_vkGetPhysicalDeviceFormatProperties)g_gipa(g_instance, "vkGetPhysicalDeviceFormatProperties");
    if (!getFormat) return;
    VkFormatProperties d32, d24;
    memset(&d32, 0, sizeof d32);
    memset(&d24, 0, sizeof d24);
    getFormat(pd, VK_FORMAT_D32_SFLOAT_S8_UINT, &d32);
    getFormat(pd, VK_FORMAT_D24_UNORM_S8_UINT, &d24);
    const VkFormatFeatureFlags need = VK_FORMAT_FEATURE_DEPTH_STENCIL_ATTACHMENT_BIT | VK_FORMAT_FEATURE_SAMPLED_IMAGE_BIT;
    int can32 = (d32.optimalTilingFeatures & need) == need, can24 = (d24.optimalTilingFeatures & need) == need;
    int swap = can24 && (opt_depth24 == 1 || (opt_depth24 < 0 && !can32));
    if (swap)
    {
        g_depthFrom = VK_FORMAT_D32_SFLOAT_S8_UINT;
        g_depthTo = VK_FORMAT_D24_UNORM_S8_UINT;
    }
    LOGK("vulkan: depth D32_SFLOAT_S8_UINT features 0x%x, D24_UNORM_S8_UINT 0x%x: %s", d32.optimalTilingFeatures, d24.optimalTilingFeatures,
         swap ? "the engine's D32_SFLOAT_S8_UINT is made D24_UNORM_S8_UINT" : can32 ? "as the engine asks" : "as the engine asks (no format with both uses)");
}

static VkFormat depth_format(VkFormat format)
{
    return g_depthTo != VK_FORMAT_UNDEFINED && format == g_depthFrom ? g_depthTo : format;
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_CreateImage(VkDevice device, const VkImageCreateInfo* info, const VkAllocationCallbacks* alloc, VkImage* out)
{
    if (info && g_depthTo != VK_FORMAT_UNDEFINED && info->format == g_depthFrom)
    {
        VkImageCreateInfo copy = *info;
        copy.format = g_depthTo;
        if (g_depthImages++ < 3) LOGK("vulkan: a %ux%u depth image made D24_UNORM_S8_UINT", info->extent.width, info->extent.height);
        return real_CreateImage(device, &copy, alloc, out);
    }
    return real_CreateImage(device, info, alloc, out);
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_CreateImageView(VkDevice device, const VkImageViewCreateInfo* info, const VkAllocationCallbacks* alloc, VkImageView* out)
{
    if (info && g_depthTo != VK_FORMAT_UNDEFINED && info->format == g_depthFrom)
    {
        VkImageViewCreateInfo copy = *info;
        copy.format = g_depthTo;
        return real_CreateImageView(device, &copy, alloc, out);
    }
    return real_CreateImageView(device, info, alloc, out);
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_CreateRenderPass(VkDevice device, const VkRenderPassCreateInfo* info, const VkAllocationCallbacks* alloc, VkRenderPass* out)
{
    if (info && g_depthTo != VK_FORMAT_UNDEFINED && info->attachmentCount > 0 && info->attachmentCount <= 32)
    {
        VkAttachmentDescription attachments[32];
        int changed = 0;
        for (uint32_t i = 0; i < info->attachmentCount; i++)
        {
            attachments[i] = info->pAttachments[i];
            if (attachments[i].format == g_depthFrom) { attachments[i].format = g_depthTo; changed = 1; }
        }
        if (changed)
        {
            VkRenderPassCreateInfo copy = *info;
            copy.pAttachments = attachments;
            return real_CreateRenderPass(device, &copy, alloc, out);
        }
    }
    return real_CreateRenderPass(device, info, alloc, out);
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_CreateRenderPass2(VkDevice device, const VkRenderPassCreateInfo2* info, const VkAllocationCallbacks* alloc, VkRenderPass* out)
{
    if (info && g_depthTo != VK_FORMAT_UNDEFINED && info->attachmentCount > 0 && info->attachmentCount <= 32)
    {
        VkAttachmentDescription2 attachments[32];
        int changed = 0;
        for (uint32_t i = 0; i < info->attachmentCount; i++)
        {
            attachments[i] = info->pAttachments[i];
            if (attachments[i].format == g_depthFrom) { attachments[i].format = g_depthTo; changed = 1; }
        }
        if (changed)
        {
            VkRenderPassCreateInfo2 copy = *info;
            copy.pAttachments = attachments;
            return real_CreateRenderPass2(device, &copy, alloc, out);
        }
    }
    return real_CreateRenderPass2(device, info, alloc, out);
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
            g_cmdPipelineBarrier = (PFN_vkCmdPipelineBarrier)g_gdpa(g_device, "vkCmdPipelineBarrier");
        }
        VkPhysicalDeviceProperties props;
        memset(&props, 0, sizeof props);
        PFN_vkGetPhysicalDeviceProperties getProps = (PFN_vkGetPhysicalDeviceProperties)g_gipa(g_instance, "vkGetPhysicalDeviceProperties");
        if (getProps) getProps(pd, &props);
        g_barriers = opt_barriers >= 0 ? opt_barriers : props.vendorID == 0x13B5;
        LOGK("vulkan: GPU \"%s\" (vendor 0x%x): %s", props.deviceName, props.vendorID,
             g_barriers ? "every command buffer opens with a full barrier" : "command buffers as the engine records them");
        choose_depth_format(pd);
    }
    char ext[2048] = "";
    for (uint32_t i = 0; i < info->enabledExtensionCount && strlen(ext) < 1900; i++)
    {
        strncat(ext, info->ppEnabledExtensionNames[i], sizeof ext - strlen(ext) - 2);
        strncat(ext, " ", sizeof ext - strlen(ext) - 1);
    }
    LOGK("vulkan: device created (%s), extensions: %s", result_name(r), ext);
    return r;
}

// The aliases below share the typedef names: map them for the HOOK macro.
#define real_DestroySwapchainKHR real_DestroySwapchain
#define hook_DestroySwapchainKHR hook_DestroySwapchain
#define real_CreateSwapchainKHR real_CreateSwapchain
#define hook_CreateSwapchainKHR hook_CreateSwapchain
#define real_AcquireNextImageKHR real_AcquireNextImage
#define hook_AcquireNextImageKHR hook_AcquireNextImage
#define real_QueuePresentKHR real_QueuePresent
#define hook_QueuePresentKHR hook_QueuePresent

// Device-level functions we stand in for; looked up through either vkGetDeviceProcAddr or vkGetInstanceProcAddr.
// ---- no image, no command ----
// A copy, blit or layout change of an image whose handle is VK_NULL_HANDLE crashes Adreno's driver (a Xiaomi with the
// Snapdragon 8 Elite, 2026-10-23: SIGSEGV at 0x140 inside vkCmdBlitImage, three hours into a game, a second after a
// caravan's new map was made): a texture was let go (evicted, released) while a command for it was still to be
// recorded. Such a command is left out here, and the first ones are noted (stderr: the crash report has it). The copy
// that does not happen costs at worst a texture that looks wrong for a while.
static int g_nullImageCommands;

static void note_null_image(const char* what)
{
    if (g_nullImageCommands++ < 20) LOGK("vulkan: %s with no image left out (%d so far)", what, g_nullImageCommands);
}

static PFN_vkCmdBlitImage2 real_CmdBlitImage2;
static VKAPI_ATTR void VKAPI_CALL hook_CmdBlitImage2(VkCommandBuffer cb, const VkBlitImageInfo2* info)
{
    if (!info || info->srcImage == VK_NULL_HANDLE || info->dstImage == VK_NULL_HANDLE) { note_null_image("vkCmdBlitImage2"); return; }
    real_CmdBlitImage2(cb, info);
}

static PFN_vkCmdCopyBufferToImage real_CmdCopyBufferToImage;
static VKAPI_ATTR void VKAPI_CALL hook_CmdCopyBufferToImage(VkCommandBuffer cb, VkBuffer buffer, VkImage dst, VkImageLayout layout, uint32_t count, const VkBufferImageCopy* regions)
{
    if (dst == VK_NULL_HANDLE) { note_null_image("vkCmdCopyBufferToImage"); return; }
    real_CmdCopyBufferToImage(cb, buffer, dst, layout, count, regions);
}

static PFN_vkCmdCopyImageToBuffer real_CmdCopyImageToBuffer;
static VKAPI_ATTR void VKAPI_CALL hook_CmdCopyImageToBuffer(VkCommandBuffer cb, VkImage src, VkImageLayout layout, VkBuffer buffer, uint32_t count, const VkBufferImageCopy* regions)
{
    if (src == VK_NULL_HANDLE) { note_null_image("vkCmdCopyImageToBuffer"); return; }
    real_CmdCopyImageToBuffer(cb, src, layout, buffer, count, regions);
}

// Barriers: only the image barriers without an image go (the rest of the call stays).
static PFN_vkCmdPipelineBarrier real_CmdPipelineBarrier;
static VKAPI_ATTR void VKAPI_CALL hook_CmdPipelineBarrier(VkCommandBuffer cb, VkPipelineStageFlags srcStage, VkPipelineStageFlags dstStage, VkDependencyFlags flags, uint32_t memoryCount, const VkMemoryBarrier* memory, uint32_t bufferCount, const VkBufferMemoryBarrier* buffers, uint32_t imageCount, const VkImageMemoryBarrier* images)
{
    uint32_t i = 0;
    while (i < imageCount && images[i].image != VK_NULL_HANDLE) i++;
    if (i == imageCount)
    {
        real_CmdPipelineBarrier(cb, srcStage, dstStage, flags, memoryCount, memory, bufferCount, buffers, imageCount, images);
        return;
    }
    VkImageMemoryBarrier kept[64];
    uint32_t n = 0;
    for (uint32_t j = 0; j < imageCount && n < 64; j++) if (images[j].image != VK_NULL_HANDLE) kept[n++] = images[j];
    note_null_image("vkCmdPipelineBarrier");
    if (memoryCount || bufferCount || n) real_CmdPipelineBarrier(cb, srcStage, dstStage, flags, memoryCount, memory, bufferCount, buffers, n, kept);
}

static PFN_vkCmdPipelineBarrier2 real_CmdPipelineBarrier2;
static VKAPI_ATTR void VKAPI_CALL hook_CmdPipelineBarrier2(VkCommandBuffer cb, const VkDependencyInfo* info)
{
    uint32_t i = 0;
    while (info && i < info->imageMemoryBarrierCount && info->pImageMemoryBarriers[i].image != VK_NULL_HANDLE) i++;
    if (!info || i == info->imageMemoryBarrierCount)
    {
        real_CmdPipelineBarrier2(cb, info);
        return;
    }
    VkImageMemoryBarrier2 kept[64];
    uint32_t n = 0;
    for (uint32_t j = 0; j < info->imageMemoryBarrierCount && n < 64; j++) if (info->pImageMemoryBarriers[j].image != VK_NULL_HANDLE) kept[n++] = info->pImageMemoryBarriers[j];
    note_null_image("vkCmdPipelineBarrier2");
    VkDependencyInfo copy = *info;
    copy.imageMemoryBarrierCount = n;
    copy.pImageMemoryBarriers = kept;
    if (copy.memoryBarrierCount || copy.bufferMemoryBarrierCount || n) real_CmdPipelineBarrier2(cb, &copy);
}

static PFN_vkVoidFunction device_hook(const char* name, PFN_vkVoidFunction real)
{
#define HOOK(n) if (strcmp(name, "vk" #n) == 0) { real_##n = (PFN_vk##n)real; return (PFN_vkVoidFunction)hook_##n; }
    HOOK(CreateSwapchainKHR)
    HOOK(DestroySwapchainKHR)
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
    HOOK(CreateDescriptorPool)
    HOOK(DestroyDescriptorPool)
    HOOK(ResetDescriptorPool)
    HOOK(AllocateDescriptorSets)
    HOOK(FreeDescriptorSets)
    HOOK(FreeMemory)
    HOOK(MapMemory)
    HOOK(UnmapMemory)
    HOOK(QueueSubmit)
    HOOK(CmdCopyBufferToImage)
    HOOK(CmdCopyImageToBuffer)
    HOOK(QueueSubmit2)
    HOOK(BeginCommandBuffer)
    HOOK(WaitForFences)
    HOOK(CreateImage)
    HOOK(CreateImageView)
    HOOK(CreateRenderPass)
    HOOK(CreateRenderPass2)
#undef HOOK
    // The real barrier stays at hand for the Mali barrier at the start of a command buffer (hook_BeginCommandBuffer).
    if (strcmp(name, "vkCmdPipelineBarrier") == 0) { g_cmdPipelineBarrier = real_CmdPipelineBarrier = (PFN_vkCmdPipelineBarrier)real; return (PFN_vkVoidFunction)hook_CmdPipelineBarrier; }
    if (strcmp(name, "vkCmdPipelineBarrier2") == 0 || strcmp(name, "vkCmdPipelineBarrier2KHR") == 0) { real_CmdPipelineBarrier2 = (PFN_vkCmdPipelineBarrier2)real; return (PFN_vkVoidFunction)hook_CmdPipelineBarrier2; }
    if (strcmp(name, "vkCmdBlitImage2") == 0 || strcmp(name, "vkCmdBlitImage2KHR") == 0) { real_CmdBlitImage2 = (PFN_vkCmdBlitImage2)real; return (PFN_vkVoidFunction)hook_CmdBlitImage2; }
    if (strcmp(name, "vkCmdBeginRenderPass2KHR") == 0) { real_CmdBeginRenderPass2 = (PFN_vkCmdBeginRenderPass2)real; return (PFN_vkVoidFunction)hook_CmdBeginRenderPass2; }
    if (strcmp(name, "vkCreateRenderPass2KHR") == 0) { real_CreateRenderPass2 = (PFN_vkCreateRenderPass2)real; return (PFN_vkVoidFunction)hook_CreateRenderPass2; }
    if (strcmp(name, "vkCmdBeginRenderingKHR") == 0) { real_CmdBeginRendering = (PFN_vkCmdBeginRendering)real; return (PFN_vkVoidFunction)hook_CmdBeginRendering; }
    if (strcmp(name, "vkQueueSubmit2KHR") == 0) { real_QueueSubmit2 = (PFN_vkQueueSubmit2)real; return (PFN_vkVoidFunction)hook_QueueSubmit2; }
    return NULL;
}

// VK_EXT_debug_utils labels and names where the driver has no such extension (Adreno 650 with its 2021 driver, a
// Xiaomi Mi 10 Pro): the engine's release build still labels every render pass (FrameRenderer.BeginPassDebugLabel,
// compiled with DEV_OR_TESTER) without asking whether the extension is there, and calls the null pointer it was
// given: SIGSEGV at pc 0 on the RenderThread two seconds after the start. These do nothing instead.
static VKAPI_ATTR void VKAPI_CALL noop_CmdLabel(VkCommandBuffer cb, const VkDebugUtilsLabelEXT* label) { (void)cb; (void)label; }
static VKAPI_ATTR void VKAPI_CALL noop_CmdEndLabel(VkCommandBuffer cb) { (void)cb; }
static VKAPI_ATTR void VKAPI_CALL noop_QueueLabel(VkQueue queue, const VkDebugUtilsLabelEXT* label) { (void)queue; (void)label; }
static VKAPI_ATTR void VKAPI_CALL noop_QueueEndLabel(VkQueue queue) { (void)queue; }
static VKAPI_ATTR VkResult VKAPI_CALL noop_SetObjectName(VkDevice device, const VkDebugUtilsObjectNameInfoEXT* info) { (void)device; (void)info; return VK_SUCCESS; }
static VKAPI_ATTR VkResult VKAPI_CALL noop_SetObjectTag(VkDevice device, const VkDebugUtilsObjectTagInfoEXT* info) { (void)device; (void)info; return VK_SUCCESS; }

// The messenger the same way (a driver that names VK_EXT_debug_utils for the instance but hands out no function for it
// would otherwise get the engine to call address 0 as the graphics device is made).
static VKAPI_ATTR VkResult VKAPI_CALL noop_CreateMessenger(VkInstance instance, const VkDebugUtilsMessengerCreateInfoEXT* info, const VkAllocationCallbacks* alloc, VkDebugUtilsMessengerEXT* out)
{
    (void)instance; (void)info; (void)alloc;
    if (out) *out = (VkDebugUtilsMessengerEXT)(uintptr_t)1;
    return VK_SUCCESS;
}
static VKAPI_ATTR void VKAPI_CALL noop_DestroyMessenger(VkInstance instance, VkDebugUtilsMessengerEXT messenger, const VkAllocationCallbacks* alloc) { (void)instance; (void)messenger; (void)alloc; }
static VKAPI_ATTR void VKAPI_CALL noop_SubmitMessage(VkInstance instance, VkDebugUtilsMessageSeverityFlagBitsEXT severity, VkDebugUtilsMessageTypeFlagsEXT types, const VkDebugUtilsMessengerCallbackDataEXT* data) { (void)instance; (void)severity; (void)types; (void)data; }

// VK_EXT_debug_report (asked for by older engines, 1.7.4976) the same way.
static VKAPI_ATTR VkResult VKAPI_CALL noop_CreateReportCallback(VkInstance instance, const VkDebugReportCallbackCreateInfoEXT* info, const VkAllocationCallbacks* alloc, VkDebugReportCallbackEXT* out)
{
    (void)instance; (void)info; (void)alloc;
    if (out) *out = (VkDebugReportCallbackEXT)(uintptr_t)1;
    return VK_SUCCESS;
}
static VKAPI_ATTR void VKAPI_CALL noop_DestroyReportCallback(VkInstance instance, VkDebugReportCallbackEXT callback, const VkAllocationCallbacks* alloc) { (void)instance; (void)callback; (void)alloc; }
static VKAPI_ATTR void VKAPI_CALL noop_ReportMessage(VkInstance instance, VkDebugReportFlagsEXT flags, VkDebugReportObjectTypeEXT type, uint64_t object, size_t location, int32_t code, const char* layer, const char* message)
{
    (void)instance; (void)flags; (void)type; (void)object; (void)location; (void)code; (void)layer; (void)message;
}

static PFN_vkVoidFunction debug_utils_noop(const char* name)
{
    PFN_vkVoidFunction noop = NULL;
    if (strcmp(name, "vkCreateDebugUtilsMessengerEXT") == 0) noop = (PFN_vkVoidFunction)noop_CreateMessenger;
    else if (strcmp(name, "vkDestroyDebugUtilsMessengerEXT") == 0) noop = (PFN_vkVoidFunction)noop_DestroyMessenger;
    else if (strcmp(name, "vkSubmitDebugUtilsMessageEXT") == 0) noop = (PFN_vkVoidFunction)noop_SubmitMessage;
    else if (strcmp(name, "vkCmdBeginDebugUtilsLabelEXT") == 0 || strcmp(name, "vkCmdInsertDebugUtilsLabelEXT") == 0) noop = (PFN_vkVoidFunction)noop_CmdLabel;
    else if (strcmp(name, "vkCmdEndDebugUtilsLabelEXT") == 0) noop = (PFN_vkVoidFunction)noop_CmdEndLabel;
    else if (strcmp(name, "vkQueueBeginDebugUtilsLabelEXT") == 0 || strcmp(name, "vkQueueInsertDebugUtilsLabelEXT") == 0) noop = (PFN_vkVoidFunction)noop_QueueLabel;
    else if (strcmp(name, "vkQueueEndDebugUtilsLabelEXT") == 0) noop = (PFN_vkVoidFunction)noop_QueueEndLabel;
    else if (strcmp(name, "vkSetDebugUtilsObjectNameEXT") == 0) noop = (PFN_vkVoidFunction)noop_SetObjectName;
    else if (strcmp(name, "vkSetDebugUtilsObjectTagEXT") == 0) noop = (PFN_vkVoidFunction)noop_SetObjectTag;
    else if (strcmp(name, "vkCreateDebugReportCallbackEXT") == 0) noop = (PFN_vkVoidFunction)noop_CreateReportCallback;
    else if (strcmp(name, "vkDestroyDebugReportCallbackEXT") == 0) noop = (PFN_vkVoidFunction)noop_DestroyReportCallback;
    else if (strcmp(name, "vkDebugReportMessageEXT") == 0) noop = (PFN_vkVoidFunction)noop_ReportMessage;
    if (noop)
    {
        static int logged;
        if (logged++ < 12) LOGK("vulkan: %s is missing (no such debug extension); a stand-in that does nothing is used", name);
    }
    else
    {
        // Which call would crash at address 0 if the engine used it (most such lookups are for extensions it never calls).
        static int missing;
        if (missing++ < 2000) LOGK("vulkan: the driver has no %s (the engine gets none)", name);
    }
    return noop;
}

// What the driver says through VK_EXT_debug_utils, the engine reports as the game's errors (GraphicsDevice.
// DebugUtilsCallback, with two stack traces each, on the render thread). Qualcomm's drivers on Adreno 7xx also give
// their own performance advice that way, "The following warning was triggered: VKDBGUTILWARN003 ...", at the end of
// render passes: about 2000 a run on testers' realme (Adreno 725) and Redmi (Adreno 710) on 2026-10-22, and the log
// window opened at every start. Those are left out here (counted, a few logged); everything else reaches the engine.
typedef struct
{
    PFN_vkDebugUtilsMessengerCallbackEXT callback;
    void* userData;
} MessengerTarget;

static long g_driverAdviceLeftOut;

static VKAPI_ATTR VkBool32 VKAPI_CALL filter_DebugMessage(VkDebugUtilsMessageSeverityFlagBitsEXT severity, VkDebugUtilsMessageTypeFlagsEXT types, const VkDebugUtilsMessengerCallbackDataEXT* data, void* userData)
{
    const MessengerTarget* target = (const MessengerTarget*)userData;
    if (data && data->pMessage && strstr(data->pMessage, "VKDBGUTILWARN"))
    {
        long n = __atomic_add_fetch(&g_driverAdviceLeftOut, 1, __ATOMIC_RELAXED);
        if (n <= 3 || n % 1000 == 0) LOGI("vulkan: driver advice left out of the game's log (%ld so far): %.200s", n, data->pMessage);
        return VK_FALSE;
    }
    return target->callback(severity, types, data, target->userData);
}

static PFN_vkCreateDebugUtilsMessengerEXT real_CreateDebugUtilsMessenger;

// vulkan.txt "debugtest=1": one driver advice (to be left out) and one ordinary message (to reach the game's log).
static void send_test_messages(VkInstance instance)
{
    PFN_vkSubmitDebugUtilsMessageEXT submit = (PFN_vkSubmitDebugUtilsMessageEXT)g_gipa(instance, "vkSubmitDebugUtilsMessageEXT");
    if (!submit) return;
    VkDebugUtilsMessengerCallbackDataEXT data = { VK_STRUCTURE_TYPE_DEBUG_UTILS_MESSENGER_CALLBACK_DATA_EXT };
    data.pMessage = "The following warning was triggered: VKDBGUTILWARN003. (port17 debugtest: must not reach the game's log)";
    submit(instance, VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT, VK_DEBUG_UTILS_MESSAGE_TYPE_VALIDATION_BIT_EXT, &data);
    data.pMessage = "port17 debugtest: an ordinary message, to reach the game's log";
    submit(instance, VK_DEBUG_UTILS_MESSAGE_SEVERITY_ERROR_BIT_EXT, VK_DEBUG_UTILS_MESSAGE_TYPE_GENERAL_BIT_EXT, &data);
    LOGI("vulkan: debugtest messages sent");
}

static VKAPI_ATTR VkResult VKAPI_CALL hook_CreateDebugUtilsMessenger(VkInstance instance, const VkDebugUtilsMessengerCreateInfoEXT* info, const VkAllocationCallbacks* alloc, VkDebugUtilsMessengerEXT* out)
{
    // Kept for the messenger's life (the engine makes one, and destroys it only with the instance).
    if (!real_CreateDebugUtilsMessenger) return noop_CreateMessenger(instance, info, alloc, out);
    MessengerTarget* target = info && info->pfnUserCallback ? (MessengerTarget*)malloc(sizeof *target) : NULL;
    if (!target) return real_CreateDebugUtilsMessenger(instance, info, alloc, out);
    target->callback = info->pfnUserCallback;
    target->userData = info->pUserData;
    VkDebugUtilsMessengerCreateInfoEXT wrapped = *info;
    wrapped.pfnUserCallback = filter_DebugMessage;
    wrapped.pUserData = target;
    VkResult r = real_CreateDebugUtilsMessenger(instance, &wrapped, alloc, out);
    if (r != VK_SUCCESS)
    {
        free(target);
        return r;
    }
    if (opt_debugTest) send_test_messages(instance);
    return r;
}

static PFN_vkEnumerateInstanceExtensionProperties real_EnumerateInstanceExtensions;

static VKAPI_ATTR VkResult VKAPI_CALL hook_EnumerateInstanceExtensions(const char* layer, uint32_t* count, VkExtensionProperties* props)
{
    if (!opt_noDebugUtils || layer) return real_EnumerateInstanceExtensions(layer, count, props);
    uint32_t n = 0;
    VkResult r = real_EnumerateInstanceExtensions(NULL, &n, NULL);
    if (r != VK_SUCCESS) return r;
    VkExtensionProperties* all = (VkExtensionProperties*)calloc(n ? n : 1, sizeof *all);
    r = real_EnumerateInstanceExtensions(NULL, &n, all);
    uint32_t kept = 0;
    for (uint32_t i = 0; i < n; i++) if (strcmp(all[i].extensionName, "VK_EXT_debug_utils") != 0) all[kept++] = all[i];
    if (!props)
    {
        *count = kept;
        free(all);
        return VK_SUCCESS;
    }
    uint32_t copied = *count < kept ? *count : kept;
    memcpy(props, all, copied * sizeof *all);
    *count = copied;
    free(all);
    return copied < kept ? VK_INCOMPLETE : VK_SUCCESS;
}

static VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL hook_GetDeviceProcAddr(VkDevice device, const char* name)
{
    if (g_droppedDebugUtils && strstr(name, "DebugUtils")) return debug_utils_noop(name);
    PFN_vkVoidFunction real = g_gdpa(device, name);
    if (!real) return debug_utils_noop(name);
    PFN_vkVoidFunction hook = device_hook(name, real);
    return hook ? hook : real;
}

API VKAPI_ATTR PFN_vkVoidFunction VKAPI_CALL vkGetInstanceProcAddr(VkInstance instance, const char* name)
{
    if (!ensure() || !name) return NULL;
    if (instance && ((g_droppedDebugUtils && strstr(name, "DebugUtils")) || (g_droppedDebugReport && strstr(name, "DebugReport")))) return debug_utils_noop(name);
    PFN_vkVoidFunction real = g_gipa(instance, name);
    if (!real) return instance ? debug_utils_noop(name) : NULL;
    if (instance && !g_instance) g_instance = instance;
    if (strcmp(name, "vkGetInstanceProcAddr") == 0) return (PFN_vkVoidFunction)vkGetInstanceProcAddr;
    if (strcmp(name, "vkEnumerateInstanceExtensionProperties") == 0)
    {
        real_EnumerateInstanceExtensions = (PFN_vkEnumerateInstanceExtensionProperties)real;
        return opt_noDebugUtils ? (PFN_vkVoidFunction)hook_EnumerateInstanceExtensions : real;
    }
    if (strcmp(name, "vkCreateInstance") == 0) { real_CreateInstance = (PFN_vkCreateInstance)real; return (PFN_vkVoidFunction)hook_CreateInstance; }
    if (strcmp(name, "vkCreateDevice") == 0) { real_CreateDevice = (PFN_vkCreateDevice)real; return (PFN_vkVoidFunction)hook_CreateDevice; }
    if (strcmp(name, "vkCreateDebugUtilsMessengerEXT") == 0) { real_CreateDebugUtilsMessenger = (PFN_vkCreateDebugUtilsMessengerEXT)real; return (PFN_vkVoidFunction)hook_CreateDebugUtilsMessenger; }
    if (strcmp(name, "vkGetDeviceProcAddr") == 0) { g_gdpa = (PFN_vkGetDeviceProcAddr)real; return (PFN_vkVoidFunction)hook_GetDeviceProcAddr; }
    if (strcmp(name, "vkDestroySurfaceKHR") == 0) { real_DestroySurface = (PFN_vkDestroySurfaceKHR)real; return (PFN_vkVoidFunction)hook_DestroySurface; }
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
