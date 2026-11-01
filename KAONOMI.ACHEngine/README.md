[www.kaonomi.net](https://www.kaonomi.net)

# KAONOMI.ACHEngine

Background host for the **ACHEngine** M-A-R-I agents (collect → analysis → actions), cloned from [KAONOMI.Vision](../KAONOMI.Vision/README.md). Runs the `achengine` sequence every second; the logic of its agents is still to be written (`TODO` in each agent).

## Run

```bash
dotnet run --project KAONOMI.ACHEngine
```

Press Ctrl+C to stop the loop.

## Configuration

`config.json` next to the executable: `appName` (`KaonomiACHEngine`), `ollamaUrl`, `llmmodel`, Elasticsearch repositories. Orleans silo port 11117, gateway port 30006.

## Agents

| Type | Role |
| --- | --- |
| Collect | Gathers the inputs / context |
| Analysis | Runs the analysis steps |
| Actions | Persists outcomes and performance metrics |

## Dependencies

- `KAONOMI.Core`, M-A-R-I / Orleans hosting

## See also

- [Solution README](../README.md)
- [KAONOMI.Core](../KAONOMI.Core/README.md)
