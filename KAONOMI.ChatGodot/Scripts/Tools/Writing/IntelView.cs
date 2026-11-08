using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Writing;

/// <summary>
/// /intel: the latest intels listed when it opens, a search in all their fields (Enter or Search; and, or, parentheses;
/// empty: the latest ones), New (⌘/Ctrl N), the intels on the left (date, category, period, badges new / modified, title,
/// description, tags), the selected one editable on the right (name, description, category (a drop-down), start and end
/// dates, triggers, events, related and excluded entities, signal, tags, instruction, actions, comment; the application,
/// user and machine are kept but never shown)
/// with its id, date, status, Delete (confirmed; only marked deleted), Clone, Discard changes and Save (⌘/Ctrl S).
/// </summary>
[ToolView(typeof(IntelWindowViewModel), "Intel", "🕵", 1100, 750)]
public sealed class IntelView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (IntelWindowViewModel)viewModel;
        vm.ConfirmAsync = WritingUi.Confirm;
        vm.OpenTextFileAsync = async () => await Dialogs.OpenFile("Import intels", ["*.json ; JSON"]) is { Length: > 0 } path ? await System.IO.File.ReadAllTextAsync(path) : null;
        vm.PickFolderAsync = title => Dialogs.OpenFolder(title);

        var search = Ui.SearchBar(vm, nameof(vm.Term), vm.SearchCommand, "Search the intels in all their fields (and, or, parentheses) · empty: latest intels",
            Ui.Command("NEW", vm.NewCommand, tooltip: "Create a new intel (Ctrl+N / ⌘N)"),
            Ui.Command("IMPORT…", vm.ImportCommand, tooltip: "Import intels from a JSON file (one or an array, as the seed files); same id: overwritten"),
            Ui.Command("IMPORT FOLDER…", vm.ImportFolderCommand, tooltip: "Import a folder exported with Export all (one JSON file each); same id: overwritten"),
            Ui.Command("EXPORT ALL…", vm.ExportAllCommand, tooltip: "Export every intel of the server to a folder: one JSON file each"));
        var searchBox = (LineEdit)search.GetChild(0);

        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        var empty = Ui.Placeholder("No results");
        Bind.Items(list, vm.Results, entry => ResultRow(vm, entry), empty);
        var left = Ui.Column(0, Ui.FillHeight(Ui.Scroll(list)), empty);
        left.CustomMinimumSize = new Vector2(300, 0);

        var editor = Ui.Swap(vm, nameof(vm.SelectedResult), value => value is IntelEntryViewModel entry ? Editor(vm, entry) : null);

        var page = Ui.Column(6, Ui.Toolbar(search), Ui.SplitH(left, editor, 340),
            WritingUi.StatusBar(vm, nameof(vm.Status), (System.ComponentModel.INotifyPropertyChanged)vm.SearchCommand));

        WritingUi.Shortcuts(page, key =>
        {
            if (WritingUi.IsCommand(key, Key.S))
                return WritingUi.Run(vm.SelectedResult?.SaveCommand);
            if (WritingUi.IsCommand(key, Key.N))
                return WritingUi.Run(vm.NewCommand);
            return false;
        });
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(searchBox))
                searchBox.GrabFocus();
        }).CallDeferred();
        return page;
    }

    /// <summary>An intel of the list: its summary, badges new / modified, title, description and tags.</summary>
    private static Control ResultRow(IntelWindowViewModel vm, IntelEntryViewModel entry)
    {
        var description = WritingUi.Clamp(Ui.Label(entry, nameof(entry.Description), Eve.Eve.Muted, 11), 1);
        Bind.Visible(description, entry, nameof(entry.HasDescription));
        var tags = WritingUi.Clamp(Ui.Label(entry, nameof(entry.TagsPreview), Eve.Eve.Cyan with { A = 0.8f }, 11), 1);
        Bind.Visible(tags, entry, nameof(entry.HasTags));
        var content = Ui.Column(2,
            Ui.Row(6, Ui.Label(entry, nameof(entry.Summary), Eve.Eve.Muted, 10),
                Ui.Badge("new", Eve.Eve.Green, entry, nameof(entry.IsNew)),
                Ui.Badge("modified", Eve.Eve.Amber, entry, nameof(entry.IsDirty))),
            WritingUi.Clamp(Ui.Label(entry, nameof(entry.Title), Eve.Eve.TextBright, 13), 2),
            description,
            tags);
        return WritingUi.ResultRow(vm, nameof(vm.SelectedResult), () => ReferenceEquals(vm.SelectedResult, entry), () => vm.SelectedResult = entry, content);
    }

    /// <summary>The editor of the selected intel.</summary>
    private static Control Editor(IntelWindowViewModel vm, IntelEntryViewModel entry)
    {
        var dot = Bind.Visible(Eve.Eve.Text_("●", Eve.Eve.Red, 10), entry, nameof(entry.IsError));
        var status = Ui.Label(entry, nameof(entry.Status), Eve.Eve.Text, 12);
        status.ClipText = true;
        status.CustomMinimumSize = new Vector2(80, 0);
        status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        status.HorizontalAlignment = HorizontalAlignment.Right;
        Bind.When(status, entry, () => status.TooltipText = entry.Status, nameof(entry.Status));
        var idLabel = Ui.Line(entry, nameof(entry.IdLabel));
        idLabel.Editable = false;
        idLabel.Flat = true;
        idLabel.AddThemeFontSizeOverride("font_size", 12);
        var statusRow = Ui.Row(6, dot, status);
        statusRow.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var header = WritingUi.Flow(8, 6,
            Ui.Fill(Ui.Column(0, idLabel, Ui.Label(entry, nameof(entry.DateLabel), Eve.Eve.Muted, 10))),
            statusRow,
            Ui.Command("DELETE", vm.DeleteCommand, tooltip: "Delete this intel (only marked deleted on the server: isDeleted = true)"),
            Ui.Command("CLONE", vm.CloneCommand, tooltip: "Create a copy of this intel (new id on save)"),
            Ui.Command("DISCARD CHANGES", entry.RevertCommand),
            Ui.Command("SAVE", entry.SaveCommand, primary: true, tooltip: "Save (Ctrl+S / ⌘S)"));

        // If: the entities that must (not) be there, the level at which it triggers, fuzzy or not, the instructions.
        var level = Bind.Number(new SpinBox { MinValue = 0, MaxValue = 100, Step = 1, Suffix = "%", CustomMinimumSize = new Vector2(110, 0) }, entry, nameof(entry.TriggerLevel));
        var slider = Bind.Range(new HSlider { MinValue = 0, MaxValue = 100, Step = 1, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter }, entry, nameof(entry.TriggerLevel));
        var condition = Ui.Column(8,
            Eve.Eve.Caption("IF", Eve.Eve.Cyan, 12),
            WritingUi.Cells(2,
                Ui.Field("Included entities (one per line: Entity = weight)", Ui.Lines(entry, nameof(entry.IncludedEntities), 90, "Claude Shannon = 80")),
                Ui.Field("Excluded entities (one per line: Entity = weight)", Ui.Lines(entry, nameof(entry.ExcludedEntities), 90, "Herbert Simon = 20"))),
            Ui.Field("Trigger level", Ui.Row(8, slider, level, Ui.Check("Fuzzy", entry, nameof(entry.IsFuzzy), "The entities may match loosely (a typo, a close form)"))),
            Ui.Field("Instructions", Ui.Lines(entry, nameof(entry.Instructions), 80)));

        // Then: the places, the times, the related entities and the questions.
        var consequence = Ui.Column(8,
            Eve.Eve.Caption("THEN", Eve.Eve.Amber, 12),
            WritingUi.Cells(2,
                Ui.Field("Locations (one per line)", Ui.Lines(entry, nameof(entry.Locations), 70)),
                Ui.Field("Times (one per line)", Ui.Lines(entry, nameof(entry.Times), 70)),
                Ui.Field("Related entities (one per line)", Ui.Lines(entry, nameof(entry.RelatedEntities), 70)),
                Ui.Field("Questions (one per line)", Ui.Lines(entry, nameof(entry.Questions), 70))));

        var metadata = WritingUi.Cells(3,
            Ui.Field("Tags (one per line: #TOPIC)", Ui.Lines(entry, nameof(entry.Tags), 60, MetaTags.TagsHint)),
            Ui.Field("Events (one per line: @EVENT)", Ui.Lines(entry, nameof(entry.Events), 60, MetaTags.EventsHint)),
            Ui.Field("Signals (one per line: !SIGNAL)", Ui.Lines(entry, nameof(entry.Signals), 60, MetaTags.SignalsHint)));

        var fields = Ui.Column(12,
            Ui.Field("Title", Ui.Line(entry, nameof(entry.IntelTitle), "Title of the intel")),
            condition,
            Eve.Eve.Separator(),
            consequence,
            Eve.Eve.Separator(),
            metadata,
            Ui.Field("Comment", Ui.Lines(entry, nameof(entry.Comment), 44)));
        return Ui.Column(8, Ui.Margin(header, 8, 4, 8, 0), Eve.Eve.Separator(), Ui.FillHeight(Ui.Scroll(Ui.Margin(fields, 8, 0, 12, 12))));
    }
}
