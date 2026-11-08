using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// Adds a window to a folder (📁+ in its header): an existing folder chosen in the list, or a new folder given a name,
/// a description and a security level; the window goes in with its data and a picture of it.
/// </summary>
[ToolView(typeof(AddToFolderViewModel), "Add to a folder", "📁", 560, 500, PerObject = true)]
public sealed class AddToFolderView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (AddToFolderViewModel)viewModel;
        vm.CloseRequested += () => Ui.OnMain(() => shell.Close(vm));

        // The two ways: an existing folder, or a new one.
        var existing = new ToggleButton { Content = "Existing folder", Focusable = false };
        var create = new ToggleButton { Content = "＋ New folder", Focusable = false };
        existing.Click += (_, _) => vm.IsNewFolder = false;
        create.Click += (_, _) => vm.IsNewFolder = true;

        var list = FoldersView.FolderList(vm.Folders, vm, nameof(vm.SelectedFolder));
        list.DoubleTapped += (_, _) => Ui.Run(vm.AddCommand);
        var none = Ui.Label("No folder yet: make one with ＋ New folder.", Ui.Muted, 12, wrap: true);
        var caption = Ui.Label("Choose the folder:", Ui.Text, 13);
        caption.Margin = new Thickness(0, 0, 0, 6);
        none.Margin = new Thickness(0, 6, 0, 0);
        var pick = Ui.Dock((caption, Dock.Top), (none, Dock.Bottom), (list, Dock.Top));

        var name = DataUi.Line(vm, nameof(vm.NewName), "Name of the folder");
        var security = DataUi.Options(vm.SecurityLevels, vm, nameof(vm.NewSecurity), level => level.ToUpperInvariant(), 160);
        var form = Ui.Column(10,
            DataUi.Field("NAME", name),
            DataUi.Field("DESCRIPTION", DataUi.Lines(vm, nameof(vm.NewDescription), 90, "What the folder is about")),
            DataUi.Field("SECURITY LEVEL", security));

        Ui.When(form, vm, () =>
        {
            pick.IsVisible = !vm.IsNewFolder;
            none.IsVisible = vm.Folders.Count == 0;
            form.IsVisible = vm.IsNewFolder;
            existing.IsChecked = !vm.IsNewFolder;
            create.IsChecked = vm.IsNewFolder;
            if (vm.IsNewFolder)
                Dispatcher.UIThread.Post(() => name.Focus(), DispatcherPriority.Background);
        }, nameof(vm.IsNewFolder));

        var status = DataUi.ErrorColor(DataUi.Bound(vm, nameof(vm.Status), Ui.Muted, 12, wrap: true), vm, nameof(vm.IsError));
        var buttons = DataUi.Bar([], status, DataUi.Action("CANCEL", () => shell.Close(vm)), DataUi.Command("ADD TO FOLDER", vm.AddCommand, primary: true));
        var what = vm.HasPicture ? "its data and a picture of it" : "its data";
        var title = Ui.Label($"The {vm.WindowName} window goes into a folder, with {what}.", Ui.Text, 13, wrap: true);
        var modes = Ui.Row(6, existing, create);
        var top = Ui.Column(10, title, modes);
        top.Margin = new Thickness(0, 0, 0, 10);
        buttons.Margin = new Thickness(0, 10, 0, 0);
        var middle = new Panel();
        middle.Children.Add(pick);
        middle.Children.Add(form);
        var page = Ui.Dock((top, Dock.Top), (buttons, Dock.Bottom), (middle, Dock.Top));
        page.Margin = new Thickness(12);
        return page;
    }

    /// <summary>Opens the adding of the window <paramref name="name"/> (data from <paramref name="provider"/>, PNG <paramref name="picture"/>).</summary>
    public static void Open(Shell shell, string name, object? provider, byte[]? picture)
    {
        var source = provider as INoteContextProvider;
        var windowName = source?.NoteWindowName ?? name;
        var context = NoteContext.Build(source, windowName, whole: true);
        shell.Open(new AddToFolderViewModel(windowName, context, picture), title: $"Add to a folder · {windowName}");
    }
}
