[www.kaonomi.net](https://www.kaonomi.net)

# KAONOMI.Admin

Interactive console that sends prompts to Ollama and persists logs, performance metrics, and completions through `SessionManager` / Elasticsearch.

## Run

```bash
dotnet run --project KAONOMI.Admin
```

## Configuration

`config.json` (copied to the output directory on build):

| Key | Purpose |
| --- | --- |
| `appName` | Identity written into logs |
| `ollamaUrl` | Ollama base URL |
| `llmmodel` | Model used for chat |
| `*Repository` | Elasticsearch connection settings |

## Usage

Prompt style: `KAONOMI@user:root/$`

Any non-command line is sent to Ollama. The console prints the answer and stats (tokens, duration), then saves log / performance / completion records.

| Command | Effect |
| --- | --- |
| `/model` | Shows the selected model |
| `/rocstory` | Placeholder for the ROC story isotopy batch (`Models/StoryInput`, `Models/IsotopyAnalysis`); the loader call is currently commented out |
| `/bye`, `/exit`, `/quit`, `quit()` | Exits |

## Dependencies

- `KAONOMI.Core`

## See also

- [Solution README](../README.md)
