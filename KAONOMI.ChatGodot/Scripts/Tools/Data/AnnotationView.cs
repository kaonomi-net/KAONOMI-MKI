using System;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using KAONOMI.ChatGodot.Tools.Knowledge;

namespace KAONOMI.ChatGodot.Tools.Data;

/// <summary>
/// The annotation of an object (a right click → 📝 Annotate… on an image, a text selected, a video, an audio file, an
/// entity): what is annotated (and the text selected), the annotation typed, its tags (typed, or a click on a suggested
/// one), the context saved with it, Save; the window closes by itself once the server has the annotation.
/// </summary>
[ToolView(typeof(AnnotationDialogViewModel), "Annotate", "🏷", 620, 560, PerObject = true)]
public sealed class AnnotationView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (AnnotationDialogViewModel)viewModel;
        vm.CloseRequested += () => Bind.OnMain(() => shell.Close(vm));
        var target = Eve.Eve.Text_(vm.TargetLabel, Eve.Eve.TextBright, 13, wrap: true);
        var excerpt = Eve.Eve.Text_(vm.ExcerptPreview, Eve.Eve.Muted, 12, wrap: true);
        excerpt.Visible = vm.HasExcerpt;
        var excerptCard = new PanelContainer { Visible = vm.HasExcerpt };
        excerptCard.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Field, Eve.Eve.Edge, 6));
        excerptCard.AddChild(Ui.Margin(excerpt, 8));
        var text = Ui.Lines(vm, nameof(vm.Text), 120, "Type your annotation…");
        Bind.Enabled(text, vm, nameof(vm.IsSent), invert: true);
        var tags = Ui.Line(vm, nameof(vm.TagsText), "#SOFTWARE #IDEA … (separated by spaces)");
        Bind.Enabled(tags, vm, nameof(vm.IsSent), invert: true);
        // The tags suggested: a click adds one (or removes it when it is there).
        var chips = new HFlowContainer();
        chips.AddThemeConstantOverride("h_separation", 6);
        chips.AddThemeConstantOverride("v_separation", 6);
        foreach (var tag in vm.SuggestedTags)
        {
            var chip = Eve.Eve.Button(tag, tooltip: "Add this tag (or remove it)");
            chip.AddThemeFontSizeOverride("font_size", 11);
            chip.Pressed += () => Bind.Run(vm.ToggleTagCommand, tag);
            chips.AddChild(chip);
        }
        var context = new TextEdit { Text = vm.Context, Editable = false, WrapMode = TextEdit.LineWrappingMode.Boundary, CustomMinimumSize = new Vector2(0, 110), Visible = false };
        context.AddThemeFontOverride("font", Eve.Eve.Mono);
        context.AddThemeFontSizeOverride("font_size", 11);
        var show = new CheckButton { Text = "Context saved with the annotation (window and data)" };
        show.Toggled += on => context.Visible = on;
        var cancel = Ui.Action("CANCEL", () => shell.Close(vm));
        var save = Ui.Command("SAVE", vm.SaveCommand, primary: true);
        text.CallDeferred(Control.MethodName.GrabFocus);
        return Ui.Column(8,
            target,
            excerptCard,
            Ui.FillHeight(Ui.Field("Annotation", Ui.FillHeight(text))),
            Ui.Field("Tags", tags),
            chips,
            show,
            context,
            Ui.Row(8, Ui.Fill(Ui.Label(vm, nameof(vm.Status), Eve.Eve.Muted, 12, wrap: true)), cancel, save));
    }
}

/// <summary>
/// Opens the annotation of an object from the control the right click was on: the context saved with it is the one of
/// the window holding that control (its view model, when it gives one), as for a note; outside a window, the chat's.
/// </summary>
public static class Annotations
{
    /// <summary>The item of the context menus.</summary>
    public const string MenuText = "📝 Annotate…";

