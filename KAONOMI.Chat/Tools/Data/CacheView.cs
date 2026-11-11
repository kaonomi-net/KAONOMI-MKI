using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// /cache: the cache builders of the server (CacheBuilder: several prompts and the completions that answer them,
/// written by hand), as in the Godot chat. A search (Enter or Search; empty: the newest ones) and NEW at the top; the
/// documents on the left (date, language, model, counts, badges deleted / new / modified, title or first prompt, first
/// completion); the selected one editable on the right: title, language, model, the prompts and the completions (two
/// sections that fold; one box each, + ADD, ✕ removes), comment, tags, events, signals, related and excluded entities,
/// deleted; with Clone, Discard changes and Save (the related entities and the tags left empty are detected by the
/// server on save). The model selected in the chat helps: VARIATIONS… asks how many in a popup and adds that many
/// wordings of the first one (a waiting animation meanwhile); EVALUATE… opens a window that scrolls where every prompt
/// is marked with every completion; TRANSLATE… opens a popup where the language is chosen, then a new cache builder
/// window with the translation to finish. PUBLISH writes every pair of the saved document in the index "cache" of the
/// server; TO SEMANTIC CACHE sends them to the semantic cache of the LLM gateway.
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
        var newButton = DataUi.Command("NEW", vm.NewCommand, "Create a new document: prompts and their completions");
        var toolbar = DataUi.Toolbar(DataUi.SearchBar(vm, nameof(vm.Term), vm.SearchCommand, "Search the prompts and completions… (empty: the newest)", newButton));

        // The documents found.
        var left = DataUi.ScrollList(vm.Results, entry => ResultRow(vm, entry), "No document", spacing: 2);
        left.Margin = new Thickness(8, 8, 0, 8);

        // The translation: its popup, then a new window of the tool for the translated document.
        Control? popup = null;
        void Translate(CacheEntryViewModel entry)
        {
            if (popup is not null)
                shell.Close(popup);
            vm.SelectedResult = entry;
            popup = TranslatePopup(vm, entry);
            shell.OpenControl($"cache-translate-{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(vm)}", "Translate the document", "🌐", popup, new Size(460, 260));
        }
        vm.TranslationReady += (window, language) =>
        {
            if (popup is not null)
                shell.Close(popup);
            popup = null;
            shell.Open(window, $"cache-translated-{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(window)}", $"Cache builder · {language.Name}");
        };

        // The selected document.
        var editor = DataUi.Swap(vm, nameof(vm.SelectedResult), value => value is CacheEntryViewModel entry ? Editor(vm, entry, shell, Translate) : DataUi.Placeholder("Select a document, or NEW"));
        editor.Margin = new Thickness(4, 8, 8, 8);

        return Ui.Dock(
            (toolbar, Dock.Top),
            (DataUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError)), Dock.Bottom),
            (DataUi.SplitH(left, editor, 340, 260), Dock.Top));
    }

    /// <summary>A document of the list: its summary, badges, title or first prompt, first completion; a click selects it.</summary>
    private static Control ResultRow(CacheWindowViewModel vm, CacheEntryViewModel entry)
    {
        var content = Ui.Column(2,
            Ui.Row(6, DataUi.Bound(entry, nameof(entry.Summary), Ui.Muted, 10),
                DataUi.Badge("deleted", Ui.Red, entry, nameof(entry.IsDeleted)),
                DataUi.Badge("new", Ui.Green, entry, nameof(entry.IsNew)),
                DataUi.Badge("modified", DataUi.Amber, entry, nameof(entry.IsDirty))),
            DataUi.Bound(entry, nameof(entry.Headline), Ui.TextBright, 13, format: v => DataUi.Trim(v?.ToString(), 120)),
            DataUi.Bound(entry, nameof(entry.CompletionPreview), Ui.Muted, 11, format: v => DataUi.Trim(v?.ToString(), 80)).Visible(entry, nameof(entry.HasCompletion)));
        return DataUi.ClickRow(content, () => vm.SelectedResult = entry, null, vm, nameof(vm.SelectedResult), () => ReferenceEquals(vm.SelectedResult, entry));
    }

    /// <summary>The editor of the selected document: its fields and its actions.</summary>
    private static Control Editor(CacheWindowViewModel vm, CacheEntryViewModel entry, Shell shell, System.Action<CacheEntryViewModel> translate)
    {
        var header = DataUi.Bar([],
            Ui.Column(0, DataUi.Bound(entry, nameof(entry.IdLabel), Ui.Text, 12), DataUi.Bound(entry, nameof(entry.DateLabel), Ui.Muted, 10),
                DataUi.Bound(entry, nameof(entry.Status), Ui.Muted, 11)),
            DataUi.Command("CLONE", vm.CloneCommand, "Create a copy of this document (new id on save)"),
            DataUi.Command("DISCARD CHANGES", entry.RevertCommand),
            DataUi.Command("SAVE", entry.SaveCommand, primary: true),
            DataUi.Action("🌐 TRANSLATE…", () => translate(entry), "Choose a language: the model of the chat translates the title, the prompts and the completions, and a new cache builder window opens with the translation to finish"),
            DataUi.Busy(vm.PublishCommand, 70),
            DataUi.Busy(vm.ToSemanticCacheCommand, 70),
            DataUi.Command("⬆ PUBLISH", vm.PublishCommand, "Write every prompt with every completion in the index \"cache\" of the server (the saved document; the pairs removed since the last publication are withdrawn)"),
            DataUi.Command("🧠 TO SEMANTIC CACHE", vm.ToSemanticCacheCommand, "Add the prompts and their completions to the semantic cache of the LLM gateway (every model): only the entries it does not keep yet are added"));

        // The prompts and the completions, one box each.
        var prompts = DataUi.Items(entry.Prompts, item => TextRow(item, entry.RemovePromptCommand, 60, "A wording of the question", "Remove this prompt"), spacing: 6);
        var completions = DataUi.Items(entry.Completions, item => TextRow(item, entry.RemoveCompletionCommand, 110, "An answer to these prompts", "Remove this completion"), spacing: 6);

        var fields = Ui.Column(8,
            Columns(
                DataUi.Field("Title", DataUi.Line(entry, nameof(entry.Title), "Optional: what this document is about")),
                DataUi.Field("Language", DataUi.Line(entry, nameof(entry.Language), "FR, EN, DE…")),
                DataUi.Field("Model", DataUi.Line(entry, nameof(entry.Model), "user, or the model that wrote the completions"))),
            DataUi.Expander("Prompts (the wordings of the same question)", Ui.Column(6,
                DataUi.Bar([], DataUi.Bound(entry, nameof(entry.CountLabel), Ui.Muted, 11),
                    Waiting(vm, nameof(vm.IsGeneratingPrompts)),
                    DataUi.Command("✨ VARIATIONS…", vm.GeneratePromptsCommand, "The model of the chat writes other wordings of the first prompt (same meaning, same intention); how many is asked first"),
                    DataUi.Command("+ ADD PROMPT", entry.AddPromptCommand, "Add another wording of the question")),
                prompts), _promptsOpen, open => _promptsOpen = open),
            DataUi.Expander("Completions (the answers)", Ui.Column(6,
                DataUi.Bar([], null,
                    Waiting(vm, nameof(vm.IsGeneratingCompletions)),
                    DataUi.Command("✨ VARIATIONS…", vm.GenerateCompletionsCommand, "The model of the chat writes other wordings of the first completion (same meaning, same intention); how many is asked first"),
                    DataUi.Command("+ ADD COMPLETION", entry.AddCompletionCommand, "Add another answer")),
                completions), _completionsOpen, open => _completionsOpen = open),
            DataUi.Separator(),
            DataUi.Bar([DataUi.Caption("Evaluation")], DataUi.Bound(entry, nameof(entry.EvaluationSummary), Ui.Muted, 11),
                DataUi.Action("⚖ EVALUATE…", () => Evaluate(vm, entry, shell), "Open the evaluation: the model of the chat marks every prompt with every completion, 0 to 10 (one call per pair)", primary: true)),
            DataUi.Field("Comment", DataUi.Lines(entry, nameof(entry.Comment), 50)),
            Columns(
                DataUi.Field("Tags (one per line)", DataUi.Lines(entry, nameof(entry.Tags), 60, MetaTags.TagsHint)),
                DataUi.Field("Events (one per line)", DataUi.Lines(entry, nameof(entry.Events), 60, MetaTags.EventsHint)),
                DataUi.Field("Signals (one per line)", DataUi.Lines(entry, nameof(entry.Signals), 60, MetaTags.SignalsHint))),
            Columns(
                DataUi.Field("Related entities (one per line)", DataUi.Lines(entry, nameof(entry.RelatedEntities), 60)),
                DataUi.Field("Excluded entities (one per line)", DataUi.Lines(entry, nameof(entry.ExcludedEntities), 60))),
            DataUi.Check("Deleted", entry, nameof(entry.IsDeleted)));
        fields.Margin = new Thickness(0, 0, 12, 12);
        var separator = DataUi.Separator();
        separator.Margin = new Thickness(0, 8);
        return Ui.Dock((header, Dock.Top), (separator, Dock.Top), (Ui.Scroll(fields), Dock.Top));
    }

    /// <summary>
    /// Opens the evaluation of <paramref name="entry"/> in its own window, which scrolls (the progress, CANCEL,
    /// EVALUATE AGAIN, the summary, then a row per prompt and completion pair), and starts it.
    /// </summary>
    private static void Evaluate(CacheWindowViewModel vm, CacheEntryViewModel entry, Shell shell)
    {
        var cancel = DataUi.Command("CANCEL", vm.CancelWorkCommand, "Stop the evaluation");
        cancel.Visible(vm, nameof(vm.IsWorking));
        var title = DataUi.Text(DataUi.Trim(entry.Headline, 140), Ui.TextBright, 13, wrap: true);
        var bar = DataUi.Bar([DataUi.Busy(vm.EvaluateCommand)], DataUi.Bound(vm, nameof(vm.Status), Ui.Text, 12), cancel,
            DataUi.Command("⚖ EVALUATE AGAIN", vm.EvaluateCommand, "Mark every prompt with every completion again, as they are now", primary: true));
        bar.Margin = new Thickness(0, 8);
        var summary = DataUi.Bound(entry, nameof(entry.EvaluationSummary), Ui.Muted, 11);
        summary.Margin = new Thickness(0, 0, 0, 8);
        var rows = DataUi.Items(entry.Evaluations, EvaluationRow, spacing: 8);
        rows.Margin = new Thickness(0, 0, 12, 12);
        var page = Ui.Dock((title, Dock.Top), (bar, Dock.Top), (summary, Dock.Top), (Ui.Scroll(rows), Dock.Top));
        page.Margin = new Thickness(10);
        shell.OpenControl($"cache-evaluation-{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(entry)}", "Evaluation of the prompts and completions", "⚖",
            page, new Size(820, 620));
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
        var languages = DataUi.Enabled(DataUi.Options(CacheWindowViewModel.Languages, vm, nameof(vm.TranslationLanguage), language => language.Label, 200),
            vm, nameof(vm.IsTranslating), invert: true);

        var bar = new ProgressBar { IsIndeterminate = true, Height = 6, MinHeight = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        var waiting = Ui.Column(6, bar, DataUi.Bound(vm, nameof(vm.Status), Ui.Text, 12, wrap: true)).Visible(vm, nameof(vm.IsTranslating));
        var error = DataUi.Bound(vm, nameof(vm.Status), Ui.Red, 12, wrap: true).Visible(vm, nameof(vm.IsError));

        var page = Ui.Column(10,
            DataUi.Text(DataUi.Trim(entry.Headline, 120), Ui.TextBright, 13, wrap: true),
            DataUi.Field("Translate the title, the prompts and the completions into", languages),
            waiting,
            error,
            DataUi.Bar([], null,
                DataUi.Command("CANCEL", vm.CancelWorkCommand, "Stop the translation").Visible(vm, nameof(vm.IsTranslating)),
                DataUi.Command("🌐 TRANSLATE", vm.TranslateCommand, "Translate with the model of the chat, then open the translation in a new cache builder window", primary: true)));
        page.Margin = new Thickness(12);
        return page;
    }

    /// <summary>While the model writes variations (the bool property <paramref name="running"/>): the waiting animation, what it does, CANCEL.</summary>
    private static Control Waiting(CacheWindowViewModel vm, string running)
    {
        var bar = new ProgressBar { IsIndeterminate = true, Width = 90, MinWidth = 90, Height = 4, MinHeight = 4, VerticalAlignment = VerticalAlignment.Center };
        var text = DataUi.Text("The model is writing…", Ui.Accent, 11);
        text.VerticalAlignment = VerticalAlignment.Center;
        var row = Ui.Row(8, bar, text, DataUi.Command("CANCEL", vm.CancelWorkCommand, "Stop the generation"));
        row.Visible(vm, running);
        return row;
    }

    /// <summary>A pair: its mark (green from 7, red under 4), the prompt, the completion, why.</summary>
    private static Control EvaluationRow(CacheEvaluation evaluation)
    {
        var score = DataUi.Text(evaluation.ScoreLabel, evaluation.IsGood ? Ui.Green : evaluation.IsBad ? Ui.Red : DataUi.Amber, 15, bold: true);
        score.MinWidth = 52;
        score.VerticalAlignment = VerticalAlignment.Top;
        return Ui.Dock((score, Dock.Left), (Ui.Column(0,
            DataUi.Text(DataUi.Trim(evaluation.Prompt, 160), Ui.TextBright, 12, wrap: true),
            DataUi.Text("→ " + DataUi.Trim(evaluation.Completion, 220), Ui.Text, 12, wrap: true),
            DataUi.Text(evaluation.Reason, Ui.Muted, 11, wrap: true)), Dock.Left));
    }

    /// <summary>The controls side by side, the same width each.</summary>
    private static Grid Columns(params Control[] children)
    {
        var grid = new Grid();
        for (var i = 0; i < children.Length; i++)
        {
            if (i > 0)
                grid.ColumnDefinitions.Add(new ColumnDefinition(10, GridUnitType.Pixel));
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            Grid.SetColumn(children[i], i * 2);
            grid.Children.Add(children[i]);
        }
        return grid;
    }

    /// <summary>A prompt or a completion: its text and ✕.</summary>
    private static Control TextRow(CacheTextItemViewModel item, System.Windows.Input.ICommand remove, double height, string placeholder, string tooltip)
    {
        var button = DataUi.Command("✕", remove, tooltip, parameter: item);
        button.VerticalAlignment = VerticalAlignment.Top;
        return DataUi.Bar([], DataUi.Lines(item, nameof(item.Text), height, placeholder), button);
    }
}
