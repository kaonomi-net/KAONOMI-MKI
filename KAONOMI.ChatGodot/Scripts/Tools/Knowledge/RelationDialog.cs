using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.Views;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Knowledge;

/// <summary>
/// The popup of a relation (port of <c>RelationDialog</c>), a window of the desktop: on the left the element it starts
/// from, on the right the elements it goes to, in the order of the string (their window, the title and the id of
/// their object; ↻ Refresh reads the titles again, for an entity of the ontology its name; a text to complete what is
/// known about them; their data); in the middle the nature of the link, its title, its probability (-100 to 100), its
/// optional dates (From, To) and its description. It opens for a new link (the red string drawn in the graph) or for a
/// stored relation (a double-click on a link of <c>/relation</c>): its current version is shown, and the list of its
/// versions loads an older one in the form. Save stores a new version in the <c>relation</c> index of the Kaonomi
/// server, category "interdata" (<c>PUT /relation</c>); Delete removes a stored one (<c>DELETE /relation/{id}</c>). Escape or Cancel
/// closes it.
/// </summary>
internal static class RelationDialog
{
    private static readonly Color Accent = new("#E11D48");

    /// <summary>Opens the popup of a stored relation, on its current version.</summary>
    public static DeckWindow Open(Shell shell, RelationEntry relation, Action saved, Action? closed = null) =>
        Open(shell, EndOf(relation.Current.Source), relation.Current.Destinations.Select(EndOf).ToList(), relation, saved, closed);

    private static LinkEnd EndOf(RelationEndEntry end) => new(end.WindowName ?? "Window", end.ObjectId, end.Context ?? "{}");

