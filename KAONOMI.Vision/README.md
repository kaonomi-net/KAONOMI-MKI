# KAONOMI.Vision

Background host for the **Vision** M-A-R-I agents (collect → analysis → actions). Runs the `Vision` sequence every second for vision-oriented context work.

## Run

```bash
dotnet run --project KAONOMI.Vision
```

Press Ctrl+C to stop the loop.

## Configuration

`config.json` next to the executable: `appName`, `ollamaUrl`, `llmmodel`, Elasticsearch repositories.

## Agents

| Type | Role |
| --- | --- |
| Collect | Gathers vision-related inputs / context |
| Analysis | Runs vision analysis steps |
| Actions | Persists outcomes and performance metrics |

## Dependencies

- `KAONOMI.Core`, M-A-R-I / Orleans hosting

## See also

- [Solution README](../README.md)
- [KAONOMI.Core](../KAONOMI.Core/README.md)
