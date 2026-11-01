# KAONOMI.Core

Shared class library for the Kaonomi platform: domain models, Elasticsearch persistence (`NEST`), Ollama HTTP helpers, configuration loading, and the `SessionManager` façade used by the server and agent hosts.

## Responsibilities

- **Models** — `EntityBase`, dialogs, logs, performance, completions, LLM request/response DTOs, `Cache`, `SymbolicPattern`, `MessageFlow` (ticker messages: `text`, `color`, `priority`, `url`), search criteria, service return values
- **Data** — `ElasticRepository` (save, load, field search with match + case-insensitive wildcard)
- **Helpers** — `HelperOllamaClient`, `HelperFooocusClient` (image generation through the Fooocus API), `HelperConfigManager`, parsers/tools, hashing
- **SessionManager** — high-level save/get/search for logs, dialogs, cache, symbolic patterns, message flows, token usage
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
