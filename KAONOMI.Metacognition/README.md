# KAONOMI.Metacognition

Background host for the **Metacognition** M-A-R-I agents (collect → analysis → actions). Runs the `Metacognition` sequence every second for higher-level monitoring and self-reflection style updates.

## Run

```bash
dotnet run --project KAONOMI.Metacognition
```

Press Ctrl+C to stop the loop.

## Configuration

`config.json` next to the executable: `appName`, `ollamaUrl`, `llmmodel`, Elasticsearch repositories.

## Agents

| Type | Role |
| --- | --- |
| Collect | Collects metacognitive signals / history |
| Analysis | Interprets patterns and quality of prior runs |
| Actions | Applies metacognitive updates and records performance |

## Dependencies

- `KAONOMI.Core`, M-A-R-I / Orleans hosting

## See also

- [Solution README](../README.md)
- [KAONOMI.Core](../KAONOMI.Core/README.md)
