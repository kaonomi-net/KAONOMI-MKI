namespace KAONOMI.Core
{
    using KAONOMI.Core.Data;
    using KAONOMI.Core.Helpers;
    using KAONOMI.Core.Models;
    using KAONOMI.Core.Models.LLM;
    using Models;
    using System;
using System.Linq;
using System.Threading.Tasks;
    using static OrleansDashboard.Implementation.DashboardTelemetryExporter;
    using static System.Runtime.InteropServices.JavaScript.JSType;

    /// <summary>
    /// Singleton access point to Elasticsearch repositories (data, dialog, admin) and Completion / perf / completion persistence.
    /// </summary>
    public class SessionManager : EntityBase
    {
        private Config _config;
        /// <summary>Elasticsearch settings of the last initialized repository (shared across branches).</summary>
        protected ElasticServerSettings ElasticSearchServerParameters;
        /// <summary>Main "data" repository.</summary>
        protected ElasticRepository ElasticSearchServer;
        /// <summary>Dialog repository (or same as server if not configured).</summary>
        protected ElasticRepository ElasticSearchDialog;
        /// <summary>Admin / logs repository (or same as server if not configured).</summary>
        protected ElasticRepository ElasticSearchAdmin;
        private static SessionManager instance;
        private static object locker = new object();



        /// <summary>Builds the manager and Elasticsearch clients from <see cref="Config"/>.</summary>
        /// <summary>The configuration the session was made with (config.json of the application).</summary>
        public Config Config => _config;

        public SessionManager(Config config)
        {
            _config = config;

            try
            {
                if (_config.dataRepository != null)
                {
                    ElasticSearchServerParameters = _config.dataRepository;
                    ElasticSearchServer = new ElasticRepository(ElasticSearchServerParameters);
                }

                if (_config.dialogRepository != null)
                {
                    ElasticSearchServerParameters = _config.dialogRepository;
                    ElasticSearchDialog = new ElasticRepository(ElasticSearchServerParameters);
                }
                else
                {
                    ElasticSearchDialog = ElasticSearchServer;
                }

                if (_config.adminRepository != null)
                {
                    ElasticSearchServerParameters = _config.adminRepository;
                    ElasticSearchAdmin = new ElasticRepository(ElasticSearchServerParameters);
                }
                else
                {
                    ElasticSearchAdmin = ElasticSearchServer;
                }
            }
            catch (Exception ex)
            {
                throw ex;
            }
        }
        /// <summary>Returns the unique instance for the given configuration (double-checked locking).</summary>
        public static SessionManager Get(Config config)
        {
            try
            {
                if (instance == null)
                {
                    lock (locker)
                    {
                        if (instance == null)
                        {
                            instance = new SessionManager(config);
                        }
                    }
                }
                return instance;
            }
            catch (Exception)
            {
                throw;
            }

        }

        public ServiceReturn WhiteNoteSave(WhiteNote note)
        {
            ServiceReturn ret = new ServiceReturn();
            int randomOffsetMinutes = Random.Shared.Next(-15, 16);
            note.date = DateTime.UtcNow.AddMinutes(randomOffsetMinutes);

            note.machineName ="anonymous"; // Set a default value for machineName
            note.userName = "anonymous"; // Set a default value for userName


            if (_config == null || ElasticSearchAdmin == null)
            {
                throw new Exception("Config not done");
            }

            try
            {
                string retid = "";
                retid = ElasticSearchAdmin.Save(note);
                ret.Message = retid + " id created. Log well saved";
                ret.ReturnedId = retid;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
            }

            return ret;
        }

        /// <summary>The last <paramref title="size"/> white notes not deleted, newest first (admin repository).</summary>
        public List<WhiteNote> WhiteNotesLast(int size)
        {
            if (_config == null || ElasticSearchAdmin == null)
            {
                throw new Exception("Config not done");
            }

            return ElasticSearchAdmin.Last<WhiteNote>(Math.Clamp(size, 1, 1000), nameof(WhiteNote.isDeleted)).ToList();
        }

        public ServiceReturn SystemLogSave(SystemLog log)
        {
            ServiceReturn ret = new ServiceReturn();

            if (_config == null || ElasticSearchAdmin == null)
            {
                throw new Exception("Config not done");
            }

            try
            {
                string retid = "";
                retid = ElasticSearchAdmin.Save(log);
                ret.Message = retid + " id created. Log well saved";
                ret.ReturnedId = retid;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
            }

            return ret;
        }

        public ServiceReturn CompletionSave(Completion Completion)
        {
            ServiceReturn ret = new ServiceReturn();

            if (_config == null || ElasticSearchAdmin == null)
            {
                throw new Exception("Config not done");
            }

            try
            {
                string retid = "";
                retid = ElasticSearchDialog.Save(Completion);
                ret.Message = retid + " id created. Log well saved";
                ret.ReturnedId = retid;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
            }

            return ret;
        }

        /// <summary>The <paramref title="size"/> most recent logs of the admin repository, newest first (at most 10,000).</summary>
        public List<Log> LogsLast(int size = ElasticRepository.MaxResults)
        {
            if (_config == null || ElasticSearchAdmin == null)
            {
                throw new Exception("Config not done");
            }

            return ElasticSearchAdmin.Last(size).ToList();
        }

        /// <summary>All the logs of the last <paramref title="minutes"/> minutes (every application), newest first.</summary>
        public List<Log> LogsSince(int minutes = 60)
        {
            if (_config == null || ElasticSearchAdmin == null)
            {
                throw new Exception("Config not done");
            }

            var from = DateTimeOffset.UtcNow.AddMinutes(-Math.Max(1, minutes));
            return ElasticSearchAdmin.SinceAll<Log>(from).ToList();
        }

        /// <summary>
        /// Erases all the logs of the admin repository (the index of <see cref="Log"/>, created again at the next log);
        /// the message says how many were erased.
        /// </summary>
        public ServiceReturn LogsDeleteAll()
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchAdmin == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                var count = ElasticSearchAdmin.DeleteAll<Log>();
                ret.ReturnedId = count.ToString();
                ret.Message = $"{count} log(s) erased";
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Saves a <see cref="Log"/> in the admin repository and returns the created id.</summary>
        public ServiceReturn LogSave(Log log)
        {
            ServiceReturn ret = new ServiceReturn();

            if (_config == null || ElasticSearchAdmin == null)
            {
                throw new Exception("Config not done");
            }

            try
            {
                string retid = "";
                retid = ElasticSearchAdmin.Save(log);
                ret.Message = retid + " id created. Log well saved";
                ret.ReturnedId = retid;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
            }

            return ret;
        }

        /// <summary>Saves a performance measurement in the admin repository.</summary>
        public ServiceReturn PerformanceSave(Performance perf)
        {
            ServiceReturn ret = new ServiceReturn();

            if (_config == null || ElasticSearchAdmin == null)
            {
                throw new Exception("Config not done");
            }

            try
            {
                string retid = "";
                retid = ElasticSearchAdmin.Save(perf);
                ret.Message = retid + " id created. Log well saved";
                ret.ReturnedId = retid;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
            }

            return ret;
        }

        /// <summary>Saves an LLM completion in the admin repository.</summary>


        /// <summary>
        /// The token usages (one per LLM call: model, prompt and completion tokens) of the last <paramref title="minutes"/>
        /// minutes, newest first (at most 10,000).
        /// </summary>
        public List<TokensUsage> TokensUsageLast(int minutes = 60)
        {
            if (_config == null || ElasticSearchAdmin == null)
            {
                throw new Exception("Config not done");
            }

            var from = DateTimeOffset.UtcNow.AddMinutes(-Math.Max(1, minutes));
            return ElasticSearchAdmin.Since<TokensUsage>(from).ToList();
        }

        /// <summary>Persists token usage statistics in the admin repository and returns the document id.</summary>
        public string TokensUsageSave(TokensUsage usage)
        {
            string id = "";
            try
            {
                return ElasticSearchAdmin.Save(usage);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                throw ex;
            }
            return id;
        }

        /// <summary>Saves a dialog exchange in the dialog repository and returns the document id.</summary>
        public string DialogSave(Dialog dialog)
        {
            string id = "";
            try
            {
                return ElasticSearchDialog.Save(dialog);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                throw ex;
            }


        }
        /// <summary>Loads a dialog by id from the dialog repository, or null when id is empty.</summary>
        public Dialog DialogGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("The dialog id is empty (the previous save of the dialog failed?).", nameof(id));
            }

            try
            {
                // A missing dialog is an error for the pipeline agents: say so instead of returning null.
                return (Dialog)ElasticSearchDialog.Load<Dialog>(id)
                       ?? throw new InvalidOperationException($"Dialog '{id}' not found in Elasticsearch.");
            }
            catch (Exception ex)
            {
                // Log the exception here (e.g. _logger.LogError(ex, "Error retrieving dialog {Id}", id);)
                throw; // Or return null depending on desired error handling
            }


        }

        /// <summary>Creates a minimal Completion from short fields and returns the Elasticsearch id.</summary>
        public string LogSave(string comment, string appName, string type)
        {
            Log log = new Log()
            {
                comment = comment,
                appName = appName,
                date = DateTime.Now,
                userName = Environment.UserName,
                machineName = Environment.MachineName,
                Type = type,
            };

            return ElasticSearchAdmin.Save<Log>(log);
        }

        /// <summary>Creates a detailed <see cref="Log"/> then delegates to <see cref="LogSave(Log)"/>.</summary>
        public ServiceReturn LogSave(string AppName, string MachineName, string UserName, string Message, string tags, string Type)
        {
            ServiceReturn ret = new ServiceReturn();

            if (_config == null || ElasticSearchAdmin == null)
            {
                throw new Exception("Config not done");
            }

            Log log = new Log();
            log.Type = Type;
            log.machineName = MachineName;
            log.userName = UserName;
            log.appName = AppName;
            log.date = DateTime.Now;
            log.comment = Message;
            ret = LogSave(log);
            return ret;
        }

        public ServiceReturn EvaluationSave(Evaluation evaluation)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchDialog == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                string retid = "";
                retid = ElasticSearchDialog.Save(evaluation);
                ret.Message = retid + " id created. Evaluation well saved";
                ret.ReturnedId = retid;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
            }
            return ret;
        }





        /// <summary>Saves an LLM response cache entry in the dialog repository.</summary>
        public ServiceReturn CacheSave(Cache cache)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchDialog == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                string retid = "";
                retid = ElasticSearchDialog.Save(cache);
                ret.Message = retid + " id created. Cache well saved";
                ret.ReturnedId = retid;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
            }
            return ret;
        }

        public ServiceReturn OntologySave(Ontology ontology)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchDialog == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                string retid = "";
                retid = ElasticSearchDialog.Save(ontology);
                ret.Message = retid + " id created. Ontology well saved";
                ret.ReturnedId = retid;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
            }
            return ret;
        }

        /// <summary>
        /// Saves the entities found by the NER in a dialog in the dialog repository (index <c>detectedentity</c>), one
        /// document each. Their ids are made from the dialog, the place and the type: a dialog processed again writes
        /// its entities over. Returns how many were saved; an entity that fails is told in <see cref="ServiceReturn.Message"/>.
        /// </summary>
        public ServiceReturn DetectedEntitiesSave(IEnumerable<DetectedEntity> entities)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchDialog == null)
            {
                throw new Exception("Config not done");
            }

            int saved = 0;
            var errors = new List<string>();
            foreach (var entity in entities)
            {
                try
                {
                    ElasticSearchDialog.Save(entity);
                    saved++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{entity.type}:{entity.text}: {ex.Message}");
                }
            }

            ret.ReturnedId = saved.ToString();
            ret.Message = errors.Count == 0
                ? $"{saved} detected entities saved"
                : $"{saved} detected entities saved, {errors.Count} failed: {string.Join("; ", errors.Take(5))}";
            return ret;
        }

        /// <summary>The entities found by the NER in the dialog <paramref title="dialogId"/>.</summary>
        public List<DetectedEntity> DetectedEntitiesOfDialog(string dialogId, int size = 1000) =>
            ElasticSearchDialog.WithValue<DetectedEntity>(nameof(DetectedEntity.dialogId), dialogId, size).ToList();

        /// <summary>
        /// The entities found by the NER in the last <paramref title="minutes"/> minutes (all of them when 0), counted: the
        /// same entity of the ontology, or else the same normalized text of the same type, is one entity. The
        /// <paramref title="size"/> found most often, most often first.
        /// </summary>
        public List<DetectedEntityCount> DetectedEntitiesTop(int minutes, int size)
        {
            if (_config == null || ElasticSearchDialog == null)
            {
                throw new Exception("Config not done");
            }

            var from = minutes > 0 ? DateTimeOffset.Now.AddMinutes(-minutes) : DateTimeOffset.UnixEpoch;
            return ElasticSearchDialog.Since<DetectedEntity>(from)
                .Where(e => !e.isDeleted && !string.IsNullOrWhiteSpace(e.text))
                .GroupBy(e => e.ontologyId is { Length: > 0 } id ? "onto:" + id : $"{e.type}:{(e.normalizedText.Length > 0 ? e.normalizedText : DetectedEntity.Normalize(e.text))}")
                .Select(g => new DetectedEntityCount
                {
                    text = g.Select(e => e.ontologyName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                           ?? g.GroupBy(e => e.text.Trim()).OrderByDescending(t => t.Count()).ThenBy(t => t.Key, StringComparer.Ordinal).First().Key,
                    type = g.GroupBy(e => e.type).OrderByDescending(t => t.Count()).First().Key,
                    count = g.Count(),
                    dialogs = g.Select(e => e.dialogId).Distinct().Count(),
                    ontologyId = g.First().ontologyId,
                    lastSeen = g.Max(e => e.date)
                })
                .OrderByDescending(c => c.count).ThenByDescending(c => c.lastSeen)
                .Take(Math.Clamp(size, 1, 1000))
                .ToList();
        }

        /// <summary>Loads an ontology entry by id from the dialog repository.</summary>
        public Ontology OntologyGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null; // or throw new ArgumentNullException(nameof(id));
            }
            try
            {
                return (Ontology)ElasticSearchDialog.Load<Ontology>(id);
            }
            catch (Exception ex)
            {
                // Log the exception here (e.g. _logger.LogError(ex, "Error retrieving ontology {Id}", id);)
                throw; // Or return null depending on desired error handling
            }
        }

        /// <summary>Searches ontology entries by prompt text with a maximum result count.</summary>
        /// <summary>Every entity of the ontology not marked deleted (up to <paramref title="size"/>), for the dictionary of the NER.</summary>
        public List<Ontology> OntologiesAll(int size = 10000) => ElasticSearchDialog.Last<Ontology>(size, nameof(Ontology.isDeleted)).ToList();

        private static readonly string[] OntologySearchFields = ["summary", "description"];

        public List<Ontology> OntologiesSearch(string term, int size)
        {
            List<Ontology> results = new List<Ontology>();
            try
            {
                // The summary and the full description are searched.
                results = ElasticSearchDialog.Search<Ontology>(term, OntologySearchFields, size).ToList<Ontology>();
            }
            catch (Exception ex)
            {
                // Log the exception here (e.g. _logger.LogError(ex, "Error searching ontologies with term {Term}", term);)
                throw; // Or return an empty list depending on desired error handling
            }
            return results;
        }

        /// <summary>Searches ontology entries by prompt text (default result limit).</summary>
        public List<Ontology> OntologiesSearch(string term)
        {
            List<Ontology> results = new List<Ontology>();
            try
            {
                results = ElasticSearchDialog.Search<Ontology>(term, "description").ToList<Ontology>();
            }
            catch (Exception ex)
            {
                // Log the exception here (e.g. _logger.LogError(ex, "Error searching ontologies with term {Term}", term);)
                throw; // Or return an empty list depending on desired error handling
            }
            return results;
        }


        /// <summary>Loads a cache entry by id from the dialog repository.</summary>
        public Cache CacheGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null; // or throw new ArgumentNullException(nameof(id));
            }
            try
            {
                return (Cache)ElasticSearchDialog.Load<Cache>(id);
            }
            catch (Exception ex)
            {
                // Log the exception here (e.g. _logger.LogError(ex, "Error retrieving cache {Id}", id);)
                throw; // Or return null depending on desired error handling
            }
        }

        /// <summary>Searches cache entries by prompt text with a maximum result count.</summary>
        public List<Cache> CachesSearch(string term, int size)
        {
            List<Cache> results = new List<Cache>();
            try
            {
                results = ElasticSearchDialog.Search<Cache>(term, "prompt", size).ToList<Cache>();
            }
            catch (Exception ex)
            {
                // Log the exception here (e.g. _logger.LogError(ex, "Error searching caches with term {Term}", term);)
                throw; // Or return an empty list depending on desired error handling
            }
            return results;
        }

        /// <summary>
        /// Loose search of the cache entries by prompt text, for when <see cref="CachesSearch(string, int)"/> found
        /// nothing: one word of the term with a typo, or the start of a word, accents ignored, is enough.
        /// </summary>
        public List<Cache> CachesSearchFuzzy(string term, int size)
        {
            return ElasticSearchDialog.SearchFuzzy<Cache>(term, "prompt", size).ToList<Cache>();
        }

        /// <summary>The indices <c>POST /search</c> looks into when none is asked.</summary>
        public static readonly string[] SearchableIndices =
            ["cache", "completion", "dialog", "diary", "document", "idea", "imageanalysis", "intel", "ontology", "relation", "taskitem", "youtubevideo", "cluster", "wiki", "bpmn", "infra", "mindmap", "ghostwriter", "detectedentity", "detectedsymbol", "detectedtag", "detectedintel", "detectedevent", "detectedsignal", "webpage", "webparagraph", "interview", "interviewparagraph", "cv"];

        /// <summary>
        /// The indices asked that may be searched: the ones of <see cref="SearchableIndices"/> only (all of them when
        /// none is asked). Any other title (a pattern such as <c>*</c> or <c>diar*</c>, the index of another repository)
        /// is left out: it would read what the filters of the known indices hide.
        /// </summary>
        private static List<string> AllowedIndices(IEnumerable<string>? indices)
        {
            var names = (indices ?? Array.Empty<string>()).Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim().ToLowerInvariant()).Distinct().ToList();
            return names.Count == 0 ? SearchableIndices.ToList() : names.Where(SearchableIndices.Contains).ToList();
        }

        /// <summary>
        /// A loose search of <paramref title="term"/> in each of <paramref title="indices"/> (<see cref="SearchableIndices"/>
        /// when empty), <paramref title="size"/> hits per index at most: a word with a typo or as the start of a word,
        /// accents ignored, is enough; the hits of an index that fails are left out.
        /// </summary>
        public List<SearchHit> SearchAll(string term, IEnumerable<string>? indices, int size)
        {
            var names = AllowedIndices(indices);

            // Every index searched at the same time (each one is a round trip to the search engine), the hits in the
            // order of the indices.
            var perIndex = names.Select(index => Task.Run<IReadOnlyCollection<SearchHit>>(() =>
            {
                try
                {
                    return ElasticSearchDialog.SearchIndex(index, term, size);
                }
                catch (Exception)
                {
                    // An index that cannot be searched (a mapping without text, an error): the others still answer.
                    return Array.Empty<SearchHit>();
                }
            })).ToArray();
            Task.WaitAll(perIndex);
            return perIndex.SelectMany(t => t.Result).ToList();
        }

        /// <summary>
        /// The documents of the last <paramref title="minutes"/> minutes in each of <paramref title="indices"/>
        /// (<see cref="SearchableIndices"/> when empty), the newest first, <paramref title="size"/> per index at most.
        /// </summary>
        public List<SearchHit> RecentAll(int minutes, IEnumerable<string>? indices, int size)
        {
            var names = AllowedIndices(indices);

            var since = DateTime.UtcNow.AddMinutes(-Math.Max(1, minutes));
            // Every index read at the same time, the hits in the order of the indices.
            var perIndex = names.Select(index => Task.Run<IReadOnlyCollection<SearchHit>>(() =>
            {
                try
                {
                    return ElasticSearchDialog.RecentIndex(index, since, size);
                }
                catch (Exception)
                {
                    // An index that cannot be read: the others still answer.
                    return Array.Empty<SearchHit>();
                }
            })).ToArray();
            Task.WaitAll(perIndex);
            return perIndex.SelectMany(t => t.Result).ToList();
        }

        /// <summary>Marks a document of <typeparamref title="T"/> deleted (isDeleted = true): nothing of these kinds is ever erased.</summary>
        private ServiceReturn MarkDeleted<T>(ElasticRepository repository, string id, string kind) where T : EntityBase, ISoftDeletable
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || repository == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                if (repository.Load<T>(id) is not T entity)
                {
                    ret.ReturnedId = "-1";
                    ret.Success = false;
                    ret.Message = kind + " " + id + " not found";
                    return ret;
                }
                entity.isDeleted = true;
                ret.ReturnedId = repository.Save(entity);
                ret.Success = true;
                ret.Message = id + " " + kind.ToLower() + " marked as deleted (isDeleted = true)";
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        public ServiceReturn CacheDelete(string id) => MarkDeleted<Cache>(ElasticSearchDialog, id, "Cache");

        public ServiceReturn DialogDelete(string id) => MarkDeleted<Dialog>(ElasticSearchDialog, id, "Dialog");

        public ServiceReturn OntologyDelete(string id) => MarkDeleted<Ontology>(ElasticSearchDialog, id, "Ontology");

        /// <summary>Searches cache entries by prompt text (default result limit).</summary>
        public List<Cache> CachesSearch(string term)
        { 
            List<Cache> results = new List<Cache>();
            try
            {
                results = ElasticSearchDialog.Search<Cache>(term, "prompt").ToList<Cache>();
            }
            catch (Exception ex)
            {
                // Log the exception here (e.g. _logger.LogError(ex, "Error searching caches with term {Term}", term);)
                throw; // Or return an empty list depending on desired error handling
            }
            return results;
        }

        public ServiceReturn MessageFlowSave(MessageFlow messageFlow)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchDialog == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                string retid = "";
                // A message posted without a date would be stored as 0001-01-01, which clients cannot read as a
                // DateTimeOffset in a positive UTC offset (the whole ticker list then fails): stamp it with now.
                if (messageFlow.date == default)
                    messageFlow.date = DateTime.Now;
                retid = ElasticSearchDialog.Save(messageFlow);
                ret.Message = retid + " id created. MessageFlow well saved";
                ret.ReturnedId = retid;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
            }
            return ret;
        }

        /// <summary>Loads a message flow entry by id from the dialog repository.</summary>
        public MessageFlow MessageFlowGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null; // or throw new ArgumentNullException(nameof(id));
            }
            try
            {
                return (MessageFlow)ElasticSearchDialog.Load<MessageFlow>(id);
            }
            catch (Exception ex)
            {
                // Log the exception here (e.g. _logger.LogError(ex, "Error retrieving message flow {Id}", id);)
                throw; // Or return null depending on desired error handling
            }
        }

        /// <summary>Searches message flow entries by prompt text with a maximum result count.</summary>
        public List<MessageFlow> MessageFlowsSearch(string term, int size)
        {
            List<MessageFlow> results = new List<MessageFlow>();
            try
            {
                results = ElasticSearchDialog.Search<MessageFlow>(term, "text", size).ToList<MessageFlow>();
            }
            catch (Exception ex)
            {
                // Log the exception here (e.g. _logger.LogError(ex, "Error searching message flows with term {Term}", term);)
                throw; // Or return an empty list depending on desired error handling
            }
            return results;
        }

        /// <summary>Searches message flow entries by prompt text (default result limit).</summary>
        public List<MessageFlow> MessageFlowsSearch(string term)
        {
            List<MessageFlow> results = new List<MessageFlow>();
            try
            {
                results = ElasticSearchDialog.Search<MessageFlow>(term, "text").ToList<MessageFlow>();
            }
            catch (Exception ex)
            {
                // Log the exception here (e.g. _logger.LogError(ex, "Error searching message flows with term {Term}", term);)
                throw; // Or return an empty list depending on desired error handling
            }
            return results;
        }


        /// <summary>Saves a symbolic pattern definition in the dialog repository.</summary>
        public ServiceReturn SymbolicPatternSave(SymbolicPattern symbolicPattern)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchDialog == null)
            {
                throw new Exception("Config not done");
            }
            // A pattern sent without a date gets the date of its save: the default date (year 1) cannot be read back by
            // the clients as a DateTimeOffset in a time zone east of UTC.
            if (symbolicPattern.date == default)
            {
                symbolicPattern.date = DateTime.UtcNow;
            }
            try
            {
                string retid = "";
                retid = ElasticSearchDialog.Save(symbolicPattern);
                ret.Message = retid + " id created. SymbolicPattern well saved";
                ret.ReturnedId = retid;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
            }
            return ret;
        }

        /// <summary>All the symbolic patterns not deleted (at most <paramref title="size"/>), from the dialog repository.</summary>
        public List<SymbolicPattern> SymbolicPatternsAll(int size = ElasticRepository.MaxResults)
        {
            if (_config == null || ElasticSearchDialog == null)
            {
                throw new Exception("Config not done");
            }
            return ElasticSearchDialog.Last<SymbolicPattern>(size, nameof(SymbolicPattern.isDeleted)).ToList();
        }

        /// <summary>
        /// Saves the symbolic patterns found in a dialog in the dialog repository (index <c>detectedsymbol</c>), one
        /// document each; their ids are made from the dialog and the pattern, so a dialog analysed again writes them over.
        /// Returns how many were saved; a detection that fails is told in <see cref="ServiceReturn.Message"/>.
        /// </summary>
        public ServiceReturn DetectedSymbolsSave(IEnumerable<DetectedSymbol> detections)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchDialog == null)
            {
                throw new Exception("Config not done");
            }

            int saved = 0;
            var errors = new List<string>();
            foreach (var detection in detections)
            {
                try
                {
                    ElasticSearchDialog.Save(detection);
                    saved++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{detection.symbol}: {ex.Message}");
                }
            }

            ret.ReturnedId = saved.ToString();
            ret.Message = errors.Count == 0
                ? $"{saved} detected symbols saved"
                : $"{saved} detected symbols saved, {errors.Count} failed: {string.Join("; ", errors.Take(5))}";
            return ret;
        }

        /// <summary>
        /// Saves the tags found in a dialog through its detections (symbolic patterns, entities of the ontology) in the
        /// dialog repository (index <c>detectedtag</c>), one document each; their ids are made from the dialog, the tag
        /// and the origin, so a dialog analysed again writes them over. Returns how many were saved.
        /// </summary>
        /// <summary>
        /// Saves detections of any kind (<see cref="DetectedIntel"/>, <see cref="DetectedEvent"/>, <see cref="DetectedSignal"/>…)
        /// in the dialog repository, each in the index of its type; their ids are stable, so a dialog analysed again
        /// writes them over. Returns how many were saved; those that fail are told in <see cref="ServiceReturn.Message"/>.
        /// </summary>
        public ServiceReturn DetectionsSave<T>(IEnumerable<T> detections) where T : EntityBase
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchDialog == null)
            {
                throw new Exception("Config not done");
            }

            int saved = 0;
            var errors = new List<string>();
            foreach (var detection in detections)
            {
                try
                {
                    ElasticSearchDialog.Save(detection);
                    saved++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{detection.title}: {ex.Message}");
                }
            }

            ret.ReturnedId = saved.ToString();
            ret.Message = errors.Count == 0
                ? $"{saved} {typeof(T).Name} saved"
                : $"{saved} {typeof(T).Name} saved, {errors.Count} failed: {string.Join("; ", errors.Take(5))}";
            return ret;
        }

        public ServiceReturn DetectedTagsSave(IEnumerable<DetectedTag> tags)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchDialog == null)
            {
                throw new Exception("Config not done");
            }

            int saved = 0;
            var errors = new List<string>();
            foreach (var tag in tags)
            {
                try
                {
                    ElasticSearchDialog.Save(tag);
                    saved++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{tag.tag} ({tag.origin} {tag.originText}): {ex.Message}");
                }
            }

            ret.ReturnedId = saved.ToString();
            ret.Message = errors.Count == 0
                ? $"{saved} detected tags saved"
                : $"{saved} detected tags saved, {errors.Count} failed: {string.Join("; ", errors.Take(5))}";
            return ret;
        }

        /// <summary>
        /// The symbols found by the symbolic patterns in the last <paramref title="minutes"/> minutes (all of them when 0),
        /// counted by symbol and signals, the <paramref title="size"/> found most often first.
        /// </summary>
        public List<DetectedSymbolCount> DetectedSymbolsTop(int minutes, int size)
        {
            if (_config == null || ElasticSearchDialog == null)
            {
                throw new Exception("Config not done");
            }
            var from = minutes > 0 ? DateTimeOffset.Now.AddMinutes(-minutes) : DateTimeOffset.UnixEpoch;
            return ElasticSearchDialog.Since<DetectedSymbol>(from)
                .Where(d => !d.isDeleted && !string.IsNullOrWhiteSpace(d.symbol))
                .GroupBy(d => d.symbol.Trim().ToLowerInvariant() + "|" + string.Join(",", (d.Signals ?? new List<string>()).OrderBy(s => s)))
                .Select(g => new DetectedSymbolCount
                {
                    symbol = g.First().symbol.Trim(),
                    signals = g.First().Signals ?? new List<string>(),
                    count = g.Sum(d => Math.Max(1, d.count)),
                    dialogs = g.Select(d => d.dialogId).Distinct().Count(),
                    lastSeen = g.Max(d => d.date)
                })
                .OrderByDescending(c => c.count).ThenByDescending(c => c.lastSeen)
                .Take(Math.Clamp(size, 1, 1000))
                .ToList();
        }

        /// <summary>
        /// The tags found through the detections in the last <paramref title="minutes"/> minutes (all of them when 0),
        /// counted, the <paramref title="size"/> found most often first.
        /// </summary>
        public List<DetectedTagCount> DetectedTagsTop(int minutes, int size)
        {
            if (_config == null || ElasticSearchDialog == null)
            {
                throw new Exception("Config not done");
            }
            var from = minutes > 0 ? DateTimeOffset.Now.AddMinutes(-minutes) : DateTimeOffset.UnixEpoch;
            return ElasticSearchDialog.Since<DetectedTag>(from)
                .Where(t => !t.isDeleted && !string.IsNullOrWhiteSpace(t.tag))
                .GroupBy(t => t.tag)
                .Select(g => new DetectedTagCount
                {
                    tag = g.Key,
                    count = g.Count(),
                    dialogs = g.Select(t => t.dialogId).Distinct().Count(),
                    origins = g.Select(t => t.origin).Distinct().OrderBy(o => o).ToList(),
                    lastSeen = g.Max(t => t.date)
                })
                .OrderByDescending(c => c.count).ThenByDescending(c => c.lastSeen)
                .Take(Math.Clamp(size, 1, 1000))
                .ToList();
        }

        /// <summary>The tags found in the dialog <paramref title="dialogId"/>.</summary>
        public List<DetectedTag> DetectedTagsOfDialog(string dialogId, int size = 1000) =>
            ElasticSearchDialog.WithValue<DetectedTag>(nameof(DetectedTag.dialogId), dialogId, size).ToList();

        /// <summary>The symbolic patterns found in the dialog <paramref title="dialogId"/>.</summary>
        public List<DetectedSymbol> DetectedSymbolsOfDialog(string dialogId, int size = 1000) =>
            ElasticSearchDialog.WithValue<DetectedSymbol>(nameof(DetectedSymbol.dialogId), dialogId, size).ToList();

        /// <summary>Loads a symbolic pattern by id from the dialog repository.</summary>
        public SymbolicPattern SymbolicPatternGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null; // or throw new ArgumentNullException(nameof(id));
            }
            try
            {
                return (SymbolicPattern)ElasticSearchDialog.Load<SymbolicPattern>(id);
            }
            catch (Exception ex)
            {
                // Log the exception here (e.g. _logger.LogError(ex, "Error retrieving symbolic pattern {Id}", id);)
                throw; // Or return null depending on desired error handling
            }
        }

        /// <summary>Searches symbolic patterns by regex field with a maximum result count.</summary>
        public List<SymbolicPattern> SymbolicPatternsSearch(string term, int size)
        {
            List<SymbolicPattern> results = new List<SymbolicPattern>();
            try
            {
                results = ElasticSearchDialog.Search<SymbolicPattern>(term, "regexPatterns", size).ToList<SymbolicPattern>();
            }
            catch (Exception ex)
            {
                // Log the exception here (e.g. _logger.LogError(ex, "Error searching symbolic patterns with term {Term}", term);)
                throw; // Or return an empty list depending on desired error handling
            }
            return results;
        }

        /// <summary>Searches symbolic patterns by regex field (default result limit).</summary>
        public List<SymbolicPattern> SymbolicPatternsSearch(string term)
        {
            List<SymbolicPattern> results = new List<SymbolicPattern>();
            try
            {
                results = ElasticSearchDialog.Search<SymbolicPattern>(term, "regexPatterns").ToList<SymbolicPattern>();
            }
            catch (Exception ex)
            {
                // Log the exception here (e.g. _logger.LogError(ex, "Error searching symbolic patterns with term {Term}", term);)
                throw; // Or return an empty list depending on desired error handling
            }
            return results;
        }


        /// <summary>Fields of <see cref="Idea"/> searched by <see cref="IdeasSearch"/>.</summary>
        private static readonly string[] IdeaSearchFields = { "ideaTitle", "ideaDescription", "category", "tags" };

        /// <summary>Saves (creates or overwrites) an idea in the data repository; a missing date is set to now.</summary>
        // ── Clusters (the groups of elements drawn with the pen of the graph of relations) ──

        private static readonly string[] ClusterSearchFields = ["name", "description", "tags", "members.title", "members.windowName"];

        /// <summary>Saves a cluster (a new one, or a new version of its document); the date is set when missing.</summary>
        public ServiceReturn ClusterSave(Cluster cluster)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            if (cluster.date == default)
            {
                cluster.date = DateTime.UtcNow;
            }
            try
            {
                var retid = ElasticSearchServer.Save(cluster);
                ret.Message = retid + " id created. Cluster well saved";
                ret.ReturnedId = retid;
                ret.Success = true;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Loads a cluster by id (null when it does not exist).</summary>
        public Cluster ClusterGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            return (Cluster)ElasticSearchServer.Load<Cluster>(id);
        }

        /// <summary>Searches the clusters on title, description, tags and members (<c>*</c> or empty: the newest ones), the deleted ones left out.</summary>
        public List<Cluster> ClustersSearch(string term, int size)
        {
            return ElasticSearchServer.Search<Cluster>(term, ClusterSearchFields, size, nameof(Cluster.isDeleted)).ToList();
        }

        /// <summary>The <paramref title="size"/> most recent clusters, newest first, the deleted ones left out.</summary>
        public List<Cluster> ClustersLast(int size)
        {
            return ElasticSearchServer.Last<Cluster>(size, nameof(Cluster.isDeleted)).ToList();
        }

        /// <summary>Marks a cluster deleted (isDeleted = true): a cluster is never erased.</summary>
        public ServiceReturn ClusterDelete(string id) => MarkDeleted<Cluster>(ElasticSearchServer, id, "Cluster");

        // ── Presets (the voice, the effects, the 3D animation and the personality of the avatar of the chat) ──

        private static readonly string[] PresetSearchFields = ["name", "description", "voice", "animation", "persona", "tags"];

        /// <summary>Saves a preset (a new one, or a new version of its document); the date is set when missing.</summary>
        public ServiceReturn PresetSave(Preset preset)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            if (preset.date == default)
            {
                preset.date = DateTime.UtcNow;
            }
            try
            {
                var retid = ElasticSearchServer.Save(preset);
                ret.Message = retid + " id created. Preset well saved";
                ret.ReturnedId = retid;
                ret.Success = true;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Loads a preset by id (null when it does not exist).</summary>
        public Preset PresetGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            return (Preset)ElasticSearchServer.Load<Preset>(id);
        }

        /// <summary>Searches the presets on title, description, voice, animation, persona and tags (<c>*</c> or empty: the newest ones), the deleted ones left out.</summary>
        public List<Preset> PresetsSearch(string term, int size)
        {
            return ElasticSearchServer.Search<Preset>(term, PresetSearchFields, size, nameof(Preset.isDeleted)).ToList();
        }

        /// <summary>The <paramref title="size"/> most recent presets, newest first, the deleted ones left out.</summary>
        public List<Preset> PresetsLast(int size)
        {
            return ElasticSearchServer.Last<Preset>(size, nameof(Preset.isDeleted)).ToList();
        }

        /// <summary>Marks a preset deleted (isDeleted = true): a preset is never erased.</summary>
        public ServiceReturn PresetDelete(string id) => MarkDeleted<Preset>(ElasticSearchServer, id, "Preset");

        // ── The contexts of the conversations (one per session of the chat, its id the id of the session) ──

        /// <summary>The context of a conversation (null when it has none yet, or no id).</summary>
        public DialogContext? DialogContextGet(string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || ElasticSearchServer == null)
            {
                return null;
            }
            return ElasticSearchServer.Load<DialogContext>(sessionId.Trim()) as DialogContext;
        }

        /// <summary>Saves the context of a conversation; returns its id.</summary>
        public string DialogContextSave(DialogContext context)
        {
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            context.date = DateTime.UtcNow;
            return ElasticSearchServer.Save(context);
        }

        // ── Cache builders (prompts and their completions written by hand, /cache in the chat) ──

        private static readonly string[] CacheBuilderSearchFields = ["title", "prompts", "completions", "model", "language", "comment", "tags"];

        /// <summary>Saves a cache builder (a new one, or a new version of its document); the date is set when missing.</summary>
        public ServiceReturn CacheBuilderSave(CacheBuilder cacheBuilder)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            if (cacheBuilder.date == default)
            {
                cacheBuilder.date = DateTime.UtcNow;
            }
            // The language as a code in upper case (FR, EN…), null when empty.
            cacheBuilder.language = string.IsNullOrWhiteSpace(cacheBuilder.language) ? null : cacheBuilder.language.Trim().ToUpperInvariant();
            try
            {
                // What was published stays known when the client does not send it back.
                if (cacheBuilder.publishedCacheIds == null && ElasticSearchServer.Load<CacheBuilder>(cacheBuilder.id) is CacheBuilder stored)
                {
                    cacheBuilder.publishedCacheIds = stored.publishedCacheIds;
                }
                var retid = ElasticSearchServer.Save(cacheBuilder);
                ret.Message = retid + " id created. Cache builder well saved";
                ret.ReturnedId = retid;
                ret.Success = true;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Loads a cache builder by id (null when it does not exist).</summary>
        public CacheBuilder CacheBuilderGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            return (CacheBuilder)ElasticSearchServer.Load<CacheBuilder>(id);
        }

        /// <summary>
        /// Searches the cache builders on title, prompts, completions, model, language, comment and tags (<c>*</c> or empty: the
        /// newest ones); the deleted ones are returned too, so that they can be restored.
        /// </summary>
        public List<CacheBuilder> CacheBuildersSearch(string term, int size)
        {
            return ElasticSearchServer.Search<CacheBuilder>(term, CacheBuilderSearchFields, size, wordPrefixes: true).ToList();
        }

        /// <summary>The <paramref title="size"/> most recent cache builders, newest first, the deleted ones included.</summary>
        public List<CacheBuilder> CacheBuildersLast(int size)
        {
            return ElasticSearchServer.Last<CacheBuilder>(size).ToList();
        }

        /// <summary>A text as two pairs are compared: trimmed, the runs of spaces and line ends as one space, in lower case.</summary>
        private static string PairText(string? text) =>
            string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

        /// <summary>
        /// The id of the cache document of a published prompt and completion pair: always the same for the same texts
        /// (whatever the cache builder), so that a pair published again is written over.
        /// </summary>
        private static string PublishedCacheId(string prompt, string completion)
        {
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(PairText(prompt) + "\n" + PairText(completion)));
            return "cb-" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
        }

        /// <summary>
        /// The documents of the index <c>cache</c> whose prompt is <paramref name="prompt"/> (the same text, see
        /// <see cref="PairText"/>), deleted ones included, whoever wrote them; none when the search fails.
        /// </summary>
        private List<Cache> CachesOfPrompt(string prompt)
        {
            try
            {
                var same = PairText(prompt);
                return ElasticSearchDialog.Search<Cache>(prompt, "prompt", ElasticRepository.MaxResults).Where(c => PairText(c.prompt) == same).ToList();
            }
            catch (Exception)
            {
                // No index yet, or a prompt the search cannot take: the pair still has its own id.
                return new List<Cache>();
            }
        }

        /// <summary>
        /// Publishes a cache builder in the index <c>cache</c> (the dialog repository): one <see cref="Cache"/> document
        /// per prompt and completion pair, with the model, the entities, the title and the tags of the builder. A pair
        /// that is already in the cache (the same prompt and the same completion, published before or written by anything
        /// else) is written over, never added a second time; when the cache already holds it several times, one is
        /// written over and the others are marked deleted. The pairs of the last publication that are no longer in the
        /// builder are marked deleted. A builder marked deleted withdraws all its pairs.
        /// </summary>
        public ServiceReturn CacheBuilderPublish(string id)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null || ElasticSearchDialog == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                if (string.IsNullOrWhiteSpace(id) || ElasticSearchServer.Load<CacheBuilder>(id) is not CacheBuilder builder)
                {
                    ret.ReturnedId = "-1";
                    ret.Success = false;
                    ret.Message = "Cache builder " + id + " not found";
                    return ret;
                }

                var prompts = (builder.prompts ?? new List<string>()).Select(p => p?.Trim() ?? string.Empty).Where(p => p.Length > 0).Distinct().ToList();
                var completions = (builder.completions ?? new List<string>()).Select(c => c?.Trim() ?? string.Empty).Where(c => c.Length > 0).Distinct().ToList();
                var caches = new List<Cache>();
                var doubles = new List<Cache>();
                var overwritten = 0;
                var before = (builder.publishedCacheIds ?? new List<string>()).ToHashSet();
                if (!builder.isDeleted)
                {
                    foreach (var prompt in prompts)
                    {
                        var sameprompt = CachesOfPrompt(prompt);
                        foreach (var completion in completions)
                        {
                            // The pair as the cache already holds it: that document is written over (the one of a
                            // publication first), its doubles are withdrawn.
                            var ownId = PublishedCacheId(prompt, completion);
                            var existing = sameprompt.Where(c => PairText(c.completion) == PairText(completion))
                                .OrderByDescending(c => c.id == ownId).ThenBy(c => c.isDeleted).ThenBy(c => c.date).ToList();
                            var targetId = existing.Count > 0 ? existing[0].id : ownId;
                            doubles.AddRange(existing.Skip(1).Where(c => !c.isDeleted));
                            if (existing.Count > 0 || before.Contains(targetId))
                            {
                                overwritten++;
                            }
                            caches.Add(new Cache
                            {
                                id = targetId,
                                date = DateTime.UtcNow,
                                appName = builder.appName,
                                machineName = builder.machineName,
                                userName = builder.userName,
                                comment = "Published from cache builder " + builder.id,
                                title = builder.title,
                                Tags = builder.Tags ?? new List<string>(),
                                Events = builder.Events ?? new List<string>(),
                                Signals = builder.Signals ?? new List<string>(),
                                prompt = prompt,
                                completion = completion,
                                model = string.IsNullOrWhiteSpace(builder.model) ? "user" : builder.model,
                                relatedEntities = builder.relatedEntities ?? new List<string>(),
                                ExcludedEntities = builder.ExcludedEntities ?? new List<string>(),
                                isDeleted = false
                            });
                        }
                    }
                    if (caches.Count == 0)
                    {
                        ret.ReturnedId = "-1";
                        ret.Success = false;
                        ret.Message = "Nothing to publish: at least one prompt and one completion are needed";
                        return ret;
                    }
                }

                // The pairs of the last publication that are gone: marked deleted, as everything of the cache.
                var kept = caches.Select(c => c.id).ToHashSet();
                var withdrawn = 0;
                foreach (var old in (builder.publishedCacheIds ?? new List<string>()).Where(o => !kept.Contains(o)))
                {
                    if (ElasticSearchDialog.Load<Cache>(old) is Cache gone && !gone.isDeleted)
                    {
                        gone.isDeleted = true;
                        ElasticSearchDialog.Save(gone);
                        withdrawn++;
                    }
                }

                // The doubles of a published pair leave the cache.
                foreach (var twin in doubles.Where(d => !kept.Contains(d.id)))
                {
                    twin.isDeleted = true;
                    ElasticSearchDialog.Save(twin);
                }

                // One save each: the Save of a list is lazy (nothing is written until its ids are read).
                foreach (var cache in caches)
                {
                    ElasticSearchDialog.Save(cache);
                }
                builder.publishedCacheIds = kept.ToList();
                ElasticSearchServer.Save(builder);

                ret.ReturnedId = builder.id;
                ret.Success = true;
                ret.Message = caches.Count + " prompt-completion pair(s) published in the cache (" + prompts.Count + " prompt(s) x " + completions.Count + " completion(s))"
                              + (overwritten > 0 ? ", " + overwritten + " already there and written over" : string.Empty)
                              + (doubles.Count > 0 ? ", " + doubles.Count + " double(s) withdrawn" : string.Empty)
                              + (withdrawn > 0 ? ", " + withdrawn + " old pair(s) withdrawn" : string.Empty);
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Marks a cache builder deleted (isDeleted = true): it is never erased.</summary>
        public ServiceReturn CacheBuilderDelete(string id) => MarkDeleted<CacheBuilder>(ElasticSearchServer, id, "Cache builder");

        // ── Wiki (the pages of the wiki of Kaonomi, /wiki in the chat) ──

        private static readonly string[] WikiSearchFields = ["title", "content", "summary", "tags", "author"];

        /// <summary>
        /// Saves a page of the wiki (normalised: its id the slug of its title, its links and summary): a new page when
        /// <paramref title="create"/> (refused when a page of that slug exists and is not deleted), else a new version of it
        /// (its version one more, its date of creation kept).
        /// </summary>
        public ServiceReturn WikiSave(Wiki page, bool create)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                page.Normalize();
                var existing = (Wiki)ElasticSearchServer.Load<Wiki>(page.id);
                if (create && existing is { isDeleted: false })
                {
                    ret.Message = $"A page \"{existing.title}\" ({page.id}) already exists";
                    ret.ReturnedId = "-1";
                    ret.Success = false;
                    return ret;
                }
                var now = DateTime.UtcNow;
                page.created = existing is { isDeleted: false } ? existing.created ?? existing.date : now;
                page.version = existing is { isDeleted: false } ? existing.version + 1 : 1;
                page.date = now;
                page.isDeleted = false;
                var retid = ElasticSearchServer.Save(page);
                ret.Message = retid + " id created. Wiki page well saved (version " + page.version + ")";
                ret.ReturnedId = retid;
                ret.Success = true;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Loads a page of the wiki by its slug (null when it does not exist or is deleted).</summary>
        public Wiki WikiGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            var page = (Wiki)ElasticSearchServer.Load<Wiki>(Wiki.Slug(id));
            return page is { isDeleted: false } ? page : null;
        }

        /// <summary>Searches the pages of the wiki on their title, content, summary, tags and author (<c>*</c> or empty: the newest ones), the deleted ones left out.</summary>
        public List<Wiki> WikisSearch(string term, int size)
        {
            return ElasticSearchServer.Search<Wiki>(term, WikiSearchFields, size, nameof(Wiki.isDeleted)).ToList();
        }

        /// <summary>The <paramref title="size"/> pages of the wiki changed last, newest first, the deleted ones left out.</summary>
        public List<Wiki> WikisLast(int size)
        {
            return ElasticSearchServer.Last<Wiki>(size, nameof(Wiki.isDeleted)).ToList();
        }

        /// <summary>The pages of the wiki that link to the page <paramref title="id"/> (among the 1000 changed last), by title.</summary>
        public List<Wiki> WikiBacklinks(string id)
        {
            var slug = Wiki.Slug(id);
            return ElasticSearchServer.Last<Wiki>(1000, nameof(Wiki.isDeleted))
                .Where(p => p.id != slug && p.links != null && p.links.Contains(slug))
                .OrderBy(p => p.title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        /// <summary>Marks a page of the wiki deleted (isDeleted = true): a page is never erased.</summary>
        public ServiceReturn WikiDelete(string id) => MarkDeleted<Wiki>(ElasticSearchServer, Wiki.Slug(id), "Wiki page");

        // ── CVs (/cv in the chat) ──

        private static readonly string[] CvSearchFields =
            ["title", "lastName", "firstName", "email", "phone", "addressCity", "addressCountry", "summary", "skills", "adaptation",
             "experiences.company", "experiences.position", "experiences.description", "experiences.technologies",
             "educations.institution", "educations.degree", "educations.fieldOfStudy", "educations.description",
             "creations.title", "creations.description", "tags"];

        /// <summary>
        /// Saves a CV (normalised: its title the name of the person when none is given): created when it does not
        /// exist, else a new version of it (its version one more, its date of creation kept).
        /// </summary>
        public ServiceReturn CvSave(Cv cv)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                cv.Normalize();
                var existing = (Cv)ElasticSearchServer.Load<Cv>(cv.id);
                var now = DateTime.UtcNow;
                cv.Created = existing is not null ? existing.Created ?? existing.date : now;
                cv.Version = existing is not null ? existing.Version + 1 : 1;
                cv.date = now;
                cv.isDeleted = false;
                var retid = ElasticSearchServer.Save(cv);
                ret.Message = retid + " id created. CV well saved (version " + cv.Version + ")";
                ret.ReturnedId = retid;
                ret.Success = true;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Loads a CV by its id (null when it does not exist or is deleted).</summary>
        public Cv CvGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            var cv = (Cv)ElasticSearchServer.Load<Cv>(id);
            return cv is { isDeleted: false } ? cv : null;
        }

        /// <summary>Searches the CVs on the person, the summary, the skills, the experiences, the educations and the creations (<c>*</c> or empty: the newest ones), the deleted ones left out.</summary>
        public List<Cv> CvsSearch(string term, int size)
        {
            return ElasticSearchServer.Search<Cv>(term, CvSearchFields, size, nameof(Cv.isDeleted)).ToList();
        }

        /// <summary>The <paramref title="size"/> CVs changed last, newest first, the deleted ones left out.</summary>
        public List<Cv> CvsLast(int size)
        {
            return ElasticSearchServer.Last<Cv>(size, nameof(Cv.isDeleted)).ToList();
        }

        /// <summary>Marks a CV deleted (isDeleted = true): a CV is never erased.</summary>
        public ServiceReturn CvDelete(string id) => MarkDeleted<Cv>(ElasticSearchServer, id, "CV");

        // ── Interviews (/interview in the chat): the interview, and its transcription paragraph by paragraph ──

        private static readonly string[] InterviewSearchFields =
            ["title", "firstName", "lastName", "address", "email", "phone", "documentNumber", "content", "summary", "summaryNote", "tags"];

        /// <summary>
        /// Saves an interview (normalised: its title the name of the person, its summary): created when it does not
        /// exist, else a new version of it (its version one more, its date of creation kept).
        /// </summary>
        public ServiceReturn InterviewSave(Interview interview)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                interview.Normalize();
                var existing = (Interview)ElasticSearchServer.Load<Interview>(interview.id);
                var now = DateTime.UtcNow;
                interview.created = existing is not null ? existing.created ?? existing.date : now;
                interview.version = existing is not null ? existing.version + 1 : 1;
                interview.date = now;
                interview.isDeleted = false;
                var retid = ElasticSearchServer.Save(interview);
                ret.Message = retid + " id created. Interview well saved (version " + interview.version + ")";
                ret.ReturnedId = retid;
                ret.Success = true;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Loads an interview by its id (null when it does not exist or is deleted).</summary>
        public Interview InterviewGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            var interview = (Interview)ElasticSearchServer.Load<Interview>(id);
            return interview is { isDeleted: false } ? interview : null;
        }

        /// <summary>Searches the interviews on the person (name, address, email, phone, document number), the notes and the tags (<c>*</c> or empty: the newest ones), the deleted ones left out.</summary>
        public List<Interview> InterviewsSearch(string term, int size)
        {
            return ElasticSearchServer.Search<Interview>(term, InterviewSearchFields, size, nameof(Interview.isDeleted)).ToList();
        }

        /// <summary>The <paramref title="size"/> interviews changed last, newest first, the deleted ones left out.</summary>
        public List<Interview> InterviewsLast(int size)
        {
            return ElasticSearchServer.Last<Interview>(size, nameof(Interview.isDeleted)).ToList();
        }

        /// <summary>The paragraphs of the transcription of an interview, by recording then by rank, the deleted ones left out.</summary>
        public List<InterviewParagraph> InterviewParagraphs(string interviewId)
        {
            if (string.IsNullOrWhiteSpace(interviewId) || ElasticSearchServer == null)
            {
                return new List<InterviewParagraph>();
            }
            return ElasticSearchServer.WithValue<InterviewParagraph>(nameof(InterviewParagraph.interviewId), interviewId, 10000)
                .Where(p => !p.isDeleted && p.interviewId == interviewId)
                .OrderBy(p => p.audioFile, StringComparer.Ordinal)
                .ThenBy(p => p.index)
                .ToList();
        }

        /// <summary>The paragraph made ready to be saved: its id from its interview, recording and rank, its title the person.</summary>
        private static void Prepare(InterviewParagraph paragraph)
        {
            paragraph.interviewId = (paragraph.interviewId ?? string.Empty).Trim();
            paragraph.audioFile = Path.GetFileName((paragraph.audioFile ?? string.Empty).Trim());
            paragraph.text = (paragraph.text ?? string.Empty).Trim();
            paragraph.person = (paragraph.person ?? string.Empty).Trim();
            paragraph.id = InterviewParagraph.IdOf(paragraph.interviewId, paragraph.audioFile, paragraph.index);
            if (string.IsNullOrWhiteSpace(paragraph.title))
            {
                paragraph.title = paragraph.person.Length > 0 ? "Interview · " + paragraph.person : "Interview";
            }
            paragraph.isDeleted = false;
        }

        /// <summary>Saves (creates or overwrites) one paragraph of a transcription, index <c>interviewparagraph</c> (the live transcription: one at a time).</summary>
        public ServiceReturn InterviewParagraphSave(InterviewParagraph paragraph)
        {
            if (string.IsNullOrWhiteSpace(paragraph.interviewId))
            {
                return new ServiceReturn { ReturnedId = "-1", Success = false, Message = "The paragraph has no interview (interviewId)" };
            }
            Prepare(paragraph);
            return SaveInData(paragraph, "Interview paragraph");
        }

        /// <summary>
        /// Saves the transcription of one recording of an interview, paragraph by paragraph, in place of the one
        /// stored: the paragraphs of that recording that are no longer in it are marked deleted.
        /// </summary>
        public ServiceReturn InterviewTranscriptSave(InterviewTranscript transcript)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            if (transcript is null || string.IsNullOrWhiteSpace(transcript.interviewId))
            {
                return new ServiceReturn { ReturnedId = "-1", Success = false, Message = "The transcription has no interview (interviewId)" };
            }
            try
            {
                var interviewId = transcript.interviewId.Trim();
                var audioFile = Path.GetFileName((transcript.audioFile ?? string.Empty).Trim());
                var now = DateTime.UtcNow;
                var saved = new HashSet<string>();
                var rank = 0;
                foreach (var paragraph in (transcript.paragraphs ?? new List<InterviewParagraph>()).Where(p => !string.IsNullOrWhiteSpace(p?.text)))
                {
                    paragraph.interviewId = interviewId;
                    paragraph.audioFile = audioFile;
                    paragraph.index = rank++;
                    Prepare(paragraph);
                    if (paragraph.date == default)
                    {
                        paragraph.date = now;
                    }
                    saved.Add(ElasticSearchServer.Save(paragraph));
                }
                var removed = 0;
                foreach (var old in InterviewParagraphs(interviewId).Where(p => p.audioFile == audioFile && !saved.Contains(p.id)))
                {
                    old.isDeleted = true;
                    ElasticSearchServer.Save(old);
                    removed++;
                }
                ret.ReturnedId = interviewId;
                ret.Success = true;
                ret.Message = $"{saved.Count} paragraph(s) saved for {audioFile}" + (removed > 0 ? $", {removed} older one(s) marked deleted" : string.Empty);
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Marks an interview deleted (isDeleted = true), and the paragraphs of its transcription with it: nothing is erased, the WAV files stay.</summary>
        public ServiceReturn InterviewDelete(string id)
        {
            var ret = MarkDeleted<Interview>(ElasticSearchServer, id, "Interview");
            if (ret.ReturnedId is null or "-1")
            {
                return ret;
            }
            try
            {
                foreach (var paragraph in InterviewParagraphs(id))
                {
                    paragraph.isDeleted = true;
                    ElasticSearchServer.Save(paragraph);
                }
            }
            catch (Exception ex)
            {
                ret.Message += " (its paragraphs could not be marked deleted: " + ex.Message + ")";
            }
            return ret;
        }

        private static readonly string[] BpmnSearchFields = ["title", "description", "summary", "elements", "tags", "author"];

        /// <summary>
        /// Saves a BPMN diagram (normalised: its id the slug of its title, its elements and summary): a new diagram when
        /// <paramref title="create"/> (refused when a diagram of that slug exists and is not deleted), else a new version
        /// of it (its version one more, its date of creation kept).
        /// </summary>
        public ServiceReturn BpmnSave(Bpmn diagram, bool create) => DiagramSave(diagram, create);

        /// <summary>Saves an infrastructure diagram (/infra) as <see cref="BpmnSave"/> does, in the infra index.</summary>
        public ServiceReturn InfraSave(Infra diagram, bool create) => DiagramSave(diagram, create);

        /// <summary>Saves a diagram of <typeparamref title="T"/> (BPMN, infrastructure) in its own index; see <see cref="BpmnSave"/>.</summary>
        private ServiceReturn DiagramSave<T>(T diagram, bool create) where T : Bpmn
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                diagram.Normalize();
                var existing = (T)ElasticSearchServer.Load<T>(diagram.id);
                if (create && existing is { isDeleted: false })
                {
                    ret.Message = $"A diagram \"{existing.title}\" ({diagram.id}) already exists";
                    ret.ReturnedId = "-1";
                    ret.Success = false;
                    return ret;
                }
                var now = DateTime.UtcNow;
                diagram.created = existing is { isDeleted: false } ? existing.created ?? existing.date : now;
                diagram.version = existing is { isDeleted: false } ? existing.version + 1 : 1;
                diagram.date = now;
                diagram.isDeleted = false;
                var retid = ElasticSearchServer.Save(diagram);
                ret.Message = retid + " id created. BPMN diagram well saved (version " + diagram.version + ")";
                ret.ReturnedId = retid;
                ret.Success = true;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Loads a BPMN diagram by its slug (null when it does not exist or is deleted).</summary>
        public Bpmn BpmnGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            var diagram = (Bpmn)ElasticSearchServer.Load<Bpmn>(Bpmn.Slug(id));
            return diagram is { isDeleted: false } ? diagram : null;
        }

        /// <summary>Searches the BPMN diagrams on their title, comment, summary, elements, tags and author (<c>*</c> or empty: the newest ones), the deleted ones left out.</summary>
        public List<Bpmn> BpmnsSearch(string term, int size)
        {
            return ElasticSearchServer.Search<Bpmn>(term, BpmnSearchFields, size, nameof(Bpmn.isDeleted)).ToList();
        }

        /// <summary>The <paramref title="size"/> BPMN diagrams changed last, newest first, the deleted ones left out.</summary>
        public List<Bpmn> BpmnsLast(int size)
        {
            return ElasticSearchServer.Last<Bpmn>(size, nameof(Bpmn.isDeleted)).ToList();
        }

        /// <summary>Marks a BPMN diagram deleted (isDeleted = true): a diagram is never erased.</summary>
        public ServiceReturn BpmnDelete(string id) => MarkDeleted<Bpmn>(ElasticSearchServer, Bpmn.Slug(id), "BPMN diagram");

        /// <summary>Loads an infrastructure diagram by its slug (null when it does not exist or is deleted).</summary>
        public Infra InfraGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            var diagram = (Infra)ElasticSearchServer.Load<Infra>(Bpmn.Slug(id));
            return diagram is { isDeleted: false } ? diagram : null;
        }

        /// <summary>Searches the infrastructure diagrams as <see cref="BpmnsSearch"/> does.</summary>
        public List<Infra> InfrasSearch(string term, int size) =>
            ElasticSearchServer.Search<Infra>(term, BpmnSearchFields, size, nameof(Bpmn.isDeleted)).ToList();

        /// <summary>The <paramref title="size"/> infrastructure diagrams changed last, newest first, the deleted ones left out.</summary>
        public List<Infra> InfrasLast(int size) => ElasticSearchServer.Last<Infra>(size, nameof(Bpmn.isDeleted)).ToList();

        /// <summary>Marks an infrastructure diagram deleted (isDeleted = true): it is never erased.</summary>
        public ServiceReturn InfraDelete(string id) => MarkDeleted<Infra>(ElasticSearchServer, Bpmn.Slug(id), "Infrastructure diagram");

        private static readonly string[] MindmapSearchFields = ["title", "description", "summary", "elements", "tags", "author"];

        /// <summary>
        /// Saves a mind map (normalised: its id the slug of its title, its nodes and summary): a new map when
        /// <paramref title="create"/> (refused when a map of that slug exists and is not deleted), else a new version of
        /// it (its version one more, its date of creation kept).
        /// </summary>
        public ServiceReturn MindmapSave(Mindmap map, bool create)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                map.Normalize();
                var existing = (Mindmap)ElasticSearchServer.Load<Mindmap>(map.id);
                if (create && existing is { isDeleted: false })
                {
                    ret.Message = $"A map \"{existing.title}\" ({map.id}) already exists";
                    ret.ReturnedId = "-1";
                    ret.Success = false;
                    return ret;
                }
                var now = DateTime.UtcNow;
                map.created = existing is { isDeleted: false } ? existing.created ?? existing.date : now;
                map.version = existing is { isDeleted: false } ? existing.version + 1 : 1;
                map.date = now;
                map.isDeleted = false;
                var retid = ElasticSearchServer.Save(map);
                ret.Message = retid + " id created. Mind map well saved (version " + map.version + ")";
                ret.ReturnedId = retid;
                ret.Success = true;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Loads a mind map by its slug (null when it does not exist or is deleted).</summary>
        public Mindmap MindmapGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            var map = (Mindmap)ElasticSearchServer.Load<Mindmap>(Mindmap.Slug(id));
            return map is { isDeleted: false } ? map : null;
        }

        /// <summary>Searches the mind maps on their title, comment, summary, nodes, tags and author (<c>*</c> or empty: the newest ones), the deleted ones left out.</summary>
        public List<Mindmap> MindmapsSearch(string term, int size)
        {
            return ElasticSearchServer.Search<Mindmap>(term, MindmapSearchFields, size, nameof(Mindmap.isDeleted)).ToList();
        }

        /// <summary>The <paramref title="size"/> mind maps changed last, newest first, the deleted ones left out.</summary>
        public List<Mindmap> MindmapsLast(int size)
        {
            return ElasticSearchServer.Last<Mindmap>(size, nameof(Mindmap.isDeleted)).ToList();
        }

        /// <summary>Marks a mind map deleted (isDeleted = true): a map is never erased.</summary>
        public ServiceReturn MindmapDelete(string id) => MarkDeleted<Mindmap>(ElasticSearchServer, Mindmap.Slug(id), "Mind map");

        private static readonly string[] GhostWriterSearchFields = ["title", "description", "goals", "audienceDescription", "style", "author", "summary"];

        /// <summary>
        /// Saves a writing (normalised: its id the slug of its title, its chapters numbered): a new writing when
        /// <paramref title="create"/> (refused when a writing of that slug exists and is not deleted), else a new version
        /// of it (its version one more, its date of creation kept).
        /// </summary>
        public ServiceReturn GhostWriterSave(GhostWriter writing, bool create)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                writing.Normalize();
                var existing = (GhostWriter)ElasticSearchServer.Load<GhostWriter>(writing.id);
                if (create && existing is { isDeleted: false })
                {
                    ret.Message = $"A writing \"{existing.title}\" ({writing.id}) already exists";
                    ret.ReturnedId = "-1";
                    ret.Success = false;
                    return ret;
                }
                var now = DateTime.UtcNow;
                writing.created = existing is { isDeleted: false } ? existing.created ?? existing.date : now;
                writing.version = existing is { isDeleted: false } ? existing.version + 1 : 1;
                writing.date = now;
                writing.isDeleted = false;
                var retid = ElasticSearchServer.Save(writing);
                ret.Message = retid + " id created. Writing well saved (version " + writing.version + ")";
                ret.ReturnedId = retid;
                ret.Success = true;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Loads a writing by its slug (null when it does not exist or is deleted).</summary>
        public GhostWriter GhostWriterGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            var writing = (GhostWriter)ElasticSearchServer.Load<GhostWriter>(GhostWriter.Slug(id));
            return writing is { isDeleted: false } ? writing : null;
        }

        /// <summary>Searches the writings on their title, description, goals, audience, style and author (<c>*</c> or empty: the newest ones), the deleted ones left out.</summary>
        public List<GhostWriter> GhostWritersSearch(string term, int size)
        {
            return ElasticSearchServer.Search<GhostWriter>(term, GhostWriterSearchFields, size, nameof(GhostWriter.isDeleted)).ToList();
        }

        /// <summary>The <paramref title="size"/> writings changed last, newest first, the deleted ones left out.</summary>
        public List<GhostWriter> GhostWritersLast(int size)
        {
            return ElasticSearchServer.Last<GhostWriter>(size, nameof(GhostWriter.isDeleted)).ToList();
        }

        /// <summary>Marks a writing deleted (isDeleted = true): a writing is never erased.</summary>
        public ServiceReturn GhostWriterDelete(string id) => MarkDeleted<GhostWriter>(ElasticSearchServer, GhostWriter.Slug(id), "Writing");

        /// <summary>
        /// Adds an entry to the journal of a writing. The journal only grows: the entry gets a new id and the date of
        /// now whatever was sent (an entry sent twice is two entries; one is never replaced), and there is no update nor
        /// delete of an entry.
        /// </summary>
        public ServiceReturn GhostWriterJournalAdd(GhostWriterJournal entry)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                if (string.IsNullOrWhiteSpace(entry.writingId) || string.IsNullOrWhiteSpace(entry.action))
                {
                    ret.Message = "An entry of the journal needs the id of its writing and its action";
                    ret.ReturnedId = "-1";
                    ret.Success = false;
                    return ret;
                }
                entry.id = Guid.NewGuid().ToString();
                entry.writingId = GhostWriter.Slug(entry.writingId);
                entry.date = DateTime.UtcNow;
                entry.happened ??= entry.date;
                var retid = ElasticSearchServer.Save(entry);
                ret.Message = retid + " id created. Journal entry added";
                ret.ReturnedId = retid;
                ret.Success = true;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>The <paramref title="size"/> last entries of the journal of the writing <paramref title="writingId"/>, newest first.</summary>
        public List<GhostWriterJournal> GhostWriterJournalOf(string writingId, int size)
        {
            var id = GhostWriter.Slug(writingId);
            return ElasticSearchServer.WithValue<GhostWriterJournal>(nameof(GhostWriterJournal.writingId), id, size)
                .Where(e => e.writingId == id)
                .ToList();
        }

        public ServiceReturn IdeaSave(Idea idea)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            if (idea.date == default)
            {
                idea.date = DateTime.UtcNow;
            }
            try
            {
                string retid = "";
                retid = ElasticSearchServer.Save(idea);
                ret.Message = retid + " id created. Idea well saved";
                ret.ReturnedId = retid;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Loads an idea by id from the data repository (null when it does not exist).</summary>
        public Idea IdeaGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            return (Idea)ElasticSearchServer.Load<Idea>(id);
        }

        /// <summary>Searches ideas on title, description, category and tags (<c>*</c> or empty: the newest ones).</summary>
        public List<Idea> IdeasSearch(string term, int size)
        {
            return ElasticSearchServer.Search<Idea>(term, IdeaSearchFields, size).ToList();
        }

        /// <summary>The <paramref title="size"/> most recent ideas, newest first.</summary>
        public List<Idea> IdeasLast(int size)
        {
            return ElasticSearchServer.Last<Idea>(size).ToList();
        }

        /// <summary>Deletes an idea from the data repository.</summary>
        public ServiceReturn IdeaDelete(string id)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                var deleted = ElasticSearchServer.Delete<Idea>(id); // marked deleted (isDeleted = true), never erased
                ret.ReturnedId = deleted ? id : "-1";
                ret.Success = deleted;
                ret.Message = deleted ? id + " idea marked as deleted (isDeleted = true)" : "Idea " + id + " not found";
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }


        /// <summary>Field of <see cref="Diary"/> searched by <see cref="DiariesSearch"/>.</summary>
        private static readonly string[] DiarySearchFields = { "text" };

        /// <summary>Saves (creates or overwrites) a diary entry in the data repository; a missing date is set to now.</summary>
        public ServiceReturn DiarySave(Diary diary)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            if (diary.date == default)
            {
                diary.date = DateTime.UtcNow;
            }
            try
            {
                string retid = "";
                retid = ElasticSearchServer.Save(diary);
                ret.Message = retid + " id created. Diary well saved";
                ret.ReturnedId = retid;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Loads a diary entry by id from the data repository (null when it does not exist).</summary>
        public Diary DiaryGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            return (Diary)ElasticSearchServer.Load<Diary>(id);
        }

        /// <summary>
        /// Searches diary entries on the <c>text</c> field only (<c>*</c> or empty: the newest ones); deleted entries
        /// (<c>isDeleted</c>) are left out.
        /// </summary>
        public List<Diary> DiariesSearch(string term, int size)
        {
            return ElasticSearchServer.Search<Diary>(term, DiarySearchFields, size, nameof(Diary.isDeleted)).ToList();
        }

        /// <summary>The <paramref title="size"/> most recent diary entries, newest first, without the deleted ones.</summary>
        public List<Diary> DiariesLast(int size)
        {
            return ElasticSearchServer.Last<Diary>(size, nameof(Diary.isDeleted)).ToList();
        }

        /// <summary>
        /// Soft delete: the diary entry is kept but marked <c>isDeleted = true</c> (no longer returned by search and
        /// last; GET /diary/{id} still reads it, and POST /diary/update with <c>isDeleted = false</c> restores it).
        /// </summary>
        public ServiceReturn DiaryDelete(string id)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                var diary = DiaryGet(id);
                if (diary == null)
                {
                    ret.ReturnedId = "-1";
                    ret.Success = false;
                    ret.Message = "Diary " + id + " not found";
                    return ret;
                }
                diary.isDeleted = true;
                ret.ReturnedId = ElasticSearchServer.Save(diary);
                ret.Message = id + " diary marked as deleted (isDeleted = true)";
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }


        /// <summary>Fields of <see cref="TaskItem"/> searched by <see cref="TaskItemsSearch"/>.</summary>
        private static readonly string[] TaskItemSearchFields = { "name", "description", "type", "status", "comment" };

        /// <summary>Saves (creates or overwrites) a task in the data repository; a missing date is set to now.</summary>
        public ServiceReturn TaskItemSave(TaskItem task)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            if (task.date == default)
            {
                task.date = DateTime.UtcNow;
            }
            try
            {
                string retid = "";
                retid = ElasticSearchServer.Save(task);
                ret.Message = retid + " id created. Task well saved";
                ret.ReturnedId = retid;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Loads a task by id from the data repository, deleted or not (null when it does not exist).</summary>
        public TaskItem TaskItemGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            return (TaskItem)ElasticSearchServer.Load<TaskItem>(id);
        }

        /// <summary>
        /// Searches tasks on title, description, type, status and comment (<c>*</c> or empty: the newest ones); deleted
        /// tasks (<c>isDeleted</c>) are left out.
        /// </summary>
        public List<TaskItem> TaskItemsSearch(string term, int size)
        {
            return ElasticSearchServer.Search<TaskItem>(term, TaskItemSearchFields, size, nameof(TaskItem.isDeleted)).ToList();
        }

        /// <summary>The <paramref title="size"/> most recent tasks, newest first, without the deleted ones.</summary>
        public List<TaskItem> TaskItemsLast(int size)
        {
            return ElasticSearchServer.Last<TaskItem>(size, nameof(TaskItem.isDeleted)).ToList();
        }

        /// <summary>
        /// Soft delete: the task is kept but marked <c>isDeleted = true</c> (no longer returned by search and last;
        /// GET /task/{id} still reads it, and POST /task/update with <c>isDeleted = false</c> restores it).
        /// </summary>
        public ServiceReturn TaskItemDelete(string id)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                var task = TaskItemGet(id);
                if (task == null)
                {
                    ret.ReturnedId = "-1";
                    ret.Success = false;
                    ret.Message = "Task " + id + " not found";
                    return ret;
                }
                task.isDeleted = true;
                ret.ReturnedId = ElasticSearchServer.Save(task);
                ret.Message = id + " task marked as deleted (isDeleted = true)";
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>
        /// Fields of <see cref="Intel"/> searched by <see cref="IntelsSearch"/>: every text field of the intel (its lists
        /// too) and of its origin.
        /// </summary>
        private static readonly string[] IntelSearchFields =
        {
            "title", "instructions", "includedEntities.entityName", "excludedEntities.entityName",
            "locations", "times", "relatedEntities", "questions", "tags", "events", "signals",
            "comment", "appName", "userName", "machineName"
        };

        /// <summary>Saves (creates or overwrites) an intel in the data repository; a missing date is set to now.</summary>
        public ServiceReturn IntelSave(Intel intel)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            if (intel.date == default)
            {
                intel.date = DateTime.UtcNow;
            }
            // The trigger level is a percentage; the entities without a name are left out.
            intel.TriggerLevel = Math.Clamp(intel.TriggerLevel, 0, 100);
            intel.IncludedEntities = (intel.IncludedEntities ?? new List<EntityWithWeight>()).Where(e => !string.IsNullOrWhiteSpace(e?.EntityName)).ToList();
            intel.ExcludedEntities = (intel.ExcludedEntities ?? new List<EntityWithWeight>()).Where(e => !string.IsNullOrWhiteSpace(e?.EntityName)).ToList();
            try
            {
                string retid = "";
                retid = ElasticSearchServer.Save(intel);
                ret.Message = retid + " id created. Intel well saved";
                ret.ReturnedId = retid;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Loads an intel by id from the data repository, deleted or not (null when it does not exist).</summary>
        public Intel IntelGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            return (Intel)ElasticSearchServer.Load<Intel>(id);
        }

        /// <summary>
        /// Searches intels in all their text fields (<see cref="IntelSearchFields"/>; <c>*</c> or empty: the newest
        /// ones); deleted intels (<c>isDeleted</c>) are left out.
        /// </summary>
        public List<Intel> IntelsSearch(string term, int size)
        {
            return ElasticSearchServer.Search<Intel>(term, IntelSearchFields, size, nameof(Intel.isDeleted)).ToList();
        }

        /// <summary>The <paramref title="size"/> most recent intels, newest first, without the deleted ones.</summary>
        public List<Intel> IntelsLast(int size)
        {
            return ElasticSearchServer.Last<Intel>(size, nameof(Intel.isDeleted)).ToList();
        }

        /// <summary>
        /// Soft delete: the intel is kept but marked <c>isDeleted = true</c> (no longer returned by search and last;
        /// GET /intel/{id} still reads it, and POST /intel/update with <c>isDeleted = false</c> restores it).
        /// </summary>
        public ServiceReturn IntelDelete(string id)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                var intel = IntelGet(id);
                if (intel == null)
                {
                    ret.ReturnedId = "-1";
                    ret.Success = false;
                    ret.Message = "Intel " + id + " not found";
                    return ret;
                }
                intel.isDeleted = true;
                ret.ReturnedId = ElasticSearchServer.Save(intel);
                ret.Message = id + " intel marked as deleted (isDeleted = true)";
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>
        /// Fields of <see cref="CollectionPlan"/> searched by <see cref="CollectionPlansSearch"/>: every text field of the
        /// plan, of its websites and their URLs, and of its origin.
        /// </summary>
        private static readonly string[] CollectionPlanSearchFields =
        {
            "name", "description", "project", "instructions",
            "websites.name", "websites.urlProcesses.url", "websites.urlProcesses.keywords",
            "comment", "appName", "userName", "machineName"
        };

        /// <summary>Saves (creates or overwrites) a collection plan in the data repository; a missing date is set to now.</summary>
        public ServiceReturn CollectionPlanSave(CollectionPlan plan)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            if (plan.date == default)
            {
                plan.date = DateTime.UtcNow;
            }
            try
            {
                string retid = "";
                retid = ElasticSearchServer.Save(plan);
                ret.Message = retid + " id created. Collection plan well saved";
                ret.ReturnedId = retid;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Loads a collection plan by id from the data repository, deleted or not (null when it does not exist).</summary>
        public CollectionPlan CollectionPlanGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            return (CollectionPlan)ElasticSearchServer.Load<CollectionPlan>(id);
        }

        /// <summary>
        /// Searches collection plans in all their text fields (<see cref="CollectionPlanSearchFields"/>; <c>*</c> or
        /// empty: the newest ones); deleted plans (<c>isDeleted</c>) are left out.
        /// </summary>
        public List<CollectionPlan> CollectionPlansSearch(string term, int size)
        {
            return ElasticSearchServer.Search<CollectionPlan>(term, CollectionPlanSearchFields, size, nameof(CollectionPlan.isDeleted)).ToList();
        }

        /// <summary>The <paramref title="size"/> most recent collection plans, newest first, without the deleted ones.</summary>
        public List<CollectionPlan> CollectionPlansLast(int size)
        {
            return ElasticSearchServer.Last<CollectionPlan>(size, nameof(CollectionPlan.isDeleted)).ToList();
        }

        /// <summary>Saves (creates or overwrites) a web page of the web scraper in the data repository (index <c>webpage</c>).</summary>
        public ServiceReturn WebPageSave(WebPage page) => SaveInData(page, "Web page");

        /// <summary>Saves (creates or overwrites) a paragraph of a web page in the data repository (index <c>webparagraph</c>).</summary>
        public ServiceReturn WebParagraphSave(WebParagraph paragraph) => SaveInData(paragraph, "Web paragraph");

        /// <summary>
        /// The most recent visit of <paramref title="url"/> by the web scraper (its URL or the URL reached), or null when
        /// the page was never saved.
        /// </summary>
        public WebPage? WebPageLastVisit(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || ElasticSearchServer == null)
            {
                return null;
            }
            var hits = ElasticSearchServer.Search<WebPage>(url, new[] { "url", "finalUrl" }, 50, nameof(WebPage.isDeleted));
            return hits.Where(p => string.Equals(p.url, url, StringComparison.OrdinalIgnoreCase) || string.Equals(p.finalUrl, url, StringComparison.OrdinalIgnoreCase))
                       .OrderByDescending(p => p.scrapedAt)
                       .FirstOrDefault();
        }

        /// <summary>Saves a document in the data repository; a missing date is set to now.</summary>
        private ServiceReturn SaveInData<TEntity>(TEntity entity, string what) where TEntity : EntityBase
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            if (entity.date == default)
            {
                entity.date = DateTime.UtcNow;
            }
            try
            {
                ret.ReturnedId = ElasticSearchServer.Save(entity);
                ret.Message = ret.ReturnedId + " id created. " + what + " well saved";
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>
        /// Soft delete: the collection plan is kept but marked <c>isDeleted = true</c> (no longer returned by search and
        /// last; GET /collectionplan/{id} still reads it, and POST /collectionplan/update with <c>isDeleted = false</c>
        /// restores it).
        /// </summary>
        public ServiceReturn CollectionPlanDelete(string id)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                var plan = CollectionPlanGet(id);
                if (plan == null)
                {
                    ret.ReturnedId = "-1";
                    ret.Success = false;
                    ret.Message = "Collection plan " + id + " not found";
                    return ret;
                }
                plan.isDeleted = true;
                ret.ReturnedId = ElasticSearchServer.Save(plan);
                ret.Message = id + " collection plan marked as deleted (isDeleted = true)";
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Fields of <see cref="UserActivities"/> searched by <see cref="UserActivitiesSearch"/>.</summary>
        private static readonly string[] UserActivitySearchFields = { "action", "windowName", "link", "description", "comment" };

        /// <summary>Saves an activity of the journal (index useractivities); a missing date is set to now.</summary>
        public ServiceReturn UserActivitySave(UserActivities activity)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            if (activity.date == default)
            {
                activity.date = DateTime.UtcNow;
            }
            try
            {
                string retid = "";
                retid = ElasticSearchServer.Save(activity);
                ret.Message = retid + " id created. User activity well saved";
                ret.ReturnedId = retid;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>The <paramref title="size"/> most recent activities, newest first, without the deleted ones.</summary>
        public List<UserActivities> UserActivitiesLast(int size)
        {
            return ElasticSearchServer.Last<UserActivities>(size, nameof(UserActivities.isDeleted)).ToList();
        }

        /// <summary>Searches the activities in their text fields (<c>*</c> or empty: the newest); deleted ones left out.</summary>
        public List<UserActivities> UserActivitiesSearch(string term, int size)
        {
            return ElasticSearchServer.Search<UserActivities>(term, UserActivitySearchFields, size, nameof(UserActivities.isDeleted)).ToList();
        }

        /// <summary>Saves the score of a finished game; a missing date is set to now.</summary>
        public ServiceReturn GameScoreSave(GameScore gameScore)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            if (gameScore.date == default)
            {
                gameScore.date = DateTime.UtcNow;
            }
            try
            {
                string retid = "";
                retid = ElasticSearchServer.Save(gameScore);
                ret.Message = retid + " id created. Game score well saved";
                ret.ReturnedId = retid;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>
        /// The <paramref title="size"/> best scores of <paramref title="game"/> (the highest first, the oldest first on a tie),
        /// among its last 5000 scores; deleted scores are left out.
        /// </summary>
        public List<GameScore> GameScoresBest(string game, int size)
        {
            return ElasticSearchServer.Last<GameScore>(5000, nameof(GameScore.isDeleted))
                .Where(s => string.Equals(s.game, game, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.score)
                .ThenBy(s => s.date)
                .Take(size)
                .ToList();
        }

        /// <summary>The <paramref title="size"/> most recent scores (all games), newest first, without the deleted ones.</summary>
        public List<GameScore> GameScoresLast(int size)
        {
            return ElasticSearchServer.Last<GameScore>(size, nameof(GameScore.isDeleted)).ToList();
        }

        /// <summary>Soft delete: the score is kept but marked <c>isDeleted = true</c> (no longer in the best and last scores).</summary>
        public ServiceReturn GameScoreDelete(string id)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                var gameScore = string.IsNullOrWhiteSpace(id) ? null : (GameScore)ElasticSearchServer.Load<GameScore>(id);
                if (gameScore == null)
                {
                    ret.ReturnedId = "-1";
                    ret.Success = false;
                    ret.Message = "Game score " + id + " not found";
                    return ret;
                }
                gameScore.isDeleted = true;
                ret.ReturnedId = ElasticSearchServer.Save(gameScore);
                ret.Message = id + " game score marked as deleted (isDeleted = true)";
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Fields of <see cref="YoutubeVideo"/> searched by <see cref="YoutubeVideosSearch"/>.</summary>
        private static readonly string[] YoutubeVideoSearchFields = { "title", "description" };

        /// <summary>Saves (creates or overwrites) a YouTube video in the data repository; a missing date is set to now.</summary>
        public ServiceReturn YoutubeVideoSave(YoutubeVideo video)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            if (video.date == default)
            {
                video.date = DateTime.UtcNow;
            }
            try
            {
                string retid = "";
                retid = ElasticSearchServer.Save(video);
                ret.Message = retid + " id created. YouTube video well saved";
                ret.ReturnedId = retid;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Loads a YouTube video by id from the data repository, deleted or not (null when it does not exist).</summary>
        public YoutubeVideo YoutubeVideoGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            return (YoutubeVideo)ElasticSearchServer.Load<YoutubeVideo>(id);
        }

        /// <summary>
        /// Searches YouTube videos on their <c>title</c> and <c>description</c> (<c>*</c> or empty: the newest ones);
        /// deleted videos (<c>isDeleted</c>) are left out.
        /// </summary>
        public List<YoutubeVideo> YoutubeVideosSearch(string term, int size)
        {
            return ElasticSearchServer.Search<YoutubeVideo>(term, YoutubeVideoSearchFields, size, nameof(YoutubeVideo.isDeleted)).ToList();
        }

        /// <summary>The <paramref title="size"/> most recent YouTube videos, newest first, without the deleted ones.</summary>
        public List<YoutubeVideo> YoutubeVideosLast(int size)
        {
            return ElasticSearchServer.Last<YoutubeVideo>(size, nameof(YoutubeVideo.isDeleted)).ToList();
        }

        /// <summary>
        /// Soft delete: the YouTube video is kept but marked <c>isDeleted = true</c> (no longer returned by search and
        /// last; GET /youtubevideo/{id} still reads it, and POST /youtubevideo/update with <c>isDeleted = false</c>
        /// restores it).
        /// </summary>
        public ServiceReturn YoutubeVideoDelete(string id)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                var video = YoutubeVideoGet(id);
                if (video == null)
                {
                    ret.ReturnedId = "-1";
                    ret.Success = false;
                    ret.Message = "YouTube video " + id + " not found";
                    return ret;
                }
                video.isDeleted = true;
                ret.ReturnedId = ElasticSearchServer.Save(video);
                ret.Message = id + " YouTube video marked as deleted (isDeleted = true)";
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }


        /// <summary>
        /// Fields of <see cref="Document"/> searched by <see cref="DocumentsSearch"/>: its <c>title</c>, its
        /// <c>description</c> and the text of every version of its content (<c>contents.content</c>).
        /// </summary>
        private static readonly string[] DocumentSearchFields = { "title", "description", "contents.content" };

        /// <summary>Saves (creates or overwrites) a document in the data repository; a missing date is set to now.</summary>
        public ServiceReturn DocumentSave(Document document)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            if (document.date == default)
            {
                document.date = DateTime.UtcNow;
            }
            try
            {
                string retid = "";
                retid = ElasticSearchServer.Save(document);
                ret.Message = retid + " id created. Document well saved";
                ret.ReturnedId = retid;
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Loads a document by id from the data repository, deleted or not (null when it does not exist).</summary>
        public Document DocumentGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            return (Document)ElasticSearchServer.Load<Document>(id);
        }

        /// <summary>
        /// Fuzzy search of documents on their <c>title</c>, <c>description</c> and content (<see cref="DocumentSearchFields"/>):
        /// a term matches when one of these fields holds all its words, typos allowed, or holds it as a part of its text;
        /// <c>and</c> / <c>or</c> and parentheses combine terms (<c>*</c> or empty: the newest documents); deleted documents
        /// (<c>isDeleted</c>) are left out.
        /// </summary>
        public List<Document> DocumentsSearch(string term, int size)
        {
            return ElasticSearchServer.Search<Document>(term, DocumentSearchFields, size, nameof(Document.isDeleted)).ToList();
        }

        /// <summary>The <paramref title="size"/> most recent documents, newest first, without the deleted ones.</summary>
        public List<Document> DocumentsLast(int size)
        {
            return ElasticSearchServer.Last<Document>(size, nameof(Document.isDeleted)).ToList();
        }

        /// <summary>
        /// Soft delete: the document is kept but marked <c>isDeleted = true</c> (no longer returned by search and last;
        /// GET /document/{id} still reads it, and POST /document/update with <c>isDeleted = false</c> restores it).
        /// </summary>
        public ServiceReturn DocumentDelete(string id)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                var document = DocumentGet(id);
                if (document == null)
                {
                    ret.ReturnedId = "-1";
                    ret.Success = false;
                    ret.Message = "Document " + id + " not found";
                    return ret;
                }
                document.isDeleted = true;
                ret.ReturnedId = ElasticSearchServer.Save(document);
                ret.Message = id + " document marked as deleted (isDeleted = true)";
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }


        /// <summary>
        /// Fields of <see cref="ImageAnalysis"/> searched by <see cref="ImageAnalysesSearch"/>: the file title, the AI
        /// analysis, the comments, the comments of the annotations and the place.
        /// </summary>
        private static readonly string[] ImageAnalysisSearchFields = { "title", "fileName", "aiAnalysis", "comments.text", "annotations.comment", "locationName", "imagePath", "category", "comment" };

        /// <summary>
        /// Saves (creates or overwrites) the analysis of a picture in the data repository: its id is always
        /// <see cref="ImageAnalysis.IdFor"/> its picture (one analysis per picture), its link to the picture is set when
        /// missing, and the date is the one of the save.
        /// </summary>
        public ServiceReturn ImageAnalysisSave(ImageAnalysis analysis)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            if (string.IsNullOrWhiteSpace(analysis.imagePath))
            {
                ret.ReturnedId = "-1";
                ret.Success = false;
                ret.Message = "imagePath is missing: the analysis must give the path of its picture";
                return ret;
            }
            analysis.id = ImageAnalysis.IdFor(analysis.imagePath);
            analysis.imageUrl ??= new Uri(Path.GetFullPath(analysis.imagePath)).AbsoluteUri;
            analysis.fileName ??= Path.GetFileName(analysis.imagePath);
            analysis.date = DateTime.UtcNow;
            try
            {
                ret.ReturnedId = ElasticSearchServer.Save(analysis);
                ret.Message = ret.ReturnedId + " image analysis saved";
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Loads an image analysis by id, deleted or not (null when it does not exist).</summary>
        public ImageAnalysis ImageAnalysisGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            return (ImageAnalysis)ElasticSearchServer.Load<ImageAnalysis>(id);
        }

        /// <summary>The analysis of the picture at <paramref title="imagePath"/> (null when it has none yet).</summary>
        public ImageAnalysis ImageAnalysisOfImage(string imagePath)
        {
            return string.IsNullOrWhiteSpace(imagePath) ? null : ImageAnalysisGet(ImageAnalysis.IdFor(imagePath));
        }

        /// <summary>
        /// Fuzzy search of the image analyses (<see cref="ImageAnalysisSearchFields"/>; <c>and</c> / <c>or</c> and
        /// parentheses combine terms; <c>*</c> or empty: the newest ones); deleted analyses are left out.
        /// </summary>
        public List<ImageAnalysis> ImageAnalysesSearch(string term, int size)
        {
            return ElasticSearchServer.Search<ImageAnalysis>(term, ImageAnalysisSearchFields, size, nameof(ImageAnalysis.isDeleted)).ToList();
        }

        /// <summary>The <paramref title="size"/> most recent image analyses, without the deleted ones.</summary>
        public List<ImageAnalysis> ImageAnalysesLast(int size)
        {
            return ElasticSearchServer.Last<ImageAnalysis>(size, nameof(ImageAnalysis.isDeleted)).ToList();
        }

        /// <summary>Soft delete: the analysis is kept but marked <c>isDeleted = true</c>.</summary>
        public ServiceReturn ImageAnalysisDelete(string id)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                var analysis = ImageAnalysisGet(id);
                if (analysis == null)
                {
                    ret.ReturnedId = "-1";
                    ret.Success = false;
                    ret.Message = "Image analysis " + id + " not found";
                    return ret;
                }
                analysis.isDeleted = true;
                ret.ReturnedId = ElasticSearchServer.Save(analysis);
                ret.Message = id + " image analysis marked as deleted (isDeleted = true)";
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }


        /// <summary>
        /// Fields of <see cref="Relation"/> searched by <see cref="RelationsSearch"/>: the nature, title and description of
        /// its versions, and the window, object and information of their ends.
        /// </summary>
        private static readonly string[] RelationSearchFields =
        {
            "category", "versions.nature", "versions.title", "versions.description",
            "versions.source.windowName", "versions.source.objectId", "versions.source.information",
            "versions.destinations.windowName", "versions.destinations.objectId", "versions.destinations.information"
        };

        /// <summary>
        /// Saves a version of a relation. A new relation (no id, or an id not stored yet) is created with the versions it
        /// brings, numbered from 1; for a stored relation, the last version brought is added to its versions with the
        /// next number (the previous versions are kept as they are). Each version added gets the date of the save; the
        /// relation gets the date of its last version.
        /// </summary>
        public ServiceReturn RelationSave(Relation relation)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            if (relation.versions == null || relation.versions.Count == 0)
            {
                ret.ReturnedId = "-1";
                ret.Success = false;
                ret.Message = "The relation brings no version to save";
                return ret;
            }
            try
            {
                var now = DateTime.UtcNow;
                var stored = string.IsNullOrWhiteSpace(relation.id) ? null : RelationGet(relation.id);
                Relation saved;
                if (stored == null)
                {
                    saved = relation;
                    for (var i = 0; i < saved.versions.Count; i++)
                    {
                        saved.versions[i].version = i + 1;
                        if (saved.versions[i].date == default)
                        {
                            saved.versions[i].date = now;
                        }
                    }
                }
                else
                {
                    var added = relation.versions[^1];
                    added.version = stored.versions.Count == 0 ? 1 : stored.versions.Max(v => v.version) + 1;
                    added.date = now;
                    stored.versions.Add(added);
                    stored.isDeleted = relation.isDeleted;
                    if (!string.IsNullOrWhiteSpace(relation.category))
                    {
                        stored.category = relation.category;
                    }
                    saved = stored;
                }
                if (string.IsNullOrWhiteSpace(saved.category))
                {
                    saved.category = "interdata";
                }
                saved.date = now;
                // Searchable at once: the relation graph searches again right after a save.
                ret.ReturnedId = ElasticSearchServer.SaveSearchable(saved);
                ret.Message = $"{ret.ReturnedId} relation saved (version {saved.versions[^1].version})";
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Loads a relation by id, with all its versions, deleted or not (null when it does not exist).</summary>
        public Relation RelationGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            return (Relation)ElasticSearchServer.Load<Relation>(id);
        }

        /// <summary>
        /// Fuzzy search of the relations, on all their versions (<see cref="RelationSearchFields"/>; <c>and</c> / <c>or</c>
        /// and parentheses combine terms; <c>*</c> or empty: the newest ones); deleted relations are left out.
        /// </summary>
        public List<Relation> RelationsSearch(string term, int size)
        {
            return ElasticSearchServer.Search<Relation>(term, RelationSearchFields, size, nameof(Relation.isDeleted)).ToList();
        }

        /// <summary>The <paramref title="size"/> most recent relations, without the deleted ones.</summary>
        public List<Relation> RelationsLast(int size)
        {
            return ElasticSearchServer.Last<Relation>(size, nameof(Relation.isDeleted)).ToList();
        }

        /// <summary>Soft delete: the relation is kept but marked <c>isDeleted = true</c>.</summary>
        public ServiceReturn RelationDelete(string id)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                var relation = RelationGet(id);
                if (relation == null)
                {
                    ret.ReturnedId = "-1";
                    ret.Success = false;
                    ret.Message = "Relation " + id + " not found";
                    return ret;
                }
                relation.isDeleted = true;
                // Left out of the searches at once: the relation graph searches again right after a delete.
                ret.ReturnedId = ElasticSearchServer.SaveSearchable(relation);
                ret.Message = id + " relation marked as deleted (isDeleted = true)";
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }


        /// <summary>Fields of <see cref="Annotation"/> searched by <see cref="AnnotationsSearch"/>.</summary>
        private static readonly string[] AnnotationSearchFields =
        {
            "text", "tags", "targetType", "targetId", "targetTitle", "excerpt", "comment"
        };

        /// <summary>
        /// Saves an annotation (index <c>annotation</c>): a new one without an id (or with an id not stored yet), else it
        /// replaces the stored one. Its tags are normalized (<c>#SOFTWARE</c>…, no duplicates) and it can be searched as
        /// soon as this returns. Returns its id, or "-1" with the error message.
        /// </summary>
        public ServiceReturn AnnotationSave(Annotation annotation)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            if (string.IsNullOrWhiteSpace(annotation.text) && (annotation.Tags == null || annotation.Tags.Count == 0))
            {
                ret.ReturnedId = "-1";
                ret.Success = false;
                ret.Message = "The annotation has neither a text nor a tag";
                return ret;
            }
            if (string.IsNullOrWhiteSpace(annotation.targetType))
            {
                ret.ReturnedId = "-1";
                ret.Success = false;
                ret.Message = "The annotation does not say what it annotates (targetType)";
                return ret;
            }
            try
            {
                if (string.IsNullOrWhiteSpace(annotation.id))
                {
                    annotation.id = Guid.NewGuid().ToString();
                }
                var tags = annotation.Tags ?? [];
                annotation.Tags = [];
                foreach (var tag in tags)
                {
                    annotation.AddTag(tag);
                }
                annotation.targetType = annotation.targetType.Trim();
                // An update without a date keeps the date the annotation was written.
                if (annotation.date == default)
                {
                    annotation.date = AnnotationGet(annotation.id)?.date ?? DateTime.UtcNow;
                }
                ret.ReturnedId = ElasticSearchServer.SaveSearchable(annotation);
                ret.Message = $"{ret.ReturnedId} annotation saved";
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Loads an annotation by id, deleted or not (null when it does not exist).</summary>
        public Annotation AnnotationGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            return (Annotation)ElasticSearchServer.Load<Annotation>(id);
        }

        /// <summary>
        /// Fuzzy search of the annotations (<see cref="AnnotationSearchFields"/>: text, tags, the object annotated, the
        /// excerpt; <c>and</c> / <c>or</c> and parentheses combine terms; <c>*</c> or empty: the newest ones); deleted
        /// annotations are left out.
        /// </summary>
        public List<Annotation> AnnotationsSearch(string term, int size)
        {
            return ElasticSearchServer.Search<Annotation>(term, AnnotationSearchFields, size, nameof(Annotation.isDeleted)).ToList();
        }

        /// <summary>The <paramref title="size"/> most recent annotations, without the deleted ones.</summary>
        public List<Annotation> AnnotationsLast(int size)
        {
            return ElasticSearchServer.Last<Annotation>(size, nameof(Annotation.isDeleted)).ToList();
        }

        /// <summary>Soft delete: the annotation is kept but marked <c>isDeleted = true</c> (left out of the searches at once).</summary>
        public ServiceReturn AnnotationDelete(string id)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchServer == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                var annotation = AnnotationGet(id);
                if (annotation == null)
                {
                    ret.ReturnedId = "-1";
                    ret.Success = false;
                    ret.Message = "Annotation " + id + " not found";
                    return ret;
                }
                annotation.isDeleted = true;
                ret.ReturnedId = ElasticSearchServer.SaveSearchable(annotation);
                ret.Message = id + " annotation marked as deleted (isDeleted = true)";
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>Fields of <see cref="Completion"/> searched by <see cref="CompletionsSearch"/>.</summary>
        private static readonly string[] CompletionSearchFields = { "prompt", "completion" };

        /// <summary>Loads a completion by id from the dialog repository (null when it does not exist).</summary>
        public Completion CompletionGet(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            return (Completion)ElasticSearchDialog.Load<Completion>(id);
        }

        /// <summary>Searches completions on their <c>prompt</c> and <c>completion</c> (<c>*</c> or empty: the newest ones).</summary>
        public List<Completion> CompletionsSearch(string term, int size)
        {
            return ElasticSearchDialog.Search<Completion>(term, CompletionSearchFields, size, wordPrefixes: true).ToList();
        }

        /// <summary>
        /// The advanced search of the completions (<see cref="CompletionQuery"/>): the text in the prompt, the completion
        /// or both, the period, the models, languages, topics and collections, the order; the deleted ones left out.
        /// </summary>
        public List<Completion> CompletionsQuery(CompletionQuery query)
        {
            var fields = query.searchIn?.ToLowerInvariant() switch
            {
                "prompt" => new[] { "prompt" },
                "completion" => new[] { "completion" },
                _ => CompletionSearchFields
            };
            var exact = new Dictionary<string, IReadOnlyList<string>>
            {
                [nameof(Completion.model)] = query.models ?? new List<string>(),
                [nameof(Completion.language)] = query.languages ?? new List<string>(),
                [nameof(Completion.topic)] = query.topics ?? new List<string>(),
                [nameof(Completion.collectionName)] = query.collections ?? new List<string>()
            };
            var sort = query.sort?.ToLowerInvariant();
            return ElasticSearchDialog.SearchFiltered<Completion>(query.term, fields, query.from, query.to, exact,
                query.size > 0 ? query.size : 200, oldestFirst: sort == "oldest", byRelevance: sort == "relevance",
                excludeFlag: nameof(Completion.isDeleted)).ToList();
        }

        /// <summary>The <paramref title="size"/> most recent completions, newest first.</summary>
        public List<Completion> CompletionsLast(int size)
        {
            return ElasticSearchDialog.Last<Completion>(size).ToList();
        }

        /// <summary>Deletes a completion from the dialog repository.</summary>
        public ServiceReturn CompletionDelete(string id)
        {
            ServiceReturn ret = new ServiceReturn();
            if (_config == null || ElasticSearchDialog == null)
            {
                throw new Exception("Config not done");
            }
            try
            {
                var deleted = ElasticSearchDialog.Delete<Completion>(id); // marked deleted (isDeleted = true), never erased
                ret.ReturnedId = deleted ? id : "-1";
                ret.Success = deleted;
                ret.Message = deleted ? id + " completion marked as deleted (isDeleted = true)" : "Completion " + id + " not found";
            }
            catch (Exception ex)
            {
                ret.Message = "Error : " + ex.Message;
                Console.WriteLine(ex.ToString());
                ret.ReturnedId = "-1";
                ret.Success = false;
            }
            return ret;
        }

        /// <summary>State of the Fooocus API that generates the images (<see cref="Config.fooocusUrl"/>): its version, its default model, whether it is busy; throws when it does not answer.</summary>
        public async Task<System.Text.Json.JsonElement> ImageStatusAsync() =>
            await HelperFooocusClient.GetHealthAsync(_config.fooocusUrl);

        /// <summary>What an image generation accepts (styles, performances, aspect ratios, models) and the defaults of Fooocus.</summary>
        public async Task<System.Text.Json.JsonElement> ImageOptionsAsync() =>
            await HelperFooocusClient.GetOptionsAsync(_config.fooocusUrl);

        /// <summary>Generates the images of <paramref title="request"/> with Fooocus, and logs it (prompt, seed, time, or the error).</summary>
        public async Task<ImageGenerationResult> ImageGenerateAsync(ImageGenerationRequest request)
        {
            var result = await HelperFooocusClient.GenerateAsync(_config.fooocusUrl, request);
            var prompt = request.prompt.Length > 120 ? request.prompt[..120] + "…" : request.prompt;
            if (result.error is null)
                LogSave($"Image generated ({result.images.Count} image(s), seed {result.seed}, {result.seconds} s): {prompt}", _config.appName, "INFO");
            else
                LogSave($"Image generation failed: {result.error}", _config.appName, "ERROR");
            return result;
        }
    }
}
