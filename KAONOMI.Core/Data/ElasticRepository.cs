

namespace KAONOMI.Core.Data
{
    using Models;
    using Nest;

    /// <summary>
    /// Nest implementation of Elasticsearch CRUD operations for Kaonomi entities.
    /// </summary>
    public class ElasticRepository : iElasticRepository<Log>
    {
        protected ElasticServerSettings _ESconfig;
        /// <summary>Creates a client configured from the server settings.</summary>
        public ElasticRepository(ElasticServerSettings ESconfig)
        {
            _ESconfig = ESconfig;
        }

        /// <summary>Saves multiple entities and returns their identifiers.</summary>
        public IEnumerable<string> Save<TEntity>(params TEntity[] entities)
            where TEntity : EntityBase
        {
            foreach (var entity in entities)
                yield return SaveImpl(entity);
        }

        /// <summary>Saves an enumeration of entities and returns their identifiers.</summary>
        public IEnumerable<string> Save<TEntity>(IEnumerable<TEntity> entities)
            where TEntity : EntityBase
        {
            foreach (var entity in entities)
                yield return SaveImpl(entity);
        }

        /// <summary>Indexes an entity and returns the document id.</summary>
        public string Save<TEntity>(TEntity entity)
            where TEntity : EntityBase
        {
            return SaveImpl(entity);
        }

        /// <summary>
        /// Indexes an entity and returns the document id once it can be searched (<c>refresh=wait_for</c>, up to about
        /// a second): a search right after the save finds it (e.g. the relation graph searched again after a save).
        /// </summary>
        public string SaveSearchable<TEntity>(TEntity entity)
            where TEntity : EntityBase
        {
            return SaveImpl(entity, waitForSearch: true);
        }

        /// <summary>Loads an entity by identifier.</summary>
        public Object Load<TEntity>(string id)
            where TEntity : EntityBase
        {
            return LoadImpl<TEntity>(id);
        }

        /// <summary>Limited fuzzy search on the <c>comment</c> field (10 results).</summary>
        public IReadOnlyCollection<TEntity> Search<TEntity>(string freeText)
            where TEntity : EntityBase
        {
            return SearchImpl<TEntity>(freeText);
        }


        /// <summary>Fuzzy search on a named field (default 10 results).</summary>
        public IReadOnlyCollection<TEntity> Search<TEntity>(string freeText, string fieldName)
            where TEntity : EntityBase
        {
            return SearchImplField<TEntity>(freeText, fieldName);
        }

        /// <summary>Fuzzy search on a named field with a custom maximum result count.</summary>
        public IReadOnlyCollection<TEntity> Search<TEntity>(string freeText, string fieldName, int size)
            where TEntity : EntityBase
        {
            return SearchImplField<TEntity>(freeText, fieldName, size);
        }



        /// <summary>
        /// Fuzzy / wildcard search on several fields at once (a hit on any field matches), <paramref title="size"/>
        /// results at most; an empty term or <c>*</c> matches every document. With <paramref title="excludeFlag"/> (e.g.
        /// <c>isDeleted</c>), the documents where that boolean field is true are left out. With
        /// <paramref title="wordPrefixes"/>, each word may also be the start of a word, accents ignored, or hold a typo
        /// (see <see cref="WordQuery{TEntity}"/>).
        /// </summary>
        public IReadOnlyCollection<TEntity> Search<TEntity>(string freeText, IReadOnlyList<string> fieldNames, int size, string? excludeFlag = null,
            bool wordPrefixes = false)
            where TEntity : EntityBase
        {
            return SearchImplFields<TEntity>(freeText, fieldNames, size, excludeFlag, wordPrefixes);
        }

        /// <summary>
        /// Loose search, for a search bar whose plain search found nothing: a document matches when
        /// <paramref title="fieldName"/> holds at least one word of <paramref title="freeText"/> with a typo (fuzzy,
        /// <c>AUTO</c>) or as the start of one of its words, accents ignored (see <see cref="WordQuery{TEntity}"/>).
        /// The best matches (the most words found) come first, <paramref title="size"/> results at most.
        /// </summary>
        public IReadOnlyCollection<TEntity> SearchFuzzy<TEntity>(string freeText, string fieldName, int size)
            where TEntity : EntityBase
        {
            return SearchImplFuzzy<TEntity>(freeText, fieldName, size);
        }

