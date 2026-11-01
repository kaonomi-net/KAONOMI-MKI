[www.kaonomi.net](https://www.kaonomi.net)

# KAONOMI

A conversational .NET platform: a REST API orchestrates a M-A-R-I agent sequence that talks to [Ollama](https://ollama.com), desktop and console clients send prompts, and dialogs, logs, performance data, and token usage are stored in Elasticsearch. Background agent hosts (Dreamer, Vision, Reflex, Metacognition, EarlyWarningSystem, ACHEngine) run scheduled sequences for context work.

Documentation: [https://www.kaonomi.net](https://www.kaonomi.net)

## Prerequisites

Every prerequisite and how to install it: [PREREQUISITES.md](PREREQUISITES.md) (English), [PREREQUIS.md](PREREQUIS.md) (français). In short:

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Ollama](https://ollama.com) running locally (`http://localhost:11434`) with the configured models (`gpt-oss:20b`, `mistral-small3.2` for the vision and the AI tools of the text areas)
- Elasticsearch 7.14 running locally (`http://localhost:9200`), or [`KAONOMI.SearchEngine`](KAONOMI.SearchEngine/README.md) in its place (its data in `~/kaonomi/data`)
- MongoDB 8 running locally (`mongodb://localhost:27017`, the Orleans cluster membership of MARI), or [`KAONOMI.MongoEngine`](KAONOMI.MongoEngine/README.md) in its place
- For the chat: Godot 4.7.2 .NET; optional: Fooocus (images), Whisper (speech to text), Python and Piper (text to speech)

## Solution

| Project | Role |
| --- | --- |
| [`KAONOMI.Core`](KAONOMI.Core/README.md) | Shared models (incl. `MessageFlow`), Ollama helpers, Elasticsearch access (`NEST`), session manager |
| [`KAONOMI.Server`](KAONOMI.Server/README.md) | ASP.NET Core API, M-A-R-I dialog sequence, Swagger, message-flow, cache & symbolic-pattern APIs |
| [`KAONOMI.Admin`](KAONOMI.Admin/README.md) | Interactive console REPL for Ollama |
| [`KAONOMI.Dreamer`](KAONOMI.Dreamer/README.md) | Background dreamer / context-update agent host (every 15 min, 22:00–06:00) |
| [`KAONOMI.Vision`](KAONOMI.Vision/README.md) | Background vision analysis agent host (every second) |
| [`KAONOMI.EarlyWarningSystem`](KAONOMI.EarlyWarningSystem/README.md) | Background early warning system agent host (every second; cloned from Vision, agents to write) |
| [`KAONOMI.ACHEngine`](KAONOMI.ACHEngine/README.md) | Background ACH engine agent host (every second; cloned from Vision, agents to write) |
| [`KAONOMI.Reflex`](KAONOMI.Reflex/README.md) | Background reflex agent host (every 100 ms) |
| [`KAONOMI.Metacognition`](KAONOMI.Metacognition/README.md) | Background metacognition agent host (every second) |
| [`KAONOMI.SearchEngine`](KAONOMI.SearchEngine/README.md) | Small Elasticsearch 7.14 compatible engine (one node, Lucene.Net): what KAONOMI uses of Elasticsearch, YAML configuration |
| `KAONOMI.SearchEngine.Tests` | Tests of the engine through the repository of KAONOMI (NEST), compared with a real Elasticsearch when one is running |
| [`KAONOMI.MongoEngine`](KAONOMI.MongoEngine/README.md) | Small MongoDB compatible server (wire protocol, standalone) for MARI and the Orleans cluster membership, YAML configuration |
| `KAONOMI.MongoEngine.Tests` | Tests of the MongoDB engine with the official driver and real Orleans silos |
| [`KAONOMI.ChatGodot`](KAONOMI.ChatGodot/README.md) | The Kaonomi chat (Godot 4.7 .NET, EVE Online look): the chat, its desktop of tool windows, the journal of the activities, `/think`, `/game1` |

Solution file: `KAONOMI.slnx`.

## Getting started

```bash
dotnet build KAONOMI.slnx
dotnet run --project KAONOMI.MongoEngine      # or MongoDB
dotnet run --project KAONOMI.SearchEngine     # or Elasticsearch
dotnet run --project KAONOMI.Dreamer          # the 6 agent hosts, before the server:
dotnet run --project KAONOMI.Vision           #   Dreamer, Vision, Reflex, Metacognition,
dotnet run --project KAONOMI.Reflex           #   EarlyWarningSystem, ACHEngine
dotnet run --project KAONOMI.Metacognition
dotnet run --project KAONOMI.EarlyWarningSystem
dotnet run --project KAONOMI.ACHEngine
dotnet run --project KAONOMI.Server
```

The server listens on `https://localhost:49165` and `http://localhost:49166`. The root path redirects to Swagger.
Start the 6 agent hosts before (or with) the server: the dialog sequence needs their agents, and the server refuses to
start while hosts stopped a few minutes ago are still registered as alive in the cluster.

Each executable reads configuration next to the binary (`config.json`, and for Chat also `config.yml`). Copy or run from the project folder so those files are available in the output directory.

The console applications (Server, Admin, Dreamer, Vision, Reflex, Metacognition, EarlyWarningSystem, ACHEngine) get their look from `consoleTheme` in their config.json (`HelperConsoleTheme` in Core):

| `consoleTheme` | Look |
| --- | --- |
| `matrix` | Black screen and phosphor green text, digital rain intro ("Wake up..."), KAONOMI banner, timestamped status lines |
| `jarvis` | Iron Man / J.A.R.V.I.S. HUD: navy screen, arc reactor blue text with gold accents, arc reactor power-up animation, system diagnostics with progress bars, greeting, `JARVIS ▸` prompt |
| `ubuntu` | Ubuntu terminal: aubergine screen, Ubuntu orange accents, boot splash with the loading dots, systemd `[  OK  ]` boot messages, message-of-the-day banner, green/blue `user@host:~/path$` prompt, `HH:mm:ss [ INFO ]` status lines |
| anything else | Classic console (plain text, default colors) |

Themed consoles are 30 % transparent. Press any key to skip the intro, or set the environment variable `KAONOMI_NO_INTRO` to turn it off; it is also skipped when the output is redirected.

On a PC with several screens, the background hosts open on the smallest screen (by surface), each in one cell of a 3x2 grid (3 columns, 2 rows): Dreamer, Metacognition and Reflex on the top row, Vision, EarlyWarningSystem and ACHEngine on the bottom row.

## Main API

| Method | Route | Description |
| --- | --- | --- |
| `POST` | `/aiassistant` | Saves the dialog, runs the M-A-R-I sequence, returns the completion |
| `GET` | `/version` | Current version string |
| `GET` / `POST` / `PUT` | `/messageflow…` | Ticker message CRUD and search (shown in the Chat ticker band) |
| `GET` / `POST` / `PUT` | `/cache…` | Cache document CRUD and search |
| `GET` / `POST` / `PUT` | `/symbolicpattern…` | Symbolic pattern CRUD and search |
| `GET` | `/tokensusage/last?minutes=N` | Token usages (model, prompt and completion tokens per LLM call) of the last N minutes, newest first (default 60) |
| `GET` | `/log/since?minutes=N` | All the logs of the last N minutes (every application), newest first, without the 10,000 limit (default 60) |
| `GET` | `/log/last?size=N` | The N most recent logs of every application, newest first (default and maximum 10,000) |
| `DELETE` | `/log` | Erases ALL the logs (the index of the logs, created again at the next log) |
| `GET` / `POST` / `PUT` / `DELETE` | `/intel…`, `/collectionplan…` | Intels and collection plans of the web scraper: CRUD, search, last (a delete only marks them deleted) |
| `PUT` / `POST` | `/gamescore`, `/gamescore/best` | The scores of `/game1`, and the best ones of a game |
| `PUT` / `POST` | `/useractivity`, `/useractivity/last`, `/useractivity/search` | The journal of the activities of the chat (index `useractivities`) |
| `GET` | `/` | Redirects to Swagger |

See [`KAONOMI.Server/README.md`](KAONOMI.Server/README.md) for full route details.

## License

MIT. See `LICENSE.txt`.
