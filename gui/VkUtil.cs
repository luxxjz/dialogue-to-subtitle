// VkUtil.cs - 枚举 Vulkan 物理设备
// 用途: ① 多显卡笔记本上 whisper.cpp 的 Vulkan 后端默认枚举选第一个设备(常是核显),
//       通过环境变量 GGML_VK_VISIBLE_DEVICES 显式指定 NVIDIA 独显索引, 让 VAD/识别都走独显;
//       ② GUI 提供 GPU 下拉, 让用户自选 whisper 用哪块卡 (索引 -> GGML_VK_VISIBLE_DEVICES)。
// 仅枚举(只读), 不创建任何窗口/设备; 失败返回空列表 / -1 (调用方维持默认行为)。

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SrtGui
{
    public static class VkUtil
    {
        const int VK_SUCCESS = 0;
        const int VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO = 1;
        const uint NVIDIA_VENDOR_ID = 0x10DE;

        // GUI 下拉用: 一块物理设备 = 索引 + 名称 + 厂商
        public struct GpuInfo
        {
            public int Index;
            public string Name;
            public uint Vendor;
            public bool IsNvidia { get { return Vendor == NVIDIA_VENDOR_ID; } }
            public override string ToString() { return Name; }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct VkInstanceCreateInfo
        {
            public int sType;        // VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO = 1
            public IntPtr pNext;
            public uint flags;
            public IntPtr pApplicationInfo;
            public uint enabledLayerCount;
            public IntPtr ppEnabledLayerNames;
            public uint enabledExtensionCount;
            public IntPtr ppEnabledExtensionNames;
        }

        [DllImport("vulkan-1.dll")]
        static extern int vkCreateInstance(ref VkInstanceCreateInfo pCreateInfo, IntPtr pAllocator, out IntPtr pInstance);
        [DllImport("vulkan-1.dll")]
        static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint pPhysicalDeviceCount, IntPtr pPhysicalDevices);
        [DllImport("vulkan-1.dll")]
        static extern void vkGetPhysicalDeviceProperties(IntPtr physicalDevice, IntPtr pProperties);

        // 枚举全部 Vulkan 物理设备 (索引 = GGML_VK_VISIBLE_DEVICES 取值); 失败返回空列表
        public static GpuInfo[] EnumerateDevices()
        {
            List<GpuInfo> list = new List<GpuInfo>();
            try
            {
                VkInstanceCreateInfo ci = new VkInstanceCreateInfo();
                ci.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
                IntPtr instance;
                if (vkCreateInstance(ref ci, IntPtr.Zero, out instance) != VK_SUCCESS) return list.ToArray();

                uint count = 0;
                if (vkEnumeratePhysicalDevices(instance, ref count, IntPtr.Zero) != VK_SUCCESS || count == 0) return list.ToArray();

                IntPtr arr = Marshal.AllocHGlobal((int)count * IntPtr.Size);
                // VkPhysicalDeviceProperties 含大型 limits 结构, 用足够大的缓冲接收后手动读头部字段
                IntPtr buf = Marshal.AllocHGlobal(1024);
                try
                {
                    if (vkEnumeratePhysicalDevices(instance, ref count, arr) != VK_SUCCESS) return list.ToArray();
                    for (uint i = 0; i < count; i++)
                    {
                        IntPtr dev = Marshal.ReadIntPtr(arr, (int)i * IntPtr.Size);
                        vkGetPhysicalDeviceProperties(dev, buf);
                        // 布局: apiVersion(0) driverVersion(4) vendorID(8) deviceID(12) deviceType(16) deviceName(20,256)
                        uint vendor = (uint)Marshal.ReadInt32(buf, 8);
                        string name = Marshal.PtrToStringAnsi(new IntPtr(buf.ToInt64() + 20)) ?? "";
                        GpuInfo g = new GpuInfo();
                        g.Index = (int)i;
                        g.Name = name;
                        g.Vendor = vendor;
                        list.Add(g);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(arr);
                    Marshal.FreeHGlobal(buf);
                }
            }
            catch { }
            return list.ToArray();
        }

        // 返回 NVIDIA Vulkan 设备的索引; 无 NVIDIA 设备或失败返回 -1
        public static int FindNvidiaVkIndex()
        {
            GpuInfo[] devs = EnumerateDevices();
            foreach (GpuInfo g in devs) { if (g.IsNvidia) return g.Index; }
            return -1;
        }
    }
}
