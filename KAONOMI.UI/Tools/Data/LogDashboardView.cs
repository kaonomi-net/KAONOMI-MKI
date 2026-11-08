using System;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// /log: the most recent logs. Header: status (busy, error dot), the period read (GET /log/last), auto-refresh (15 s),
/// REFRESH, SAVE (the content of the window as JSON) and ERASE ALL. Key figures: logs, errors, applications, time span,
/// rate. Activity graph (all logs / errors), the applications (a click filters on one, a second click shows them all)
/// and the most frequent errors (a click shows their logs). The table of the logs (date, type badge, application,
/// message; the message as tooltip; double-click shows the whole log) with the search, type and application filters
/// and Clear filters. The data is read when the window opens.
/// </summary>
[ToolView(typeof(LogDashboardViewModel), "Logs dashboard", "📜", 1400, 900)]
public sealed class LogDashboardView : IToolView
{
    private static readonly IBrush InfoBadge = DataUi.Fade(DataUi.Blue, 0.27);
    private static readonly IBrush ErrorBadge = DataUi.Fade(DataUi.ErrorRed, 0.4);

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (LogDashboardViewModel)viewModel;

        // Header.
        var status = DataUi.ErrorColor(DataUi.Bound(vm, nameof(vm.Status), Ui.Text, 12), vm, nameof(vm.IsError), Ui.Text);
        var scopes = DataUi.Tip(DataUi.Options(vm.Scopes, vm, nameof(vm.Scope), s => s.Label, 170), "Number of logs read with GET /log/last");
        var save = DataUi.Action("SAVE", () => Save(vm), "Saves the content of the window (figures, applications, errors and the logs of the table) as JSON");
        var eraseAll = DataUi.Command("🗑 ERASE ALL", vm.DeleteAllCommand, "Erases ALL the logs of every application on the server (asked first; they cannot be brought back)");
        eraseAll.Foreground = Ui.Red;
        var header = DataUi.Toolbar(DataUi.Bar([DataUi.Dot(Ui.Red, vm, nameof(vm.IsError)), DataUi.Busy(vm.RefreshCommand)], status,
            DataUi.Caption("Period"),
            scopes,
            DataUi.Check("Auto-refresh (15 s)", vm, nameof(vm.AutoRefresh)),
            DataUi.Command("REFRESH", vm.RefreshCommand, primary: true),
            save,
            eraseAll));

        // Key figures.
        var figures = DataUi.Cells(5, 8, 8,
            DataUi.Kpi(vm, "Logs", nameof(vm.TotalText), nameof(vm.TotalDetails)),
            DataUi.Kpi(vm, "Errors", nameof(vm.ErrorText), nameof(vm.ErrorDetails), colorProperty: nameof(vm.ErrorBrush)),
            DataUi.Kpi(vm, "Applications", nameof(vm.AppsText), nameof(vm.AppsDetails)),
            DataUi.Kpi(vm, "Time span", nameof(vm.SpanText), nameof(vm.SpanDetails)),
            DataUi.Kpi(vm, "Rate", nameof(vm.RateText), nameof(vm.RateDetails)));

        // Activity.
        var chart = UsageChart.Of(vm.Activity, "#2B9BF4", LogDashboardViewModel.Buckets, vm.ErrorActivity, "#E81123", vm, nameof(vm.ActivityMax));
        chart.SeriesName = "Logs";
        chart.SecondaryName = "Errors";
        chart.Format = v => $"{v:N0}";
        var max = DataUi.Bound(vm, nameof(vm.ActivityMaxLabel), Ui.Muted, 10);
        max.VerticalAlignment = VerticalAlignment.Top;
        max.Margin = new Thickness(6, 0, 0, 0);
        var range = DataUi.Bar([], DataUi.Bound(vm, nameof(vm.ActivityStart), Ui.Muted, 11), DataUi.Bound(vm, nameof(vm.ActivityEnd), Ui.Muted, 11));
        range.Margin = new Thickness(0, 4, 0, 0);
        var activity = DataUi.Card(Ui.Dock((range, Dock.Bottom), (max, Dock.Right), (chart, Dock.Left)),
            vm, nameof(vm.ActivityTitle), DataUi.Text("■ all logs", DataUi.Blue, 11), DataUi.Text("■ errors", DataUi.ErrorRed, 11));

        // By application.
        var apps = DataUi.Card(Ui.Scroll(DataUi.Items(vm.AppStats, app => AppRow(vm, app))), "By application (click to filter)");

        // Most frequent errors.
        var errors = DataUi.Card(DataUi.ScrollList(vm.TopErrors, error => ErrorRow(vm, error), "No error 🎉"), "Most frequent errors (click to filter)");

        var middle = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,*,*"), ColumnSpacing = 8, Height = 250 };
        Grid.SetColumn(apps, 1);
        Grid.SetColumn(errors, 2);
        middle.Children.Add(activity);
        middle.Children.Add(apps);
        middle.Children.Add(errors);

