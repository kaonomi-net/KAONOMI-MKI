using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// /symbol: a search of the symbolic patterns (Enter or SEARCH), NEW; the documents found on the left (summary,
/// badges deleted / new / modified, symbol, regex preview, event, tags); the selected one on the right: id and date
/// (right click on the id copies it), status, CLONE, DISCARD CHANGES, SAVE; event, comment, tags, deleted, symbol, the
/// regex patterns (each checked as it is typed, red edges and the error when it does not compile, ✕ removes it,
/// + ADD PATTERN). Below, the regex assistant (folds): example lines, extraction goal, GENERATE REGEX (CANCEL while it
/// runs, with the local Ollama model of the chat), LOAD… a saved regex; the generated regex (editable, red when
/// invalid) with ADD (to the patterns of the document) and SAVE…; the test on each example (✓ / ✗, the detected parts
/// highlighted in yellow, the details).
/// </summary>
[ToolView(typeof(SymbolicPatternWindowViewModel), "Symbolic patterns", "🔣", 1150, 800)]
public sealed class SymbolicPatternView : IToolView
{
    private static readonly IBrush MatchText = new SolidColorBrush(Color.Parse("#FFE066"));
    private static readonly IBrush MatchBack = new SolidColorBrush(Color.Parse("#4D4000"));
    private static readonly IBrush Valid = new SolidColorBrush(Color.Parse("#16C60C"));

    /// <summary>The assistant stays unfolded or folded across the selections.</summary>
    private bool _assistantOpen = true;

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (SymbolicPatternWindowViewModel)viewModel;
        vm.ConfirmAsync = (message, confirm) => Dialogs.Confirm("Symbolic patterns", message, confirm);
        vm.OpenTextFileAsync = async () => await Dialogs.OpenFile("Import symbolic patterns", ["*.json ; JSON"]) is { Length: > 0 } path ? await System.IO.File.ReadAllTextAsync(path) : null;
        vm.PickFolderAsync = title => Dialogs.OpenFolder(title);
        var toolbar = DataUi.Toolbar(DataUi.SearchBar(vm, nameof(vm.Term), vm.SearchCommand, "Search symbolic patterns…", out var search,
            DataUi.Command("NEW", vm.NewCommand, "Create a new symbolic pattern"),
            DataUi.Command("IMPORT…", vm.ImportCommand, "Import symbolic patterns from a JSON file (one or an array, as the seed files); same id: overwritten"),
            DataUi.Command("IMPORT FOLDER…", vm.ImportFolderCommand, "Import a folder exported with Export all (one JSON file each); same id: overwritten"),
            DataUi.Command("EXPORT ALL…", vm.ExportAllCommand, "Export every symbolic pattern of the server to a folder: one JSON file each")));
        DataUi.FocusWhenShown(search);

        var left = DataUi.ScrollList(vm.Results, entry => ResultRow(vm, entry), "No results", spacing: 2);
        left.Margin = new Thickness(8, 8, 0, 8);
        var right = DataUi.Swap(vm, nameof(vm.SelectedResult), value => value is SymbolicPatternEntryViewModel entry ? Editor(vm, entry) : DataUi.Placeholder("Select a document"));
        right.Margin = new Thickness(4, 8, 8, 8);

