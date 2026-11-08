using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// The popup of a cluster, a window of the desktop: the elements inside the drawing (their window, their title, their
/// id), the name of the cluster, its title, its description, its tags (#TOPIC), events (@EVENT) and signals (!SIGNAL),
/// separated by spaces or commas, '-' inside one (<see cref="MetaTags"/>: upper case, no space). It opens for a new cluster (the pen has just
/// drawn around some elements: CREATE CLUSTER stores it, <c>PUT /cluster</c>) or for a stored one (a double-click on its
/// name: SAVE stores the changes, <c>POST /cluster/update</c>, DELETE marks it deleted, <c>DELETE /cluster/{id}</c>, it
/// is never erased). The cluster and each of its elements also have a period (from, to: dates "yyyy-MM-dd", optional)
/// and a probability (0 to 100, 100: sure). Escape or Cancel closes it.
/// </summary>
internal static class ClusterDialog
{
    private static readonly Color Accent = ClusterPen.Color;

    /// <summary>Opens the popup of a new cluster of <paramref name="members"/> of the graph of relations, drawn along <paramref name="path"/>.</summary>
    public static DeskWindow Open(Shell shell, IReadOnlyList<RelationGraphNode> members, IReadOnlyList<Point> path, Action saved, Action? closed = null)
    {
        var entry = New(ClusterCategories.GraphNode, path);
        entry.Members = members.Select(m => new ClusterMemberEntry { WindowName = m.Type, ObjectId = m.ObjectId ?? m.Key, Title = m.Title, Context = m.Context }).ToList();
        return Open(shell, entry, isNew: true, saved, closed);
    }

    /// <summary>
    /// Opens the popup of a new cluster of <paramref name="members"/> drawn along <paramref name="path"/> in an editor
    /// other than the graph (<paramref name="category"/>: bpmnai, infra, mindmap).
    /// </summary>
    public static DeskWindow Open(Shell shell, IReadOnlyList<ClusterMemberEntry> members, IReadOnlyList<Point> path, string category, Action saved, Action? closed = null)
    {
        var entry = New(category, path);
        entry.Members = [.. members];
        return Open(shell, entry, isNew: true, saved, closed);
    }

    /// <summary>Opens the popup of the stored cluster <paramref name="cluster"/>.</summary>
    public static DeskWindow Open(Shell shell, ClusterEntry cluster, Action saved, Action? closed = null) =>
        Open(shell, cluster, isNew: false, saved, closed);

    private static ClusterEntry New(string category, IReadOnlyList<Point> path) => new()
    {
        AppName = "KAONOMI.Chat",
        MachineName = Environment.MachineName,
        UserName = Environment.UserName,
        Category = category,
        Path = path.Select(p => new ClusterPointEntry { X = Math.Round(p.X, 1), Y = Math.Round(p.Y, 1) }).ToList()
    };

    private static DeskWindow Open(Shell shell, ClusterEntry entry, bool isNew, Action saved, Action? closed)
    {
        DeskWindow? window = null;
        var content = Build(entry, isNew, saved, () =>
        {
            if (window is not null)
                shell.Deck.Close(window);
        });
        var key = isNew ? $"cluster-new#{Guid.NewGuid():N}" : $"cluster#{entry.Id}";
        var title = isNew ? $"Cluster · {entry.Members.Count} element(s)" : $"Cluster · {entry.Name}";
        window = shell.OpenControl(key, title, "◎", content, new Size(860, 700));
        if (closed is not null)
            window.Closed += closed;
        return window;
    }

