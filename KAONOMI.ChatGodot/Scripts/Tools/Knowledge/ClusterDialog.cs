using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Knowledge;

/// <summary>
/// The popup of a cluster, a window of the desktop: the elements inside the drawing (their window, their title, their
/// id), the name of the cluster, its title, its description, its tags (#TOPIC), events (@EVENT) and signals (!SIGNAL),
/// separated by spaces or commas, '-' inside one (<see cref="MetaTags"/>: upper case, no space). It opens for a new cluster (the pen of the
/// graph of relations has just drawn around some elements: CREATE CLUSTER stores it, <c>PUT /cluster</c>) or for a
/// stored one (a double-click on its name in the graph: SAVE stores the changes, <c>POST /cluster/update</c>, DELETE
/// marks it deleted, <c>DELETE /cluster/{id}</c>, it is never erased). The cluster and each of its elements also have
/// a period (from, to: dates "yyyy-MM-dd", optional) and a probability (0 to 100, 100: sure). Escape or Cancel closes it.
/// </summary>
internal static class ClusterDialog
{
    private static readonly Color Accent = new("#D4A017");

    /// <summary>Opens the popup of a new cluster of <paramref name="members"/>, drawn along <paramref name="path"/>.</summary>
    public static DeckWindow Open(Shell shell, IReadOnlyList<RelationGraphNode> members, IReadOnlyList<Vector2> path, Action saved, Action? closed = null)
    {
        var entry = new ClusterEntry
        {
            AppName = "KAONOMI.Chat",
            MachineName = System.Environment.MachineName,
            UserName = System.Environment.UserName,
            Category = ClusterCategories.GraphNode,
            Members = members.Select(m => new ClusterMemberEntry { WindowName = m.Type, ObjectId = m.ObjectId ?? m.Key, Title = m.Title, Context = m.Context }).ToList(),
            Path = path.Select(p => new ClusterPointEntry { X = Math.Round(p.X, 1), Y = Math.Round(p.Y, 1) }).ToList()
        };
        return Open(shell, entry, isNew: true, saved, closed);
    }

    /// <summary>
    /// Opens the popup of a new cluster of <paramref name="members"/> drawn along <paramref name="path"/> in an editor
    /// other than the graph (<paramref name="category"/>: bpmnai, infra, mindmap).
    /// </summary>
    public static DeckWindow Open(Shell shell, IReadOnlyList<ClusterMemberEntry> members, IReadOnlyList<Vector2> path, string category, Action saved, Action? closed = null)
    {
        var entry = new ClusterEntry
        {
            AppName = "KAONOMI.Chat",
            MachineName = System.Environment.MachineName,
            UserName = System.Environment.UserName,
            Category = category,
            Members = [.. members],
            Path = path.Select(p => new ClusterPointEntry { X = Math.Round(p.X, 1), Y = Math.Round(p.Y, 1) }).ToList()
        };
        return Open(shell, entry, isNew: true, saved, closed);
    }

    /// <summary>Opens the popup of the stored cluster <paramref name="cluster"/>.</summary>
    public static DeckWindow Open(Shell shell, ClusterEntry cluster, Action saved, Action? closed = null) =>
        Open(shell, cluster, isNew: false, saved, closed);

    private static DeckWindow Open(Shell shell, ClusterEntry entry, bool isNew, Action saved, Action? closed)
    {
        DeckWindow? window = null;
        var content = Build(entry, isNew, saved, () =>
        {
            if (window is not null)
                shell.Deck.Close(window);
        });
        var key = isNew ? $"cluster-new#{Guid.NewGuid():N}" : $"cluster#{entry.Id}";
        var title = isNew ? $"Cluster · {entry.Members.Count} element(s)" : $"Cluster · {entry.Name}";
        window = shell.OpenControl(key, title, "◎", content, new Vector2(860, 700));
        if (closed is not null)
            window.Closed += closed;
        return window;
    }

