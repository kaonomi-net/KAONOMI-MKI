using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Knowledge;

/// <summary>
/// The Entity window of the ontology (port of <c>EntityWindow.axaml</c>), one per node opened: on the left a search on
/// every node of the ontology (Enter searches, Esc clears; a hit opens in the editor and is selected in the tree); on
/// the right the editor of the node: its name, path, id, parent, virtual parents and dates, its status, Clone, Add
/// child, Discard changes and Save (Ctrl+S / ⌘S); every field: name with Auto (fills it with the model of the chat) and
/// Cancel, entity type, symbol, confidentiality (with the minimum given by the parent), ontology class, description,
/// example sentences, comment, synonyms, tags, related entities and links, the six emotion levels, application, user,
/// machine.
/// </summary>
[ToolView(typeof(EntityWindowViewModel), "Entity", "🧩", 1150, 780, PerObject = true)]
public sealed class EntityView : IToolView
{
    private sealed class NameBox
    {
        public LineEdit? Edit;
    }

    private static readonly ConditionalWeakTable<EntityWindowViewModel, NameBox> Names = new();

    /// <summary>Focuses the name of the node shown by <paramref name="viewModel"/> and selects it (as <c>ShowNode</c>).</summary>
    public static void FocusName(EntityWindowViewModel viewModel) => Callable.From(() =>
    {
        if (Names.TryGetValue(viewModel, out var box) && box.Edit is { } edit && GodotObject.IsInstanceValid(edit) && edit.IsInsideTree())
        {
            edit.GrabFocus();
            edit.SelectAll();
        }
    }).CallDeferred();

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (EntityWindowViewModel)viewModel;
        Names.AddOrUpdate(vm, new NameBox());