        return Ui.Dock(
            (toolbar, Dock.Top),
            (DataUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), null, vm.SearchCommand), Dock.Bottom),
            (DataUi.SplitH(left, right, 340, 260), Dock.Top));
    }

    /// <summary>A document of the list; a click selects it.</summary>
    private static Control ResultRow(SymbolicPatternWindowViewModel vm, SymbolicPatternEntryViewModel entry)
    {
        var regex = DataUi.Bound(entry, nameof(entry.RegexPreview), Ui.Muted, 11, wrap: true);
        regex.FontFamily = Ui.Mono;
        regex.MaxLines = 2;
        regex.TextWrapping = TextWrapping.WrapWithOverflow;
        regex.TextTrimming = TextTrimming.CharacterEllipsis;
        var content = Ui.Column(2,
            Ui.Row(6, DataUi.Bound(entry, nameof(entry.Summary), Ui.Muted, 10),
                DataUi.Badge("deleted", Ui.Red, entry, nameof(entry.IsDeleted)),
                DataUi.Badge("new", Ui.Green, entry, nameof(entry.IsNew)),
                DataUi.Badge("modified", DataUi.Amber, entry, nameof(entry.IsDirty))),
            DataUi.Bound(entry, nameof(entry.Symbol), Ui.TextBright, 13),
            regex,
            DataUi.Bound(entry, nameof(entry.Event), Ui.Muted, 11).Visible(entry, nameof(entry.HasEvent)),
            DataUi.Bound(entry, nameof(entry.TagsPreview), Ui.Muted, 11).Visible(entry, nameof(entry.HasTags)));
        return DataUi.ClickRow(content, () => vm.SelectedResult = entry, null, vm, nameof(vm.SelectedResult), () => ReferenceEquals(vm.SelectedResult, entry));
    }

    /// <summary>The selected document and the regex assistant.</summary>
    private Control Editor(SymbolicPatternWindowViewModel vm, SymbolicPatternEntryViewModel entry)
    {
        var status = DataUi.Tip(DataUi.ErrorColor(DataUi.Bound(entry, nameof(entry.Status), Ui.Text, 12), entry, nameof(entry.IsError), Ui.Text), entry, nameof(entry.Status));
        var statusBox = DataUi.Bar([DataUi.Dot(Ui.Red, entry, nameof(entry.IsError))], status);
        var id = DataUi.Bound(entry, nameof(entry.IdLabel), Ui.Text, 12);
        ToolTip.SetTip(id, "Right-click to copy the id");
        id.Background = Brushes.Transparent;
        id.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(id).Properties.IsRightButtonPressed && entry.Id is { } value)
            {
                Dialogs.Copy(value);
                entry.Status = "Id copied to the clipboard";
                e.Handled = true;
            }
        };
        var date = DataUi.Bound(entry, nameof(entry.DateLabel), Ui.Muted, 10);
        // Id, date and status on the left (trimmed when narrow), the actions on the right.
        var header = DataUi.Bar([], Ui.Column(0, id, date, statusBox),
            DataUi.Command("CLONE", vm.CloneCommand, "Create a copy of this document (new id on save)"),
            DataUi.Command("DISCARD CHANGES", entry.RevertCommand),
            DataUi.Command("SAVE", entry.SaveCommand, primary: true));

        // The regex patterns.
        var patterns = DataUi.Items(entry.RegexPatterns, item => PatternRow(entry, item), spacing: 6);

        var fields = Ui.Column(8,
            DataUi.Field("Title", DataUi.Line(entry, nameof(entry.Title), "Title of the pattern")),
            DataUi.Field("Event", DataUi.Line(entry, nameof(entry.Event))),
            DataUi.Field("Language", DataUi.Line(entry, nameof(entry.Language), "FR, EN, DE…")),
            DataUi.Field("Comment", DataUi.Lines(entry, nameof(entry.Comment), 50)),
            DataUi.Field("Tags (one per line)", DataUi.Lines(entry, nameof(entry.Tags), 60, MetaTags.TagsHint)),
            DataUi.Field("Events (one per line)", DataUi.Lines(entry, nameof(entry.Events), 50, MetaTags.EventsHint)),
            DataUi.Field("Signals (one per line)", DataUi.Lines(entry, nameof(entry.Signals), 50, MetaTags.SignalsHint)),
            DataUi.Check("Deleted", entry, nameof(entry.IsDeleted)),
            DataUi.Field("Symbol", DataUi.Line(entry, nameof(entry.Symbol))),
            DataUi.Bar([], DataUi.Caption("Regex patterns"), DataUi.Command("+ ADD PATTERN", entry.AddPatternCommand, "Add a regular expression")),
            patterns,
            DataUi.Expander("Regex assistant", Assistant(vm.RegexAssistant), _assistantOpen, open => _assistantOpen = open));
        fields.Margin = new Thickness(0, 0, 12, 12);
        var separator = DataUi.Separator();
        separator.Margin = new Thickness(0, 8);
        return Ui.Dock((header, Dock.Top), (separator, Dock.Top), (Ui.Scroll(fields), Dock.Top));
    }

    /// <summary>A regular expression: its text (mono, red edges when it does not compile), ✕, the error.</summary>
    private static Control PatternRow(SymbolicPatternEntryViewModel entry, RegexPatternItemViewModel item)
    {
        var edit = DataUi.InvalidEdge(DataUi.Line(item, nameof(item.Pattern), @"Regular expression, e.g. \bhello\b", mono: true), item, nameof(item.IsInvalid));
        var remove = DataUi.Command("✕", entry.RemovePatternCommand, "Remove this regular expression", parameter: item);
        var error = DataUi.Bound(item, nameof(item.Error), Ui.Red, 11, wrap: true).Visible(item, nameof(item.IsInvalid));
        return Ui.Column(2, DataUi.Bar([], edit, remove), error);
    }

    /// <summary>The regex assistant: examples + goal → a regex from the local model → tested, added, saved.</summary>
    private static Control Assistant(RegexAssistantViewModel assistant)
    {
        var examples = DataUi.Lines(assistant, nameof(assistant.Examples), 90, "Order 12345 shipped on 2026-10-08\nOrder 987 shipped on 2026-09-30", mono: true, wrap: false);
        var generate = DataUi.Command("GENERATE REGEX", assistant.GenerateCommand, "Ask the local Ollama model selected in the chat window for a regex", primary: true);
        generate.MinWidth = 140;
        generate.Visible(assistant.GenerateCommand, "IsRunning", invert: true);
        var cancel = DataUi.Command("CANCEL", assistant.GenerateCancelCommand);
        cancel.MinWidth = 140;
        cancel.Visible(assistant.GenerateCommand, "IsRunning");
        var load = DataUi.Action("LOAD…", () => Load(assistant), "Load a saved regex with its goal and examples (regex folder of config.yml)");

        var status = DataUi.ErrorColor(DataUi.Bound(assistant, nameof(assistant.Status), Ui.Text, 12, wrap: true), assistant, nameof(assistant.IsError), Ui.Text);

        // The generated regex and its test.
        var regex = DataUi.InvalidEdge(DataUi.Line(assistant, nameof(assistant.GeneratedRegex), mono: true), assistant, nameof(assistant.IsInvalid));
        var regexError = DataUi.Bound(assistant, nameof(assistant.RegexError), Ui.Red, 11, wrap: true).Visible(assistant, nameof(assistant.IsInvalid));
        var results = DataUi.Items(assistant.ExampleResults, ExampleRow, spacing: 2);
        var generated = Ui.Column(6,
            DataUi.Field("Generated regex (you can edit it)", DataUi.Bar([], regex,
                DataUi.Command("ADD", assistant.AddCommand, "Add this regex to the regex patterns of the selected document", primary: true),
                DataUi.Action("SAVE…", () => Save(assistant), "Save the regex with the goal and the examples (JSON, regex folder of config.yml)"))),
            regexError,
            DataUi.Caption("Test on the examples"),
            results).Visible(assistant, nameof(assistant.HasRegex));

        return Ui.Column(8,
            DataUi.Field("Example lines (one per line)", examples),
            DataUi.Field("Extraction goal", DataUi.Bar([], DataUi.Line(assistant, nameof(assistant.Goal), "e.g. the order number and the shipping date"), generate, cancel, load)),
            DataUi.Bar([DataUi.Busy(assistant.GenerateCommand), DataUi.Dot(Ui.Red, assistant, nameof(assistant.IsError))], status),
            generated);
    }

    /// <summary>A tested example: ✓ / ✗, the line with the detected parts in yellow, the details.</summary>
    private static Control ExampleRow(RegexExampleResult result)
    {
        var mark = DataUi.Text(result.Mark ?? string.Empty, result.IsMatch ? Valid : DataUi.ErrorRed, 13);
        mark.VerticalAlignment = VerticalAlignment.Top;
        var line = new SelectableTextBlock { FontFamily = Ui.Mono, FontSize = 12, Foreground = Ui.Text, TextWrapping = TextWrapping.Wrap, Inlines = new InlineCollection() };
        foreach (var segment in result.Segments)
            line.Inlines.Add(segment.IsMatch
                ? new Run(segment.Text) { Foreground = MatchText, Background = MatchBack }
                : new Run(segment.Text));
        var detail = Ui.Label(result.Detail ?? string.Empty, Ui.Muted, 11, wrap: true);
        var row = Ui.Dock((mark, Dock.Left), (Ui.Column(0, line, detail), Dock.Left));
        mark.Margin = new Thickness(0, 0, 8, 0);
        return row;
    }

    private static async void Save(RegexAssistantViewModel assistant)
    {
        try
        {
            Directory.CreateDirectory(assistant.Directory);
            var path = await Dialogs.SaveFile("Save regex", assistant.SuggestedFileName, ["*.json ; Regex (JSON)"], assistant.Directory);
            if (path is null)
                return;
            if (Path.GetExtension(path).Length == 0)
                path += ".json";
            await using var stream = File.Create(path);
            await assistant.SaveAsync(stream, Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            assistant.IsError = true;
            assistant.Status = $"Not saved: {ex.Message}";
        }
    }

    private static async void Load(RegexAssistantViewModel assistant)
    {
        try
        {
            Directory.CreateDirectory(assistant.Directory);
            var path = await Dialogs.OpenFile("Load regex", ["*.json ; Regex (JSON)", "* ; All files"], assistant.Directory);
            if (path is null)
                return;
            await using var stream = File.OpenRead(path);
            await assistant.LoadAsync(stream, Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            assistant.IsError = true;
            assistant.Status = $"Not loaded: {ex.Message}";
        }
    }
}
