[www.kaonomi.net](https://www.kaonomi.net)

# KAONOMI

A conversational .NET platform: a REST API orchestrates a M-A-R-I agent sequence that talks to [Ollama](https://ollama.com), desktop and console clients send prompts, and dialogs, logs, performance data, and token usage are stored in Elasticsearch. Background agent hosts (Dreamer, Vision, Reflex, Metacognition, EarlyWarningSystem, ACHEngine) run scheduled sequences for context work. An optional LLM gateway in front of Ollama adds a semantic cache and routes each prompt to a model by its entities, and the immune system scans the machine for AI-generated images, code hidden in Office files and the AI services of the browser histories.

Documentation: [https://www.kaonomi.net](https://www.kaonomi.net)

## Prerequisites

Every prerequisite and how to install it: [PREREQUISITES.md](PREREQUISITES.md) (English), [PREREQUIS.md](PREREQUIS.md) (français). In short:

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Ollama](https://ollama.com) running locally (`http://localhost:11434`) with the configured models (`gpt-oss:20b`, `mistral-small3.2` for the vision and the AI tools of the text areas)
- Elasticsearch 7.14 running locally (`http://localhost:9200`), or [`KAONOMI.SearchEngine`](KAONOMI.SearchEngine/README.md) in its place (its data in `~/kaonomi/data`)
- MongoDB 8 running locally (`mongodb://localhost:27017`, the Orleans cluster membership of MARI), or [`KAONOMI.MongoEngine`](KAONOMI.MongoEngine/README.md) in its place
- For the chat: Godot 4.7.2 .NET; optional: Fooocus (images), Whisper (speech to text), Python and Piper (text to speech)
- The MARI packages (`MARI.*` 1.0.0) in `mari-nupkgs/`, the local NuGet source of `nuget.config` (relative to it): see [mari-nupkgs/README.md](mari-nupkgs/README.md)
- Optional, for the semantic cache of the LLM gateway: `ollama pull nomic-embed-text` (without it the gateway uses a local embedding of the words)

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
| [`KAONOMI.LLMGateway`](KAONOMI.LLMGateway/README.md) | Gateway in front of Ollama (`http://localhost:11435`): Ollama and OpenAI APIs, semantic cache, router of the models by the entities of the prompt, export / import of the cached phrases, admin API `/gateway/…` |
| `KAONOMI.LLMGateway.Tests` | Tests of the gateway: cache, entity extractor, router, end to end |
| [`KAONOMI.ImmuneSystem`](KAONOMI.ImmuneSystem/README.md) | Console scanner (self-contained, read only, no network): AI-generated or AI-edited images (C2PA, IPTC, generator parameters), code in Office files (VBA, Excel 4.0 macros, DDE, OLE, Power Query…), browser histories (AI services visited, files downloaded); one JSON report |
| [`KAONOMI.ChatGodot`](KAONOMI.ChatGodot/README.md) | The Kaonomi chat (Godot 4.7 .NET, EVE Online look): the chat, its voice mode, its desktop of tool windows, the journal of the activities, `/think`, the games (`/game:solitaire`, `/game:chess`, `/game:go`, `/game:marie`), `/fractal`, `/immune`, `/gateway` |

Solution file: `KAONOMI.slnx`.

## Getting started

```bash
dotnet build KAONOMI.slnx
dotnet run --project KAONOMI.MongoEngine      # or MongoDB
dotnet run --project KAONOMI.SearchEngine     # or Elasticsearch
cd KAONOMI.Dreamer && dotnet run              # the 6 agent hosts, before the server, each from its own folder:
cd KAONOMI.Vision && dotnet run               #   Dreamer, Vision, Reflex, Metacognition,
cd KAONOMI.Reflex && dotnet run               #   EarlyWarningSystem, ACHEngine
cd KAONOMI.Metacognition && dotnet run
cd KAONOMI.EarlyWarningSystem && dotnet run
cd KAONOMI.ACHEngine && dotnet run
cd KAONOMI.Server && dotnet run
cd KAONOMI.LLMGateway && dotnet run           # optional: the gateway, http://localhost:11435 → Ollama
cd KAONOMI.ChatGodot && godot-mono --path .   # the chat (the first time: godot-mono --headless --path . --import)
```

The server listens on `https://localhost:49165` and `http://localhost:49166`. The root path redirects to Swagger.
Start the 6 agent hosts before (or with) the server: the dialog sequence needs their agents, and the server refuses to
start while hosts stopped a few minutes ago are still registered as alive in the cluster. From the chat, `/process`
starts, stops and restarts all of them (START CORE, STOP ALL), and `/killall` stops everything at once.

Each executable reads its configuration (`config.json`, and for Chat also `config.yml`) from the current folder: start
the agent hosts and the server from their project folder, as above (from the root of the repository they stop at once
on `Could not find file '…\config.json'`).

To send the prompts of the server and of the agents through the gateway, set `"ollamaUrl": "http://localhost:11435"`
in their `config.json`; the chat finds the gateway with `llmGatewayUrl` in its `config.yml`.

The immune system is a one-shot scan, not a service:

```bash
dotnet run --project KAONOMI.ImmuneSystem                 # the home folder; --machine for the whole machine
dotnet run --project KAONOMI.ImmuneSystem -- ~/Downloads --out report.json
```

When the repository sits on a drive also written by a Mac, macOS leaves `._*` files (AppleDouble metadata) next to the
sources, and the C# compiler stops on them (`CS2015: '…\._File.cs' is a binary file`): delete them before building.

The console applications (Server, Admin, Dreamer, Vision, Reflex, Metacognition, EarlyWarningSystem, ACHEngine) get their look from `consoleTheme` in their config.json (`HelperConsoleTheme` in Core):

| `consoleTheme` | Look |
| --- | --- |
| `matrix` | Black screen and phosphor green text, digital rain intro ("Wake up..."), KAONOMI banner, timestamped status lines |
| `jarvis` | Iron Man / J.A.R.V.I.S. HUD: navy screen, arc reactor blue text with gold accents, arc reactor power-up animation, system diagnostics with progress bars, greeting, `JARVIS ▸` prompt |
| `ubuntu` | Ubuntu terminal: aubergine screen, Ubuntu orange accents, boot splash with the loading dots, systemd `[  OK  ]` boot messages, message-of-the-day banner, green/blue `user@host:~/path$` prompt, `HH:mm:ss [ INFO ]` status lines |
| anything else | Classic console (plain text, default colors) |

Themed consoles are 30 % transparent. Press any key to skip the intro, or set the environment variable `KAONOMI_NO_INTRO` to turn it off; it is also skipped when the output is redirected.

On a PC with several screens, the background hosts open on the smallest screen (by surface), each in one cell of a 3x2 grid (3 columns, 2 rows): Dreamer, Metacognition and Reflex on the top row, Vision, EarlyWarningSystem and ACHEngine on the bottom row.

## What's new in the chat

Every tool is described in [`KAONOMI.ChatGodot/README.md`](KAONOMI.ChatGodot/README.md); the latest ones:

| Command | What |
| --- | --- |
| `/game:chess` | Chess against the LLM (you play White): every rule (castling, en passant, promotion, fifty moves, threefold repetition…), the model held to its legal moves by the structured output of Ollama, its comment for each move; UNDO, RESIGN, PGN |
| `/game:go` | Go against the LLM (you play Black) on 9×9, 13×13 or 19×19: Chinese rules, positional superko, dead stones marked at the end, COUNT with komi 7.5; UNDO, RESIGN, SGF |
| `/game:marie` | A first-person maze of 25×25 cells where MarieKilleuse hunts you: life, speed bonuses, three weapons, a map, the 10 best escapes kept |
| `/fractal` | Fractal generator on the GPU: escape time (Mandelbrot, Julia, Burning Ship, Newton…, or your own formula of z and c), L-systems, IFS; palettes, zoom box, Julia of a point, SAVE as JSON, PNG |
| `/immune` | Runs `KAONOMI.ImmuneSystem` from the chat: the scan animated as an immune cell, live counters and findings, then the report (overview charts, AI images, Office code, browsing) and the reports kept |
| `/gateway` | Drives `KAONOMI.LLMGateway`: cache and routing settings, the rules, the last requests (model asked → chosen, HIT / MISS), the cached answers and phrases (export, import, search), ANALYZE |
| `/collectionplan` 🕷 RUN | Carries out a collection plan: the web scraper reads the sites (fixed, explore, random; robots.txt followed), pictures each page, the vision model reads the pictures, the chat model applies the instructions with the sources cited; the result opens as a `/work` document |
| Voice mode | A command line above the microphone (prompts and `/tools` typed there), the windows shown one at a time at 90 % of the screen (◀ ▶ between them, ⏬ puts them in a row at the bottom), the desktop back as it was afterwards |

`/ngame:chess`, `/ngame:go`, `/ngame:marie` and `/nimmune` open the same tool in its own window.

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
| `PUT` / `POST` | `/gamescore`, `/gamescore/best` | The scores of `/game:solitaire` (game `game1`), and the best ones of a game |
| `PUT` / `POST` | `/useractivity`, `/useractivity/last`, `/useractivity/search` | The journal of the activities of the chat (index `useractivities`) |
| `GET` | `/` | Redirects to Swagger |

See [`KAONOMI.Server/README.md`](KAONOMI.Server/README.md) for full route details.

The gateway (`http://localhost:11435`) speaks the API of Ollama (`/api/chat`, `/api/generate`, the others passed
through) and of OpenAI (`/v1/chat/completions`), and is driven by `/gateway/status`, `/gateway/settings`,
`/gateway/rules`, `/gateway/cache…` (with `/gateway/cache/export` and `/gateway/cache/import`), `/gateway/log` and
`/gateway/analyze`: see [`KAONOMI.LLMGateway/README.md`](KAONOMI.LLMGateway/README.md). Each answer says what was done in
its `X-Gateway-Model`, `X-Gateway-Rule`, `X-Gateway-Cache` and `X-Gateway-Score` headers.

## License

MIT. See `LICENSE.txt`.
