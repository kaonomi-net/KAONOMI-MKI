using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>A drive row of the system window.</summary>
public sealed class DriveItemViewModel(DriveSpace drive)
{
    /// <summary>Drive letter / mount point and label, e.g. <c>C:\ (Windows)</c>.</summary>
    public string Title { get; } = string.IsNullOrWhiteSpace(drive.Label) ? drive.Name : $"{drive.Name} ({drive.Label})";

    /// <summary>File system and drive type, e.g. <c>NTFS · Fixed</c>.</summary>
    public string Details { get; } = $"{drive.Format} · {drive.Type}";

    /// <summary>Used share of the drive, 0–100.</summary>
    public double UsedPercent { get; } = drive.TotalBytes > 0 ? (drive.TotalBytes - drive.FreeBytes) * 100.0 / drive.TotalBytes : 0;

    /// <summary>Free and total space, e.g. <c>120.5 GB free of 931.5 GB</c>.</summary>
    public string Capacity { get; } = $"{SystemWindowViewModel.Size(drive.FreeBytes)} free of {SystemWindowViewModel.Size(drive.TotalBytes)}";

    /// <summary>The drive (note context).</summary>
    public DriveSpace Drive { get; } = drive;
}

/// <summary>
/// <c>/system</c> window: CPU, GPU, GPU memory (VRAM), memory and network activity (refreshed every second, with a one-minute graph each),
/// processor and graphics card models, and the drives with their total and free space (refreshed every 10 seconds).
/// While the window is open, the measures are sent to <c>PUT /systemlog</c> every <see cref="SystemLogSeconds"/> seconds.
/// </summary>
public partial class SystemWindowViewModel : ViewModelBase, INoteContextProvider, IDisposable
{
    /// <summary>Points kept in each graph (one per second).</summary>
    public const int HistoryLength = 60;

    /// <summary>Seconds between two sendings of the measures to <c>PUT /systemlog</c>.</summary>
    public const int SystemLogSeconds = 5;

    private readonly KaonomiAssistantService? _assistant;

    /// <summary>Disk model of each drive letter, read once (it does not change while the window is open).</summary>
    private readonly ConcurrentDictionary<string, string?> _diskModels = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Outcome of the last sending to /systemlog, shown in the status line.</summary>
    private string _systemLogNote = string.Empty;

    /// <summary>Null before the first sending, so that only changes (failing / working again) are logged.</summary>
    private bool? _systemLogWorking;

    private readonly SystemMonitorService _monitor = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly SystemHardware _hardware;
    private readonly GpuAdapter? _vramGpu;
    private SystemSample? _lastSample;

    [ObservableProperty] private string _cpuName = string.Empty;
    [ObservableProperty] private string _cpuDetails = string.Empty;
    [ObservableProperty] private string _cpuUsage = "–";
    [ObservableProperty] private string _gpuName = string.Empty;
    [ObservableProperty] private string _gpuDetails = string.Empty;
    [ObservableProperty] private string _gpuUsage = "–";

    /// <summary>Temperature of the graphics cards, e.g. "🌡 43 °C" (empty when no card reports it).</summary>
    [ObservableProperty] private string _gpuTemperature = string.Empty;
    [ObservableProperty] private string _gpuTemperatureTip = string.Empty;

    /// <summary>Green below 70 °C, orange below 85 °C, red above.</summary>
    [ObservableProperty] private IBrush _gpuTemperatureBrush = Brushes.Gray;

    [ObservableProperty] private string _gpuTemperatureName = "Graphics card";
    [ObservableProperty] private string _gpuTemperatureValue = "–";
    [ObservableProperty] private string _gpuTemperatureDetails = string.Empty;
    [ObservableProperty] private string _vramName = string.Empty;
    [ObservableProperty] private string _vramDetails = string.Empty;
    [ObservableProperty] private string _vramUsage = "–";
    [ObservableProperty] private string _memoryDetails = string.Empty;
    [ObservableProperty] private string _memoryUsage = "–";
    [ObservableProperty] private string _networkName = "Network adapter";
    [ObservableProperty] private string _networkDetails = string.Empty;
    [ObservableProperty] private string _networkUsage = "–";

