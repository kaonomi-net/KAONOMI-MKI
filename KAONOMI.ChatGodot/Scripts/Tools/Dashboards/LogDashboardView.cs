using System;
using System.IO;
using System.Text.Json;
using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;

namespace KAONOMI.ChatGodot.Tools.Dashboards;

/// <summary>
/// /log: the most recent logs. Header: status (busy, error dot), the period read (GET /log/last), auto-refresh (15 s),
/// REFRESH and SAVE (the content of the window as JSON). Key figures: logs, errors, applications, time span, rate.
/// Activity graph (all logs / errors), the applications (a click filters on one, a second click shows them all) and
/// the most frequent errors (a click shows their logs). The table of the logs (date, type badge, application,
/// message; the message as tooltip; double-click shows the whole log) with the search, type and application filters
/// and Clear filters. The data is read when the window opens.
/// </summary>
[ToolView(typeof(LogDashboardViewModel), "Logs dashboard", "📜", 1400, 900)]
public sealed class LogDashboardView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (LogDashboardViewModel)viewModel;

        // Header.
        var status = Ui.Label(vm, nameof(vm.Status), EveStyle.Text, 12);
        status.ClipText = true;
        status.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        Bind.When(status, vm, () => status.AddThemeColorOverride("font_color", vm.IsError ? EveStyle.Red : EveStyle.Text), nameof(vm.IsError));
        var scopes = Bind.Options(new OptionButton { CustomMinimumSize = new Vector2(170, 0), TooltipText = "Number of logs read with GET /log/last" }, vm.Scopes, vm, nameof(vm.Scope), s => s.Label);
        var save = Ui.Action("SAVE", () => Save(vm), tooltip: "Saves the content of the window (figures, applications, errors and the logs of the table) as JSON");
        var eraseAll = Ui.Command("🗑 ERASE ALL", vm.DeleteAllCommand, tooltip: "Erases ALL the logs of every application on the server (asked first; they cannot be brought back)");
        eraseAll.AddThemeColorOverride("font_color", EveStyle.Red);
        var header = Ui.Toolbar(Ui.Row(8,
            DashUi.Dot(EveStyle.Red, vm, nameof(vm.IsError)),
            DashUi.Busy(vm.RefreshCommand),
            Ui.Fill(status),
            EveStyle.Caption("Period"),
            scopes,
            Ui.Check("Auto-refresh (15 s)", vm, nameof(vm.AutoRefresh)),
            Ui.Command("REFRESH", vm.RefreshCommand, primary: true),
            save,
            eraseAll));

        // Key figures.
        var figures = Ui.Grid(5, 8, 8);
        figures.AddChild(Ui.Fill(DashUi.Kpi(vm, "Logs", nameof(vm.TotalText), nameof(vm.TotalDetails))));
        figures.AddChild(Ui.Fill(DashUi.Kpi(vm, "Errors", nameof(vm.ErrorText), nameof(vm.ErrorDetails), colorProperty: nameof(vm.ErrorBrush))));
        figures.AddChild(Ui.Fill(DashUi.Kpi(vm, "Applications", nameof(vm.AppsText), nameof(vm.AppsDetails))));
        figures.AddChild(Ui.Fill(DashUi.Kpi(vm, "Time span", nameof(vm.SpanText), nameof(vm.SpanDetails))));
        figures.AddChild(Ui.Fill(DashUi.Kpi(vm, "Rate", nameof(vm.RateText), nameof(vm.RateDetails))));

        // Activity.
        var chart = UsageChart.Of(vm.Activity, DashUi.Blue, LogDashboardViewModel.Buckets, vm.ErrorActivity, DashUi.ErrorRed, vm, nameof(vm.ActivityMax));
        chart.SeriesName = "Logs";
        chart.SecondaryName = "Errors";
        chart.Format = v => $"{v:N0}";
        chart.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var activity = DashUi.Card(Ui.Column(4,
                Ui.FillHeight(Ui.Row(6, Ui.Fill(chart), Top(Ui.Label(vm, nameof(vm.ActivityMaxLabel), EveStyle.Muted, 10)))),
                Ui.Row(6, Ui.Fill(Ui.Label(vm, nameof(vm.ActivityStart), EveStyle.Muted, 11)), Ui.Label(vm, nameof(vm.ActivityEnd), EveStyle.Muted, 11))),
            vm, nameof(vm.ActivityTitle), EveStyle.Text_("■ all logs", DashUi.Blue, 11), EveStyle.Text_("■ errors", DashUi.ErrorRed, 11));
        activity.SizeFlagsStretchRatio = 2;

        // By application.
        var apps = Ui.Column(2);
        Bind.Items(apps, vm.AppStats, app => AppRow(vm, app));
        var byApp = DashUi.Card(Ui.FillHeight(Ui.Scroll(apps)), "By application (click to filter)");

        // Most frequent errors.
        var errors = Ui.Column(2);
        var noError = Ui.Placeholder("No error 🎉");
        Bind.Items(errors, vm.TopErrors, error => ErrorRow(vm, error), noError);
        var topErrors = DashUi.Card(Ui.FillHeight(Ui.Column(0, Ui.FillHeight(Ui.Scroll(errors)), noError)), "Most frequent errors (click to filter)");

        var middle = Ui.Row(8, Ui.Fill(activity, true), Ui.Fill(byApp, true), Ui.Fill(topErrors, true));
        middle.CustomMinimumSize = new Vector2(0, 250);
        foreach (var card in new[] { activity, byApp, topErrors })
            card.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        // Filters and table.
        var search = Ui.Line(vm, nameof(vm.Search), "Search in the messages and application names…");
        var types = Bind.Options(new OptionButton { CustomMinimumSize = new Vector2(120, 0), FitToLongestItem = false }, vm.Types, vm, nameof(vm.SelectedType));
        var appFilter = Bind.Options(new OptionButton { CustomMinimumSize = new Vector2(260, 0), ClipText = true, FitToLongestItem = false }, vm.Apps, vm, nameof(vm.SelectedApp));
        var tableTitle = EveStyle.Caption("");
        Bind.Text(tableTitle, vm, nameof(vm.TableTitle), v => (v?.ToString() ?? string.Empty).ToUpperInvariant());
        tableTitle.CustomMinimumSize = new Vector2(150, 0);
        var table = new VirtualTable { EmptyText = "No log", Tooltip = row => ((LogRowViewModel)row).Comment };
        table.Columns(
            new TableColumn("Date", 185, row => ((LogRowViewModel)row).Date) { Mono = true },
            new TableColumn("Type", 70, row => ((LogRowViewModel)row).Type) { Badge = row => ((LogRowViewModel)row).IsError ? DashUi.ErrorRed with { A = 0.4f } : DashUi.Blue with { A = 0.27f } },
            new TableColumn("Application", 300, row => ((LogRowViewModel)row).App),
            new TableColumn("Message", 0, row => ((LogRowViewModel)row).Comment) { Color = row => ((LogRowViewModel)row).IsError ? DashUi.ErrorRed.Lightened(0.2f) : null });
        table.CopyText = row => row is LogRowViewModel log ? $"{log.Date}\t{log.Type}\t{log.App}\t{log.Comment}" : string.Empty;
        table.Activated += row =>
        {
            if (row is LogRowViewModel log)
                _ = Dialogs.Message($"{log.Type} · {log.App}", $"{log.Date}\n\n{log.Comment}");
        };
        Bind.When(table, vm, () => table.SetRows(vm.Rows), nameof(vm.Rows));
        var logs = DashUi.Card(Ui.Column(8,
            Ui.Row(8, tableTitle, Ui.Fill(search), types, appFilter, Ui.Command("CLEAR FILTERS", vm.ClearFiltersCommand)),
            table));
        logs.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        var page = Ui.Column(8, header, Ui.FillHeight(Ui.Margin(Ui.Column(8, figures, middle, Ui.FillHeight(logs)), 8, 0, 8, 8)));

        // The data is read once, when the window opens.
        page.Ready += () => Bind.Run(vm.RefreshCommand);
        return page;
    }

    private static Label Top(Label label)
    {
        label.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        return label;
    }

    /// <summary>An application: its name, its count (red with errors), a bar of its share; a click filters on it.</summary>
    private static Control AppRow(LogDashboardViewModel vm, AppStatViewModel app)
    {
        var name = EveStyle.Text_(app.App, EveStyle.Text, 11);
        name.ClipText = true;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var count = EveStyle.Text_(app.CountText, app.HasErrors ? DashUi.ErrorRed.Lightened(0.15f) : EveStyle.Text, 11);
        return Row(Ui.Column(2, Ui.Row(6, name, count), DashUi.Share(app.Share)), app.App,
            () => Bind.Run(vm.FilterAppCommand, app),
            button => Bind.When(button, vm, () => button.SetPressedNoSignal(vm.SelectedApp == app.App), nameof(vm.SelectedApp)));
    }

    /// <summary>An error message: the message (red, two lines), how many, where and when; a click shows its logs.</summary>
    private static Control ErrorRow(LogDashboardViewModel vm, ErrorStatViewModel error)
    {
        var message = EveStyle.Text_(error.Message, DashUi.ErrorRed.Lightened(0.15f), 11, wrap: true);
        message.MaxLinesVisible = 2;
        message.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var details = EveStyle.Text_(error.Details, EveStyle.Muted, 11);
        details.ClipText = true;
        details.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        return Row(Ui.Column(1, message, details), error.Message, () => Bind.Run(vm.FilterErrorCommand, error));
    }

    /// <summary>A flat button row holding <paramref name="content"/>.</summary>
    private static Button Row(Control content, string tooltip, Action pressed, Action<Button>? bind = null)
    {
        var button = new Button { Flat = true, ToggleMode = bind is not null, TooltipText = tooltip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        button.AddThemeStyleboxOverride("pressed", EveStyle.Box(EveStyle.Cyan with { A = 0.16f }, EveStyle.Cyan with { A = 0.5f }, 4));
        button.AddThemeStyleboxOverride("hover", EveStyle.Box(Eve.Eve.Raised(0.05f), EveStyle.Edge with { A = 0 }, 4));
        button.AddThemeStyleboxOverride("normal", EveStyle.Box(new Color(0, 0, 0, 0), EveStyle.Edge with { A = 0 }, 4));
        var margin = Ui.Margin(content, 4, 3, 4, 3);
        margin.MouseFilter = Control.MouseFilterEnum.Ignore;
        Ignore(margin);
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        button.AddChild(margin);
        // The button takes the height of its content.
        margin.MinimumSizeChanged += () => button.CustomMinimumSize = new Vector2(0, margin.GetCombinedMinimumSize().Y);
        button.Ready += () => button.CustomMinimumSize = new Vector2(0, margin.GetCombinedMinimumSize().Y);
        button.Pressed += pressed;
        bind?.Invoke(button);
        return button;
    }

    private static void Ignore(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is Control control)
                control.MouseFilter = Control.MouseFilterEnum.Ignore;
            Ignore(child);
        }
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