    private static Control Build(ClusterEntry entry, bool isNew, Action saved, Action close)
    {
        // A date field ("yyyy-MM-dd", empty: none) and a probability slider (0 to 100) with its value beside it.
        static TextBox DateField(DateTimeOffset? value, string tooltip)
        {
            var box = new TextBox { PlaceholderText = "yyyy-MM-dd", Text = value?.ToLocalTime().ToString("yyyy-MM-dd") ?? string.Empty, Width = 104 };
            ToolTip.SetTip(box, tooltip);
            return box;
        }
        static (Slider Slider, TextBlock Value) Probability(int value, double width)
        {
            var slider = new Slider { Minimum = 0, Maximum = 100, SmallChange = 1, LargeChange = 10, IsSnapToTickEnabled = true, TickFrequency = 1, Value = Math.Clamp(value, 0, 100), MinWidth = width, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(slider, "Probability, 0 to 100 (100: sure)");
            var label = Ui.Label($"{(int)slider.Value} %", Ui.TextBright, 11);
            label.MinWidth = 38;
            label.TextAlignment = TextAlignment.Right;
            slider.ValueChanged += (_, e) => label.Text = $"{(int)e.NewValue} %";
            return (slider, label);
        }
        static bool TryDate(TextBox field, out DateTimeOffset? date)
        {
            date = null;
            var text = (field.Text ?? string.Empty).Trim();
            if (text.Length == 0)
                return true;
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
                return false;
            date = parsed;
            return true;
        }

        // The elements of the cluster, each with its period and its probability.
        var list = new StackPanel { Spacing = 4 };
        var memberFields = new List<(ClusterMemberEntry Member, TextBox From, TextBox To, Slider Probability)>();
        foreach (var member in entry.Members)
        {
            var dot = new Border { Width = 8, Height = 8, Background = Paint.Brush(RelationGraphCanvas.ColorOf(member.WindowName)), VerticalAlignment = VerticalAlignment.Center };
            var type = Ui.Caption(member.WindowName);
            type.Width = 96;
            var title = Ui.Label(string.IsNullOrWhiteSpace(member.Title) ? member.ObjectId ?? string.Empty : member.Title, Ui.TextBright, 13);
            ToolTip.SetTip(title, member.ObjectId);
            var from = DateField(member.DateFrom, "From (yyyy-MM-dd, empty: none)");
            var to = DateField(member.DateTo, "To (yyyy-MM-dd, empty: none)");
            var (probability, value) = Probability(member.Probability, 90);
            memberFields.Add((member, from, to, probability));
            list.Children.Add(KnowledgeUi.FillRow(8, [dot, type], title, from, to, probability, value));
        }
        var membersPanel = Ui.Box(Ui.Scroll(list), 8, Ui.Field);
        membersPanel.MinHeight = 120;

        // The period and the probability of the cluster itself.
        var dateFrom = DateField(entry.DateFrom, "The cluster holds from this date (yyyy-MM-dd, empty: none)");
        var dateTo = DateField(entry.DateTo, "The cluster holds until this date (yyyy-MM-dd, empty: none)");
        var (clusterProbability, clusterValue) = Probability(entry.Probability, 160);

        var name = new TextBox { PlaceholderText = "Name of the cluster", Text = entry.Name };
        var description = new TextBox { PlaceholderText = "What these elements have in common", Text = entry.Description, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90 };
        var titleField = new TextBox { PlaceholderText = "Title of the cluster (besides its name)", Text = entry.Title };
        var tags = new TextBox { PlaceholderText = MetaTags.TagsHint + " · separated by spaces or commas, '-' inside one", Text = MetaTags.Format(entry.Tags) };
        var events = new TextBox { PlaceholderText = MetaTags.EventsHint + " · separated by spaces or commas, '-' inside one", Text = MetaTags.Format(entry.Events) };
        var signals = new TextBox { PlaceholderText = MetaTags.SignalsHint + " · separated by spaces or commas, '-' inside one", Text = MetaTags.Format(entry.Signals) };
        var status = Ui.Label(string.Empty, Ui.Muted, 12, wrap: true);
        void SetStatus(string text, bool error)
        {
            status.Text = text;
            status.Foreground = error ? Ui.Red : Ui.Muted;
        }
        var save = Ui.Button(isNew ? "💾 CREATE CLUSTER" : "💾 SAVE", primary: true);
        save.Background = Paint.Brush(Paint.Alpha(Accent, 0.3));
        save.BorderBrush = Paint.Brush(Accent);
        var delete = Ui.Button("DELETE", tooltip: "Marks the cluster deleted (it stays in its index, out of the searches and of the graph)");
        delete.Foreground = Ui.Red;
        delete.IsVisible = !isNew;
        var cancel = Ui.Button("CANCEL", close);

        // Reads the fields into the entry; false (with the status set and the field focused) when a date is not a date.
        bool Fill()
        {
            entry.Name = (name.Text ?? string.Empty).Trim();
            entry.Description = (description.Text ?? string.Empty).Trim();
            entry.Title = (titleField.Text ?? string.Empty).Trim();
            entry.Tags = MetaTags.Parse(tags.Text, MetaTags.Tag);
            entry.Events = MetaTags.Parse(events.Text, MetaTags.Event);
            entry.Signals = MetaTags.Parse(signals.Text, MetaTags.Signal);
            if (!TryDate(dateFrom, out var from) || !TryDate(dateTo, out var to))
            {
                SetStatus("The dates of the cluster must be yyyy-MM-dd (or empty)", true);
                (TryDate(dateFrom, out _) ? dateTo : dateFrom).Focus();
                return false;
            }
            entry.DateFrom = from;
            entry.DateTo = to;
            entry.Probability = (int)clusterProbability.Value;
            foreach (var (member, fromField, toField, probability) in memberFields)
            {
                if (!TryDate(fromField, out var memberFrom) || !TryDate(toField, out var memberTo))
                {
                    SetStatus($"The dates of \"{member.Title}\" must be yyyy-MM-dd (or empty)", true);
                    (TryDate(fromField, out _) ? toField : fromField).Focus();
                    return false;
                }
                member.DateFrom = memberFrom;
                member.DateTo = memberTo;
                member.Probability = (int)probability.Value;
            }
            return true;
        }

        async Task SaveAsync()
        {
            if (string.IsNullOrWhiteSpace(name.Text))
            {
                SetStatus("Give the cluster a name", true);
                name.Focus();
                return;
            }
            if (KaonomiAssistantService.Shared is not { } server)
            {
                SetStatus("No Kaonomi server: the cluster cannot be saved", true);
                return;
            }
            if (!Fill())
                return;
            save.IsEnabled = delete.IsEnabled = false;
            SetStatus("Saving…", false);
            try
            {
                var result = isNew ? await server.CreateClusterAsync(entry) : await server.UpdateClusterAsync(entry);
                if (result.ReturnedId is null or "-1")
                {
                    SetStatus(result.Message ?? "The cluster was not saved", true);
                    save.IsEnabled = delete.IsEnabled = true;
                    return;
                }
                ActivityLog.Info("Cluster", $"Cluster \"{entry.Name}\" {(isNew ? "created" : "saved")} ({entry.Members.Count} element(s), tags {string.Join(' ', entry.Tags)}): {result.ReturnedId}");
                saved();
                close();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                SetStatus("The cluster was not saved: " + ex.Message, true);
                save.IsEnabled = delete.IsEnabled = true;
                ActivityLog.Error("Cluster", "The cluster was not saved", ex);
            }
        }

        async Task DeleteAsync()
        {
            if (entry.Id is null || KaonomiAssistantService.Shared is not { } server)
                return;
            if (!await Dialogs.Confirm("Delete the cluster", $"Mark the cluster \"{entry.Name}\" deleted? It stays in its index but leaves the searches and the graph.", "Delete"))
                return;
            save.IsEnabled = delete.IsEnabled = false;
            SetStatus("Deleting…", false);
            try
            {
                var result = await server.DeleteClusterAsync(entry.Id);
                if (result.ReturnedId is null or "-1")
                {
                    SetStatus(result.Message ?? "The cluster was not deleted", true);
                    save.IsEnabled = delete.IsEnabled = true;
                    return;
                }
                ActivityLog.Info("Cluster", $"Cluster \"{entry.Name}\" marked deleted: {entry.Id}");
                saved();
                close();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                SetStatus("The cluster was not deleted: " + ex.Message, true);
                save.IsEnabled = delete.IsEnabled = true;
            }
        }
        save.Click += (_, _) => _ = SaveAsync();
        delete.Click += (_, _) => _ = DeleteAsync();
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _ = SaveAsync();
                e.Handled = true;
            }
        };

