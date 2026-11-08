using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// /process: the processes of KAONOMI (the engines, the server, the agents, the Fooocus API, Ollama) with their state
/// (a green dot when they run, red when not, amber while they start or stop), what they are, and START, STOP and
/// RESTART for each. Header: the folder of the projects, ↻ REFRESH, START CORE (the engines then the server), STOP ALL.
/// Status bar: what was done last. The output of a process started here goes to ~/kaonomi/logs.
/// </summary>
[ToolView(typeof(ProcessWindowViewModel), "Processes", "⚙", 980, 640)]
public sealed class ProcessView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (ProcessWindowViewModel)viewModel;

        var folder = DataUi.Tip(DataUi.Text(vm.ProjectsDirectory ?? string.Empty, Ui.Muted, 11),
            "The folder of the projects (the one holding KAONOMI.Server, found from the folder of the chat)");
        var header = DataUi.Toolbar(DataUi.Bar([DataUi.Caption("Processes", Ui.TextBright, 14)], folder,
            DataUi.Command("↻ REFRESH", vm.RefreshCommand, "Read the state of every process now (it is read every 3 seconds anyway)"),
            DataUi.Command("▶ START CORE", vm.StartCoreCommand, "Start what is missing: the two engines, then the server", primary: true),
            DataUi.Command("■ STOP ALL", vm.StopAllCommand, "Stop everything but Ollama: the agents and the Fooocus API, then the server, then the engines")));

        var list = Ui.Column(4);
        string? group = null;
        foreach (var process in vm.Processes)
        {
            if (process.Group != group)
            {
                group = process.Group;
                var caption = DataUi.Caption(group ?? string.Empty, Ui.Accent, 11);
                caption.Margin = new Thickness(4, 10, 4, 2);
                list.Children.Add(caption);
            }
            list.Children.Add(Row(vm, process));
        }
        list.Margin = new Thickness(10, 4, 10, 10);

        var status = DataUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsBusy));
        return Ui.Dock((header, Dock.Top), (status, Dock.Bottom), (Ui.Scroll(list), Dock.Top));
    }

    /// <summary>A process: its dot, its name, what it is, its state, START / STOP / RESTART.</summary>
    private static Control Row(ProcessWindowViewModel vm, ManagedProcess process)
    {
        var dot = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center };
        var name = DataUi.Text(process.Name ?? string.Empty, Ui.TextBright, 14, bold: true);
        name.MinWidth = 170;
        var description = Ui.Label(process.Description ?? string.Empty, Ui.Text, 11, wrap: true);
        description.Opacity = 0.85;
        var detail = DataUi.Bound(process, nameof(process.Detail), Ui.Muted, 11);
        detail.FontFamily = Ui.Mono;
        var texts = Ui.Column(2, Ui.Row(10, name, detail), description);

        // Enabled by the state of the process (not by the commands, shared by every process).
        var start = DataUi.Action("▶ START", () => Ui.Run(vm.StartCommand, process), $"Start {process.Name} (its output in ~/kaonomi/logs/{process.Name}.log)");
        var stop = DataUi.Action("■ STOP", () => Ui.Run(vm.StopCommand, process), $"Stop {process.Name}");
        var restart = DataUi.Action("↻ RESTART", () => Ui.Run(vm.RestartCommand, process), $"Stop then start {process.Name}");
        var buttons = Ui.Row(6, start, stop, restart);
        buttons.VerticalAlignment = VerticalAlignment.Center;

        var row = Ui.Dock((dot, Dock.Left), (buttons, Dock.Right), (texts, Dock.Left));
        dot.Margin = new Thickness(0, 0, 12, 0);
        texts.Margin = new Thickness(0, 0, 12, 0);
        var panel = Ui.Box(row, 8, Ui.Field);
        panel.Padding = new Thickness(10, 6);

        void Look()
        {
            dot.Background = process.IsBusy ? DataUi.Amber : process.IsRunning ? Ui.Green : Ui.Red;
            start.IsEnabled = !process.IsBusy && !process.IsRunning && process.CanStart;
            stop.IsEnabled = !process.IsBusy && process.IsRunning && process.CanStop;
            restart.IsEnabled = !process.IsBusy && process.CanStart && process.CanStop;
        }
        Ui.When(panel, process, Look, nameof(process.IsRunning), nameof(process.IsBusy));
        return panel;
    }
}
