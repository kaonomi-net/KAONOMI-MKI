using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Data;

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
        vm.CloseRequested += () => Bind.OnMain(() => shell.Close(vm));

        // The two ways: an existing folder, or a new one.
        var group = new ButtonGroup();
        var existing = new Button { Text = "Existing folder", ToggleMode = true, ButtonGroup = group, FocusMode = Control.FocusModeEnum.None };
        var create = new Button { Text = "＋ New folder", ToggleMode = true, ButtonGroup = group, FocusMode = Control.FocusModeEnum.None };
        existing.Pressed += () => vm.IsNewFolder = false;
        create.Pressed += () => vm.IsNewFolder = true;

        var list = Bind.List(new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 180) },
            vm.Folders, f => $"{f.Name}   ·   {f.SecurityClassification.ToUpperInvariant()}   ·   {f.WindowCount} window{(f.WindowCount == 1 ? "" : "s")}",
            vm, nameof(vm.SelectedFolder), f => string.IsNullOrWhiteSpace(f.Description) ? f.Name : f.Description, f => LevelColor(f.SecurityClassification));
        list.ItemActivated += _ => Bind.Run(vm.AddCommand);
        var none = Eve.Eve.Text_("No folder yet: make one with ＋ New folder.", Eve.Eve.Muted, 12, wrap: true);
        var pick = Ui.Column(6, Eve.Eve.Text_("Choose the folder:", Eve.Eve.Text, 13), Ui.FillHeight(list), none);

        var name = Ui.Line(vm, nameof(vm.NewName), "Name of the folder");
        var security = Bind.Options(new OptionButton(), vm.SecurityLevels, vm, nameof(vm.NewSecurity), level => level.ToUpperInvariant());
        var form = Ui.Column(10,
            Ui.Field("NAME", name),
            Ui.Field("DESCRIPTION", Ui.Lines(vm, nameof(vm.NewDescription), 90, "What the folder is about")),
            Ui.Field("SECURITY LEVEL", security));

        Bind.When(form, vm, () =>
        {
            pick.Visible = !vm.IsNewFolder;
            none.Visible = vm.Folders.Count == 0;
            form.Visible = vm.IsNewFolder;
            existing.SetPressedNoSignal(!vm.IsNewFolder);
            create.SetPressedNoSignal(vm.IsNewFolder);
            if (vm.IsNewFolder)
                name.CallDeferred(Control.MethodName.GrabFocus);
        }, nameof(vm.IsNewFolder));

        var status = Ui.Label(vm, nameof(vm.Status), Eve.Eve.Muted, 12, wrap: true);
        Bind.When(status, vm, () => status.AddThemeColorOverride("font_color", vm.IsError ? Eve.Eve.Red : Eve.Eve.Muted), nameof(vm.IsError));
        var cancel = Ui.Action("CANCEL", () => shell.Close(vm));
        var add = Ui.Command("ADD TO FOLDER", vm.AddCommand, primary: true);
        var what = vm.HasPicture ? "its data and a picture of it" : "its data";
        return Ui.Column(10,
            Eve.Eve.Text_($"The {vm.WindowName} window goes into a folder, with {what}.", Eve.Eve.Text, 13, wrap: true),
            Ui.Row(6, existing, create),
            Ui.FillHeight(Ui.Column(0, pick, form)),
            Ui.Row(8, Ui.Fill(status), cancel, add));
    }

    /// <summary>The color of a security level in the list: the higher, the warmer.</summary>
    private static Color? LevelColor(string level) => level switch
    {
        "secret" => Eve.Eve.Red,
        "confidential" => Eve.Eve.Amber,
        _ => null
    };

    /// <summary>Opens the adding of the window <paramref name="name"/> (data from <paramref name="provider"/>, PNG <paramref name="picture"/>).</summary>
    public static void Open(Shell shell, string name, object? provider, byte[]? picture)
    {
        var source = provider as INoteContextProvider;
        var windowName = source?.NoteWindowName ?? name;
        var context = NoteContext.Build(source, windowName, whole: true);
        shell.Open(new AddToFolderViewModel(windowName, context, picture), title: $"Add to a folder · {windowName}");
    }
}