        var heading = isNew
            ? $"{entry.Members.Count} element(s) inside the drawing"
            : $"{entry.Members.Count} element(s) · {entry.Id}{(entry.Date is { } date ? " · " + date.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : string.Empty)}";
        var buttons = KnowledgeUi.FillRow(8, [], status, delete, cancel, save);
        var period = Ui.Row(10, KnowledgeUi.Field("From", dateFrom), KnowledgeUi.Field("To", dateTo),
            KnowledgeUi.Field("Probability", Ui.Row(8, clusterProbability, clusterValue)));
        var form = Ui.Dock(
            (Ui.Label(heading.ToUpperInvariant(), Ui.TextBright, 12), Dock.Top),
            (buttons, Dock.Bottom),
            (period, Dock.Bottom),
            (KnowledgeUi.Field("Signals", signals), Dock.Bottom),
            (KnowledgeUi.Field("Events", events), Dock.Bottom),
            (KnowledgeUi.Field("Tags", tags), Dock.Bottom),
            (KnowledgeUi.Field("Description", description), Dock.Bottom),
            (KnowledgeUi.Field("Title", titleField), Dock.Bottom),
            (KnowledgeUi.Field("Name", name), Dock.Bottom),
            (membersPanel, Dock.Top));
        foreach (var child in form.Children)
            child.Margin = new Thickness(0, 5);
        form.Margin = new Thickness(14);
        KnowledgeUi.Shortcuts(form, e =>
        {
            if (e.Key != Key.Escape)
                return false;
            close();
            return true;
        });
        KnowledgeUi.FocusLater(name);
        return form;
    }
}
