using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>
/// The Entity window of the ontology, one per node opened: on the left a search on every node of the ontology (Enter
/// searches, Esc clears; a hit opens in the editor and is selected in the tree); on the right the editor of the node:
/// its name, path, id, parent, virtual parents and dates, its status, Annotate, Clone, Add child, Discard changes and
/// Save (Ctrl+S / ⌘S); every field: name with Auto (fills it with the model of the chat) and Cancel, entity type,
/// symbol, start and end dates, confidentiality (with the minimum given by the parent), ontology class, description,
/// example sentences, comment, synonyms, tags, related entities and links, the six emotion levels.
/// </summary>
[ToolView(typeof(EntityWindowViewModel), "Entity", "🧩", 1150, 780, PerObject = true)]
public sealed class EntityView : IToolView
{
    private sealed class NameBox
    {
        public TextBox? Edit;
    }

    private static readonly ConditionalWeakTable<EntityWindowViewModel, NameBox> Names = new();

    /// <summary>Focuses the name of the node shown by <paramref name="viewModel"/> and selects it.</summary>
    public static void FocusName(EntityWindowViewModel viewModel)
    {
        if (Names.TryGetValue(viewModel, out var box) && box.Edit is { } edit)
            KnowledgeUi.FocusLater(edit, selectAll: true);
    }

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (EntityWindowViewModel)viewModel;
        Names.AddOrUpdate(vm, new NameBox());

