using Godot;
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

        var search = Ui.SearchBar(vm, nameof(vm.Term), vm.SearchCommand, "Search the intels in all their fields (and, or, parentheses) · empty: latest intels",
            Ui.Command("NEW", vm.NewCommand, tooltip: "Create a new intel (Ctrl+N / ⌘N)"));
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

        var category = Bind.Options(new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill },
            IntelEntryViewModel.CategoryChoices, entry, nameof(entry.Category), c => c.Length == 0 ? "(none)" : c);

        var dates = WritingUi.Cells(3,
            Ui.Field("Category", category),
            Ui.Field("Start date", WritingUi.DateField(entry, nameof(entry.StartDate))),
            Ui.Field("End date", WritingUi.DateField(entry, nameof(entry.EndDate))));

        var lists = WritingUi.Cells(2,
            Ui.Field("Triggers (one per line)", Ui.Lines(entry, nameof(entry.Triggers), 70)),
            Ui.Field("Events (one per line)", Ui.Lines(entry, nameof(entry.Events), 70)),
            Ui.Field("Related entities (one per line)", Ui.Lines(entry, nameof(entry.RelatedEntities), 70)),
            Ui.Field("Excluded entities (one per line)", Ui.Lines(entry, nameof(entry.ExcludedEntities), 70)),
            Ui.Field("Tags (one per line: #TOPIC !ALERT ?QUESTION &LINK)", Ui.Lines(entry, nameof(entry.Tags), 70)),
            Ui.Field("Actions (one per line)", Ui.Lines(entry, nameof(entry.Actions), 70)));

        var fields = Ui.Column(10,
            Ui.Field("Name", Ui.Line(entry, nameof(entry.Name))),
            Ui.Field("Description", Ui.Lines(entry, nameof(entry.Description), 100)),
            dates,
            lists,
            Ui.Field("Signal", Ui.Lines(entry, nameof(entry.Signal), 44)),
            Ui.Field("Instruction", Ui.Lines(entry, nameof(entry.Instruction), 60)),
            Ui.Field("Comment", Ui.Lines(entry, nameof(entry.Comment), 44)));
        return Ui.Column(8, Ui.Margin(header, 8, 4, 8, 0), Eve.Eve.Separator(), Ui.FillHeight(Ui.Scroll(Ui.Margin(fields, 8, 0, 12, 12))));
    }
}
