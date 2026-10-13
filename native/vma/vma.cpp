// libvma.so: the Vulkan Memory Allocator with its C API exported, as the engine's Brutal.Vulkan.Vma expects
// (vma.dll on Windows). The engine creates the allocator without passing Vulkan function pointers, so the
// exported vmaCreateAllocator fills them in from the system's libvulkan.so before calling the real one.
#include <dlfcn.h>
#include <string.h>
#include <android/log.h>
#define VK_NO_PROTOTYPES
#define VMA_IMPLEMENTATION
#define VMA_STATIC_VULKAN_FUNCTIONS 0
#define VMA_DYNAMIC_VULKAN_FUNCTIONS 1
#define VMA_VULKAN_VERSION 1003000
#define vmaCreateAllocator vmaCreateAllocatorImpl
#include "vk_mem_alloc.h"
#undef vmaCreateAllocator

extern "C" VkResult vmaCreateAllocator(const VmaAllocatorCreateInfo* pCreateInfo, VmaAllocator* pAllocator)
{
    VmaAllocatorCreateInfo info = *pCreateInfo;
    VmaVulkanFunctions functions = {};
    if (info.pVulkanFunctions != nullptr)
    {
        functions = *info.pVulkanFunctions;
    }
    if (functions.vkGetInstanceProcAddr == nullptr)
    {
        // The libvulkan.so beside this library is the port's interposer (host/vulkan), which hides memory types the
        // engine cannot use and counts allocations; by name alone the linker may answer with the system's loader,
        // which the interposer has opened too. So it is asked for by its path.
        static void* vulkan = nullptr;
        Dl_info self;
        if (vulkan == nullptr && dladdr((void*)&vmaCreateAllocator, &self) != 0 && self.dli_fname != nullptr)
        {
            char path[1024];
            const char* slash = strrchr(self.dli_fname, '/');
            if (slash != nullptr && (size_t)(slash - self.dli_fname) + 16 < sizeof path)
            {
                memcpy(path, self.dli_fname, slash - self.dli_fname + 1);
                strcpy(path + (slash - self.dli_fname) + 1, "libvulkan.so");
                vulkan = dlopen(path, RTLD_NOW | RTLD_LOCAL);
            }
        }
        if (vulkan == nullptr)
        {
            vulkan = dlopen("libvulkan.so", RTLD_NOW | RTLD_LOCAL);
        }
        __android_log_print(ANDROID_LOG_INFO, "port17", "vma: Vulkan functions come from %s", vulkan == nullptr ? "nowhere" : dlsym(vulkan, "port17_vulkan_window_changed") != nullptr ? "the port's interposer" : "the system's loader");
        if (vulkan != nullptr)
        {
            functions.vkGetInstanceProcAddr = (PFN_vkGetInstanceProcAddr)dlsym(vulkan, "vkGetInstanceProcAddr");
        }
    }
    if (functions.vkGetInstanceProcAddr == nullptr)
    {
        return VK_ERROR_INITIALIZATION_FAILED;
    }
    if (functions.vkGetDeviceProcAddr == nullptr)
    {
        functions.vkGetDeviceProcAddr = (PFN_vkGetDeviceProcAddr)functions.vkGetInstanceProcAddr(info.instance, "vkGetDeviceProcAddr");
    }
    info.pVulkanFunctions = &functions;
    return vmaCreateAllocatorImpl(&info, pAllocator);
}
