// libvma.so: the Vulkan Memory Allocator with its C API exported, as the engine's Brutal.Vulkan.Vma expects
// (vma.dll on Windows). The engine creates the allocator without passing Vulkan function pointers, so the
// exported vmaCreateAllocator fills them in from the system's libvulkan.so before calling the real one.
#include <dlfcn.h>
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
        static void* vulkan = dlopen("libvulkan.so", RTLD_NOW | RTLD_LOCAL);
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
