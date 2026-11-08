using Avalonia.Controls;
using Avalonia.Input;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Writing;

/// <summary>
/// /intel: the latest intels listed when it opens, a search in all their fields (Enter or Search; and, or, parentheses;
/// empty: the latest ones), New (⌘/Ctrl N), the intels on the left (date, category, period, badges new / modified, title,
/// description, tags), the selected one editable on the right (name, description, category (a drop-down), start and end
/// dates, triggers, events, related and excluded entities, signal, tags, instruction, actions, comment; the application,
/// user and machine are kept but never shown) with its id, date, status, Delete (confirmed; only marked deleted), Clone,
/// Discard changes and Save (⌘/Ctrl S).
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

        var search = WritingUi.SearchBar(vm, nameof(vm.Term), vm.SearchCommand, "Search the intels in all their fields (and, or, parentheses) · empty: latest intels", out var searchBox,
            WritingUi.Command("NEW", vm.NewCommand, tooltip: "Create a new intel (Ctrl+N / ⌘N)"),
            WritingUi.Command("IMPORT…", vm.ImportCommand, tooltip: "Import intels from a JSON file (one or an array, as the seed files); same id: overwritten"),
            WritingUi.Command("IMPORT FOLDER…", vm.ImportFolderCommand, tooltip: "Import a folder exported with Export all (one JSON file each); same id: overwritten"),
            WritingUi.Command("EXPORT ALL…", vm.ExportAllCommand, tooltip: "Export every intel of the server to a folder: one JSON file each"));

        var left = EntryParts.List(vm.Results, entry => ResultRow(vm, entry), "No results");
        var editor = WritingUi.Swap(vm, nameof(vm.SelectedResult), value => value is IntelEntryViewModel entry ? Editor(vm, entry) : null);

        var page = WritingUi.Page([WritingUi.Toolbar(search)], WritingUi.SplitH(left, editor, 340, minLeft: 260),
            WritingUi.StatusBar(vm, nameof(vm.Status), "IsError", "IsRunning", (System.ComponentModel.INotifyPropertyChanged)vm.SearchCommand));

        WritingUi.Shortcuts(page, key =>
        {
            if (WritingUi.IsCommand(key, Key.S))
                return WritingUi.Run(vm.SelectedResult?.SaveCommand);
            if (WritingUi.IsCommand(key, Key.N))
                return WritingUi.Run(vm.NewCommand);
            return false;
        });
        WritingUi.FocusWhenShown(searchBox);
        return page;
    }

    /// <summary>An intel of the list: its summary, badges new / modified, title, description and tags.</summary>
    private static Control ResultRow(IntelWindowViewModel vm, IntelEntryViewModel entry)
    {
        var description = WritingUi.Clamp(Ui.Bound(entry, nameof(entry.Description), Ui.Muted, 11), 1).Visible(entry, nameof(entry.HasDescription));
        var tags = WritingUi.Clamp(Ui.Bound(entry, nameof(entry.TagsPreview), Ui.Accent, 11), 1).Visible(entry, nameof(entry.HasTags));
        var content = Ui.Column(2,
            Ui.Row(6, Ui.Bound(entry, nameof(entry.Summary), Ui.Muted, 10),
                WritingUi.Badge("new", Ui.Green, entry, nameof(entry.IsNew)),
                WritingUi.Badge("modified", WritingUi.Amber, entry, nameof(entry.IsDirty))),
            WritingUi.Clamp(Ui.Bound(entry, nameof(entry.Title), Ui.TextBright, 13), 2),
            description,
            tags);
        return WritingUi.ResultRow(vm, nameof(vm.SelectedResult), () => ReferenceEquals(vm.SelectedResult, entry), () => vm.SelectedResult = entry, content);
    }

    /// <summary>The editor of the selected intel.</summary>
    private static Control Editor(IntelWindowViewModel vm, IntelEntryViewModel entry)
    {
        var header = WritingUi.Flow(8,
            EntryParts.Identity(entry),
            EntryParts.Status(entry),
            WritingUi.Command("DELETE", vm.DeleteCommand, tooltip: "Delete this intel (only marked deleted on the server: isDeleted = true)"),
            WritingUi.Command("CLONE", vm.CloneCommand, tooltip: "Create a copy of this intel (new id on save)"),
            WritingUi.Command("DISCARD CHANGES", entry.RevertCommand),
            WritingUi.Command("SAVE", entry.SaveCommand, primary: true, tooltip: "Save (Ctrl+S / ⌘S)"));

        // If: the entities that must (not) be there, the level at which it triggers, fuzzy or not, the instructions.
        var level = Ui.Number(entry, nameof(entry.TriggerLevel), 0, 100);
        level.Width = 120;
        var condition = Ui.Column(8,
            new TextBlock { Text = "IF", FontWeight = Avalonia.Media.FontWeight.Bold, FontSize = 12, Foreground = Ui.Accent },
            WritingUi.Cells(2,
                WritingUi.Field("Included entities (one per line: Entity = weight)", WritingUi.Lines(entry, nameof(entry.IncludedEntities), 90, "Claude Shannon = 80")),
                WritingUi.Field("Excluded entities (one per line: Entity = weight)", WritingUi.Lines(entry, nameof(entry.ExcludedEntities), 90, "Herbert Simon = 20"))),
            WritingUi.Field("Trigger level (0 to 100 %)", Ui.Row(12, level, Ui.Check("Fuzzy", entry, nameof(entry.IsFuzzy)))),
            WritingUi.Field("Instructions", WritingUi.Lines(entry, nameof(entry.Instructions), 80)));

        // Then: the places, the times, the related entities and the questions.
        var consequence = Ui.Column(8,
            new TextBlock { Text = "THEN", FontWeight = Avalonia.Media.FontWeight.Bold, FontSize = 12, Foreground = Ui.Accent },
            WritingUi.Cells(2,
                WritingUi.Field("Locations (one per line)", WritingUi.Lines(entry, nameof(entry.Locations), 70)),
                WritingUi.Field("Times (one per line)", WritingUi.Lines(entry, nameof(entry.Times), 70)),
                WritingUi.Field("Related entities (one per line)", WritingUi.Lines(entry, nameof(entry.RelatedEntities), 70)),
                WritingUi.Field("Questions (one per line)", WritingUi.Lines(entry, nameof(entry.Questions), 70))));

        var metadata = WritingUi.Cells(3,
            WritingUi.Field("Tags (one per line: #TOPIC)", WritingUi.Lines(entry, nameof(entry.Tags), 60, MetaTags.TagsHint)),
            WritingUi.Field("Events (one per line: @EVENT)", WritingUi.Lines(entry, nameof(entry.Events), 60, MetaTags.EventsHint)),
            WritingUi.Field("Signals (one per line: !SIGNAL)", WritingUi.Lines(entry, nameof(entry.Signals), 60, MetaTags.SignalsHint)));

        var fields = Ui.Column(12,
            WritingUi.Field("Title", WritingUi.Line(entry, nameof(entry.IntelTitle), "Title of the intel")),
            condition,
            consequence,
            metadata,
            WritingUi.Field("Comment", WritingUi.Lines(entry, nameof(entry.Comment), 44)));
        return EntryParts.Editor(header, fields);
    }
}