    /// <summary>Top of the network graph: the highest received or sent throughput of the last minute (bits/s).</summary>
    [ObservableProperty] private double _networkScaleMax = 1;
    [ObservableProperty] private string _networkMaxLabel = "max –";
    [ObservableProperty] private string _networkMinLabel = "min –";
    [ObservableProperty] private string _status = "Measuring…";

    /// <summary>Design-time / stand-alone window: the measures are not sent.</summary>
    public SystemWindowViewModel() : this(null)
    {
    }

    /// <summary>
    /// Opens the counters, reads the hardware and starts the refresh loop; the measures are sent to
    /// <c>PUT /systemlog</c> with <paramref name="assistant"/> (not sent when null).
    /// </summary>
    public SystemWindowViewModel(KaonomiAssistantService? assistant)
    {
        _assistant = assistant;
        var hardware = SystemMonitorService.GetHardware();
        _hardware = hardware;
        CpuName = hardware.CpuName;
        CpuDetails = $"{hardware.LogicalProcessors} logical processors";
        GpuName = hardware.Gpus.Count == 0 ? "No graphics card found" : string.Join("  ·  ", hardware.Gpus.Select(g => g.Name));
        GpuDetails = hardware.Gpus.Count switch
        {
            0 => string.Empty,
            1 => "1 graphics adapter",
            var n => $"{n} graphics adapters"
        };

        // The VRAM graph follows the card with the most dedicated memory (the main one).
        _vramGpu = hardware.Gpus.OrderByDescending(g => g.DedicatedMemoryBytes).FirstOrDefault();
        VramName = _vramGpu is null ? "No graphics card found" : $"Dedicated memory · {_vramGpu.Name}";
        VramDetails = _vramGpu is { DedicatedMemoryBytes: > 0 } ? $"{Size(_vramGpu.DedicatedMemoryBytes)} installed" : string.Empty;
        MemoryDetails = $"{Size(hardware.TotalMemoryBytes)} installed";
        RefreshDrives();

        _ = RunAsync(_stop.Token);
    }

    /// <summary>CPU usage, last minute (percent).</summary>
    public ObservableCollection<double> CpuHistory { get; } = [];

    /// <summary>GPU usage, last minute (percent).</summary>
    public ObservableCollection<double> GpuHistory { get; } = [];

    /// <summary>GPU dedicated memory (VRAM) in use, last minute (percent).</summary>
    public ObservableCollection<double> VramHistory { get; } = [];

    /// <summary>Temperature of the hottest graphics card, last minute (°C, on a 0–100 scale).</summary>
    public ObservableCollection<double> GpuTemperatureHistory { get; } = [];

    /// <summary>Received throughput of the busiest network adapter, last minute (bits/s).</summary>
    public ObservableCollection<double> NetworkReceivedHistory { get; } = [];

    /// <summary>Sent throughput of the busiest network adapter, last minute (bits/s).</summary>
    public ObservableCollection<double> NetworkSentHistory { get; } = [];

    /// <summary>Memory in use, last minute (percent).</summary>
    public ObservableCollection<double> MemoryHistory { get; } = [];

    /// <summary>Drives with their capacities.</summary>
    public ObservableCollection<DriveItemViewModel> Drives { get; } = [];