        // The search on the left.
        var term = Ui.Edit(vm, nameof(vm.Term), "Search an entity · Enter · Esc: clear");
        term.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Ui.Run(vm.SearchCommand);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Ui.Run(vm.ClearSearchCommand);
                e.Handled = true;
            }
        };
        var search = Ui.Command("SEARCH", vm.SearchCommand, primary: true);
        search.HorizontalAlignment = HorizontalAlignment.Stretch;
        var results = new ListBox
        {
            ItemsSource = vm.Results,
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<EntitySearchHit>((hit, _) => hit is null ? new Panel() : HitRow(hit))
        }.Bind(ListBox.SelectedItemProperty, vm, nameof(vm.SelectedResult), BindingMode.TwoWay);
        var left = Ui.Dock(
            (Ui.Column(6,
                Ui.Caption("Search"),
                term,
                KnowledgeUi.Pair(6, search, Ui.Command("CLEAR", vm.ClearSearchCommand)),
                Ui.Bound(vm, nameof(vm.SearchStatus), Ui.Muted, 11, wrap: true)), Dock.Top),
            (Ui.Box(results, 2, Ui.Field), Dock.Top));
        ((Control)left.Children[0]).Margin = new Thickness(0, 0, 0, 6);
        left.Margin = new Thickness(6, 6, 4, 6);

        // The editor on the right.
        var editor = new ContentControl();
        Ui.When(editor, vm, () => editor.Content = vm.Node is { } node
            ? Editor(vm, node)
            : KnowledgeUi.Placeholder("No entity · search one on the left, or double-click a node of the ontology tree"), nameof(vm.Node));

        var page = KnowledgeUi.SplitH(left, editor, 280);
        KnowledgeUi.Shortcuts(page, key => KnowledgeUi.IsCommand(key, Key.S) && KnowledgeUi.TryRun(vm.Node?.SaveCommand));
        FocusName(vm);
        return page;
    }

    /// <summary>A hit of the search: its name, type, a deleted badge and its place in the tree.</summary>
    private static Control HitRow(EntitySearchHit hit)
    {
        var name = Ui.Label(hit.Node.DisplayName, Ui.TextBright, 13);
        var type = Ui.Label(hit.Node.EntityType ?? string.Empty, Ui.Muted, 11);
        type.IsVisible = hit.Node.HasEntityType;
        var path = Ui.Label(hit.PathLabel, Ui.Muted, 10);
        ToolTip.SetTip(path, hit.PathLabel);
        return new Border
        {
            Padding = new Thickness(4, 2),
            Child = Ui.Column(1, Ui.Row(6, name, type, KnowledgeUi.Badge("deleted", Paint.Red, hit.Node, nameof(hit.Node.IsDeleted))), path)
        };
    }

    /// <summary>The editor of <paramref name="node"/>: its header and actions, then every field in two columns.</summary>
    private static Control Editor(EntityWindowViewModel vm, OntologyNodeViewModel node)
    {
        var title = KnowledgeUi.Clip(Ui.Bound(node, nameof(node.DisplayName), Ui.TextBright, 18));
        title.FontWeight = FontWeight.SemiBold;
        var virtualParents = KnowledgeUi.Selectable(node, nameof(node.VirtualParentsLabel), Ui.Muted, 11).Visible(node, nameof(node.HasVirtualParents));
        var identity = Ui.Column(1,
            title,
            KnowledgeUi.Clip(Ui.Bound(vm, nameof(vm.NodePath), Ui.Muted, 11)),
            KnowledgeUi.Selectable(node, nameof(node.IdLabel), Ui.Text, 12),
            KnowledgeUi.Selectable(node, nameof(node.ParentLabel), Ui.Muted, 11),
            virtualParents,
            KnowledgeUi.Clip(Ui.Bound(node, nameof(node.DatesLabel), Ui.Muted, 11)));
        identity.Background = Brushes.Transparent;
        identity.MinWidth = 200;

        var dot = Ui.Label("●", Ui.Red, 10).Visible(node, nameof(node.IsError));
        var status = KnowledgeUi.Clip(Ui.Bound(node, nameof(node.Status), Ui.Muted, 12));
        status.MaxWidth = 360;
        Ui.When(status, node, () => ToolTip.SetTip(status, string.IsNullOrEmpty(node.Status) ? null : node.Status), nameof(node.Status));

        // 📝: an annotation of the entity (also a right click on its identity).
        AnnotationTarget Target() => node.AnnotationTarget;
        var annotate = Ui.Button("📝 ANNOTATE", () => KnowledgeUi.Annotate(identity, Target()), "Write an annotation of this entity, with its tags (save the entity first)");
        Ui.When(annotate, node, () => annotate.IsEnabled = !node.IsNew, nameof(node.IsNew));
        identity.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(identity).Properties.IsRightButtonPressed && !node.IsNew)
            {
                KnowledgeUi.AnnotateMenu(identity, Target());
                e.Handled = true;
            }
        };
        var actions = KnowledgeUi.Flow(6,
            Ui.Row(6, dot, status),
            annotate,
            Ui.Command("CLONE", node.CloneCommand, tooltip: "Add an unsaved copy of this node next to it (save the node first)"),
            Ui.Command("ADD CHILD", node.AddChildCommand, tooltip: "Add a child under this node (save the node first)"),
            Ui.Command("DISCARD CHANGES", node.RevertCommand),
            Ui.Command("SAVE", node.SaveCommand, tooltip: "Ctrl+S / ⌘S", primary: true));
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        actions.VerticalAlignment = VerticalAlignment.Top;
        var header = KnowledgeUi.FillRow(12, [], identity, actions);
        header.Margin = new Thickness(12, 6, 12, 2);

        // Name, and Auto (the model selected in the chat fills the comment, synonyms, tags, sentences); Cancel while it runs.
        var name = Ui.Edit(node, nameof(node.Name));
        if (Names.TryGetValue(vm, out var box))
            box.Edit = name;
        var autoProgress = new ProgressBar { IsIndeterminate = true, Width = 40, Height = 4, MinHeight = 4, VerticalAlignment = VerticalAlignment.Center };
        var auto = Ui.Command("AUTO", node.AutoFillCommand, tooltip: "Ask the model selected in the chat to fill the comment, synonyms, tags and 3 example sentences", primary: true);
        var cancel = Ui.Command("CANCEL", node.AutoFillCancelCommand);
        auto.MinWidth = cancel.MinWidth = 80;
        autoProgress.IsVisible = cancel.IsVisible = false;
        if (node.AutoFillCommand is INotifyPropertyChanged running)
            Ui.When(auto, running, () =>
            {
                var on = node.AutoFillCommand.IsRunning;
                autoProgress.IsVisible = cancel.IsVisible = on;
                auto.IsVisible = !on;
            }, nameof(IAsyncRelayCommand.IsRunning));

        // Entity type (the NER types of config.yml; "" = no type) and symbol.
        var types = Ui.Combo(vm.Owner.EntityTypeOptions, node, nameof(node.EntityType), t => string.IsNullOrEmpty(t as string) ? "(none)" : (string)t!);
        types.MinWidth = 80;
        var confidentiality = Confidentiality(vm.Owner, node);
        var ontologyClass = Ui.Check("Ontology class", node, nameof(node.IsOntologyClass));
        ToolTip.SetTip(ontologyClass, "Checked: an ontology class · unchecked: an instance");
        ontologyClass.Margin = new Thickness(0, 18, 0, 0);

        var leftColumn = Ui.Column(8,
            KnowledgeUi.Field("Name", KnowledgeUi.FillRow(8, [], name, autoProgress, auto, cancel)),
            KnowledgeUi.Pair(12, KnowledgeUi.Field("Entity type", types), KnowledgeUi.Field("Symbol", Ui.Edit(node, nameof(node.Symbol)))),
            // Optional period over which the entity holds (exists, is valid).
            KnowledgeUi.Pair(12, KnowledgeUi.Field("Start date", DateBox.Bound(node, nameof(node.StartDate), "Optional: the date the entity holds from")),
                KnowledgeUi.Field("End date", DateBox.Bound(node, nameof(node.EndDate), "Optional: the date the entity holds until"))),
            KnowledgeUi.Pair(12, KnowledgeUi.Field("Confidentiality (lowest to highest)", confidentiality), ontologyClass),
            Ui.Bound(node, nameof(node.MinimumConfidentialityLabel), Ui.Muted, 11, wrap: true).Visible(node, nameof(node.HasMinimumConfidentiality)),
            KnowledgeUi.Field("Summary", Ui.Edit(node, nameof(node.Summary), multiline: true, height: 52)),
            KnowledgeUi.Field("Full description", Ui.Edit(node, nameof(node.Description), multiline: true, height: 130)),
            KnowledgeUi.Field("Example sentences (one per line)", Ui.Edit(node, nameof(node.SentencesExemples), multiline: true, height: 110)),
            KnowledgeUi.Field("Comment", Ui.Edit(node, nameof(node.Comment), multiline: true, height: 60)));

        var emotions = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,*"), ColumnSpacing = 8, RowSpacing = 6 };
        var index = 0;
        foreach (var (label, property) in new[]
                 {
                     ("Angry", nameof(node.AngryLevel)), ("Sad", nameof(node.SadLevel)),
                     ("Happy", nameof(node.HappyLevel)), ("Fear", nameof(node.FearLevel)),
                     ("Surprise", nameof(node.SurpriseLevel)), ("Disgust", nameof(node.DisgustLevel))
                 })
        {
            if (index % 2 == 0)
                emotions.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var text = Ui.Label(label, Ui.Text, 12);
            var spin = new NumericUpDown { Minimum = -1000, Maximum = 1000, Increment = 1, FormatString = "0", MinWidth = 70 }
                .Bind(NumericUpDown.ValueProperty, node, property, BindingMode.TwoWay);
            Grid.SetRow(text, index / 2);
            Grid.SetRow(spin, index / 2);
            Grid.SetColumn(text, index % 2 * 2);
            Grid.SetColumn(spin, index % 2 * 2 + 1);
            emotions.Children.Add(text);
            emotions.Children.Add(spin);
            index++;
        }

        // The title, the tags, the events and the signals of the node (see MetaTags: upper case, '-' for the spaces).
        var rightColumn = Ui.Column(8,
            KnowledgeUi.Field("Title", Ui.Edit(node, nameof(node.Title), "Title of the node (besides its name)")),
            KnowledgeUi.Pair(12, KnowledgeUi.Field("Synonyms (one per line)", Ui.Edit(node, nameof(node.Synonyms), multiline: true, height: 90)),
                KnowledgeUi.Field("Tags (one per line)", Ui.Edit(node, nameof(node.Tags), MetaTags.TagsHint, multiline: true, height: 90))),
            KnowledgeUi.Pair(12, KnowledgeUi.Field("Events (one per line)", Ui.Edit(node, nameof(node.Events), MetaTags.EventsHint, multiline: true, height: 60)),
                KnowledgeUi.Field("Signals (one per line)", Ui.Edit(node, nameof(node.Signals), MetaTags.SignalsHint, multiline: true, height: 60))),
            KnowledgeUi.Pair(12, KnowledgeUi.Field("Related entities (one per line)", Ui.Edit(node, nameof(node.RelatedEntities), multiline: true, height: 60)),
                KnowledgeUi.Field("Related links (one per line)", Ui.Edit(node, nameof(node.RelatedLinks), multiline: true, height: 60))),
            KnowledgeUi.Field("Emotion levels", emotions));

        // Two columns side by side; one under the other when the window is narrow.
        var columns = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
        columns.Children.Add(leftColumn);
        columns.Children.Add(rightColumn);
        void Arrange(double width)
        {
            var wide = width >= 700;
            columns.ColumnDefinitions = wide ? new ColumnDefinitions("*,*") : new ColumnDefinitions("*");
            columns.RowDefinitions = wide ? new RowDefinitions("Auto") : new RowDefinitions("Auto,Auto");
            Grid.SetColumn(rightColumn, wide ? 1 : 0);
            Grid.SetRow(rightColumn, wide ? 0 : 1);
        }
        Arrange(1000);
        var scroll = Ui.Scroll(new Border { Padding = new Thickness(12, 4, 14, 12), Child = columns });
        scroll.SizeChanged += (_, e) => Arrange(e.NewSize.Width);
        return Ui.Dock((header, Dock.Top), (KnowledgeUi.Separator(), Dock.Top), (scroll, Dock.Top));
    }

    /// <summary>The confidentiality levels (their label), bound to the value of the level of <paramref name="node"/>.</summary>
    private static ComboBox Confidentiality(OntologyWindowViewModel owner, OntologyNodeViewModel node)
    {
        var list = owner.ConfidentialityOptions;
        var combo = new ComboBox
        {
            ItemsSource = list,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 80,
            ItemTemplate = new FuncDataTemplate<object?>((option, _) => new TextBlock { Text = option is null ? string.Empty : KnowledgeUi.Get(option, "Label")?.ToString() })
        };
        var updating = false;
        Ui.When(combo, node, () =>
        {
            updating = true;
            combo.SelectedItem = null;
            foreach (var option in list)
                if (option.Value == node.Confidentiality)
                    combo.SelectedItem = option;
            updating = false;
        }, nameof(node.Confidentiality));
        combo.SelectionChanged += (_, _) =>
        {
            if (!updating && combo.SelectedItem is { } selected && KnowledgeUi.Get(selected, "Value") is string value)
                node.Confidentiality = value;
        };
        return combo;
    }
}
