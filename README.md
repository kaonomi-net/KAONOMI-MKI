<p align="center">
  <a href="https://www.kaonomi.net">
    <picture>
      <source media="(prefers-color-scheme: dark)" srcset="docs/images/kaonomi-logo-dark.png" />
      <img src="docs/images/kaonomi-logo.png" alt="Kaonomi.net, 100 % non-profit AGI's exploration, distributed AI and meta AI" width="500" />
    </picture>
  </a>
</p>

<p align="center"><a href="https://www.kaonomi.net">www.kaonomi.net</a></p>

# KAONOMI

> [!WARNING]
> **This project is a prototype.** Its purpose is to explore many different aspects of a personal AI: agents,
> models, memory, voice, interfaces, tools, security… It is a playground for experiments, not a finished product.
> At the end of the experimentation, development will start again **from scratch**, keeping what was learned here,
> not the code.

A conversational .NET platform: a REST API orchestrates a M-A-R-I agent sequence that talks to [Ollama](https://ollama.com), desktop and console clients send prompts, and dialogs, logs, performance data, and token usage are stored in Elasticsearch. Background agent hosts (Dreamer, Vision, Reflex, Metacognition, EarlyWarningSystem, ACHEngine) run scheduled sequences for context work. An optional LLM gateway in front of Ollama adds a semantic cache and routes each prompt to a model by its entities, and the immune system scans the machine for AI-generated images, code hidden in Office files and the AI services of the browser histories.

Documentation: [https://www.kaonomi.net](https://www.kaonomi.net)

## What Kaonomi is

Kaonomi is a personal AI project. It runs locally, on your own machines, but it still connects to external services
when it needs them. It will be available in several editions:

| Edition | For |
| --- | --- |
| **Kaonomi Family** | An AI for the whole family |
| **Kaonomi Family Dys/ASD** | Families with dyslexic (or other "dys") children and/or autistic members |
| **Kaonomi AI** | AI enthusiasts who dream of building their own personal JARVIS |
| **SyDE AI** | IT teams: a solution for architecture, AI, cybersecurity and knowledge bases |

Kaonomi speaks and listens through a 3D avatar whose mouth follows its voice. Each preset gives it a face, a voice and
a system prompt (`/preset` in the chat):

<p align="center">
  <img src="KAONOMI.ChatGodot/Assets/kaonomi-face.png" alt="Kaonomi" width="110" />
  <img src="KAONOMI.ChatGodot/Assets/kaonami-face.png" alt="Kaonami" width="110" />
  <img src="KAONOMI.ChatGodot/Assets/kaonomi-robot-face.png" alt="Kaonomi robot" width="110" />
  <img src="KAONOMI.ChatGodot/Assets/pepper-face.png" alt="Pepper" width="110" />
  <img src="KAONOMI.ChatGodot/Assets/circuit-face.png" alt="Circuit" width="110" />
  <img src="KAONOMI.ChatGodot/Assets/green-face.png" alt="Green" width="110" />
</p>

## Architecture

Everything runs on your machine; the dotted lines are the optional parts and the connections going outside.

```mermaid
flowchart LR
    user(["You"])

    subgraph clients["Clients"]
        chat["KAONOMI.ChatGodot<br/>chat, voice, tool windows"]
        admin["KAONOMI.Admin<br/>console REPL"]
    end

    subgraph core["Core"]
        server["KAONOMI.Server<br/>REST API :49165 / :49166<br/>M-A-R-I dialog sequence"]
        hosts["6 agent hosts<br/>Dreamer · Vision · Reflex<br/>Metacognition · EarlyWarningSystem · ACHEngine"]
    end

    subgraph data["Storage"]
        search[("KAONOMI.SearchEngine<br/>or Elasticsearch :9200<br/>dialogs, logs, tokens, documents")]
        mongo[("KAONOMI.MongoEngine<br/>or MongoDB :27017<br/>MARI cluster membership")]
    end

    subgraph llm["Models"]
        gateway["KAONOMI.LLMGateway :11435<br/>semantic cache, model router"]
        ollama["Ollama :11434<br/>gpt-oss, mistral-small…"]
    end

    immune["KAONOMI.ImmuneSystem<br/>one-shot scan, JSON report"]
    outside(["Web, Fooocus,<br/>Whisper, Piper"])

    user --> chat
    user --> admin
    chat -- "HTTPS" --> server
    admin --> ollama
    server <-- "MARI / Orleans cluster" --> hosts
    server --> search
    hosts --> search
    server --> mongo
    hosts --> mongo
    server --> gateway
    hosts --> gateway
    gateway --> ollama
    chat -. "/gateway" .-> gateway
    chat -. "/immune" .-> immune
    chat -. "scraper, images, speech" .-> outside
```

Each prompt sent to `POST /aiassistant` is saved, then goes through the 13 virtual agents of the dialog sequence `jfo`
(`KAONOMI.Server/Program.cs`), one after the other:

```mermaid
flowchart TB
    subgraph s1["1 · understand"]
        direction LR
        in(["prompt"]) --> g1["Inbound<br/>guardrail"] --> pre["Pre-<br/>processing"] --> cache["Cache"] --> ent["Entities"]
    end
    subgraph s2["2 · decide and answer"]
        direction LR
        sym["Symbolic<br/>analysis"] --> fval["Forecasting<br/>validator"] --> sel["Model<br/>selection"] --> proc["Processor<br/>(the LLM)"]
    end
    subgraph s3["3 · refine and remember"]
        direction LR
        post["Post-<br/>processing"] --> emo["Emotion<br/>engine"] --> fc["Forecasting"] --> ctx["Context<br/>update"] --> g2["Outbound<br/>guardrail"] --> out(["completion"])
    end
    s1 --> s2 --> s3
```

## Prerequisites

Every prerequisite and how to install it: [PREREQUISITES.md](PREREQUISITES.md) (English), [PREREQUIS.md](PREREQUIS.md) (français). In short:

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Ollama](https://ollama.com) running locally (`http://localhost:11434`) with the configured models (`gpt-oss:20b`, `mistral-small3.2` for the vision and the AI tools of the text areas)
- Elasticsearch 7.14 running locally (`http://localhost:9200`), or [`KAONOMI.SearchEngine`](KAONOMI.SearchEngine/README.md) in its place (its data in `~/kaonomi/data`)
- MongoDB 8 running locally (`mongodb://localhost:27017`, the Orleans cluster membership of MARI), or [`KAONOMI.MongoEngine`](KAONOMI.MongoEngine/README.md) in its place
- For the chat: Godot 4.7.2 .NET; optional: Fooocus (images), Whisper (speech to text), Python and Piper (text to speech)
- The MARI packages (`MARI.*` 1.0.0, [kaonomi-net/mari](https://github.com/kaonomi-net/mari)) come from nuget.org, like every other package: `nuget.config` has no other source
- Optional, for the NER of the server (`POST /ner`): the XLM-RoBERTa int8 model in `~/kaonomi/models/ner/xlm-roberta-base-ner-hrl-int8`, made once by `KAONOMI.Server/Ner/export/ner_export.py` (see [`KAONOMI.Server/README.md`](KAONOMI.Server/README.md)); without it, the rules and the ontology still answer
- Optional, for the semantic cache of the LLM gateway: `ollama pull nomic-embed-text` (without it the gateway uses a local embedding of the words)

## Solution

| Project | Role |
| --- | --- |
| [`KAONOMI.Core`](KAONOMI.Core/README.md) | Shared models (incl. `MessageFlow`), Ollama helpers, Elasticsearch access (`NEST`), session manager |
| [`KAONOMI.Server`](KAONOMI.Server/README.md) | ASP.NET Core API, M-A-R-I dialog sequence, Swagger, message-flow, cache & symbolic-pattern APIs, NER (`POST /ner`: XLM-RoBERTa int8, rules, ontology) |
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
| [`KAONOMI.ChatGodot`](KAONOMI.ChatGodot/README.md) | The Kaonomi chat (Godot 4.7 .NET, EVE Online look): the chat, its voice mode, its desktop of tool windows, the journal of the activities, `/think`, the games (`/game:solitaire`, `/game:chess`, `/game:go`), `/fractal`, `/immune`, `/gateway` |
| [`KAONOMI.Chat`](KAONOMI.Chat/README.md) | The same chat in Avalonia, aligned on the Godot chat: the same view models, services, commands and tool windows (linked from `KAONOMI.ChatGodot/Shared`), with the full chat on a vertical screen when one is connected |

Solution file: `KAONOMI.slnx`.

## Getting started

The start order:

```mermaid
flowchart LR
    build["dotnet build"] --> engines["MongoEngine<br/>SearchEngine"] --> hosts["the 6 agent hosts"] --> server["Server"] --> chat["ChatGodot"]
    engines -. "optional" .-> gateway["LLMGateway"] -.-> server
```

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
dotnet run --project KAONOMI.Chat             # or the chat in Avalonia (no Godot needed)
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
| `/fractal` | Fractal generator on the GPU: escape time (Mandelbrot, Julia, Burning Ship, Newton…, or your own formula of z and c), L-systems, IFS; palettes, zoom box, Julia of a point, SAVE as JSON, PNG |
| `/immune` | Runs `KAONOMI.ImmuneSystem` from the chat: the scan animated as an immune cell, live counters and findings, then the report (overview charts, AI images, Office code, browsing) and the reports kept |
| `/alarm` | Alarm clock: several alarms, once on a day or weekly on the days ticked (every day, Mon–Fri, weekend; one week in 1 to 4), 4 ringtones made by the code, snooze, the label said by the avatar; they ring even with the window closed (STOP / SNOOZE) |
| `/iq` | IQ tests drawn at random from a bank of 1000 questions in JSON (`KAONOMI.ChatGodot/Data/iq-questions.json`): number series, matrices of shapes (in the manner of Raven's), words (FR / EN), memory of digits, 10 questions each harder and harder or a full test of 24, 60 s a question; a **training** mode where the time stops at each answer and the right one is shown and explained; an indicative score (not a measured IQ) and the review of every answer |
| `/calendar` | Calendar: the month with each day's events and alarms, events once or repeated (daily, weekly, monthly, yearly), all day or with a time, colours, notes, and reminders rung like the alarms, the title said by the avatar |
| `/gateway` | Drives `KAONOMI.LLMGateway`: cache and routing settings, the rules, the last requests (model asked → chosen, HIT / MISS), the cached answers and phrases (export, import, search), ANALYZE |
| `/collectionplan` 🕷 RUN | Carries out a collection plan: the web scraper reads the sites (fixed, explore, random; robots.txt followed), pictures each page, the vision model reads the pictures, the chat model applies the instructions with the sources cited; the result opens as a `/work` document |
| Voice mode | A command line above the microphone (prompts and `/tools` typed there), the windows shown one at a time at 90 % of the screen (◀ ▶ between them, ⏬ puts them in a row at the bottom), the desktop back as it was afterwards |

`/ngame:chess`, `/ngame:go` and `/nimmune` open the same tool in its own window.

## Main API

| Method | Route | Description |
| --- | --- | --- |
| `POST` | `/aiassistant` | Saves the dialog, runs the M-A-R-I sequence, returns the completion |
| `POST` | `/ner` | The entities of a text: persons, organizations, places, dates (XLM-RoBERTa int8 in ONNX), URLs, e-mails, amounts, code… (rules) and the names of the ontology with their id; `GET /ner/types` lists the types |
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

The path of a prompt through the gateway:

```mermaid
flowchart TD
    req(["request: /api/chat, /api/generate,<br/>/v1/chat/completions"]) --> ent["entities of the last prompt<br/>CODE, MATH, URL, LANGUAGE, PERSON…"]
    ent --> route{"a rule matches?<br/>by priority, model installed"}
    route -- "yes" --> model["the model of the rule"]
    route -- "no" --> def["the model asked,<br/>or defaultModel for auto"]
    model --> look{"close prompt in the cache?<br/>same model, context, entities"}
    def --> look
    look -- "HIT: minAnswers kept" --> hit(["one of the kept answers,<br/>never the last one given"])
    look -- "MISS" --> ollama["Ollama"] --> keep["answer kept in the cache"] --> resp(["answer"])
```

## License

MIT. See `LICENSE.txt`.

## Screenshots

The windows of the chat (`KAONOMI.ChatGodot`, light theme). Each tool opens in the desktop between the bar of commands
on the left and the chat on the right; here every one is shown full size.

### Knowledge and writing

<table>
  <tr>
    <td width="50%"><img src="docs/screenshots/ontology.jpg" alt="/ontology" /><br /><sub><b><code>/ontology</code></b>: the ontology tree, its entity types and nodes</sub></td>
    <td width="50%"><img src="docs/screenshots/relation.jpg" alt="/relation" /><br /><sub><b><code>/relation</code></b>: the graph of the relations between the entities</sub></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/journal.jpg" alt="/journal" /><br /><sub><b><code>/journal</code></b>: the journal, its ribbon and its feelings</sub></td>
    <td><img src="docs/screenshots/idea.jpg" alt="/idea" /><br /><sub><b><code>/idea</code></b>: the ideas, the latest ones and their editor</sub></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/project.jpg" alt="/kanban" /><br /><sub><b><code>/kanban</code></b>: the project board, Todo to Archived</sub></td>
    <td><img src="docs/screenshots/completion.jpg" alt="/completion" /><br /><sub><b><code>/completion</code></b>: the completions, their filters by model, language and topic</sub></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/wiki.jpg" alt="/wiki" /><br /><sub><b><code>/wiki</code></b>: the wiki of Kaonomi and its links between pages</sub></td>
    <td><img src="docs/screenshots/search.jpg" alt="/search" /><br /><sub><b><code>/search</code></b>: one search in every index of Kaonomi</sub></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/collectionplan.jpg" alt="/collectionplan" /><br /><sub><b><code>/collectionplan</code></b>: the collection plans of the web scraper</sub></td>
    <td></td>
  </tr>
</table>

### Diagrams

<table>
  <tr>
    <td width="50%"><img src="docs/screenshots/bpmnai.jpg" alt="/bpmnai" /><br /><sub><b><code>/bpmnai</code></b>: the editor of AI processes (BPMN 2.0, agents, LLM, RAG, MCP…)</sub></td>
    <td width="50%"><img src="docs/screenshots/infra.jpg" alt="/infra" /><br /><sub><b><code>/infra</code></b>: the editor of infrastructure diagrams</sub></td>
  </tr>
</table>

### Games and fractals

<table>
  <tr>
    <td width="50%"><img src="docs/screenshots/game-chess.jpg" alt="/game:chess" /><br /><sub><b><code>/game:chess</code></b>: chess against the LLM</sub></td>
    <td width="50%"><img src="docs/screenshots/game-go.jpg" alt="/game:go" /><br /><sub><b><code>/game:go</code></b>: go against the LLM, 9×9 to 19×19</sub></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/game-solitaire.jpg" alt="/game:solitaire" /><br /><sub><b><code>/game:solitaire</code></b>: the morpion solitaire and its scores</sub></td>
    <td><img src="docs/screenshots/fractal.jpg" alt="/fractal" /><br /><sub><b><code>/fractal</code></b>: the fractal generator on the GPU</sub></td>
  </tr>
</table>

### Models, avatar and monitoring

<table>
  <tr>
    <td width="50%"><img src="docs/screenshots/gateway.jpg" alt="/gateway" /><br /><sub><b><code>/gateway</code></b>: the LLM gateway, its semantic cache and its router</sub></td>
    <td width="50%"><img src="docs/screenshots/preset.jpg" alt="/preset" /><br /><sub><b><code>/preset</code></b>: the presets of the avatar: voice, 3D animation, system prompt</sub></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/process.jpg" alt="/process" /><br /><sub><b><code>/process</code></b>: the processes of Kaonomi, START / STOP / RESTART</sub></td>
    <td><img src="docs/screenshots/system.jpg" alt="/system" /><br /><sub><b><code>/system</code></b>: CPU, memory, GPU, VRAM, network and drives</sub></td>
  </tr>
  <tr>
    <td><img src="docs/screenshots/log.jpg" alt="/log" /><br /><sub><b><code>/log</code></b>: the logs of every application</sub></td>
    <td><img src="docs/screenshots/token.jpg" alt="/token" /><br /><sub><b><code>/token</code></b>: the tokens used, by model and by application</sub></td>
  </tr>
</table>
