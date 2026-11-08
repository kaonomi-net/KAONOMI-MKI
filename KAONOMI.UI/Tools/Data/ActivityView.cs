using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// /activity: the journal of the activities (each SAVE done in a window, index useractivities): the newest first, a
/// search (Enter; empty: the newest), each activity with its date, its window, its description and OPEN (its window
/// again: the tool of its command, or the window of its key while it is open). Read again after each new annotation
/// journaled (when no search is typed).
/// </summary>
[ToolView(typeof(ActivityWindowViewModel), "Activity journal", "🗒", 900, 680)]
public sealed class ActivityView : IToolView
{
    // The right-click menu of the texts (TextMenu) and the journal of the SAVEs (ActivityJournal) are hooked once, when
    // the views are discovered.
    static ActivityView()
    {
        TextMenu.Hook();
        ActivityJournal.Hook();
    }

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (ActivityWindowViewModel)viewModel;
        var search = DataUi.SearchBar(vm, nameof(vm.Term), vm.SearchCommand, "Search the window, the link or the description · empty: the newest activities");

        var list = DataUi.ScrollList(vm.Entries, entry => Row(shell, vm, entry), "No activity", spacing: 0, virtualize: true);
        list.Margin = new Thickness(8, 0);

        var page = Ui.Dock(
            (DataUi.Toolbar(search), Dock.Top),
            (DataUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError)), Dock.Bottom),
            (list, Dock.Top));

        // A new activity (a SAVE, an annotation): the journal is read again (when no search is typed).
        Action recorded = () => Ui.OnMain(() =>
        {
            if ((vm.Term ?? string.Empty).Trim().Length == 0)
                Ui.Run(vm.SearchCommand);
        });
        DataUi.Subscribe(page, () =>
        {
            ActivityJournal.Recorded += recorded;
            AnnotationDialogViewModel.Journaled += recorded;
        }, () =>
        {
            ActivityJournal.Recorded -= recorded;
            AnnotationDialogViewModel.Journaled -= recorded;
        });
        return page;
    }

    private static Control Row(Shell shell, ActivityWindowViewModel vm, UserActivityEntry entry)
    {
        var date = DataUi.Text(entry.Date?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty, Ui.Muted, 11, mono: true);
        date.Width = 140;
        var window = DataUi.Tip(DataUi.Text(entry.WindowName ?? string.Empty, Ui.TextBright, 12), entry.Link);
        window.Width = 190;
        var description = DataUi.Tip(DataUi.Text(entry.Description ?? string.Empty, Ui.Text, 12), entry.Description);
        var open = DataUi.Action("OPEN", () => Open(shell, vm, entry), $"Open the window again ({entry.Link})");
        var row = DataUi.Bar([date, window], description, open);
        return new Border
        {
            Child = row,
            Padding = new Thickness(2, 4),
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
    }

    /// <summary>The window of the activity again: the tool of its command, or the window of its key while it is open.</summary>
    private static void Open(Shell shell, ActivityWindowViewModel vm, UserActivityEntry entry)
    {
        var link = entry.Link ?? string.Empty;
        if (shell.Deck.Find(link) is { } open)
        {
            shell.Deck.BringToFront(open);
            return;
        }
        if (link.StartsWith('/'))
        {
            shell.RunCommand(link);
            return;
        }
        vm.Status = $"\"{entry.WindowName}\" is closed and cannot be opened from here (it is not a tool of the chat)";
        vm.IsError = true;
    }
}
