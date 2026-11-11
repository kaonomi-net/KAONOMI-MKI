using KAONOMI.Core;
using KAONOMI.Core.Models;

namespace KAONOMI.Server.Knowledge
{
    /// <summary>
    /// What Kaonomi knows about the entities of a prompt, for the model that will answer it: the entities of the
    /// ontology the prompt names (their type, summary, full description, synonyms, dates), the summary of the entities
    /// they list as related, and the intels that match these entities
    /// (their instructions, places, times, related entities, questions). Used by the agent of the entities, after the
    /// NER; the processor writes the result in the prompt.
    /// </summary>
    public static class EntityKnowledge
    {
        /// <summary>
        /// An entity of the ontology classified SECRET or above is not written in a prompt: the model that answers may
        /// be outside the machine (the gateway routes).
        /// </summary>
        private const int MaxConfidentialityLevel = 2;

        /// <summary>The entities of the ontology found by the NER, with the node of each (deleted ones and classes left out).</summary>
        public static List<(Ontology Node, List<NerEntity> Entities)> OntologyNodes(IEnumerable<NerEntity> entities, SessionManager session, List<string> log)
        {
            var nodes = new List<(Ontology, List<NerEntity>)>();
            foreach (var group in entities.Where(e => !string.IsNullOrWhiteSpace(e.ontologyId)).GroupBy(e => e.ontologyId!))
            {
                try
                {
                    var node = session.OntologyGet(group.Key);
                    if (node is null || node.isDeleted)
                        continue;
                    nodes.Add((node, group.ToList()));
                }
                catch (Exception ex)
                {
                    log.Add($"ontology {group.Key} not read: {ex.Message}");
                }
            }
            return nodes;
        }

