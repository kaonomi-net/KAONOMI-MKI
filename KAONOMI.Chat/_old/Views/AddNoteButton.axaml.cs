using Avalonia.Controls;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>
/// "Add a note" icon: opens the anonymous note popup with the context of the view it is placed in
/// (window name and current data, from the view's <see cref="INoteContextProvider"/> view model).
/// </summary>
public partial class AddNoteButton : UserControl
{
    /// <summary>Initializes the icon button.</summary>
    public AddNoteButton()
    {
        InitializeComponent();
        NoteButton.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window window)
                return;

            // The view model of the view holding the button (a tab of the workspace, or the chat).
            var provider = DataContext as INoteContextProvider;
            var title = (window as WorkspaceWindow)?.SelectedTitle ?? window.Title ?? "Kaonomi";
            var context = NoteContext.Build(provider, title);
            var windowName = provider?.NoteWindowName ?? title;
            var dialog = new NoteDialog
            {
                DataContext = new NoteDialogViewModel(KaonomiAssistantService.Shared, windowName, context)
            };
            await dialog.ShowDialog(window);
        };
    }
}
