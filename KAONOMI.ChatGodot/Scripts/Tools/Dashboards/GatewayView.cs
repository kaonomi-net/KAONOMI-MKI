using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using KAONOMI.ChatGodot.Tools.Writing;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;

namespace KAONOMI.ChatGodot.Tools.Dashboards;

/// <summary>
/// /gateway: KAONOMI.LLMGateway, the gateway in front of Ollama. Header: its state (a green dot when it answers), the
/// counters of its cache, ↻ REFRESH and CLEAR CACHE. Left: the settings of the cache and of the routing (SAVE SETTINGS),
/// then the rules of the router, one card each (on, name, priority, model, entities any / all / none, how many requests
/// it routed, 🗑), with ADD RULE and SAVE RULES. 🔎 PHRASES opens a popup to search the phrases of the cache and their
/// possible answers; ⇩ EXPORT saves them to a JSON file, ⇧ IMPORT adds the phrases of such a file. Right: ANALYZE (a prompt and a model: what the gateway would do), the
/// last requests (time, model asked → chosen and its rule, HIT / MISS, entities, prompt) and the cached answers (🗑).
/// </summary>
[ToolView(typeof(GatewayWindowViewModel), "LLM Gateway", "🔀", 1280, 820)]
public sealed class GatewayView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (GatewayWindowViewModel)viewModel;

        var dot = new ColorRect { CustomMinimumSize = new Vector2(12, 12), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        Bind.When(dot, vm, () => dot.Color = vm.IsOnline ? EveStyle.Green : EveStyle.Red, nameof(vm.IsOnline));
        var summary = Ui.Label(vm, nameof(vm.Summary), EveStyle.Text, 12);
        summary.ClipText = true;
        summary.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        summary.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        Bind.When(summary, vm, () => summary.TooltipText = vm.Summary, nameof(vm.Summary));
        var header = Ui.Toolbar(Ui.Row(10,
            EveStyle.Caption("LLM Gateway", EveStyle.TextBright, 14),
            dot, summary,
            Ui.Command("↻ REFRESH", vm.RefreshCommand, tooltip: "Read the state, the settings and the rules again (the changes not saved are lost)"),
            Ui.Action("🔎 PHRASES", () => OpenPhrases(vm, shell), tooltip: "Search the phrases of the cache and their possible answers"),
            Ui.Action("⇩ EXPORT", () => Export(vm, searched: false), tooltip: "Save every phrase of the cache with its possible answers to a JSON file"),
            Ui.Action("⇧ IMPORT", () => Import(vm), tooltip: "Add the phrases of a JSON file to the cache (an export, or phrases written by hand: prompt and answers)"),
            Ui.Command("✕ CLEAR CACHE", vm.ClearCacheCommand, tooltip: "Remove every answer kept by the semantic cache")));

        var left = Ui.Column(10, SettingsCard(vm), RulesCard(vm));
        var right = Ui.Column(10, AnalyzeCard(vm), LogCard(vm), CacheCard(vm));
        var split = Ui.SplitH(Ui.Scroll(Ui.Margin(left, 10, 8, 6, 10)), Ui.Scroll(Ui.Margin(right, 6, 8, 10, 10)), 620);
        split.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        var status = Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsBusy));
        var page = Ui.Column(0, header, split, status);
        page.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        return page;
    }

    private static Control SettingsCard(GatewayWindowViewModel vm)
    {
        var cache = Ui.Grid(2, 12, 6);
        cache.AddChild(Ui.Check("Semantic cache", vm, nameof(vm.CacheEnabled), "Give back the answer of a close prompt already answered"));
        cache.AddChild(Ui.Check("Entity guard", vm, nameof(vm.EntityGuard), "Only when the prompts hold the same numbers, dates, amounts, names, URLs…"));
        cache.AddChild(Field("Embedding model", WritingUi.TextWithChoices(vm, nameof(vm.EmbeddingModel), vm.Models, "nomic-embed-text"),
            "An embedding model of Ollama; when it is not installed, a local embedding of the words is used"));
        cache.AddChild(Field("Similarity (model)", Number(vm, nameof(vm.Threshold), 0.5, 1, 0.01), "The cosine from which a cached answer comes back, with the embedding model"));
        cache.AddChild(Field("Similarity (words)", Number(vm, nameof(vm.LexicalThreshold), 0.5, 1, 0.01), "The same with the local embedding of the words (stricter)"));
        cache.AddChild(Field("TTL (hours)", Number(vm, nameof(vm.TtlHours), 0, 24 * 365, 1), "How long an answer is kept (0: forever)"));
        cache.AddChild(Field("Max answers", Number(vm, nameof(vm.MaxEntries), 10, 1_000_000, 100), "Past it, the answers used the longest ago go"));
        cache.AddChild(Field("Answers before use", Number(vm, nameof(vm.MinAnswers), 1, 100, 1),
            "The answers of the model kept for a prompt before the cache answers it, with one of them at random so that its answers vary (1: always the first answer)"));
        var stats = Ui.Label(vm, nameof(vm.CacheSummary), EveStyle.Cyan, 12, wrap: true);
        var note = Ui.Label(vm, nameof(vm.EmbedderNote), EveStyle.Muted, 11, wrap: true);

        var routing = Ui.Grid(2, 12, 6);
        routing.AddChild(Ui.Check("Router", vm, nameof(vm.RoutingEnabled), "Choose the model by the entities of the prompt"));
        routing.AddChild(Ui.Check("Rules replace the model asked", vm, nameof(vm.OverrideRequested),
            "On: a rule that matches replaces the model asked by the client. Off: only the model auto is routed"));
        routing.AddChild(Field("Default model (auto)", WritingUi.TextWithChoices(vm, nameof(vm.DefaultModel), vm.Models, "gpt-oss:20b"),
            "The model of a request for auto that no rule matches"));

        var save = Ui.Command("SAVE SETTINGS", vm.SaveSettingsCommand, primary: true, tooltip: "Send the settings to the gateway (kept in ~/kaonomi/llmgateway/state.json)");
        return DashUi.Card(Ui.Column(8,
            EveStyle.Caption("Cache", EveStyle.Cyan, 11), cache, stats, note,
            EveStyle.Caption("Routing", EveStyle.Cyan, 11), routing,
            Ui.Row(8, Ui.Spring(), save)), "Settings");
    }

    private static Control RulesCard(GatewayWindowViewModel vm)
    {
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 6);
        Bind.Items(list, vm.Rules, rule => RuleRow(vm, rule), Ui.Placeholder("No rule: every request keeps its model (auto goes to the default model)."));
        var types = Ui.Label(vm, nameof(vm.EntityTypes), EveStyle.Muted, 11, wrap: true);
        var buttons = Ui.Row(8,
            Ui.Command("+ ADD RULE", vm.AddRuleCommand, tooltip: "A new rule at the top"),
            Ui.Spring(),
            Ui.Command("SAVE RULES", vm.SaveRulesCommand, primary: true, tooltip: "Send the rules to the gateway: the next requests are routed with them"));
        return DashUi.Card(Ui.Column(8, types, list, buttons), "Rules of the router (the highest priority first)");
    }

    private static Control RuleRow(GatewayWindowViewModel vm, GatewayRuleViewModel rule)
    {
        var name = Ui.Line(rule, nameof(rule.Name), "name");
        name.CustomMinimumSize = new Vector2(150, 0);
        var hits = Ui.Label(rule, nameof(rule.Hits), EveStyle.Green, 11);
        var top = Ui.Row(8,
            Ui.Check("", rule, nameof(rule.Enabled), "On or off"),
            name,
            Field("Priority", Number(rule, nameof(rule.Priority), -1000, 1000, 1), "The rules are tried from the highest priority"),
            Ui.Fill(Field("Model", WritingUi.TextWithChoices(rule, nameof(rule.Model), vm.Models, "model of Ollama"), "The model of the requests that match")),
            hits,
            Ui.Command("🗑", vm.RemoveRuleCommand, tooltip: "Remove the rule (SAVE RULES to keep it so)", parameter: () => rule));
        var entities = Ui.Grid(3, 8, 4);
        entities.AddChild(Field("Any of", Ui.Line(rule, nameof(rule.Any), "CODE, MATH"), "At least Min of these entities (comma separated; TYPE or TYPE:value)"));
        entities.AddChild(Field("All of", Ui.Line(rule, nameof(rule.All), "LANGUAGE:fr"), "Every one of these entities"));
        entities.AddChild(Field("None of", Ui.Line(rule, nameof(rule.None), "IMAGE"), "None of these entities"));
        foreach (var child in entities.GetChildren())
            ((Control)child).SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var description = Ui.Line(rule, nameof(rule.Description), "what the rule is for");
        description.AddThemeFontSizeOverride("font_size", 11);

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", EveStyle.Box(EveStyle.Field, EveStyle.Edge with { A = 0.4f }, 8));
        panel.AddChild(Ui.Margin(Ui.Column(6, top, entities, description), 8, 6, 8, 6));
        Bind.When(panel, rule, () => panel.Modulate = rule.Enabled ? Colors.White : Colors.White with { A = 0.55f }, nameof(rule.Enabled));
        return panel;
    }

    private static Control AnalyzeCard(GatewayWindowViewModel vm)
    {
        var prompt = Ui.Lines(vm, nameof(vm.AnalyzePrompt), 80, "A prompt: its entities, the model chosen and the cache, without asking a model");
        var model = WritingUi.TextWithChoices(vm, nameof(vm.AnalyzeModel), vm.Models, "auto");
        model.CustomMinimumSize = new Vector2(240, 0);
        var result = Ui.Markdown(vm, nameof(vm.Analysis));
        result.FitContent = true;
        return DashUi.Card(Ui.Column(6, prompt,
            Ui.Row(8, EveStyle.Text_("Model asked", EveStyle.Muted, 12), model, Ui.Spring(),
                Ui.Command("ANALYZE", vm.AnalyzeCommand, primary: true, tooltip: "What the gateway would do with this prompt")),
            result), "Analyze a prompt");
    }

    private static Control LogCard(GatewayWindowViewModel vm)
    {
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        Bind.Items(list, vm.Log, LogRow, Ui.Placeholder("No request yet: point a client of Ollama to the gateway (ollamaUrl)."));
        var scroll = Ui.Scroll(list);
        scroll.CustomMinimumSize = new Vector2(0, 220);
        return DashUi.Card(scroll, "Last requests");
    }

    private static Control LogRow(GatewayLogLine line)
    {
        var time = EveStyle.Text_(line.Time, EveStyle.Muted, 11);
        time.AddThemeFontOverride("font", EveStyle.Mono);
        var route = EveStyle.Text_(line.Route, EveStyle.TextBright, 12);
        route.CustomMinimumSize = new Vector2(230, 0);
        route.ClipText = true;
        var cache = EveStyle.Text_(line.Cache, line.Hit ? EveStyle.Green : EveStyle.Amber, 11);
        cache.CustomMinimumSize = new Vector2(80, 0);
        var prompt = EveStyle.Text_(line.Prompt, EveStyle.Text, 11);
        prompt.ClipText = true;
        prompt.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        prompt.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var entities = EveStyle.Text_(line.Entities, EveStyle.Cyan, 10);
        entities.ClipText = true;
        entities.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var row = Ui.Column(0, Ui.Row(8, time, route, cache, prompt), entities);
        row.TooltipText = line.Tooltip;
        row.MouseFilter = Control.MouseFilterEnum.Stop;
        return Ui.Margin(row, 2, 2, 2, 4);
    }

    private static Control CacheCard(GatewayWindowViewModel vm)
    {
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 4);
        Bind.Items(list, vm.CacheEntries, entry => CacheRow(vm, entry), Ui.Placeholder("The cache is empty."));
        var scroll = Ui.Scroll(list);
        scroll.CustomMinimumSize = new Vector2(0, 220);
        var search = Ui.SearchBar(vm, nameof(vm.CacheSearch), vm.SearchCacheCommand, "Search the prompts, the answers, the models");
        return DashUi.Card(Ui.Column(6, search, scroll), "Cached answers");
    }

    private static Control CacheRow(GatewayWindowViewModel vm, GatewayCacheLine entry)
    {
        var prompt = EveStyle.Text_(entry.Prompt, EveStyle.TextBright, 12, wrap: true);
        var answer = EveStyle.Text_(entry.Answer, EveStyle.Text, 11, wrap: true);
        var details = EveStyle.Text_(entry.Details, EveStyle.Muted, 10);
        var texts = Ui.Column(2, prompt, answer, details);
        texts.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var remove = Ui.Command("🗑", vm.RemoveEntryCommand, tooltip: "Remove this answer from the cache", parameter: () => entry);
        remove.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", EveStyle.Box(EveStyle.Field, EveStyle.Edge with { A = 0.3f }, 6));
        panel.AddChild(Ui.Margin(Ui.Row(8, texts, remove), 8, 4, 8, 4));
        return panel;
    }

    /// <summary>
    /// The popup of the phrases: a search (the prompts, the answers, the models), the phrases found, each with where
    /// it holds (its model, its context) and its possible answers, and EXPORT THESE for the ones found.
    /// </summary>
    private static void OpenPhrases(GatewayWindowViewModel vm, Shell shell)
    {
        var dialog = new AcceptDialog { Title = "PHRASES OF THE CACHE", OkButtonText = "CLOSE", MinSize = new Vector2I(940, 640), Exclusive = false };
        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 6);
        Bind.Items(list, vm.Phrases, PhraseRow, Ui.Placeholder("No phrase found: the cache keeps the prompts answered through the gateway."));
        var scroll = Ui.Scroll(list);
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        scroll.CustomMinimumSize = new Vector2(900, 480);
        var exportThese = Ui.Action("⇩ EXPORT THESE", () => Export(vm, searched: true), tooltip: "Save the phrases found (all of them without a search) with their answers to a JSON file");
        var search = Ui.SearchBar(vm, nameof(vm.PhraseSearch), vm.SearchPhrasesCommand, "Words of a prompt, of an answer, or a model (empty: every phrase)", exportThese);
        var summary = Ui.Label(vm, nameof(vm.PhrasesSummary), EveStyle.Cyan, 12);
        var column = Ui.Column(8, search, summary, scroll);
        column.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        dialog.AddChild(column);
        dialog.Confirmed += dialog.QueueFree;
        dialog.Canceled += dialog.QueueFree;
        shell.Root.AddChild(dialog);
        dialog.PopupCentered(new Vector2I(960, 680));
        Bind.Run(vm.SearchPhrasesCommand);
    }

    private static Control PhraseRow(GatewayPhraseLine phrase)
    {
        var prompt = EveStyle.Text_(phrase.Prompt, EveStyle.TextBright, 13, wrap: true);
        prompt.CustomMinimumSize = new Vector2(860, 0);
        var scope = EveStyle.Text_(phrase.Scope, EveStyle.Cyan, 11);
        var details = EveStyle.Text_(phrase.Details, EveStyle.Muted, 10);
        var answers = Ui.Column(2);
        foreach (var answer in phrase.Answers)
        {
            var line = EveStyle.Text_("→ " + answer, EveStyle.Text, 12, wrap: true);
            line.CustomMinimumSize = new Vector2(840, 0);
            answers.AddChild(line);
        }
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", EveStyle.Box(EveStyle.Field, EveStyle.Edge with { A = 0.35f }, 8));
        panel.AddChild(Ui.Margin(Ui.Column(4, prompt, Ui.Row(10, scope, details), Ui.Margin(answers, 12, 2, 0, 0)), 8, 6, 8, 6));
        return panel;
    }

    /// <summary>Asks where to save the phrases (all of them, or the ones of the search of the popup), then writes them.</summary>
    private static async void Export(GatewayWindowViewModel vm, bool searched)
    {
        var folder = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "kaonomi");
        var name = $"kaonomi-cache-{System.DateTime.Now:yyyyMMdd-HHmm}.json";
        if (await Dialogs.SaveFile("Export the phrases of the cache", name, ["*.json ; JSON"], folder) is { } path)
            await vm.ExportPhrasesAsync(path.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase) ? path : path + ".json", searched);
    }

    /// <summary>Asks for a JSON file of phrases, then adds them to the cache.</summary>
    private static async void Import(GatewayWindowViewModel vm)
    {
        var folder = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "kaonomi");
        if (await Dialogs.OpenFile("Import phrases into the cache", ["*.json ; JSON"], folder) is { } path)
            await vm.ImportPhrasesAsync(path);
    }

    private static Control Field(string caption, Control field, string tooltip)
    {
        var column = Ui.Field(caption, field);
        column.TooltipText = tooltip;
        column.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return column;
    }

    private static SpinBox Number(System.ComponentModel.INotifyPropertyChanged source, string name, double min, double max, double step)
    {
        var spin = new SpinBox { MinValue = min, MaxValue = max, Step = step, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(90, 0) };
        return Bind.Number(spin, source, name);
    }
}
