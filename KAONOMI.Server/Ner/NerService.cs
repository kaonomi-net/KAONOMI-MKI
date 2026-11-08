using System.Diagnostics;
using KAONOMI.Core;
using KAONOMI.Core.Models;
using KAONOMI.LLMGateway.Entities;

namespace KAONOMI.Server.Ner
{
    /// <summary>The body of <c>POST /ner</c>: the text, and the types wanted (all when empty).</summary>
    public sealed class NerRequest
    {
        public string text { get; set; } = string.Empty;

        public List<string>? types { get; set; }
    }

    /// <summary>The answer of <c>POST /ner</c>: the entities, how long it took, the model, and what did not work.</summary>
    public sealed class NerResult
    {
        public List<NerEntity> entities { get; set; } = [];

        public double durationMs { get; set; }

        public string model { get; set; } = string.Empty;

        public List<string> warnings { get; set; } = [];
    }

    /// <summary>
    /// The NER of the server, in three layers: the rules (URL, EMAIL, IP, DATE, TIME, MONEY, PERCENT, PHONE, NUMBER,
    /// FILE, HASHTAG, MENTION, CODE, PROGRAMMING_LANGUAGE, MATH; the extractor of KAONOMI.LLMGateway, its source shared),
    /// the model XLM-RoBERTa int8 (PERSON, ORG, LOC, in context) and the dictionary of the ontology (every type of it).
    /// An entity of the model that is a title of the ontology takes the type and the id of the ontology
    /// ("xlmr+ontology"); a title of the ontology the model did not see is added ("ontology"); a rule never covers a
    /// title. The ontology is read again every 10 minutes. Without the model (its folder missing), the rules and the
    /// ontology still answer, with a warning.
    /// </summary>
    public sealed class NerService
    {
        private static readonly TimeSpan OntologyRefresh = TimeSpan.FromMinutes(10);

        /// <summary>The types of the rules kept: the names (PERSON, PROPER_NOUN) are left to the model, the language is not an entity.</summary>
        private static readonly HashSet<string> RuleTypes =
        [
            EntityTypes.Url, EntityTypes.Email, EntityTypes.Ip, EntityTypes.Date, EntityTypes.Time, EntityTypes.Money, EntityTypes.Percent,
            EntityTypes.Phone, EntityTypes.Number, EntityTypes.File, EntityTypes.Hashtag, EntityTypes.Mention, EntityTypes.Code,
            EntityTypes.ProgrammingLanguage, EntityTypes.Math
        ];

        /// <summary>The rules that hold their text whole: no title is looked for inside them.</summary>
        private static readonly HashSet<string> ContainerTypes =
            [EntityTypes.Url, EntityTypes.Email, EntityTypes.File, EntityTypes.Code, EntityTypes.Ip, EntityTypes.Hashtag, EntityTypes.Mention];

        private static readonly object Gate = new();
        private static NerService? _shared;

        private readonly Config _config;
        private readonly EntityExtractor _rules = new();
        private readonly XlmrNer? _model;
        private readonly string? _modelError;
        private OntologyGazetteer _gazetteer = new([]);
        private DateTime _gazetteerRead = DateTime.MinValue;
        private string? _ontologyError;

        private NerService(Config config)
        {
            _config = config;
            var folder = config.nerModelFolder ?? DefaultModelFolder;
            try
            {
                _model = new XlmrNer(folder);
                ModelWarmMs = _model.Warm();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException
                or KeyNotFoundException or DllNotFoundException or TypeInitializationException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
            {
                _modelError = $"The NER model could not be loaded from {folder}: {ex.Message}";
            }
        }

        /// <summary>The folder of the NER models: ~/kaonomi/models/ner.</summary>
        private static string ModelsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "kaonomi", "models", "ner");

        /// <summary>
        /// Where the model is when config.json does not say (nerModelFolder):
        /// FacebookAI/xlm-roberta-large-finetuned-conll03-english (int8), or the former Davlan/xlm-roberta-base-ner-hrl
        /// when the large one has not been exported (Ner/export/ner_export.py).
        /// </summary>
        public static string DefaultModelFolder
        {
            get
            {
                var large = Path.Combine(ModelsFolder, "xlm-roberta-large-finetuned-conll03-english-int8");
                var former = Path.Combine(ModelsFolder, "xlm-roberta-base-ner-hrl-int8");
                return Directory.Exists(large) || !Directory.Exists(former) ? large : former;
            }
        }

        /// <summary>The service of the server, made at the first call (the model loaded then, in about a second).</summary>
        public static NerService Get(Config config)
        {
            lock (Gate)
                return _shared ??= new NerService(config);
        }

