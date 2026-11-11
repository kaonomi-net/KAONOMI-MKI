using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.Views;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// The popup of a relation, a window of the desktop: on the left the element it starts from, on the right the elements
/// it goes to, in the order of the string (their window, the title and the id of their object; ↻ Refresh reads the
/// titles again, for an entity of the ontology its name; a text to complete what is known about them; their data); in
/// the middle the nature of the link, its title, its probability (-100 to 100), its optional dates (From, To) and its
/// description. It opens for a new link (the red string drawn in the graph) or for a stored relation (a double-click on
/// a link of <c>/relation</c>): its current version is shown, and the list of its versions loads an older one in the
/// form. Save stores a new version in the <c>relation</c> index of the Kaonomi server, category "interdata"
/// (<c>PUT /relation</c>); Delete removes a stored one (<c>DELETE /relation/{id}</c>). Escape or Cancel closes it.
/// </summary>
internal static class RelationDialog
{
    private static readonly Color Accent = Paint.Hex("#E11D48");

    /// <summary>Opens the popup of a stored relation, on its current version.</summary>
    public static DeskWindow Open(Shell shell, RelationEntry relation, Action saved, Action? closed = null) =>
        Open(shell, EndOf(relation.Current.Source), relation.Current.Destinations.Select(EndOf).ToList(), relation, saved, closed);

    private static LinkEnd EndOf(RelationEndEntry end) => new(end.WindowName ?? "Window", end.ObjectId, end.Context ?? "{}");

    /// <summary>
    /// Opens the popup of a new link from <paramref name="source"/> to <paramref name="destinations"/> (or of
    /// <paramref name="existing"/>); <paramref name="saved"/> runs once it is saved, <paramref name="closed"/> when its
    /// window closes (saved or not).
    /// </summary>
    public static DeskWindow Open(Shell shell, LinkEnd source, IReadOnlyList<LinkEnd> destinations, RelationEntry? existing, Action saved, Action? closed = null)
    {
        var title = existing is null
            ? $"Link · {source.WindowName} → {string.Join(", ", destinations.Select(d => d.WindowName))}"
            : $"Relation · {existing.Current.Title ?? existing.Current.Nature ?? "(untitled)"} · version {existing.Current.Version}";
        DeskWindow? window = null;
        var content = Build(source, destinations, existing, saved, () =>
        {
            if (window is not null)
                shell.Deck.Close(window);
        });
        var key = existing?.Id is { } id ? $"relation#{id}" : $"relation-link#{Guid.NewGuid():N}";
        window = shell.OpenControl(key, title, "🔗", content, new Size(1200, 720));
        if (closed is not null)
            window.Closed += closed;
        return window;
    }

