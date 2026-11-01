using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// Ontology management: loads every node, shows them as a tree built from <c>nodeparentId</c>,
/// and edits, adds and soft-deletes nodes through the <c>/ontology</c> routes.
/// </summary>
public partial class OntologyWindowViewModel : ViewModelBase, INoteContextProvider
{
    private const string LogArea = "Ontology";

    /// <summary>Logs the outcome shown in the status bar: ERROR when the bar is in error, INFO otherwise.</summary>
    private void LogOutcome(string action)
    {
        if (IsError)
            ActivityLog.Error(LogArea, $"{action}: {Status}");
        else
            ActivityLog.Info(LogArea, $"{action}: {Status}");
    }

    /// <summary>Logs the outcome shown in a node's editor header.</summary>
    private static void LogNode(OntologyNodeViewModel node, string action)
    {
        var message = $"{action} \"{node.DisplayName}\": {node.Status}";
        if (node.IsError)
            ActivityLog.Error(LogArea, message);
        else
            ActivityLog.Info(LogArea, message);
    }

    /// <summary>Elasticsearch's default result window: the most nodes one <c>/ontology/last</c> call can return.</summary>
    private const int MaxNodes = 10000;

    /// <summary>Hits requested from <c>/ontology/search</c> (the server currently ignores it and returns at most 10).</summary>
    private const int SearchSize = 100;

    private readonly KaonomiAssistantService _assistant;
    private readonly List<OntologyNodeViewModel> _allNodes = [];
    private HashSet<OntologyNodeViewModel>? _searchHits;
    private bool _isRestoringSelection;
    private CancellationTokenSource? _searchCts;

