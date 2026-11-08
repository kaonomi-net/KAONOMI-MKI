using System;
using System.IO;
using System.Text;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;

namespace KAONOMI.ChatGodot.Tools.Dashboards;

/// <summary>
/// /symbol: a search of the symbolic patterns (Enter or SEARCH), NEW; the documents found on the left (summary,
/// badges deleted / new / modified, symbol, regex preview, event, tags); the selected one on the right: id and date,
/// status, CLONE, DISCARD CHANGES, SAVE; application, user, machine, event, comment, tags, deleted, symbol, the regex
/// patterns (each checked as it is typed, red edges and the error when it does not compile, ✕ removes it, + ADD
/// PATTERN). Below, the regex assistant (folds): example lines, extraction goal, GENERATE REGEX (CANCEL while it runs,
/// with the local Ollama model of the chat), LOAD… a saved regex; the generated regex (editable, red when invalid)
/// with ADD (to the patterns of the document) and SAVE…; the test on each example (✓ / ✗, the detected parts
/// highlighted in yellow, the details).
/// </summary>
[ToolView(typeof(SymbolicPatternWindowViewModel), "Symbolic patterns", "🔣", 1150, 800)]
public sealed class SymbolicPatternView : IToolView
{
    /// <summary>The assistant stays unfolded or folded across the selections.</summary>
    private bool _assistantOpen = true;

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (SymbolicPatternWindowViewModel)viewModel;
        vm.ConfirmAsync = (message, confirm) => Dialogs.Confirm("Symbolic patterns", message, confirm);
        vm.OpenTextFileAsync = async () => await Dialogs.OpenFile("Import symbolic patterns", ["*.json ; JSON"]) is { Length: > 0 } path ? await System.IO.File.ReadAllTextAsync(path) : null;
        vm.PickFolderAsync = title => Dialogs.OpenFolder(title);
        var toolbar = Ui.Toolbar(Ui.SearchBar(vm, nameof(vm.Term), vm.SearchCommand, "Search symbolic patterns…",
            Ui.Command("NEW", vm.NewCommand, tooltip: "Create a new symbolic pattern"),
            Ui.Command("IMPORT…", vm.ImportCommand, tooltip: "Import symbolic patterns from a JSON file (one or an array, as the seed files); same id: overwritten"),
            Ui.Command("IMPORT FOLDER…", vm.ImportFolderCommand, tooltip: "Import a folder exported with Export all (one JSON file each); same id: overwritten"),
            Ui.Command("EXPORT ALL…", vm.ExportAllCommand, tooltip: "Export every symbolic pattern of the server to a folder: one JSON file each")));

        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        var empty = Ui.Placeholder("No results");
        Bind.Items(list, vm.Results, entry => ResultRow(vm, entry), empty);
        var left = Ui.Column(0, Ui.FillHeight(Ui.Scroll(list)), empty);
        left.CustomMinimumSize = new Vector2(320, 0);

        var right = Ui.Swap(vm, nameof(vm.SelectedResult), value => value is SymbolicPatternEntryViewModel entry ? Editor(vm, entry) : Ui.Placeholder("Select a document"));

