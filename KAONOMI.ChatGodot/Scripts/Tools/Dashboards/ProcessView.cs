using System.Linq;
using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;

namespace KAONOMI.ChatGodot.Tools.Dashboards;

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

        var folder = EveStyle.Text_(vm.ProjectsDirectory, EveStyle.Muted, 11);
        folder.ClipText = true;
        folder.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        folder.TooltipText = "The folder of the projects (the one holding KAONOMI.Server, found from the folder of the chat)";
        folder.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var header = Ui.Toolbar(Ui.Row(10,
            EveStyle.Caption("Processes", EveStyle.TextBright, 14),
            folder,
            Ui.Command("↻ REFRESH", vm.RefreshCommand, tooltip: "Read the state of every process now (it is read every 3 seconds anyway)"),
            Ui.Command("▶ START CORE", vm.StartCoreCommand, primary: true, tooltip: "Start what is missing: the two engines, then the server"),
            Ui.Command("■ STOP ALL", vm.StopAllCommand, tooltip: "Stop everything but Ollama: the agents and the Fooocus API, then the server, then the engines")));

        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 4);
        string? group = null;
        foreach (var process in vm.Processes)
        {
            if (process.Group != group)
            {
                group = process.Group;
                var caption = EveStyle.Caption(group.ToUpperInvariant(), EveStyle.Cyan, 11);
                list.AddChild(Ui.Margin(caption, 4, 10, 4, 2));
            }
            list.AddChild(Row(vm, process));
        }
        var scroll = Ui.Scroll(Ui.Margin(list, 10, 4, 10, 10));
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        var status = Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsBusy));
        var page = Ui.Column(0, header, scroll, status);
        page.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        return page;
    }

    /// <summary>A process: its dot, its name, what it is, its state, START / STOP / RESTART.</summary>
    private static Control Row(ProcessWindowViewModel vm, ManagedProcess process)
    {
        var dot = new ColorRect { CustomMinimumSize = new Vector2(12, 12), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        var name = EveStyle.Text_(process.Name, EveStyle.TextBright, 14);
        name.AddThemeFontOverride("font", EveStyle.Display);
        name.CustomMinimumSize = new Vector2(170, 0);
        var description = EveStyle.Text_(process.Description, EveStyle.Text with { A = 0.8f }, 11, wrap: true);
        description.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var detail = Ui.Label(process, nameof(process.Detail), EveStyle.Muted, 11);
        detail.AddThemeFontOverride("font", EveStyle.Mono);
        var texts = Ui.Column(2, Ui.Row(10, name, detail), description);
        texts.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var start = Ui.Command("▶ START", vm.StartCommand, tooltip: $"Start {process.Name} (its output in ~/kaonomi/logs/{process.Name}.log)", parameter: () => process);
        var stop = Ui.Command("■ STOP", vm.StopCommand, tooltip: $"Stop {process.Name}", parameter: () => process);
        var restart = Ui.Command("↻ RESTART", vm.RestartCommand, tooltip: $"Stop then start {process.Name}", parameter: () => process);
        foreach (var button in new[] { start, stop, restart })
            button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        var buttons = Ui.Row(6, start, stop, restart);

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", EveStyle.Box(EveStyle.Field, EveStyle.Edge with { A = 0.4f }, 8));
        panel.AddChild(Ui.Margin(Ui.Row(12, dot, texts, buttons), 10, 6, 10, 6));

        void Look()
        {
            dot.Color = process.IsBusy ? EveStyle.Amber : process.IsRunning ? EveStyle.Green : EveStyle.Red;
            start.Disabled = process.IsBusy || process.IsRunning || !process.CanStart;
            stop.Disabled = process.IsBusy || !process.IsRunning || !process.CanStop;
            restart.Disabled = process.IsBusy || !process.CanStart || !process.CanStop;
        }
        Bind.When(panel, process, Look, nameof(process.IsRunning), nameof(process.IsBusy));
        return panel;
    }
}
