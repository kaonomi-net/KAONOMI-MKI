using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// /gateway: KAONOMI.LLMGateway, the gateway in front of Ollama. Header: its state (a green dot when it answers), the
/// counters of its cache, ↻ REFRESH and CLEAR CACHE. Left: the settings of the cache and of the routing (SAVE SETTINGS),
/// then the rules of the router, one card each (on, name, priority, model, entities any / all / none, how many requests
/// it routed, 🗑), with ADD RULE and SAVE RULES. 🔎 PHRASES opens a window to search the phrases of the cache and their
/// possible answers; ⇩ EXPORT saves them to a JSON file, ⇧ IMPORT adds the phrases of such a file. Right: ANALYZE (a
/// prompt and a model: what the gateway would do), the last requests (time, model asked → chosen and its rule, HIT /
/// MISS, entities, prompt) and the cached answers (🗑).
/// </summary>
[ToolView(typeof(GatewayWindowViewModel), "LLM Gateway", "🔀", 1280, 820)]
public sealed class GatewayView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (GatewayWindowViewModel)viewModel;

        var dot = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center };
        dot.Bind(Border.BackgroundProperty, vm, nameof(vm.IsOnline), BindingMode.OneWay, v => v is true ? Ui.Green : Ui.Red);
        var summary = DataUi.Tip(DataUi.Bound(vm, nameof(vm.Summary), Ui.Text, 12), vm, nameof(vm.Summary));
        var header = DataUi.Toolbar(DataUi.Bar([DataUi.Caption("LLM Gateway", Ui.TextBright, 14), dot], summary,
            DataUi.Command("↻ REFRESH", vm.RefreshCommand, "Read the state, the settings and the rules again (the changes not saved are lost)"),
            DataUi.Action("🔎 PHRASES", () => OpenPhrases(vm, shell), "Search the phrases of the cache and their possible answers"),
            DataUi.Action("⇩ EXPORT", () => Export(vm, searched: false), "Save every phrase of the cache with its possible answers to a JSON file"),
            DataUi.Action("⇧ IMPORT", () => Import(vm), "Add the phrases of a JSON file to the cache (an export, or phrases written by hand: prompt and answers)"),
            DataUi.Command("✕ CLEAR CACHE", vm.ClearCacheCommand, "Remove every answer kept by the semantic cache")));

        var left = Ui.Column(10, SettingsCard(vm), RulesCard(vm));
        left.Margin = new Thickness(10, 8, 6, 10);
        var right = Ui.Column(10, AnalyzeCard(vm), LogCard(vm), CacheCard(vm));
        right.Margin = new Thickness(6, 8, 10, 10);
        var split = DataUi.SplitH(Ui.Scroll(left), Ui.Scroll(right), 620, 360);

        var status = DataUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsBusy));
        return Ui.Dock((header, Dock.Top), (status, Dock.Bottom), (split, Dock.Top));
    }

    private static Control SettingsCard(GatewayWindowViewModel vm)
    {
        var cache = DataUi.Cells(2, 12, 6,
            DataUi.Check("Semantic cache", vm, nameof(vm.CacheEnabled), "Give back the answer of a close prompt already answered"),
            DataUi.Check("Entity guard", vm, nameof(vm.EntityGuard), "Only when the prompts hold the same numbers, dates, amounts, names, URLs…"),
            DataUi.Field("Embedding model", DataUi.TextWithChoices(vm, nameof(vm.EmbeddingModel), vm.Models, "nomic-embed-text"),
                "An embedding model of Ollama; when it is not installed, a local embedding of the words is used"),
            DataUi.Field("Similarity (model)", DataUi.Number(vm, nameof(vm.Threshold), 0.5, 1, 0.01), "The cosine from which a cached answer comes back, with the embedding model"),
            DataUi.Field("Similarity (words)", DataUi.Number(vm, nameof(vm.LexicalThreshold), 0.5, 1, 0.01), "The same with the local embedding of the words (stricter)"),
            DataUi.Field("TTL (hours)", DataUi.Number(vm, nameof(vm.TtlHours), 0, 24 * 365, 1, "0"), "How long an answer is kept (0: forever)"),
            DataUi.Field("Max answers", DataUi.Number(vm, nameof(vm.MaxEntries), 10, 1_000_000, 100, "0"), "Past it, the answers used the longest ago go"),
            DataUi.Field("Answers before use", DataUi.Number(vm, nameof(vm.MinAnswers), 1, 100, 1, "0"),
                "The answers of the model kept for a prompt before the cache answers it, with one of them at random so that its answers vary (1: always the first answer)"));
        var stats = DataUi.Bound(vm, nameof(vm.CacheSummary), Ui.Accent, 12, wrap: true);
        var note = DataUi.Bound(vm, nameof(vm.EmbedderNote), Ui.Muted, 11, wrap: true);

        var routing = DataUi.Cells(2, 12, 6,
            DataUi.Check("Router", vm, nameof(vm.RoutingEnabled), "Choose the model by the entities of the prompt"),
            DataUi.Check("Rules replace the model asked", vm, nameof(vm.OverrideRequested),
                "On: a rule that matches replaces the model asked by the client. Off: only the model auto is routed"),
            DataUi.Field("Default model (auto)", DataUi.TextWithChoices(vm, nameof(vm.DefaultModel), vm.Models, "gpt-oss:20b"),
                "The model of a request for auto that no rule matches"));

        var save = DataUi.Command("SAVE SETTINGS", vm.SaveSettingsCommand, "Send the settings to the gateway (kept in ~/kaonomi/llmgateway/state.json)", primary: true);
        save.HorizontalAlignment = HorizontalAlignment.Right;
        return DataUi.Card(Ui.Column(8,
            DataUi.Caption("Cache", Ui.Accent, 11), cache, stats, note,
            DataUi.Caption("Routing", Ui.Accent, 11), routing,
            save), "Settings");
    }

    private static Control RulesCard(GatewayWindowViewModel vm)
    {
        var empty = DataUi.Placeholder("No rule: every request keeps its model (auto goes to the default model).");
        var list = DataUi.Items(vm.Rules, rule => RuleRow(vm, rule), empty, spacing: 6);
        var types = DataUi.Bound(vm, nameof(vm.EntityTypes), Ui.Muted, 11, wrap: true);
        var buttons = DataUi.Bar([DataUi.Command("+ ADD RULE", vm.AddRuleCommand, "A new rule at the top")], null,
            DataUi.Command("SAVE RULES", vm.SaveRulesCommand, "Send the rules to the gateway: the next requests are routed with them", primary: true));
        return DataUi.Card(Ui.Column(8, types, list, empty, buttons), "Rules of the router (the highest priority first)");
    }

    private static Control RuleRow(GatewayWindowViewModel vm, GatewayRuleViewModel rule)
    {
        var name = DataUi.Line(rule, nameof(rule.Name), "name");
        name.Width = 150;
        var on = DataUi.Check(string.Empty, rule, nameof(rule.Enabled), "On or off");
        var priority = DataUi.Field("Priority", DataUi.Number(rule, nameof(rule.Priority), -1000, 1000, 1, "0"), "The rules are tried from the highest priority");
        priority.Width = 110;
        var hits = DataUi.Bound(rule, nameof(rule.Hits), Ui.Green, 11);
        var remove = DataUi.Command("🗑", vm.RemoveRuleCommand, "Remove the rule (SAVE RULES to keep it so)", parameter: rule);
        var model = DataUi.Field("Model", DataUi.TextWithChoices(rule, nameof(rule.Model), vm.Models, "model of Ollama"), "The model of the requests that match");
        var top = DataUi.Bar([on, name, priority], model, hits, remove);
        var entities = DataUi.Cells(3, 8, 4,
            DataUi.Field("Any of", DataUi.Line(rule, nameof(rule.Any), "CODE, MATH"), "At least Min of these entities (comma separated; TYPE or TYPE:value)"),
            DataUi.Field("All of", DataUi.Line(rule, nameof(rule.All), "LANGUAGE:fr"), "Every one of these entities"),
            DataUi.Field("None of", DataUi.Line(rule, nameof(rule.None), "IMAGE"), "None of these entities"));
        var description = DataUi.Line(rule, nameof(rule.Description), "what the rule is for");
        description.FontSize = 11;

        var panel = Ui.Box(Ui.Column(6, top, entities, description), 8, Ui.Field);
        panel.Bind(Visual.OpacityProperty, rule, nameof(rule.Enabled), BindingMode.OneWay, v => v is true ? 1.0 : 0.55);
        return panel;
    }

    private static Control AnalyzeCard(GatewayWindowViewModel vm)
    {
        var prompt = DataUi.Lines(vm, nameof(vm.AnalyzePrompt), 80, "A prompt: its entities, the model chosen and the cache, without asking a model");
        var model = DataUi.TextWithChoices(vm, nameof(vm.AnalyzeModel), vm.Models, "auto");
        model.Width = 240;
        var result = DataUi.Markdown(vm, nameof(vm.Analysis));
        return DataUi.Card(Ui.Column(6, prompt,
            DataUi.Bar([DataUi.Text("Model asked", Ui.Muted, 12), model], null,
                DataUi.Command("ANALYZE", vm.AnalyzeCommand, "What the gateway would do with this prompt", primary: true)),
            result), "Analyze a prompt");
    }

    private static Control LogCard(GatewayWindowViewModel vm)
    {
        var list = DataUi.ScrollList(vm.Log, LogRow, "No request yet: point a client of Ollama to the gateway (ollamaUrl).");
        list.Height = 220;
        return DataUi.Card(list, "Last requests");
    }

    private static Control LogRow(GatewayLogLine line)
    {
        var time = DataUi.Text(line.Time ?? string.Empty, Ui.Muted, 11, mono: true);
        var route = DataUi.Text(line.Route ?? string.Empty, Ui.TextBright, 12);
        route.Width = 230;
        var cache = DataUi.Text(line.Cache ?? string.Empty, line.Hit ? Ui.Green : DataUi.Amber, 11);
        cache.Width = 80;
        var prompt = DataUi.Text(line.Prompt ?? string.Empty, Ui.Text, 11);
        var entities = DataUi.Text(line.Entities ?? string.Empty, Ui.Accent, 10);
        var row = Ui.Column(0, DataUi.Bar([time, route, cache], prompt), entities);
        row.Margin = new Thickness(2, 2, 2, 4);
        row.Background = Avalonia.Media.Brushes.Transparent;
        return DataUi.Tip(row, line.Tooltip);
    }

    private static Control CacheCard(GatewayWindowViewModel vm)
    {
        var list = DataUi.ScrollList(vm.CacheEntries, entry => CacheRow(vm, entry), "The cache is empty.", spacing: 4);
        list.Height = 220;
        var search = DataUi.SearchBar(vm, nameof(vm.CacheSearch), vm.SearchCacheCommand, "Search the prompts, the answers, the models");
        return DataUi.Card(Ui.Column(6, search, list), "Cached answers");
    }

    private static Control CacheRow(GatewayWindowViewModel vm, GatewayCacheLine entry)
    {
        var texts = Ui.Column(2,
            Ui.Label(entry.Prompt ?? string.Empty, Ui.TextBright, 12, wrap: true),
            Ui.Label(entry.Answer ?? string.Empty, Ui.Text, 11, wrap: true),
            DataUi.Text(entry.Details ?? string.Empty, Ui.Muted, 10));
        var remove = DataUi.Command("🗑", vm.RemoveEntryCommand, "Remove this answer from the cache", parameter: entry);
        remove.VerticalAlignment = VerticalAlignment.Top;
        var row = Ui.Dock((remove, Dock.Right), (texts, Dock.Left));
        remove.Margin = new Thickness(8, 0, 0, 0);
        return Ui.Box(row, 6, Ui.Field);
    }

    /// <summary>
    /// The window of the phrases: a search (the prompts, the answers, the models), the phrases found, each with where it
    /// holds (its model, its context) and its possible answers, and EXPORT THESE for the ones found.
    /// </summary>
    private static void OpenPhrases(GatewayWindowViewModel vm, Shell shell)
    {
        const string key = "gateway-phrases";
        if (shell.TryBringBack(key))
        {
            Ui.Run(vm.SearchPhrasesCommand);
            return;
        }
        var list = DataUi.ScrollList(vm.Phrases, PhraseRow, "No phrase found: the cache keeps the prompts answered through the gateway.", spacing: 6);
        var exportThese = DataUi.Action("⇩ EXPORT THESE", () => Export(vm, searched: true), "Save the phrases found (all of them without a search) with their answers to a JSON file");
        var search = DataUi.SearchBar(vm, nameof(vm.PhraseSearch), vm.SearchPhrasesCommand, "Words of a prompt, of an answer, or a model (empty: every phrase)", out var edit, exportThese);
        DataUi.FocusWhenShown(edit);
        var summary = DataUi.Bound(vm, nameof(vm.PhrasesSummary), Ui.Accent, 12);
        var content = new Border { Padding = new Thickness(12) };
        var close = DataUi.Action("CLOSE", () => shell.Close(content));
        close.HorizontalAlignment = HorizontalAlignment.Right;
        var top = Ui.Column(8, search, summary);
        top.Margin = new Thickness(0, 0, 0, 8);
        close.Margin = new Thickness(0, 8, 0, 0);
        content.Child = Ui.Dock((top, Dock.Top), (close, Dock.Bottom), (list, Dock.Top));
        shell.OpenControl(key, "Phrases of the cache", "🔎", content, new Size(960, 680));
        Ui.Run(vm.SearchPhrasesCommand);
    }

    private static Control PhraseRow(GatewayPhraseLine phrase)
    {
        var answers = Ui.Column(2);
        foreach (var answer in phrase.Answers)
            answers.Children.Add(Ui.Label("→ " + answer, Ui.Text, 12, wrap: true));
        answers.Margin = new Thickness(12, 2, 0, 0);
        return Ui.Box(Ui.Column(4,
            Ui.Label(phrase.Prompt ?? string.Empty, Ui.TextBright, 13, wrap: true),
            Ui.Row(10, DataUi.Text(phrase.Scope ?? string.Empty, Ui.Accent, 11), DataUi.Text(phrase.Details ?? string.Empty, Ui.Muted, 10)),
            answers), 8, Ui.Field);
    }

    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "kaonomi");

    /// <summary>Asks where to save the phrases (all of them, or the ones of the search of the phrases), then writes them.</summary>
    private static async void Export(GatewayWindowViewModel vm, bool searched)
    {
        var name = $"kaonomi-cache-{DateTime.Now:yyyyMMdd-HHmm}.json";
        if (await Dialogs.SaveFile("Export the phrases of the cache", name, ["*.json ; JSON"], Folder) is { } path)
            await vm.ExportPhrasesAsync(path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? path : path + ".json", searched);
    }

    /// <summary>Asks for a JSON file of phrases, then adds them to the cache.</summary>
    private static async void Import(GatewayWindowViewModel vm)
    {
        if (await Dialogs.OpenFile("Import phrases into the cache", ["*.json ; JSON"], Folder) is { } path)
            await vm.ImportPhrasesAsync(path);
    }
}