        var page = Ui.Column(6, toolbar, Ui.SplitH(left, right, 340), DashUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), vm.SearchCommand));
        page.Ready += () =>
        {
            if (FirstLine(toolbar) is { } search)
                search.CallDeferred(Control.MethodName.GrabFocus);
        };
        return page;
    }

    private static LineEdit? FirstLine(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is LineEdit edit)
                return edit;
            if (FirstLine(child) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>A document of the list; a click selects it.</summary>
    private static Control ResultRow(SymbolicPatternWindowViewModel vm, SymbolicPatternEntryViewModel entry)
    {
        var button = new Button { ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = HorizontalAlignment.Left };
        button.AddThemeStyleboxOverride("normal", EveStyle.Box(Eve.Eve.Sunken(0.15f), EveStyle.Edge with { A = 0.15f }, 6));
        var symbol = Ui.Label(entry, nameof(entry.Symbol), EveStyle.TextBright, 13);
        symbol.ClipText = true;
        symbol.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var regex = Ui.Label(entry, nameof(entry.RegexPreview), EveStyle.Muted, 11);
        regex.AddThemeFontOverride("font", EveStyle.Mono);
        regex.AutowrapMode = TextServer.AutowrapMode.Arbitrary;
        regex.MaxLinesVisible = 2;
        regex.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var evt = Bind.Visible(Ui.Label(entry, nameof(entry.Event), EveStyle.Muted, 11), entry, nameof(entry.HasEvent));
        evt.ClipText = true;
        var tags = Bind.Visible(Ui.Label(entry, nameof(entry.TagsPreview), EveStyle.Muted, 11), entry, nameof(entry.HasTags));
        tags.ClipText = true;
        tags.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var content = Ui.Column(2,
            Ui.Row(6, Ui.Label(entry, nameof(entry.Summary), EveStyle.Muted, 10),
                Ui.Badge("deleted", EveStyle.Red, entry, nameof(entry.IsDeleted)),
                Ui.Badge("new", EveStyle.Green, entry, nameof(entry.IsNew)),
                Ui.Badge("modified", EveStyle.Amber, entry, nameof(entry.IsDirty))),
            symbol, regex, evt, tags);
        var margin = Ui.Margin(content, 8, 6, 8, 6);
        Ignore(margin);
        margin.MouseFilter = Control.MouseFilterEnum.Ignore;
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        button.AddChild(margin);
        void Fit() => button.CustomMinimumSize = new Vector2(0, margin.GetCombinedMinimumSize().Y);
        margin.MinimumSizeChanged += Fit;
        button.Ready += Fit;
        button.Pressed += () => vm.SelectedResult = entry;
        Bind.When(button, vm, () => button.SetPressedNoSignal(ReferenceEquals(vm.SelectedResult, entry)), nameof(vm.SelectedResult));
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

    /// <summary>The selected document and the regex assistant.</summary>
    private Control Editor(SymbolicPatternWindowViewModel vm, SymbolicPatternEntryViewModel entry)
    {
        var status = Ui.Label(entry, nameof(entry.Status), EveStyle.Text, 12);
        status.CustomMinimumSize = new Vector2(0, 0);
        status.ClipText = true;
        status.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        status.MouseFilter = Control.MouseFilterEnum.Stop;
        Bind.When(status, entry, () =>
        {
            status.TooltipText = entry.Status;
            status.AddThemeColorOverride("font_color", entry.IsError ? EveStyle.Red : EveStyle.Text);
        }, nameof(entry.Status), nameof(entry.IsError));
        var statusBox = Ui.Row(6, DashUi.Dot(EveStyle.Red, entry, nameof(entry.IsError)), Ui.Fill(status));
        var id = Ui.Label(entry, nameof(entry.IdLabel), EveStyle.Text, 12);
        id.ClipText = true;
        id.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        id.TooltipText = "Right-click to copy the id";
        id.MouseFilter = Control.MouseFilterEnum.Stop;
        id.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } && entry.Id is { } value)
            {
                Dialogs.Copy(value);
                entry.Status = "Id copied to the clipboard";
            }
        };
        var date = Ui.Label(entry, nameof(entry.DateLabel), EveStyle.Muted, 10);
        date.ClipText = true;
        // Id, date and status on the left (trimmed when narrow), the actions on the right.
        var header = Ui.Row(8,
            Ui.Fill(Ui.Column(0, id, date, statusBox)),
            Ui.Command("CLONE", vm.CloneCommand, tooltip: "Create a copy of this document (new id on save)"),
            Ui.Command("DISCARD CHANGES", entry.RevertCommand),
            Ui.Command("SAVE", entry.SaveCommand, primary: true));
        foreach (var child in header.GetChildren())
            if (child is Button button)
                button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;

        var grid = Ui.Grid(3, 12, 6);
        grid.AddChild(Field("Title", Ui.Line(entry, nameof(entry.Title), "Title of the pattern")));
        grid.AddChild(Field("Event", Ui.Line(entry, nameof(entry.Event))));
        grid.AddChild(Field("Language", Ui.Line(entry, nameof(entry.Language), "FR, EN, DE…")));

        // The regex patterns.
        var patterns = Ui.Column(6);
        Bind.Items(patterns, entry.RegexPatterns, item => PatternRow(entry, item));

        var fields = Ui.Column(8,
            grid,
            Ui.Field("Comment", Ui.Lines(entry, nameof(entry.Comment), 50)),
            Ui.Field("Tags (one per line)", Ui.Lines(entry, nameof(entry.Tags), 60, MetaTags.TagsHint)),
            Ui.Field("Events (one per line)", Ui.Lines(entry, nameof(entry.Events), 50, MetaTags.EventsHint)),
            Ui.Field("Signals (one per line)", Ui.Lines(entry, nameof(entry.Signals), 50, MetaTags.SignalsHint)),
            Ui.Check("Deleted", entry, nameof(entry.IsDeleted)),
            Ui.Field("Symbol", Ui.Line(entry, nameof(entry.Symbol))),
            Ui.Row(8, Ui.Fill(EveStyle.Caption("Regex patterns")), Ui.Command("+ ADD PATTERN", entry.AddPatternCommand, tooltip: "Add a regular expression")),
            patterns,
            DashUi.Expander("Regex assistant", Assistant(vm.RegexAssistant), _assistantOpen, open => _assistantOpen = open));
        return Ui.Column(8, header, EveStyle.Separator(), Ui.FillHeight(Ui.Scroll(Ui.Margin(fields, 0, 0, 12, 12))));
    }

    private static VBoxContainer Field(string caption, Control field) => Ui.Fill(Ui.Field(caption, field));

    /// <summary>A regular expression: its text (mono, red edges when it does not compile), ✕, the error.</summary>
    private static Control PatternRow(SymbolicPatternEntryViewModel entry, RegexPatternItemViewModel item)
    {
        var edit = DashUi.InvalidEdge(DashUi.MonoLine(item, nameof(item.Pattern), @"Regular expression, e.g. \bhello\b"), item, nameof(item.IsInvalid));
        var remove = Ui.Command("✕", entry.RemovePatternCommand, tooltip: "Remove this regular expression", parameter: () => item);
        var error = Ui.Label(item, nameof(item.Error), EveStyle.Red, 11, wrap: true);
        Bind.Visible(error, item, nameof(item.IsInvalid));
        return Ui.Column(2, Ui.Row(6, edit, remove), error);
    }

    /// <summary>The regex assistant: examples + goal → a regex from the local model → tested, added, saved.</summary>
    private static Control Assistant(RegexAssistantViewModel assistant)
    {
        var examples = Ui.Lines(assistant, nameof(assistant.Examples), 90, "Order 12345 shipped on 2026-10-08\nOrder 987 shipped on 2026-09-30", mono: true);
        examples.WrapMode = TextEdit.LineWrappingMode.None;
        var generate = Ui.Command("GENERATE REGEX", assistant.GenerateCommand, primary: true, tooltip: "Ask the local Ollama model selected in the chat window for a regex");
        generate.CustomMinimumSize = new Vector2(140, 0);
        Bind.Visible(generate, assistant.GenerateCommand, "IsRunning", invert: true);
        var cancel = Ui.Command("CANCEL", assistant.GenerateCancelCommand);
        cancel.CustomMinimumSize = new Vector2(140, 0);
        Bind.Visible(cancel, assistant.GenerateCommand, "IsRunning");
        var load = Ui.Action("LOAD…", () => Load(assistant), tooltip: "Load a saved regex with its goal and examples (regex folder of config.yml)");

        var status = Ui.Label(assistant, nameof(assistant.Status), EveStyle.Text, 12, wrap: true);
        status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        // The generated regex and its test.
        var regex = DashUi.InvalidEdge(DashUi.MonoLine(assistant, nameof(assistant.GeneratedRegex)), assistant, nameof(assistant.IsInvalid));
        var regexError = Ui.Label(assistant, nameof(assistant.RegexError), EveStyle.Red, 11, wrap: true);
        Bind.Visible(regexError, assistant, nameof(assistant.IsInvalid));
        var results = Ui.Column(2);
        Bind.Items(results, assistant.ExampleResults, ExampleRow);
        var generated = Ui.Column(6,
            Ui.Field("Generated regex (you can edit it)", Ui.Row(6, regex,
                Ui.Command("ADD", assistant.AddCommand, primary: true, tooltip: "Add this regex to the regex patterns of the selected document"),
                Ui.Action("SAVE…", () => Save(assistant), tooltip: "Save the regex with the goal and the examples (JSON, regex folder of config.yml)"))),
            regexError,
            EveStyle.Caption("Test on the examples"),
            results);
        Bind.Visible(generated, assistant, nameof(assistant.HasRegex));

        return Ui.Column(8,
            Ui.Field("Example lines (one per line)", examples),
            Ui.Field("Extraction goal", Ui.Row(6, Ui.Line(assistant, nameof(assistant.Goal), "e.g. the order number and the shipping date"), generate, cancel, load)),
            Ui.Row(6, DashUi.Busy(assistant.GenerateCommand), DashUi.Dot(EveStyle.Red, assistant, nameof(assistant.IsError)), status),
            generated);
    }

    /// <summary>A tested example: ✓ / ✗, the line with the detected parts in yellow, the details.</summary>
    private static Control ExampleRow(RegexExampleResult result)
    {
        var mark = EveStyle.Text_(result.Mark, result.IsMatch ? Color.FromHtml("#16C60C") : DashUi.ErrorRed, 13);
        mark.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        var text = new StringBuilder();
        foreach (var segment in result.Segments)
        {
            var piece = segment.Text.Replace("[", "[lb]");
            text.Append(segment.IsMatch ? $"[bgcolor=#FFD70055][color=#FFE066]{piece}[/color][/bgcolor]" : piece);
        }
        var line = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, SelectionEnabled = true, AutowrapMode = TextServer.AutowrapMode.Arbitrary, Text = text.ToString(), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        line.AddThemeFontOverride("normal_font", EveStyle.Mono);
        line.AddThemeFontSizeOverride("normal_font_size", 12);
        var detail = EveStyle.Text_(result.Detail, EveStyle.Muted, 11, wrap: true);
        return Ui.Row(8, mark, Ui.Fill(Ui.Column(0, line, detail)));
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
