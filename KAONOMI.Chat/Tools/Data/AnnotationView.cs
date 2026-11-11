using Avalonia;
using Avalonia.Controls;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

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
        vm.CloseRequested += () => Ui.OnMain(() => shell.Close(vm));
        var target = Ui.Label(vm.TargetLabel ?? string.Empty, Ui.TextBright, 13, wrap: true);
        var excerpt = Ui.Box(Ui.Label(vm.ExcerptPreview ?? string.Empty, Ui.Muted, 12, wrap: true), 8, Ui.Field);
        excerpt.IsVisible = vm.HasExcerpt;
        var text = DataUi.FocusWhenShown(DataUi.Lines(vm, nameof(vm.Text), 120, "Type your annotation…"));
        DataUi.Enabled(text, vm, nameof(vm.IsSent), invert: true);
        var title = DataUi.Line(vm, nameof(vm.AnnotationTitle), "The title of the annotation");
        DataUi.Enabled(title, vm, nameof(vm.IsSent), invert: true);
        var tags = DataUi.Line(vm, nameof(vm.TagsText), "#SOFTWARE #IDEA … (separated by spaces, '-' inside one)");
        DataUi.Enabled(tags, vm, nameof(vm.IsSent), invert: true);
        var events = DataUi.Line(vm, nameof(vm.EventsText), MetaTags.EventsHint);
        DataUi.Enabled(events, vm, nameof(vm.IsSent), invert: true);
        var signals = DataUi.Line(vm, nameof(vm.SignalsText), MetaTags.SignalsHint);
        DataUi.Enabled(signals, vm, nameof(vm.IsSent), invert: true);
        // The tags suggested: a click adds one (or removes it when it is there).
        var chips = new WrapPanel();
        foreach (var tag in vm.SuggestedTags)
        {
            var chip = DataUi.Action(tag, () => Ui.Run(vm.ToggleTagCommand, tag), "Add this tag (or remove it)");
            chip.FontSize = 11;
            chip.Margin = new Thickness(0, 0, 6, 6);
            chips.Children.Add(chip);
        }
        var context = DataUi.ReadOnly(vm.Context ?? string.Empty, 110);
        context.MaxHeight = 200;
        var show = DataUi.Reveal("Context saved with the annotation (window and data)", context);
        var buttons = DataUi.Bar([], DataUi.Bound(vm, nameof(vm.Status), Ui.Muted, 12, wrap: true),
            DataUi.Action("CANCEL", () => shell.Close(vm)),
            DataUi.Command("SAVE", vm.SaveCommand, primary: true));
        var top = Ui.Column(8, target, excerpt, DataUi.Field("Title", title));
        top.Margin = new Thickness(0, 0, 0, 8);
        var metaRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
        var eventsField = DataUi.Field("Events", events);
        var signalsField = DataUi.Field("Signals", signals);
        Grid.SetColumn(signalsField, 2);
        metaRow.Children.Add(eventsField);
        metaRow.Children.Add(signalsField);
        var bottom = Ui.Column(8, DataUi.Field("Tags", tags), chips, metaRow, show, context, buttons);
        bottom.Margin = new Thickness(0, 8, 0, 0);
        var page = Ui.Dock((top, Dock.Top), (bottom, Dock.Bottom), (DataUi.FieldFill("Annotation", text), Dock.Top));
        page.Margin = new Thickness(12);
        return page;
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
        if (Shell.Current is not { } shell)
            return;
        var window = DataUi.WindowOf(origin);
        var provider = window?.ViewModel as INoteContextProvider ?? (window is null ? shell.Chat : null);
        var title = window?.Title ?? "Chat";
        var windowName = provider?.NoteWindowName ?? title;
        var context = NoteContext.Build(provider, windowName);
        shell.Open(new AnnotationDialogViewModel(KaonomiAssistantService.Shared, target, windowName, context) { SourceLink = window?.Key },
            title: $"Annotate · {target.Title ?? windowName}");
    }

    /// <summary>Opens the annotation of the text <paramref name="selection"/> selected in <paramref name="origin"/>.</summary>
    public static void OpenText(Control origin, string selection)
    {
        if (string.IsNullOrWhiteSpace(selection))
            return;
        Open(origin, AnnotationTarget.Text(selection.Trim(), DataUi.WindowOf(origin)?.Title ?? "Chat"));
    }

    /// <summary>A menu with "📝 Annotate…" for <paramref name="target"/> (set as the context menu of a control).</summary>
    public static ContextMenu Menu(Control at, AnnotationTarget target)
    {
        var item = new MenuItem { Header = MenuText };
        item.Click += (_, _) => Open(at, target);
        return new ContextMenu { ItemsSource = new[] { item } };
    }

    /// <summary>The target of an element of the relation graph (an entity, or an image analysis), or null.</summary>
    public static AnnotationTarget? OfGraphNode(RelationGraphNode node) => RelationGraphViewModel.AnnotationTargetOf(node);
}