    private static Control Build(ClusterEntry entry, bool isNew, Action saved, Action close)
    {
        // A date field ("yyyy-MM-dd", empty: none) and a probability slider (0 to 100) with its value beside it.
        static LineEdit DateField(DateTimeOffset? value, string tooltip) =>
            new() { PlaceholderText = "yyyy-MM-dd", Text = value?.ToLocalTime().ToString("yyyy-MM-dd") ?? string.Empty, CustomMinimumSize = new Vector2(104, 0), TooltipText = tooltip };
        static (HSlider Slider, Label Value) Probability(int value, int width)
        {
            var slider = new HSlider { MinValue = 0, MaxValue = 100, Step = 1, Value = Math.Clamp(value, 0, 100), CustomMinimumSize = new Vector2(width, 0), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, TooltipText = "Probability, 0 to 100 (100: sure)" };
            var label = Eve.Eve.Text_($"{(int)slider.Value} %", Eve.Eve.TextBright, 11);
            label.CustomMinimumSize = new Vector2(38, 0);
            label.HorizontalAlignment = HorizontalAlignment.Right;
            slider.ValueChanged += v => label.Text = $"{(int)v} %";
            return (slider, label);
        }
        static bool TryDate(LineEdit field, out DateTimeOffset? date)
        {
            date = null;
            var text = field.Text.Trim();
            if (text.Length == 0)
                return true;
            if (!DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out var parsed))
                return false;
            date = parsed;
            return true;
        }

