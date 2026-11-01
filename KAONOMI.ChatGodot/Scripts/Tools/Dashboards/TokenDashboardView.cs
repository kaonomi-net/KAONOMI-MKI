using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;

namespace KAONOMI.ChatGodot.Tools.Dashboards;

/// <summary>
/// /token: the tokens and models consumed. Header: status (busy, error dot), the period (GET /tokensusage/last),
/// auto-refresh (30 s), REFRESH. Key figures: tokens, prompt tokens, completion tokens, LLM calls, models, rate. The
/// tokens over time (all / completion), by model and by application (share bars, details as tooltip), and the table
/// of the LLM calls (time, model, application, prompt, completion, total). The data is read when the window opens.
/// </summary>
[ToolView(typeof(TokenDashboardViewModel), "Tokens dashboard", "🪙", 1400, 900)]
public sealed class TokenDashboardView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (TokenDashboardViewModel)viewModel;

        var status = Ui.Label(vm, nameof(vm.Status), EveStyle.Text, 12);
        status.ClipText = true;
        status.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        Bind.When(status, vm, () => status.AddThemeColorOverride("font_color", vm.IsError ? EveStyle.Red : EveStyle.Text), nameof(vm.IsError));
        var periods = Bind.Options(new OptionButton { CustomMinimumSize = new Vector2(160, 0), TooltipText = "Period read with GET /tokensusage/last" }, vm.Periods, vm, nameof(vm.Period), p => p.Label);
        var header = Ui.Toolbar(Ui.Row(8,
            DashUi.Dot(EveStyle.Red, vm, nameof(vm.IsError)),
            DashUi.Busy(vm.RefreshCommand),
            Ui.Fill(status),
            EveStyle.Caption("Period"),
            periods,
            Ui.Check("Auto-refresh (30 s)", vm, nameof(vm.AutoRefresh)),
            Ui.Command("REFRESH", vm.RefreshCommand, primary: true)));

        var figures = Ui.Grid(6, 8, 8);
        figures.AddChild(Ui.Fill(DashUi.Kpi(vm, "Tokens", nameof(vm.TotalText), nameof(vm.TotalDetails))));
        figures.AddChild(Ui.Fill(DashUi.Kpi(vm, "Prompt tokens", nameof(vm.PromptText), nameof(vm.PromptDetails), DashUi.Blue)));
        figures.AddChild(Ui.Fill(DashUi.Kpi(vm, "Completion tokens", nameof(vm.CompletionText), nameof(vm.CompletionDetails), DashUi.Orange)));
        figures.AddChild(Ui.Fill(DashUi.Kpi(vm, "LLM calls", nameof(vm.CallsText), nameof(vm.CallsDetails))));
        figures.AddChild(Ui.Fill(DashUi.Kpi(vm, "Models", nameof(vm.ModelsText), nameof(vm.ModelsDetails))));
        figures.AddChild(Ui.Fill(DashUi.Kpi(vm, "Rate", nameof(vm.RateText), nameof(vm.RateDetails))));

        // Tokens over time.
        var chart = UsageChart.Of(vm.TokensOverTime, DashUi.Blue, TokenDashboardViewModel.Buckets, vm.CompletionOverTime, DashUi.Orange, vm, nameof(vm.ChartMax));
        chart.SeriesName = "Tokens";
        chart.SecondaryName = "Completion";
        chart.Format = v => $"{v:N0}";
        chart.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var overTime = DashUi.Card(Ui.Column(4,
                Ui.FillHeight(Ui.Row(6, Ui.Fill(chart), Top(Ui.Label(vm, nameof(vm.ChartMaxLabel), EveStyle.Muted, 10)))),
                Ui.Row(6, Ui.Fill(Ui.Label(vm, nameof(vm.ChartStart), EveStyle.Muted, 11)), Ui.Label(vm, nameof(vm.ChartEnd), EveStyle.Muted, 11))),
            vm, nameof(vm.ChartTitle), EveStyle.Text_("■ all tokens", DashUi.Blue, 11), EveStyle.Text_("■ completion", DashUi.Orange, 11));
        overTime.SizeFlagsStretchRatio = 2;

        var models = Ui.Column(8);
        Bind.Items(models, vm.Models, model => Stat(model, true));
        var byModel = DashUi.Card(Ui.FillHeight(Ui.Scroll(models)), "By model");
        var apps = Ui.Column(6);
        Bind.Items(apps, vm.Apps, app => Stat(app, false));
        var byApp = DashUi.Card(Ui.FillHeight(Ui.Scroll(apps)), "By application");

        var middle = Ui.Row(8, Ui.Fill(overTime, true), Ui.Fill(byModel, true), Ui.Fill(byApp, true));
        middle.CustomMinimumSize = new Vector2(0, 270);

        // The LLM calls.
        var table = new VirtualTable { EmptyText = "No LLM call in this period" };
        table.Columns(
            new TableColumn("Time", 110, row => ((TokenCallViewModel)row).Date) { Mono = true },
            new TableColumn("Model", 280, row => ((TokenCallViewModel)row).Model),
            new TableColumn("Application", 0, row => ((TokenCallViewModel)row).App),
            new TableColumn("Prompt", 120, row => ((TokenCallViewModel)row).Prompt) { Right = true, Color = _ => DashUi.Blue },
            new TableColumn("Completion", 120, row => ((TokenCallViewModel)row).Completion) { Right = true, Color = _ => DashUi.Orange },
            new TableColumn("Total", 120, row => ((TokenCallViewModel)row).Total) { Right = true, Bold = true, Color = _ => EveStyle.TextBright });
        table.Tooltip = row => row is TokenCallViewModel call ? $"{call.Model} · {call.App}\n{call.Prompt} prompt + {call.Completion} completion = {call.Total}" : string.Empty;
        table.Follow(vm.Calls);
        var calls = DashUi.Card(table, vm, nameof(vm.CallsTitle));
        calls.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        var page = Ui.Column(8, header, Ui.FillHeight(Ui.Margin(Ui.Column(8, figures, middle, Ui.FillHeight(calls)), 8, 0, 8, 8)));
        page.Ready += () => Bind.Run(vm.RefreshCommand);
        return page;
    }

    private static Label Top(Label label)
    {
        label.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        return label;
    }

    /// <summary>A model or an application: its name and total, a bar of its share, its details (also as tooltip).</summary>
    private static Control Stat(TokenStatViewModel stat, bool detailed)
    {
        var name = EveStyle.Text_(stat.Name, detailed ? EveStyle.TextBright : EveStyle.Text, detailed ? 12 : 11);
        name.ClipText = true;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var column = Ui.Column(2, Ui.Row(6, name, EveStyle.Text_(stat.TotalText, EveStyle.Text, detailed ? 12 : 11)), DashUi.Share(stat.Share, detailed ? 6 : 5));
        if (detailed)
        {
            var details = EveStyle.Text_(stat.Details, EveStyle.Muted, 11);
            details.ClipText = true;
            details.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            column.AddChild(details);
        }
        column.TooltipText = stat.Details;
        column.MouseFilter = Control.MouseFilterEnum.Stop;
        foreach (var child in column.GetChildren())
            if (child is Control c)
                c.MouseFilter = Control.MouseFilterEnum.Pass;
        return column;
    }
}
