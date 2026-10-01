using System.Runtime.InteropServices;

namespace LocalMind.Telemetry;

public record GpuSample(ulong UsedBytes, ulong TotalBytes, uint? Utilization, uint? Temperature, double? PowerWatts);
public sealed class Nvml : IDisposable
{
    private readonly IntPtr library, device;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetDevice(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetMemory(IntPtr device, out Memory memory);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetUsage(IntPtr device, out Usage usage);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetTemp(IntPtr device, uint sensor, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetPower(IntPtr device, out uint value);
    [StructLayout(LayoutKind.Sequential)] private struct Memory { public ulong Total, Free, Used; }
    [StructLayout(LayoutKind.Sequential)] private struct Usage { public uint Gpu, Memory; }
    private T Function<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
    public Nvml()
    {
        var paths = new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvml.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation/NVSMI/nvml.dll") };
        var path = paths.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("NVML 미확인: NVIDIA 드라이버 DLL이 없다.");
        library = NativeLibrary.Load(path);
        var initialized = false;
        try
        {
            if (Function<Init>("nvmlInit_v2")() != 0) throw new IOException("NVML 초기화 실패이다.");
            initialized = true;
            if (Function<GetDevice>("nvmlDeviceGetHandleByIndex_v2")(0, out device) != 0) throw new IOException("GPU 0 조회 실패이다.");
        }
        catch { if (initialized) Function<Init>("nvmlShutdown")(); NativeLibrary.Free(library); throw; }
    }
    public GpuSample Read()
    {
        if (Function<GetMemory>("nvmlDeviceGetMemoryInfo")(device, out var memory) != 0) throw new IOException("NVML 메모리 조회 실패이다.");
        uint? use = Function<GetUsage>("nvmlDeviceGetUtilizationRates")(device, out var usage) == 0 ? usage.Gpu : null;
        uint? temp = Function<GetTemp>("nvmlDeviceGetTemperature")(device, 0, out var t) == 0 ? t : null;
        double? power = Function<GetPower>("nvmlDeviceGetPowerUsage")(device, out var p) == 0 ? p / 1000.0 : null;
        return new(memory.Used, memory.Total, use, temp, power);
    }
    public void Dispose() { Function<Init>("nvmlShutdown")(); NativeLibrary.Free(library); }
}
