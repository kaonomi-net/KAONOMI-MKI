using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using KAONOMI.ChatGodot.Tools.Dashboards;

namespace KAONOMI.ChatGodot.Tools.Data;

/// <summary>
/// /cache: the cache builders of the server (CacheBuilder: several prompts and the completions that answer them,
/// written by hand). A search (Enter or Search; empty: the newest ones) and NEW at the top; the documents on the left
/// (date, language, model, counts, badges deleted / new / modified, title or first prompt, first completion); the selected one
/// editable on the right: title, language, model, the prompts and the completions (one box each, + ADD, ✕ removes), comment,
/// tags, events, signals, related and excluded entities, deleted; with Clone, Discard changes and Save (the related entities and the tags left empty are detected by the server on save). The model
/// selected in the chat helps: VARIATIONS… (of the prompts, of the completions) asks how many in a popup and adds
/// that many wordings of the first one, with the same meaning and intention; EVALUATE opens a window that scrolls
/// where every prompt is marked with every completion (0 to 10 and why, the worst first; CANCEL stops it). The prompts
/// and the completions are two sections that fold. PUBLISH writes every prompt and completion pair of the saved
/// document in the index "cache" of the server. TRANSLATE… opens a popup where the language is chosen; the model
/// translates the title, the prompts and the completions (a waiting animation meanwhile), then a new cache builder
/// window opens with the translation as a new unsaved document, to finish there. TO SEMANTIC CACHE sends the
/// prompts and their completions to the semantic cache of the LLM gateway, which adds only the entries it does not
/// keep yet.
/// </summary>
[ToolView(typeof(CacheWindowViewModel), "Cache builder", "🗃", 1150, 780)]
public sealed class CacheView : IToolView
{
    /// <summary>The two sections stay unfolded or folded across the selections and the windows.</summary>
    private static bool _promptsOpen = true;
    private static bool _completionsOpen = true;

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (CacheWindowViewModel)viewModel;
        vm.AskAsync = (title, text, initial) => Dialogs.Ask(title, text, initial);
        var newButton = Ui.Command("NEW", vm.NewCommand, tooltip: "Create a new document: prompts and their completions");
        var toolbar = Ui.Toolbar(Ui.SearchBar(vm, nameof(vm.Term), vm.SearchCommand, "Search the prompts and completions… (empty: the newest)", newButton));

        // The documents found.
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        var empty = Ui.Placeholder("No document");
        Bind.Items(list, vm.Results, entry => ResultRow(vm, entry), empty);
        var left = Ui.Column(0, Ui.FillHeight(Ui.Scroll(list)), empty);
        left.CustomMinimumSize = new Vector2(320, 0);

        // The translation: its popup, then a new window of the tool for the translated document.
        Control? popup = null;
        void Translate(CacheEntryViewModel entry)
        {
            if (popup is not null && GodotObject.IsInstanceValid(popup))
                shell.Close(popup);
            vm.SelectedResult = entry;
            popup = TranslatePopup(vm, entry);
            shell.OpenControl($"cache-translate-{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(vm)}", "Translate the document", "🌐", popup, new Vector2(460, 250));
        }
        vm.TranslationReady += (window, language) =>
        {
            if (popup is not null && GodotObject.IsInstanceValid(popup))
                shell.Close(popup);
            popup = null;
            shell.Open(window, $"cache-translated-{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(window)}", $"Cache builder · {language.Name}");
        };

        // The selected document.
        var editor = Ui.Swap(vm, nameof(vm.SelectedResult), value => value is CacheEntryViewModel entry ? Editor(vm, entry, shell, Translate) : Ui.Placeholder("Select a document, or NEW"));