    /// <summary>Design-time constructor using default server URL from settings.</summary>
    public OntologyWindowViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl))
    {
    }

    /// <summary>
    /// Uses the given assistant client for ontology API calls; <paramref name="ollama"/> and
    /// <paramref name="selectedModel"/> (the model chosen in the chat window) drive the node "Auto" fill.
    /// </summary>
    public OntologyWindowViewModel(
        KaonomiAssistantService assistant,
        OllamaModelService? ollama = null,
        Func<string?>? selectedModel = null,
        IEnumerable<string>? entityTypes = null,
        Action<string>? entityTypeCreated = null)
    {
        _assistant = assistant;
        _ollama = ollama;
        _selectedModel = selectedModel ?? (() => null);
        _entityTypeCreated = entityTypeCreated ?? (_ => { });

        // First entry "" = no type; then the configured NER types (config.yml, ontology.entityTypes).
        EntityTypeOptions.Add(string.Empty);
        foreach (var type in entityTypes ?? new ChatSettings().EntityTypes)
            EnsureEntityTypeOption(type);
    }

    /// <summary>
    /// Choices of the "Entity type" drop-down, shared by every node. Only ever grows: removing or clearing items
    /// would reset the drop-down selection and silently erase the node's value.
    /// </summary>
    public ObservableCollection<string> EntityTypeOptions { get; } = [];

    /// <summary>Confidentiality levels, always upper case; the same strings are accepted by the server (Ontology.cs).</summary>
    public static readonly string[] ConfidentialityLevels = OntologyEntry.ConfidentialityScale;

    /// <summary>Level of a new node (same default as the server).</summary>
    public const string DefaultConfidentiality = "PUBLIC(PU)";

    /// <summary>Former levels mapped to the new ones (Private and Confidential have no equivalent and are kept).</summary>
    private static readonly Dictionary<string, string> LegacyConfidentiality = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Public"] = "PUBLIC(PU)",
        ["ExternalRestricted"] = "RESTRICTED(NR)",
        ["InternalRestricted"] = "RESTRICTED(NR)",
        ["Secret"] = "SECRET(NS)",
        ["TopSecret"] = "COSMIC TOP SECRET(CTS)",
    };

    /// <summary>
    /// Choices of the "Confidentiality" drop-down (value = label, upper case). Only ever grows, like
    /// <see cref="EntityTypeOptions"/>: a value missing from the list would be erased by the drop-down.
    /// </summary>
    public ObservableCollection<ConfidentialityOption> ConfidentialityOptions { get; } =
        [.. ConfidentialityLevels.Select(level => new ConfidentialityOption(level, level))];

    /// <summary>
    /// Maps a stored confidentiality to a level: the level itself in any case ("public(pu)"), its code ("PU"), its name
    /// ("COSMIC TOP SECRET") or a former value ("TopSecret"). Any other value is kept in upper case and added to the
    /// drop-down, so it is not erased.
    /// </summary>
    public string ResolveConfidentiality(string? value)
    {
        var raw = value?.Trim() ?? string.Empty;
        var level = ConfidentialityLevels.FirstOrDefault(l =>
                        string.Equals(l, raw, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(l[(l.IndexOf('(') + 1)..^1], raw, StringComparison.OrdinalIgnoreCase)   // code
                        || string.Equals(l[..l.IndexOf('(')].Trim(), raw, StringComparison.OrdinalIgnoreCase))  // name
                    ?? LegacyConfidentiality.GetValueOrDefault(raw.Replace(" ", string.Empty));
        if (level is not null)
            return level;

        var kept = raw.ToUpperInvariant();
        if (!ConfidentialityOptions.Any(o => o.Value == kept))
            ConfidentialityOptions.Add(new ConfidentialityOption(kept, kept.Length == 0 ? "(NONE)" : kept));
        return kept;
    }

    /// <summary>
    /// Rank of a level, from the lowest to the highest confidentiality: 1 PUBLIC(PU), 2 RESTRICTED(NR), 3 SECRET(NS),
    /// 4 COSMIC TOP SECRET(CTS); 0 for any other value (no constraint). Same numbers as the server's confidentialityLevel.
    /// </summary>
    public static int ConfidentialityRank(string? level) =>
        Array.FindIndex(ConfidentialityLevels, l => string.Equals(l, level?.Trim(), StringComparison.OrdinalIgnoreCase)) + 1;

    /// <summary>
    /// Lowest level allowed for <paramref name="node"/>: the highest level of all its ancestors, real and virtual
    /// (a child is never less confidential than any of its parents). Null when no ancestor has a level.
    /// </summary>
    public string? MinimumConfidentiality(OntologyNodeViewModel node)
    {
        string? minimum = null;
        var visited = new HashSet<OntologyNodeViewModel> { node };
        var pending = new Stack<OntologyNodeViewModel>(ParentsOf(node));
        while (pending.Count > 0)
        {
            var ancestor = pending.Pop();
            if (!visited.Add(ancestor))
                continue;
            if (ConfidentialityRank(ancestor.Confidentiality) > ConfidentialityRank(minimum))
                minimum = ancestor.Confidentiality;
            foreach (var parent in ParentsOf(ancestor))
                pending.Push(parent);
        }
        return minimum;
    }

    /// <summary>Real parent and virtual parents of <paramref name="node"/>.</summary>
    private IEnumerable<OntologyNodeViewModel> ParentsOf(OntologyNodeViewModel node)
    {
        if (node.Parent is not null)
            yield return node.Parent;
        foreach (var id in node.VirtualParentIds)
            if (FindNode(id) is { } virtualParent)
                yield return virtualParent;
    }

    /// <summary>Descendants of <paramref name="node"/>, real and virtual, whose level is below <paramref name="level"/>.</summary>
    public IReadOnlyList<OntologyNodeViewModel> DescendantsBelow(OntologyNodeViewModel node, string level) =>
        AllDescendants(node)
            .Where(d => ConfidentialityRank(d.Confidentiality) < ConfidentialityRank(level))
            .ToList();

    /// <summary>Asks the user to confirm raising <paramref name="count"/> descendants with their parent.</summary>
    public Task<bool> ConfirmRaiseDescendantsAsync(OntologyNodeViewModel node, string level, int count) =>
        ConfirmAsync($"\"{node.DisplayName}\" becomes {level}.\n\n{count} descendant node(s) have a lower level and will be raised " +
                     $"to {level}: a child can never be less confidential than its parent.", "Raise");

    /// <summary>
    /// Raises the given descendants to <paramref name="level"/> (only the confidentiality is saved, via
    /// <c>POST /ontology/update</c>); unsaved nodes are only updated in the editor.
    /// </summary>
    public async Task RaiseDescendantsAsync(OntologyNodeViewModel node, string level, IReadOnlyList<OntologyNodeViewModel> descendants)
    {
        var done = 0;
        try
        {
            foreach (var descendant in descendants)
            {
                Status = $"Raising descendants to {level}: {done + 1}/{descendants.Count}…";
                if (!await descendant.SetConfidentialityAsync(level))
                {
                    IsError = true;
                    Status = $"Raised {done}/{descendants.Count} descendant(s) to {level}; \"{descendant.DisplayName}\" failed: {descendant.Status}";
                    LogOutcome($"Confidentiality of \"{node.DisplayName}\"");
                    return;
                }
                done++;
            }

            IsError = false;
            Status = $"\"{node.DisplayName}\" and {done} descendant(s) are now at least {level}";
            LogOutcome($"Confidentiality of \"{node.DisplayName}\"");
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = $"Raised {done}/{descendants.Count} descendant(s) to {level}; error: {ex.Message}";
            LogOutcome($"Confidentiality of \"{node.DisplayName}\"");
        }
    }

    /// <summary>Adds a type to the drop-down if missing, so values already stored on nodes stay selectable.</summary>
    public void EnsureEntityTypeOption(string? type)
    {
        if (!string.IsNullOrWhiteSpace(type) && !EntityTypeOptions.Contains(type))
            EntityTypeOptions.Add(type);
    }

    private readonly OllamaModelService? _ollama;
    private readonly Func<string?> _selectedModel;

    /// <summary>Called with a new entity type created by the Auto fill (kept for the session only).</summary>
    private readonly Action<string> _entityTypeCreated;

    /// <summary>Configured entity types, without the "" (none) entry.</summary>
    private IEnumerable<string> KnownEntityTypes => EntityTypeOptions.Where(t => t.Length > 0);

    /// <summary>
    /// Normalises an entity type to the NER convention: ASCII upper case, words joined by "_"
    /// ("medical device" → "MEDICAL_DEVICE", "état/région" → "ETAT_REGION").
    /// </summary>
    public static string NormalizeEntityType(string? type)
    {
        var withoutAccents = new string((type ?? string.Empty).Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
            .ToArray());
        var words = withoutAccents.ToUpperInvariant()
            .Split([' ', '-', '_', '.', '/', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(char.IsAsciiLetterOrDigit).ToArray()))
            .Where(word => word.Length > 0);
        return string.Join("_", words);
    }

    /// <summary>
    /// Returns the drop-down entry matching <paramref name="type"/> (case-insensitive); when there is none, creates the
    /// type in upper case and adds it to the drop-down for the session (config.yml is not changed).
    /// </summary>
    public string ResolveEntityType(string type, out bool created)
    {
        created = false;
        var normalized = NormalizeEntityType(type);
        if (normalized.Length == 0)
            return string.Empty;

        var existing = KnownEntityTypes.FirstOrDefault(t => string.Equals(t, normalized, StringComparison.OrdinalIgnoreCase)
                                                            || string.Equals(t, type.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;

        EnsureEntityTypeOption(normalized);
        _entityTypeCreated(normalized);
        created = true;
        return normalized;
    }

    /// <summary>Time allowed to the model for one "Auto" fill.</summary>
    private static readonly TimeSpan AutoFillTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Answer shape imposed on the model for the "Auto" fill (Ollama structured output).</summary>
    private static readonly JsonNode AutoFillSchema = JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "description":     { "type": "string" },
            "comment":         { "type": "string" },
            "emotions": {
              "type": "object",
              "properties": {
                "angry":    { "type": "integer", "minimum": 0, "maximum": 10 },
                "sad":      { "type": "integer", "minimum": 0, "maximum": 10 },
                "happy":    { "type": "integer", "minimum": 0, "maximum": 10 },
                "fear":     { "type": "integer", "minimum": 0, "maximum": 10 },
                "surprise": { "type": "integer", "minimum": 0, "maximum": 10 },
                "disgust":  { "type": "integer", "minimum": 0, "maximum": 10 }
              },
              "required": ["angry", "sad", "happy", "fear", "surprise", "disgust"]
            },
            "entityType":      { "type": "string" },
            "synonyms":        { "type": "array", "items": { "type": "string" } },
            "tags":            { "type": "array", "items": { "type": "string" } },
            "sentences":       { "type": "array", "items": { "type": "string" }, "minItems": 3, "maxItems": 3 },
            "relatedEntities": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["description", "comment", "emotions", "entityType", "synonyms", "tags", "sentences", "relatedEntities"]
        }
        """)!;

    /// <summary>
    /// Asks the chat's selected Ollama model to fill the comment, synonyms, tags (capitalised hashtags) and three
    /// example sentences using the node name. The editor fields are replaced but not saved: the user reviews,
    /// then saves or discards.
    /// </summary>
    public async Task AutoFillAsync(OntologyNodeViewModel node, CancellationToken cancellationToken)
    {
        var name = node.Name.Trim();
        var model = _selectedModel();
        if (_ollama is null || string.IsNullOrWhiteSpace(model))
        {
            node.IsError = true;
            node.Status = "No Ollama model selected in the chat window.";
            LogNode(node, "Auto fill");
            return;
        }

        node.IsError = false;
        node.Status = $"Asking {model}…";

        const string system =
            "You enrich the nodes of an ontology. Answer only with the requested JSON, plain text values, no Markdown. " +
            "Write the description, comment, synonyms, sentences and related entities in the same language as the node name and " +
            "description; tags and the entity type are always in English. Detect that language from the name itself " +
            "(e.g. \"Anniversaire\" is French, \"Geburtstag\" is German) and never switch to English unless the name is English.";
        var context = string.Join("\n", new[]
        {
            $"Name: {name}",
            string.IsNullOrWhiteSpace(node.EntityType) ? null : $"Entity type: {node.EntityType}",
            node.IsOntologyClass ? "Kind: ontology class" : "Kind: instance",
            node.Parent is null ? null : $"Parent node: {node.Parent.DisplayName}",
            string.IsNullOrWhiteSpace(node.Description) ? null : $"Description: {node.Description}",
        }.Where(line => line is not null));
        var user =
            $"{context}\n\n" +
            "Fill these fields for this node:\n" +
            "- description: a clear definition of the term in 2 to 4 sentences (improve the current description if there is one).\n" +
            "- comment: one or two sentences explaining the term.\n" +
            "- emotions: how strongly the term usually evokes each emotion, from 0 (not at all) to 10 (very strongly): " +
            "angry, sad, happy, fear, surprise, disgust.\n" +
            "- synonyms: up to 8 synonyms or close variants of the name.\n" +
            "- tags: 3 to 8 hashtags ALWAYS IN ENGLISH, whatever the language of the node (translate them), each starting " +
            "with # followed by a capital letter, words joined without spaces, each word capitalised (e.g. #Animal, #DomesticPet).\n" +
            $"- sentences: exactly 3 natural example sentences, each containing the word \"{name}\".\n" +
            "- relatedEntities: up to 8 names of entities closely related to this node (parts, kinds, places, people, " +
            "organizations, concepts…), one name per item, without explanation.\n" +
            $"- entityType: the named-entity (NER) type of this node. Use one of these types when one fits precisely: " +
            $"{string.Join(", ", KnownEntityTypes.Where(t => !t.Equals("OTHERS", StringComparison.OrdinalIgnoreCase)))}. " +
            "Otherwise create a new precise type: a short name in English, upper case, words joined with _ " +
            "(e.g. ANIMAL, MEDICAL_DEVICE, DISEASE). Use OTHERS only when no meaningful type can be named.";

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(AutoFillTimeout);
            var json = await _ollama.ChatJsonAsync(model, system, user, AutoFillSchema, timeout.Token);
            var answer = JsonSerializer.Deserialize<AutoFillAnswer>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                         ?? throw new JsonException("Empty answer.");

            var synonyms = Clean(answer.Synonyms)
                .Where(s => !s.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var tags = Clean(answer.Tags).Select(ToHashtag).Where(t => t.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase);
            var sentences = Clean(answer.Sentences).Take(3);
            var related = Clean(answer.RelatedEntities)
                .Where(e => !e.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8);

            if (!string.IsNullOrWhiteSpace(answer.Comment))
                node.Comment = answer.Comment.Trim();
            node.Synonyms = string.Join(Environment.NewLine, synonyms);
            node.Tags = string.Join(Environment.NewLine, tags);
            node.SentencesExemples = string.Join(Environment.NewLine, sentences);
            node.RelatedEntities = string.Join(Environment.NewLine, related);
            if (!string.IsNullOrWhiteSpace(answer.Description))
                node.Description = answer.Description.Replace("**", string.Empty).Trim();
            if (answer.Emotions is { } emotions)
            {
                node.AngryLevel = Level(emotions.Angry);
                node.SadLevel = Level(emotions.Sad);
                node.HappyLevel = Level(emotions.Happy);
                node.FearLevel = Level(emotions.Fear);
                node.SurpriseLevel = Level(emotions.Surprise);
                node.DisgustLevel = Level(emotions.Disgust);
            }

            // The entity type is only set when the node has none: a type chosen by the user is kept.
            var typeNote = string.Empty;
            if (string.IsNullOrWhiteSpace(node.EntityType) && !string.IsNullOrWhiteSpace(answer.EntityType))
            {
                node.EntityType = ResolveEntityType(answer.EntityType, out var created);
                typeNote = created ? $" · new entity type {node.EntityType} (this session)" : $" · type {node.EntityType}";
                if (created)
                    ActivityLog.Info(LogArea, $"Entity type {node.EntityType} created by the Auto fill of \"{node.DisplayName}\"");
            }

            node.Status = $"Filled by {model}{typeNote}: review, then Save (or Discard changes)";
            LogNode(node, "Auto fill");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            node.Status = "Auto fill cancelled";
            LogNode(node, "Auto fill");
        }
        catch (OperationCanceledException)
        {
            node.IsError = true;
            node.Status = $"{model} did not answer within {AutoFillTimeout.TotalMinutes:0} minutes.";
            LogNode(node, "Auto fill");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            node.IsError = true;
            node.Status = $"Ollama unreachable at {_ollama.BaseAddress} ({ex.Message})";
            LogNode(node, "Auto fill");
        }
        catch (Exception ex)
        {
            node.IsError = true;
            node.Status = $"Auto fill failed: {ex.Message}";
            LogNode(node, "Auto fill");
        }

        static IEnumerable<string> Clean(IEnumerable<string>? values) =>
            (values ?? []).Select(v => v.Trim()).Where(v => v.Length > 0);

        // Emotion levels use a 0-10 scale; out-of-range answers are clamped.
        static decimal Level(int value) => Math.Clamp(value, 0, 10);
    }

    /// <summary>
    /// Normalises a tag to a hashtag starting with a capital letter, without spaces:
    /// "domestic pet" → "#DomesticPet", "#animal" → "#Animal".
    /// </summary>
    public static string ToHashtag(string tag)
    {
        var words = tag.Trim().TrimStart('#').Split([' ', '\t', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        return "#" + string.Concat(words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private sealed class AutoFillAnswer
    {
        public string? Comment { get; set; }
        public List<string>? Synonyms { get; set; }
        public List<string>? Tags { get; set; }
        public List<string>? Sentences { get; set; }
        public List<string>? RelatedEntities { get; set; }
        public string? EntityType { get; set; }
        public string? Description { get; set; }
        public EmotionLevels? Emotions { get; set; }
    }

    /// <summary>One choice of the confidentiality drop-down.</summary>
    /// <param name="Value">Value stored on the node and sent to the server.</param>
    /// <param name="Label">Text shown in the drop-down.</param>
    public sealed record ConfidentialityOption(string Value, string Label);

    private sealed class EmotionLevels
    {
        public int Angry { get; set; }
        public int Sad { get; set; }
        public int Happy { get; set; }
        public int Fear { get; set; }
        public int Surprise { get; set; }
        public int Disgust { get; set; }
    }

    /// <summary>Asks the user to confirm an action (message, confirm-button label); set by the view. Defaults to "yes".</summary>
    public Func<string, string, Task<bool>> ConfirmAsync { get; set; } = (message, label) => Confirmations.Ask(message, label);

    /// <summary>
    /// Asks where to save an export and writes it; set by the view. Arguments: suggested file name, content.
    /// Returns the saved file name, or null when cancelled.
    /// </summary>
    public Func<string, string, Task<string?>> SaveTextFileAsync { get; set; } = (_, _) => Task.FromResult<string?>(null);

    /// <summary>Asks for a file to import and returns its text, or null when cancelled; set by the view.</summary>
    public Func<Task<string?>> OpenTextFileAsync { get; set; } = () => Task.FromResult<string?>(null);

    /// <summary>Raised when a node must be shown in the Entity window (the editor) with the name field focused.</summary>
    public event Action<OntologyNodeViewModel>? EditRequested;

    /// <summary>Visible root occurrences of the tree (a node with virtual parents appears several times).</summary>
    public ObservableCollection<OntologyTreeItem> RootItems { get; } = [];

    /// <summary>True when the tree shows at least one node.</summary>
    public bool HasNodes => RootItems.Count > 0;

    /// <summary>Node shown in the editor (the node of <see cref="SelectedTreeItem"/>).</summary>
    [ObservableProperty]
    private OntologyNodeViewModel? _selectedNode;

    /// <summary>Occurrence selected in the tree.</summary>
    [ObservableProperty]
    private OntologyTreeItem? _selectedTreeItem;

    /// <summary>Guards the two-way sync between <see cref="SelectedNode"/> and <see cref="SelectedTreeItem"/>.</summary>
    private bool _syncingSelection;

    partial void OnSelectedTreeItemChanged(OntologyTreeItem? value)
    {
        if (_syncingSelection)
            return;
        _syncingSelection = true;
        try
        {
            SelectedNode = value?.Node;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    /// <summary>Virtual children of each node (nodes listing it in <c>nodevirtualeftchildrenId</c>), built with the tree.</summary>
    private readonly Dictionary<string, List<OntologyNodeViewModel>> _virtualChildren = [];

    /// <summary>Loaded node with this id, or null.</summary>
    public OntologyNodeViewModel? FindNode(string? id) =>
        string.IsNullOrEmpty(id) ? null : _allNodes.FirstOrDefault(n => n.Id == id);

    /// <summary>Node waiting for its new virtual parent ("Attach to another node…"), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAttaching), nameof(AttachPrompt))]
    private OntologyNodeViewModel? _attachSource;

    /// <summary>True while waiting for the click on the future virtual parent.</summary>
    public bool IsAttaching => AttachSource is not null;

    /// <summary>Message of the attach banner.</summary>
    public string AttachPrompt => $"Click the node to add as a virtual parent of \"{AttachSource?.DisplayName}\" (Esc to cancel)";

    [ObservableProperty]
    private string _term = string.Empty;

    [ObservableProperty]
    private bool _showDeleted;

    [ObservableProperty]
    private string _status = "Loading the ontology…";

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    private bool _isBusy;

    partial void OnShowDeletedChanged(bool value)
    {
        ApplyFilter();
        ActivityLog.Info(LogArea, value ? "Deleted nodes shown" : "Deleted nodes hidden");
    }

    partial void OnSelectedNodeChanged(OntologyNodeViewModel? value)
    {
        if (value is not null && !_isRestoringSelection)
            _ = value.RefreshFromServerAsync();

        // A node selected from code (search, edit, refresh…) selects its occurrence in the tree.
        if (!_syncingSelection && SelectedTreeItem?.Node != value)
        {
            _syncingSelection = true;
            try
            {
                SelectedTreeItem = value is null ? null : FindItem(value);
            }
            finally
            {
                _syncingSelection = false;
            }
        }
    }

    // ---- Virtual parents ("Attach to another node…") ----

    /// <summary>Enters attach mode: the next node clicked in the tree becomes a virtual parent of <paramref name="node"/>.</summary>
    public void StartAttach(OntologyNodeViewModel node)
    {
        AttachSource = node;
        IsError = false;
        Status = AttachPrompt;
        ActivityLog.Info(LogArea, $"Attach of \"{node.DisplayName}\" started: waiting for the virtual parent");
    }

    /// <summary>Leaves attach mode without change.</summary>
    [RelayCommand]
    private void CancelAttach()
    {
        if (AttachSource is not { } source)
            return;
        AttachSource = null;
        Status = "Attach cancelled";
        ActivityLog.Info(LogArea, $"Attach of \"{source.DisplayName}\" cancelled");
    }

    /// <summary>
    /// Adds <paramref name="target"/> as a virtual parent of the node being attached, after checking that it makes sense:
    /// not itself, not its real parent, not already a virtual parent, not one of its descendants (loop), and not more
    /// confidential than the node (a child is never less confidential than its parent).
    /// </summary>
    public async Task CompleteAttachAsync(OntologyNodeViewModel target)
    {
        if (AttachSource is not { } source)
            return;

        string? refusal = null;
        if (ReferenceEquals(target, source))
            refusal = "a node cannot be attached to itself";
        else if (ReferenceEquals(target, source.Parent))
            refusal = $"\"{target.DisplayName}\" is already its real parent";
        else if (source.VirtualParentIds.Contains(target.Id))
            refusal = $"it is already attached under \"{target.DisplayName}\"";
        else if (target.IsNew)
            refusal = $"save \"{target.DisplayName}\" first";
        else if (AllDescendants(source).Contains(target))
            refusal = $"\"{target.DisplayName}\" is one of its descendants: the tree would loop";
        else
        {
            var floor = HighestLevel(target.Confidentiality, MinimumConfidentiality(target));
            if (ConfidentialityRank(source.Confidentiality) < ConfidentialityRank(floor))
                refusal = $"\"{source.DisplayName}\" ({source.Confidentiality}) would be less confidential than its new parent ({floor}): raise it first";
        }

        if (refusal is not null)
        {
            // Stay in attach mode so another node can be clicked.
            IsError = true;
            Status = $"Not attached: {refusal}. Click another node or press Esc.";
            ActivityLog.Error(LogArea, $"Attach of \"{source.DisplayName}\" under \"{target.DisplayName}\" refused: {refusal}");
            return;
        }

        AttachSource = null;
        IsBusy = true;
        try
        {
            if (!await source.SetVirtualParentsAsync([.. source.VirtualParentIds, target.Id]))
            {
                IsError = true;
                Status = $"Not attached: {source.Status}";
                LogOutcome("Attach");
                return;
            }

            BuildTree();
            target.IsExpanded = true;
            ApplyFilter();
            SelectedTreeItem = FindItem(source, target) ?? SelectedTreeItem;
            IsError = false;
            Status = $"\"{source.DisplayName}\" (with its children) is now also attached under \"{target.DisplayName}\"";
            LogOutcome("Attach");
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = $"Not attached: {ex.Message}";
            LogOutcome("Attach");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Removes the virtual link between <paramref name="node"/> and <paramref name="virtualParent"/>.</summary>
    public async Task DetachAsync(OntologyNodeViewModel node, OntologyNodeViewModel virtualParent)
    {
        IsBusy = true;
        try
        {
            if (!await node.SetVirtualParentsAsync(node.VirtualParentIds.Where(id => id != virtualParent.Id).ToList()))
            {
                IsError = true;
                Status = $"Not detached: {node.Status}";
                LogOutcome("Detach");
                return;
            }

            BuildTree();
            ApplyFilter();
            SelectedNode = node;
            IsError = false;
            Status = $"\"{node.DisplayName}\" is no longer attached under \"{virtualParent.DisplayName}\"";
            LogOutcome("Detach");
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = $"Not detached: {ex.Message}";
            LogOutcome("Detach");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Virtual children of <paramref name="node"/> (nodes attached under it).</summary>
    private IReadOnlyList<OntologyNodeViewModel> VirtualChildrenOf(OntologyNodeViewModel node) =>
        _virtualChildren.TryGetValue(node.Id, out var children) ? children : [];

    /// <summary>Real and virtual descendants of <paramref name="node"/> (each once, loops ignored).</summary>
    private HashSet<OntologyNodeViewModel> AllDescendants(OntologyNodeViewModel node)
    {
        var found = new HashSet<OntologyNodeViewModel>();
        var pending = new Stack<OntologyNodeViewModel>([node]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var child in current.AllChildren.Concat(VirtualChildrenOf(current)))
                if (!ReferenceEquals(child, node) && found.Add(child))
                    pending.Push(child);
        }
        return found;
    }

    private static string? HighestLevel(string? a, string? b) => ConfidentialityRank(a) >= ConfidentialityRank(b) ? a : b;

    /// <summary>
    /// First occurrence of <paramref name="node"/> in the visible tree: under <paramref name="virtualParent"/> when given,
    /// otherwise preferably at its real place.
    /// </summary>
    private OntologyTreeItem? FindItem(OntologyNodeViewModel node, OntologyNodeViewModel? virtualParent = null)
    {
        var occurrences = FlattenItems(RootItems).Where(i => ReferenceEquals(i.Node, node)).ToList();
        return virtualParent is not null
            ? occurrences.FirstOrDefault(i => ReferenceEquals(i.VirtualParent, virtualParent))
            : occurrences.FirstOrDefault(i => !i.IsInVirtualBranch) ?? occurrences.FirstOrDefault();
    }

    private static IEnumerable<OntologyTreeItem> FlattenItems(IEnumerable<OntologyTreeItem> items) =>
        items.SelectMany(i => FlattenItems(i.Children).Prepend(i));

    /// <summary>
    /// The root nodes every ontology has: persons, organisation, location, material and misc (each its own entity type,
    /// as in named entity recognition). A spelling variant (person, organization, locations, materials…) counts.
    /// </summary>
    private const string DefaultRootIdPrefix = "ontology-root-";

    private static readonly (string Name, string EntityType, string[] Variants)[] DefaultRoots =
    [
        ("persons", "PERSON", ["persons", "person", "people"]),
        ("organisation", "ORGANISATION", ["organisation", "organisations", "organization", "organizations"]),
        ("location", "LOCATION", ["location", "locations"]),
        ("material", "MATERIAL", ["material", "materials"]),
        ("misc", "MISC", ["misc", "miscellaneous"])
    ];

    /// <summary>
    /// Makes sure the <see cref="DefaultRoots"/> are root nodes of the ontology: a missing one is created on the server
    /// (an ontology class, public), a deleted one is restored. Returns what was done ("created persons, location…"),
    /// empty when they were all there.
    /// </summary>
    private async Task<string> EnsureDefaultRootsAsync(CancellationToken cancellationToken)
    {
        var ids = _allNodes.Select(n => n.Id).ToHashSet();
        var roots = _allNodes.Where(n => n.ToEntry() is var e && (string.IsNullOrEmpty(e.NodeParentId) || !ids.Contains(e.NodeParentId))).ToList();
        var created = new List<string>();
        var restored = new List<string>();
        foreach (var (name, entityType, variants) in DefaultRoots)
        {
            var existing = roots.Where(r => r.Id == DefaultRootIdPrefix + name || variants.Contains(r.ToEntry().Name?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)).ToList();
            if (existing.Any(r => !r.IsDeleted))
                continue;
            if (existing.FirstOrDefault() is { } deleted)
            {
                if (await deleted.SetDeletedAsync(false, cancellationToken))
                    restored.Add(name);
                continue;
            }
            var entry = new OntologyEntry
            {
                // A fixed id: two windows opened before Elasticsearch shows the new root save the same document.
                Id = DefaultRootIdPrefix + name,
                Name = name,
                EntityType = entityType,
                Description = $"Default root of the ontology: {name}.",
                IsOntologyClass = true,
                Confidentiality = DefaultConfidentiality,
                IsDeleted = false,
                Date = DateTime.Now,
                DateCreated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                AppName = "KAONOMI.Chat",
                MachineName = Environment.MachineName,
                UserName = Environment.UserName
            };
            var result = await _assistant.CreateOntologyAsync(entry, cancellationToken);
            if (!result.IsSuccess)
            {
                ActivityLog.Error(LogArea, $"Default root \"{name}\" not created: {result.Message}");
                continue;
            }
            EnsureEntityTypeOption(entityType);
            _allNodes.Add(new OntologyNodeViewModel(entry, this, _assistant));
            created.Add(name);
        }
        var done = new List<string>();
        if (created.Count > 0)
            done.Add("created " + string.Join(", ", created));
        if (restored.Count > 0)
            done.Add("restored " + string.Join(", ", restored));
        if (done.Count > 0)
            ActivityLog.Info(LogArea, "Default roots of the ontology: " + string.Join("; ", done));
        return string.Join("; ", done);
    }

    /// <summary>Reloads every node with <c>POST /ontology/last</c> and rebuilds the tree. Unsaved edits are lost.</summary>
    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        IsError = false;
        Status = "Loading the ontology…";
        var selectedId = SelectedNode?.Id;

        try
        {
            var entries = await _assistant.GetLastOntologiesAsync("*", MaxNodes, cancellationToken);

            _allNodes.Clear();
            // Duplicated or empty ids cannot be placed reliably in the tree: keep the first occurrence only.
            foreach (var entry in entries.Where(e => !string.IsNullOrEmpty(e.Id)).DistinctBy(e => e.Id))
                _allNodes.Add(new OntologyNodeViewModel(entry, this, _assistant));
            var defaults = await EnsureDefaultRootsAsync(cancellationToken);

            BuildTree();
            _searchHits = string.IsNullOrWhiteSpace(Term)
                ? null
                : _allNodes.Where(n => n.MatchesText(Term.Trim())).ToHashSet();
            ApplyFilter();
            SelectedNode = _allNodes.FirstOrDefault(n => n.Id == selectedId);

            var deleted = _allNodes.Count(n => n.IsDeleted);
            var skipped = entries.Count - _allNodes.Count;
            Status = $"{_allNodes.Count} node(s) · {deleted} deleted"
                     + (skipped > 0 ? $" · {skipped} skipped (empty or duplicated id)" : string.Empty)
                     + (entries.Count >= MaxNodes ? $" · limit of {MaxNodes} reached, some nodes may be missing" : string.Empty)
                     + (defaults.Length > 0 ? $" · default roots {defaults}" : string.Empty);
                     LogOutcome("Ontology loaded");
        }
        catch (OperationCanceledException)
        {
            Status = "Loading cancelled";
            LogOutcome("Load");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
            LogOutcome("Load");
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = ex.Message;
            LogOutcome("Load");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Filters as the user types: local match at once, server hits after a short pause.</summary>
    partial void OnTermChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        _ = RunSearchAsync(value, TimeSpan.FromMilliseconds(300), _searchCts.Token);
    }

    /// <summary>Runs the search now (Enter / Search button) and selects the first match.</summary>
    [RelayCommand]
    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        await RunSearchAsync(Term, TimeSpan.Zero, _searchCts.Token);
        SelectFirstHit();
    }

    /// <summary>Removes the search filter and shows the whole tree again.</summary>
    [RelayCommand]
    private void ClearSearch()
    {
        _searchCts?.Cancel();
        Term = string.Empty;
    }

    /// <summary>
    /// Filters the tree to nodes matching <paramref name="text"/>. Local matching (every text field,
    /// case and accent insensitive) is applied immediately so the search never depends on the server;
    /// <c>POST /ontology/search</c> hits (description field) are merged in when the server answers.
    /// Ancestors of a match stay visible so each hit keeps its place in the hierarchy.
    /// </summary>
    private async Task RunSearchAsync(string text, TimeSpan serverDelay, CancellationToken cancellationToken)
    {
        var term = text.Trim();
        if (term.Length == 0)
        {
            _searchHits = null;
            ApplyFilter();
            IsError = false;
            Status = $"{_allNodes.Count} node(s)";
            LogOutcome("Search cleared");
            return;
        }

        var hits = _allNodes.Where(n => n.MatchesText(term)).ToHashSet();
        _searchHits = hits;
        ApplyFilter();
        IsError = false;
        Status = DescribeHits(hits, term) + " · searching the server…";

        try
        {
            if (serverDelay > TimeSpan.Zero)
                await Task.Delay(serverDelay, cancellationToken);

            // The shared HTTP client has no timeout: bound the call so a stalled server cannot freeze the search.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var serverHits = await _assistant.SearchOntologiesAsync(term, SearchSize, timeout.Token);
            cancellationToken.ThrowIfCancellationRequested();

            var byId = _allNodes.ToDictionary(n => n.Id);
            var added = 0;
            foreach (var entry in serverHits)
                if (entry.Id is { } id && byId.TryGetValue(id, out var node) && hits.Add(node))
                    added++;

            if (added > 0)
                ApplyFilter();
            Status = DescribeHits(hits, term);
            LogOutcome($"Search \"{term}\"");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Superseded by a newer search or cleared: the newer call owns the status.
        }
        catch (Exception ex)
        {
            // The local result is already shown; only report that the server part is missing.
            Status = DescribeHits(hits, term) + $" · server search unavailable ({ex.Message})";
            ActivityLog.Error(LogArea, $"Search \"{term}\": {Status}");
        }
    }

    private string DescribeHits(HashSet<OntologyNodeViewModel> hits, string term)
    {
        var visible = hits.Count(n => ShowDeleted || !IsHiddenByDeletion(n));
        return visible == 0 ? $"No node matches \"{term}\"" : $"{visible} node(s) match \"{term}\"";
    }

    /// <summary>Selects the first visible match in tree order.</summary>
    private void SelectFirstHit()
    {
        if (_searchHits is null)
            return;

        var first = FlattenItems(RootItems).FirstOrDefault(i => i.Node.IsMatch);
        if (first is not null)
            SelectedTreeItem = first;
    }

    /// <summary>Adds an unsaved root node.</summary>
    [RelayCommand]
    private void AddRoot() => InsertNew(OntologyNodeViewModel.CreateNew(null, this, _assistant), parent: null);

    /// <summary>Expands every visible node.</summary>
    [RelayCommand]
    private void ExpandAll() => SetExpanded(RootItems, true);

    /// <summary>Collapses every visible node.</summary>
    [RelayCommand]
    private void CollapseAll() => SetExpanded(RootItems, false);

    /// <summary>Adds an unsaved child under <paramref name="parent"/> and opens it in the editor.</summary>
    public void AddChild(OntologyNodeViewModel parent) =>
        InsertNew(OntologyNodeViewModel.CreateNew(parent.Id, this, _assistant,
            ConfidentialityRank(parent.Confidentiality) > 0 ? parent.Confidentiality : null), parent);

    /// <summary>
    /// Adds an unsaved copy of <paramref name="node"/> next to it (same parent, every field copied, "(copy)" added to
    /// the name, a new id, no virtual parent) and opens it in the Entity window.
    /// </summary>
    public void CloneNode(OntologyNodeViewModel node)
    {
        var entry = node.ToEntry();
        entry.Id = Guid.NewGuid().ToString();
        entry.Name = (entry.Name ?? string.Empty) + " (copy)";
        entry.IsDeleted = false;
        entry.VirtualParentIds = null;
        entry.Date = DateTime.Now;
        entry.DateCreated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        entry.AppName = "KAONOMI.Chat";
        entry.MachineName = Environment.MachineName;
        entry.UserName = Environment.UserName;
        ActivityLog.Info(LogArea, $"Clone of \"{node.DisplayName}\" started (unsaved)");
        InsertNew(new OntologyNodeViewModel(entry, this, _assistant, isNew: true), node.Parent);
    }

    /// <summary>
    /// The nodes matching <paramref name="text"/> for the search of the Entity window: the local match (every text
    /// field, accents ignored) plus the hits of <c>POST /ontology/search</c> (description) when the server answers;
    /// deleted nodes only while "Show deleted" is on. Returns the nodes by name, and whether the server answered.
    /// </summary>
    public async Task<(List<OntologyNodeViewModel> Nodes, bool ServerAnswered)> FindNodesAsync(string text, CancellationToken cancellationToken)
    {
        var term = text.Trim();
        if (term.Length == 0)
            return ([], true);
        var hits = _allNodes.Where(n => !n.IsNew && n.MatchesText(term)).ToHashSet();
        var answered = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            foreach (var entry in await _assistant.SearchOntologiesAsync(term, SearchSize, timeout.Token))
                if (FindNode(entry.Id) is { } node)
                    hits.Add(node);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            answered = false;
            ActivityLog.Error(LogArea, $"Entity search \"{term}\": server search unavailable ({ex.Message})");
        }
        var nodes = hits.Where(n => ShowDeleted || !IsHiddenByDeletion(n))
            .OrderBy(n => n.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        ActivityLog.Info(LogArea, $"Entity search \"{term}\": {nodes.Count} node(s)");
        return (nodes, answered);
    }

    /// <summary>The path of <paramref name="node"/> in the tree ("root › … › parent"), empty for a root.</summary>
    public static string PathOf(OntologyNodeViewModel node)
    {
        var path = new List<string>();
        for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
            path.Insert(0, ancestor.DisplayName);
        return string.Join(" › ", path);
    }

    /// <summary>Selects <paramref name="node"/> and asks the view to show it in the Entity window.</summary>
    public void EditNode(OntologyNodeViewModel node)
    {
        SelectedNode = node;
        EditRequested?.Invoke(node);
    }

    /// <summary>Raised when a never-saved node is dropped (its Entity window closes).</summary>
    public event Action<OntologyNodeViewModel>? UnsavedNodeRemoved;

    /// <summary>Drops a never-saved node from the tree.</summary>
    public void RemoveUnsavedNode(OntologyNodeViewModel node)
    {
        UnsavedNodeRemoved?.Invoke(node);
    ActivityLog.Info(LogArea, $"Unsaved node \"{node.DisplayName}\" removed");
        _allNodes.Remove(node);
        node.Parent?.AllChildren.Remove(node);
        var parent = node.Parent;
        ApplyFilter();
        SelectedNode = parent;
    }

    /// <summary>
    /// Soft-deletes <paramref name="node"/> and its descendants (<c>isDeleted = true</c> via
    /// <c>POST /ontology/update</c>), so no live node is left hidden under a deleted parent.
    /// </summary>
    public async Task DeleteNodeAsync(OntologyNodeViewModel node)
    {
        if (node.IsNew)
        {
            RemoveUnsavedNode(node);
            return;
        }

        var targets = Descendants(node).Prepend(node).Where(n => !n.IsDeleted && !n.IsNew).ToList();
        var unsavedChildren = Descendants(node).Where(n => n.IsNew).ToList();
        var others = targets.Count - 1;
        var question = others == 0
            ? $"Delete \"{node.DisplayName}\"?"
            : $"Delete \"{node.DisplayName}\" and its {others} descendant node(s)?";
        if (!await ConfirmAsync(question + "\n\nNodes are only flagged as deleted and can be restored with \"Show deleted\".", "Delete"))
            return;

        IsBusy = true;
        IsError = false;
        var done = 0;
        try
        {
            foreach (var target in targets)
            {
                Status = $"Deleting {done + 1}/{targets.Count}…";
                if (!await target.SetDeletedAsync(true))
                {
                    IsError = true;
                    Status = $"Deleted {done}/{targets.Count}; \"{target.DisplayName}\" failed: {target.Status}";
                    LogOutcome($"Delete \"{node.DisplayName}\"");
                    return;
                }
                done++;
            }

            foreach (var unsaved in unsavedChildren)
                RemoveUnsavedNode(unsaved);

            Status = $"Deleted {done} node(s)";
            LogOutcome($"Delete \"{node.DisplayName}\"");
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = $"Deleted {done}/{targets.Count}; error: {ex.Message}";
            LogOutcome($"Delete \"{node.DisplayName}\"");
        }
        finally
        {
            IsBusy = false;
            ApplyFilter();
        }
    }

    /// <summary>
    /// Undeletes (<c>isDeleted = false</c> via <c>POST /ontology/update</c>) <paramref name="node"/> and the
    /// deleted ancestors needed to make it visible; with <paramref name="includeDescendants"/>, also every
    /// deleted node below it (the reverse of the cascading delete).
    /// </summary>
    public async Task RestoreNodeAsync(OntologyNodeViewModel node, bool includeDescendants)
    {
        var targets = new List<OntologyNodeViewModel>();
        for (var current = node.Parent; current is not null; current = current.Parent)
            if (current.IsDeleted)
                targets.Insert(0, current);
        if (node.IsDeleted)
            targets.Add(node);
        if (includeDescendants)
            targets.AddRange(Descendants(node).Where(n => n.IsDeleted && !n.IsNew));

        if (targets.Count == 0)
        {
            IsError = false;
            Status = $"Nothing to undelete under \"{node.DisplayName}\"";
            LogOutcome("Undelete");
            return;
        }

        IsBusy = true;
        IsError = false;
        var done = 0;
        try
        {
            foreach (var target in targets)
            {
                Status = $"Undeleting {done + 1}/{targets.Count}…";
                if (!await target.SetDeletedAsync(false))
                {
                    IsError = true;
                    Status = $"Undeleted {done}/{targets.Count}; \"{target.DisplayName}\" failed: {target.Status}";
                    LogOutcome("Undelete");
                    return;
                }
                done++;
            }

            Status = done == 1
                ? $"Undeleted \"{targets[0].DisplayName}\""
                : $"Undeleted {done} node(s) from \"{node.DisplayName}\"";
                LogOutcome("Undelete");
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = $"Undeleted {done}/{targets.Count}; error: {ex.Message}";
            LogOutcome("Undelete");
        }
        finally
        {
            IsBusy = false;
            ApplyFilter();
        }
    }

    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        // Keep accents readable in the exported file.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// JSON export of <paramref name="node"/> and its live descendants (deleted and unsaved nodes are skipped).
    /// <c>id</c> and <c>nodeparentId</c> are removed: the hierarchy is kept by nesting nodes in <c>children</c>.
    /// </summary>
    public string BuildExport(OntologyNodeViewModel node) => ToExportNode(node).ToJsonString(ExportJsonOptions);

    private static JsonObject ToExportNode(OntologyNodeViewModel node)
    {
        var json = JsonSerializer.SerializeToNode(node.Entry)!.AsObject();
        json.Remove("id");
        json.Remove("nodeparentId");
        // Virtual parents point to ids of this ontology: meaningless once imported elsewhere with new ids.
        json.Remove("nodevirtualeftchildrenId");

        var children = node.AllChildren
            .Where(child => !child.IsDeleted && !child.IsNew)
            .Select(child => (JsonNode)ToExportNode(child))
            .ToArray();
        if (children.Length > 0)
            json["children"] = new JsonArray(children);
        return json;
    }

    /// <summary>Number of nodes in an export object, children included.</summary>
    private static int CountNodes(JsonObject node) =>
        1 + ((node["children"] as JsonArray)?.OfType<JsonObject>().Sum(CountNodes) ?? 0);

    /// <summary>Exports a node and its descendants to a JSON file chosen by the user.</summary>
    public async Task ExportNodeAsync(OntologyNodeViewModel node)
    {
        IsError = false;
        try
        {
            var export = ToExportNode(node);
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var safeName = new string(node.DisplayName.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            var fileName = await SaveTextFileAsync($"ontology-{safeName}.json", export.ToJsonString(ExportJsonOptions));

            Status = fileName is null
                ? "Export cancelled"
                : $"Exported {CountNodes(export)} node(s) from \"{node.DisplayName}\" to {fileName}";
                LogOutcome("Export");
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = $"Export failed: {ex.Message}";
            LogOutcome("Export");
        }
    }

    /// <summary>
    /// Imports a JSON export (one node object or an array of them, with nested <c>children</c>) under
    /// <paramref name="parent"/>: every node gets a new id, is linked to its new parent and is created with
    /// <c>PUT /ontology</c>. Ids found in the file are ignored.
    /// </summary>
    public async Task ImportIntoNodeAsync(OntologyNodeViewModel parent)
    {
        IsError = false;
        string? text;
        try
        {
            text = await OpenTextFileAsync();
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = $"Could not read the file: {ex.Message}";
            LogOutcome("Import");
            return;
        }

        if (text is null)
        {
            Status = "Import cancelled";
            LogOutcome("Import");
            return;
        }

        List<JsonObject> roots;
        try
        {
            roots = JsonNode.Parse(text) switch
            {
                JsonArray array => array.OfType<JsonObject>().ToList(),
                JsonObject single => [single],
                _ => []
            };
        }
        catch (JsonException ex)
        {
            IsError = true;
            Status = $"Invalid JSON file: {ex.Message}";
            LogOutcome("Import");
            return;
        }

        if (roots.Count == 0)
        {
            IsError = true;
            Status = "The file contains no ontology node.";
            LogOutcome("Import");
            return;
        }

        var total = roots.Sum(CountNodes);
        if (!await ConfirmAsync($"Import {total} node(s) under \"{parent.DisplayName}\"?\n\nEach node is created with a new id.", "Import"))
            return;

        IsBusy = true;
        var imported = 0;
        try
        {
            foreach (var root in roots)
                await ImportNodeAsync(root, parent);

            Status = $"Imported {imported} node(s) under \"{parent.DisplayName}\"";
            LogOutcome("Import");
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = $"Imported {imported}/{total} node(s); error: {ex.Message}";
            LogOutcome("Import");
        }
        finally
        {
            IsBusy = false;
            parent.AllChildren.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
            parent.IsExpanded = true;
            ApplyFilter();
        }

        async Task ImportNodeAsync(JsonObject source, OntologyNodeViewModel newParent)
        {
            var fields = source.DeepClone().AsObject();
            fields.Remove("children");
            fields.Remove("id");
            fields.Remove("nodeparentId");
            fields.Remove("nodevirtualeftchildrenId");

            var entry = fields.Deserialize<OntologyEntry>() ?? new OntologyEntry();
            entry.Id = Guid.NewGuid().ToString();
            entry.NodeParentId = newParent.Id;
            entry.IsDeleted = false;
            entry.Date = DateTime.Now;
            entry.DateCreated = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            entry.AppName = "KAONOMI.Chat";
            entry.MachineName = Environment.MachineName;
            entry.UserName = Environment.UserName;

            // Never less confidential than the new parent (or its ancestors); missing level → the parent's or PUBLIC(PU).
            var level = ResolveConfidentiality(entry.Confidentiality);
            if (ConfidentialityRank(level) == 0)
                level = DefaultConfidentiality;
            foreach (var floor in new[] { newParent.Confidentiality, MinimumConfidentiality(newParent) })
                if (ConfidentialityRank(floor) > ConfidentialityRank(level))
                    level = floor!;
            entry.Confidentiality = level;

            var result = await _assistant.CreateOntologyAsync(entry);
            if (!result.IsSuccess)
                throw new InvalidOperationException(result.Message ?? $"The server rejected \"{entry.Name}\".");

            var node = new OntologyNodeViewModel(entry, this, _assistant) { Parent = newParent };
            newParent.AllChildren.Add(node);
            _allNodes.Add(node);
            _searchHits?.Add(node);
            imported++;
            Status = $"Importing {imported}/{total}…";

            foreach (var child in (source["children"] as JsonArray)?.OfType<JsonObject>() ?? [])
                await ImportNodeAsync(child, node);
        }
    }

    private void InsertNew(OntologyNodeViewModel node, OntologyNodeViewModel? parent)
    {
    ActivityLog.Info(LogArea, parent is null ? "New root node started (unsaved)" : $"New child node started under \"{parent.DisplayName}\" (unsaved)");
        node.Parent = parent;
        parent?.AllChildren.Add(node);
        _allNodes.Add(node);

        // A new node must be visible even when a search is active.
        _searchHits?.Add(node);
        if (parent is not null)
            parent.IsExpanded = true;

        ApplyFilter();
        EditNode(node);
    }

    /// <summary>Links nodes to their parent; unknown parents and cycles put the node at the root.</summary>
    private void BuildTree()
    {
        var byId = _allNodes.ToDictionary(n => n.Id);
        foreach (var node in _allNodes)
        {
            node.AllChildren.Clear();
            node.Parent = null;
            node.IsOrphan = false;
        }

        foreach (var node in _allNodes)
        {
            if (string.IsNullOrEmpty(node.ParentId))
                continue;

            if (!byId.TryGetValue(node.ParentId, out var parent) || CreatesCycle(node, parent))
            {
                node.IsOrphan = true;
                continue;
            }

            node.Parent = parent;
            parent.AllChildren.Add(node);
        }

        // Virtual links: ids pointing to a missing node, to the node itself or to its real parent are ignored.
        _virtualChildren.Clear();
        foreach (var node in _allNodes)
        {
            foreach (var parentId in node.VirtualParentIds.Distinct())
            {
                if (parentId == node.Id || parentId == node.ParentId || !byId.ContainsKey(parentId))
                    continue;
                if (!_virtualChildren.TryGetValue(parentId, out var list))
                    _virtualChildren[parentId] = list = [];
                list.Add(node);
            }
        }

        foreach (var node in _allNodes)
        {
            node.AllChildren.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
            node.RefreshParentLabel();
        }
    }

    private static bool CreatesCycle(OntologyNodeViewModel node, OntologyNodeViewModel parent)
    {
        for (var current = parent; current is not null; current = current.Parent)
            if (ReferenceEquals(current, node))
                return true;
        return false;
    }

    /// <summary>Rebuilds the visible tree (occurrences) from the deleted and search filters.</summary>
    private void ApplyFilter()
    {
        var selectedNode = SelectedNode;
        var selectedVirtualParent = SelectedTreeItem?.VirtualParent;

        RootItems.Clear();
        foreach (var root in _allNodes.Where(n => n.Parent is null)
                     .OrderBy(n => n.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            if (Fill(root, virtualParent: null, inVirtualBranch: false, path: []) is { } item)
                RootItems.Add(item);
        }

        OnPropertyChanged(nameof(HasNodes));

        // Rebuilding the collections clears the TreeView selection; put it back on the same occurrence when visible.
        _isRestoringSelection = true;
        try
        {
            SelectedTreeItem = null;
            SelectedTreeItem = selectedNode is null
                ? null
                : (selectedVirtualParent is not null ? FindItem(selectedNode, selectedVirtualParent) : null) ?? FindItem(selectedNode);
            if (SelectedTreeItem is null)
                SelectedNode = null;
        }
        finally
        {
            _isRestoringSelection = false;
        }
    }

    /// <summary>
    /// Builds the occurrence of <paramref name="node"/> with its visible children: real children, then nodes attached
    /// under it virtually (each with all their children). Returns null when hidden; <paramref name="path"/> prevents loops.
    /// </summary>
    private OntologyTreeItem? Fill(OntologyNodeViewModel node, OntologyNodeViewModel? virtualParent, bool inVirtualBranch, HashSet<OntologyNodeViewModel> path)
    {
        if ((node.IsDeleted && !ShowDeleted) || !path.Add(node))
            return null;

        var item = new OntologyTreeItem(node, virtualParent, inVirtualBranch);
        var children = node.AllChildren.Select(child => (Child: child, IsVirtual: false))
            .Concat(VirtualChildrenOf(node).Select(child => (Child: child, IsVirtual: true)))
            .OrderBy(c => c.Child.DisplayName, StringComparer.CurrentCultureIgnoreCase);
        foreach (var (child, isVirtual) in children)
        {
            if (Fill(child, isVirtual ? node : null, inVirtualBranch || isVirtual, path) is { } childItem)
                item.Children.Add(childItem);
        }
        path.Remove(node);

        node.IsMatch = _searchHits?.Contains(node) == true;
        if (_searchHits is null)
            return item;

        // In search mode keep matches, and ancestors of matches expanded so the hits are visible.
        if (item.Children.Count > 0)
            item.IsExpanded = true;
        return node.IsMatch || item.Children.Count > 0 ? item : null;
    }

    private bool IsVisible(OntologyNodeViewModel node) => FindItem(node) is not null;

    private bool IsHiddenByDeletion(OntologyNodeViewModel node)
    {
        for (var current = node; current is not null; current = current.Parent)
            if (current.IsDeleted)
                return true;
        return false;
    }

    private static IEnumerable<OntologyNodeViewModel> Descendants(OntologyNodeViewModel node) =>
        node.AllChildren.SelectMany(child => Descendants(child).Prepend(child));

    private static void SetExpanded(IEnumerable<OntologyTreeItem> items, bool expanded)
    {
        foreach (var item in items)
        {
            item.IsExpanded = expanded;
            SetExpanded(item.Children, expanded);
        }
    }

    /// <inheritdoc />
    public string NoteWindowName => "Ontology";

    /// <inheritdoc />
    public object? GetNoteContext()
    {
        var path = new List<string>();
        for (var ancestor = SelectedNode?.Parent; ancestor is not null; ancestor = ancestor.Parent)
            path.Insert(0, ancestor.DisplayName);

        return new
        {
            searchTerm = Term,
            showDeleted = ShowDeleted,
            nodeCount = _allNodes.Count,
            selectedNode = SelectedNode?.ToEntry(),
            selectedNodePath = path,
            shownUnderVirtualParent = SelectedTreeItem?.VirtualParent?.DisplayName
        };
    }
}