        // The elements of the cluster, each with its period and its probability.
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 4);
        var memberFields = new List<(ClusterMemberEntry Member, LineEdit From, LineEdit To, HSlider Probability)>();
        foreach (var member in entry.Members)
        {
            var dot = new ColorRect { Color = RelationGraphCanvas.ColorOf(member.WindowName), CustomMinimumSize = new Vector2(8, 8), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
            var type = Eve.Eve.Caption(member.WindowName, Eve.Eve.Muted, 10);
            type.CustomMinimumSize = new Vector2(96, 0);
            var title = Eve.Eve.Text_(string.IsNullOrWhiteSpace(member.Title) ? member.ObjectId : member.Title, Eve.Eve.TextBright, 13);
            title.ClipText = true;
            title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            title.TooltipText = member.ObjectId;
            var from = DateField(member.DateFrom, "From (yyyy-MM-dd, empty: none)");
            var to = DateField(member.DateTo, "To (yyyy-MM-dd, empty: none)");
            var (probability, value) = Probability(member.Probability, 90);
            memberFields.Add((member, from, to, probability));
            list.AddChild(Ui.Row(8, dot, type, title, from, to, probability, value));
        }
        var membersPanel = new PanelContainer();
        membersPanel.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Field, Eve.Eve.Edge with { A = 0.4f }, 8));
        membersPanel.AddChild(Ui.Scroll(list));
        membersPanel.CustomMinimumSize = new Vector2(0, 120);
        membersPanel.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        // The period and the probability of the cluster itself.
        var dateFrom = DateField(entry.DateFrom, "The cluster holds from this date (yyyy-MM-dd, empty: none)");
        var dateTo = DateField(entry.DateTo, "The cluster holds until this date (yyyy-MM-dd, empty: none)");
        var (clusterProbability, clusterValue) = Probability(entry.Probability, 160);
        clusterProbability.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var name = new LineEdit { PlaceholderText = "Name of the cluster", Text = entry.Name };
        var description = new TextEdit { PlaceholderText = "What these elements have in common", Text = entry.Description, WrapMode = TextEdit.LineWrappingMode.Boundary, CustomMinimumSize = new Vector2(0, 90) };
        var titleField = new LineEdit { PlaceholderText = "Title of the cluster (besides its name)", Text = entry.Title };
        var tags = new LineEdit { PlaceholderText = MetaTags.TagsHint + " · separated by spaces or commas, '-' inside one", Text = MetaTags.Format(entry.Tags) };
        var events = new LineEdit { PlaceholderText = MetaTags.EventsHint + " · separated by spaces or commas, '-' inside one", Text = MetaTags.Format(entry.Events) };
        var signals = new LineEdit { PlaceholderText = MetaTags.SignalsHint + " · separated by spaces or commas, '-' inside one", Text = MetaTags.Format(entry.Signals) };
        var status = Eve.Eve.Text_(string.Empty, Eve.Eve.Muted, 12, wrap: true);
        status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        void SetStatus(string text, bool error)
        {
            status.Text = text;
            status.AddThemeColorOverride("font_color", error ? Eve.Eve.Red : Eve.Eve.Muted);
        }
        var save = Eve.Eve.Button(isNew ? "💾 CREATE CLUSTER" : "💾 SAVE", primary: true);
        save.AddThemeStyleboxOverride("normal", Eve.Eve.Box(Accent with { A = 0.3f }, Accent, 6));
        save.AddThemeStyleboxOverride("hover", Eve.Eve.Box(Accent with { A = 0.45f }, Accent, 6));
        var delete = Eve.Eve.Button("DELETE", tooltip: "Marks the cluster deleted (it stays in its index, out of the searches and of the graph)");
        delete.AddThemeColorOverride("font_color", Eve.Eve.Red);
        delete.Visible = !isNew;
        var cancel = Ui.Action("CANCEL", close);

        // Reads the fields into the entry; false (with the status set and the field focused) when a date is not a date.
        bool Fill()
        {
            entry.Name = name.Text.Trim();
            entry.Description = description.Text.Trim();
            entry.Title = titleField.Text.Trim();
            entry.Tags = MetaTags.Parse(tags.Text, MetaTags.Tag);
            entry.Events = MetaTags.Parse(events.Text, MetaTags.Event);
            entry.Signals = MetaTags.Parse(signals.Text, MetaTags.Signal);
            if (!TryDate(dateFrom, out var from) || !TryDate(dateTo, out var to))
            {
                SetStatus("The dates of the cluster must be yyyy-MM-dd (or empty)", true);
                (TryDate(dateFrom, out _) ? dateTo : dateFrom).GrabFocus();
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
                    (TryDate(fromField, out _) ? toField : fromField).GrabFocus();
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
                name.GrabFocus();
                return;
            }
            if (KaonomiAssistantService.Shared is not { } server)
            {
                SetStatus("No Kaonomi server: the cluster cannot be saved", true);
                return;
            }
            if (!Fill())
                return;
            save.Disabled = delete.Disabled = true;
            SetStatus("Saving…", false);
            try
            {
                var result = isNew ? await server.CreateClusterAsync(entry) : await server.UpdateClusterAsync(entry);
                if (!GodotObject.IsInstanceValid(save))
                    return;
                if (result.ReturnedId is null or "-1")
                {
                    SetStatus(result.Message ?? "The cluster was not saved", true);
                    save.Disabled = delete.Disabled = false;
                    return;
                }
                ActivityLog.Info("Cluster", $"Cluster \"{entry.Name}\" {(isNew ? "created" : "saved")} ({entry.Members.Count} element(s), tags {string.Join(' ', entry.Tags)}): {result.ReturnedId}");
                saved();
                close();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (!GodotObject.IsInstanceValid(save))
                    return;
                SetStatus("The cluster was not saved: " + ex.Message, true);
                save.Disabled = delete.Disabled = false;
                ActivityLog.Error("Cluster", "The cluster was not saved", ex);
            }
        }

        async Task DeleteAsync()
        {
            if (entry.Id is null || KaonomiAssistantService.Shared is not { } server)
                return;
            if (!await Dialogs.Confirm("Delete the cluster", $"Mark the cluster \"{entry.Name}\" deleted? It stays in its index but leaves the searches and the graph.", "Delete"))
                return;
            save.Disabled = delete.Disabled = true;
            SetStatus("Deleting…", false);
            try
            {
                var result = await server.DeleteClusterAsync(entry.Id);
                if (!GodotObject.IsInstanceValid(save))
                    return;
                if (result.ReturnedId is null or "-1")
                {
                    SetStatus(result.Message ?? "The cluster was not deleted", true);
                    save.Disabled = delete.Disabled = false;
                    return;
                }
                ActivityLog.Info("Cluster", $"Cluster \"{entry.Name}\" marked deleted: {entry.Id}");
                saved();
                close();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (!GodotObject.IsInstanceValid(save))
                    return;
                SetStatus("The cluster was not deleted: " + ex.Message, true);
                save.Disabled = delete.Disabled = false;
            }
        }
        save.Pressed += () => _ = SaveAsync();
        delete.Pressed += () => _ = DeleteAsync();
        name.TextSubmitted += text => { _ = SaveAsync(); };

        var heading = isNew
            ? $"{entry.Members.Count} element(s) inside the drawing"
            : $"{entry.Members.Count} element(s) · {entry.Id}{(entry.Date is { } date ? " · " + date.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : string.Empty)}";
        var form = Ui.Column(10,
            Eve.Eve.Caption(heading, Eve.Eve.TextBright, 12),
            membersPanel,
            Ui.Field("Name", name),
            Ui.Field("Title", titleField),
            Ui.Field("Description", description),
            Ui.Field("Tags", tags),
            Ui.Field("Events", events),
            Ui.Field("Signals", signals),
            Ui.Row(10, Ui.Field("From", dateFrom), Ui.Field("To", dateTo), Ui.Field("Probability", Ui.Row(8, clusterProbability, clusterValue))),
            Ui.Row(8, status, delete, cancel, save));
        var root = Ui.Margin(form, 14);
        root.Ready += () => name.GrabFocus();
        root.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
            {
                root.AcceptEvent();
                close();
            }
        };
        return root;
    }
}