    private static Control Build(LinkEnd source, IReadOnlyList<LinkEnd> destinations, RelationEntry? existing, Action saved, Action close)
    {
        var sourceInformation = InformationBox();
        var destinationInformations = destinations.Select(_ => InformationBox()).ToList();
        // The title of each end (source first): the one saved with it, else the one in its data; Refresh reads it again.
        var ends = new[] { source }.Concat(destinations).ToList();
        var titles = ends.Select(e => RelationTitles.FromContext(e.Context)).ToArray();
        var titleBlocks = ends.Select(_ => KnowledgeUi.Selectable(string.Empty, Ui.TextBright, 13)).ToList();
        void ShowTitles()
        {
            for (var i = 0; i < titleBlocks.Count; i++)
            {
                titleBlocks[i].Text = titles[i] ?? "(no title)";
                titleBlocks[i].Opacity = titles[i] is null ? 0.5 : 1;
            }
        }

        var nature = new TextBox { PlaceholderText = "e.g. same person, cause of, illustrates, contradicts…" };
        var titleEdit = new TextBox { PlaceholderText = "Title of the link" };
        var (probabilityPanel, probability) = ProbabilityField();
        var dateFrom = new DateBox { Tip = "Optional: the date the link holds from" };
        var dateTo = new DateBox { Tip = "Optional: the date the link holds until" };
        var dates = KnowledgeUi.Pair(10, KnowledgeUi.Field("From", dateFrom), KnowledgeUi.Field("To", dateTo));
        var description = new TextBox { PlaceholderText = "Description of the link", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, VerticalContentAlignment = VerticalAlignment.Top };
        var status = Ui.Label(string.Empty, Ui.Muted, 12, wrap: true);
        void SetStatus(string text, bool error)
        {
            status.Text = text;
            status.Foreground = error ? Ui.Red : Ui.Muted;
        }
        var save = Ui.Button("💾 SAVE", primary: true);
        save.Background = Paint.Brush(Paint.Alpha(Accent, 0.3));
        save.BorderBrush = Paint.Brush(Accent);
        var cancel = Ui.Button("CANCEL", close);
        var refresh = Ui.Button("↻ REFRESH", tooltip: "Read the titles of the source and of the destinations again (for an entity of the ontology its name); Save keeps them");

        // The titles read again: from the server for the objects it stores, else from the data of the windows.
        async Task RefreshAsync()
        {
            refresh.IsEnabled = false;
            SetStatus("Reading the titles…", false);
            var server = KaonomiAssistantService.Shared;
            var changed = 0;
            for (var i = 0; i < ends.Count; i++)
            {
                var read = await RelationTitles.ResolveAsync(ends[i].WindowName, ends[i].ObjectId, ends[i].Context, server);
                if (read is not null && read != titles[i])
                {
                    titles[i] = read;
                    changed++;
                }
            }
            ShowTitles();
            SetStatus(changed == 0 ? "Titles up to date" : $"{changed} title(s) updated · Save to keep them in a new version", false);
            refresh.IsEnabled = true;
            ActivityLog.Info("Relation", $"Titles refreshed: {string.Join(" → ", titles.Select(t => t ?? "(none)"))}");
        }
        refresh.Click += (_, _) => _ = RefreshAsync();

        async Task SaveAsync()
        {
            if (KaonomiAssistantService.Shared is not { } server)
            {
                SetStatus("No Kaonomi server: the link cannot be saved.", true);
                return;
            }
            if (dateFrom.Value is { } start && dateTo.Value is { } end && end < start)
            {
                SetStatus("The date To is before the date From.", true);
                return;
            }
            save.IsEnabled = false;
            SetStatus("Saving…", false);
            // Only the new version is sent: the server adds it to the versions of a stored relation.
            var version = new RelationVersionEntry
            {
                UserName = Environment.UserName,
                MachineName = Environment.MachineName,
                Nature = Text(nature.Text),
                Title = Text(titleEdit.Text),
                Description = Text(description.Text),
                Probability = (int)Math.Round(probability.Value),
                DateFrom = dateFrom.Value,
                DateTo = dateTo.Value,
                Source = End(source, sourceInformation, titles[0]),
                Destinations = destinations.Select((d, i) => End(d, destinationInformations[i], titles[i + 1])).ToList()
            };
            var relation = new RelationEntry
            {
                Id = existing?.Id,
                AppName = "KAONOMI.Chat",
                MachineName = Environment.MachineName,
                UserName = Environment.UserName,
                Category = existing?.Category ?? "interdata",
                Versions = [version]
            };
            try
            {
                var result = await server.SaveRelationAsync(relation);
                if (result.ReturnedId is null or "-1")
                {
                    SetStatus("Not saved: " + result.Message, true);
                    save.IsEnabled = true;
                    return;
                }
                ActivityLog.Info("Relation", $"Link saved ({result.Message}): {source.WindowName} → {string.Join(", ", destinations.Select(d => d.WindowName))}, {version.Nature} · {version.Title}");
                saved();
                close();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                SetStatus("Not saved: " + ex.Message, true);
                save.IsEnabled = true;
            }
        }
        save.Click += (_, _) => _ = SaveAsync();

        // A stored relation can be deleted (soft delete on the server: it leaves the graph, its versions are kept).
        var delete = Ui.Button("🗑 DELETE", tooltip: "Delete this relation, all its versions with it (DELETE /relation/{id}: marked as deleted on the server)");
        delete.Foreground = Ui.Red;
        delete.IsVisible = existing?.Id is not null;
        async Task DeleteAsync()
        {
            if (existing?.Id is not { } id)
                return;
            if (KaonomiAssistantService.Shared is not { } server)
            {
                SetStatus("No Kaonomi server: the relation cannot be deleted.", true);
                return;
            }
            if (!await Dialogs.Confirm("Delete", $"Delete the relation \"{existing.Current.Title ?? existing.Current.Nature ?? "(untitled)"}\" and all its versions?", "Delete"))
                return;
            delete.IsEnabled = save.IsEnabled = false;
            SetStatus("Deleting…", false);
            try
            {
                var result = await server.DeleteRelationAsync(id);
                if (!result.Success)
                {
                    SetStatus("Not deleted: " + result.Message, true);
                    delete.IsEnabled = save.IsEnabled = true;
                    return;
                }
                ActivityLog.Info("Relation", $"Relation deleted ({id}): {existing.Current.Nature} · {existing.Current.Title}");
                saved();
                close();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                SetStatus("Not deleted: " + ex.Message, true);
                delete.IsEnabled = save.IsEnabled = true;
            }
        }
        delete.Click += (_, _) => _ = DeleteAsync();

        // A stored relation: its current version in the form, and the list of its versions to load an older one.
        void Show(RelationVersionEntry version)
        {
            nature.Text = version.Nature ?? string.Empty;
            titleEdit.Text = version.Title ?? string.Empty;
            description.Text = version.Description ?? string.Empty;
            probability.Value = version.Probability;
            dateFrom.Value = version.DateFrom?.Date;
            dateTo.Value = version.DateTo?.Date;
            sourceInformation.Text = version.Source.Information ?? string.Empty;
            for (var i = 0; i < destinationInformations.Count; i++)
                destinationInformations[i].Text = i < version.Destinations.Count ? version.Destinations[i].Information ?? string.Empty : string.Empty;
            var stored = new[] { version.Source }.Concat(version.Destinations).ToList();
            for (var i = 0; i < titles.Length; i++)
                titles[i] = (i < stored.Count && !string.IsNullOrWhiteSpace(stored[i].Title) ? stored[i].Title!.Trim() : null)
                            ?? RelationTitles.FromContext(ends[i].Context);
            ShowTitles();
        }
        ShowTitles();
        Control versionsPanel = new Panel();
        if (existing is { Versions.Count: > 0 })
        {
            var versions = existing.Versions.AsEnumerable().Reverse().ToList();
            var picker = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                ItemsSource = versions.Select(v => $"Version {v.Version} · {v.Date?.ToLocalTime():yyyy-MM-dd HH:mm}{(ReferenceEquals(v, existing.Current) ? " (current)" : string.Empty)}").ToList(),
                SelectedIndex = 0
            };
            ToolTip.SetTip(picker, "The versions of the relation, the newest first: choose one to see it in the form; Save makes a new version from what the form holds");
            picker.SelectionChanged += (_, _) =>
            {
                if (picker.SelectedIndex >= 0 && picker.SelectedIndex < versions.Count)
                    Show(versions[picker.SelectedIndex]);
            };
            versionsPanel = KnowledgeUi.Field($"Versions ({existing.Versions.Count}) · Save adds version {existing.Versions.Max(v => v.Version) + 1}", picker);
            Show(existing.Current);
            save.Content = "💾 SAVE A NEW VERSION";
        }

