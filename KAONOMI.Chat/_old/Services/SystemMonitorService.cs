using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace KAONOMI.Chat.Services;

/// <summary>Hardware description that does not change while the application runs.</summary>
public sealed record SystemHardware(string CpuName, int LogicalProcessors, IReadOnlyList<GpuAdapter> Gpus, ulong TotalMemoryBytes);

/// <summary>A graphics adapter and its dedicated memory (0 when unknown).</summary>
public sealed record GpuAdapter(string Name, ulong DedicatedMemoryBytes);

/// <summary>One measure of the activity; null values could not be read.</summary>
public sealed record SystemSample(double? CpuPercent, double? GpuPercent, ulong? GpuMemoryUsedBytes, ulong MemoryUsedBytes, ulong MemoryTotalBytes)
{
    /// <summary>Activity of each connected network adapter since the previous sample.</summary>
    public IReadOnlyList<NetworkUsage> Network { get; init; } = [];

    /// <summary>Temperature of each graphics card that reports it (NVIDIA cards, through the driver's NVML).</summary>
    public IReadOnlyList<GpuTemperature> GpuTemperatures { get; init; } = [];
}

/// <summary>Core temperature of a graphics card, in °C.</summary>
public sealed record GpuTemperature(string Name, int Celsius);

/// <summary>
/// Activity of a network adapter: received and sent bits per second, link speed, and the usage of the link
/// (received + sent against the link speed, as the Task Manager shows it, 0–100).
/// </summary>
public sealed record NetworkUsage(string Name, double Percent, double ReceivedBitsPerSecond, double SentBitsPerSecond, long LinkBitsPerSecond);

/// <summary>A drive and its capacities.</summary>
public sealed record DriveSpace(string Name, string Label, string Format, string Type, long TotalBytes, long FreeBytes);

/// <summary>
/// Reads the CPU, GPU and memory activity, the processor and graphics card models and the drives.
/// CPU from <c>GetSystemTimes</c>, memory from <c>GlobalMemoryStatusEx</c>, GPU from the Windows "GPU Engine" and
/// "GPU Adapter Memory" performance counters (same figures as the Task Manager, any vendor), GPU temperatures from
/// NVML (the NVIDIA driver library, NVIDIA cards only), models from the registry.
/// On macOS: CPU from <c>host_statistics</c>, memory from <c>host_statistics64</c> (used = app memory + wired +
/// compressed, as the Activity Monitor), processor model from <c>sysctl</c>; no GPU activity. The network activity is
/// read everywhere. Elsewhere, what is not available is reported as unavailable.
/// </summary>
public sealed class SystemMonitorService : IDisposable
{
    private ulong _lastIdle, _lastKernel, _lastUser;
    private IntPtr _query;
    private IntPtr _gpuEngineCounter;
    private IntPtr _gpuMemoryCounter;
    private bool _pdhReady;

    /// <summary>NVIDIA cards found through NVML (empty without an NVIDIA driver).</summary>
    private readonly List<(IntPtr Device, string Name)> _nvmlDevices = [];
    private bool _nvmlReady;

    /// <summary>Bytes received / sent by each adapter at the previous sample, to compute the throughput.</summary>
    private readonly Dictionary<string, (long Received, long Sent, long Timestamp)> _lastNetwork = [];

