[www.kaonomi.net](https://www.kaonomi.net)

# KAONOMI — Prerequisites

Everything KAONOMI needs to run, and how to install it. For the search engine and the database you have the choice:
install the real product, or use the engine shipped with KAONOMI (nothing to install). The chat checks most of this
list itself: type `/setup` in the chat to see what is missing (it can download or install some parts for you).

French version: [PREREQUIS.md](PREREQUIS.md).

| Part | Needed for | Option A (install) | Option B (built in) |
|---|---|---|---|
| .NET 8 SDK | everything | required | — |
| Search engine | dialogs, logs, ideas, intels, wiki, scores… | Elasticsearch 7.14 | `KAONOMI.SearchEngine` |
| Database | the Orleans cluster (membership of the 7 hosts) | MongoDB 8 | `KAONOMI.MongoEngine` |
| LLM | the chat, the agents, translations, analyses | Ollama + models | — |
| Images | `/fooocus` (optional) | Fooocus + its API | — |
| Speech to text (STT) | the microphone of the chat | Whisper model | — |
| Text to speech (TTS) | the voice of the chat | Python + Piper + voices | — |
| Chat | the desktop client | Godot 4.7.2 .NET | — |

All the services listen on the local machine only. Ports used: 9200 (search engine), 27017 (database), 11434 (Ollama),
8888 (Fooocus API), 49165/49166 (KAONOMI.Server), 11111–11117 and 30000–30006 (Orleans hosts).

---

## 1. .NET 8 SDK (required)

All the projects target `net8.0`.

- Download: <https://dotnet.microsoft.com/download/dotnet/8.0>
- Check: `dotnet --version` (8.0.x or newer SDK).
- Build everything from the `kaonomi` folder: `dotnet build KAONOMI.slnx`

---

## 2. Search engine: Elasticsearch 7.14 **or** KAONOMI.SearchEngine

KAONOMI stores its documents (dialogs, logs, ideas, intels, collection plans, game scores…) in an Elasticsearch-like
search engine, through the NEST 7.17 client, at `http://localhost:9200`.

### Option B — KAONOMI.SearchEngine (recommended, nothing to install)

A small search engine compatible with the REST API of Elasticsearch 7.14, built in .NET (Lucene).

- Start it: `dotnet run --project KAONOMI.SearchEngine`
- It listens on `127.0.0.1:9200` and stores its indices in `~/kaonomi/data`
  (`C:\Users\<user>\kaonomi\data` on Windows, `/Users/<user>/kaonomi/data` on macOS, `/home/<user>/kaonomi/data` on Linux),
  created at the first start.
- Settings: `KAONOMI.SearchEngine/config/searchengine.yml` (port, data folder, security, refresh…).
- The indices are created on the first write: there is no schema to prepare.
- To copy the data of an existing Elasticsearch into it (the engine stopped):
  `dotnet run --project KAONOMI.SearchEngine -- import --from http://localhost:9200 [--user u --password p]`

### Option A — Elasticsearch 7.14

1. Download Elasticsearch **7.14.x** (past releases): <https://www.elastic.co/downloads/past-releases#elasticsearch>
2. Unzip it, then in `config/elasticsearch.yml` set a single node on this machine:
   ```yaml
   discovery.type: single-node
   network.host: 127.0.0.1
   http.port: 9200
   xpack.security.enabled: false   # or keep it on and give the user and password below
   ```
3. Start it: `bin/elasticsearch` (`bin\elasticsearch.bat` on Windows). Check: <http://localhost:9200>
4. Do **not** start `KAONOMI.SearchEngine` at the same time (both use port 9200).
5. With the security on, give the user and the password in the `config.json` of each project
   (`KAONOMI.Server`, the 6 agent hosts, `KAONOMI.Admin`), in `dataRepository`, `dialogRepository` and `adminRepository`:
   ```json
   "dataRepository": { "Url": "http://localhost:9200", "User": "elastic", "Password": "…" }
   ```
   The chat reaches the search engine at `searchEngineUrl` of `KAONOMI.ChatGodot/config.yml`.

---

## 3. Database: MongoDB 8 **or** KAONOMI.MongoEngine

The 7 hosts of KAONOMI (the server and the 6 agent hosts) form an Orleans cluster whose membership table is in
MongoDB, database `kaonomi`, at `mongodb://localhost:27017`. Nothing else of KAONOMI is stored there.

### Option B — KAONOMI.MongoEngine (recommended, nothing to install)

A small server speaking the wire protocol of MongoDB, made for what Orleans needs.

- Start it: `dotnet run --project KAONOMI.MongoEngine`
- It listens on `127.0.0.1:27017`; its data is in `KAONOMI.MongoEngine/data` (settings: `config/mongoengine.yml`).

### Option A — MongoDB 8

1. Download MongoDB Community Server 8: <https://www.mongodb.com/try/download/community>
   (macOS: `brew tap mongodb/brew && brew install mongodb-community@8.0`).
2. Start `mongod` on `127.0.0.1:27017` (the default). Check: `mongosh --eval "db.runCommand({ ping: 1 })"`
3. Do **not** start `KAONOMI.MongoEngine` at the same time (both use port 27017).
4. No database to create: the hosts write their membership table when they start.

---

## 4. Ollama and the models (required)

The LLM of the chat, of the agent sequence of the server and of the AI tools (translation, analyses, image analysis).

1. Install Ollama: <https://ollama.com/download> — it listens on `http://localhost:11434`.
2. Pull the models:
   ```bash
   ollama pull gpt-oss:20b          # the model of the server (llmmodel in KAONOMI.Server/config.json)
   ollama pull mistral-small3.2     # the vision model: image analysis, AI review, the text menu (visionmodel in config.yml)
   ```
   Any other chat model can be used: it is chosen in the chat (`llmmodel` / `model` in the config files).
3. Check: `ollama list`

The models need a good GPU or a Mac with Apple Silicon (about 13 GB for `gpt-oss:20b`, 14 GB for `mistral-small3.2`).

### LM Studio in place of Ollama

KAONOMI also speaks to [LM Studio](https://lmstudio.ai) through its OpenAI-compatible API (`/v1/models`, `/v1/chat/completions`).

1. In LM Studio, download a chat model and a vision model, then start the local server (the *Developer* tab, or
   `lms server start`) — it listens on `http://localhost:1234`.
2. In `KAONOMI.Server/config.json` (and `KAONOMI.Admin/config.json`):
   ```json
   "ollamaUrl": "http://localhost:1234",
   "llmProvider": "lmstudio",
   "llmmodel": "openai/gpt-oss-20b"
   ```
3. In `KAONOMI.ChatGodot/config.yml`: `ollamaUrl: http://localhost:1234`, `llmProvider: lmstudio`, and `visionmodel`
   with the identifier of the vision model; `llmmodel` in `KAONOMI.ChatGodot/config.json`.
4. With the gateway: `"upstream": "http://localhost:1234"` and `"upstreamProvider": "lmstudio"` in
   `KAONOMI.LLMGateway/config/llmgateway.json` (the models of the rules become those of LM Studio); its clients keep
   `llmProvider: lmstudio` and point `ollamaUrl` to `http://localhost:11435`.

The models are named by their LM Studio identifier (the one of `GET http://localhost:1234/v1/models`). The context
length is set on the model in LM Studio, not in a request: allow at least 16,000 tokens for the analysis of pages.

---

## 5. Fooocus — images (optional)

The images of `/fooocus` (the chat works without them). The Kaonomi server talks to the API of Fooocus on
`http://127.0.0.1:8888` (`fooocusUrl` in `KAONOMI.Server/config.json`).

1. Install Git and Python **3.10** (Fooocus needs this version), and an NVIDIA GPU (or Apple Silicon).
2. Clone Fooocus into `~/kaonomi/fooocus`:
   ```bash
   git clone https://github.com/lllyasviel/Fooocus.git ~/kaonomi/fooocus
   cd ~/kaonomi/fooocus
   uv venv --python 3.10 --seed env        # or: python3.10 -m venv env
   env/bin/pip install fastapi uvicorn pygit2   # env\Scripts\pip on Windows
   ```
3. Start the API (from the Fooocus folder):
   ```bash
   env/bin/python <kaonomi>/KAONOMI.Server/Fooocus/fooocus_api.py --fooocus ~/kaonomi/fooocus
   ```
   Its first start installs torch and the models (about 8 GB).

Project: <https://github.com/lllyasviel/Fooocus>

---

## 6. Speech to text (STT) — Whisper

The microphone of the chat is transcribed locally by Whisper (whisper.cpp through Whisper.net, on the GPU with Vulkan
when possible, else on the CPU).

1. Download the model `ggml-small-q5_1.bin` to `~/kaonomi/models/`:
   <https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin>
   (`speech.model` in `KAONOMI.ChatGodot/config.yml`; `/setup` can download it).
2. A microphone, allowed in the privacy settings of the system.
3. For the GPU: an up-to-date graphics driver with Vulkan (NVIDIA, AMD, Intel). The native libraries come with the
   build (Whisper.net runtimes, PortAudio): nothing else to install. On macOS without the bundled library:
   `brew install portaudio`.

---

## 7. Text to speech (TTS) — Piper

The voice of the chat is made locally by Piper.

1. Python **3.9 or newer**: <https://www.python.org/downloads/> (macOS: `brew install python`).
2. Piper in a venv `~/kaonomi/piper/venv` (`/setup` can build it):
   ```bash
   python3 -m venv ~/kaonomi/piper/venv
   ~/kaonomi/piper/venv/bin/pip install piper-tts
   ```
   On Windows: `py -3 -m venv %USERPROFILE%\kaonomi\piper\venv` then
   `%USERPROFILE%\kaonomi\piper\venv\Scripts\pip install piper-tts`.
3. The voices in `~/kaonomi/piper/voices` (one per language of the chat, and an English one for the intro):
   ```bash
   ~/kaonomi/piper/venv/bin/python -m piper.download_voices --data-dir ~/kaonomi/piper/voices fr_FR-siwis-medium en_GB-alan-medium
   ```
   Other default voices: `de_DE-thorsten-medium`, `nl_BE-nathalie-medium`. List: <https://github.com/OHF-voice/piper1-gpl/blob/main/VOICES.md>
4. In `KAONOMI.ChatGodot/config.yml`: `tts.piper` (the `piper` executable of the venv) and `tts.voicesDirectory`.

---

## 8. The chat — Godot 4.7.2 .NET

To run the desktop client from the sources:

- Godot **4.7.2 .NET** (the "mono" build, with C#): <https://godotengine.org/download>
- Start: `Godot_v4.7.2-stable_mono --path KAONOMI.ChatGodot` (or open the project in the Godot editor).
- The data folder `~/kaonomi` (models, voices, prompts collections, JSON results…): `/setup` creates what is missing.

---

## 9. Start order

```bash
dotnet run --project KAONOMI.MongoEngine      # or MongoDB 8
dotnet run --project KAONOMI.SearchEngine     # or Elasticsearch 7.14
# the 6 agent hosts, before the server
dotnet run --project KAONOMI.Dreamer
dotnet run --project KAONOMI.Vision
dotnet run --project KAONOMI.Reflex
dotnet run --project KAONOMI.Metacognition
dotnet run --project KAONOMI.EarlyWarningSystem
dotnet run --project KAONOMI.ACHEngine
dotnet run --project KAONOMI.Server           # https://localhost:49165, Swagger at /swagger
# then the chat (Godot), and Ollama running
```

Start the 6 agent hosts before (or with) the server: the server refuses to start while hosts stopped a few minutes ago
are still registered as alive in the cluster, and a dialog needs the agents of the 6 hosts to answer.
