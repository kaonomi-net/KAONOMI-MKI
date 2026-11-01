# KAONOMI.SearchEngine

A small search engine that answers like **Elasticsearch 7.14** for what KAONOMI does with it: one node (no
clustering), one shard per index, built on [Lucene.Net 4.8](https://lucenenet.apache.org) (Elasticsearch is built on
Lucene too). The applications keep their `NEST` client and their `config.json`: the engine listens on
`http://localhost:9200`, answers the root (version `0.1`, tagline "let's go to think", `X-elastic-product: Elasticsearch`) and the APIs below with the
bodies, the status codes and the errors of Elasticsearch.

## Run

```bash
dotnet run --project KAONOMI.SearchEngine
```

Stop Elasticsearch first, or give the engine another port (`-E http.port=9201`), since both use 9200 by default.
Stop the engine with Ctrl+C, `SIGTERM` or `POST /_shutdown` (the chat sends it on `/bye`): the indices are written to disk before it exits. A second engine (or an import) on the same `path.data` stops at once (`node.lock`), and the port is checked before the indices are opened.

| Command | Role |
| --- | --- |
| `KAONOMI.SearchEngine [--config file.yml] [-E key=value]…` | Serves the REST API |
| `KAONOMI.SearchEngine check` | Checks the Lucene indices of `path.data` (engine stopped) |
| `KAONOMI.SearchEngine import --from http://localhost:9200 [--indices a,b*] [--user u --password p] [--batch 2000] [--overwrite]` | Copies indices of a running Elasticsearch (mapping, then documents with their ids, read by scroll) into `path.data`. The engine must not be running on that data |

To move KAONOMI to the engine: run Elasticsearch, run `import` (about 18,000 documents per second: `--indices` copies some
of the indices only, e.g. without `log`), stop Elasticsearch, then start the engine on port 9200.

## Configuration: `config/searchengine.yml`

Keys in the style of `elasticsearch.yml`, dotted (`http.port: 9200`) or nested (`http: { port: 9200 }`). Any key can be
overridden on the command line (`-E path.data=/data/kaonomi`). Relative paths are relative to the folder of the file.

| Key | Default | Role |
| --- | --- | --- |
| `cluster.name`, `node.name` | `kaonomi`, `kaonomi-search-1` | Shown by `GET /` and `_cluster/health` |
| `path.data` | `../data` | Where the indices are stored (`indices/<name>/`: Lucene index + `meta.json`) |
| `path.logs` | (console only) | Folder of the log file `<cluster>.log` |
| `network.host`, `http.port` | `127.0.0.1`, `9200` | Where the API listens (`0.0.0.0`: every interface) |
| `xpack.security.enabled`, `xpack.security.users` | `false` | Basic authentication with the listed users (`username`, `password`) |
| `action.auto_create_index` | `true` | A write creates its index |
| `index.refresh_interval` | `1s` | When the writes become searchable (`-1`: only on `refresh`) |
| `index.translog.sync_interval` | `5s` | When the indices are written to disk |
| `index.max_result_window` | `10000` | Largest `from + size` |
| `index.number_of_replicas` | `1` | Replicas reported (there are none: `1` gives the "yellow" health of a single Elasticsearch node) |
| `indices.query.bool.max_clause_count` | `1024` | Largest number of clauses of a query |
| `kaonomi.ram_buffer_mb` | `64` | Memory of the writes before a segment is written |
| `kaonomi.log_requests` | `false` | Logs every request |

## What is supported

