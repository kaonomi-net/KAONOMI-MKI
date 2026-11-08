using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// /folders (or /folder): the folders made with 📁+, searched as the words are typed (name, description, security
/// level); a double-click on a folder, or OPEN, opens every window kept in it, each in a window of its own.
/// </summary>
[ToolView(typeof(FoldersWindowViewModel), "Folders", "📁", 640, 520)]
public sealed class FoldersView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (FoldersWindowViewModel)viewModel;
        vm.OpenRequested += (folder, items) => Ui.OnMain(() =>
        {
            foreach (var item in items)
                shell.Open(new FolderItemViewModel(folder, item), title: $"{folder.Name} · {item.WindowName}");
        });

        var search = DataUi.FocusWhenShown(DataUi.Line(vm, nameof(vm.Term), "Search the folders (name, description, security level)"));
        var list = FolderList(vm.Folders, vm, nameof(vm.SelectedFolder));
        list.DoubleTapped += (_, _) => Ui.Run(vm.OpenCommand);

        var description = Ui.Label(string.Empty, Ui.Muted, 12, wrap: true);
        Ui.When(description, vm, () =>
        {
            var f = vm.SelectedFolder;
            description.Text = f is null ? string.Empty : $"{(string.IsNullOrWhiteSpace(f.Description) ? "No description." : f.Description)}   ·   created {f.Created.LocalDateTime:g}";
        }, nameof(vm.SelectedFolder));

        var show = DataUi.Action("SHOW FILES", () =>
        {
            var path = vm.SelectedFolder is { } f ? Path.Combine(FolderStore.Root, f.Id) : FolderStore.Root;
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }, "The files of the folder in the file explorer");

        var top = DataUi.Bar([], search, DataUi.Command("↻", vm.RefreshCommand, "Read the folders again"));
        var bottom = DataUi.Bar([], DataUi.Bound(vm, nameof(vm.Status), Ui.Muted, 12), show,
            DataUi.Command("OPEN", vm.OpenCommand, "Open every window of the folder (or double-click it)", primary: true));
        var page = Ui.Dock((top, Dock.Top), (bottom, Dock.Bottom), (description, Dock.Bottom), (list, Dock.Top));
        top.Margin = new Thickness(0, 0, 0, 8);
        description.Margin = new Thickness(0, 8);
        page.Margin = new Thickness(10);
        return page;
    }

    /// <summary>The folders (name, security level, number of windows; the description as tooltip; secret in red, confidential in amber).</summary>
    internal static ListBox FolderList(System.Collections.Generic.IEnumerable<WindowFolder> folders, object source, string selected)
    {
        var list = new ListBox
        {
            ItemsSource = folders,
            MinHeight = 160,
            ItemTemplate = new FuncDataTemplate<WindowFolder>((f, _) =>
            {
                if (f is null)
                    return new Panel();
                var text = Ui.Label($"{f.Name}   ·   {f.SecurityClassification.ToUpperInvariant()}   ·   {f.WindowCount} window{(f.WindowCount == 1 ? "" : "s")}",
                    LevelColor(f.SecurityClassification) ?? Ui.Text, 13);
                ToolTip.SetTip(text, string.IsNullOrWhiteSpace(f.Description) ? f.Name : f.Description);
                return text;
            })
        };
        return list.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, source, selected, BindingMode.TwoWay);
    }

    /// <summary>The colour of a security level in the lists: the higher, the warmer.</summary>
    internal static IBrush? LevelColor(string level) => level switch
    {
        "secret" => Ui.Red,
        "confidential" => DataUi.Amber,
        _ => null
    };
}

/// <summary>A window kept in a folder, opened again: the picture taken when it was added, and its data.</summary>
[ToolView(typeof(FolderItemViewModel), "Folder item", "📁", 900, 640, PerObject = true)]
public sealed class FolderItemView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (FolderItemViewModel)viewModel;
        var item = vm.Item;
        var info = Ui.Label($"{item.WindowName}   ·   folder {vm.Folder.Name} ({vm.Folder.SecurityClassification.ToUpperInvariant()})   ·   added {item.Added:g}", Ui.Muted, 12, wrap: true);
        info.Margin = new Thickness(0, 0, 0, 8);
        var data = DataUi.ReadOnly(item.Data ?? string.Empty, 160);

        Bitmap? picture = null;
        try
        {
            if (item.PicturePath is { } path && File.Exists(path))
                picture = new Bitmap(path);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            picture = null;
        }

        Control page;
        if (picture is null)
            page = Ui.Dock((info, Dock.Top), (data, Dock.Top));
        else
        {
            // The picture first; the data under it, shown on demand.
            var image = new Image { Source = picture, Stretch = Stretch.Uniform, MinHeight = 200 };
            var toggle = DataUi.Reveal("Data of the window", data);
            data.MaxHeight = 280;
            page = Ui.Dock((info, Dock.Top), (data, Dock.Bottom), (toggle, Dock.Bottom), (image, Dock.Top));
        }
        page.Margin = new Thickness(10);
        return page;
    }
}