        /// <summary>
        /// A loose search of <paramref title="freeText"/> in the index named <paramref title="index"/> (its prefix added),
        /// whatever its documents are: every text field of its mapping is searched, a word with a typo (fuzzy,
        /// <c>AUTO</c>) or as the start of a word, accents ignored, is enough; the documents holding every word come
        /// first. Each hit gives the title, the date and the application of the document when it has them, a piece
        /// of its text around the first word found, and the document as JSON. An index that does not exist gives
        /// nothing. A document marked deleted is never given; an entry of the journal only when the user wrote it in
        /// the chat and it is not encrypted (<see cref="Searchable"/>).
        /// </summary>
        public IReadOnlyCollection<SearchHit> SearchIndex(string index, string freeText, int size)
        {
            var words = (freeText ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(w => w != "*")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (words.Length == 0)
                return Array.Empty<SearchHit>();
            var name = _ESconfig.prefix + index.Trim().ToLowerInvariant();
            var client = CreateClient(name);
            var fields = TextFieldsOf(client, name);
            if (fields is null || fields.Count == 0)
                return Array.Empty<SearchHit>();

            // Every word in one of the fields (boosted: the best matches), or at least one word.
            var response = client.Search<Dictionary<string, object>>(s => s
                .Index(name)
                .From(0)
                .Size(Math.Clamp(size * 2, 1, MaxResults))
                .Query(q => q.Bool(b => b
                    .Should(
                        new QueryContainerDescriptor<Dictionary<string, object>>().Bool(all => all
                            .Must(words.Select(w => WordQuery<Dictionary<string, object>>(w, fields)).ToArray())
                            .Boost(2)),
                        new QueryContainerDescriptor<Dictionary<string, object>>().Bool(any => any
                            .Should(words.Select(w => WordQuery<Dictionary<string, object>>(w, fields)).ToArray())
                            .MinimumShouldMatch(1)))
                    .MinimumShouldMatch(1))));
            if (!response.IsValid)
            {
                if (response.ApiCall?.HttpStatusCode == 404)
                    return Array.Empty<SearchHit>();
                throw new InvalidOperationException($"Elasticsearch could not search {name}: {Describe(response)}");
            }
            return response.Hits.Where(hit => Searchable(index, hit.Source)).Take(Math.Max(1, size))
                .Select(hit => ToSearchHit(index, hit.Id, hit.Score ?? 0, hit.Source, fields, words)).ToList();
        }

        /// <summary>Start of the text of an encrypted entry of the journal (the chat encrypts it, AES-256-GCM), and of the first version.</summary>
        private static readonly string[] EncryptedMarks = { "**CRYPTED**", "KAONOMI-AES256-GCM:v1:" };

        /// <summary>
        /// Whether a document found may be given by the searches (<c>POST /search</c>, <c>POST /recent</c>): a document
        /// marked deleted never is; an entry of the journal (<c>diary</c>) only when the user wrote it in the chat
        /// (its application is the chat, not an agent) and it is not encrypted.
        /// </summary>
        private static bool Searchable(string index, Dictionary<string, object>? source)
        {
            if (source is null)
                return true;
            if (source.TryGetValue("isDeleted", out var deleted) && deleted is bool and true)
                return false;
            if (!string.Equals(index.Trim(), "diary", StringComparison.OrdinalIgnoreCase))
                return true;
            var appName = source.TryGetValue("appName", out var app) ? app as string : null;
            if (appName is null || !appName.StartsWith("KAONOMI.Chat", StringComparison.OrdinalIgnoreCase))
                return false;
            var text = source.TryGetValue("text", out var value) ? value as string : null;
            return text is null || !EncryptedMarks.Any(mark => text.StartsWith(mark, StringComparison.Ordinal));
        }

        /// <summary>
        /// The documents of the index named <paramref title="index"/> dated since <paramref title="since"/>, the newest
        /// first, <paramref title="size"/> at most, each one as a <see cref="SearchHit"/> (its title, its text, its
        /// source). An index that does not exist gives nothing.
        /// </summary>
        public IReadOnlyCollection<SearchHit> RecentIndex(string index, DateTime since, int size)
        {
            var name = _ESconfig.prefix + index.Trim().ToLowerInvariant();
            var client = CreateClient(name);
            var fields = TextFieldsOf(client, name);
            if (fields is null)
                return Array.Empty<SearchHit>();
            var response = client.Search<Dictionary<string, object>>(s => s
                .Index(name)
                .From(0)
                .Size(Math.Clamp(size * 2, 1, MaxResults))
                .Sort(so => so.Field(f => f.Field("date").Order(SortOrder.Descending).UnmappedType(FieldType.Date)))
                .Query(q => q.DateRange(r => r.Field("date").GreaterThanOrEquals(since))));
            if (!response.IsValid)
            {
                if (response.ApiCall?.HttpStatusCode == 404)
                    return Array.Empty<SearchHit>();
                throw new InvalidOperationException($"Elasticsearch could not read the recent documents of {name}: {Describe(response)}");
            }
            return response.Hits.Where(hit => Searchable(index, hit.Source)).Take(Math.Max(1, size))
                .Select(hit => ToSearchHit(index, hit.Id, hit.Score ?? 0, hit.Source, fields, Array.Empty<string>())).ToList();
        }

        /// <summary>The text fields of the index <paramref title="name"/> from its mapping, or null when the index does not exist.</summary>
        /// <summary>The text fields of each index read lately (a mapping changes seldom: read again after <see cref="TextFieldsTtl"/>).</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Read, List<string>? Fields)> TextFieldsCache = new();
        private static readonly TimeSpan TextFieldsTtl = TimeSpan.FromSeconds(60);

        /// <summary>
        /// The text fields of the mapping of the index <paramref title="name"/> (null when the index does not exist), kept
        /// for a minute: the searches of the same index do not read its mapping again each time.
        /// </summary>
        private static List<string>? TextFieldsOf(ElasticClient client, string name)
        {
            var key = $"{client.ConnectionSettings.ConnectionPool.Nodes.FirstOrDefault()?.Uri}|{name}";
            if (TextFieldsCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.Read < TextFieldsTtl && cached.Fields is not null)
                return cached.Fields;
            var fields = ReadTextFields(client, name);
            TextFieldsCache[key] = (DateTime.UtcNow, fields);
            return fields;
        }

        private static List<string>? ReadTextFields(ElasticClient client, string name)
        {
            var mapping = client.Indices.GetMapping<Dictionary<string, object>>(m => m.Index(name));
            if (!mapping.IsValid)
            {
                if (mapping.ApiCall?.HttpStatusCode == 404)
                    return null;
                throw new InvalidOperationException($"Elasticsearch could not read the mapping of {name}: {Describe(mapping)}");
            }
            var fields = new List<string>();
            foreach (var indexMapping in mapping.Indices.Values)
                CollectTextFields(indexMapping.Mappings?.Properties, null, fields);
            return fields;
        }

        /// <summary>The names (dotted) of the text fields of <paramref title="properties"/>, the objects walked.</summary>
        private static void CollectTextFields(IProperties? properties, string? prefix, List<string> fields)
        {
            if (properties is null)
                return;
            foreach (var pair in properties)
            {
                var fieldName = prefix is null ? pair.Key.Name : prefix + "." + pair.Key.Name;
                switch (pair.Value)
                {
                    case ITextProperty:
                        fields.Add(fieldName);
                        break;
                    case IObjectProperty objectProperty:
                        CollectTextFields(objectProperty.Properties, fieldName, fields);
                        break;
                }
            }
        }

        private static readonly string[] TitleFields = ["title", "ideaTitle", "name", "prompt", "request", "promptUserText", "question", "subject", "label", "text", "fileName", "entity", "description", "comment"];

        /// <summary>A hit of <see cref="SearchIndex"/>: its title, date and application, a piece of its text, its source.</summary>
        private static SearchHit ToSearchHit(string index, string id, double score, Dictionary<string, object>? source, IReadOnlyList<string> fields, string[] words)
        {
            source ??= new Dictionary<string, object>();
            string? Text(string field)
            {
                object? value = source;
                foreach (var part in field.Split('.'))
                {
                    if (value is Dictionary<string, object> dictionary && dictionary.TryGetValue(part, out var next))
                        value = next;
                    else
                        return null;
                }
                // Only a text: an object or a list (the request of a dialog…) is not a title.
                return value switch
                {
                    string text => text,
                    null or Dictionary<string, object> or System.Collections.IList => null,
                    _ => value.ToString()
                };
            }
            var title = TitleFields.Select(Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? string.Empty;
            var all = string.Join(" · ", fields.Select(Text).Where(t => !string.IsNullOrWhiteSpace(t))).Replace('\n', ' ').Replace('\r', ' ');
            var snippet = all;
            foreach (var word in words)
            {
                var at = all.IndexOf(word, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                    continue;
                var start = Math.Max(0, at - 70);
                snippet = (start > 0 ? "…" : string.Empty) + all.Substring(start, Math.Min(200, all.Length - start)) + (start + 200 < all.Length ? "…" : string.Empty);
                break;
            }
            if (snippet.Length > 220)
                snippet = snippet[..220] + "…";
            if (words.Length == 0 && snippet.Length > 0)
                snippet = all.Length > 220 ? all[..220] + "…" : all;
            DateTime? date = DateTime.TryParse(Text("date"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;
            return new SearchHit
            {
                index = index,
                id = id,
                score = score,
                date = date,
                appName = Text("appName") ?? string.Empty,
                title = title.Length > 160 ? title[..160] + "…" : title,
                snippet = snippet,
                text = all.Length > 4000 ? all[..4000] : all,
                source = System.Text.Json.JsonSerializer.Serialize(source)
            };
        }

        /// <summary>
        /// Deletes a document by id. Returns false when the document does not exist; a failed call is retried, then
        /// reported as an exception.
        /// </summary>
        public bool Delete<TEntity>(string id)
            where TEntity : EntityBase
        {
            // A document that is never erased: marked deleted instead, whoever asks (false when it does not exist).
            if (typeof(ISoftDeletable).IsAssignableFrom(typeof(TEntity)))
            {
                if (Load<TEntity>(id) is not TEntity entity)
                    return false;
                ((ISoftDeletable)entity).isDeleted = true;
                Save(entity);
                return true;
            }
            var client = CreateClient<TEntity>();
            var index = IndexName<TEntity>();
            return (bool)WithRetries($"delete {typeof(TEntity).Name} '{id}'", () =>
            {
                var response = client.Delete<TEntity>(id, d => d.Index(index));
                if (response.IsValid)
                    return (true, (Object)true);
                if (response.ApiCall?.HttpStatusCode == 404 && response.OriginalException is null)
                    return (true, (Object)false); // not found
                return (false, (Object)Describe(response));
            });
        }

        /// <summary>Largest number of documents returned by one search (Elasticsearch default max_result_window).</summary>
        public const int MaxResults = 10000;

        /// <summary>The <paramref title="size"/> most recent logs, newest first (<see cref="iElasticRepository{TEntity}.Last"/>).</summary>
        public IReadOnlyCollection<Log> Last(int size)
        {
            return LastImpl<Log>(size);
        }

        /// <summary>
        /// Erases every document of <typeparamref title="TEntity"/>: its index is deleted (it is created again, empty, at
        /// the next save). Returns the number of documents it held (0 when it did not exist).
        /// </summary>
        public long DeleteAll<TEntity>()
            where TEntity : EntityBase
        {
            var name = IndexName<TEntity>();
            var client = CreateClient(name);
            var count = client.Count<TEntity>(c => c.Index(name));
            var response = client.Indices.Delete(name);
            if (!response.IsValid && response.ApiCall?.HttpStatusCode != 404)
                throw new InvalidOperationException($"Elasticsearch could not delete the index {name}: {Describe(response)}");
            return count.IsValid ? count.Count : 0;
        }

        /// <summary>The <paramref title="size"/> most recent documents of any entity type, newest first (at most <see cref="MaxResults"/>).</summary>
        public IReadOnlyCollection<TEntity> Last<TEntity>(int size)
            where TEntity : EntityBase
        {
            return LastImpl<TEntity>(size);
        }

        /// <summary>
        /// The <paramref title="size"/> most recent documents, newest first, without the ones where the boolean field
        /// <paramref title="excludeFlag"/> (e.g. <c>isDeleted</c>) is true.
        /// </summary>
        public IReadOnlyCollection<TEntity> Last<TEntity>(int size, string excludeFlag)
            where TEntity : EntityBase
        {
            return LastImpl<TEntity>(size, excludeFlag);
        }

        /// <summary>
        /// The documents dated from <paramref title="from"/> (included), newest first, at most <paramref title="size"/>
        /// (capped at <see cref="MaxResults"/>).
        /// </summary>
        public IReadOnlyCollection<TEntity> Since<TEntity>(DateTimeOffset from, int size = MaxResults)
            where TEntity : EntityBase
        {
            return SinceImpl<TEntity>(from, size);
        }

        /// <summary>Safety limit of <see cref="SinceAll{TEntity}"/> (number of documents read in one call).</summary>
        public const int SinceAllLimit = 1_000_000;

        /// <summary>
        /// All the documents dated from <paramref title="from"/> (included), newest first, without the 10,000 limit of
        /// <see cref="Since{TEntity}"/>: they are read page after page (<c>search_after</c>), up to <see cref="SinceAllLimit"/>.
        /// </summary>
        public IReadOnlyCollection<TEntity> SinceAll<TEntity>(DateTimeOffset from)
            where TEntity : EntityBase
        {
            return SinceAllImpl<TEntity>(from, SinceAllLimit);
        }

        /// <summary>Saves a <see cref="Log"/> (explicit log contract implementation).</summary>
        public string Save(Log entity)
        {
            return SaveImpl(entity);
        }

        /// <summary>Deletes a document by id in the index for type <typeparamref title="TEntity"/>.</summary>
        public bool DeleteImpl<TEntity>(string id) where TEntity : EntityBase
        {
            bool res;
            try
            {
                var uri = new Uri(_ESconfig.url);
                bool hasPassword = false;
                // check as a password 
                if (_ESconfig.password != null && _ESconfig.user != null)
                {
                    if (_ESconfig.password.Length > 0 && _ESconfig.user.Length > 0)
                    {
                        hasPassword = true;
                    }
                }
                var settings = new ConnectionSettings();
                if (hasPassword)
                {
                    settings = new ConnectionSettings(uri).BasicAuthentication(_ESconfig.user, _ESconfig.password)
                    .DefaultIndex(_ESconfig.prefix + typeof(TEntity).Name.ToLower());
                }
                else
                {
                    settings = new ConnectionSettings(new Uri(_ESconfig.url)).DefaultIndex(_ESconfig.prefix + typeof(TEntity).Name.ToLower());
                }
                var client = new ElasticClient(settings);

                var response = client.Delete<TEntity>(id);
                res = true;

            }
            catch (Exception)
            {
                throw;
            }
            return res;
        }

        /// <summary>
        /// Loads a document by id via <see cref="ElasticClient.Get{TDocument}"/>. Returns null when the document does not
        /// exist; a failed call (connection, timeout, server error) is retried, then reported as an exception instead of
        /// a silent null.
        /// </summary>
        private Object LoadImpl<TEntity>(string id) where TEntity : EntityBase
        {
            var client = CreateClient<TEntity>();
            var index = IndexName<TEntity>();
            return WithRetries($"load {typeof(TEntity).Name} '{id}'", () =>
            {
                var response = client.Get<TEntity>(id, g => g.Index(index));
                if (response.IsValid)
                    return (true, (Object)Migrated(response.Source));
                if (response.ApiCall?.HttpStatusCode == 404 && response.OriginalException is null)
                    return (true, (Object)null!); // not found
                return (false, (Object)Describe(response));
            });
        }

        private IReadOnlyCollection<TEntity> SearchImplField<TEntity>(string freeText, string fieldName, int maxResults = 10)
            where TEntity : EntityBase
        {
            var client = CreateClient<TEntity>();
            var field = ToCamelCase(fieldName);
            var keywordField = field + ".keyword";
            var term = freeText?.Trim() ?? string.Empty;
            var isMatchAll = string.IsNullOrEmpty(term) || term == "*";
            var wildcard = $"*{EscapeWildcard(term)}*";

            var searchResponse = client.Search<TEntity>(s => s
                .From(0)
                .Size(maxResults)
                .Query(q => isMatchAll
                    ? q.MatchAll()
                    : q.Bool(b => b
                        .Should(
                            sh => sh.Match(m => m
                                .Field(field)
                                .Query(term)
                                .Operator(Operator.And)
                                .Fuzziness(Fuzziness.Auto)),
                            sh => sh.Wildcard(w => w
                                .Field(field)
                                .Value(wildcard)
                                .CaseInsensitive(true)),
                            sh => sh.Wildcard(w => w
                                .Field(keywordField)
                                .Value(wildcard)
                                .CaseInsensitive(true)))
                        .MinimumShouldMatch(1)))
            );

            return Migrated(searchResponse.Documents);
        }

        /// <summary>
        /// Same query as <see cref="SearchImplField{TEntity}"/> on every field of <paramref title="fieldNames"/>, newest
        /// first when the term matches all. An empty or missing index gives an empty list.
        /// </summary>
        private IReadOnlyCollection<TEntity> SearchImplFields<TEntity>(string freeText, IReadOnlyList<string> fieldNames, int maxResults,
            string? excludeFlag = null, bool wordPrefixes = false)
            where TEntity : EntityBase
        {
            var client = CreateClient<TEntity>();
            var term = freeText?.Trim() ?? string.Empty;
            var isMatchAll = string.IsNullOrEmpty(term) || term == "*";

            // "a and (b or c)": each term is searched as a plain search, the terms combined by the operators.
            var expression = BooleanSearch.Parse(term);
            isMatchAll |= expression is null;

            var response = client.Search<TEntity>(s =>
            {
                s = s.Index(IndexName<TEntity>())
                    .From(0)
                    .Size(Math.Clamp(maxResults, 1, MaxResults));
                return isMatchAll
                    ? s.Query(q => ExcludeFlag(q, excludeFlag, b => b.Must(m => m.MatchAll())))
                        .Sort(so => so.Field(f => f.Field("date").Order(SortOrder.Descending).Missing("_last").UnmappedType(FieldType.Date)))
                    : s.Query(q => ExcludeFlag(q, excludeFlag, b => b.Must(BooleanQuery<TEntity>(expression!, fieldNames, wordPrefixes))));
            });

            if (response.IsValid)
                return Migrated(response.Documents);
            if (response.ApiCall?.HttpStatusCode == 404)
                return Array.Empty<TEntity>(); // no document saved yet
            throw new InvalidOperationException($"Elasticsearch could not search the {typeof(TEntity).Name} documents: {Describe(response)}");
        }

        /// <summary>
        /// The query of <see cref="SearchFuzzy{TEntity}"/>: a bool should of one <see cref="WordQuery{TEntity}"/> per
        /// word of the term (at least one must match), so that the documents holding the most words score first. An
        /// empty term, or <c>*</c>, gives an empty list (the plain search is the one that matches all).
        /// </summary>
        private IReadOnlyCollection<TEntity> SearchImplFuzzy<TEntity>(string freeText, string fieldName, int maxResults)
            where TEntity : EntityBase
        {
            var words = (freeText ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(w => w != "*")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (words.Length == 0)
                return Array.Empty<TEntity>();

            var client = CreateClient<TEntity>();
            var fieldNames = new[] { fieldName };
            var response = client.Search<TEntity>(s => s
                .Index(IndexName<TEntity>())
                .From(0)
                .Size(Math.Clamp(maxResults, 1, MaxResults))
                .Query(q => q.Bool(b => b
                    .Should(words.Select(w => WordQuery<TEntity>(w, fieldNames)).ToArray())
                    .MinimumShouldMatch(1))));

            if (response.IsValid)
                return Migrated(response.Documents);
            if (response.ApiCall?.HttpStatusCode == 404)
                return Array.Empty<TEntity>(); // no document saved yet
            throw new InvalidOperationException($"Elasticsearch could not search the {typeof(TEntity).Name} documents (fuzzy): {Describe(response)}");
        }

        /// <summary>
        /// The query of a search expression: a term matches when one of <paramref title="fieldNames"/> holds all its
        /// words (fuzzy) or holds it as a part of its text; <c>and</c> becomes a bool must, <c>or</c> a bool should.
        /// With <paramref title="wordPrefixes"/>, the term also matches when each of its words matches
        /// <see cref="WordQuery{TEntity}"/> in one of the fields.
        /// </summary>
        private static QueryContainer BooleanQuery<TEntity>(BooleanSearch expression, IReadOnlyList<string> fieldNames, bool wordPrefixes = false)
            where TEntity : class
        {
            switch (expression)
            {
                case BooleanSearch.And and:
                    return new QueryContainerDescriptor<TEntity>().Bool(b => b.Must(and.Parts.Select(p => BooleanQuery<TEntity>(p, fieldNames, wordPrefixes)).ToArray()));
                case BooleanSearch.Or or:
                    return new QueryContainerDescriptor<TEntity>().Bool(b => b.Should(or.Parts.Select(p => BooleanQuery<TEntity>(p, fieldNames, wordPrefixes)).ToArray()).MinimumShouldMatch(1));
                case BooleanSearch.Term { Text: var term }:
                    var wildcard = $"*{EscapeWildcard(term)}*";
                    var clauses = new List<Func<QueryContainerDescriptor<TEntity>, QueryContainer>>();
                    foreach (var field in fieldNames.Select(ToCamelCase))
                    {
                        clauses.Add(sh => sh.Match(m => m.Field(field).Query(term).Operator(Operator.And).Fuzziness(Fuzziness.Auto)));
                        clauses.Add(sh => sh.Wildcard(w => w.Field(field).Value(wildcard).CaseInsensitive(true)));
                        clauses.Add(sh => sh.Wildcard(w => w.Field(field + ".keyword").Value(wildcard).CaseInsensitive(true)));
                    }
                    var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (wordPrefixes && words.Length > 0)
                        clauses.Add(sh => sh.Bool(b => b.Must(words.Select(w => WordQuery<TEntity>(w, fieldNames)).ToArray())));
                    return new QueryContainerDescriptor<TEntity>().Bool(b => b.Should(clauses.ToArray()).MinimumShouldMatch(1));
                default:
                    throw new ArgumentException($"Unknown search expression {expression}", nameof(expression));
            }
        }

        /// <summary>Runs a fuzzy query on <c>comment</c> (size 10).</summary>
        /// <summary>
        /// All documents of the index sorted by <c>date</c> (newest first; documents without a date come last),
        /// <paramref title="size"/> of them, between 1 and <see cref="MaxResults"/>. An empty or missing index gives an
        /// empty list; any other failure is reported as an exception.
        /// </summary>
        private IReadOnlyCollection<TEntity> LastImpl<TEntity>(int size, string? excludeFlag = null) where TEntity : EntityBase
        {
            var client = CreateClient<TEntity>();
            var count = Math.Clamp(size, 1, MaxResults);
            var response = client.Search<TEntity>(s => s
                .Index(IndexName<TEntity>())
                .From(0)
                .Size(count)
                .Query(q => ExcludeFlag(q, excludeFlag, b => b.Must(m => m.MatchAll())))
                .Sort(so => so.Field(f => f.Field("date").Order(SortOrder.Descending).Missing("_last").UnmappedType(FieldType.Date))));

            // Elasticsearch sorts dates to the millisecond: the final sort keeps the full precision of the dates.
            if (response.IsValid)
                return Migrated(response.Documents).OrderByDescending(d => d.date).ToList();
            if (response.ApiCall?.HttpStatusCode == 404)
                return Array.Empty<TEntity>(); // no document saved yet
            throw new InvalidOperationException($"Elasticsearch could not read the last {typeof(TEntity).Name} documents: {Describe(response)}");
        }

        /// <summary>
        /// An advanced search: the documents matching <paramref title="freeText"/> in <paramref title="fieldNames"/> (as
        /// <see cref="Search{TEntity}(string, IReadOnlyList{string}, int, string?, bool)"/>: and, or, ( ), "exact words";
        /// empty or <c>*</c>: all), dated between <paramref title="from"/> and <paramref title="to"/> (both included, each
        /// optional), whose text fields of <paramref title="exact"/> hold one of the values given (their keyword: a filter
        /// per field, the values of a field joined by or), without the ones where <paramref title="excludeFlag"/> is true;
        /// sorted by date (newest first, or oldest first with <paramref title="oldestFirst"/>) or, with
        /// <paramref title="byRelevance"/> and a text, by relevance; <paramref title="size"/> of them at most.
        /// </summary>
        public IReadOnlyCollection<TEntity> SearchFiltered<TEntity>(string? freeText, IReadOnlyList<string> fieldNames, DateTime? from, DateTime? to,
            IReadOnlyDictionary<string, IReadOnlyList<string>> exact, int size, bool oldestFirst = false, bool byRelevance = false,
            string? excludeFlag = null) where TEntity : EntityBase
        {
            var client = CreateClient<TEntity>();
            var term = freeText?.Trim() ?? string.Empty;
            var expression = term.Length == 0 || term == "*" ? null : BooleanSearch.Parse(term);
            var filters = new List<Func<QueryContainerDescriptor<TEntity>, QueryContainer>>();
            if (from is not null || to is not null)
                filters.Add(f => f.DateRange(r =>
                {
                    r = r.Field("date");
                    if (from is { } start)
                        r = r.GreaterThanOrEquals(start.ToUniversalTime());
                    if (to is { } end)
                        r = r.LessThanOrEquals(end.ToUniversalTime());
                    return r;
                }));
            foreach (var (field, values) in exact)
            {
                var kept = values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToArray();
                if (kept.Length > 0)
                    filters.Add(f => f.Terms(t => t.Field(ToCamelCase(field) + ".keyword").Terms(kept)));
            }

            var response = client.Search<TEntity>(s =>
            {
                s = s.Index(IndexName<TEntity>())
                    .From(0)
                    .Size(Math.Clamp(size, 1, MaxResults))
                    .Query(q => ExcludeFlag(q, excludeFlag, b =>
                    {
                        b = expression is null ? b.Must(m => m.MatchAll()) : b.Must(BooleanQuery<TEntity>(expression, fieldNames, wordPrefixes: true));
                        return filters.Count == 0 ? b : b.Filter(filters.ToArray());
                    }));
                return byRelevance && expression is not null
                    ? s
                    : s.Sort(so => so.Field(f => f.Field("date").Order(oldestFirst ? SortOrder.Ascending : SortOrder.Descending).Missing("_last").UnmappedType(FieldType.Date)));
            });

            if (response.IsValid)
                return byRelevance && expression is not null
                    ? Migrated(response.Documents)
                    : (oldestFirst ? Migrated(response.Documents).OrderBy(d => d.date) : Migrated(response.Documents).OrderByDescending(d => d.date)).ToList();
            if (response.ApiCall?.HttpStatusCode == 404)
                return Array.Empty<TEntity>(); // no document saved yet
            throw new InvalidOperationException($"Elasticsearch could not search the {typeof(TEntity).Name} documents: {Describe(response)}");
        }

        /// <summary>
        /// The documents whose text field <paramref title="fieldName"/> is exactly <paramref title="value"/> (its keyword),
        /// newest first, <paramref title="size"/> of them at most. An empty or missing index gives an empty list; any other
        /// failure is reported as an exception.
        /// </summary>
        public IReadOnlyCollection<TEntity> WithValue<TEntity>(string fieldName, string value, int size) where TEntity : EntityBase
        {
            var client = CreateClient<TEntity>();
            var count = Math.Clamp(size, 1, MaxResults);
            var response = client.Search<TEntity>(s => s
                .Index(IndexName<TEntity>())
                .From(0)
                .Size(count)
                .Query(q => q.Term(t => t.Field(ToCamelCase(fieldName) + ".keyword").Value(value)))
                .Sort(so => so.Field(f => f.Field("date").Order(SortOrder.Descending).Missing("_last").UnmappedType(FieldType.Date))));

            if (response.IsValid)
                return Migrated(response.Documents).OrderByDescending(d => d.date).ToList();
            if (response.ApiCall?.HttpStatusCode == 404)
                return Array.Empty<TEntity>(); // no document saved yet
            throw new InvalidOperationException($"Elasticsearch could not read the {typeof(TEntity).Name} documents of {fieldName} {value}: {Describe(response)}");
        }

        /// <summary>
        /// Documents whose <c>date</c> is at or after <paramref title="from"/>, sorted by date (newest first). An empty or
        /// missing index gives an empty list; any other failure is reported as an exception.
        /// </summary>
        private IReadOnlyCollection<TEntity> SinceImpl<TEntity>(DateTimeOffset from, int size) where TEntity : EntityBase
        {
            var client = CreateClient<TEntity>();
            var count = Math.Clamp(size, 1, MaxResults);
            var response = client.Search<TEntity>(s => s
                .Index(IndexName<TEntity>())
                .From(0)
                .Size(count)
                .Query(q => q.DateRange(r => r.Field("date").GreaterThanOrEquals(from.UtcDateTime)))
                .Sort(so => so.Field(f => f.Field("date").Order(SortOrder.Descending).UnmappedType(FieldType.Date))));

            if (response.IsValid)
                return Migrated(response.Documents).OrderByDescending(d => d.date).ToList();
            if (response.ApiCall?.HttpStatusCode == 404)
                return Array.Empty<TEntity>(); // no document saved yet
            throw new InvalidOperationException($"Elasticsearch could not read the {typeof(TEntity).Name} documents since {from:O}: {Describe(response)}");
        }

        /// <summary>
        /// Pages of <see cref="MaxResults"/> documents sorted by <c>date</c> (newest first) then <c>id</c> (stable order
        /// between pages), each page starting after the last document of the previous one (<c>search_after</c>).
        /// </summary>
        private IReadOnlyCollection<TEntity> SinceAllImpl<TEntity>(DateTimeOffset from, int limit) where TEntity : EntityBase
        {
            var client = CreateClient<TEntity>();
            var index = IndexName<TEntity>();
            var documents = new List<TEntity>();
            IReadOnlyCollection<object>? after = null;

            while (documents.Count < limit)
            {
                var response = client.Search<TEntity>(s =>
                {
                    s = s.Index(index)
                        .Size(MaxResults)
                        .Query(q => q.DateRange(r => r.Field("date").GreaterThanOrEquals(from.UtcDateTime)))
                        .Sort(so => so
                            .Field(f => f.Field("date").Order(SortOrder.Descending).UnmappedType(FieldType.Date))
                            .Field(f => f.Field("id.keyword").Order(SortOrder.Ascending).UnmappedType(FieldType.Keyword)));
                    return after is null ? s : s.SearchAfter(after.ToArray());
                });

                if (!response.IsValid)
                {
                    if (response.ApiCall?.HttpStatusCode == 404)
                        break; // no document saved yet
                    throw new InvalidOperationException($"Elasticsearch could not read the {typeof(TEntity).Name} documents since {from:O}: {Describe(response)}");
                }

                var hits = response.Hits;
                if (hits.Count == 0)
                    break;
                documents.AddRange(hits.Select(h => h.Source));
                after = hits.Last().Sorts;
                if (hits.Count < MaxResults)
                    break;
            }

            // Elasticsearch sorts dates to the millisecond: the final sort keeps the full precision of the dates.
            return documents.OrderByDescending(d => d.date).ToList();
        }

        private IReadOnlyCollection<TEntity> SearchImpl<TEntity>(string freeText) where TEntity : EntityBase
        {
            IReadOnlyCollection<TEntity> entity;
            try
            {
                var uri = new Uri(_ESconfig.url);
                bool hasPassword = false;
                // check as a password 
                if (_ESconfig.password != null && _ESconfig.user != null)
                {
                    if (_ESconfig.password.Length > 0 && _ESconfig.user.Length > 0)
                    {
                        hasPassword = true;
                    }
                }
                var settings = new ConnectionSettings();
                if (hasPassword)
                {
                    settings = new ConnectionSettings(uri).BasicAuthentication(_ESconfig.user, _ESconfig.password)
                    .DefaultIndex(_ESconfig.prefix + typeof(TEntity).Name.ToLower());
                }
                else
                {
                    settings = new ConnectionSettings(new Uri(_ESconfig.url)).DefaultIndex(_ESconfig.prefix + typeof(TEntity).Name.ToLower());
                }
                var client = new ElasticClient(settings);
                //TODO - Extend the search method
                var searchResponse3 = client.Search<TEntity>(s => s
                                                                .From(0)
                                                                .Size(10)
                                                                .Query(q => q
                                                                        .Fuzzy(c => c
                                                                            .Name("fuzzy")
                                                                            .Boost(1.1)
                                                                            .Field(p => p.comment)
                                                                            .Fuzziness(Fuzziness.Auto)
                                                                            .Value(freeText)
                                                                            .MaxExpansions(100)
                                                                              )
                                                                      )
                                                            );
                entity = Migrated(searchResponse3.Documents);
            }
            catch (Exception)
            {
                throw;
            }
            return entity;
        }
        /// <summary>
        /// The documents read, their metadata as they are kept (<see cref="TagRules.Apply"/>): the old tags moved
        /// (!ALERT to the signals, ?QUESTION and &amp;LINK to #TAGS, '_' to '-') without waiting for their next save.
        /// </summary>
        private static IReadOnlyCollection<TEntity> Migrated<TEntity>(IReadOnlyCollection<TEntity> documents) where TEntity : EntityBase
        {
            foreach (var document in documents)
                if (document is not null)
                    TagRules.Apply(document);
            return documents;
        }

        /// <summary>One document read, its metadata as they are kept (null stays null).</summary>
        private static TEntity? Migrated<TEntity>(TEntity? document) where TEntity : EntityBase
        {
            if (document is not null)
                TagRules.Apply(document);
            return document;
        }

        /// <summary>
        /// Indexes the document and returns its id. A failed call is retried, then reported as an exception (an empty id
        /// used to be returned silently, so that the next load of that id gave null).
        /// </summary>
        private string SaveImpl<TEntity>(TEntity entity, bool waitForSearch = false) where TEntity : EntityBase
        {
            // The metadata as they are kept: #TAG, @EVENT, !SIGNAL, upper case, '-' for the spaces (the old tags moved).
            TagRules.Apply(entity);
            var client = CreateClient<TEntity>();
            return (string)WithRetries($"save {typeof(TEntity).Name} '{entity.id}'", () =>
            {
                var response = waitForSearch
                    ? client.Index(entity, i => i.Refresh(Elasticsearch.Net.Refresh.WaitFor))
                    : client.IndexDocument(entity);
                return response.IsValid && !string.IsNullOrEmpty(response.Id)
                    ? (true, (Object)response.Id)
                    : (false, (Object)Describe(response));
            });
        }

        /// <summary>Re-indexes a document (same logic as insert for Nest).</summary>
        private string UpdateImpl<TEntity>(TEntity entity) where TEntity : EntityBase => SaveImpl(entity);

        /// <summary>Attempts of a load / save before the error is reported.</summary>
        private const int Attempts = 3;

        /// <summary>
        /// Runs <paramref title="call"/> up to <see cref="Attempts"/> times (short pause between attempts) while it fails;
        /// returns its value, or throws with the last error.
        /// </summary>
        private static Object WithRetries(string operation, Func<(bool Ok, Object Value)> call)
        {
            string error = "unknown error";
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                var (ok, value) = call();
                if (ok)
                    return value;
                error = value as string ?? error;
                if (attempt < Attempts)
                    Thread.Sleep(200 * attempt);
            }
            throw new InvalidOperationException($"Elasticsearch could not {operation} after {Attempts} attempts: {error}");
        }

        /// <summary>Short description of a failed Elasticsearch call.</summary>
        private static string Describe(IResponse response) =>
            response.ServerError?.Error?.Reason
            ?? response.OriginalException?.Message
            ?? $"HTTP {response.ApiCall?.HttpStatusCode?.ToString() ?? "?"}";

        /// <summary>
        /// Bool query of <paramref title="query"/>; with <paramref title="excludeFlag"/>, the documents where that boolean
        /// field is true are left out (documents without the field are kept).
        /// </summary>
        private static QueryContainer ExcludeFlag<TEntity>(QueryContainerDescriptor<TEntity> descriptor, string? excludeFlag,
            Func<BoolQueryDescriptor<TEntity>, BoolQueryDescriptor<TEntity>> query)
            where TEntity : class
        {
            return descriptor.Bool(b =>
            {
                var result = query(b);
                return excludeFlag is null ? result : result.MustNot(n => n.Term(t => t.Field(ToCamelCase(excludeFlag)).Value(true)));
            });
        }

        /// <summary>Index of an entity type: prefix + type title in lower case.</summary>
        private string IndexName<TEntity>() => _ESconfig.prefix + typeof(TEntity).Name.ToLower();

        /// <summary>Clients already created, by server, user and index: NEST clients are thread-safe and meant to be reused.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ElasticClient> Clients = new();

        private ElasticClient CreateClient<TEntity>() where TEntity : EntityBase => CreateClient(IndexName<TEntity>());

        /// <summary>The client of <paramref title="index"/> (one per index, kept).</summary>
        private ElasticClient CreateClient(string index)
        {
            var hasPassword = !string.IsNullOrEmpty(_ESconfig.user) && !string.IsNullOrEmpty(_ESconfig.password);
            var key = $"{_ESconfig.url}|{(hasPassword ? _ESconfig.user : string.Empty)}|{index}";
            return Clients.GetOrAdd(key, _ =>
            {
                var uri = new Uri(_ESconfig.url);
                var settings = hasPassword
                    ? new ConnectionSettings(uri).BasicAuthentication(_ESconfig.user, _ESconfig.password).DefaultIndex(index)
                    : new ConnectionSettings(uri).DefaultIndex(index);
                return new ElasticClient(settings);
            });
        }

        private static string ToCamelCase(string name)
        {
            if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
                return name;
            return char.ToLowerInvariant(name[0]) + name[1..];
        }

        /// <summary>Letters that also match their accented forms in <see cref="WordQuery{TEntity}"/>.</summary>
        private static readonly Dictionary<char, string> AccentClasses = new()
        {
            ['a'] = "[aàâäáã]", ['e'] = "[eéèêë]", ['i'] = "[iîïíì]", ['o'] = "[oôöóòõ]",
            ['u'] = "[uùûüú]", ['c'] = "[cç]", ['y'] = "[yÿ]", ['n'] = "[nñ]",
        };

        /// <summary>
        /// One word of a search typed in a search bar: a word of one of <paramref title="fieldNames"/> holds it with a
        /// typo (fuzzy, <c>AUTO</c>), or starts with it, accents ignored (<c>bearnai</c> finds <c>béarnaise</c>).
        /// </summary>
        private static QueryContainer WordQuery<TEntity>(string word, IReadOnlyList<string> fieldNames)
            where TEntity : class
        {
            var lower = word.ToLowerInvariant();
            var prefix = AccentFoldedPattern(lower) + ".*";
            var clauses = new List<Func<QueryContainerDescriptor<TEntity>, QueryContainer>>();
            foreach (var field in fieldNames.Select(ToCamelCase))
            {
                clauses.Add(sh => sh.Fuzzy(f => f.Field(field).Value(lower).Fuzziness(Fuzziness.Auto)));
                clauses.Add(sh => sh.Regexp(r => r.Field(field).Value(prefix)));
            }
            return new QueryContainerDescriptor<TEntity>().Bool(b => b.Should(clauses.ToArray()).MinimumShouldMatch(1));
        }

        /// <summary>Lucene regexp of <paramref title="word"/> (lower case) where each letter matches its accented forms.</summary>
        private static string AccentFoldedPattern(string word)
        {
            var bare = new System.Text.StringBuilder();
            foreach (var c in word.Normalize(System.Text.NormalizationForm.FormD))
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                    bare.Append(c);
            }
            var pattern = new System.Text.StringBuilder();
            foreach (var c in bare.ToString().Normalize(System.Text.NormalizationForm.FormC))
            {
                if (AccentClasses.TryGetValue(c, out var accented))
                    pattern.Append(accented);
                else if (char.IsLetterOrDigit(c))
                    pattern.Append(c);
                else
                    pattern.Append('\\').Append(c);
            }
            return pattern.ToString();
        }

        private static string EscapeWildcard(string value) =>
            value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("*", "\\*", StringComparison.Ordinal)
                .Replace("?", "\\?", StringComparison.Ordinal);
    }
}
