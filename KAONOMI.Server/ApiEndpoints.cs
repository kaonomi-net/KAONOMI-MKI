namespace KAONOMI.Server
{
    using KAONOMI.Core;
    using KAONOMI.Core.Data;
    using KAONOMI.Core.Models;
    using KAONOMI.Core.Models.LLM;

    using Microsoft.AspNetCore.Http;

    /// <summary>
    /// Extension methods that register message flow, cache, symbolic pattern, and utility HTTP routes.
    /// </summary>
    internal static class ApiEndpoints
    {
        /// <summary>
        /// Maps version, message-flow, cache, and symbolic-pattern CRUD/search endpoints onto the application.
        /// </summary>
        /// <param title="app">The endpoint route builder.</param>
        /// <param title="session">Session manager used for persistence operations.</param>
        /// <returns>The same <paramref title="app"/> instance for chaining.</returns>
        public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder app, SessionManager session)
        {
            // POST /ner {text, types?}: the entities of the text (XLM-RoBERTa int8: PERSON, ORG, LOC; the rules: URL, EMAIL,
            // DATE, MONEY, CODE…; the names of the ontology, with their id), each with its type, position, score and source.
            app.MapPost("/ner", (Ner.NerRequest p) => Task.FromResult(Ner.NerService.Get(session.Config).Extract(p.text, p.types)));

            // GET /ner/types: the types the NER finds (of the model, of the rules, of the ontology) and the state of the model.
            app.MapGet("/ner/types", () => Task.FromResult(Ner.NerService.Get(session.Config).Types()));

            // GET /detectedentity/top?minutes=60&size=60: the entities found by the NER in the dialogs of the last minutes
            // (all of them when minutes is 0), counted, the most frequent first.
            app.MapGet("/detectedentity/top", (int? minutes, int? size) =>
                Task.FromResult(session.DetectedEntitiesTop(minutes ?? 60, size ?? 60)));

            // GET /detectedsymbol/top?minutes=60&size=60: the symbols found by the symbolic patterns in the dialogs of the
            // last minutes (all of them when minutes is 0), counted by symbol and signals, the most frequent first.
            app.MapGet("/detectedsymbol/top", (int? minutes, int? size) =>
                Task.FromResult(session.DetectedSymbolsTop(minutes ?? 60, size ?? 60)));

            // GET /detectedtag/top?minutes=60&size=60: the tags found through the detections (patterns, entities of the
            // ontology, tags written) in the dialogs of the last minutes, counted, the most frequent first.
            app.MapGet("/detectedtag/top", (int? minutes, int? size) =>
                Task.FromResult(session.DetectedTagsTop(minutes ?? 60, size ?? 60)));

            // GET /version: returns the server version string.
            app.MapGet("/version", () =>
            {
                string res = "version 0.01.01";
                return Task.FromResult(res);
            });

            // GET /: redirects the root URL to the Swagger UI.
            app.MapGet("/", (request) =>
            {
                request.Response.Redirect("swagger");
                return Task.CompletedTask;
            });


            // GET /messageflow/{id}: loads one ticker message by its document id.
            app.MapGet("/messageflow/{id}", (string id) =>
            {
                MessageFlow result = new MessageFlow();
                try
                {
                    result = session.MessageFlowGet(id);
                }
                catch (Exception ex)
                {
                    throw ex;
                }
                return Task.FromResult(result);
            });

            // GET /tokensusage/last?minutes=N: the token usages (model, prompt and completion tokens per LLM call) of the last N minutes, newest first; N defaults to 60.
            app.MapGet("/tokensusage/last", (int? minutes) =>
            {
                try
                {
                    return Results.Ok(session.TokensUsageLast(minutes ?? 60));
                }
                catch (Exception ex)
                {
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError, title: "Token usages could not be read");
                }
            });

            // GET /log/since?minutes=N: all the logs (every application) of the last N minutes, newest first, without the 10,000 limit of /log/last; N defaults to 60.
            app.MapGet("/log/since", (int? minutes) =>
            {
                try
                {
                    return Results.Ok(session.LogsSince(minutes ?? 60));
                }
                catch (Exception ex)
                {
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError, title: "Logs could not be read");
                }
            });

            // GET /log/last?size=N: the N most recent logs (all applications), newest first; N defaults to and is capped at 10,000.
            app.MapGet("/log/last", (int? size) =>
            {
                try
                {
                    return Results.Ok(session.LogsLast(size ?? ElasticRepository.MaxResults));
                }
                catch (Exception ex)
                {
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError, title: "Logs could not be read");
                }
            });

            // DELETE /log: erases ALL the logs (the index of the logs is deleted, created again empty at the next log);
            // returns the number erased in ReturnedId, or "-1" with the error message.
            app.MapDelete("/log", () =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.LogsDeleteAll();
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            //PUT
            app.MapPut("/log", (Log p) =>
            {
                ServiceReturn result = new ServiceReturn();
                result.ReturnedId = "";
                result.Message = "";
                try
                {
                    result = session.LogSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                }
                return Task.FromResult(result);
            });

            app.MapPut("/completion", (Completion p) =>
            {
                ServiceReturn result = new ServiceReturn();
                result.ReturnedId = "";
                result.Message = "";
                try
                {
                    result = session.CompletionSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                }
                return Task.FromResult(result);
            });

            app.MapPut("/systemlog", (SystemLog p) =>
            {
                ServiceReturn result = new ServiceReturn();
                result.ReturnedId = "";
                result.Message = "";
                try
                {
                    result = session.SystemLogSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                }
                return Task.FromResult(result);
            });

            app.MapPut("/whitenote", (WhiteNote p) =>
            {
                ServiceReturn result = new ServiceReturn();
                result.ReturnedId = "";
                result.Message = "";
                try
                {
                    result = session.WhiteNoteSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                }
                return Task.FromResult(result);
            });

            // GET /whitenote/last?size=200: the last white notes, newest first: their text and their (blurred) date only,
            // never their context.
            app.MapGet("/whitenote/last", (int? size) =>
                Task.FromResult(session.WhiteNotesLast(size ?? 200).Select(n => new { n.text, n.date }).ToList()));


            // POST /messageflow/update: saves (creates or overwrites) a ticker message; returns the saved id, or "-1" with the error message.
            app.MapPost("/messageflow/update", (MessageFlow p) =>
            {
                ServiceReturn result = new ServiceReturn();
                result.ReturnedId = "";
                result.Message = "";
                try
                {
                    result = session.MessageFlowSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                }
                return Task.FromResult(result);
            });

            // POST /messageflow/search: free-text search on the message "text" field (body: SearchCriterias, only "term" is used).
            app.MapPost("/messageflow/search", (SearchCriterias p) =>
            {
                List<MessageFlow> result = new List<MessageFlow>();
                try
                {
                    result = session.MessageFlowsSearch(p.term);
                }
                catch (Exception ex)
                {
                    throw ex;
                }
                return Task.FromResult(result);
            });

            // POST /messageflow/last: returns up to "size" ticker messages matching "term" ("*" when empty). No sort is applied, so hits come in search-relevance order.
            app.MapPost("/messageflow/last", (SearchCriterias p) =>
            {
                if (p.term == null || p.term == "")
                {
                    p.term = "*";
                }
                if (p.size == null)
                {
                    p.size = 10;
                }
                List<MessageFlow> result = new List<MessageFlow>();
                try
                {
                    result = session.MessageFlowsSearch(p.term, p.size);
                }
                catch (Exception ex)
                {
                    throw ex;
                }
                return Task.FromResult(result);
            });

            // PUT /messageflow: creates a ticker message (same save as /messageflow/update); returns the new id, or "-1" with the error message.
            app.MapPut("/messageflow", (MessageFlow p) =>
            {
                ServiceReturn result = new ServiceReturn();
                result.ReturnedId = "";
                result.Message = "";
                try
                {
                    result = session.MessageFlowSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                }
                return Task.FromResult(result);
            });


            // GET /cache/{id}: loads one cached dialog by its document id.
            app.MapGet("/cache/{id}", (string id) =>
            {
                Cache result = new Cache();
                try
                {
                    result = session.CacheGet(id);
                }
                catch (Exception ex)
                {
                    throw ex;
                }
                return Task.FromResult(result);
            });

            // DELETE /cache/{id}, /dialog/{id}, /ontology/{id}: the document is marked deleted (isDeleted = true), never erased
            // (as every DELETE of the server: cache, completion, dialog, diary, document, idea, imageanalysis, intel, ontology,
            // relation, taskitem, youtubevideo stay in their index).
            app.MapDelete("/cache/{id}", (string id) => Task.FromResult(SoftDelete(() => session.CacheDelete(id))));
            app.MapDelete("/dialog/{id}", (string id) => Task.FromResult(SoftDelete(() => session.DialogDelete(id))));
            app.MapDelete("/ontology/{id}", (string id) => Task.FromResult(SoftDelete(() => session.OntologyDelete(id))));

            // POST /cache/update: saves (creates or overwrites) a cached dialog; returns the saved id, or "-1" with the error message.
            app.MapPost("/cache/update", (Cache p) =>
            {
                ServiceReturn result = new ServiceReturn();
                result.ReturnedId = "";
                result.Message = "";
                try
                {
                    result = session.CacheSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                }
                return Task.FromResult(result);
            });

            // POST /cache/search: free-text search on the cached "prompt" field (body: SearchCriterias: "term", "size"
            // (10 when missing) and "fuzzy": with "fuzzy", a loose search (one word with a typo or as the start of a word,
            // accents ignored, is enough), which the clients ask for when the plain search found nothing).
            app.MapPost("/cache/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                List<Cache> result = new List<Cache>();
                try
                {
                    result = p.fuzzy
                        ? session.CachesSearchFuzzy(p.term ?? string.Empty, size)
                        : session.CachesSearch(p.term ?? string.Empty, size);
                }
                catch (Exception ex)
                {
                    throw ex;
                }
                return Task.FromResult(result);
            });

            // POST /search: a loose search of "term" in several indices at once (body: MultiSearchCriterias: "term", "indices"
            // (cache, completion, dialog, diary, document, idea, imageanalysis, intel, ontology, relation, taskitem,
            // youtubevideo; all of them when empty), "size" per index): one word with a typo or as the start of a word is enough.
            app.MapPost("/search", (MultiSearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.SearchAll(p.term ?? string.Empty, p.indices, size));
            });

            // POST /recent: the documents of the last "minutes" minutes (60 by default) in "indices" (the same ones as /search;
            // all of them when empty), "size" per index, the newest first, each with its title and its text (/think of the chat).
            app.MapPost("/recent", (MultiSearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 50;
                var minutes = p.minutes > 0 ? p.minutes : 60;
                return Task.FromResult(session.RecentAll(minutes, p.indices, size));
            });

            // POST /cache/last: returns up to "size" cached dialogs matching "term" ("*" when empty). No sort is applied, so hits come in search-relevance order.
            app.MapPost("/cache/last", (SearchCriterias p) =>
            {
                if(p.term == null || p.term == "")
                {
                    p.term = "*";
                }
                if(p.size == null)
                {
                    p.size = 10;
                }
                List<Cache> result = new List<Cache>();
                try
                {
                    result = session.CachesSearch(p.term, p.size);
                }
                catch (Exception ex)
                {
                    throw ex;
                }
                return Task.FromResult(result);
            });



            // PUT /cache: creates a cached dialog (same save as /cache/update); returns the new id, or "-1" with the error message.
            app.MapPut("/cache", (Cache p) =>
            {
                ServiceReturn result = new ServiceReturn();
                result.ReturnedId = "";
                result.Message = "";
                try
                {
                    result = session.CacheSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                }
                return Task.FromResult(result);
            });

            // GET /ontology/{id}: loads one ontology by its document id.
            app.MapGet("/ontology/{id}", (string id) =>
            {
                Ontology result = new Ontology();
                try
                {
                    result = session.OntologyGet(id);
                }
                catch (Exception ex)
                {
                    throw ex;
                }
                return Task.FromResult(result);
            });

            // PUT /evaluation: saves (creates or overwrites) an evaluation; returns the saved id, or "-1" with the error message.
            app.MapPut("/evaluation", (Evaluation p) =>
            {
                ServiceReturn result = new ServiceReturn();
                result.ReturnedId = "";
                result.Message = "";
                try
                {
                    result = session.EvaluationSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                }
                return Task.FromResult(result);
            });

            // POST /ontology/update: saves (creates or overwrites) an ontology; returns the saved id, or "-1" with the error message.
            app.MapPost("/ontology/update", (Ontology p) =>
            {
                ServiceReturn result = new ServiceReturn();
                result.ReturnedId = "";
                result.Message = "";
                try
                {
                    result = session.OntologySave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                }
                return Task.FromResult(result);
            });

            // POST /ontology/search: free-text search on the ontology "prompt" field (body: SearchCriterias, only "term" is used).
            app.MapPost("/ontology/search", (SearchCriterias p) =>
            {
                List<Ontology> result = new List<Ontology>();
                try
                {
                    result = session.OntologiesSearch(p.term);
                }
                catch (Exception ex)
                {
                    throw ex;
                }
                return Task.FromResult(result);
            });

            // POST /ontology/last: returns up to "size" ontologies matching "term" ("*" when empty). No sort is applied, so hits come in search-relevance order.
            app.MapPost("/ontology/last", (SearchCriterias p) =>
            {
                if (p.term == null || p.term == "")
                {
                    p.term = "*";
                }
                if (p.size == null)
                {
                    p.size = 10;
                }
                List<Ontology> result = new List<Ontology>();
                try
                {
                    result = session.OntologiesSearch(p.term, p.size);
                }
                catch (Exception ex)
                {
                    throw ex;
                }
                return Task.FromResult(result);
            });

            // PUT /ontology: creates an ontology (same save as /ontology/update); returns the new id, or "-1" with the error message.
            app.MapPut("/ontology", (Ontology p) =>
            {
                ServiceReturn result = new ServiceReturn();
                result.ReturnedId = "";
                result.Message = "";
                try
                {
                    result = session.OntologySave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                }
                return Task.FromResult(result);
            });


            // GET /symbolicpattern/{id}: loads one symbolic pattern by its document id.
            app.MapGet("/symbolicpattern/{id}", (string id) =>
            {
                SymbolicPattern result = new SymbolicPattern();
                try
                {
                    result = session.SymbolicPatternGet(id);
                }
                catch (Exception ex)
                {
                    throw ex;
                }
                return Task.FromResult(result);
            });

            // POST /symbolicpattern/update: saves (creates or overwrites) a symbolic pattern; returns the saved id, or "-1" with the error message.
            app.MapPost("/symbolicpattern/update", (SymbolicPattern p) =>
            {
                ServiceReturn result = new ServiceReturn();
                result.ReturnedId = "";
                result.Message = "";
                try
                {
                    result = session.SymbolicPatternSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                }
                return Task.FromResult(result);
            });

            // POST /symbolicpattern/search: free-text search on the "regexPatterns" field (body: SearchCriterias, only "term" is used).
            app.MapPost("/symbolicpattern/search", (SearchCriterias p) =>
            {
                List<SymbolicPattern> result = new List<SymbolicPattern>();
                try
                {
                    result = session.SymbolicPatternsSearch(p.term);
                }
                catch (Exception ex)
                {
                    throw ex;
                }
                return Task.FromResult(result);
            });

            // POST /symbolicpattern/last: returns up to "size" symbolic patterns matching "term" ("*" when empty). No sort is applied, so hits come in search-relevance order.
            app.MapPost("/symbolicpattern/last", (SearchCriterias p) =>
            {
                if (p.term == null || p.term == "")
                {
                    p.term = "*";
                }
                if (p.size == null)
                {
                    p.size = 10;
                }
                List<SymbolicPattern> result = new List<SymbolicPattern>();
                try
                {
                    result = session.SymbolicPatternsSearch(p.term, p.size);
                }
                catch (Exception ex)
                {
                    throw ex;
                }
                return Task.FromResult(result);
            });

            // PUT /symbolicpattern: creates a symbolic pattern (same save as /symbolicpattern/update); returns the new id, or "-1" with the error message.
            app.MapPut("/symbolicpattern", (SymbolicPattern p) =>
            {
                ServiceReturn result = new ServiceReturn();
                result.ReturnedId = "";
                result.Message = "";
                try
                {
                    result = session.SymbolicPatternSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                }
                return Task.FromResult(result);
            });


            // GET /idea/{id}: loads one idea by its document id (404 when it does not exist).
            // ── Clusters: the groups of elements drawn with the pen of the graph of relations (never erased: DELETE marks isDeleted) ──

            // GET /cluster/{id}: loads one cluster by its document id (404 when it does not exist).
            app.MapGet("/cluster/{id}", (string id) =>
            {
                Cluster result = session.ClusterGet(id);
                return result is null ? Results.NotFound() : Results.Ok(result);
            });

            // PUT /cluster: creates a cluster; returns the new id, or "-1" with the error message.
            app.MapPut("/cluster", (Cluster p) => { p.Normalize(); return Task.FromResult(SoftDelete(() => session.ClusterSave(p))); });

            // POST /cluster/update: saves (creates or overwrites) a cluster; returns the saved id, or "-1" with the error message.
            app.MapPost("/cluster/update", (Cluster p) => { p.Normalize(); return Task.FromResult(SoftDelete(() => session.ClusterSave(p))); });

            // POST /cluster/search: free-text search on title, description, tags and members (body: SearchCriterias; "*" or empty: the newest ones).
            app.MapPost("/cluster/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.ClustersSearch(p.term ?? "*", size));
            });

            // POST /cluster/last: the "size" most recent clusters, newest first ("term" is ignored).
            app.MapPost("/cluster/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.ClustersLast(size));
            });

            // DELETE /cluster/{id}: marks the cluster deleted (isDeleted = true); returns its id, or "-1" with the error message.
            app.MapDelete("/cluster/{id}", (string id) => Task.FromResult(SoftDelete(() => session.ClusterDelete(id))));

            // ── Presets: the voice, the effects, the 3D animation and the personality of the avatar of the chat (/preset; never erased) ──

            // GET /preset/{id}: loads one preset by its document id (404 when it does not exist).
            app.MapGet("/preset/{id}", (string id) =>
            {
                Preset result = session.PresetGet(id);
                return result is null ? Results.NotFound() : Results.Ok(result);
            });

            // PUT /preset: creates a preset (its title in upper case, its effects within their ranges); returns the new id, or "-1" with the error message.
            app.MapPut("/preset", (Preset p) => { p.Normalize(); return Task.FromResult(SoftDelete(() => session.PresetSave(p))); });

            // POST /preset/update: saves (creates or overwrites) a preset; returns the saved id, or "-1" with the error message.
            app.MapPost("/preset/update", (Preset p) => { p.Normalize(); return Task.FromResult(SoftDelete(() => session.PresetSave(p))); });

            // POST /preset/search: free-text search on title, description, voice, animation, persona and tags (body: SearchCriterias; "*" or empty: the newest ones).
            app.MapPost("/preset/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.PresetsSearch(p.term ?? "*", size));
            });

            // POST /preset/last: the "size" most recent presets, newest first ("term" is ignored).
            app.MapPost("/preset/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.PresetsLast(size));
            });

            // DELETE /preset/{id}: marks the preset deleted (isDeleted = true); returns its id, or "-1" with the error message.
            app.MapDelete("/preset/{id}", (string id) => Task.FromResult(SoftDelete(() => session.PresetDelete(id))));

            // ── Wiki: the pages of the wiki of Kaonomi (/wiki in the chat), one document each, its id the slug of its title; never erased ──

            // GET /wiki/{id}: loads a page by its slug (404 when it does not exist or is deleted).
            app.MapGet("/wiki/{id}", (string id) =>
            {
                Wiki result = session.WikiGet(id);
                return result is null ? Results.NotFound() : Results.Ok(result);
            });

            // GET /wiki/{id}/backlinks: the pages that link to it ([text](wiki:id) in their content), by title.
            app.MapGet("/wiki/{id}/backlinks", (string id) => Task.FromResult(session.WikiBacklinks(id)));

            // PUT /wiki: creates a page (its id the slug of its title, or of the id given); "-1" when a page of that slug exists.
            app.MapPut("/wiki", (Wiki p) => Task.FromResult(SoftDelete(() => session.WikiSave(p, create: true))));

            // POST /wiki/update: saves a new version of a page (created when it does not exist); returns its id.
            app.MapPost("/wiki/update", (Wiki p) => Task.FromResult(SoftDelete(() => session.WikiSave(p, create: false))));

            // POST /wiki/search: free-text search on title, content, summary, tags and author (body: SearchCriterias; "*" or empty: the newest ones).
            app.MapPost("/wiki/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 50;
                return Task.FromResult(session.WikisSearch(p.term ?? "*", size));
            });

            // POST /wiki/last: the "size" pages changed last, newest first ("term" is ignored).
            app.MapPost("/wiki/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 50;
                return Task.FromResult(session.WikisLast(size));
            });

            // DELETE /wiki/{id}: marks the page deleted (isDeleted = true); returns its id, or "-1" with the error message.
            app.MapDelete("/wiki/{id}", (string id) => Task.FromResult(SoftDelete(() => session.WikiDelete(id))));

            // ── BPMN: the BPMN 2.0 diagrams of the chat (/bpmn), one document each (its XML and a comment), its id the slug of its title; never erased ──

            // GET /bpmn/{id}: loads a diagram by its slug (404 when it does not exist or is deleted).
            app.MapGet("/bpmn/{id}", (string id) =>
            {
                Bpmn result = session.BpmnGet(id);
                return result is null ? Results.NotFound() : Results.Ok(result);
            });

            // PUT /bpmn: creates a diagram (its id the slug of its title, or of the id given); "-1" when a diagram of that slug exists.
            app.MapPut("/bpmn", (Bpmn p) => Task.FromResult(SoftDelete(() => session.BpmnSave(p, create: true))));

            // POST /bpmn/update: saves a new version of a diagram (created when it does not exist); returns its id.
            app.MapPost("/bpmn/update", (Bpmn p) => Task.FromResult(SoftDelete(() => session.BpmnSave(p, create: false))));

            // POST /bpmn/search: free-text search on title, comment, summary, the names of the elements, tags and author (body: SearchCriterias; "*" or empty: the newest ones).
            app.MapPost("/bpmn/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 50;
                return Task.FromResult(session.BpmnsSearch(p.term ?? "*", size));
            });

            // POST /bpmn/last: the "size" diagrams changed last, newest first ("term" is ignored).
            app.MapPost("/bpmn/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 50;
                return Task.FromResult(session.BpmnsLast(size));
            });

            // DELETE /bpmn/{id}: marks the diagram deleted (isDeleted = true); returns its id, or "-1" with the error message.
            app.MapDelete("/bpmn/{id}", (string id) => Task.FromResult(SoftDelete(() => session.BpmnDelete(id))));

            // ── Infrastructure: the infrastructure diagrams of the chat (/infra), stored as the BPMN diagrams, in the infra index; never erased ──

            // GET /infra/{id}: loads a diagram by its slug (404 when it does not exist or is deleted).
            app.MapGet("/infra/{id}", (string id) =>
            {
                Infra result = session.InfraGet(id);
                return result is null ? Results.NotFound() : Results.Ok(result);
            });

            // PUT /infra: creates a diagram (its id the slug of its title); "-1" when a diagram of that slug exists.
            app.MapPut("/infra", (Infra p) => Task.FromResult(SoftDelete(() => session.InfraSave(p, create: true))));

            // POST /infra/update: saves a new version of a diagram (created when it does not exist); returns its id.
            app.MapPost("/infra/update", (Infra p) => Task.FromResult(SoftDelete(() => session.InfraSave(p, create: false))));

            // POST /infra/search: free-text search on title, comment, summary, the names of the elements, tags and author.
            app.MapPost("/infra/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 50;
                return Task.FromResult(session.InfrasSearch(p.term ?? "*", size));
            });

            // POST /infra/last: the "size" diagrams changed last, newest first.
            app.MapPost("/infra/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 50;
                return Task.FromResult(session.InfrasLast(size));
            });

            // DELETE /infra/{id}: marks the diagram deleted (isDeleted = true); returns its id, or "-1" with the error message.
            app.MapDelete("/infra/{id}", (string id) => Task.FromResult(SoftDelete(() => session.InfraDelete(id))));

            // ── Mind maps: the mind maps of the chat (/mindmap), one document each (its FreeMind XML and a comment), its id the slug of its title; never erased ──

            // GET /mindmap/{id}: loads a map by its slug (404 when it does not exist or is deleted).
            app.MapGet("/mindmap/{id}", (string id) =>
            {
                Mindmap result = session.MindmapGet(id);
                return result is null ? Results.NotFound() : Results.Ok(result);
            });

            // PUT /mindmap: creates a map (its id the slug of its title, or of the id given); "-1" when a map of that slug exists.
            app.MapPut("/mindmap", (Mindmap p) => Task.FromResult(SoftDelete(() => session.MindmapSave(p, create: true))));

            // POST /mindmap/update: saves a new version of a map (created when it does not exist); returns its id.
            app.MapPost("/mindmap/update", (Mindmap p) => Task.FromResult(SoftDelete(() => session.MindmapSave(p, create: false))));

            // POST /mindmap/search: free-text search on title, comment, summary, the texts of the nodes, tags and author (body: SearchCriterias; "*" or empty: the newest ones).
            app.MapPost("/mindmap/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 50;
                return Task.FromResult(session.MindmapsSearch(p.term ?? "*", size));
            });

            // POST /mindmap/last: the "size" maps changed last, newest first ("term" is ignored).
            app.MapPost("/mindmap/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 50;
                return Task.FromResult(session.MindmapsLast(size));
            });

            // DELETE /mindmap/{id}: marks the map deleted (isDeleted = true); returns its id, or "-1" with the error message.
            app.MapDelete("/mindmap/{id}", (string id) => Task.FromResult(SoftDelete(() => session.MindmapDelete(id))));

            // ── Ghost writer: the writings of the chat (/write), one document each (general information, actors, locations, chapters and their texts), its id the slug of its title; never erased ──

            // GET /ghostwriter/{id}: loads a writing by its slug (404 when it does not exist or is deleted).
            app.MapGet("/ghostwriter/{id}", (string id) =>
            {
                GhostWriter result = session.GhostWriterGet(id);
                return result is null ? Results.NotFound() : Results.Ok(result);
            });

            // PUT /ghostwriter: creates a writing (its id the slug of its title, or of the id given); "-1" when a writing of that slug exists.
            app.MapPut("/ghostwriter", (GhostWriter p) => Task.FromResult(SoftDelete(() => session.GhostWriterSave(p, create: true))));

            // POST /ghostwriter/update: saves a new version of a writing (created when it does not exist); returns its id.
            app.MapPost("/ghostwriter/update", (GhostWriter p) => Task.FromResult(SoftDelete(() => session.GhostWriterSave(p, create: false))));

            // POST /ghostwriter/search: free-text search on title, description, goals, audience, style and author (body: SearchCriterias; "*" or empty: the newest ones).
            app.MapPost("/ghostwriter/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 50;
                return Task.FromResult(session.GhostWritersSearch(p.term ?? "*", size));
            });

            // POST /ghostwriter/last: the "size" writings changed last, newest first ("term" is ignored).
            app.MapPost("/ghostwriter/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 50;
                return Task.FromResult(session.GhostWritersLast(size));
            });

            // DELETE /ghostwriter/{id}: marks the writing deleted (isDeleted = true); returns its id, or "-1" with the error message.
            app.MapDelete("/ghostwriter/{id}", (string id) => Task.FromResult(SoftDelete(() => session.GhostWriterDelete(id))));

            // ── The journal of the writings: one entry per action that changes a text (save, chapter, generation, correction…). It only grows: no update, no delete. ──

            // PUT /ghostwriter/journal: adds an entry (a new id and the date of now, whatever was sent); returns its id, or "-1" with the error message.
            app.MapPut("/ghostwriter/journal", (GhostWriterJournal p) => Task.FromResult(SoftDelete(() => session.GhostWriterJournalAdd(p))));

            // POST /ghostwriter/journal/search: the "size" last entries of the writing "term" (its id), newest first.
            app.MapPost("/ghostwriter/journal/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 200;
                return Task.FromResult(session.GhostWriterJournalOf(p.term ?? string.Empty, size));
            });

            app.MapGet("/idea/{id}", (string id) =>
            {
                Idea result = session.IdeaGet(id);
                return result is null ? Results.NotFound() : Results.Ok(result);
            });

            // PUT /idea: creates an idea; returns the new id, or "-1" with the error message.
            app.MapPut("/idea", (Idea p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.IdeaSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /idea/update: saves (creates or overwrites) an idea; returns the saved id, or "-1" with the error message.
            app.MapPost("/idea/update", (Idea p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.IdeaSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /idea/search: free-text search on title, description, category and tags (body: SearchCriterias; "*" or empty: the newest ideas).
            app.MapPost("/idea/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.IdeasSearch(p.term ?? "*", size));
            });

            // POST /idea/last: the "size" most recent ideas, newest first ("term" is ignored).
            app.MapPost("/idea/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.IdeasLast(size));
            });

            // DELETE /idea/{id}: deletes an idea; returns its id, or "-1" with the error message.
            app.MapDelete("/idea/{id}", (string id) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.IdeaDelete(id);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });


            // GET /diary/{id}: loads one diary entry by its document id (404 when it does not exist).
            app.MapGet("/diary/{id}", (string id) =>
            {
                Diary result = session.DiaryGet(id);
                return result is null ? Results.NotFound() : Results.Ok(result);
            });

            // PUT /diary: creates a diary entry; returns the new id, or "-1" with the error message.
            app.MapPut("/diary", (Diary p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.DiarySave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /diary/update: saves (creates or overwrites) a diary entry; returns the saved id, or "-1" with the error message.
            app.MapPost("/diary/update", (Diary p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.DiarySave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /diary/search: free-text search on the "text" field only (body: SearchCriterias; "*" or empty: the newest entries).
            app.MapPost("/diary/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.DiariesSearch(p.term ?? "*", size));
            });

            // POST /diary/last: the "size" most recent diary entries, newest first ("term" is ignored).
            app.MapPost("/diary/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.DiariesLast(size));
            });

            // DELETE /diary/{id}: soft delete, nothing is erased: the entry gets isDeleted = true (left out of search and last);
            // returns its id, or "-1" with the error message.
            app.MapDelete("/diary/{id}", (string id) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.DiaryDelete(id);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // GET /task/{id}: loads one task by its document id, deleted or not (404 when it does not exist).
            app.MapGet("/task/{id}", (string id) =>
            {
                TaskItem result = session.TaskItemGet(id);
                return result is null ? Results.NotFound() : Results.Ok(result);
            });

            // PUT /task: creates a task; returns the new id, or "-1" with the error message.
            app.MapPut("/task", (TaskItem p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.TaskItemSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /task/update: saves (creates or overwrites) a task; returns the saved id, or "-1" with the error message.
            app.MapPost("/task/update", (TaskItem p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.TaskItemSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /task/search: free-text search on title, description, type, status and comment (body: SearchCriterias; "*" or empty: the newest tasks); deleted tasks are left out.
            app.MapPost("/task/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.TaskItemsSearch(p.term ?? "*", size));
            });

            // POST /task/last: the "size" most recent tasks, newest first, without the deleted ones ("term" is ignored).
            app.MapPost("/task/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.TaskItemsLast(size));
            });

            // DELETE /task/{id}: soft delete, nothing is erased: the task gets isDeleted = true (left out of search and last);
            // returns its id, or "-1" with the error message.
            app.MapDelete("/task/{id}", (string id) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.TaskItemDelete(id);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });


            // GET /intel/{id}: loads one intel by its document id, deleted or not (404 when it does not exist).
            app.MapGet("/intel/{id}", (string id) =>
            {
                Intel result = session.IntelGet(id);
                return result is null ? Results.NotFound() : Results.Ok(result);
            });

            // PUT /intel: creates an intel; returns the new id, or "-1" with the error message.
            app.MapPut("/intel", (Intel p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.IntelSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /intel/update: saves (creates or overwrites) an intel; returns the saved id, or "-1" with the error message.
            app.MapPost("/intel/update", (Intel p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.IntelSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /intel/search: free-text search in all the text fields of the intels, lists included (body: SearchCriterias; "*" or empty: the newest intels); deleted intels are left out.
            app.MapPost("/intel/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.IntelsSearch(p.term ?? "*", size));
            });

            // POST /intel/last: the "size" most recent intels, newest first, without the deleted ones ("term" is ignored).
            app.MapPost("/intel/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.IntelsLast(size));
            });

            // DELETE /intel/{id}: soft delete, nothing is erased: the intel gets isDeleted = true (left out of search and last);
            // returns its id, or "-1" with the error message.
            app.MapDelete("/intel/{id}", (string id) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.IntelDelete(id);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });


            // PUT /useractivity: saves an activity of the journal of the chat (index useractivities); returns the new id, or "-1" with the error message.
            app.MapPut("/useractivity", (UserActivities p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.UserActivitySave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /useractivity/last: the "size" most recent activities, newest first, without the deleted ones ("term" is ignored).
            app.MapPost("/useractivity/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 50;
                return Task.FromResult(session.UserActivitiesLast(size));
            });

            // POST /useractivity/search: search in the action, window, link and description ("*" or empty: the newest); deleted ones left out.
            app.MapPost("/useractivity/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 50;
                return Task.FromResult(session.UserActivitiesSearch(p.term ?? "*", size));
            });

            // PUT /gamescore: saves the score of a finished game of the chat (game1: the morpion solitaire of /game:solitaire); returns the new id, or "-1" with the error message.
            app.MapPut("/gamescore", (GameScore p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.GameScoreSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /gamescore/best: the "size" best scores of the game named by "term" (game1 when empty), the highest first; deleted scores are left out.
            app.MapPost("/gamescore/best", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                var game = string.IsNullOrWhiteSpace(p.term) || p.term == "*" ? "game1" : p.term.Trim();
                return Task.FromResult(session.GameScoresBest(game, size));
            });

            // POST /gamescore/last: the "size" most recent scores (all games), newest first, without the deleted ones ("term" is ignored).
            app.MapPost("/gamescore/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.GameScoresLast(size));
            });

            // DELETE /gamescore/{id}: soft delete, the score gets isDeleted = true (left out of best and last); returns its id, or "-1" with the error message.
            app.MapDelete("/gamescore/{id}", (string id) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.GameScoreDelete(id);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });


            // GET /collectionplan/{id}: loads one collection plan by its document id, deleted or not (404 when it does not exist).
            app.MapGet("/collectionplan/{id}", (string id) =>
            {
                CollectionPlan result = session.CollectionPlanGet(id);
                return result is null ? Results.NotFound() : Results.Ok(result);
            });

            // PUT /collectionplan: creates a collection plan; returns the new id, or "-1" with the error message.
            app.MapPut("/collectionplan", (CollectionPlan p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.CollectionPlanSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /collectionplan/update: saves (creates or overwrites) a collection plan; returns the saved id, or "-1" with the error message.
            app.MapPost("/collectionplan/update", (CollectionPlan p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.CollectionPlanSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /collectionplan/search: free-text search in all the text fields of the collection plans (body: SearchCriterias; "*" or empty: the newest plans); deleted plans are left out.
            app.MapPost("/collectionplan/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.CollectionPlansSearch(p.term ?? "*", size));
            });

            // POST /collectionplan/last: the "size" most recent collection plans, newest first, without the deleted ones ("term" is ignored).
            app.MapPost("/collectionplan/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.CollectionPlansLast(size));
            });

            // DELETE /collectionplan/{id}: soft delete, nothing is erased: the plan gets isDeleted = true (left out of search and last);
            // returns its id, or "-1" with the error message.
            app.MapDelete("/collectionplan/{id}", (string id) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.CollectionPlanDelete(id);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });


            // GET /youtubevideo/{id}: loads one YouTube video by its document id, deleted or not (404 when it does not exist).
            app.MapGet("/youtubevideo/{id}", (string id) =>
            {
                YoutubeVideo result = session.YoutubeVideoGet(id);
                return result is null ? Results.NotFound() : Results.Ok(result);
            });

            // PUT /youtubevideo: creates a YouTube video; returns the new id, or "-1" with the error message.
            app.MapPut("/youtubevideo", (YoutubeVideo p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.YoutubeVideoSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /youtubevideo/update: saves (creates or overwrites) a YouTube video; returns the saved id, or "-1" with the error message.
            app.MapPost("/youtubevideo/update", (YoutubeVideo p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.YoutubeVideoSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /youtubevideo/search: free-text search on "title" and "description" (body: SearchCriterias; "*" or empty: the newest videos); deleted videos are left out.
            app.MapPost("/youtubevideo/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.YoutubeVideosSearch(p.term ?? "*", size));
            });

            // POST /youtubevideo/last: the "size" most recent YouTube videos, newest first, without the deleted ones ("term" is ignored).
            app.MapPost("/youtubevideo/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.YoutubeVideosLast(size));
            });

            // DELETE /youtubevideo/{id}: soft delete, nothing is erased: the YouTube video gets isDeleted = true (left out of search
            // and last); returns its id, or "-1" with the error message.
            app.MapDelete("/youtubevideo/{id}", (string id) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.YoutubeVideoDelete(id);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // GET /document/{id}: loads one document by its id, deleted or not (404 when it does not exist).
            app.MapGet("/document/{id}", (string id) =>
            {
                Document result = session.DocumentGet(id);
                return result is null ? Results.NotFound() : Results.Ok(result);
            });

            // PUT /document: creates a document; returns the new id, or "-1" with the error message.
            app.MapPut("/document", (Document p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.DocumentSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /document/update: saves (creates or overwrites) a document; returns the saved id, or "-1" with the error message.
            app.MapPost("/document/update", (Document p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.DocumentSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /document/search: fuzzy search on "title", "description" and the content of every version (body: SearchCriterias; "*" or empty: the newest documents); deleted documents are left out.
            app.MapPost("/document/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.DocumentsSearch(p.term ?? "*", size));
            });

            // POST /document/last: the "size" most recent documents, newest first, without the deleted ones ("term" is ignored).
            app.MapPost("/document/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.DocumentsLast(size));
            });

            // DELETE /document/{id}: soft delete, nothing is erased: the document gets isDeleted = true (left out of search and
            // last); returns its id, or "-1" with the error message.
            app.MapDelete("/document/{id}", (string id) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.DocumentDelete(id);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // GET /completion/{id}: loads one completion by its document id (404 when it does not exist).
            app.MapGet("/completion/{id}", (string id) =>
            {
                Completion result = session.CompletionGet(id);
                return result is null ? Results.NotFound() : Results.Ok(result);
            });

            // POST /completion/update: saves (creates or overwrites) a completion; returns the saved id, or "-1" with the error message.
            app.MapPost("/completion/update", (Completion p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.CompletionSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /completion/search: free-text search on "prompt" and "completion" (body: SearchCriterias; "*" or empty: the newest completions).
            app.MapPost("/completion/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.CompletionsSearch(p.term ?? "*", size));
            });

            // POST /completion/query: the advanced search (body: CompletionQuery): a text in the prompt and/or the completion, a period
            // (from, to), models, languages, topics, collections, the order (newest, oldest, relevance), the size; the deleted ones left out.
            app.MapPost("/completion/query", (CompletionQuery p) => Task.FromResult(session.CompletionsQuery(p)));

            // POST /completion/last: the "size" most recent completions, newest first ("term" is ignored).
            app.MapPost("/completion/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.CompletionsLast(size));
            });

            // DELETE /completion/{id}: deletes a completion; returns its id, or "-1" with the error message.
            app.MapDelete("/completion/{id}", (string id) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.CompletionDelete(id);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // GET /relation/{id}: loads one relation by its id, deleted or not (404 when it does not exist).
            app.MapGet("/relation/{id}", (string id) =>
            {
                Relation result = session.RelationGet(id);
                return result is null ? Results.NotFound() : Results.Ok(result);
            });

            // PUT /relation: saves a version of a relation between windows (category "interdata" by default): a new relation is created with its versions; for a stored one (its id), the last version brought is added with the next number. Returns its id, or "-1" with the error message.
            app.MapPut("/relation", (Relation p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.RelationSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /relation/update: same as PUT /relation (adds a version to a stored relation).
            app.MapPost("/relation/update", (Relation p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.RelationSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /relation/search: fuzzy search on the nature, title, description and ends of all the versions (body: SearchCriterias); deleted ones left out.
            app.MapPost("/relation/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.RelationsSearch(p.term ?? "*", size));
            });

            // POST /relation/last: the "size" most recent relations, without the deleted ones.
            app.MapPost("/relation/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.RelationsLast(size));
            });

            // DELETE /relation/{id}: soft delete (isDeleted = true; restored with /relation/update and isDeleted = false).
            app.MapDelete("/relation/{id}", (string id) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.RelationDelete(id);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // GET /annotation/{id}: loads one annotation by its id, deleted or not (404 when it does not exist).
            app.MapGet("/annotation/{id}", (string id) =>
            {
                Annotation result = session.AnnotationGet(id);
                return result is null ? Results.NotFound() : Results.Ok(result);
            });

            // PUT /annotation: saves an annotation of an object of the application (an entity, a block of text, an image, an element of a project, an idea…): created without an id, replaced with the id of a stored one; tags normalized (#SOFTWARE…). Returns its id, or "-1" with the error message.
            app.MapPut("/annotation", (Annotation p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.AnnotationSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /annotation/update: same as PUT /annotation (replaces a stored annotation).
            app.MapPost("/annotation/update", (Annotation p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.AnnotationSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /annotation/search: fuzzy search on the text, tags, object annotated (type, id, title) and excerpt (body: SearchCriterias); deleted ones left out.
            app.MapPost("/annotation/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.AnnotationsSearch(p.term ?? "*", size));
            });

            // POST /annotation/last: the "size" most recent annotations, without the deleted ones.
            app.MapPost("/annotation/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.AnnotationsLast(size));
            });

            // DELETE /annotation/{id}: soft delete (isDeleted = true; restored with /annotation/update and isDeleted = false).
            app.MapDelete("/annotation/{id}", (string id) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.AnnotationDelete(id);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // GET /imageanalysis/{id}: loads one image analysis by its id, deleted or not (404 when it does not exist).
            app.MapGet("/imageanalysis/{id}", (string id) =>
            {
                ImageAnalysis result = session.ImageAnalysisGet(id);
                return result is null ? Results.NotFound() : Results.Ok(result);
            });

            // GET /imageanalysis/image?path=…: the analysis of the picture at this path (404 when it has none yet).
            app.MapGet("/imageanalysis/image", (string path) =>
            {
                ImageAnalysis result = session.ImageAnalysisOfImage(path);
                return result is null ? Results.NotFound() : Results.Ok(result);
            });

            // PUT /imageanalysis: saves the analysis of a picture (one per picture: its id comes from the path of the picture); returns the id, or "-1" with the error message.
            app.MapPut("/imageanalysis", (ImageAnalysis p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.ImageAnalysisSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /imageanalysis/update: same as PUT /imageanalysis (creates or overwrites the analysis of the picture).
            app.MapPost("/imageanalysis/update", (ImageAnalysis p) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.ImageAnalysisSave(p);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // POST /imageanalysis/search: fuzzy search on the file title, the AI analysis, the comments, the annotations and the place (body: SearchCriterias); deleted ones left out.
            app.MapPost("/imageanalysis/search", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.ImageAnalysesSearch(p.term ?? "*", size));
            });

            // POST /imageanalysis/last: the "size" most recent image analyses, without the deleted ones.
            app.MapPost("/imageanalysis/last", (SearchCriterias p) =>
            {
                var size = p.size > 0 ? p.size : 10;
                return Task.FromResult(session.ImageAnalysesLast(size));
            });

            // DELETE /imageanalysis/{id}: soft delete (isDeleted = true; restored with /imageanalysis/update and isDeleted = false).
            app.MapDelete("/imageanalysis/{id}", (string id) =>
            {
                ServiceReturn result = new ServiceReturn();
                try
                {
                    result = session.ImageAnalysisDelete(id);
                }
                catch (Exception ex)
                {
                    result.ReturnedId = "-1";
                    result.Message = ex.Message;
                    result.Success = false;
                }
                return Task.FromResult(result);
            });

            // GET /image/status: state of the Fooocus API that generates the images (version, default model, busy); 503 when it does not answer.
            app.MapGet("/image/status", async () =>
            {
                try
                {
                    return Results.Ok(await session.ImageStatusAsync());
                }
                catch (Exception ex)
                {
                    return Results.Problem($"Fooocus API not reachable: {ex.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });

            // GET /image/options: the styles, performances, aspect ratios and models an image generation accepts, and the defaults of Fooocus.
            app.MapGet("/image/options", async () =>
            {
                try
                {
                    return Results.Ok(await session.ImageOptionsAsync());
                }
                catch (Exception ex)
                {
                    return Results.Problem($"Fooocus API not reachable: {ex.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            });

            // POST /image/generate: generates images from a prompt with Fooocus (body: ImageGenerationRequest); answers when they are done, with
            // their files and their data in base 64 (400 for an empty prompt, 502 when Fooocus fails or does not answer).
            app.MapPost("/image/generate", async (ImageGenerationRequest p) =>
            {
                if (string.IsNullOrWhiteSpace(p.prompt))
                    return Results.BadRequest(new ImageGenerationResult { error = "The prompt is empty" });
                var result = await session.ImageGenerateAsync(p);
                return result.error is null ? Results.Ok(result) : Results.Json(result, statusCode: StatusCodes.Status502BadGateway);
            });

            return app;
        }

        /// <summary>A soft delete asked by a DELETE: its result, or the error as a result.</summary>
        private static ServiceReturn SoftDelete(Func<ServiceReturn> mark)
        {
            try
            {
                return mark();
            }
            catch (Exception ex)
            {
                return new ServiceReturn { ReturnedId = "-1", Message = ex.Message, Success = false };
            }
        }

    }
}