| Area | API |
| --- | --- |
| Root, health | `GET /`, `HEAD /`, `_cluster/health`, `_cat/indices`, `_cat/health`, `_cat/count` (`?v`, `?format=json`, `?h=`); `POST /_shutdown` (not in Elasticsearch: stops the engine) |
| Documents | `PUT/POST /i/_doc/id`, `POST /i/_doc`, `_create`, `GET/HEAD/DELETE /i/_doc/id`, `GET /i/_source/id`, `POST /i/_update/id` (`doc`, `doc_as_upsert`, `upsert`), `_bulk`, `_mget`; `?refresh=true|wait_for`, `op_type`; versions, `_seq_no`; realtime reads by id |
| Searches | `_search` and `_count` on an index, a list, a wildcard or every index; `from`, `size`, `sort` (fields, `_score`, `_doc`, `order`, `missing`, `unmapped_type`, `mode`), `search_after`, `track_total_hits`, `track_scores`, `_source` filtering, `version`, `seq_no_primary_term`, `min_score`, `q=` |
| Queries | `match_all`, `match_none`, `match` (operator, fuzziness `AUTO`, `minimum_should_match`, `zero_terms_query`, `lenient`), `match_phrase`, `multi_match`, `term`, `terms`, `ids`, `range` (dates with formats, date math, `time_zone`), `exists`, `prefix`, `wildcard`, `regexp` (`case_insensitive`), `fuzzy`, `bool` (`must`, `should`, `must_not`, `filter`, `minimum_should_match`), `constant_score`, `dis_max`, `nested` (as an object); `boost`, `_name` → `matched_queries` |
| Indices | `PUT /i` (`mappings`, `settings`), `DELETE /i`, `HEAD /i`, `GET /i`, `_mapping` (get, put), `_settings` (get), `_alias(es)` (none), `_refresh`, `_flush`, `_analyze` |
| Mapping | Dynamic mapping as Elasticsearch: strings → `text` + `keyword` (`ignore_above: 256`) or `date` (`strict_date_optional_time`, `yyyy/MM/dd`), integers → `long`, decimals → `float`, `boolean`, objects; `dynamic: true|false|strict`, `date_detection`, `numeric_detection`; explicit `text`, `keyword`, `date` (`format`), numbers, `boolean`, `object`/`nested`, `enabled: false`. Coercion and refusals (`mapper_parsing_exception`) as Elasticsearch |

Not supported (an error says so): aggregations, scroll (use `search_after`), scripts, `post_filter`, aliases,
templates, custom analyzers (every text field uses the `standard` analyzer), clustering and replicas.

## How close to Elasticsearch

- **Analysis**: the `standard` analyzer of Lucene 8.9 (Elasticsearch 7.14), rebuilt on the tokenizer of Lucene 4.8: the
  newer word rules (U+202F, the narrow no-break space of French typography, joins words; emoji are tokens; long tokens
  are cut at 255 characters). Same tokens on the 4,540 texts of the `completion` index of KAONOMI.
- **Scores**: BM25 as Lucene 8 (idf and average length over the documents with the field, length norms encoded on one
  byte as Lucene 8), fuzzy expansions scored with blended frequencies (`top_terms_blended_freqs`), wildcards with a
  constant score: the same scores as Elasticsearch to the third decimal.
- **Sorts**: the values of `sort` (dates in milliseconds, `Long.MIN_VALUE` for a missing date in a descending sort…),
  missing values last, `search_after` with ties left out as Elasticsearch.
- Checked against Elasticsearch 7.14.2: the searches of `ElasticRepository` on 16 real indices of KAONOMI (342 queries:
  same hits in the same order, same totals), the scenario of `KAONOMI.SearchEngine.Tests` through NEST, and 45 raw
  requests (bodies and errors).

## Performance

Searches sorted on a date (`Last`, `Since`, `SinceAll` of KAONOMI) only look at the most recent values (a window on
the field, widened until the page is full) and count the total up to `track_total_hits` (10,000), as Elasticsearch does:
on 2 million logs, the last 10,000 come in about 50 ms, a page of `search_after` in about 30 ms. Indexing through `_bulk`
runs at about 10,000 documents per second.

## Tests

```bash
dotnet test KAONOMI.SearchEngine.Tests
```

The tests start the engine in their process (free port, temporary folder) and use the repository of KAONOMI
(`ElasticRepository`, NEST 7.17). When an Elasticsearch answers on `ES_URL` (default `http://localhost:9200`), the same
scenario runs on it too and the results must be equal line by line; its test indices (`zz_setest_…`) are deleted after.

## Code

| Folder | Role |
| --- | --- |
| `Configuration/` | `EngineSettings`: the YAML file and the `-E` overrides |
| `Http/` | `EsHttpHandler` (routes, answers), `EsException` (errors of Elasticsearch), `EsJson` |
| `Engine/` | `IndexManager` (indices on disk), `EsIndex` (Lucene writer and searcher, versions, realtime reads, refresh and commit), `Mapping` and `DocumentParser` (dynamic mapping, JSON → Lucene fields), `QueryBuilder` (query DSL → Lucene), `SearchService` (sorts, `search_after`, hits), `DateWindow` (fast date sorts), `Scoring` (BM25 of Lucene 8, blended fuzzy rewrite), `EsAnalyzer` (standard analyzer of Lucene 8.9), `EsDates` (date formats and date math) |
| `Importer.cs` | The `import` command |
