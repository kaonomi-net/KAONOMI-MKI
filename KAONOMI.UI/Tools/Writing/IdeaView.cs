using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Writing;

/// <summary>
/// /idea: the latest ideas listed when it opens, a search (Enter, F5 or Search; empty: the latest ones), New idea (⌘/Ctrl N,
/// the cursor in its title), the ideas on the left (date, category, badges new / modified, title, description, tags), the
/// selected one editable on the right (title, category with the known ones, description, tags, comment) with its id, date,
/// author, status, Delete (confirmed), → Task (a new task in /project filled from the idea), Discard changes and Save
/// (⌘/Ctrl S).
/// </summary>
[ToolView(typeof(IdeaWindowViewModel), "Ideas", "💡", 1000, 680)]
public sealed class IdeaView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (IdeaWindowViewModel)viewModel;
        vm.ConfirmAsync = WritingUi.Confirm;
        TextBox? titleBox = null;

        void NewIdea()
        {
            Ui.Run(vm.NewCommand);
            WritingUi.Later(() => titleBox?.Focus());
        }

        var search = WritingUi.SearchBar(vm, nameof(vm.Term), vm.SearchCommand, "Search ideas (title, description, category, tags) · empty: latest ideas", out var searchBox,
            Ui.Button("NEW IDEA", NewIdea, "Create a new idea (Ctrl+N / ⌘N)"));

        var left = EntryParts.List(vm.Results, entry => ResultRow(vm, entry), "No ideas");

        var editor = WritingUi.Swap(vm, nameof(vm.SelectedResult), value =>
        {
            if (value is not IdeaEntryViewModel entry)
                return null;
            var built = Editor(vm, entry, shell, out var title);
            titleBox = title;
            return built;
        });

        var page = WritingUi.Page([WritingUi.Toolbar(search)], WritingUi.SplitH(left, editor, 340, minLeft: 260),
            WritingUi.StatusBar(vm, nameof(vm.Status), "IsError", "IsRunning", (System.ComponentModel.INotifyPropertyChanged)vm.SearchCommand));

        WritingUi.Shortcuts(page, key =>
        {
            if (WritingUi.IsCommand(key, Key.S))
                return WritingUi.Run(vm.SelectedResult?.SaveCommand);
            if (WritingUi.IsCommand(key, Key.N))
            {
                NewIdea();
                return true;
            }
            if (key.Key == Key.F5)
                return WritingUi.Run(vm.SearchCommand);
            return false;
        });

        // The latest ideas are listed when the view opens.
        Ui.Run(vm.SearchCommand);
        WritingUi.FocusWhenShown(searchBox);
        return page;
    }

    /// <summary>An idea of the list: its date and category, badges new / modified, title, description and tags.</summary>
    private static Control ResultRow(IdeaWindowViewModel vm, IdeaEntryViewModel entry)
    {
        var tags = WritingUi.Clamp(Ui.Bound(entry, nameof(entry.TagsPreview), Ui.Accent, 11), 1).Visible(entry, nameof(entry.HasTags));
        var content = Ui.Column(2,
            Ui.Row(6, Ui.Bound(entry, nameof(entry.Summary), Ui.Muted, 10),
                WritingUi.Badge("new", Ui.Green, entry, nameof(entry.IsNew)),
                WritingUi.Badge("modified", WritingUi.Amber, entry, nameof(entry.IsDirty))),
            WritingUi.Clamp(Ui.Bound(entry, nameof(entry.DisplayTitle), Ui.TextBright, 13), 1),
            WritingUi.Clamp(Ui.Bound(entry, nameof(entry.IdeaDescription), Ui.Muted, 11), 2),
            tags);
        return WritingUi.ResultRow(vm, nameof(vm.SelectedResult), () => ReferenceEquals(vm.SelectedResult, entry), () => vm.SelectedResult = entry, content);
    }

    /// <summary>The editor of the selected idea: its header (id, date, author, status, Delete, → Task, Discard changes, Save) and fields.</summary>
    private static Control Editor(IdeaWindowViewModel vm, IdeaEntryViewModel entry, Shell shell, out TextBox titleBox)
    {
        var header = WritingUi.Flow(8,
            EntryParts.Identity(entry),
            EntryParts.Status(entry),
            WritingUi.Command("🗑", vm.DeleteCommand, tooltip: "Delete this idea"),
            Ui.Button("→ TASK", () => SendToProject(vm, entry, shell), "Opens /project with a new task filled from this idea (title, description, comment), to check and create"),
            WritingUi.Command("DISCARD CHANGES", entry.RevertCommand),
            WritingUi.Command("SAVE", entry.SaveCommand, primary: true, tooltip: "Save the idea (Ctrl+S / ⌘S)"));

        titleBox = WritingUi.Line(entry, nameof(entry.IdeaTitle), "A short title for the idea");
        titleBox.FontSize = 16;
        var fields = Ui.Column(10,
            WritingUi.Field("Title", titleBox),
            WritingUi.Field("Category", WritingUi.TextWithChoices(entry, nameof(entry.Category), vm.Categories, "e.g. Product, Research, UI")),
            WritingUi.Field("Description", WritingUi.Lines(entry, nameof(entry.IdeaDescription), 220, "Describe the idea…")),
            WritingUi.Field("Tags (one per line: #TOPIC)", WritingUi.Lines(entry, nameof(entry.Tags), 70, MetaTags.TagsHint)),
            WritingUi.Field("Events (one per line: @EVENT)", WritingUi.Lines(entry, nameof(entry.Events), 54, MetaTags.EventsHint)),
            WritingUi.Field("Signals (one per line: !SIGNAL)", WritingUi.Lines(entry, nameof(entry.Signals), 54, MetaTags.SignalsHint)),
            WritingUi.Field("Comment", WritingUi.Lines(entry, nameof(entry.Comment), 60)));
        return EntryParts.Editor(header, fields);
    }

    /// <summary>
    /// Opens /project (or brings back the one already open) with its new-task form filled from the idea, as it is in the
    /// editor (saved or not): nothing is created before CREATE there.
    /// </summary>
    private static void SendToProject(IdeaWindowViewModel vm, IdeaEntryViewModel entry, Shell shell)
    {
        const string key = "/project";
        var open = shell.Deck.Find(key);
        var project = open?.ViewModel as ProjectWindowViewModel ?? vm.CreateProjectWindow();
        project.NewTaskFrom(entry.ToEntry());
        if (open?.ViewModel is ProjectWindowViewModel)
            shell.Deck.BringToFront(open);
        else
            shell.Open(project, key);
        entry.Status = "Sent to /project as a new task";
    }
}
