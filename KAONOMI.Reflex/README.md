# KAONOMI.Reflex

Background host for the **Reflex** M-A-R-I agents (collect → analysis → actions). Runs the `Reflex` sequence every 100 ms for reflexive / reactive context processing.

## Run

```bash
dotnet run --project KAONOMI.Reflex
```

Press Ctrl+C to stop the loop.

## Configuration

`config.json` next to the executable: `appName`, `ollamaUrl`, `llmmodel`, Elasticsearch repositories.

## Agents

| Type | Role |
| --- | --- |
| Collect | Collects reflex-triggering context |
| Analysis | Evaluates reflex conditions |
| Actions | Executes reflex actions and logs performance |

## Dependencies

- `KAONOMI.Core`, M-A-R-I / Orleans hosting

## See also

- [Solution README](../README.md)
- [KAONOMI.Core](../KAONOMI.Core/README.md)