    /// <summary>Opens the GPU performance counters (the first sample sets the reference for the next ones).</summary>
    public SystemMonitorService()
    {
        ReadCpuTimes(out _lastIdle, out _lastKernel, out _lastUser);
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out _query) == 0
                && PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuEngineCounter) == 0)
            {
                PdhAddEnglishCounter(_query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out _gpuMemoryCounter);
                _pdhReady = PdhCollectQueryData(_query) == 0;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _pdhReady = false;
        }

        OpenNvml();
    }

    /// <summary>Loads NVML (installed with the NVIDIA driver) and lists the NVIDIA cards; nothing without it.</summary>
    private void OpenNvml()
    {
        try
        {
            if (NvmlInit() != 0)
                return;
            _nvmlReady = true;
            if (NvmlDeviceGetCount(out var count) != 0)
                return;

            for (uint i = 0; i < count; i++)
            {
                if (NvmlDeviceGetHandleByIndex(i, out var device) != 0)
                    continue;
                var buffer = new byte[96];
                var name = NvmlDeviceGetName(device, buffer, (uint)buffer.Length) == 0
                    ? System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0').Trim()
                    : $"NVIDIA GPU {i}";
                _nvmlDevices.Add((device, name));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _nvmlReady = false;
        }
    }

    /// <summary>Core temperature of each NVIDIA card (NVML_TEMPERATURE_GPU).</summary>
    private IReadOnlyList<GpuTemperature> ReadGpuTemperatures()
    {
        var temperatures = new List<GpuTemperature>();
        if (!_nvmlReady)
            return temperatures;

        const int NvmlTemperatureGpu = 0;
        foreach (var (device, name) in _nvmlDevices)
        {
            if (NvmlDeviceGetTemperature(device, NvmlTemperatureGpu, out var celsius) == 0)
                temperatures.Add(new GpuTemperature(name, (int)celsius));
        }
        return temperatures;
    }

    /// <summary>Processor model, graphics cards and installed memory.</summary>
    public static SystemHardware GetHardware()
    {
        var memory = ReadMemory();
        if (OperatingSystem.IsMacOS())
            return new SystemHardware(MacSysctlString("machdep.cpu.brand_string") ?? RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount, [], memory.Total);
        if (!OperatingSystem.IsWindows())
            return new SystemHardware(RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount, [], memory.Total);

        return new SystemHardware(ReadCpuName(), Environment.ProcessorCount, ReadGpus(), memory.Total);
    }

    /// <summary>Activity since the previous call (call it about once per second).</summary>
    public SystemSample Sample()
    {
        var memory = ReadMemory();
        double? cpu = null;
        if (ReadCpuTimes(out var idle, out var kernel, out var user))
        {
            // Kernel time includes the idle time.
            var total = (kernel - _lastKernel) + (user - _lastUser);
            var busy = total - (idle - _lastIdle);
            if (total > 0)
                cpu = Math.Clamp(busy * 100.0 / total, 0, 100);
            (_lastIdle, _lastKernel, _lastUser) = (idle, kernel, user);
        }

        if (!OperatingSystem.IsWindows())
            return new SystemSample(cpu, null, null, memory.Total - memory.Available, memory.Total) { Network = SampleNetwork() };

        double? gpu = null;
        ulong? gpuMemory = null;
        if (_pdhReady && PdhCollectQueryData(_query) == 0)
        {
            gpu = GpuUtilization();
            gpuMemory = GpuDedicatedMemory();
        }

        return new SystemSample(cpu, gpu, gpuMemory, memory.Total - memory.Available, memory.Total) { Network = SampleNetwork(), GpuTemperatures = ReadGpuTemperatures() };
    }

    /// <summary>
    /// Throughput of the connected adapters (up, with a link speed, loopback and tunnels excluded) since the previous
    /// call; an adapter seen for the first time is reported at 0 %.
    /// </summary>
    private IReadOnlyList<NetworkUsage> SampleNetwork()
    {
        var usages = new List<NetworkUsage>();
        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return usages;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        foreach (var adapter in adapters)
        {
            try
            {
                if (adapter.OperationalStatus != OperationalStatus.Up
                    || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                // macOS: only the physical interfaces (en0, en1… Ethernet and Wi-Fi), not awdl, llw, utun, bridge…
                if (OperatingSystem.IsMacOS() && !adapter.Name.StartsWith("en", StringComparison.Ordinal))
                    continue;
                // The link speed is not always reported outside Windows (Wi-Fi): 1 Gbit/s is assumed for the percentage.
                var speed = adapter.Speed > 0 ? adapter.Speed : OperatingSystem.IsWindows() ? 0 : 1_000_000_000L;
                if (speed <= 0)
                    continue;

                var statistics = adapter.GetIPStatistics();
                var (received, sent) = (statistics.BytesReceived, statistics.BytesSent);
                double receivedBps = 0, sentBps = 0;
                if (_lastNetwork.TryGetValue(adapter.Id, out var last))
                {
                    var seconds = (now - last.Timestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
                    if (seconds > 0)
                    {
                        receivedBps = Math.Max(0, received - last.Received) * 8 / seconds;
                        sentBps = Math.Max(0, sent - last.Sent) * 8 / seconds;
                    }
                }
                _lastNetwork[adapter.Id] = (received, sent, now);

                var percent = Math.Clamp((receivedBps + sentBps) * 100 / speed, 0, 100);
                var name = OperatingSystem.IsMacOS() ? adapter.Name : adapter.Description;
                usages.Add(new NetworkUsage(name, percent, receivedBps, sentBps, speed));
            }
            catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException)
            {
                // Adapter gone or statistics unavailable: skipped.
            }
        }
        return usages;
    }

    /// <summary>Ready drives (fixed, removable, network…) with their total and free space.</summary>
    public static IReadOnlyList<DriveSpace> GetDrives()
    {
        var drives = new List<DriveSpace>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.TotalSize <= 0)
                    continue;
                // macOS: the system disk ("/") and the mounted volumes, not the system volumes (/System/Volumes/…, /dev…).
                if (OperatingSystem.IsMacOS() && drive.Name != "/" && !drive.Name.StartsWith("/Volumes/", StringComparison.Ordinal))
                    continue;
                drives.Add(new DriveSpace(drive.Name, drive.VolumeLabel, drive.DriveFormat, drive.DriveType.ToString(),
                    drive.TotalSize, drive.AvailableFreeSpace));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Drive removed or not readable: skipped.
            }
        }
        return drives;
    }

    /// <summary>
    /// Model of the disk holding the drive <paramref name="driveName"/> (e.g. <c>C:\</c> → "Samsung SSD 990 PRO 2TB"),
    /// from the storage device descriptor (vendor + product); null when unknown (network drive, not Windows…).
    /// </summary>
    public static string? GetDiskModel(string driveName)
    {
        if (!OperatingSystem.IsWindows() || driveName.Length < 2 || driveName[1] != ':')
            return null;

        const uint FileShareReadWrite = 0x1 | 0x2;
        const uint OpenExisting = 3;
        const uint IoctlStorageQueryProperty = 0x2D1400;

        try
        {
            // No access right is needed to query the device properties.
            using var handle = CreateFile($@"\\.\{driveName[..2]}", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid)
                return null;

            // STORAGE_PROPERTY_QUERY { StorageDeviceProperty = 0, PropertyStandardQuery = 0 }.
            var query = new byte[12];
            var buffer = Marshal.AllocHGlobal(1024);
            try
            {
                if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, query.Length, buffer, 1024, out _, IntPtr.Zero))
                    return null;

                // STORAGE_DEVICE_DESCRIPTOR: VendorIdOffset at 12, ProductIdOffset at 16 (0 when absent).
                string? Text(int offsetField)
                {
                    var offset = Marshal.ReadInt32(buffer, offsetField);
                    return offset is > 0 and < 1024 ? Marshal.PtrToStringAnsi(buffer + offset)?.Trim() : null;
                }

                var model = string.Join(' ', new[] { Text(12), Text(16) }.Where(s => !string.IsNullOrWhiteSpace(s)));
                return model.Length == 0 ? null : model;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Busiest engine type (3D, copy, video…) of the busiest adapter, as the Task Manager does: the instance
    /// utilisations are added per adapter and engine type (one instance per process and engine).
    /// </summary>
    private double? GpuUtilization()
    {
        var values = ReadCounterArray(_gpuEngineCounter);
        if (values is null)
            return null;

        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            var key = $"{Part(name, "luid_", "_phys")}|{Part(name, "engtype_", null)}";
            totals[key] = totals.GetValueOrDefault(key) + value;
        }
        return totals.Count == 0 ? 0 : Math.Clamp(totals.Values.Max(), 0, 100);
    }

    /// <summary>Dedicated memory used on the adapter that uses the most (the main graphics card).</summary>
    private ulong? GpuDedicatedMemory()
    {
        if (_gpuMemoryCounter == IntPtr.Zero || ReadCounterArray(_gpuMemoryCounter) is not { } values)
            return null;
        return values.Count == 0 ? 0 : (ulong)Math.Max(0, values.Max(v => v.Value));
    }

    private static string Part(string name, string start, string? end)
    {
        var i = name.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return string.Empty;
        i += start.Length;
        var j = end is null ? -1 : name.IndexOf(end, i, StringComparison.OrdinalIgnoreCase);
        return j < 0 ? name[i..] : name[i..j];
    }

    /// <summary>Instances and values of a wildcard counter, or null when the counter cannot be read.</summary>
    private static List<(string Name, double Value)>? ReadCounterArray(IntPtr counter)
    {
        const uint PdhFmtDouble = 0x00000200;
        const uint PdhMoreData = 0x800007D2;

        uint size = 0, count = 0;
        var status = PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref size, out count, IntPtr.Zero);
        if (status != PdhMoreData)
            return status == 0 ? [] : null;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (PdhGetFormattedCounterArray(counter, PdhFmtDouble, ref size, out count, buffer) != 0)
                return null;

            // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, then { DWORD CStatus; double value } (8-byte aligned).
            var itemSize = IntPtr.Size + 16;
            var list = new List<(string, double)>((int)count);
            for (var i = 0; i < count; i++)
            {
                var item = buffer + i * itemSize;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item)) ?? string.Empty;
                var cStatus = (uint)Marshal.ReadInt32(item + IntPtr.Size);
                if (cStatus is 0 or 1) // PDH_CSTATUS_VALID_DATA / NEW_DATA
                    list.Add((name, BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + IntPtr.Size + 8))));
            }
            return list;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool ReadCpuTimes(out ulong idle, out ulong kernel, out ulong user)
    {
        idle = kernel = user = 0;
        if (OperatingSystem.IsMacOS())
            return MacCpuTicks(out idle, out kernel, out user);
        if (!OperatingSystem.IsWindows() || !GetSystemTimes(out var i, out var k, out var u))
            return false;
        (idle, kernel, user) = (i, k, u);
        return true;
    }

    private static (ulong Total, ulong Available) ReadMemory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
                return (status.TotalPhys, status.AvailPhys);
        }

        if (OperatingSystem.IsMacOS() && MacMemory() is { } mac)
            return mac;

        var info = GC.GetGCMemoryInfo();
        return ((ulong)info.TotalAvailableMemoryBytes, 0);
    }

    [SupportedOSPlatform("windows")]
    private static string ReadCpuName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var name = (key?.GetValue("ProcessorNameString") as string)?.Trim();
        return string.IsNullOrEmpty(name) ? RuntimeInformation.ProcessArchitecture.ToString() : name;
    }

    /// <summary>Display adapters from the registry (display class), without the virtual Microsoft adapters.</summary>
    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<GpuAdapter> ReadGpus()
    {
        const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
        var gpus = new List<GpuAdapter>();
        using var root = Registry.LocalMachine.OpenSubKey(DisplayClass);
        if (root is null)
            return gpus;

        foreach (var subKeyName in root.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
        {
            try
            {
                using var key = root.OpenSubKey(subKeyName);
                if (key?.GetValue("DriverDesc") is not string name || string.IsNullOrWhiteSpace(name)
                    || name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
                    || gpus.Any(g => g.Name == name))
                    continue;

                ulong memory = key.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => (ulong)l,
                    byte[] { Length: >= 8 } b => BitConverter.ToUInt64(b, 0),
                    _ => key.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int i => (uint)i,
                        byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b, 0),
                        _ => 0UL
                    }
                };
                gpus.Add(new GpuAdapter(name.Trim(), memory));
            }
            catch (System.Security.SecurityException)
            {
                // Adapter key not readable: skipped.
            }
        }
        return gpus;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
        _pdhReady = false;

        if (_nvmlReady)
        {
            _nvmlReady = false;
            try
            {
                NvmlShutdown();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // Already unloaded.
            }
        }
    }

    // ---- macOS (libSystem: sysctl and the Mach host statistics) ----

    private const string LibSystem = "/usr/lib/libSystem.dylib";
    private const int HostCpuLoadInfo = 3;     // HOST_CPU_LOAD_INFO: ticks user, system, idle, nice
    private const int HostVmInfo64 = 4;        // HOST_VM_INFO64: vm_statistics64 (38 32-bit words)
    private static uint _macHost;

    private static uint MacHost() => _macHost != 0 ? _macHost : _macHost = mach_host_self();

    /// <summary>CPU ticks, mapped to the Windows meaning (kernel includes idle) so that the same formula applies.</summary>
    private static bool MacCpuTicks(out ulong idle, out ulong kernel, out ulong user)
    {
        idle = kernel = user = 0;
        try
        {
            var ticks = new uint[4];
            var count = (uint)ticks.Length;
            if (host_statistics(MacHost(), HostCpuLoadInfo, ticks, ref count) != 0)
                return false;
            idle = ticks[2];
            kernel = (ulong)ticks[1] + ticks[2];
            user = (ulong)ticks[0] + ticks[3];
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Physical memory and what is available: total - (app memory + wired + compressed), as the Activity Monitor.</summary>
    private static (ulong Total, ulong Available)? MacMemory()
    {
        try
        {
            if (MacSysctlUInt64("hw.memsize") is not { } total || MacSysctlUInt64("hw.pagesize") is not { } page)
                return null;

            var words = new uint[38];
            var count = (uint)words.Length;
            if (host_statistics64(MacHost(), HostVmInfo64, words, ref count) != 0)
                return (total, 0);

            // vm_statistics64 offsets (32-bit words): wire_count 3, purgeable_count 22, compressor_page_count 32,
            // internal_page_count 35.
            var appPages = (ulong)Math.Max(0L, (long)words[35] - words[22]);
            var used = (appPages + words[3] + words[32]) * page;
            return (total, used >= total ? 0 : total - used);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static ulong? MacSysctlUInt64(string name)
    {
        var buffer = new byte[8];
        var length = (nint)buffer.Length;
        if (sysctlbyname(name, buffer, ref length, IntPtr.Zero, 0) != 0)
            return null;
        return length == 4 ? BitConverter.ToUInt32(buffer, 0) : BitConverter.ToUInt64(buffer, 0);
    }

    private static string? MacSysctlString(string name)
    {
        try
        {
            nint length = 0;
            if (sysctlbyname(name, null, ref length, IntPtr.Zero, 0) != 0 || length <= 0)
                return null;
            var buffer = new byte[length];
            if (sysctlbyname(name, buffer, ref length, IntPtr.Zero, 0) != 0)
                return null;
            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, (int)length).TrimEnd('\0').Trim();
            return text.Length > 0 ? text : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport(LibSystem)]
    private static extern int sysctlbyname(string name, byte[]? oldValue, ref nint oldLength, IntPtr newValue, nint newLength);

    [DllImport(LibSystem)]
    private static extern uint mach_host_self();

    [DllImport(LibSystem)]
    private static extern int host_statistics(uint host, int flavor, uint[] info, ref uint count);

    [DllImport(LibSystem)]
    private static extern int host_statistics64(uint host, int flavor, uint[] info, ref uint count);

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    private static extern int NvmlInit();

    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
    private static extern int NvmlShutdown();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
    private static extern int NvmlDeviceGetCount(out uint count);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName")]
    private static extern int NvmlDeviceGetName(IntPtr device, byte[] name, uint length);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
    private static extern int NvmlDeviceGetTemperature(IntPtr device, int sensorType, out uint temperature);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhGetFormattedCounterArrayW")]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle device, uint ioControlCode, byte[] inBuffer, int inBufferSize,
        IntPtr outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
}
