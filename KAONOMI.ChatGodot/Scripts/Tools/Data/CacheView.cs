using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Data;

/// <summary>
/// /cache: a search on the cache of the completions (Enter or Search), the documents found on the left (date, model,
/// application, badges deleted / new / modified, prompt, comment), the selected one editable on the right (application,
/// model, user, machine, comment, related and excluded entities, deleted, prompt, completion with a Markdown preview),
/// with Clone, Discard changes and Save; New starts a new document.
/// </summary>
[ToolView(typeof(CacheWindowViewModel), "Cache", "🗃", 1100, 720)]
public sealed class CacheView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (CacheWindowViewModel)viewModel;
        var newButton = Ui.Command("NEW", vm.NewCommand, tooltip: "Create a new cache document");
        var toolbar = Ui.Toolbar(Ui.SearchBar(vm, nameof(vm.Term), vm.SearchCommand, "Search the cache…", newButton));

        // The documents found.
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        var empty = Ui.Placeholder("No results");
        Bind.Items(list, vm.Results, entry => ResultRow(vm, entry), empty);
        var left = Ui.Column(0, Ui.FillHeight(Ui.Scroll(list)), empty);
        left.CustomMinimumSize = new Vector2(320, 0);

        // The selected document.
        var editor = Ui.Swap(vm, nameof(vm.SelectedResult), value => value is CacheEntryViewModel entry ? Editor(vm, entry) : Ui.Placeholder("Select a document"));

        var page = Ui.Column(6, toolbar, Ui.SplitH(left, editor, 340), Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError)));
        return page;
    }

    /// <summary>A document of the list: a button showing its summary, badges, prompt and comment; a click selects it.</summary>
    private static Control ResultRow(CacheWindowViewModel vm, CacheEntryViewModel entry)
    {
        var button = new Button { ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 64), Alignment = HorizontalAlignment.Left };
        button.AddThemeStyleboxOverride("normal", Eve.Eve.Box(Eve.Eve.Sunken(0.15f), Eve.Eve.Edge with { A = 0.15f }, 6));
        var content = Ui.Column(2,
            Ui.Row(6, Ui.Label(entry, nameof(entry.Summary), Eve.Eve.Muted, 10),
                Ui.Badge("deleted", Eve.Eve.Red, entry, nameof(entry.IsDeleted)),
                Ui.Badge("new", Eve.Eve.Green, entry, nameof(entry.IsNew)),
                Ui.Badge("modified", Eve.Eve.Amber, entry, nameof(entry.IsDirty))),
            Ui.Label(entry, nameof(entry.Prompt), Eve.Eve.TextBright, 13, format: v => Trim(v?.ToString(), 120)),
            Bind.Visible(Ui.Label(entry, nameof(entry.Comment), Eve.Eve.Muted, 11, format: v => Trim(v?.ToString(), 80)), entry, nameof(entry.HasComment)));
        content.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (var child in content.GetChildren())
            if (child is Control c)
                c.MouseFilter = Control.MouseFilterEnum.Ignore;
        var margin = Ui.Margin(content, 8, 6, 8, 6);
        margin.MouseFilter = Control.MouseFilterEnum.Ignore;
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        button.AddChild(margin);
        button.Pressed += () => vm.SelectedResult = entry;
        Bind.When(button, vm, () => button.SetPressedNoSignal(ReferenceEquals(vm.SelectedResult, entry)), nameof(vm.SelectedResult));
        return button;
    }

    private static string Trim(string? text, int length)
    {
        var line = (text ?? string.Empty).Replace('\n', ' ').Trim();
        return line.Length > length ? line[..length] + "…" : line;
    }

    /// <summary>The editor of the selected document: its fields, Clone, Discard changes, Save.</summary>
    private static Control Editor(CacheWindowViewModel vm, CacheEntryViewModel entry)
    {
        var header = Ui.Row(8,
            Ui.Fill(Ui.Column(0, Ui.Label(entry, nameof(entry.IdLabel), Eve.Eve.Text, 12), Ui.Label(entry, nameof(entry.DateLabel), Eve.Eve.Muted, 10))),
            Ui.Label(entry, nameof(entry.Status), Eve.Eve.Muted, 11),
            Ui.Command("CLONE", vm.CloneCommand, tooltip: "Create a copy of this document (new id on save)"),
            Ui.Command("DISCARD CHANGES", entry.RevertCommand),
            Ui.Command("SAVE", entry.SaveCommand, primary: true));

        var grid = Ui.Grid(2);
        grid.AddChild(Ui.Field("Model", Ui.Line(entry, nameof(entry.Model))));

        var entities = Ui.Grid(2);
        entities.AddChild(Ui.Field("Related entities (one per line)", Ui.Lines(entry, nameof(entry.RelatedEntities), 60)));
        entities.AddChild(Ui.Field("Excluded entities (one per line)", Ui.Lines(entry, nameof(entry.ExcludedEntities), 60)));

        // The completion: as text, or its Markdown preview.
        var completion = Ui.Lines(entry, nameof(entry.Completion), 180);
        var preview = Ui.Markdown(entry, nameof(entry.Completion));
        preview.Visible = false;
        var toggle = new CheckButton { Text = "Markdown preview" };
        toggle.Toggled += on =>
        {
            preview.Visible = on;
            completion.Visible = !on;
        };

        var fields = Ui.Column(8,
            grid,
            Ui.Field("Comment", Ui.Lines(entry, nameof(entry.Comment), 50)),
            entities,
            Ui.Check("Deleted", entry, nameof(entry.IsDeleted)),
            Ui.Field("Prompt", Ui.Lines(entry, nameof(entry.Prompt), 90)),
            Ui.Row(8, Ui.Fill(Eve.Eve.Caption("Completion")), toggle),
            completion,
            preview);
        return Ui.Column(8, header, Eve.Eve.Separator(), Ui.FillHeight(Ui.Scroll(Ui.Margin(fields, 0, 0, 10, 10))));
    }
}
