using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// /search: a loose search in the indices of KAONOMI at once (Enter or Search; a typo or the start of a word is enough).
/// Under the search bar, one toggle per index (its icon, its colour, the number of hits after a search) to tick or not;
/// on the left the hits as cards grouped by index (a strip of the colour of the index, its icon, the title, a piece of
/// the text, the date and the score, a gauge of the score); on the right the selected one: its title and ids, OPEN,
/// COPY ID, COPY JSON, and the document as coloured JSON. A double-click on a hit opens it in the window of its kind.
/// </summary>
[ToolView(typeof(SearchWindowViewModel), "Search", "🔎", 1180, 760)]
public sealed class SearchView : IToolView
{
    /// <summary>The icon of each index (the ones of the tools of the bar of commands).</summary>
    private static readonly Dictionary<string, string> Icons = new()
    {
        ["cache"] = "🗃", ["completion"] = "☁", ["dialog"] = "💬", ["diary"] = "📓", ["document"] = "📄", ["idea"] = "💡",
        ["imageanalysis"] = "🔬", ["intel"] = "🕵", ["ontology"] = "🌳", ["relation"] = "🕸", ["taskitem"] = "📋", ["youtubevideo"] = "📺", ["cluster"] = "◎", ["wiki"] = "📚", ["webpage"] = "🕷"
    };

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (SearchWindowViewModel)viewModel;
        vm.OpenRequested += hit => Ui.OnMain(() => _ = SearchOpener.OpenAsync(shell, vm, hit));

        // The search bar.
        var glyph = DataUi.Text("🔎", Ui.Muted, 20);
        var bar = DataUi.SearchBar(vm, nameof(vm.Term), vm.SearchCommand, "Search every index… a typo or the start of a word is enough", out var edit);
        DataUi.FocusWhenShown(edit);
        var top = Ui.Box(DataUi.Bar([glyph], bar), 8);

        // The indices: one toggle each, in its colour while ticked, its count after a search.
        var chips = new WrapPanel();
        foreach (var index in vm.Indices)
            chips.Children.Add(Chip(index));
        var indices = DataUi.Bar([], chips, DataUi.Command("ALL / NONE", vm.ToggleAllCommand, "Tick every index, or none"));

        // The hits.
        var left = new Panel();
        var empty = EmptyState();
        var list = DataUi.Items(vm.Results, hit => HitRow(vm, hit), empty, virtualize: true);
        left.Children.Add(Ui.Scroll(list));
        left.Children.Add(empty);

        // The selected hit.
        var detail = DataUi.Swap(vm, nameof(vm.SelectedResult), value => value is SearchHitViewModel hit ? Detail(vm, hit) : DataUi.Placeholder("Select a hit"));
        detail.Margin = new Thickness(6, 0, 0, 0);

