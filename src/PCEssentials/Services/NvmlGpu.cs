using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using PCEssentials.Core;

namespace PCEssentials.Services;

/// The first NVIDIA GPU, read through NVIDIA's management library (nvml.dll, installed with the driver).
/// Load, temperature and memory take about 2 µs here. LibreHardwareMonitor's NVIDIA update took ~24 ms of
/// CPU and ~80 ms in all every second, mostly on readings this app never shows (PCIe throughput, which
/// NVML samples for 20 ms per direction, plus clocks, power rails and fans).
public sealed class NvmlGpu : IDisposable
{
    private readonly IntPtr _device;
    public string Name { get; }

    private NvmlGpu(IntPtr device, string name)
    {
        _device = device;
        Name = name;
    }

    /// Null without an NVIDIA GPU or driver; LibreHardwareMonitor then reads the GPU as before.
    public static NvmlGpu? TryOpen()
    {
        try
        {
            // DCH drivers put nvml.dll in System32; older ones only in NVSMI. Loading it by path first makes
            // the DllImports below find it.
            var nvsmi = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI\nvml.dll");
            if (!File.Exists(Path.Combine(Environment.SystemDirectory, "nvml.dll")) && File.Exists(nvsmi)) NativeLibrary.TryLoad(nvsmi, out _);
            if (nvmlInit_v2() != 0) return null;
            if (nvmlDeviceGetCount_v2(out var count) != 0 || count == 0 || nvmlDeviceGetHandleByIndex_v2(0, out var device) != 0)
            {
                nvmlShutdown();
                return null;
            }
            var name = new byte[96];
            var text = "NVIDIA GPU";
            if (nvmlDeviceGetName(device, name, (uint)name.Length) == 0)
            {
                var length = Array.IndexOf(name, (byte)0);
                text = Encoding.ASCII.GetString(name, 0, length < 0 ? name.Length : length);
            }
            return new NvmlGpu(device, text);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("nvml open", ex);
            return null;
        }
    }

    public (double? Load, double? Temp, double? MemUsedMb, double? MemTotalMb) Read()
    {
        double? load = null, temp = null, used = null, total = null;
        if (nvmlDeviceGetUtilizationRates(_device, out var u) == 0) load = u.Gpu;
        if (nvmlDeviceGetTemperature(_device, 0 /* NVML_TEMPERATURE_GPU */, out var t) == 0) temp = t;
        if (nvmlDeviceGetMemoryInfo(_device, out var m) == 0)
        {
            used = m.Used / 1048576.0;
            total = m.Total / 1048576.0;
        }
        return (load, temp, used, total);
    }

    public void Dispose()
    {
        try { nvmlShutdown(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct Memory { public ulong Total, Free, Used; }

    [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
    [DllImport("nvml.dll")] private static extern int nvmlShutdown();
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCount_v2(out uint count);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temperature);
    [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out Memory memory);
}
