using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// /system: CPU and memory, GPU and VRAM, the network (received and sent, on a dynamic scale with its max / min) and
/// the GPU temperature, each with its value, its hardware model, a graph of the last minute and its details; then the
/// drives (name, format, a bar of the used space, the free space). The status of the measures below.
/// </summary>
[ToolView(typeof(SystemWindowViewModel), "System activity", "🖥", 900, 960)]
public sealed class SystemView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (SystemWindowViewModel)viewModel;
        const int history = SystemWindowViewModel.HistoryLength;

        var cpu = Card(vm, "CPU", nameof(vm.CpuUsage), nameof(vm.CpuName), null,
            Chart(vm.CpuHistory, "#2B9BF4", "CPU"), Meta(vm, nameof(vm.CpuDetails)));
        var memory = Card(vm, "Memory", nameof(vm.MemoryUsage), null, "Physical memory (RAM)",
            Chart(vm.MemoryHistory, "#B65FD1", "Memory"), Meta(vm, nameof(vm.MemoryDetails)));

        // GPU: the temperature (NVIDIA cards, through NVML) at the right of its details.
        var temperature = DataUi.Tip(DataUi.Bound(vm, nameof(vm.GpuTemperature), Ui.Text, 12), vm, nameof(vm.GpuTemperatureTip));
        temperature.Bind(TextBlock.ForegroundProperty, vm, nameof(vm.GpuTemperatureBrush), BindingMode.OneWay, v => v as IBrush ?? Ui.Text);
        var gpu = Card(vm, "GPU", nameof(vm.GpuUsage), nameof(vm.GpuName), null,
            Chart(vm.GpuHistory, "#3DBB4A", "GPU"), DataUi.Bar([], Meta(vm, nameof(vm.GpuDetails)), temperature));
        var vram = Card(vm, "VRAM", nameof(vm.VramUsage), nameof(vm.VramName), null,
            Chart(vm.VramHistory, "#E3A21A", "VRAM"), Meta(vm, nameof(vm.VramDetails)));

        // Network: a dynamic scale (the top is the highest throughput of the last minute), its max and min at the right.
        var network = UsageChart.Of(vm.NetworkReceivedHistory, "#1FB5A8", history, vm.NetworkSentHistory, "#F28C38", vm, nameof(vm.NetworkScaleMax));
        network.SeriesName = "↓ in";
        network.SecondaryName = "↑ out";
        network.Format = Throughput;
        var legend = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,*,Auto"), Width = 80, MinHeight = 56 };
        void Place(Control control, int row)
        {
            Grid.SetRow(control, row);
            legend.Children.Add(control);
        }
        Place(DataUi.Bound(vm, nameof(vm.NetworkMaxLabel), Ui.Muted, 10), 0);
        Place(DataUi.Text("↓ in", new SolidColorBrush(Color.Parse("#1FB5A8")), 10), 2);
        Place(DataUi.Text("↑ out", new SolidColorBrush(Color.Parse("#F28C38")), 10), 3);
        Place(DataUi.Bound(vm, nameof(vm.NetworkMinLabel), Ui.Muted, 10), 5);
        legend.Margin = new Thickness(6, 0, 0, 0);
        var networkDetails = DataUi.Tip(Meta(vm, nameof(vm.NetworkDetails)), vm, nameof(vm.NetworkDetails));
        var net = Card(vm, "Network", nameof(vm.NetworkUsage), nameof(vm.NetworkName), null,
            Ui.Dock((legend, Dock.Right), (network, Dock.Left)), networkDetails);

        // GPU temperature (0–100 °C).
        var tempChart = Chart(vm.GpuTemperatureHistory, "#E8603C", "Temperature");
        tempChart.Format = v => $"{v:0} °C";
        var temp = Card(vm, "GPU temperature", nameof(vm.GpuTemperatureValue), nameof(vm.GpuTemperatureName), null,
            tempChart, Meta(vm, nameof(vm.GpuTemperatureDetails)), tooltip: nameof(vm.GpuTemperatureTip), valueColor: nameof(vm.GpuTemperatureBrush));

        // Drives.
        var drives = DataUi.Items(vm.Drives, Drive, spacing: 6);
        var drivesCaption = DataUi.Caption("Drives", Ui.Accent);
        drivesCaption.Margin = new Thickness(2, 8, 0, 0);

        var content = Ui.Column(8,
            DataUi.Cells(2, 8, 8, cpu, memory, gpu, vram),
            net,
            temp,
            drivesCaption,
            drives);
        content.Margin = new Thickness(8, 8, 12, 8);
        return Ui.Dock((DataUi.StatusBar(vm, nameof(vm.Status), null), Dock.Bottom), (Ui.Scroll(content), Dock.Top));
    }

    private static UsageChart Chart(IEnumerable<double> values, string color, string name)
    {
        var chart = UsageChart.Of(values, color, SystemWindowViewModel.HistoryLength);
        chart.SeriesName = name;
        chart.Format = v => $"{v:0.#} %";
        return chart;
    }

    private static string Throughput(double bitsPerSecond) => bitsPerSecond switch
    {
        >= 1e9 => $"{bitsPerSecond / 1e9:0.##} Gbit/s",
        >= 1e6 => $"{bitsPerSecond / 1e6:0.##} Mbit/s",
        >= 1e3 => $"{bitsPerSecond / 1e3:0.#} kbit/s",
        _ => $"{bitsPerSecond:0} bit/s"
    };

    private static TextBlock Meta(SystemWindowViewModel vm, string name) => DataUi.Bound(vm, name, Ui.Muted, 11);

    /// <summary>A card: its title and value, the hardware model (a property or a fixed text), the graph, the details.</summary>
    private static Border Card(SystemWindowViewModel vm, string title, string value, string? model, string? fixedModel,
        Control chart, Control details, string? tooltip = null, string? valueColor = null)
    {
        var big = DataUi.Bound(vm, value, Ui.TextBright, 18);
        if (valueColor is not null)
            big.Bind(TextBlock.ForegroundProperty, vm, valueColor, BindingMode.OneWay, v => v as IBrush ?? Ui.TextBright);
        var name = model is null ? DataUi.Text(fixedModel ?? string.Empty, Ui.Text, 11) : DataUi.Bound(vm, model, Ui.Text, 11);
        var tip = tooltip ?? model;
        if (tip is not null)
            DataUi.Tip(name, vm, tip);
        var header = DataUi.Bar([], DataUi.Caption(title, Ui.Accent, 12), big);
        return DataUi.Card(Ui.Column(4, header, name, chart, details), padding: 8);
    }

    /// <summary>A drive: its name and format, a bar of the used space, its free space.</summary>
    private static Control Drive(DriveItemViewModel drive)
    {
        var title = DataUi.Tip(DataUi.Text(drive.Title ?? string.Empty, Ui.TextBright, 12), drive.Title);
        var names = Ui.Column(0, title, DataUi.Text(drive.Details ?? string.Empty, Ui.Muted, 10));
        names.Width = 150;
        var bar = DataUi.Tip(DataUi.Share(drive.UsedPercent, 10), $"{drive.UsedPercent:0} % used");
        var capacity = DataUi.Text(drive.Capacity ?? string.Empty, Ui.Text, 11);
        return DataUi.Card(DataUi.Bar([names], bar, capacity), padding: 6);
    }
}
