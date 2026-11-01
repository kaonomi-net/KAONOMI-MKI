[www.kaonomi.net](https://www.kaonomi.net)

# KAONOMI.Core

Shared class library for the Kaonomi platform: domain models, Elasticsearch persistence (`NEST`), Ollama HTTP helpers, configuration loading, and the `SessionManager` façade used by the server and agent hosts.

## Responsibilities

- **Models** — `EntityBase`, dialogs, logs, performance, completions, LLM request/response DTOs, `Cache`, `SymbolicPattern`, `MessageFlow` (ticker messages: `text`, `color`, `priority`, `url`), search criteria, service return values; and, among the documents of the chat:
  - `Intel` (name, description, `categories`, triggers, related and excluded entities, events, signal, tags, instruction, actions, start and end dates)
  - `CollectionPlan` (the plans of the web scraper: name, description, project, instructions, active, and its `websites`, each with its `urlProcesses`: url, mode fixed / explore / random, keywords)
  - `Cluster` (the groups drawn with the pen; `category`: where it was drawn, graphnode, bpmnai, infra, mindmap)
  - `Annotation` (its `AnnotationTargetTypes`, with `DiagramElement` and `MindmapTopic`)
  - `GameScore` (the scores of `/game1`: score, lines, durationSeconds, end, comment)
  - `UserActivities` (the journal of the activities, index `useractivities`: action, windowName, link, description)
- **Data** — `ElasticRepository` (save, load, field search with match + case-insensitive wildcard; `DeleteAll<T>` erases a whole index, used by `DELETE /log`); the index of a type is its name in lower case (`UserActivities` → `useractivities`)
- **Helpers** — `HelperOllamaClient`, `HelperFooocusClient` (image generation through the Fooocus API), `HelperConfigManager`, parsers/tools, hashing
- **SessionManager** — high-level save/get/search for logs (and erase them all), dialogs, cache, symbolic patterns, message flows, token usage, intels, collection plans, game scores (the best of a game), the journal of the activities
- **Hosting** — MARI/Orleans silo builder extensions (`UseKaonomiNode`)

## Target framework

.NET 8.

## Dependencies

- `NEST` 7.x (Elasticsearch)
- Microsoft Orleans / MARI-related packages used by hosting extensions

## Configuration consumers

Projects that reference Core typically load a `config.json` with:

- `ollamaUrl`
- `llmmodel`
- `dataRepository` / `dialogRepository` / `adminRepository` (Elasticsearch URL, user, password, prefix)

## Build

```bash
dotnet build KAONOMI.Core/KAONOMI.Core.csproj
```

## See also

- [Solution README](../README.md)
- [KAONOMI.Server](../KAONOMI.Server/README.md)
