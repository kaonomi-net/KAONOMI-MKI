using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Data;

/// <summary>
/// /project: the projects of the server (Project: a name, a description, goals, tags). A search (Enter or Search;
/// empty: the newest ones) and NEW at the top; the projects on the left (name, badges new / modified, date, user and
/// number of goals, start of the description); the one selected editable on the right: name, start date (today by
/// default) and end date (may be left empty), description, the goals
/// (one box each, + ADD GOAL, ✕ removes), tags (left empty, the server finds the declared ones on save), comment;
/// with DELETE (confirmed; marked deleted on the server, never erased), DISCARD CHANGES and SAVE. The kanban of the
/// tasks is /kanban.
/// </summary>
[ToolView(typeof(ProjectWindowViewModel), "Projects", "🎯", 1100, 720)]
public sealed class ProjectView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (ProjectWindowViewModel)viewModel;
        vm.ConfirmAsync = (title, text, ok) => Dialogs.Confirm(title, text, ok);
        var newButton = Ui.Command("NEW", vm.NewCommand, tooltip: "Start a new project");
        var toolbar = Ui.Toolbar(Ui.SearchBar(vm, nameof(vm.Term), vm.SearchCommand, "Search the projects: name, description, goals, tags… (empty: the newest)", newButton));

        // The projects found.
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        var empty = Ui.Placeholder("No project");
        Bind.Items(list, vm.Results, entry => ResultRow(vm, entry), empty);
        var left = Ui.Column(0, Ui.FillHeight(Ui.Scroll(list)), empty);
        left.CustomMinimumSize = new Vector2(300, 0);

        // The project selected.
        var editor = Ui.Swap(vm, nameof(vm.SelectedResult), value => value is ProjectEntryViewModel entry ? Editor(vm, entry) : Ui.Placeholder("Select a project, or NEW"));

        return Ui.Column(6, toolbar, Ui.SplitH(left, editor, 340), Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError)));
    }

    /// <summary>A project of the list: its name, badges, summary, start of its description; a click selects it.</summary>
    private static Control ResultRow(ProjectWindowViewModel vm, ProjectEntryViewModel entry)
    {
        var button = new Button { ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 64), Alignment = HorizontalAlignment.Left };
        button.AddThemeStyleboxOverride("normal", Eve.Eve.Box(Eve.Eve.Sunken(0.15f), Eve.Eve.Edge with { A = 0.15f }, 6));
        var name = Ui.Label(entry, nameof(entry.Headline), Eve.Eve.TextBright, 14, format: v => Trim(v?.ToString(), 60));
        var content = Ui.Column(2,
            Ui.Row(6, name,
                Ui.Badge("new", Eve.Eve.Green, entry, nameof(entry.IsNew)),
                Ui.Badge("modified", Eve.Eve.Amber, entry, nameof(entry.IsDirty))),
            Ui.Label(entry, nameof(entry.Summary), Eve.Eve.Muted, 10),
            Bind.Visible(Ui.Label(entry, nameof(entry.DescriptionPreview), Eve.Eve.Muted, 11, format: v => Trim(v?.ToString(), 70)), entry, nameof(entry.HasDescription)));
        content.MouseFilter = Control.MouseFilterEnum.Ignore;
        Ignore(content);
        var margin = Ui.Margin(content, 8, 6, 8, 6);
        margin.MouseFilter = Control.MouseFilterEnum.Ignore;
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        button.AddChild(margin);
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

    private static string Trim(string? text, int length)
    {
        var line = (text ?? string.Empty).Replace('\n', ' ').Trim();
        return line.Length > length ? line[..length] + "…" : line;
    }

    /// <summary>The editor of the project selected: its fields, Delete, Discard changes, Save.</summary>
    private static Control Editor(ProjectWindowViewModel vm, ProjectEntryViewModel entry)
    {
        var header = Ui.Row(8,
            Ui.Fill(Ui.Column(0, Ui.Label(entry, nameof(entry.IdLabel), Eve.Eve.Text, 12), Ui.Label(entry, nameof(entry.DateLabel), Eve.Eve.Muted, 10))),
            Ui.Label(entry, nameof(entry.Status), Eve.Eve.Muted, 11),
            Ui.Command("DELETE", vm.DeleteCommand, tooltip: "Delete this project after a confirmation: it is marked deleted on the server (never erased) and leaves the lists"),
            Ui.Command("DISCARD CHANGES", entry.RevertCommand, tooltip: "Give the changes up: the project as it was saved last"),
            Ui.Command("SAVE", entry.SaveCommand, primary: true, tooltip: "Save the project on the server"));

        // The goals, one box each.
        var goals = Ui.Column(6);
        Bind.Items(goals, entry.Goals, goal => GoalRow(entry, goal));

        var meta = Ui.Grid(2, 12, 6);
        meta.AddChild(Ui.Fill(Ui.Field("Tags (one per line; left empty, the declared tags found in the texts are set on save)", Ui.Lines(entry, nameof(entry.Tags), 70, MetaTags.TagsHint))));
        meta.AddChild(Ui.Fill(Ui.Field("Comment", Ui.Lines(entry, nameof(entry.Comment), 70))));

        var fields = Ui.Column(8,
            Ui.Field("Name", Ui.Line(entry, nameof(entry.Name), "The name of the project")),
            Ui.Row(12,
                Ui.Field("Start date (yyyy-MM-dd; today by default)", Ui.Line(entry, nameof(entry.StartDate), "yyyy-MM-dd")),
                Ui.Field("End date (yyyy-MM-dd; empty: no end)", Ui.Line(entry, nameof(entry.EndDate), "yyyy-MM-dd, or empty")),
                Ui.Fill(new Control())),
            Ui.Field("Description", Ui.Lines(entry, nameof(entry.Description), 130, "What the project is about")),
            Eve.Eve.Separator(),
            Ui.Row(8, Eve.Eve.Caption("Goals"), Ui.Fill(Ui.Label(entry, nameof(entry.GoalsLabel), Eve.Eve.Muted, 11)),
                Ui.Command("+ ADD GOAL", entry.AddGoalCommand, tooltip: "Add another goal")),
            goals,
            Eve.Eve.Separator(),
            meta);
        return Ui.Column(8, header, Eve.Eve.Separator(), Ui.FillHeight(Ui.Scroll(Ui.Margin(fields, 0, 0, 12, 12))));
    }

    /// <summary>A goal: its text and ✕.</summary>
    private static Control GoalRow(ProjectEntryViewModel entry, ProjectGoalViewModel goal)
    {
        var edit = Ui.Lines(goal, nameof(goal.Text), 48, "A goal of the project");
        edit.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var button = Ui.Command("✕", entry.RemoveGoalCommand, tooltip: "Remove this goal", parameter: () => goal);
        button.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        return Ui.Row(6, edit, button);
    }
}
