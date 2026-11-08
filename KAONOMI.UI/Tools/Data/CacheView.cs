using Avalonia;
using Avalonia.Controls;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// /cache: a search on the cache of the completions (Enter or Search), the documents found on the left (date, model,
/// application, badges deleted / new / modified, prompt, comment), the selected one editable on the right (model,
/// comment, related and excluded entities, deleted, prompt, completion with a Markdown preview), with Clone, Discard
/// changes and Save; New starts a new document.
/// </summary>
[ToolView(typeof(CacheWindowViewModel), "Cache", "🗃", 1100, 720)]
public sealed class CacheView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (CacheWindowViewModel)viewModel;
        var newButton = DataUi.Command("NEW", vm.NewCommand, "Create a new cache document");
        var toolbar = DataUi.Toolbar(DataUi.SearchBar(vm, nameof(vm.Term), vm.SearchCommand, "Search the cache…", newButton));

        // The documents found.
        var left = DataUi.ScrollList(vm.Results, entry => ResultRow(vm, entry), "No results", spacing: 2);
        left.Margin = new Thickness(8, 8, 0, 8);

        // The selected document.
        var editor = DataUi.Swap(vm, nameof(vm.SelectedResult), value => value is CacheEntryViewModel entry ? Editor(vm, entry) : DataUi.Placeholder("Select a document"));
        editor.Margin = new Thickness(4, 8, 8, 8);

        return Ui.Dock(
            (toolbar, Dock.Top),
            (DataUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError)), Dock.Bottom),
            (DataUi.SplitH(left, editor, 340, 260), Dock.Top));
    }

    /// <summary>A document of the list: its summary, badges, prompt and comment; a click selects it.</summary>
    private static Control ResultRow(CacheWindowViewModel vm, CacheEntryViewModel entry)
    {
        var content = Ui.Column(2,
            Ui.Row(6, DataUi.Bound(entry, nameof(entry.Summary), Ui.Muted, 10),
                DataUi.Badge("deleted", Ui.Red, entry, nameof(entry.IsDeleted)),
                DataUi.Badge("new", Ui.Green, entry, nameof(entry.IsNew)),
                DataUi.Badge("modified", DataUi.Amber, entry, nameof(entry.IsDirty))),
            DataUi.Bound(entry, nameof(entry.Prompt), Ui.TextBright, 13, format: v => DataUi.Trim(v?.ToString(), 120)),
            DataUi.Bound(entry, nameof(entry.Comment), Ui.Muted, 11, format: v => DataUi.Trim(v?.ToString(), 80)).Visible(entry, nameof(entry.HasComment)));
        return DataUi.ClickRow(content, () => vm.SelectedResult = entry, null, vm, nameof(vm.SelectedResult), () => ReferenceEquals(vm.SelectedResult, entry));
    }

    /// <summary>The editor of the selected document: its fields, Clone, Discard changes, Save.</summary>
    private static Control Editor(CacheWindowViewModel vm, CacheEntryViewModel entry)
    {
        var header = DataUi.Bar([],
            Ui.Column(0, DataUi.Bound(entry, nameof(entry.IdLabel), Ui.Text, 12), DataUi.Bound(entry, nameof(entry.DateLabel), Ui.Muted, 10)),
            DataUi.Bound(entry, nameof(entry.Status), Ui.Muted, 11),
            DataUi.Command("CLONE", vm.CloneCommand, "Create a copy of this document (new id on save)"),
            DataUi.Command("DISCARD CHANGES", entry.RevertCommand),
            DataUi.Command("SAVE", entry.SaveCommand, primary: true));

        var entities = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };
        var related = DataUi.Field("Related entities (one per line)", DataUi.Lines(entry, nameof(entry.RelatedEntities), 60));
        var excluded = DataUi.Field("Excluded entities (one per line)", DataUi.Lines(entry, nameof(entry.ExcludedEntities), 60));
        Grid.SetColumn(excluded, 2);
        entities.Children.Add(related);
        entities.Children.Add(excluded);

        // The completion: as text, or its Markdown preview.
        var completion = DataUi.Lines(entry, nameof(entry.Completion), 180);
        var preview = DataUi.Markdown(entry, nameof(entry.Completion));
        preview.IsVisible = false;
        var toggle = new CheckBox { Content = "Markdown preview" };
        toggle.IsCheckedChanged += (_, _) =>
        {
            preview.IsVisible = toggle.IsChecked == true;
            completion.IsVisible = toggle.IsChecked != true;
        };

        var fields = Ui.Column(8,
            DataUi.Field("Model", DataUi.Line(entry, nameof(entry.Model))),
            DataUi.Field("Comment", DataUi.Lines(entry, nameof(entry.Comment), 50)),
            entities,
            DataUi.Check("Deleted", entry, nameof(entry.IsDeleted)),
            DataUi.Field("Prompt", DataUi.Lines(entry, nameof(entry.Prompt), 90)),
            DataUi.Bar([], DataUi.Caption("Completion"), toggle),
            completion,
            preview);
        fields.Margin = new Thickness(0, 0, 10, 10);
        var separator = DataUi.Separator();
        separator.Margin = new Thickness(0, 8);
        return Ui.Dock((header, Dock.Top), (separator, Dock.Top), (Ui.Scroll(fields), Dock.Top));
    }
}
