using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

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

        var status = DataUi.ErrorColor(DataUi.Bound(vm, nameof(vm.Status), Ui.Text, 12), vm, nameof(vm.IsError), Ui.Text);
        var periods = DataUi.Tip(DataUi.Options(vm.Periods, vm, nameof(vm.Period), p => p.Label, 160), "Period read with GET /tokensusage/last");
        var header = DataUi.Toolbar(DataUi.Bar([DataUi.Dot(Ui.Red, vm, nameof(vm.IsError)), DataUi.Busy(vm.RefreshCommand)], status,
            DataUi.Caption("Period"),
            periods,
            DataUi.Check("Auto-refresh (30 s)", vm, nameof(vm.AutoRefresh)),
            DataUi.Command("REFRESH", vm.RefreshCommand, primary: true)));

        var figures = DataUi.Cells(6, 8, 8,
            DataUi.Kpi(vm, "Tokens", nameof(vm.TotalText), nameof(vm.TotalDetails)),
            DataUi.Kpi(vm, "Prompt tokens", nameof(vm.PromptText), nameof(vm.PromptDetails), DataUi.Blue),
            DataUi.Kpi(vm, "Completion tokens", nameof(vm.CompletionText), nameof(vm.CompletionDetails), DataUi.Orange),
            DataUi.Kpi(vm, "LLM calls", nameof(vm.CallsText), nameof(vm.CallsDetails)),
            DataUi.Kpi(vm, "Models", nameof(vm.ModelsText), nameof(vm.ModelsDetails)),
            DataUi.Kpi(vm, "Rate", nameof(vm.RateText), nameof(vm.RateDetails)));

        // Tokens over time.
        var chart = UsageChart.Of(vm.TokensOverTime, "#2B9BF4", TokenDashboardViewModel.Buckets, vm.CompletionOverTime, "#F28C38", vm, nameof(vm.ChartMax));
        chart.SeriesName = "Tokens";
        chart.SecondaryName = "Completion";
        chart.Format = v => $"{v:N0}";
        var max = DataUi.Bound(vm, nameof(vm.ChartMaxLabel), Ui.Muted, 10);
        max.VerticalAlignment = VerticalAlignment.Top;
        max.Margin = new Thickness(6, 0, 0, 0);
        var range = DataUi.Bar([], DataUi.Bound(vm, nameof(vm.ChartStart), Ui.Muted, 11), DataUi.Bound(vm, nameof(vm.ChartEnd), Ui.Muted, 11));
        range.Margin = new Thickness(0, 4, 0, 0);
        var overTime = DataUi.Card(Ui.Dock((range, Dock.Bottom), (max, Dock.Right), (chart, Dock.Left)),
            vm, nameof(vm.ChartTitle), DataUi.Text("■ all tokens", DataUi.Blue, 11), DataUi.Text("■ completion", DataUi.Orange, 11));

        var byModel = DataUi.Card(Ui.Scroll(DataUi.Items(vm.Models, model => Stat(model, true), spacing: 8)), "By model");
        var byApp = DataUi.Card(Ui.Scroll(DataUi.Items(vm.Apps, app => Stat(app, false), spacing: 6)), "By application");

        var middle = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,*,*"), ColumnSpacing = 8, Height = 270 };
        Grid.SetColumn(byModel, 1);
        Grid.SetColumn(byApp, 2);
        middle.Children.Add(overTime);
        middle.Children.Add(byModel);
        middle.Children.Add(byApp);

        // The LLM calls.
        var table = new VirtualTable { EmptyText = "No LLM call in this period" };
        table.Columns(
            new TableColumn("Time", 110, row => ((TokenCallViewModel)row).Date) { Mono = true },
            new TableColumn("Model", 280, row => ((TokenCallViewModel)row).Model),
            new TableColumn("Application", 0, row => ((TokenCallViewModel)row).App),
            new TableColumn("Prompt", 120, row => ((TokenCallViewModel)row).Prompt) { Right = true, Color = _ => DataUi.Blue },
            new TableColumn("Completion", 120, row => ((TokenCallViewModel)row).Completion) { Right = true, Color = _ => DataUi.Orange },
            new TableColumn("Total", 120, row => ((TokenCallViewModel)row).Total) { Right = true, Bold = true, Color = _ => Ui.TextBright });
        table.Tooltip = row => row is TokenCallViewModel call ? $"{call.Model} · {call.App}\n{call.Prompt} prompt + {call.Completion} completion = {call.Total}" : string.Empty;
        table.Follow(vm.Calls);
        var calls = DataUi.Card(table, vm, nameof(vm.CallsTitle));

        figures.Margin = new Thickness(0, 0, 0, 8);
        middle.Margin = new Thickness(0, 0, 0, 8);
        var body = Ui.Dock((figures, Dock.Top), (middle, Dock.Top), (calls, Dock.Top));
        body.Margin = new Thickness(8);
        var page = Ui.Dock((header, Dock.Top), (body, Dock.Top));
        var read = false;
        page.AttachedToVisualTree += (_, _) =>
        {
            if (read)
                return;
            read = true;
            Ui.Run(vm.RefreshCommand);
        };
        return page;
    }

    /// <summary>A model or an application: its name and total, a bar of its share, its details (also as tooltip).</summary>
    private static Control Stat(TokenStatViewModel stat, bool detailed)
    {
        var name = DataUi.Text(stat.Name ?? string.Empty, detailed ? Ui.TextBright : Ui.Text, detailed ? 12 : 11);
        var column = Ui.Column(2, DataUi.Bar([], name, DataUi.Text(stat.TotalText ?? string.Empty, Ui.Text, detailed ? 12 : 11)), DataUi.Share(stat.Share, detailed ? 6 : 5));
        if (detailed)
            column.Children.Add(DataUi.Text(stat.Details ?? string.Empty, Ui.Muted, 11));
        column.Background = Avalonia.Media.Brushes.Transparent;
        return DataUi.Tip(column, stat.Details);
    }
}
