using System;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Data;

/// <summary>
/// /activity: the journal of the activities (each SAVE done in a window, index useractivities): the newest first, a
/// search (Enter; empty: the newest), each activity with its date, its window, its description and OPEN (its window
/// again: the tool of its command, or the window of its key while it is open). Read again after each new activity.
/// </summary>
[ToolView(typeof(ActivityWindowViewModel), "Activity journal", "🗒", 900, 680)]
public sealed class ActivityView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (ActivityWindowViewModel)viewModel;
        var search = Ui.SearchBar(vm, nameof(vm.Term), vm.SearchCommand, "Search the window, the link or the description · empty: the newest activities");

        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        var empty = Ui.Placeholder("No activity");
        Bind.Items(list, vm.Entries, entry => Row(shell, vm, entry), empty);

        var page = Ui.Column(6, Ui.Toolbar(search), Ui.FillHeight(Ui.Margin(Ui.Column(0, Ui.FillHeight(Ui.Scroll(list)), empty), 8, 0, 8, 0)),
            Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError)));

        // A new activity: the journal is read again (when no search is typed).
        Action recorded = () =>
        {
            if (vm.Term.Trim().Length == 0)
                vm.SearchCommand.Execute(null);
        };
        Action annotated = () => Bind.OnMain(recorded);
        ActivityJournal.Recorded += recorded;
        AnnotationDialogViewModel.Journaled += annotated;
        Bind.OnGone(page, () =>
        {
            ActivityJournal.Recorded -= recorded;
            AnnotationDialogViewModel.Journaled -= annotated;
        });
        return page;
    }

    private static Control Row(Shell shell, ActivityWindowViewModel vm, UserActivityEntry entry)
    {
        var date = Eve.Eve.Text_(entry.Date?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty, Eve.Eve.Muted, 11);
        date.CustomMinimumSize = new Vector2(130, 0);
        date.AddThemeFontOverride("font", Eve.Eve.Mono);
        var window = Eve.Eve.Text_(entry.WindowName, Eve.Eve.TextBright, 12);
        window.CustomMinimumSize = new Vector2(190, 0);
        window.ClipText = true;
        window.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        window.TooltipText = entry.Link;
        var description = Eve.Eve.Text_(entry.Description, Eve.Eve.Text, 12);
        description.ClipText = true;
        description.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        description.TooltipText = entry.Description;
        var open = Ui.Action("OPEN", () => Open(shell, vm, entry), tooltip: $"Open the window again ({entry.Link})");
        var row = Ui.Row(10, date, window, Ui.Fill(description), open);
        return Ui.Column(2, row, Eve.Eve.Separator());
    }

    /// <summary>The window of the activity again: the tool of its command, or the window of its key while it is open.</summary>
    private static void Open(Shell shell, ActivityWindowViewModel vm, UserActivityEntry entry)
    {
        if (shell.Deck.Find(entry.Link) is { } open)
        {
            shell.Deck.BringToFront(open);
            return;
        }
        if (entry.Link.StartsWith('/'))
        {
            shell.RunCommand(entry.Link);
            return;
        }
        vm.Status = $"\"{entry.WindowName}\" is closed and cannot be opened from here (it is not a tool of the chat)";
        vm.IsError = true;
    }
}