        // The search on the left.
        var term = Ui.Line(vm, nameof(vm.Term), "Search an entity · Enter · Esc: clear");
        term.TextSubmitted += _ => Bind.Run(vm.SearchCommand);
        term.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
            {
                Bind.Run(vm.ClearSearchCommand);
                term.AcceptEvent();
            }
        };
        var search = Ui.Command("SEARCH", vm.SearchCommand, primary: true);
        search.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var results = new VBoxContainer();
        results.AddThemeConstantOverride("separation", 2);
        Bind.Items(results, vm.Results, hit => HitRow(vm, hit));
        var left = Ui.Column(6,
            Eve.Eve.Caption("Search"),
            term,
            Ui.Row(6, search, Ui.Command("CLEAR", vm.ClearSearchCommand)),
            Ui.Label(vm, nameof(vm.SearchStatus), Eve.Eve.Muted, 11, wrap: true),
            Ui.FillHeight(Ui.Scroll(results)));
        left.CustomMinimumSize = new Vector2(240, 0);

        // The editor on the right.
        var editor = Ui.Swap(vm, nameof(vm.Node), value => value is OntologyNodeViewModel node
            ? Editor(vm, node)
            : Ui.Placeholder("No entity · search one on the left, or double-click a node of the ontology tree"));

        var page = Ui.SplitH(Ui.Margin(left, 4, 4, 8, 4), editor, 280);
        KnowledgeUi.Shortcuts(page, key =>
            KnowledgeUi.IsCommand(key, Key.S) && KnowledgeUi.TryRun(vm.Node?.SaveCommand));
        FocusName(vm);
        return page;
    }

    /// <summary>A hit of the search: its name, type, a deleted badge and its place in the tree; a click opens it.</summary>
    private static Control HitRow(EntityWindowViewModel vm, EntitySearchHit hit)
    {
        var button = new Button { ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 44) };
        button.AddThemeStyleboxOverride("normal", Eve.Eve.Box(Eve.Eve.Sunken(0.15f), Eve.Eve.Edge with { A = 0.15f }, 6));
        var name = Eve.Eve.Text_(hit.Node.DisplayName, Eve.Eve.TextBright, 13);
        name.ClipText = true;
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var type = Eve.Eve.Text_(hit.Node.EntityType, Eve.Eve.Muted, 11);
        type.Visible = hit.Node.HasEntityType;
        var path = Eve.Eve.Text_(hit.PathLabel, Eve.Eve.Muted, 10);
        path.ClipText = true;
        var content = Ui.Column(1, Ui.Row(6, name, type, Ui.Badge("deleted", Eve.Eve.Red, hit.Node, nameof(hit.Node.IsDeleted))), path);
        var margin = Ui.Margin(content, 8, 4, 8, 4);
        Ignore(margin);
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        button.AddChild(margin);
        button.Pressed += () => vm.SelectedResult = hit;
        Bind.When(button, vm, () => button.SetPressedNoSignal(ReferenceEquals(vm.SelectedResult, hit)), nameof(vm.SelectedResult));
        return button;
    }

    private static void Ignore(Control control)
    {
        control.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (var child in control.GetChildren())
            if (child is Control c)
                Ignore(c);
    }

    /// <summary>The editor of <paramref name="node"/>: its header and actions, then every field in two columns.</summary>
    private static Control Editor(EntityWindowViewModel vm, OntologyNodeViewModel node)
    {
        var title = Ui.Label(node, nameof(node.DisplayName), Eve.Eve.TextBright, 18);
        title.AddThemeFontOverride("font", Eve.Eve.Display);
        title.ClipText = true;
        var identity = Ui.Column(1,
            title,
            Clip(Ui.Label(vm, nameof(vm.NodePath), Eve.Eve.Muted, 11)),
            KnowledgeUi.Selectable(node, nameof(node.IdLabel), Eve.Eve.Text, 12),
            KnowledgeUi.Selectable(node, nameof(node.ParentLabel), Eve.Eve.Muted, 11),
            Bind.Visible(KnowledgeUi.Selectable(node, nameof(node.VirtualParentsLabel), Eve.Eve.Muted, 11), node, nameof(node.HasVirtualParents)),
            Clip(Ui.Label(node, nameof(node.DatesLabel), Eve.Eve.Muted, 11)));
        identity.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var dot = Bind.Visible(Eve.Eve.Text_("●", Eve.Eve.Red, 10), node, nameof(node.IsError));
        var status = Ui.Label(node, nameof(node.Status), Eve.Eve.Muted, 12);
        status.ClipText = true;
        status.CustomMinimumSize = new Vector2(120, 0);
        status.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        Bind.When(status, node, () =>
        {
            status.TooltipText = node.Status;
            status.CustomMinimumSize = new Vector2(Mathf.Min(360, status.GetThemeFont("font").GetStringSize(node.Status, fontSize: 12).X + 4), 0);
        }, nameof(node.Status));
        // The status and the actions; a narrow window puts the actions under the identity.
        var actions = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = FlowContainer.AlignmentMode.End };
        actions.AddThemeConstantOverride("h_separation", 6);
        actions.AddThemeConstantOverride("v_separation", 6);
        actions.AddChild(Ui.Row(6, dot, status));
        // 📝: an annotation of the entity (also a right click on its identity).
        AnnotationTarget Target() => node.AnnotationTarget;
        var annotate = Ui.Action("📝 ANNOTATE", () => Data.Annotations.Open(identity, Target()), tooltip: "Write an annotation of this entity, with its tags (save the entity first)");
        Bind.When(annotate, node, () => annotate.Disabled = node.IsNew, nameof(node.IsNew));
        identity.MouseFilter = Control.MouseFilterEnum.Pass;
        identity.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } right && !node.IsNew)
                Data.Annotations.ShowMenu(identity, right.Position, Target());
        };
        actions.AddChild(annotate);
        actions.AddChild(Ui.Command("CLONE", node.CloneCommand, tooltip: "Add an unsaved copy of this node next to it (save the node first)"));
        actions.AddChild(Ui.Command("ADD CHILD", node.AddChildCommand, tooltip: "Add a child under this node (save the node first)"));
        actions.AddChild(Ui.Command("DISCARD CHANGES", node.RevertCommand));
        actions.AddChild(Ui.Command("SAVE", node.SaveCommand, primary: true, tooltip: "Ctrl+S / ⌘S"));
        identity.CustomMinimumSize = new Vector2(200, 0);
        var header = new HFlowContainer();
        header.AddThemeConstantOverride("v_separation", 6);
        header.AddChild(identity);
        header.AddChild(actions);

        // Name, and Auto (the model selected in the chat fills the comment, synonyms, tags, sentences); Cancel while it runs.
        var name = Ui.Line(node, nameof(node.Name));
        if (Names.TryGetValue(vm, out var box))
            box.Edit = name;
        var autoProgress = new ProgressBar { Indeterminate = true, ShowPercentage = false, CustomMinimumSize = new Vector2(40, 4), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        var auto = Ui.Command("AUTO", node.AutoFillCommand, primary: true,
            tooltip: "Ask the model selected in the chat to fill the comment, synonyms, tags and 3 example sentences");
        var cancel = Ui.Command("CANCEL", node.AutoFillCancelCommand);
        auto.CustomMinimumSize = cancel.CustomMinimumSize = new Vector2(80, 0);
        if (node.AutoFillCommand is INotifyPropertyChanged running)
            Bind.When(auto, running, () =>
            {
                var on = node.AutoFillCommand.IsRunning;
                autoProgress.Visible = cancel.Visible = on;
                auto.Visible = !on;
            }, nameof(IAsyncRelayCommand.IsRunning));

        // Entity type (the NER types of config.yml; "" = no type) and symbol.
        var types = Bind.Options(new OptionButton { ClipText = true, CustomMinimumSize = new Vector2(80, 0) }, vm.Owner.EntityTypeOptions, node, nameof(node.EntityType), t => string.IsNullOrEmpty(t) ? "(none)" : t);
        var confidentiality = Confidentiality(vm.Owner, node);

        var leftColumn = Ui.Column(8,
            Ui.Field("Name", Ui.Row(8, name, autoProgress, auto, cancel)),
            Pair(Ui.Field("Entity type", types), Ui.Field("Symbol", Ui.Line(node, nameof(node.Symbol)))),
            // Optional period over which the entity holds (exists, is valid).
            Pair(Ui.Field("Start date", DateBox.Bound(node, nameof(node.StartDate), "Optional: the date the entity holds from")),
                Ui.Field("End date", DateBox.Bound(node, nameof(node.EndDate), "Optional: the date the entity holds until"))),
            Pair(Ui.Field("Confidentiality (lowest to highest)", confidentiality),
                Ui.Margin(Ui.Check("Ontology class", node, nameof(node.IsOntologyClass), "Checked: an ontology class · unchecked: an instance"), 0, 18, 0, 0)),
            Bind.Visible(Ui.Label(node, nameof(node.MinimumConfidentialityLabel), Eve.Eve.Muted, 11, wrap: true), node, nameof(node.HasMinimumConfidentiality)),
            Ui.Field("Description", Ui.Lines(node, nameof(node.Description), 90)),
            Ui.Field("Example sentences (one per line)", Ui.Lines(node, nameof(node.SentencesExemples), 110)),
            Ui.Field("Comment", Ui.Lines(node, nameof(node.Comment), 60)));

        var emotions = Ui.Grid(4, 8, 6);
        foreach (var (label, property) in new[]
                 {
                     ("Angry", nameof(node.AngryLevel)), ("Sad", nameof(node.SadLevel)),
                     ("Happy", nameof(node.HappyLevel)), ("Fear", nameof(node.FearLevel)),
                     ("Surprise", nameof(node.SurpriseLevel)), ("Disgust", nameof(node.DisgustLevel))
                 })
        {
            emotions.AddChild(Eve.Eve.Text_(label, Eve.Eve.Text, 12));
            var spin = new SpinBox { CustomMinimumSize = new Vector2(70, 0), MinValue = -1000, MaxValue = 1000, Step = 1, Rounded = true, AllowGreater = true, AllowLesser = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            emotions.AddChild(Bind.Number(spin, node, property));
        }

        // The title, the tags, the events and the signals of the node (see MetaTags: upper case, '-' for the spaces).
        var rightColumn = Ui.Column(8,
            Ui.Field("Title", Ui.Line(node, nameof(node.Title), "Title of the node (besides its name)")),
            Pair(Ui.Field("Synonyms (one per line)", Ui.Lines(node, nameof(node.Synonyms), 90)),
                Ui.Field("Tags (one per line)", Ui.Lines(node, nameof(node.Tags), 90, MetaTags.TagsHint))),
            Pair(Ui.Field("Events (one per line)", Ui.Lines(node, nameof(node.Events), 60, MetaTags.EventsHint)),
                Ui.Field("Signals (one per line)", Ui.Lines(node, nameof(node.Signals), 60, MetaTags.SignalsHint))),
            Pair(Ui.Field("Related entities (one per line)", Ui.Lines(node, nameof(node.RelatedEntities), 60)),
                Ui.Field("Related links (one per line)", Ui.Lines(node, nameof(node.RelatedLinks), 60))),
            Ui.Field("Emotion levels", emotions));

        // Two columns side by side; one under the other when the window is narrow.
        var columns = new HFlowContainer();
        columns.AddThemeConstantOverride("h_separation", 24);
        columns.AddThemeConstantOverride("v_separation", 8);
        foreach (var column in new[] { leftColumn, rightColumn })
        {
            column.CustomMinimumSize = new Vector2(330, 0);
            column.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            columns.AddChild(column);
        }
        return Ui.Column(8, Ui.Margin(header, 12, 6, 12, 2), Eve.Eve.Separator(), Ui.FillHeight(Ui.Scroll(Ui.Margin(columns, 12, 4, 14, 12))));
    }

    private static Label Clip(Label label)
    {
        label.ClipText = true;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        return label;
    }

    /// <summary>Two fields side by side, sharing the width.</summary>
    private static HBoxContainer Pair(Control left, Control right) => Ui.Row(12, Ui.Fill(left), Ui.Fill(right));

    /// <summary>The confidentiality levels (their label), bound to the value of the level of <paramref name="node"/>.</summary>
    private static OptionButton Confidentiality(OntologyWindowViewModel owner, OntologyNodeViewModel node)
    {
        var options = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, ClipText = true, CustomMinimumSize = new Vector2(80, 0) };
        var list = owner.ConfidentialityOptions;
        foreach (var option in list)
            options.AddItem(option.Label);
        var updating = false;
        Bind.When(options, node, () =>
        {
            updating = true;
            var index = -1;
            for (var i = 0; i < list.Count; i++)
                if (list[i].Value == node.Confidentiality)
                    index = i;
            options.Selected = index;
            updating = false;
        }, nameof(node.Confidentiality));
        options.ItemSelected += index =>
        {
            if (!updating && index >= 0 && index < list.Count)
                node.Confidentiality = list[(int)index].Value;
        };
        return options;
    }
}