        // Filters and table.
        var search = DataUi.Line(vm, nameof(vm.Search), "Search in the messages and application names…");
        var types = DataUi.Options(vm.Types, vm, nameof(vm.SelectedType), minWidth: 120);
        var appFilter = DataUi.Options(vm.Apps, vm, nameof(vm.SelectedApp), minWidth: 260);
        appFilter.MaxWidth = 320;
        var tableTitle = DataUi.Bound(vm, nameof(vm.TableTitle), Ui.Muted, 10, format: v => (v?.ToString() ?? string.Empty).ToUpperInvariant());
        tableTitle.MinWidth = 150;
        var table = new VirtualTable { EmptyText = "No log", Tooltip = row => ((LogRowViewModel)row).Comment };
        table.Columns(
            new TableColumn("Date", 185, row => ((LogRowViewModel)row).Date) { Mono = true },
            new TableColumn("Type", 70, row => ((LogRowViewModel)row).Type) { Badge = row => ((LogRowViewModel)row).IsError ? ErrorBadge : InfoBadge },
            new TableColumn("Application", 300, row => ((LogRowViewModel)row).App),
            new TableColumn("Message", 0, row => ((LogRowViewModel)row).Comment) { Color = row => ((LogRowViewModel)row).IsError ? DataUi.ErrorLight : null });
        table.CopyText = row => row is LogRowViewModel log ? $"{log.Date}\t{log.Type}\t{log.App}\t{log.Comment}" : string.Empty;
        table.Activated += row =>
        {
            if (row is LogRowViewModel log)
                _ = Dialogs.Message($"{log.Type} · {log.App}", $"{log.Date}\n\n{log.Comment}");
        };
        Ui.When(table, vm, () => table.SetRows(vm.Rows), nameof(vm.Rows));
        var filters = DataUi.Bar([tableTitle], search, types, appFilter, DataUi.Command("CLEAR FILTERS", vm.ClearFiltersCommand));
        filters.Margin = new Thickness(0, 0, 0, 8);
        var logs = Ui.Box(Ui.Dock((filters, Dock.Top), (table, Dock.Top)), 10);

        figures.Margin = new Thickness(0, 0, 0, 8);
        middle.Margin = new Thickness(0, 0, 0, 8);
        var body = Ui.Dock((figures, Dock.Top), (middle, Dock.Top), (logs, Dock.Top));
        body.Margin = new Thickness(8, 8, 8, 8);
        var page = Ui.Dock((header, Dock.Top), (body, Dock.Top));

        // The data is read once, when the window opens.
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

    /// <summary>An application: its name, its count (red with errors), a bar of its share; a click filters on it.</summary>
    private static Control AppRow(LogDashboardViewModel vm, AppStatViewModel app)
    {
        var name = DataUi.Text(app.App ?? string.Empty, Ui.Text, 11);
        var count = DataUi.Text(app.CountText ?? string.Empty, app.HasErrors ? DataUi.ErrorLight : Ui.Text, 11);
        var content = Ui.Column(2, DataUi.Bar([], name, count), DataUi.Share(app.Share));
        var row = DataUi.ClickRow(content, () => Ui.Run(vm.FilterAppCommand, app), padding: 4);
        Ui.When(row, vm, () =>
        {
            var on = vm.SelectedApp == app.App;
            row.Background = on ? DataUi.Fade(Ui.Accent, 0.16) : Brushes.Transparent;
            row.BorderBrush = on ? Ui.Accent : Brushes.Transparent;
        }, nameof(vm.SelectedApp));
        return DataUi.Tip(row, app.App);
    }

    /// <summary>An error message: the message (red, two lines), how many, where and when; a click shows its logs.</summary>
    private static Control ErrorRow(LogDashboardViewModel vm, ErrorStatViewModel error)
    {
        var message = new TextBlock
        {
            Text = error.Message,
            Foreground = DataUi.ErrorLight,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var details = DataUi.Text(error.Details ?? string.Empty, Ui.Muted, 11);
        var row = DataUi.ClickRow(Ui.Column(1, message, details), () => Ui.Run(vm.FilterErrorCommand, error), padding: 4);
        row.Background = Brushes.Transparent;
        row.BorderBrush = Brushes.Transparent;
        return DataUi.Tip(row, error.Message);
    }

    /// <summary>SAVE: the content of the window to a JSON file chosen by the user.</summary>
    private static async void Save(LogDashboardViewModel vm)
    {
        try
        {
            var path = await Dialogs.SaveFile("Save the logs dashboard", $"logs-dashboard-{DateTime.Now:yyyyMMdd-HHmmss}.json", ["*.json ; JSON"]);
            if (path is null)
                return;
            if (Path.GetExtension(path).Length == 0)
                path += ".json";
            var export = vm.BuildExport();
            await using (var stream = File.Create(path))
                await JsonSerializer.SerializeAsync(stream, export, new JsonSerializerOptions { WriteIndented = true });
            vm.Status = $"Saved to {Path.GetFileName(path)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            vm.Status = $"Save failed: {ex.Message}";
        }
    }
}