    /// <summary>
    /// Opens the popup of a new link from <paramref name="source"/> to <paramref name="destinations"/> (or of
    /// <paramref name="existing"/>); <paramref name="saved"/> runs once it is saved, <paramref name="closed"/> when its
    /// window closes (saved or not).
    /// </summary>
    public static DeckWindow Open(Shell shell, LinkEnd source, IReadOnlyList<LinkEnd> destinations, RelationEntry? existing, Action saved, Action? closed = null)
    {
        var title = existing is null
            ? $"Link · {source.WindowName} → {string.Join(", ", destinations.Select(d => d.WindowName))}"
            : $"Relation · {existing.Current.Title ?? existing.Current.Nature ?? "(untitled)"} · version {existing.Current.Version}";
        DeckWindow? window = null;
        var content = Build(source, destinations, existing, saved, () =>
        {
            if (window is not null)
                shell.Deck.Close(window);
        });
        var key = existing?.Id is { } id ? $"relation#{id}" : $"relation-link#{Guid.NewGuid():N}";
        window = shell.OpenControl(key, title, "🔗", content, new Vector2(1200, 720));
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
        var titleBlocks = ends.Select(_ => KnowledgeUi.Selectable(string.Empty, Eve.Eve.TextBright, 13)).ToList();
        void ShowTitles()
        {
            for (var i = 0; i < titleBlocks.Count; i++)
            {
                titleBlocks[i].Text = titles[i] ?? "(no title)";
                titleBlocks[i].Modulate = titles[i] is null ? new Color(1, 1, 1, 0.5f) : Colors.White;
            }
        }

        var nature = new LineEdit { PlaceholderText = "e.g. same person, cause of, illustrates, contradicts…" };
        var titleEdit = new LineEdit { PlaceholderText = "Title of the link" };
        var (probabilityPanel, probability) = ProbabilityField();
        var dateFrom = new DateBox { Tip = "Optional: the date the link holds from" };
        var dateTo = new DateBox { Tip = "Optional: the date the link holds until" };
        var dates = Ui.Row(10, Ui.Fill(Ui.Field("From", dateFrom)), Ui.Fill(Ui.Field("To", dateTo)));
        var description = new TextEdit
        {
            PlaceholderText = "Description of the link",
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            CustomMinimumSize = new Vector2(0, 80),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        var status = Eve.Eve.Text_(string.Empty, Eve.Eve.Muted, 12, wrap: true);
        status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        void SetStatus(string text, bool error)
        {
            status.Text = text;
            status.AddThemeColorOverride("font_color", error ? Eve.Eve.Red : Eve.Eve.Muted);
        }
        var save = Eve.Eve.Button("💾 SAVE", primary: true);
        save.AddThemeStyleboxOverride("normal", Eve.Eve.Box(Accent with { A = 0.3f }, Accent, 6));
        save.AddThemeStyleboxOverride("hover", Eve.Eve.Box(Accent with { A = 0.45f }, Accent, 6));
        var cancel = Ui.Action("CANCEL", close);
        var refresh = Eve.Eve.Button("↻ REFRESH", tooltip: "Read the titles of the source and of the destinations again (for an entity of the ontology its name); Save keeps them");

        // The titles read again: from the server for the objects it stores, else from the data of the windows.
        async Task RefreshAsync()
        {
            refresh.Disabled = true;
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
            if (!GodotObject.IsInstanceValid(refresh))
                return;
            ShowTitles();
            SetStatus(changed == 0 ? "Titles up to date" : $"{changed} title(s) updated · Save to keep them in a new version", false);
            refresh.Disabled = false;
            ActivityLog.Info("Relation", $"Titles refreshed: {string.Join(" → ", titles.Select(t => t ?? "(none)"))}");
        }
        refresh.Pressed += () => _ = RefreshAsync();

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
            save.Disabled = true;
            SetStatus("Saving…", false);
            // Only the new version is sent: the server adds it to the versions of a stored relation.
            var version = new RelationVersionEntry
            {
                UserName = System.Environment.UserName,
                MachineName = System.Environment.MachineName,
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
                MachineName = System.Environment.MachineName,
                UserName = System.Environment.UserName,
                Category = existing?.Category ?? "interdata",
                Versions = [version]
            };
            try
            {
                var result = await server.SaveRelationAsync(relation);
                if (result.ReturnedId is null or "-1")
                {
                    SetStatus("Not saved: " + result.Message, true);
                    save.Disabled = false;
                    return;
                }
                ActivityLog.Info("Relation", $"Link saved ({result.Message}): {source.WindowName} → {string.Join(", ", destinations.Select(d => d.WindowName))}, {version.Nature} · {version.Title}");
                saved();
                close();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                SetStatus("Not saved: " + ex.Message, true);
                save.Disabled = false;
            }
        }
        save.Pressed += () => _ = SaveAsync();

        // A stored relation can be deleted (soft delete on the server: it leaves the graph, its versions are kept).
        var delete = Eve.Eve.Button("🗑 DELETE", tooltip: "Delete this relation, all its versions with it (DELETE /relation/{id}: marked as deleted on the server)");
        delete.AddThemeColorOverride("font_color", Eve.Eve.Red);
        delete.Visible = existing?.Id is not null;
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
            delete.Disabled = save.Disabled = true;
            SetStatus("Deleting…", false);
            try
            {
                var result = await server.DeleteRelationAsync(id);
                if (!result.Success)
                {
                    SetStatus("Not deleted: " + result.Message, true);
                    delete.Disabled = save.Disabled = false;
                    return;
                }
                ActivityLog.Info("Relation", $"Relation deleted ({id}): {existing.Current.Nature} · {existing.Current.Title}");
                saved();
                close();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                SetStatus("Not deleted: " + ex.Message, true);
                delete.Disabled = save.Disabled = false;
            }
        }
        delete.Pressed += () => _ = DeleteAsync();

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
        Control versionsPanel = new Control();
        if (existing is { Versions.Count: > 0 })
        {
            var versions = existing.Versions.AsEnumerable().Reverse().ToList();
            var picker = new OptionButton { TooltipText = "The versions of the relation, the newest first: choose one to see it in the form; Save makes a new version from what the form holds", ClipText = true };
            foreach (var v in versions)
                picker.AddItem($"Version {v.Version} · {v.Date?.ToLocalTime():yyyy-MM-dd HH:mm}{(ReferenceEquals(v, existing.Current) ? " (current)" : string.Empty)}");
            picker.Selected = 0;
            picker.ItemSelected += index =>
            {
                if (index >= 0 && index < versions.Count)
                    Show(versions[(int)index]);
            };
            versionsPanel = Ui.Field($"Versions ({existing.Versions.Count}) · Save adds version {existing.Versions.Max(v => v.Version) + 1}", picker);
            Show(existing.Current);
            save.Text = "💾 SAVE A NEW VERSION";
        }

        // The link in the middle.
        var middle = Card(existing is null ? "THE LINK" : "THE RELATION", Ui.Column(6,
            versionsPanel,
            Ui.Field("Nature of the link", nature),
            Ui.Field("Title", titleEdit),
            probabilityPanel,
            dates,
            Eve.Eve.Caption("Description"),
            description), accent: true);