        return Ui.Column(6, toolbar, Ui.SplitH(left, editor, 340), Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError)));
    }

    /// <summary>A document of the list: its summary, badges, title or first prompt, first completion; a click selects it.</summary>
    private static Control ResultRow(CacheWindowViewModel vm, CacheEntryViewModel entry)
    {
        var button = new Button { ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 64), Alignment = HorizontalAlignment.Left };
        button.AddThemeStyleboxOverride("normal", Eve.Eve.Box(Eve.Eve.Sunken(0.15f), Eve.Eve.Edge with { A = 0.15f }, 6));
        var content = Ui.Column(2,
            Ui.Row(6, Ui.Label(entry, nameof(entry.Summary), Eve.Eve.Muted, 10),
                Ui.Badge("deleted", Eve.Eve.Red, entry, nameof(entry.IsDeleted)),
                Ui.Badge("new", Eve.Eve.Green, entry, nameof(entry.IsNew)),
                Ui.Badge("modified", Eve.Eve.Amber, entry, nameof(entry.IsDirty))),
            Ui.Label(entry, nameof(entry.Headline), Eve.Eve.TextBright, 13, format: v => Trim(v?.ToString(), 120)),
            Bind.Visible(Ui.Label(entry, nameof(entry.CompletionPreview), Eve.Eve.Muted, 11, format: v => Trim(v?.ToString(), 80)), entry, nameof(entry.HasCompletion)));
        content.MouseFilter = Control.MouseFilterEnum.Ignore;
        Ignore(content);
        var margin = Ui.Margin(content, 8, 6, 8, 6);
        margin.MouseFilter = Control.MouseFilterEnum.Ignore;
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        button.AddChild(margin);
        button.Pressed += () => vm.SelectedResult = entry;
        Bind.When(button, vm, () => button.SetPressedNoSignal(ReferenceEquals(vm.SelectedResult, entry)), nameof(vm.SelectedResult));
        return button;
    }

    private static void Ignore(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is Control control)
                control.MouseFilter = Control.MouseFilterEnum.Ignore;
            Ignore(child);
        }
    }

    private static string Trim(string? text, int length)
    {
        var line = (text ?? string.Empty).Replace('\n', ' ').Trim();
        return line.Length > length ? line[..length] + "…" : line;
    }

    /// <summary>The editor of the selected document: its fields, Clone, Discard changes, Save.</summary>
    private static Control Editor(CacheWindowViewModel vm, CacheEntryViewModel entry, Shell shell, System.Action<CacheEntryViewModel> translate)
    {
        var header = Ui.Row(8,
            Ui.Fill(Ui.Column(0, Ui.Label(entry, nameof(entry.IdLabel), Eve.Eve.Text, 12), Ui.Label(entry, nameof(entry.DateLabel), Eve.Eve.Muted, 10))),
            Ui.Label(entry, nameof(entry.Status), Eve.Eve.Muted, 11),
            Ui.Command("CLONE", vm.CloneCommand, tooltip: "Create a copy of this document (new id on save)"),
            Ui.Command("DISCARD CHANGES", entry.RevertCommand),
            Ui.Command("SAVE", entry.SaveCommand, primary: true),
            Ui.Action("🌐 TRANSLATE…", () => translate(entry), tooltip: "Choose a language: the model of the chat translates the title, the prompts and the completions, and a new cache builder window opens with the translation to finish"),
            DashUi.Busy(vm.PublishCommand, 70),
            DashUi.Busy(vm.ToSemanticCacheCommand, 70),
            Ui.Command("⬆ PUBLISH", vm.PublishCommand, tooltip: "Write every prompt with every completion in the index \"cache\" of the server (the saved document; the pairs removed since the last publication are withdrawn)"),
            Ui.Command("🧠 TO SEMANTIC CACHE", vm.ToSemanticCacheCommand, tooltip: "Add the prompts and their completions to the semantic cache of the LLM gateway (every model): only the entries it does not keep yet are added"));

        var language = Ui.Field("Language", Ui.Line(entry, nameof(entry.Language), "FR, EN, DE…"));
        language.CustomMinimumSize = new Vector2(110, 0);
        var top = Ui.Grid(3, 12, 6);
        top.AddChild(Ui.Fill(Ui.Field("Title", Ui.Line(entry, nameof(entry.Title), "Optional: what this document is about"))));
        top.AddChild(language);
        top.AddChild(Ui.Fill(Ui.Field("Model", Ui.Line(entry, nameof(entry.Model), "user, or the model that wrote the completions"))));

        // The prompts and the completions, one box each.
        var prompts = Ui.Column(6);
        Bind.Items(prompts, entry.Prompts, item => TextRow(item, entry.RemovePromptCommand, 60, "A wording of the question", "Remove this prompt"));
        var completions = Ui.Column(6);
        Bind.Items(completions, entry.Completions, item => TextRow(item, entry.RemoveCompletionCommand, 110, "An answer to these prompts", "Remove this completion"));

        var entities = Ui.Grid(2, 12, 6);
        entities.AddChild(Ui.Fill(Ui.Field("Related entities (one per line)", Ui.Lines(entry, nameof(entry.RelatedEntities), 60))));
        entities.AddChild(Ui.Fill(Ui.Field("Excluded entities (one per line)", Ui.Lines(entry, nameof(entry.ExcludedEntities), 60))));

        var meta = Ui.Grid(3, 12, 6);
        meta.AddChild(Ui.Fill(Ui.Field("Tags (one per line)", Ui.Lines(entry, nameof(entry.Tags), 60, MetaTags.TagsHint))));
        meta.AddChild(Ui.Fill(Ui.Field("Events (one per line)", Ui.Lines(entry, nameof(entry.Events), 60, MetaTags.EventsHint))));
        meta.AddChild(Ui.Fill(Ui.Field("Signals (one per line)", Ui.Lines(entry, nameof(entry.Signals), 60, MetaTags.SignalsHint))));

        var fields = Ui.Column(8,
            top,
            DashUi.Expander("Prompts (the wordings of the same question)", Ui.Column(6,
                Ui.Row(8, Ui.Fill(Ui.Label(entry, nameof(entry.CountLabel), Eve.Eve.Muted, 11)),
                    Waiting(vm, nameof(vm.IsGeneratingPrompts)),
                    Ui.Command("✨ VARIATIONS…", vm.GeneratePromptsCommand, tooltip: "The model of the chat writes other wordings of the first prompt (same meaning, same intention); how many is asked first"),
                    Ui.Command("+ ADD PROMPT", entry.AddPromptCommand, tooltip: "Add another wording of the question")),
                prompts), _promptsOpen, open => _promptsOpen = open),
            DashUi.Expander("Completions (the answers)", Ui.Column(6,
                Ui.Row(8, Ui.Fill(new Control()),
                    Waiting(vm, nameof(vm.IsGeneratingCompletions)),
                    Ui.Command("✨ VARIATIONS…", vm.GenerateCompletionsCommand, tooltip: "The model of the chat writes other wordings of the first completion (same meaning, same intention); how many is asked first"),
                    Ui.Command("+ ADD COMPLETION", entry.AddCompletionCommand, tooltip: "Add another answer")),
                completions), _completionsOpen, open => _completionsOpen = open),
            Eve.Eve.Separator(),
            Ui.Row(8, Eve.Eve.Caption("Evaluation"), Ui.Fill(Ui.Label(entry, nameof(entry.EvaluationSummary), Eve.Eve.Muted, 11)),
                Ui.Action("⚖ EVALUATE…", () => Evaluate(vm, entry, shell), primary: true, tooltip: "Open the evaluation: the model of the chat marks every prompt with every completion, 0 to 10 (one call per pair)")),
            Ui.Field("Comment", Ui.Lines(entry, nameof(entry.Comment), 50)),
            meta,
            entities,
            Ui.Check("Deleted", entry, nameof(entry.IsDeleted)));
        return Ui.Column(8, header, Eve.Eve.Separator(), Ui.FillHeight(Ui.Scroll(Ui.Margin(fields, 0, 0, 12, 12))));
    }

    /// <summary>
    /// Opens the evaluation of <paramref name="entry"/> in its own window, which scrolls (the progress, CANCEL,
    /// EVALUATE AGAIN, the summary, then a row per prompt and completion pair), and starts it.
    /// </summary>
    private static void Evaluate(CacheWindowViewModel vm, CacheEntryViewModel entry, Shell shell)
    {
        var cancel = Ui.Command("CANCEL", vm.CancelWorkCommand, tooltip: "Stop the evaluation");
        Bind.Visible(cancel, vm, nameof(vm.IsWorking));
        var progress = Ui.Label(vm, nameof(vm.Status), Eve.Eve.Text, 12);
        progress.ClipText = true;
        progress.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var rows = Ui.Column(8);
        Bind.Items(rows, entry.Evaluations, EvaluationRow);
        var page = Ui.Column(8,
            Eve.Eve.Text_(Trim(entry.Headline, 140), Eve.Eve.TextBright, 13, wrap: true),
            Ui.Row(8, DashUi.Busy(vm.EvaluateCommand), Ui.Fill(progress), cancel,
                Ui.Command("⚖ EVALUATE AGAIN", vm.EvaluateCommand, primary: true, tooltip: "Mark every prompt with every completion again, as they are now")),
            Ui.Label(entry, nameof(entry.EvaluationSummary), Eve.Eve.Muted, 11),
            Eve.Eve.Separator(),
            Ui.FillHeight(Ui.Scroll(Ui.Margin(rows, 0, 0, 12, 12))));
        shell.OpenControl($"cache-evaluation-{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(entry)}", "Evaluation of the prompts and completions", "⚖",
            Ui.Margin(page, 10, 10, 10, 10), new Vector2(820, 620));
        vm.SelectedResult = entry;
        if (vm.EvaluateCommand.CanExecute(null))
            vm.EvaluateCommand.Execute(null);
    }

    /// <summary>
    /// The popup of the translation: the language (a list), TRANSLATE; while the model works, the waiting animation,
    /// what it is doing and CANCEL.
    /// </summary>
    private static Control TranslatePopup(CacheWindowViewModel vm, CacheEntryViewModel entry)
    {
        var languages = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        for (var i = 0; i < CacheWindowViewModel.Languages.Count; i++)
        {
            languages.AddItem(CacheWindowViewModel.Languages[i].Label);
            if (CacheWindowViewModel.Languages[i] == vm.TranslationLanguage)
                languages.Selected = i;
        }
        languages.ItemSelected += index => vm.TranslationLanguage = CacheWindowViewModel.Languages[(int)index];
        Bind.When(languages, vm, () => languages.Disabled = vm.IsTranslating, nameof(vm.IsTranslating));

        var bar = new ProgressBar { Indeterminate = true, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 6), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var progress = Ui.Label(vm, nameof(vm.Status), Eve.Eve.Text, 12, wrap: true);
        var waiting = Bind.Visible(Ui.Column(6, bar, progress), vm, nameof(vm.IsTranslating));
        var error = Bind.Visible(Ui.Label(vm, nameof(vm.Status), Eve.Eve.Red, 12, wrap: true), vm, nameof(vm.IsError));

        var page = Ui.Column(10,
            Eve.Eve.Text_(Trim(entry.Headline, 120), Eve.Eve.TextBright, 13, wrap: true),
            Ui.Field("Translate the title, the prompts and the completions into", languages),
            waiting,
            error,
            Ui.Row(8, Ui.Fill(new Control()),
                Bind.Visible(Ui.Command("CANCEL", vm.CancelWorkCommand, tooltip: "Stop the translation"), vm, nameof(vm.IsTranslating)),
                Ui.Command("🌐 TRANSLATE", vm.TranslateCommand, primary: true, tooltip: "Translate with the model of the chat, then open the translation in a new cache builder window")));
        return Ui.Margin(page, 12, 12, 12, 12);
    }

    /// <summary>While the model writes the variations of (the bool property <paramref name="running"/>): the waiting animation, what it does, CANCEL.</summary>
    private static Control Waiting(CacheWindowViewModel vm, string running)
    {
        var bar = new ProgressBar { Indeterminate = true, ShowPercentage = false, CustomMinimumSize = new Vector2(90, 4), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        var row = Ui.Row(8, bar, Eve.Eve.Text_("The model is writing…", Eve.Eve.Cyan, 11),
            Ui.Command("CANCEL", vm.CancelWorkCommand, tooltip: "Stop the generation"));
        return Bind.Visible(row, vm, running);
    }

    /// <summary>A pair: its mark (green from 7, red under 4), the prompt, the completion, why.</summary>
    private static Control EvaluationRow(CacheEvaluation evaluation)
    {
        var color = evaluation.IsGood ? Eve.Eve.Green : evaluation.IsBad ? Eve.Eve.Red : Eve.Eve.Amber;
        var score = Eve.Eve.Text_(evaluation.ScoreLabel, color, 15);
        score.CustomMinimumSize = new Vector2(52, 0);
        score.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        return Ui.Row(8, score, Ui.Fill(Ui.Column(0,
            Eve.Eve.Text_(Trim(evaluation.Prompt, 160), Eve.Eve.TextBright, 12, wrap: true),
            Eve.Eve.Text_("→ " + Trim(evaluation.Completion, 220), Eve.Eve.Text, 12, wrap: true),
            Eve.Eve.Text_(evaluation.Reason, Eve.Eve.Muted, 11, wrap: true))));
    }

    /// <summary>A prompt or a completion: its text and ✕.</summary>
    private static Control TextRow(CacheTextItemViewModel item, System.Windows.Input.ICommand remove, int height, string placeholder, string tooltip)
    {
        var edit = Ui.Lines(item, nameof(item.Text), height, placeholder);
        edit.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var button = Ui.Command("✕", remove, tooltip: tooltip, parameter: () => item);
        button.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        return Ui.Row(6, edit, button);
    }
}
