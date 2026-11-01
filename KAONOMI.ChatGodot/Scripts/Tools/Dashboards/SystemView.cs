using System.Collections.Generic;
using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;

namespace KAONOMI.ChatGodot.Tools.Dashboards;

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
        var temperature = Ui.Label(vm, nameof(vm.GpuTemperature), EveStyle.Text, 12);
        Bind.When(temperature, vm, () =>
        {
            temperature.AddThemeColorOverride("font_color", AvaloniaBridge.ToColor(vm.GpuTemperatureBrush));
            temperature.TooltipText = vm.GpuTemperatureTip;
            temperature.MouseFilter = Control.MouseFilterEnum.Stop;
        }, nameof(vm.GpuTemperatureBrush), nameof(vm.GpuTemperatureTip));
        var gpu = Card(vm, "GPU", nameof(vm.GpuUsage), nameof(vm.GpuName), null,
            Chart(vm.GpuHistory, "#3DBB4A", "GPU"), Ui.Row(8, Ui.Fill(Meta(vm, nameof(vm.GpuDetails))), temperature));
        var vram = Card(vm, "VRAM", nameof(vm.VramUsage), nameof(vm.VramName), null,
            Chart(vm.VramHistory, "#E3A21A", "VRAM"), Meta(vm, nameof(vm.VramDetails)));

        // Network: a dynamic scale (the top is the highest throughput of the last minute), its max and min at the right.
        var network = UsageChart.Of(vm.NetworkReceivedHistory, Color.FromHtml("#1FB5A8"), history, vm.NetworkSentHistory, Color.FromHtml("#F28C38"), vm, nameof(vm.NetworkScaleMax));
        network.SeriesName = "↓ in";
        network.SecondaryName = "↑ out";
        network.Format = Throughput;
        var legend = Ui.Column(0,
            Ui.Label(vm, nameof(vm.NetworkMaxLabel), EveStyle.Muted, 10),
            Ui.FillHeight(new Control { MouseFilter = Control.MouseFilterEnum.Ignore }),
            EveStyle.Text_("↓ in", Color.FromHtml("#1FB5A8"), 10),
            EveStyle.Text_("↑ out", Color.FromHtml("#F28C38"), 10),
            Ui.FillHeight(new Control { MouseFilter = Control.MouseFilterEnum.Ignore }),
            Ui.Label(vm, nameof(vm.NetworkMinLabel), EveStyle.Muted, 10));
        legend.CustomMinimumSize = new Vector2(70, 56);
        var networkDetails = Meta(vm, nameof(vm.NetworkDetails));
        Bind.When(networkDetails, vm, () => networkDetails.TooltipText = vm.NetworkDetails, nameof(vm.NetworkDetails));
        networkDetails.MouseFilter = Control.MouseFilterEnum.Stop;
        var net = Card(vm, "Network", nameof(vm.NetworkUsage), nameof(vm.NetworkName), null,
            Ui.Row(6, Ui.Fill(network), legend), networkDetails);

        // GPU temperature (0–100 °C).
        var tempChart = Chart(vm.GpuTemperatureHistory, "#E8603C", "Temperature");
        tempChart.Format = v => $"{v:0} °C";
        var temp = Card(vm, "GPU temperature", nameof(vm.GpuTemperatureValue), nameof(vm.GpuTemperatureName), null,
            tempChart, Meta(vm, nameof(vm.GpuTemperatureDetails)), tooltip: nameof(vm.GpuTemperatureTip), valueColor: nameof(vm.GpuTemperatureBrush));

        // Drives.
        var drives = Ui.Column(6);
        Bind.Items(drives, vm.Drives, Drive);

        var content = Ui.Column(8,
            Ui.Row(8, Ui.Fill(cpu), Ui.Fill(memory)),
            Ui.Row(8, Ui.Fill(gpu), Ui.Fill(vram)),
            net,
            temp,
            Ui.Margin(EveStyle.Caption("Drives", EveStyle.Cyan), 2, 8, 0, 0),
            drives);
        return Ui.Column(6,
            Ui.FillHeight(Ui.Scroll(Ui.Margin(content, 8, 8, 12, 8))),
            Ui.StatusBar(vm, nameof(vm.Status), null));
    }

    private static UsageChart Chart(IEnumerable<double> values, string color, string name)
    {
        var chart = UsageChart.Of(values, Color.FromHtml(color), SystemWindowViewModel.HistoryLength);
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

    private static Label Meta(SystemWindowViewModel vm, string name)
    {
        var label = Ui.Label(vm, name, EveStyle.Muted, 11);
        label.ClipText = true;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return label;
    }

    /// <summary>A card: its title and value, the hardware model (a property or a fixed text), the graph, the details.</summary>
    private static PanelContainer Card(SystemWindowViewModel vm, string title, string value, string? model, string? fixedModel,
        Control chart, Control details, string? tooltip = null, string? valueColor = null)
    {
        var big = Ui.Label(vm, value, EveStyle.TextBright, 18);
        big.HorizontalAlignment = HorizontalAlignment.Right;
        if (valueColor is not null)
            Bind.When(big, vm, () => big.AddThemeColorOverride("font_color", AvaloniaBridge.ToColor(Bind.Get(vm, valueColor) as Avalonia.Media.IBrush)), valueColor);
        var name = model is null ? EveStyle.Text_(fixedModel ?? string.Empty, EveStyle.Text, 11) : Ui.Label(vm, model, EveStyle.Text, 11);
        name.ClipText = true;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        name.MouseFilter = Control.MouseFilterEnum.Stop;
        var tip = tooltip ?? model;
        if (tip is not null)
            Bind.When(name, vm, () => name.TooltipText = Bind.Get(vm, tip)?.ToString() ?? string.Empty, tip);
        var header = Ui.Row(8, Ui.Fill(EveStyle.Caption(title, EveStyle.Cyan, 12)), big);
        return DashUi.Card(Ui.Column(4, header, name, chart, details), padding: 8);
    }

    /// <summary>A drive: its name and format, a bar of the used space, its free space.</summary>
    private static Control Drive(DriveItemViewModel drive)
    {
        var title = EveStyle.Text_(drive.Title, EveStyle.TextBright, 12);
        title.ClipText = true;
        title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        title.TooltipText = drive.Title;
        title.MouseFilter = Control.MouseFilterEnum.Stop;
        var names = Ui.Column(0, title, EveStyle.Text_(drive.Details, EveStyle.Muted, 10));
        names.CustomMinimumSize = new Vector2(150, 0);
        var bar = DashUi.Share(drive.UsedPercent, 10);
        bar.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        bar.TooltipText = $"{drive.UsedPercent:0} % used";
        bar.MouseFilter = Control.MouseFilterEnum.Stop;
        var capacity = EveStyle.Text_(drive.Capacity, EveStyle.Text, 11);
        capacity.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        return DashUi.Card(Ui.Row(10, names, bar, capacity), padding: 6);
    }
}
