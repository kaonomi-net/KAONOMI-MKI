# KAONOMI.Dreamer

Background host for the **Dreamer** M-A-R-I agents (collect → analysis → actions). Runs the `Dreamer` sequence every 15 minutes during the night window (22:00–06:00), outside the main chat request path.

## Run

```bash
dotnet run --project KAONOMI.Dreamer
```

Press Ctrl+C to stop the loop.

## Configuration

`config.json` next to the executable: `appName`, `ollamaUrl`, `llmmodel`, Elasticsearch repositories (`dataRepository`, `dialogRepository`, `adminRepository`).

## Agents

| Type | Role |
| --- | --- |
| Collect | Gathers dreamer-related context |
| Analysis | Interprets collected data |
| Actions | Applies updates / records performance |

Schedule constants (`NightStart`, `NightEnd`, `RunInterval`) are defined in `Program.cs`.

## Dependencies

- `KAONOMI.Core`, M-A-R-I / Orleans hosting

## See also

- [Solution README](../README.md)
- [KAONOMI.Core](../KAONOMI.Core/README.md)