        // One destination fills its column; several are stacked in it, in the order of the string, and scroll.
        Control right;
        if (destinations.Count == 1)
            right = EndCard("DESTINATION", destinations[0], titleBlocks[1], destinationInformations[0], fill: true);
        else
        {
            var stack = new VBoxContainer();
            stack.AddThemeConstantOverride("separation", 10);
            for (var i = 0; i < destinations.Count; i++)
                stack.AddChild(EndCard($"DESTINATION {i + 1} / {destinations.Count}", destinations[i], titleBlocks[i + 1], destinationInformations[i], fill: false));
            right = Ui.Scroll(stack);
        }
        var left = EndCard("SOURCE", source, titleBlocks[0], sourceInformation, fill: true);
        foreach (var (column, ratio) in new[] { (left, 1f), (middle, 1.2f), (right, 1f) })
        {
            column.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            column.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            column.SizeFlagsStretchRatio = ratio;
            column.CustomMinimumSize = new Vector2(200, 0);
        }
        var columns = Ui.Row(0, left, Arrow(), middle, Arrow(), right);
        columns.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        var buttons = Ui.Row(8, status, delete, refresh, save, cancel);
        var page = Ui.Margin(Ui.Column(12, columns, buttons), 14);
        KnowledgeUi.Shortcuts(page, key =>
        {
            if (key.Keycode != Key.Escape)
                return false;
            close();
            return true;
        });
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(nature) && nature.IsInsideTree())
                nature.GrabFocus();
        }).CallDeferred();
        return page;
    }

    /// <summary>
    /// The probability of the link: a slider from -100 (absolutely impossible) to 100 (absolutely certain), 0 (to
    /// evaluate) at the middle, with its value and its meaning above it in the colour of its side.
    /// </summary>
    private static (Control Panel, HSlider Slider) ProbabilityField()
    {
        var slider = new HSlider { MinValue = -100, MaxValue = 100, Step = 1, Value = 0, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "How probable is this link? -100: absolutely impossible · 0: to evaluate · 100: absolutely certain" };
        var value = Eve.Eve.Text_(string.Empty, Eve.Eve.Muted, 12);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
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
            value.AddThemeColorOverride("font_color", v < 0 ? new Color("#E5484D") : v > 0 ? new Color("#30C48D") : Eve.Eve.Muted);
        }
        slider.ValueChanged += _ => Show();
        Show();
        return (Ui.Column(2, Ui.Row(6, Eve.Eve.Caption("Probability"), value), slider, Marks("-100 · Absolutely impossible", "0 · To evaluate", "Absolutely certain · 100")), slider);
    }

    /// <summary>Three small marks under a slider: left, middle, right.</summary>
    internal static Control Marks(string left, string middle, string right)
    {
        Label Mark(string text, HorizontalAlignment alignment)
        {
            var label = Eve.Eve.Text_(text, Eve.Eve.Muted, 10);
            label.HorizontalAlignment = alignment;
            label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            label.ClipText = true;
            return label;
        }
        return Ui.Row(0, Mark(left, HorizontalAlignment.Left), Mark(middle, HorizontalAlignment.Center), Mark(right, HorizontalAlignment.Right));
    }

    private static string? Text(string text) => text.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    private static RelationEndEntry End(LinkEnd end, TextEdit information, string? title) => new()
    {
        WindowName = end.WindowName,
        ObjectId = end.ObjectId,
        Title = title,
        Information = Text(information.Text),
        Context = end.Context
    };

    private static TextEdit InformationBox() => new()
    {
        PlaceholderText = "What this element shows, for this link…",
        WrapMode = TextEdit.LineWrappingMode.Boundary,
        CustomMinimumSize = new Vector2(0, 120)
    };

    /// <summary>
    /// One end of the link: its window, the title and the id of its object, the text to complete (filling the card, or
    /// of a fixed height when the cards are stacked), and its data (folded).
    /// </summary>
    private static Control EndCard(string heading, LinkEnd end, RichTextLabel title, TextEdit information, bool fill)
    {
        if (fill)
            information.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        else
            information.CustomMinimumSize = new Vector2(0, 90);
        var data = new TextEdit
        {
            Text = end.Context,
            Editable = false,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            CustomMinimumSize = new Vector2(0, 150),
            Visible = false
        };
        data.AddThemeFontOverride("font", Eve.Eve.Mono);
        data.AddThemeFontSizeOverride("font_size", 10);
        var expander = new Button { Text = "▸ DATA OF THE WINDOW", ToggleMode = true, Flat = true, Alignment = HorizontalAlignment.Left };
        expander.AddThemeFontSizeOverride("font_size", 10);
        expander.Toggled += on =>
        {
            data.Visible = on;
            expander.Text = (on ? "▾" : "▸") + " DATA OF THE WINDOW";
        };
        var windowName = KnowledgeUi.Selectable(end.WindowName, Eve.Eve.TextBright, 15);
        var objectId = KnowledgeUi.Selectable(end.ObjectId ?? "(no object in this window)", end.ObjectId is null ? Eve.Eve.Muted : Eve.Eve.Text, 12);
        return Card(heading, Ui.Column(6,
            Ui.Field("Window", windowName),
            Ui.Field("Title", title),
            Ui.Field("Object id", objectId),
            Eve.Eve.Caption("Information"),
            information,
            expander,
            data));
    }

    private static PanelContainer Card(string heading, Control content, bool accent = false)
    {
        var column = Ui.Column(8, Eve.Eve.Caption(heading, Accent, 11), content);
        content.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        return KnowledgeUi.Card(column, accent ? Accent with { A = 0.85f } : null, 12, accent ? 2 : 1);
    }

    /// <summary>The red string between the cards.</summary>
    private static Control Arrow()
    {
        var holder = new CenterContainer { CustomMinimumSize = new Vector2(14, 0) };
        holder.AddChild(new ColorRect { Color = Accent, CustomMinimumSize = new Vector2(14, 3), MouseFilter = Control.MouseFilterEnum.Ignore });
        return holder;
    }
}