        // The link in the middle.
        var middle = Card(existing is null ? "THE LINK" : "THE RELATION", Ui.Dock(
            (Ui.Column(6,
                versionsPanel,
                KnowledgeUi.Field("Nature of the link", nature),
                KnowledgeUi.Field("Title", titleEdit),
                probabilityPanel,
                dates,
                Ui.Caption("Description")), Dock.Top),
            (description, Dock.Top)), accent: true);

        // One destination fills its column; several are stacked in it, in the order of the string, and scroll.
        Control right;
        if (destinations.Count == 1)
            right = EndCard("DESTINATION", destinations[0], titleBlocks[1], destinationInformations[0], fill: true);
        else
        {
            var stack = new StackPanel { Spacing = 10 };
            for (var i = 0; i < destinations.Count; i++)
                stack.Children.Add(EndCard($"DESTINATION {i + 1} / {destinations.Count}", destinations[i], titleBlocks[i + 1], destinationInformations[i], fill: false));
            right = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }
        var left = EndCard("SOURCE", source, titleBlocks[0], sourceInformation, fill: true);
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("1*,14,1.2*,14,1*") };
        foreach (var (control, column) in new[] { (left, 0), (Arrow(), 1), (middle, 2), (Arrow(), 3), (right, 4) })
        {
            Grid.SetColumn(control, column);
            columns.Children.Add(control);
        }
        left.MinWidth = middle.MinWidth = right.MinWidth = 200;

        var buttons = KnowledgeUi.FillRow(8, [], status, delete, refresh, save, cancel);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        var page = Ui.Dock((buttons, Dock.Bottom), (columns, Dock.Top));
        page.Margin = new Thickness(14);
        KnowledgeUi.Shortcuts(page, key =>
        {
            if (key.Key != Key.Escape)
                return false;
            close();
            return true;
        });
        KnowledgeUi.FocusLater(nature);
        return page;
    }

    /// <summary>
    /// The probability of the link: a slider from -100 (absolutely impossible) to 100 (absolutely certain), 0 (to
    /// evaluate) at the middle, with its value and its meaning above it in the colour of its side.
    /// </summary>
    private static (Control Panel, Slider Slider) ProbabilityField()
    {
        var slider = new Slider { Minimum = -100, Maximum = 100, SmallChange = 1, LargeChange = 10, TickFrequency = 1, IsSnapToTickEnabled = true, Value = 0 };
        ToolTip.SetTip(slider, "How probable is this link? -100: absolutely impossible · 0: to evaluate · 100: absolutely certain");
        var value = Ui.Label(string.Empty, Ui.Muted, 12);
        value.TextAlignment = TextAlignment.Right;
        void Show()
        {
            var v = (int)Math.Round(slider.Value);
            var meaning = v switch
            {
                -100 => "absolutely impossible",
                <= -60 => "very unlikely",
                <= -20 => "unlikely",
                < 0 => "rather unlikely",
                0 => "to evaluate",
                < 20 => "rather likely",
                < 60 => "likely",
                < 100 => "very likely",
                _ => "absolutely certain"
            };
            value.Text = $"{(v > 0 ? "+" : "")}{v} · {meaning}";
            value.Foreground = v < 0 ? Paint.Brush(Paint.Hex("#E5484D")) : v > 0 ? Paint.Brush(Paint.Hex("#30C48D")) : Ui.Muted;
        }
        slider.ValueChanged += (_, _) => Show();
        Show();
        return (Ui.Column(2, KnowledgeUi.FillRow(6, [Ui.Caption("Probability")], value), slider,
            KnowledgeUi.Marks("-100 · Absolutely impossible", "0 · To evaluate", "Absolutely certain · 100")), slider);
    }

    private static string? Text(string? text) => text?.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    private static RelationEndEntry End(LinkEnd end, TextBox information, string? title) => new()
    {
        WindowName = end.WindowName,
        ObjectId = end.ObjectId,
        Title = title,
        Information = Text(information.Text),
        Context = end.Context
    };

    private static TextBox InformationBox() => new()
    {
        PlaceholderText = "What this element shows, for this link…",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 120,
        VerticalContentAlignment = VerticalAlignment.Top
    };

    /// <summary>
    /// One end of the link: its window, the title and the id of its object, the text to complete (filling the card, or
    /// of a fixed height when the cards are stacked), and its data (folded).
    /// </summary>
    private static Control EndCard(string heading, LinkEnd end, SelectableTextBlock title, TextBox information, bool fill)
    {
        if (!fill)
            information.MinHeight = 90;
        var data = new TextBox
        {
            Text = end.Context,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 150,
            MaxHeight = 300,
            FontFamily = Ui.Mono,
            FontSize = 10,
            IsVisible = false
        };
        var expander = new ToggleButton { Content = "▸ DATA OF THE WINDOW", FontSize = 10, HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        expander.IsCheckedChanged += (_, _) =>
        {
            var on = expander.IsChecked == true;
            data.IsVisible = on;
            expander.Content = (on ? "▾" : "▸") + " DATA OF THE WINDOW";
        };
        var windowName = KnowledgeUi.Selectable(end.WindowName, Ui.TextBright, 15);
        var objectId = KnowledgeUi.Selectable(end.ObjectId ?? "(no object in this window)", end.ObjectId is null ? Ui.Muted : Ui.Text, 12);
        var top = Ui.Column(6,
            KnowledgeUi.Field("Window", windowName),
            KnowledgeUi.Field("Title", title),
            KnowledgeUi.Field("Object id", objectId),
            Ui.Caption("Information"));
        var bottom = Ui.Column(6, expander, data);
        top.Margin = new Thickness(0, 0, 0, 6);
        bottom.Margin = new Thickness(0, 6, 0, 0);
        var content = fill
            ? (Control)Ui.Dock((top, Dock.Top), (bottom, Dock.Bottom), (information, Dock.Top))
            : Ui.Column(0, top, information, bottom);
        return Card(heading, content);
    }

    private static Border Card(string heading, Control content, bool accent = false) =>
        KnowledgeUi.Card(Ui.Dock((new TextBlock { Text = heading, Foreground = Paint.Brush(Accent), FontSize = 11, Margin = new Thickness(0, 0, 0, 8) }, Dock.Top), (content, Dock.Top)),
            accent ? Paint.Brush(Paint.Alpha(Accent, 0.85)) : null, 12, accent ? 2 : 1);

    /// <summary>The red string between the cards.</summary>
    private static Control Arrow() => new Border { Height = 3, Background = Paint.Brush(Accent), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
}