    /// <summary>Opens the annotation of <paramref name="target"/>, written in the window holding <paramref name="origin"/>.</summary>
    public static void Open(Control? origin, AnnotationTarget target)
    {
        var shell = Shell.Current;
        if (shell is null)
            return;
        var window = origin is null ? null : shell.WindowOf(origin);
        var provider = window?.ViewModel as INoteContextProvider ?? (window is null ? shell.Chat : null);
        var title = window?.Title ?? "Chat";
        var windowName = provider?.NoteWindowName ?? title;
        var context = NoteContext.Build(provider, windowName);
        shell.Open(new AnnotationDialogViewModel(KaonomiAssistantService.Shared, target, windowName, context) { SourceLink = window?.Key },
            title: $"Annotate · {target.Title ?? windowName}");
    }

    /// <summary>
    /// Shows a menu with "📝 Annotate…" at <paramref name="local"/> of <paramref name="at"/>; for a picture (the file
    /// <paramref name="picture"/>, or the one annotated), "🖼 Set as wallpaper" too.
    /// </summary>
    public static void ShowMenu(Control at, Vector2 local, AnnotationTarget target, string? picture = null)
    {
        var menu = new ContextMenu().Item(MenuText, () => Open(at, target));
        picture ??= target.Type == AnnotationTargetTypes.Image ? target.Id : null;
        if (picture is not null && System.IO.File.Exists(picture))
            menu.Item(Wallpaper.MenuText, () => Wallpaper.Set(picture));
        menu.Show(at, local);
    }

    /// <summary>The target of an element of the relation graph (an entity, or an image analysis), or null.</summary>
    public static AnnotationTarget? OfGraphNode(RelationGraphNode node) => RelationGraphViewModel.AnnotationTargetOf(node);

    // ── A text selected, everywhere ──

    private const int AnnotateId = 9_401;

    /// <summary>
    /// The right click of the texts (<see cref="SceneTree.NodeAdded"/>, once): the text areas show the menu of the chat
    /// (<see cref="TextMenu"/>: edit, annotate, translate, analyse, summarise, emotions); the text fields and the
    /// selectable texts keep the menu of Godot, with "📝 Annotate selection…" added (enabled when a text is selected).
    /// </summary>
    public static void HookTextMenus(SceneTree tree) =>
        tree.NodeAdded += node =>
        {
            // The menu is handled at the right click on the control (its gui_input signal comes before its own
            // handling, which opens the menu of Godot): the menus of the controls never right-clicked are not created.
            if (node is TextEdit or LineEdit or RichTextLabel { ContextMenuEnabled: true, SelectionEnabled: true } && !node.HasMeta("annotate_watched"))
            {
                var control = (Control)node;
                control.SetMeta("annotate_watched", true);
                control.GuiInput += e =>
                {
                    if (e is not InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } press)
                        return;
                    if (control is TextEdit edit && TextMenu.RightClicked(edit, press))
                        edit.AcceptEvent();
                    else
                        Hook(control);
                };
            }
        };

    private static void Hook(Control control)
    {
        if (!GodotObject.IsInstanceValid(control) || control.HasMeta("annotate_hooked"))
            return;
        PopupMenu? menu = control switch
        {
            TextEdit edit => edit.GetMenu(),
            LineEdit line => line.GetMenu(),
            RichTextLabel label when label.ContextMenuEnabled => label.GetMenu(),
            _ => null
        };
        if (menu is null)
            return;
        control.SetMeta("annotate_hooked", true);
        menu.AddSeparator();
        menu.AddItem("📝 Annotate selection…", AnnotateId);
        menu.AboutToPopup += () =>
        {
            var index = menu.GetItemIndex(AnnotateId);
            if (index >= 0)
                menu.SetItemDisabled(index, string.IsNullOrWhiteSpace(Selection(control)));
        };
        menu.IdPressed += id =>
        {
            if (id != AnnotateId || Selection(control) is not { Length: > 0 } selection || string.IsNullOrWhiteSpace(selection))
                return;
            var window = Shell.Current?.WindowOf(control);
            Open(control, AnnotationTarget.Text(selection.Trim(), window?.Title ?? "Chat"));
        };
    }

    private static string? Selection(Control control) => control switch
    {
        TextEdit edit => edit.GetSelectedText(),
        LineEdit line => line.GetSelectedText(),
        RichTextLabel label => label.GetSelectedText(),
        _ => null
    };
}
