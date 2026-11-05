using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Writing;

/// <summary>
/// /idea: the latest ideas listed when it opens, a search (Enter, F5 or Search; empty: the latest ones), New idea
/// (⌘/Ctrl N, the cursor in its title), the ideas on the left (date, category, badges new / modified, title, description,
/// tags), the selected one editable on the right (title, category with the known ones, description, tags, comment) with
/// its id, date, author, status, Delete (confirmed), → Task (a new task in /project filled from the idea), Discard
/// changes and Save (⌘/Ctrl S).
/// </summary>
[ToolView(typeof(IdeaWindowViewModel), "Ideas", "💡", 1000, 680)]
public sealed class IdeaView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (IdeaWindowViewModel)viewModel;
        vm.ConfirmAsync = WritingUi.Confirm;
        LineEdit? titleBox = null;

        void NewIdea()
        {
            Bind.Run(vm.NewCommand);
            Callable.From(() =>
            {
                if (titleBox is not null && GodotObject.IsInstanceValid(titleBox))
                    titleBox.GrabFocus();
            }).CallDeferred();
        }

        var search = Ui.SearchBar(vm, nameof(vm.Term), vm.SearchCommand, "Search ideas (title, description, category, tags) · empty: latest ideas",
            Ui.Action("NEW IDEA", NewIdea, tooltip: "Create a new idea (Ctrl+N / ⌘N)"));
        var searchBox = (LineEdit)search.GetChild(0);

        // The ideas.
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        var empty = Ui.Placeholder("No ideas");
        Bind.Items(list, vm.Results, entry => ResultRow(vm, entry), empty);
        var left = Ui.Column(0, Ui.FillHeight(Ui.Scroll(list)), empty);
        left.CustomMinimumSize = new Vector2(300, 0);

        // The selected idea.
        var editor = Ui.Swap(vm, nameof(vm.SelectedResult), value =>
        {
            if (value is not IdeaEntryViewModel entry)
                return null;
            var built = Editor(vm, entry, shell, out var title);
            titleBox = title;
            return built;
        });

        var page = Ui.Column(6, Ui.Toolbar(search), Ui.SplitH(left, editor, 340),
            WritingUi.StatusBar(vm, nameof(vm.Status), (System.ComponentModel.INotifyPropertyChanged)vm.SearchCommand));

        WritingUi.Shortcuts(page, key =>
        {
            if (WritingUi.IsCommand(key, Key.S))
                return WritingUi.Run(vm.SelectedResult?.SaveCommand);
            if (WritingUi.IsCommand(key, Key.N))
            {
                NewIdea();
                return true;
            }
            if (key.Keycode == Key.F5)
                return WritingUi.Run(vm.SearchCommand);
            return false;
        });

        // The latest ideas are listed when the view opens.
        Bind.Run(vm.SearchCommand);
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(searchBox))
                searchBox.GrabFocus();
        }).CallDeferred();
        return page;
    }

    /// <summary>An idea of the list: its date and category, badges new / modified, title, description and tags.</summary>
    private static Control ResultRow(IdeaWindowViewModel vm, IdeaEntryViewModel entry)
    {
        var tags = WritingUi.Clamp(Ui.Label(entry, nameof(entry.TagsPreview), Eve.Eve.Cyan with { A = 0.8f }, 11), 1);
        Bind.Visible(tags, entry, nameof(entry.HasTags));
        var content = Ui.Column(2,
            Ui.Row(6, Ui.Label(entry, nameof(entry.Summary), Eve.Eve.Muted, 10),
                Ui.Badge("new", Eve.Eve.Green, entry, nameof(entry.IsNew)),
                Ui.Badge("modified", Eve.Eve.Amber, entry, nameof(entry.IsDirty))),
            WritingUi.Clamp(Ui.Label(entry, nameof(entry.DisplayTitle), Eve.Eve.TextBright, 13), 1),
            WritingUi.Clamp(Ui.Label(entry, nameof(entry.IdeaDescription), Eve.Eve.Muted, 11), 2),
            tags);
        return WritingUi.ResultRow(vm, nameof(vm.SelectedResult), () => ReferenceEquals(vm.SelectedResult, entry), () => vm.SelectedResult = entry, content);
    }

    /// <summary>The editor of the selected idea: its header (id, date, author, status, Delete, Discard changes, Save) and fields.</summary>
    private static Control Editor(IdeaWindowViewModel vm, IdeaEntryViewModel entry, Shell shell, out LineEdit titleBox)
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
        var header = WritingUi.Flow(8, 6,
            Ui.Fill(Ui.Column(0, idLabel, Ui.Label(entry, nameof(entry.DateLabel), Eve.Eve.Muted, 10))),
            Ui.Row(6, dot, status),
            Ui.Command("🗑", vm.DeleteCommand, tooltip: "Delete this idea"),
            Ui.Action("→ TASK", () => SendToProject(vm, entry, shell), tooltip: "Opens /project with a new task filled from this idea (title, description, comment), to check and create"),
            Ui.Command("DISCARD CHANGES", entry.RevertCommand),
            Ui.Command("SAVE", entry.SaveCommand, primary: true, tooltip: "Save the idea (Ctrl+S / ⌘S)"));
        header.GetChild<HBoxContainer>(1).SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        titleBox = Ui.Line(entry, nameof(entry.IdeaTitle), "A short title for the idea");
        titleBox.AddThemeFontSizeOverride("font_size", 16);
        var fields = Ui.Column(10,
            Ui.Field("Title", titleBox),
            Ui.Field("Category", WritingUi.TextWithChoices(entry, nameof(entry.Category), vm.Categories, "e.g. Product, Research, UI")),
            Ui.Field("Description", Ui.Lines(entry, nameof(entry.IdeaDescription), 220, "Describe the idea…")),
            Ui.Field("Tags (one per line)", Ui.Lines(entry, nameof(entry.Tags), 70)),
            Ui.Field("Comment", Ui.Lines(entry, nameof(entry.Comment), 60)));
        return Ui.Column(8, Ui.Margin(header, 8, 4, 8, 0), Eve.Eve.Separator(), Ui.FillHeight(Ui.Scroll(Ui.Margin(fields, 8, 0, 12, 12))));
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
