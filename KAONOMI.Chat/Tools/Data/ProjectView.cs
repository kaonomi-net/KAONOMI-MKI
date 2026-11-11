using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// /project: the projects of the server (Project: a name, a description, goals, tags), as in the Godot chat. A search
/// (Enter or Search; empty: the newest ones) and NEW at the top; the projects on the left (name, badges new /
/// modified, date, user and number of goals, start of the description); the one selected editable on the right: name,
/// start date (today by default) and end date (may be left empty),
/// description, the goals (one box each, + ADD GOAL, ✕ removes), tags (left empty, the server finds the declared ones
/// on save), comment; with DELETE (confirmed; marked deleted on the server, never erased), DISCARD CHANGES and SAVE.
/// The kanban of the tasks is /kanban.
/// </summary>
[ToolView(typeof(ProjectWindowViewModel), "Projects", "🎯", 1100, 720)]
public sealed class ProjectView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (ProjectWindowViewModel)viewModel;
        vm.ConfirmAsync = (title, text, ok) => Dialogs.Confirm(title, text, ok);
        var newButton = DataUi.Command("NEW", vm.NewCommand, "Start a new project");
        var toolbar = DataUi.Toolbar(DataUi.SearchBar(vm, nameof(vm.Term), vm.SearchCommand, "Search the projects: name, description, goals, tags… (empty: the newest)", newButton));

        // The projects found.
        var left = DataUi.ScrollList(vm.Results, entry => ResultRow(vm, entry), "No project", spacing: 2);
        left.Margin = new Thickness(8, 8, 0, 8);

        // The project selected.
        var editor = DataUi.Swap(vm, nameof(vm.SelectedResult), value => value is ProjectEntryViewModel entry ? Editor(vm, entry) : DataUi.Placeholder("Select a project, or NEW"));
        editor.Margin = new Thickness(4, 8, 8, 8);

        return Ui.Dock(
            (toolbar, Dock.Top),
            (DataUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError)), Dock.Bottom),
            (DataUi.SplitH(left, editor, 340, 260), Dock.Top));
    }

    /// <summary>A project of the list: its name, badges, summary, start of its description; a click selects it.</summary>
    private static Control ResultRow(ProjectWindowViewModel vm, ProjectEntryViewModel entry)
    {
        var content = Ui.Column(2,
            Ui.Row(6, DataUi.Bound(entry, nameof(entry.Headline), Ui.TextBright, 14, format: v => DataUi.Trim(v?.ToString(), 60)),
                DataUi.Badge("new", Ui.Green, entry, nameof(entry.IsNew)),
                DataUi.Badge("modified", DataUi.Amber, entry, nameof(entry.IsDirty))),
            DataUi.Bound(entry, nameof(entry.Summary), Ui.Muted, 10),
            DataUi.Bound(entry, nameof(entry.DescriptionPreview), Ui.Muted, 11, format: v => DataUi.Trim(v?.ToString(), 70)).Visible(entry, nameof(entry.HasDescription)));
        return DataUi.ClickRow(content, () => vm.SelectedResult = entry, null, vm, nameof(vm.SelectedResult), () => ReferenceEquals(vm.SelectedResult, entry));
    }

    /// <summary>The editor of the project selected: its fields, Delete, Discard changes, Save.</summary>
    private static Control Editor(ProjectWindowViewModel vm, ProjectEntryViewModel entry)
    {
        var header = DataUi.Bar([],
            Ui.Column(0, DataUi.Bound(entry, nameof(entry.IdLabel), Ui.Text, 12), DataUi.Bound(entry, nameof(entry.DateLabel), Ui.Muted, 10),
                DataUi.Bound(entry, nameof(entry.Status), Ui.Muted, 11)),
            DataUi.Command("DELETE", vm.DeleteCommand, "Delete this project after a confirmation: it is marked deleted on the server (never erased) and leaves the lists"),
            DataUi.Command("DISCARD CHANGES", entry.RevertCommand, "Give the changes up: the project as it was saved last"),
            DataUi.Command("SAVE", entry.SaveCommand, "Save the project on the server", primary: true));

        // The goals, one box each.
        var goals = DataUi.Items(entry.Goals, goal => GoalRow(entry, goal), spacing: 6);

        var meta = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };
        var tags = DataUi.Field("Tags (one per line; left empty, the declared tags found in the texts are set on save)", DataUi.Lines(entry, nameof(entry.Tags), 70, MetaTags.TagsHint));
        var comment = DataUi.Field("Comment", DataUi.Lines(entry, nameof(entry.Comment), 70));
        Grid.SetColumn(comment, 2);
        meta.Children.Add(tags);
        meta.Children.Add(comment);

        var fields = Ui.Column(8,
            DataUi.Field("Name", DataUi.Line(entry, nameof(entry.Name), "The name of the project")),
            Ui.Row(12,
                DataUi.Field("Start date (yyyy-MM-dd; today by default)", DataUi.Line(entry, nameof(entry.StartDate), "yyyy-MM-dd")),
                DataUi.Field("End date (yyyy-MM-dd; empty: no end)", DataUi.Line(entry, nameof(entry.EndDate), "yyyy-MM-dd, or empty"))),
            DataUi.Field("Description", DataUi.Lines(entry, nameof(entry.Description), 130, "What the project is about")),
            DataUi.Separator(),
            DataUi.Bar([DataUi.Caption("Goals")], DataUi.Bound(entry, nameof(entry.GoalsLabel), Ui.Muted, 11),
                DataUi.Command("+ ADD GOAL", entry.AddGoalCommand, "Add another goal")),
            goals,
            DataUi.Separator(),
            meta);
        fields.Margin = new Thickness(0, 0, 12, 12);
        var separator = DataUi.Separator();
        separator.Margin = new Thickness(0, 8);
        return Ui.Dock((header, Dock.Top), (separator, Dock.Top), (Ui.Scroll(fields), Dock.Top));
    }

    /// <summary>A goal: its text and ✕.</summary>
    private static Control GoalRow(ProjectEntryViewModel entry, ProjectGoalViewModel goal)
    {
        var button = DataUi.Command("✕", entry.RemoveGoalCommand, "Remove this goal", parameter: goal);
        button.VerticalAlignment = VerticalAlignment.Top;
        return DataUi.Bar([], DataUi.Lines(goal, nameof(goal.Text), 48, "A goal of the project"), button);
    }
}