    /// <summary>Samples every second on a background thread and publishes on the UI thread.</summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var tick = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var sample = await Task.Run(_monitor.Sample, cancellationToken);
                var refreshDrives = ++tick % 10 == 0;
                if (_assistant is not null && tick % SystemLogSeconds == 0)
                    _ = SendSystemLogAsync(sample, cancellationToken);
                Dispatcher.UIThread.Post(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                        return;
                    Apply(sample);
                    if (refreshDrives)
                        RefreshDrives();
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Window closed.
        }
        catch (Exception ex)
        {
            ActivityLog.Error("System", "System activity refresh failed", ex);
            Dispatcher.UIThread.Post(() => Status = $"Refresh stopped: {ex.Message}");
        }
        finally
        {
            // Closed here, once no sample is running any more.
            _monitor.Dispose();
            _stop.Dispose();
        }
    }

    private void Apply(SystemSample sample)
    {
        _lastSample = sample;

        CpuUsage = sample.CpuPercent is { } cpu ? $"{cpu:0}%" : "n/a";
        Push(CpuHistory, sample.CpuPercent ?? 0);

        GpuUsage = sample.GpuPercent is { } gpu ? $"{gpu:0}%" : "n/a";
        Push(GpuHistory, sample.GpuPercent ?? 0);
        ApplyGpuTemperatures(sample.GpuTemperatures);
        var vramTotal = _vramGpu?.DedicatedMemoryBytes ?? 0;
        if (sample.GpuMemoryUsedBytes is { } vram && vramTotal > 0)
        {
            var vramPercent = Math.Clamp(vram * 100.0 / vramTotal, 0, 100);
            VramUsage = $"{vramPercent:0}%";
            VramDetails = $"{Size(vram)} used of {Size(vramTotal)}";
            Push(VramHistory, vramPercent);
        }
        else
        {
            VramUsage = sample.GpuMemoryUsedBytes is { } used ? Size(used) : "n/a";
            Push(VramHistory, 0);
        }

        var memoryPercent = sample.MemoryTotalBytes > 0 ? sample.MemoryUsedBytes * 100.0 / sample.MemoryTotalBytes : 0;
        MemoryUsage = $"{memoryPercent:0}%";
        MemoryDetails = $"{Size(sample.MemoryUsedBytes)} used of {Size(sample.MemoryTotalBytes)}";
        Push(MemoryHistory, memoryPercent);

        // The card shows the busiest connected adapter.
        if (sample.Network.OrderByDescending(n => n.Percent).FirstOrDefault() is { } network)
        {
            NetworkUsage = $"{network.Percent:0}%";
            NetworkName = sample.Network.Count == 1 ? network.Name : $"{network.Name} (+{sample.Network.Count - 1})";
            NetworkDetails = $"↓ {Bits(network.ReceivedBitsPerSecond)}  ↑ {Bits(network.SentBitsPerSecond)} · link {Bits(network.LinkBitsPerSecond)}";
            Push(NetworkReceivedHistory, network.ReceivedBitsPerSecond);
            Push(NetworkSentHistory, network.SentBitsPerSecond);
        }
        else
        {
            NetworkUsage = "n/a";
            NetworkName = "No connected network adapter";
            NetworkDetails = string.Empty;
            Push(NetworkReceivedHistory, 0);
            Push(NetworkSentHistory, 0);
        }
        RescaleNetwork();

        Status = (sample.GpuPercent is null
            ? $"Updated {DateTime.Now:HH:mm:ss} · GPU activity unavailable (Windows performance counters)"
            : $"Updated {DateTime.Now:HH:mm:ss}") + _systemLogNote;
    }

    /// <summary>
    /// Sends the measures to <c>PUT /systemlog</c>: CPU and GPU (model, usage %), RAM and VRAM (usage %, capacity in
    /// MB), one entry per drive (letter and disk model, used %, capacity in MB). A failure never stops the window.
    /// </summary>
    private async Task SendSystemLogAsync(SystemSample sample, CancellationToken cancellationToken)
    {
        try
        {
            var entries = await Task.Run(() => BuildSystemLog(sample), cancellationToken);
            foreach (var entry in entries)
                await _assistant!.SaveSystemLogAsync(entry, cancellationToken);

            _systemLogNote = $" · sent to /systemlog at {DateTime.Now:HH:mm:ss}";
            if (_systemLogWorking != true)
                ActivityLog.Info("System", $"Measures sent to /systemlog ({entries.Count} entries every {SystemLogSeconds} s)");
            _systemLogWorking = true;
        }
        catch (OperationCanceledException)
        {
            // Window closed.
        }
        catch (Exception ex)
        {
            _systemLogNote = $" · not sent to /systemlog: {ex.Message}";
            if (_systemLogWorking != false)
                ActivityLog.Error("System", "Measures not sent to /systemlog", ex);
            _systemLogWorking = false;
        }
    }

    /// <summary>A graphics card name without its vendor prefix: "NVIDIA GeForce RTX 4090" → "RTX 4090".</summary>
    internal static string ShortGpuName(string name)
    {
        foreach (var prefix in new[] { "NVIDIA GeForce ", "NVIDIA ", "GeForce " })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length > prefix.Length)
                return name[prefix.Length..].Trim();
        }
        return name.Trim();
    }

    /// <summary>Bits per second as bps / Kbps / Mbps / Gbps.</summary>
    internal static string Bits(double bitsPerSecond)
    {
        string[] units = ["bps", "Kbps", "Mbps", "Gbps", "Tbps"];
        var unit = 0;
        while (bitsPerSecond >= 1000 && unit < units.Length - 1)
        {
            bitsPerSecond /= 1000;
            unit++;
        }
        return unit == 0 ? $"{bitsPerSecond:0} {units[unit]}" : $"{bitsPerSecond:0.#} {units[unit]}";
    }

    /// <summary>The <c>/systemlog</c> entries of a sample (drives read now).</summary>
    private List<SystemLogEntry> BuildSystemLog(SystemSample sample)
    {
        static int Percent(double value) => (int)Math.Round(Math.Clamp(value, 0, 100));
        static int Megabytes(double bytes) => (int)Math.Min(int.MaxValue, Math.Round(bytes / (1024 * 1024)));

        var entries = new List<SystemLogEntry>();
        var date = DateTimeOffset.Now;

        if (sample.CpuPercent is { } cpu)
            entries.Add(new SystemLogEntry { Date = date, Category = "CPU", SystemComponent = _hardware.CpuName, ValueCurrentInPourcent = Percent(cpu) });

        if (sample.GpuPercent is { } gpu && _vramGpu is not null)
            entries.Add(new SystemLogEntry { Date = date, Category = "GPU", SystemComponent = _vramGpu.Name, ValueCurrentInPourcent = Percent(gpu) });

        if (sample.MemoryTotalBytes > 0)
            entries.Add(new SystemLogEntry
            {
                Date = date, Category = "RAM", SystemComponent = "RAM",
                ValueCurrentInPourcent = Percent(sample.MemoryUsedBytes * 100.0 / sample.MemoryTotalBytes),
                MemoryMaxCapacityInMB = Megabytes(sample.MemoryTotalBytes)
            });

        if (sample.GpuMemoryUsedBytes is { } vram && _vramGpu is { DedicatedMemoryBytes: > 0 } vramGpu)
            entries.Add(new SystemLogEntry
            {
                Date = date, Category = "VRAM", SystemComponent = "VRAM",
                ValueCurrentInPourcent = Percent(vram * 100.0 / vramGpu.DedicatedMemoryBytes),
                MemoryMaxCapacityInMB = Megabytes(vramGpu.DedicatedMemoryBytes)
            });

        // One entry per graphics card reporting its temperature: the degrees go into valueCurrentInPourcent.
        foreach (var temperature in sample.GpuTemperatures)
            entries.Add(new SystemLogEntry { Date = date, Category = "GPUTemperature", SystemComponent = $"{ShortGpuName(temperature.Name)} Temp", ValueCurrentInPourcent = temperature.Celsius });

        // One entry per connected network adapter: its model and the usage of its link.
        foreach (var network in sample.Network)
            entries.Add(new SystemLogEntry { Date = date, Category = "Network", SystemComponent = network.Name, ValueCurrentInPourcent = Percent(network.Percent) });

        foreach (var drive in SystemMonitorService.GetDrives())
        {
            var letter = drive.Name.TrimEnd('\\', '/');
            var model = _diskModels.GetOrAdd(drive.Name, SystemMonitorService.GetDiskModel);
            entries.Add(new SystemLogEntry
            {
                Date = date, Category = "Harddisk",
                SystemComponent = model is null ? letter : $"{letter} {model}",
                ValueCurrentInPourcent = Percent((drive.TotalBytes - drive.FreeBytes) * 100.0 / drive.TotalBytes),
                MemoryMaxCapacityInMB = Megabytes(drive.TotalBytes)
            });
        }

        return entries;
    }

    /// <summary>
    /// Dynamic scale of the network graph: its top is the highest received or sent throughput visible (last minute),
    /// shown with the lowest one next to the graph.
    /// </summary>
    private void RescaleNetwork()
    {
        var visible = NetworkReceivedHistory.Concat(NetworkSentHistory).ToList();
        var max = visible.Count == 0 ? 0 : visible.Max();
        var min = visible.Count == 0 ? 0 : visible.Min();
        NetworkScaleMax = max > 0 ? max : 1;
        NetworkMaxLabel = $"max {Bits(max)}";
        NetworkMinLabel = $"min {Bits(min)}";
    }

    /// <summary>
    /// Shows the temperature of the graphics cards: the value alone with one card, "name °C" for each card otherwise;
    /// colored by the hottest one.
    /// </summary>
    private void ApplyGpuTemperatures(IReadOnlyList<GpuTemperature> temperatures)
    {
        if (temperatures.Count == 0)
        {
            GpuTemperature = string.Empty;
            GpuTemperatureTip = "Temperature unavailable (NVIDIA cards only, through the driver)";
            GpuTemperatureValue = "n/a";
            GpuTemperatureName = GpuTemperatureTip;
            GpuTemperatureDetails = string.Empty;
            Push(GpuTemperatureHistory, 0);
            return;
        }

        GpuTemperature = temperatures.Count == 1
            ? $"🌡 {temperatures[0].Celsius} °C"
            : "🌡 " + string.Join("  ", temperatures.Select(t => $"{t.Celsius} °C"));
        GpuTemperatureTip = string.Join("\n", temperatures.Select(t => $"{t.Name}: {t.Celsius} °C"));
        var hottest = temperatures.Max(t => t.Celsius);
        GpuTemperatureBrush = new SolidColorBrush(Color.Parse(hottest < 70 ? "#3DBB4A" : hottest < 85 ? "#F28C38" : "#E81123"));

        // Temperature graph: the hottest card, 0–100 °C.
        var hottestCard = temperatures.First(t => t.Celsius == hottest);
        Push(GpuTemperatureHistory, hottest);
        GpuTemperatureValue = $"{hottest} °C";
        GpuTemperatureName = temperatures.Count == 1 ? hottestCard.Name : $"{hottestCard.Name} (hottest of {temperatures.Count})";
        GpuTemperatureDetails = $"min {GpuTemperatureHistory.Min():0} °C · max {GpuTemperatureHistory.Max():0} °C (last minute)";
    }

    private static void Push(ObservableCollection<double> history, double value)
    {
        history.Add(value);
        while (history.Count > HistoryLength)
            history.RemoveAt(0);
    }

    private void RefreshDrives()
    {
        var drives = SystemMonitorService.GetDrives();
        Drives.Clear();
        foreach (var drive in drives)
            Drives.Add(new DriveItemViewModel(drive));
    }

    /// <summary>Bytes as KB / MB / GB / TB with one decimal.</summary>
    internal static string Size(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes:0} {units[unit]}" : $"{bytes:0.0} {units[unit]}";
    }

    /// <inheritdoc />
    public string NoteWindowName => "System";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        cpu = new { model = CpuName, usage = CpuUsage, _hardware.LogicalProcessors },
        gpu = new { models = _hardware.Gpus.Select(g => g.Name).ToArray(), usage = GpuUsage },
        vram = new { usage = VramUsage, details = VramDetails },
        memory = new { usage = MemoryUsage, details = MemoryDetails },
        network = new { adapter = NetworkName, usage = NetworkUsage, details = NetworkDetails },
        gpuTemperature = new { card = GpuTemperatureName, value = GpuTemperatureValue, details = GpuTemperatureDetails },
        drives = Drives.Select(d => new { d.Drive.Name, d.Drive.Format, d.Drive.Type, d.Drive.TotalBytes, d.Drive.FreeBytes }).ToArray(),
        sampledAt = _lastSample is null ? null : DateTime.Now.ToString("O")
    };

    /// <summary>Stops the refresh loop (which then closes the performance counters).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _stop.Cancel();
    }

    private bool _disposed;
}