        /// <summary>The time of a short sentence after the start, in milliseconds (0 without the model).</summary>
        public double ModelWarmMs { get; }

        public string ModelName => _model is null ? "none" : $"{_model.ModelId} int8 ({_model.ModelFile})";

        /// <summary>The entities of <paramref title="text"/> of the types <paramref title="types"/> (all when empty).</summary>
        public NerResult Extract(string? text, IReadOnlyCollection<string>? types = null)
        {
            var watch = Stopwatch.StartNew();
            var result = new NerResult { model = ModelName };
            if (_modelError is not null)
                result.warnings.Add(_modelError);
            if (string.IsNullOrWhiteSpace(text))
                return result;

            var gazetteer = Gazetteer(result.warnings);
            var rules = _rules.Extract(text).Where(r => RuleTypes.Contains(r.Type))
                .Select(r => new NerEntity { text = r.Text, type = r.Type, start = r.Start, end = r.Start + r.Length, score = 1, source = "rule" })
                .ToList();
            // A URL, an e-mail, a file, code… hold no title: "kaonomi" of https://www.kaonomi.net is the URL only.
            var containers = rules.Where(r => ContainerTypes.Contains(r.type)).ToList();
            bool Inside(NerEntity e) => containers.Any(c => c.start <= e.start && e.end <= c.end);
            var names = (_model?.Extract(text) ?? []).Where(e => !Inside(e)).ToList();
            var known = gazetteer.Find(text).Where(e => !Inside(e)).ToList();

            // A title of the model that a title of the ontology covers whole (the same, or longer: "Université de Paris" for
            // "Paris") takes its place, its type and its id. A title of the ontology inside a longer title of the model is
            // not that title: "Olaf" (the OLAF, the anti-fraud office) in "Olaf Scholz" leaves Olaf Scholz a person.
            foreach (var name in names)
            {
                var match = known.Where(k => k.start <= name.start && name.end <= k.end)
                    .OrderBy(k => k.end - k.start).FirstOrDefault();
                if (match is null)
                    continue;
                name.start = match.start;
                name.end = match.end;
                name.text = match.text;
                name.type = match.type;
                name.ontologyId = match.ontologyId;
                name.ontologyName = match.ontologyName;
                name.source = "xlmr+ontology";
            }
            // Two names of the model inside the same title of the ontology are that title once.
            names = names.GroupBy(n => (n.start, n.end)).Select(g => g.OrderByDescending(n => n.score).First()).ToList();
            var entities = new List<NerEntity>(containers);
            entities.AddRange(names);
            entities.AddRange(known.Where(k => !names.Any(n => n.start < k.end && k.start < n.end)));
            // The other rules (a date, an amount…) where no title already is.
            foreach (var rule in rules.Where(r => !ContainerTypes.Contains(r.type)))
                if (!entities.Any(e => e.start < rule.end && rule.start < e.end))
                    entities.Add(rule);
            if (types is { Count: > 0 })
            {
                var wanted = types.Select(t => t.Trim().ToUpperInvariant()).ToHashSet();
                entities = entities.Where(e => wanted.Contains(e.type)).ToList();
            }
            result.entities = entities.OrderBy(e => e.start).ThenBy(e => e.end).ToList();
            result.durationMs = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
            return result;
        }

        /// <summary>The types found: those of the model, of the rules and of the ontology.</summary>
        public object Types()
        {
            var gazetteer = Gazetteer([]);
            return new
            {
                model = _model is null ? Array.Empty<string>() : _model.Types,
                rules = RuleTypes.OrderBy(t => t).ToList(),
                ontology = gazetteer.Types,
                ontologyEntities = gazetteer.Count,
                modelName = ModelName,
                modelWarmMs = Math.Round(ModelWarmMs, 1),
                warnings = new[] { _modelError, _ontologyError }.Where(w => w is not null).ToList()
            };
        }

        /// <summary>The dictionary of the ontology, read again when it is older than 10 minutes (kept as it was when the search engine does not answer).</summary>
        private OntologyGazetteer Gazetteer(List<string> warnings)
        {
            lock (Gate)
            {
                if (DateTime.Now - _gazetteerRead > OntologyRefresh)
                {
                    _gazetteerRead = DateTime.Now;
                    try
                    {
                        _gazetteer = new OntologyGazetteer(new SessionManager(_config).OntologiesAll());
                        _ontologyError = null;
                    }
                    catch (Exception ex)
                    {
                        _ontologyError = $"The ontology could not be read: {ex.Message}";
                    }
                }
                if (_ontologyError is not null)
                    warnings.Add(_ontologyError);
                return _gazetteer;
            }
        }
    }
}