        /// <summary>
        /// The knowledge of the ontology: one item per entity the prompt names that has something to say: its summary
        /// then its full description (or dates); an entity with only a name adds nothing to a prompt that already names it.
        /// </summary>
        public static List<DialogKnowledge> FromOntology(IEnumerable<(Ontology Node, List<NerEntity> Entities)> nodes, List<string> log)
        {
            var items = new List<DialogKnowledge>();
            foreach (var (node, entities) in nodes)
            {
                var name = string.IsNullOrWhiteSpace(node.name) ? entities[0].ontologyName ?? entities[0].text : node.name.Trim();
                if (node.confidentialityLevel > MaxConfidentialityLevel)
                {
                    log.Add($"{name}: {node.Confidentiality}, not written in the prompt");
                    continue;
                }
                // The summary first, then the full description (when it says something else).
                var summary = (node.summary ?? string.Empty).Trim();
                var full = FullDescription(node);
                var description = summary.Length == 0 ? full
                    : full.Length == 0 || string.Equals(full, summary, StringComparison.OrdinalIgnoreCase) ? summary
                    : summary + (summary.EndsWith('.') || summary.EndsWith('!') || summary.EndsWith('?') ? " " : ". ") + full;
                var dates = Dates(node);
                if (description.Length == 0 && dates.Length == 0)
                    continue;

                var text = name;
                if (!string.IsNullOrWhiteSpace(node.EntityType))
                    text += $" ({node.EntityType.Trim()})";
                var synonyms = (node.synonyms ?? []).Where(s => !string.IsNullOrWhiteSpace(s) && !string.Equals(s.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
                if (synonyms.Count > 0)
                    text += ", also called " + string.Join(", ", synonyms);
                if (description.Length > 0)
                    text += ": " + description;
                if (dates.Length > 0)
                    text += (description.Length > 0 && !description.EndsWith('.') ? "." : string.Empty) + " " + dates;

                items.Add(new DialogKnowledge
                {
                    kind = DialogKnowledge.KindOntology,
                    sourceId = node.id,
                    title = name,
                    text = text,
                    matchedTexts = entities.Select(e => e.text).Distinct().ToList()
                });
            }
            return items;
        }

        /// <summary>
        /// The full description of an entity; none for the one the seed writes to an entity that has no description
        /// (« Cité par 3 intels : … »): it lists where the entity is cited and says nothing about it (the intels
        /// themselves come by <see cref="FromIntels"/>).
        /// </summary>
        private static string FullDescription(Ontology node)
        {
            var description = (node.description ?? string.Empty).Trim();
            return description.StartsWith("Cité par ", StringComparison.OrdinalIgnoreCase) ? string.Empty : description;
        }

        // ── The related entities ──

        private const int MaxRelatedPerEntity = 12;
        private const int MaxRelated = 30;
        private static readonly TimeSpan Refresh = TimeSpan.FromMinutes(5);
        private static readonly object Gate = new();
        private static Dictionary<string, Ontology> _byName = new(StringComparer.OrdinalIgnoreCase);
        private static DateTime _read = DateTime.MinValue;

        /// <summary>
        /// The entities of the ontology by their name and their synonyms (a related entity is written by its name),
        /// and by their id; read again every five minutes. A name two entities share is the one of the first read.
        /// </summary>
        private static Dictionary<string, Ontology> ByName(SessionManager session, List<string> log)
        {
            lock (Gate)
            {
                if (DateTime.UtcNow - _read < Refresh)
                    return _byName;
                try
                {
                    var index = new Dictionary<string, Ontology>(StringComparer.OrdinalIgnoreCase);
                    var all = session.OntologiesAll();
                    foreach (var node in all)
                    {
                        if (!string.IsNullOrWhiteSpace(node.id))
                            index.TryAdd(node.id.Trim(), node);
                        if (!string.IsNullOrWhiteSpace(node.name))
                            index.TryAdd(node.name.Trim(), node);
                    }
                    foreach (var node in all)
                        foreach (var synonym in (node.synonyms ?? []).Where(s => !string.IsNullOrWhiteSpace(s)))
                            index.TryAdd(synonym.Trim(), node);
                    _byName = index;
                    _read = DateTime.UtcNow;
                }
                catch (Exception ex)
                {
                    log.Add("related entities: the ontology could not be read (" + ex.Message + ")");
                }
                return _byName;
            }
        }

        /// <summary>
        /// For each entity the prompt names, the entities it lists as related (<c>relatedEntities</c>, by name or id):
        /// their summary only, never their full description. A related entity the prompt names itself, one without a
        /// summary, a deleted one or one classified SECRET or above gives nothing; each one is written once, with the
        /// entities of the prompt it is related to.
        /// </summary>
        public static List<DialogKnowledge> FromRelated(IEnumerable<(Ontology Node, List<NerEntity> Entities)> nodes, SessionManager session, List<string> log)
        {
            var named = nodes.Select(n => n.Node.id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var items = new Dictionary<string, DialogKnowledge>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Ontology>? index = null;
            foreach (var (node, _) in nodes)
            {
                var names = (node.relatedEntities ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxRelatedPerEntity).ToList();
                if (names.Count == 0)
                    continue;
                index ??= ByName(session, log);
                var from = string.IsNullOrWhiteSpace(node.name) ? node.id : node.name.Trim();
                foreach (var name in names)
                {
                    if (!index.TryGetValue(name, out var related) || related.isDeleted || named.Contains(related.id)
                        || related.confidentialityLevel > MaxConfidentialityLevel || string.IsNullOrWhiteSpace(related.summary))
                        continue;
                    if (items.TryGetValue(related.id, out var known))
                    {
                        if (!known.matchedTexts.Contains(from, StringComparer.OrdinalIgnoreCase))
                            known.matchedTexts.Add(from);
                        continue;
                    }
                    if (items.Count >= MaxRelated)
                        continue;
                    var title = string.IsNullOrWhiteSpace(related.name) ? name : related.name.Trim();
                    items[related.id] = new DialogKnowledge
                    {
                        kind = DialogKnowledge.KindRelated,
                        sourceId = related.id,
                        title = title,
                        text = title + (string.IsNullOrWhiteSpace(related.EntityType) ? string.Empty : $" ({related.EntityType.Trim()})") + ": " + related.summary.Trim(),
                        matchedTexts = [from],
                        score = 50
                    };
                }
            }
            // What it is related to is said in the text: the model reads why it is there.
            foreach (var item in items.Values)
                item.text += (item.text.EndsWith('.') || item.text.EndsWith('!') || item.text.EndsWith('?') ? " " : ". ") + "Related to " + string.Join(", ", item.matchedTexts) + ".";
            return items.Values.ToList();
        }

        private static string Dates(Ontology node) =>
            node.startDate is { } start && node.endDate is { } end ? $"From {start:yyyy-MM-dd} to {end:yyyy-MM-dd}."
            : node.startDate is { } from ? $"Since {from:yyyy-MM-dd}."
            : node.endDate is { } until ? $"Until {until:yyyy-MM-dd}."
            : string.Empty;

        /// <summary>
        /// The intels that match the entities: the prompt is read with the names the ontology gives to its entities
        /// (their name there and their synonyms), so that an intel about « Emmanuel Macron » matches a prompt that says
        /// « Macron » or « le président ». The rules of the intels hold (weights, trigger level, excluded entities).
        /// </summary>
        public static List<DialogKnowledge> FromIntels(string? prompt, IEnumerable<NerEntity> entities,
            IEnumerable<(Ontology Node, List<NerEntity> Entities)> nodes, Config config, List<string> log)
        {
            var names = entities.Select(e => e.text)
                .Concat(nodes.SelectMany(n => new[] { n.Node.name }.Concat(n.Node.synonyms ?? [])))
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var items = new List<DialogKnowledge>();
            if (string.IsNullOrWhiteSpace(prompt) && names.Count == 0)
                return items;

            var result = Intels.IntelMatcher.Get(config).Match((prompt ?? string.Empty) + "\n" + string.Join("\n", names));
            log.AddRange(result.warnings);
            foreach (var match in result.matches.OrderByDescending(m => m.Score))
            {
                var intel = match.Intel;
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(intel.Instructions))
                    parts.Add(intel.Instructions.Trim());
                else if (!string.IsNullOrWhiteSpace(intel.comment))
                    parts.Add(intel.comment.Trim());
                Add(parts, "Where", intel.Locations);
                Add(parts, "When", intel.Times);
                Add(parts, "Related", intel.RelatedEntities);
                Add(parts, "Questions to consider", intel.Questions);
                if (parts.Count == 0)
                    continue; // An intel that only raises signals says nothing to the model.

                var title = string.IsNullOrWhiteSpace(intel.title) ? string.Join(", ", match.Found.Select(f => f.entityName)) : intel.title.Trim();
                items.Add(new DialogKnowledge
                {
                    kind = DialogKnowledge.KindIntel,
                    sourceId = intel.id,
                    title = title,
                    text = title + ": " + string.Join(" ", parts.Select(p => p.EndsWith('.') || p.EndsWith('?') || p.EndsWith('!') ? p : p + ".")),
                    matchedTexts = match.Found.Select(f => f.text ?? f.entityName).Distinct().ToList(),
                    score = match.Score
                });
            }
            return items;
        }

        private static void Add(List<string> parts, string label, List<string>? values)
        {
            var kept = (values ?? []).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct().ToList();
            if (kept.Count > 0)
                parts.Add($"{label}: {string.Join("; ", kept)}");
        }
    }
}
