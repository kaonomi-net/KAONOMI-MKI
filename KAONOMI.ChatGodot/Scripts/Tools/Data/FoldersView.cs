using System.IO;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Data;

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
        vm.OpenRequested += (folder, items) => Bind.OnMain(() =>
        {
            foreach (var item in items)
                shell.Open(new FolderItemViewModel(folder, item), title: $"{folder.Name} · {item.WindowName}");
        });

        var search = Ui.Line(vm, nameof(vm.Term), "Search the folders (name, description, security level)");
        var list = Bind.List(new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill },
            vm.Folders,
            f => $"{f.Name}   ·   {f.SecurityClassification.ToUpperInvariant()}   ·   {f.WindowCount} window{(f.WindowCount == 1 ? "" : "s")}",
            vm, nameof(vm.SelectedFolder),
            f => string.IsNullOrWhiteSpace(f.Description) ? f.Name : f.Description,
            f => f.SecurityClassification switch { "secret" => Eve.Eve.Red, "confidential" => Eve.Eve.Amber, _ => null });
        list.ItemActivated += _ => Bind.Run(vm.OpenCommand);

        var description = Eve.Eve.Text_("", Eve.Eve.Muted, 12, wrap: true);
        Bind.When(description, vm, () =>
        {
            var f = vm.SelectedFolder;
            description.Text = f is null ? "" : $"{(string.IsNullOrWhiteSpace(f.Description) ? "No description." : f.Description)}   ·   created {f.Created.LocalDateTime:g}";
        }, nameof(vm.SelectedFolder));

        var show = Ui.Action("SHOW FILES", () =>
        {
            var path = vm.SelectedFolder is { } f ? Path.Combine(FolderStore.Root, f.Id) : FolderStore.Root;
            if (Directory.Exists(path))
                OS.ShellOpen(path);
        }, tooltip: "The files of the folder in the file explorer");
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(search))
                search.GrabFocus();
        }).CallDeferred();
        return Ui.Column(8,
            Ui.Row(6, search, Ui.Command("↻", vm.RefreshCommand, tooltip: "Read the folders again")),
            Ui.FillHeight(list),
            description,
            Ui.Row(8, Ui.Fill(Ui.Label(vm, nameof(vm.Status), Eve.Eve.Muted, 12)), show, Ui.Command("OPEN", vm.OpenCommand, primary: true, tooltip: "Open every window of the folder (or double-click it)")));
    }
}

/// <summary>A window kept in a folder, opened again: the picture taken when it was added, and its data.</summary>
[ToolView(typeof(FolderItemViewModel), "Folder item", "📁", 900, 640, PerObject = true)]
public sealed class FolderItemView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (FolderItemViewModel)viewModel;
        var item = vm.Item;
        var info = Eve.Eve.Text_($"{item.WindowName}   ·   folder {vm.Folder.Name} ({vm.Folder.SecurityClassification.ToUpperInvariant()})   ·   added {item.Added:g}", Eve.Eve.Muted, 12, wrap: true);

        var data = new TextEdit { Text = item.Data, Editable = false, WrapMode = TextEdit.LineWrappingMode.Boundary, SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 160) };
        data.AddThemeFontOverride("font", Eve.Eve.Mono);
        data.AddThemeFontSizeOverride("font_size", 11);

        Texture2D? texture = null;
        if (item.PicturePath is { } path && Image.LoadFromFile(path) is { } image && !image.IsEmpty())
            texture = ImageTexture.CreateFromImage(image);
        if (texture is null)
            return Ui.Column(8, info, Ui.FillHeight(data));

        // The picture first; the data under it, shown on demand.
        var picture = Ui.FillHeight(Ui.Picture(texture, 200));
        data.Visible = false;
        var toggle = new CheckButton { Text = "Data of the window" };
        toggle.Toggled += on => data.Visible = on;
        return Ui.Column(8, info, picture, toggle, data);
    }
}
