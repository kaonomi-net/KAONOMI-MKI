using System;
using System.Collections.Generic;
using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Data;

/// <summary>
/// /search: a loose search in the indices of KAONOMI at once (Enter or Search; a typo or the start of a word is enough).
/// Under the search bar, one pill per index (its icon, its colour, the number of hits after a search) to tick or not;
/// on the left the hits as cards grouped by index (a line of the colour of the index, its icon, the title, a piece of
/// the text, the date and the application, a gauge of the score); on the right the selected one: a card with its
/// title and ids, OPEN, COPY ID, COPY JSON, and the document as coloured JSON. A double-click on a hit opens it in the
/// window of its kind.
/// </summary>
[ToolView(typeof(SearchWindowViewModel), "Search", "🔎", 1180, 760)]
public sealed class SearchView : IToolView
{
    /// <summary>The icon of each index (the ones of the tools of the bar of commands).</summary>
    private static readonly Dictionary<string, string> Icons = new()
    {
        ["cache"] = "🗃", ["completion"] = "☁", ["dialog"] = "💬", ["diary"] = "📓", ["document"] = "📄", ["idea"] = "💡",
        ["imageanalysis"] = "🔬", ["intel"] = "🕵", ["ontology"] = "🌳", ["relation"] = "🕸", ["taskitem"] = "📋", ["youtubevideo"] = "📺", ["cluster"] = "◎", ["wiki"] = "📚", ["webpage"] = "🕷", ["interview"] = "🎙", ["interviewparagraph"] = "🗣"
    };

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (SearchWindowViewModel)viewModel;
        vm.OpenRequested += hit => _ = SearchOpener.OpenAsync(shell, vm, hit);

        // The search bar: a glass strip with the glass and the field.
        var glyph = Eve.Eve.Text_("🔎", Eve.Eve.Muted, 20);
        glyph.CustomMinimumSize = new Vector2(30, 0);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        var bar = Ui.SearchBar(vm, nameof(vm.Term), vm.SearchCommand, "Search every index… a typo or the start of a word is enough");
        var top = new PanelContainer();
        top.AddThemeStyleboxOverride("panel", Glass(Eve.Eve.IsLight ? 0.5f : 0.05f, 12, 10));
        top.AddChild(Ui.Row(8, glyph, Ui.Fill(bar)));

        // The indices: one pill each, lit in its colour while ticked, its count after a search.
        var chips = new HFlowContainer();
        chips.AddThemeConstantOverride("h_separation", 6);
        chips.AddThemeConstantOverride("v_separation", 6);
        foreach (var index in vm.Indices)
            chips.AddChild(Chip(index));
        var all = Ui.Command("ALL / NONE", vm.ToggleAllCommand, tooltip: "Tick every index, or none");
        var indices = Ui.Row(10, Ui.Fill(chips), all);