        top.Margin = new Thickness(10, 8, 10, 0);
        indices.Margin = new Thickness(10, 8, 10, 8);
        var split = DataUi.SplitH(left, detail, 420, 300);
        split.Margin = new Thickness(10, 0, 10, 6);
        return Ui.Dock(
            (top, Dock.Top),
            (indices, Dock.Top),
            (DataUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsSearching)), Dock.Bottom),
            (split, Dock.Top));
    }

    // ── Looks ──

    private static bool IsLight => Application.Current?.ActualThemeVariant == ThemeVariant.Light;

    /// <summary>The colour of an index: a hue of its own, soft on the dark theme, deeper on the light one.</summary>
    private static IBrush ColorOf(string index)
    {
        var i = Array.IndexOf(SearchWindowViewModel.AllIndices, index);
        var hue = (((i < 0 ? 0 : i) * 0.083 + 0.52) % 1.0) * 360;
        return new SolidColorBrush(HsvColor.ToRgb(hue, IsLight ? 0.6 : 0.42, IsLight ? 0.62 : 0.92));
    }

    private static string IconOf(string index) => Icons.TryGetValue(index, out var icon) ? icon : "📁";

    /// <summary>The toggle of an index: ticked (its colour) or not, its icon, its name, its count.</summary>
    private static Control Chip(SearchIndexChoice index)
    {
        var color = ColorOf(index.Name);
        var label = new TextBlock { FontSize = 11 };
        var button = new ToggleButton { Content = label, Focusable = false, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 4), Cursor = new Cursor(StandardCursorType.Hand) };
        var updating = false;
        void Refresh()
        {
            updating = true;
            button.IsChecked = index.Enabled;
            updating = false;
            label.Text = $"{IconOf(index.Name)}  {index.Name.ToUpperInvariant()}{(index.Count > 0 ? $"  {index.Count}" : string.Empty)}";
            label.Foreground = index.Enabled ? color : Ui.Muted;
            button.BorderBrush = index.Enabled ? color : Ui.Edge;
        }
        button.IsCheckedChanged += (_, _) =>
        {
            if (!updating)
                index.Enabled = button.IsChecked == true;
        };
        Ui.When(button, index, Refresh, nameof(index.Enabled), nameof(index.Count));
        return button;
    }

    /// <summary>Nothing yet: the glass and the hint.</summary>
    private static Control EmptyState()
    {
        var glyph = DataUi.Text("🔎", Ui.Muted, 46);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        var text = Ui.Label("Type a few words and press Enter: the indices ticked above are searched at once.\nA typo or the start of a word is enough.", Ui.Muted, 12, wrap: true);
        text.TextAlignment = TextAlignment.Center;
        var column = Ui.Column(8, glyph, text);
        column.VerticalAlignment = VerticalAlignment.Center;
        column.Margin = new Thickness(24);
        return column;
    }

    /// <summary>A hit: the header of its group when it opens one, then its card.</summary>
    private static Control HitRow(SearchWindowViewModel vm, SearchHitViewModel hit)
    {
        var card = Card(vm, hit);
        card.Margin = new Thickness(0, 0, 4, 4);
        if (!hit.IsFirstOfIndex)
            return card;
        var color = ColorOf(hit.Index);
        var dot = new Border { Width = 8, Height = 8, Background = color, VerticalAlignment = VerticalAlignment.Center };
        var name = DataUi.Caption($"{IconOf(hit.Index)}  {hit.Index}", Ui.TextBright, 11);
        var count = DataUi.Text($"{hit.CountInIndex} hit(s)", Ui.Muted, 10);
        var line = new Border { Height = 1, Background = color, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center };
        var header = DataUi.Bar([dot, name, count], line);
        header.Margin = new Thickness(4, 10, 4, 4);
        return Ui.Column(0, header, card);
    }

    /// <summary>
    /// The card of a hit: a strip of the colour of its index, its icon, the title (one line), the text (wrapped), the
    /// date and the score, a gauge of the score. A click selects it, a double-click opens it.
    /// </summary>
    private static Control Card(SearchWindowViewModel vm, SearchHitViewModel hit)
    {
        var color = ColorOf(hit.Index);
        var icon = DataUi.Text(IconOf(hit.Index), Ui.TextBright, 22);
        icon.Width = 34;
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.TextAlignment = TextAlignment.Center;
        var title = DataUi.Text(DataUi.Trim(hit.Title, 140), Ui.TextBright, 13, bold: true);
        var snippet = Ui.Label(DataUi.Trim(hit.Snippet, 170), Ui.Muted, 11, wrap: true);
        var meta = DataUi.Text(Meta(hit), Ui.Muted, 10);
        // The gauge of the score: a thin track, filled in the colour of the index as far as its weight in its index.
        var gauge = new Grid { Width = 70, Height = 3, VerticalAlignment = VerticalAlignment.Center, Background = Ui.Edge };
        gauge.Children.Add(new Border { Background = color, HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(4, 70 * Math.Clamp(hit.Relative, 0, 1)) });
        var footer = DataUi.Bar([], meta, gauge);
        var content = Ui.Column(3, title, snippet, footer);
        var strip = new Border { Width = 4, Background = color };
        var inner = Ui.Dock((strip, Dock.Left), (icon, Dock.Left), (content, Dock.Left));
        icon.Margin = new Thickness(8, 0, 6, 0);
        content.Margin = new Thickness(0, 8, 10, 8);
        var card = DataUi.ClickRow(inner, () => vm.SelectedResult = hit, () => vm.Open(hit), vm, nameof(vm.SelectedResult),
            () => ReferenceEquals(vm.SelectedResult, hit), color, padding: 0);
        ToolTip.SetTip(card, "Click: show it on the right · double-click: open it in its window");
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

    /// <summary>The selected hit: its icon, title, ids and buttons, then its document as coloured JSON.</summary>
    private static Control Detail(SearchWindowViewModel vm, SearchHitViewModel hit)
    {
        var color = ColorOf(hit.Index);
        var icon = DataUi.Text(IconOf(hit.Index), Ui.TextBright, 30);
        icon.Width = 44;
        icon.TextAlignment = TextAlignment.Center;
        var title = Ui.Label(hit.Title ?? string.Empty, Ui.TextBright, 15, wrap: true, bold: true);
        var badge = DataUi.Badge(hit.Index, color);
        var ids = Ui.Label($"{hit.Id}  ·  {Meta(hit)}", Ui.Muted, 10, wrap: true);
        var buttons = Ui.Row(6,
            DataUi.Action("OPEN", () => vm.Open(hit), "Opens the document in the window of its kind (a double-click on the hit does too)", primary: true),
            DataUi.Action("COPY ID", () => Dialogs.Copy(hit.Id), "Copies the id of the document"),
            DataUi.Action("COPY JSON", () => Dialogs.Copy(hit.SourceJson), "Copies the document as JSON"));
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var head = Ui.Dock((icon, Dock.Left), (Ui.Column(4, DataUi.Bar([badge], title), ids, buttons), Dock.Left));
        var card = new Border
        {
            Child = head,
            Padding = new Thickness(12),
            Background = Ui.Panel,
            BorderBrush = color,
            BorderThickness = new Thickness(1, 3, 1, 1),
            CornerRadius = new CornerRadius(2),
            Margin = new Thickness(0, 0, 0, 8)
        };

        // The document as JSON, coloured (keys, strings, numbers, true / false / null).
        var json = CodeSyntax.View(lineNumbers: false, wrap: true);
        json.ShowJson(hit.SourceJson ?? string.Empty);
        return Ui.Dock((card, Dock.Top), (json, Dock.Top));
    }
}