        // The hits.
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 4);
        var empty = EmptyState();
        Bind.Items(list, vm.Results, hit => HitRow(vm, hit), empty);
        var left = Ui.Column(0, Ui.FillHeight(Ui.Scroll(list)), empty);
        left.CustomMinimumSize = new Vector2(400, 0);

        // The selected hit.
        var detail = Ui.Swap(vm, nameof(vm.SelectedResult), value => value is SearchHitViewModel hit ? Detail(vm, hit) : Ui.Placeholder("Select a hit"));

        var page = Ui.Column(8, top, indices, Ui.SplitH(left, detail, 420), Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsSearching)));
        return Ui.Margin(page, 10, 8, 10, 6);
    }

    // ── Looks ──

    /// <summary>The colour of an index: a hue of its own, soft on the dark theme, deeper on the light one.</summary>
    private static Color ColorOf(string index)
    {
        var i = Array.IndexOf(SearchWindowViewModel.AllIndices, index);
        var hue = ((i < 0 ? 0 : i) * 0.083f + 0.52f) % 1f;
        return Color.FromHsv(hue, Eve.Eve.IsLight ? 0.6f : 0.42f, Eve.Eve.IsLight ? 0.62f : 0.92f);
    }

    private static string IconOf(string index) => Icons.TryGetValue(index, out var icon) ? icon : "📁";

    /// <summary>A pane of glass: a faint white fill, a hairline, rounded.</summary>
    private static StyleBoxFlat Glass(float alpha, int radius, int margin)
    {
        var box = Eve.Eve.Box(new Color(1, 1, 1, alpha), new Color(1, 1, 1, Eve.Eve.IsLight ? 0.8f : 0.1f), margin, 1);
        box.SetCornerRadiusAll(radius);
        return box;
    }

    /// <summary>The pill of an index: ticked (its colour) or not (a hairline), its icon, its name, its count.</summary>
    private static Control Chip(SearchIndexChoice index)
    {
        var color = ColorOf(index.Name);
        var button = new Button { ToggleMode = true, FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand, CustomMinimumSize = new Vector2(0, 28) };
        button.AddThemeFontOverride("font", Eve.Eve.Display);
        button.AddThemeFontSizeOverride("font_size", 11);
        StyleBoxFlat Pill(Color back, Color edge)
        {
            var box = Eve.Eve.Box(back, edge, 4, 1);
            box.ContentMarginLeft = box.ContentMarginRight = 10;
            box.SetCornerRadiusAll(14);
            return box;
        }
        button.AddThemeStyleboxOverride("normal", Pill(new Color(0, 0, 0, 0), new Color(1, 1, 1, Eve.Eve.IsLight ? 0.5f : 0.14f)));
        button.AddThemeStyleboxOverride("hover", Pill(new Color(1, 1, 1, Eve.Eve.IsLight ? 0.5f : 0.08f), new Color(1, 1, 1, Eve.Eve.IsLight ? 0.8f : 0.25f)));
        button.AddThemeStyleboxOverride("pressed", Pill(color with { A = 0.18f }, color with { A = 0.8f }));
        button.AddThemeStyleboxOverride("hover_pressed", Pill(color with { A = 0.26f }, color));
        button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        button.AddThemeColorOverride("font_color", Eve.Eve.Muted);
        button.AddThemeColorOverride("font_hover_color", Eve.Eve.TextBright);
        button.AddThemeColorOverride("font_pressed_color", Eve.Eve.TextBright);
        button.AddThemeColorOverride("font_hover_pressed_color", Eve.Eve.TextBright);
        void Refresh()
        {
            button.SetPressedNoSignal(index.Enabled);
            button.Text = $"{IconOf(index.Name)}  {index.Name.ToUpperInvariant()}{(index.Count > 0 ? $"  {index.Count}" : string.Empty)}";
        }
        button.Toggled += on => index.Enabled = on;
        Bind.When(button, index, Refresh, nameof(index.Enabled), nameof(index.Count));
        Refresh();
        return button;
    }

    /// <summary>Nothing yet: the glass, big and faint, and the hint.</summary>
    private static Control EmptyState()
    {
        var glyph = Eve.Eve.Text_("🔎", Eve.Eve.Muted with { A = 0.35f }, 46);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        var text = Eve.Eve.Text_("Type a few words and press Enter: the indices ticked above are searched at once.\nA typo or the start of a word is enough.", Eve.Eve.Muted, 12, wrap: true);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        var column = Ui.Column(8, glyph, text);
        column.Alignment = BoxContainer.AlignmentMode.Center;
        column.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        return Ui.Margin(column, 24, 40, 24, 24);
    }

    /// <summary>A hit: the header of its group when it opens one, then its card.</summary>
    private static Control HitRow(SearchWindowViewModel vm, SearchHitViewModel hit)
    {
        var card = Card(vm, hit);
        if (!hit.IsFirstOfIndex)
            return card;
        var color = ColorOf(hit.Index);
        var dot = new ColorRect { Color = color, CustomMinimumSize = new Vector2(8, 8), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore };
        var name = Eve.Eve.Caption($"{IconOf(hit.Index)}  {hit.Index}", Eve.Eve.TextBright, 11);
        var count = Eve.Eve.Text_($"{hit.CountInIndex} hit(s)", Eve.Eve.Muted, 10);
        count.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        var line = new ColorRect { Color = color with { A = 0.35f }, CustomMinimumSize = new Vector2(0, 1), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, MouseFilter = Control.MouseFilterEnum.Ignore };
        var header = Ui.Margin(Ui.Row(8, dot, name, count, line), 4, 10, 4, 2);
        return Ui.Column(4, header, card);
    }

    /// <summary>
    /// The card of a hit: a line of the colour of its index, its icon, the title (one line, cut with an ellipsis), the
    /// text (wrapped), the date and the application, a gauge of the score. Its height follows its content; a click
    /// selects it, a double-click opens it.
    /// </summary>
    private static Control Card(SearchWindowViewModel vm, SearchHitViewModel hit)
    {
        var color = ColorOf(hit.Index);
        var card = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.PointingHand, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        StyleBoxFlat Look(float fill, float edge)
        {
            var box = Eve.Eve.Box(new Color(1, 1, 1, fill), new Color(1, 1, 1, edge), 0, 1);
            box.SetCornerRadiusAll(10);
            box.BorderWidthLeft = 4;
            box.BorderColor = color with { A = 0.9f };
            box.ContentMarginLeft = 12;
            box.ContentMarginRight = 10;
            box.ContentMarginTop = 8;
            box.ContentMarginBottom = 8;
            return box;
        }
        var rest = Look(Eve.Eve.IsLight ? 0.35f : 0.03f, Eve.Eve.IsLight ? 0.6f : 0.08f);
        var hover = Look(Eve.Eve.IsLight ? 0.55f : 0.07f, Eve.Eve.IsLight ? 0.8f : 0.16f);
        var chosen = Look(Eve.Eve.IsLight ? 0.7f : 0.11f, Eve.Eve.IsLight ? 0.9f : 0.3f);
        var hovered = false;
        void Restyle() => card.AddThemeStyleboxOverride("panel", ReferenceEquals(vm.SelectedResult, hit) ? chosen : hovered ? hover : rest);

        var icon = Eve.Eve.Text_(IconOf(hit.Index), Eve.Eve.TextBright, 22);
        icon.CustomMinimumSize = new Vector2(34, 0);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Top;
        var title = Eve.Eve.Text_(Trim(hit.Title, 140), Eve.Eve.TextBright, 13);
        title.AddThemeFontOverride("font", new SystemFont { FontNames = Eve.Eve.Body.FontNames, FontWeight = 600 });
        title.ClipText = true;
        title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var snippet = Eve.Eve.Text_(Trim(hit.Snippet, 170), Eve.Eve.Muted, 11, wrap: true);
        snippet.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var meta = Eve.Eve.Text_(Meta(hit), Eve.Eve.Muted, 10);
        meta.ClipText = true;
        meta.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        meta.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        // The gauge of the score: a thin track, filled in the colour of the index as far as its weight in its index.
        var track = new ColorRect { Color = new Color(1, 1, 1, Eve.Eve.IsLight ? 0.35f : 0.08f), CustomMinimumSize = new Vector2(70, 3), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, ClipContents = true };
        var fill = new ColorRect { Color = color, Position = Vector2.Zero, Size = new Vector2(Math.Max(4, (float)(70 * hit.Relative)), 3) };
        track.AddChild(fill);
        var footer = Ui.Row(8, meta, track);
        var content = Ui.Column(3, title, snippet, footer);
        content.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var row = Ui.Row(8, icon, content);
        foreach (var child in new Control[] { row, icon, content, title, snippet, footer, meta, track, fill })
            child.MouseFilter = Control.MouseFilterEnum.Ignore;
        card.AddChild(row);
        card.TooltipText = "Click: show it on the right · double-click: open it in its window";
        card.MouseEntered += () => { hovered = true; Restyle(); };
        card.MouseExited += () => { hovered = false; Restyle(); };
        card.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click)
                return;
            card.AcceptEvent();
            vm.SelectedResult = hit;
            if (click.DoubleClick)
                vm.Open(hit);
        };
        Bind.When(card, vm, Restyle, nameof(vm.SelectedResult));
        Restyle();
        return card;
    }

    private static string Meta(SearchHitViewModel hit)
    {
        var parts = new List<string>();
        if (hit.Hit.Date is { } date)
            parts.Add(date.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        parts.Add($"score {hit.Hit.Score:0.0}");
        return string.Join("  ·  ", parts);
    }

    private static string Trim(string? text, int length)
    {
        var line = (text ?? string.Empty).Replace('\n', ' ').Trim();
        return line.Length > length ? line[..length] + "…" : line;
    }

    /// <summary>The selected hit: a card with its icon, title, ids and buttons, then its document as coloured JSON.</summary>
    private static Control Detail(SearchWindowViewModel vm, SearchHitViewModel hit)
    {
        var color = ColorOf(hit.Index);
        var icon = Eve.Eve.Text_(IconOf(hit.Index), Eve.Eve.TextBright, 30);
        icon.CustomMinimumSize = new Vector2(44, 0);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var title = Eve.Eve.Text_(hit.Title, Eve.Eve.TextBright, 15, wrap: true);
        title.AddThemeFontOverride("font", Eve.Eve.Display);
        var badge = Ui.Badge(hit.Index, color);
        var ids = Eve.Eve.Text_($"{hit.Id}  ·  {Meta(hit)}", Eve.Eve.Muted, 10, wrap: true);
        var buttons = Ui.Row(6,
            Ui.Action("OPEN", () => vm.Open(hit), primary: true, tooltip: "Opens the document in the window of its kind (a double-click on the hit does too)"),
            Ui.Action("COPY ID", () => DisplayServer.ClipboardSet(hit.Id), tooltip: "Copies the id of the document"),
            Ui.Action("COPY JSON", () => DisplayServer.ClipboardSet(hit.SourceJson), tooltip: "Copies the document as JSON"));
        buttons.Alignment = BoxContainer.AlignmentMode.End;
        var card = new PanelContainer();
        var box = Glass(Eve.Eve.IsLight ? 0.45f : 0.04f, 12, 12);
        box.BorderWidthTop = 3;
        box.BorderColor = color with { A = 0.85f };
        card.AddThemeStyleboxOverride("panel", box);
        card.AddChild(Ui.Row(12, icon, Ui.Fill(Ui.Column(4, Ui.Row(8, badge, Ui.Fill(title)), ids, buttons))));

        // The document as JSON, coloured (keys, strings, numbers, true / false / null) by the highlighter of the JSON client.
        var json = Dashboards.CodeEditors.Editor(editable: false, lineNumbers: false);
        json.Text = hit.SourceJson;
        json.SyntaxHighlighter = Dashboards.CodeEditors.Json();
        json.WrapMode = TextEdit.LineWrappingMode.Boundary;
        json.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        return Ui.Margin(Ui.Column(8, card, Ui.FillHeight(json)), 6, 0, 0, 0);
    }
}
