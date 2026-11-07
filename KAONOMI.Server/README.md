[www.kaonomi.net](https://www.kaonomi.net)

# KAONOMI.Server

ASP.NET Core REST host that saves dialogs, runs a M-A-R-I agent sequence against Ollama, and exposes message-flow, cache, and symbolic-pattern APIs. Swagger UI is available at startup.

## Run

```bash
dotnet run --project KAONOMI.Server
```

Default URLs (see `Properties/launchSettings.json`):

- `https://localhost:49165`
- `http://localhost:49166`

On start, the app may open Swagger, the M-A-R-I (Orleans) dashboard (`http://localhost:8090`), and a Kibana dashboard if configured.

## Configuration

`config.json` next to the executable:

| Key | Purpose |
| --- | --- |
| `appName` | Application identity for logs |
| `ollamaUrl` | Local Ollama base URL |
| `fooocusUrl` | Fooocus API base URL, for the image routes (default `http://127.0.0.1:8888`) |
| `llmmodel` | Default model name |
| `dataRepository` / `dialogRepository` / `adminRepository` | Elasticsearch endpoints |

## Main endpoint

`POST /aiassistant` — body (`DialogGenericRequest`):

```json
{
  "model": "mistral-small3.2",
  "stream": false,
  "messages": [
    { "role": "user", "content": "Hello" }
  ]
}
```

Flow: persist dialog → run M-A-R-I sequence `jfo` with the dialog id → return `DialogGenericResponse` (chat-completion style).

## NER: the entities of a text

```http
POST /ner
{ "text": "Jérôme Fortias présente Kaonomi à Paris le 12/11/2026, voir https://www.kaonomi.net.", "types": [] }
```

```json
{ "entities": [
    { "text": "Jérôme Fortias", "type": "PERSON",   "start": 0,  "end": 14, "score": 0.95, "source": "xlmr+ontology", "ontologyId": "…", "ontologyName": "Jerome Fortias" },
    { "text": "Kaonomi",        "type": "SOFTWARE", "start": 24, "end": 31, "score": 0.92, "source": "xlmr+ontology", "ontologyId": "…", "ontologyName": "Kaonomi" },
    { "text": "Paris",          "type": "CITY",     "start": 34, "end": 39, "score": 0.64, "source": "xlmr+ontology", "ontologyId": "…", "ontologyName": "Paris" },
    { "text": "12/11/2026",     "type": "DATE",     "start": 43, "end": 53, "score": 1,    "source": "rule" },
    { "text": "https://www.kaonomi.net", "type": "URL", "start": 60, "end": 83, "score": 1, "source": "rule" } ],
  "durationMs": 8.2, "model": "Davlan/xlm-roberta-base-ner-hrl int8 (model_quantized.onnx)", "warnings": [] }
```

`types` (optional) keeps only those types. `start` / `end` are positions of characters in `text` (end excluded).
`GET /ner/types` lists the types of each layer, the number of entities of the ontology read, and the state of the model.

Three layers (`Ner/`):

| Layer | What | Types |
| --- | --- | --- |
| Model | [`Davlan/xlm-roberta-base-ner-hrl`](https://huggingface.co/Davlan/xlm-roberta-base-ner-hrl) (XLM-RoBERTa base fine-tuned on 10 languages, French and English among them; license AFL-3.0), exported to ONNX and quantized in **int8** (278 MB), run on the CPU by ONNX Runtime: about 5 ms a sentence, 0.6 s to load | `PERSON`, `ORG`, `LOC`, `DATE` |
| Rules | the extractor of `KAONOMI.LLMGateway` (its source compiled here too, the gateway stays on its own) | `URL`, `EMAIL`, `IP`, `DATE`, `TIME`, `MONEY`, `PERCENT`, `PHONE`, `NUMBER`, `FILE`, `HASHTAG`, `MENTION`, `CODE`, `PROGRAMMING_LANGUAGE`, `MATH` |
| Ontology | every entity of the ontology (not a class, not deleted), by its name and its synonyms, whatever the case and the accents, the longest name first, on whole words; read again every 10 minutes | the types of the ontology (`SOFTWARE`, `HARDWARE`, `COUNTRY`, `CITY`, `EVENT`…) |

How they combine: a URL, an e-mail, a file, code, an IP, a hashtag or a mention is kept whole (no name looked for
inside it); a name of the model that a name of the ontology covers whole (the same, or a longer one) takes the type
and the id of the ontology (`xlmr+ontology`), but a name of the ontology inside a longer name of the model is not that
name ("Olaf", the OLAF of the ontology, in "Olaf Scholz" leaves Olaf Scholz a person); a name of the ontology the model
did not see is added (`ontology`); the other rules (dates,
amounts…) fill where no name is. The model's own output is made whole: a word it cut in two ("J" + "érôme") is one
entity, a lone punctuation ("'" of "l'ESA") is dropped. Without the model folder, the rules and the ontology still
answer, with a warning.

The tokenizer (`Ner/XlmrTokenizer.cs`, SentencePiece Unigram read from `tokenizer.json`) gives the same pieces and
positions as the one of Hugging Face, and the entities before they are made whole are those of the pipeline of
Transformers on the same int8 model (checked on 30 French, English, German and Spanish sentences; the int8 model finds
the same entities as the original fp32 one in 28 of them).

The agent of the entities of the dialog sequence (`DialogEntityVAgent`) runs the same NER on the prompt of the user
and keeps the entities with the dialog (`Dialog.Entities`). The model and the ontology are loaded in the background
when the server starts.

**The model** is not in git. It is in `~/kaonomi/models/ner/xlm-roberta-base-ner-hrl-int8` (`nerModelFolder` in
config.json to put it elsewhere). To make it again, with Python 3.10 or later:

```bash
python -m venv ~/kaonomi/tools/ner-export
~/kaonomi/tools/ner-export/Scripts/python -m pip install torch --index-url https://download.pytorch.org/whl/cpu
~/kaonomi/tools/ner-export/Scripts/python -m pip install "optimum[onnxruntime]" transformers sentencepiece
~/kaonomi/tools/ner-export/Scripts/python KAONOMI.Server/Ner/export/ner_export.py ~/kaonomi/models/ner/xlm-roberta-base-ner-hrl-int8
```

(on macOS and Linux: `bin/python` instead of `Scripts/python`). On Windows, keep the environment on a short path: the
files of PyTorch go beyond 260 characters in a deep folder. The script also writes `reference.json`, the pieces and the
entities of Python for 30 sentences, to compare with.

## Other routes

| Method | Route | Description |
| --- | --- | --- |
| `GET` | `/version` | Version string |
| `GET` | `/` | Redirect to Swagger |
| `GET` | `/tokensusage/last?minutes=N` | Token usages (one per LLM call: model, prompt and completion tokens) of the last N minutes, newest first; `minutes` defaults to 60 |
| `GET` | `/log/since?minutes=N` | All the logs of the last N minutes (every application), newest first, read page by page without the 10,000 limit; `minutes` defaults to 60 |
| `GET` | `/log/last?size=N` | The N most recent logs (all applications), newest first; `size` defaults to and is capped at 10,000 |
| `DELETE` | `/log` | Erases ALL the logs (the index of the logs is deleted, created again empty at the next log); `returnedId`: the number erased |
| `GET` | `/messageflow/{id}` | Get ticker message |
| `POST` | `/messageflow/search` | Search the `text` field (`term`) |
| `POST` | `/messageflow/last` | Up to `size` messages matching `term` (`*` when empty) |
| `POST` | `/messageflow/update` | Save (create or overwrite) a ticker message |
| `PUT` | `/messageflow` | Create ticker message |
| `GET` | `/cache/{id}` | Get cache document |
| `POST` | `/cache/search` | Search the `prompt` field (`term`) |
| `POST` | `/cache/last` | Up to `size` cache entries matching `term` (`*` when empty) |
| `POST` | `/cache/update` | Update cache document |
| `PUT` | `/cache` | Create cache document |
| `GET` | `/symbolicpattern/{id}` | Get symbolic pattern |
| `POST` | `/symbolicpattern/search` | Search the `regexPattern` field (`term`) |
| `POST` | `/symbolicpattern/last` | Up to `size` patterns matching `term` (`*` when empty) |
| `POST` | `/symbolicpattern/update` | Update pattern |
| `PUT` | `/symbolicpattern` | Create pattern |
| `GET` | `/idea/{id}` | Get idea (404 when it does not exist) |
| `POST` | `/idea/search` | Search `ideaTitle`, `ideaDescription`, `category` and `tags` (`term`, `size`; `*` or empty: newest ideas) |
| `POST` | `/idea/last` | The `size` most recent ideas, newest first |
| `POST` | `/idea/update` | Update idea |
| `PUT` | `/idea` | Create idea (date set to now when missing) |
| `DELETE` | `/idea/{id}` | Delete idea |
| `GET` | `/diary/{id}` | Get diary entry (404 when it does not exist) |
| `POST` | `/diary/search` | Search the `text` field only (`term`, `size`; `*` or empty: newest entries); deleted entries are left out |
| `POST` | `/diary/last` | The `size` most recent diary entries, newest first, without the deleted ones |
| `POST` | `/diary/update` | Update diary entry |
| `PUT` | `/diary` | Create diary entry (date set to now when missing) |
| `DELETE` | `/diary/{id}` | Soft delete: nothing is erased, the entry gets `isDeleted = true` (still readable with `GET`, restored by `POST /diary/update` with `isDeleted = false`) |
| `GET` | `/task/{id}` | Get task, deleted or not (404 when it does not exist) |
| `POST` | `/task/search` | Search `name`, `description`, `type`, `status` and `comment` (`term`, `size`; `*` or empty: newest tasks); deleted tasks are left out |
| `POST` | `/task/last` | The `size` most recent tasks, newest first, without the deleted ones |
| `POST` | `/task/update` | Update task |
| `PUT` | `/task` | Create task (date set to now when missing) |
| `DELETE` | `/task/{id}` | Soft delete: nothing is erased, the task gets `isDeleted = true` (still readable with `GET`, restored by `POST /task/update` with `isDeleted = false`) |
| `GET` | `/intel/{id}` | Get intel, deleted or not (404 when it does not exist) |
| `POST` | `/intel/search` | Search all the text fields: `name`, `description`, `categories`, `triggers`, `relatedEntities`, `excludedEntities`, `events`, `signal`, `tags`, `instruction`, `actions`, `comment`, `appName`, `userName`, `machineName` (`term`, `size`; `*` or empty: newest intels); deleted intels are left out |
| `POST` | `/intel/last` | The `size` most recent intels, newest first, without the deleted ones |
| `POST` | `/intel/update` | Update intel |
| `PUT` | `/intel` | Create intel (date set to now when missing) |
| `DELETE` | `/intel/{id}` | Soft delete: nothing is erased, the intel gets `isDeleted = true` (still readable with `GET`, restored by `POST /intel/update` with `isDeleted = false`) |
| `PUT` | `/useractivity` | Save an activity of the journal of the chat, index `useractivities` (`action`: SAVE, `windowName`, `link`: the command of the window, `description`) |
| `POST` | `/useractivity/last` | The `size` most recent activities, newest first |
| `POST` | `/useractivity/search` | Search `action`, `windowName`, `link`, `description` (`term`, `size`; `*` or empty: newest) |
| `PUT` | `/gamescore` | Save the score of a finished game of the chat (`game`: game1, `score`, `lines`, `durationSeconds`, `end`: stopped or blocked, `comment`) |
| `POST` | `/gamescore/best` | The `size` best scores of the game named by `term` (game1 when empty), the highest first; deleted scores left out |
| `POST` | `/gamescore/last` | The `size` most recent scores (all games), newest first |
| `DELETE` | `/gamescore/{id}` | Soft delete: the score gets `isDeleted = true` |
| `GET` | `/collectionplan/{id}` | Get collection plan (of the web scraper), deleted or not (404 when it does not exist) |
| `POST` | `/collectionplan/search` | Search all the text fields: `name`, `description`, `project`, `instructions`, `websites.name`, `websites.urlProcesses.url`, `websites.urlProcesses.keywords`, `comment`, `appName`, `userName`, `machineName` (`term`, `size`; `*` or empty: newest plans); deleted plans are left out |
| `POST` | `/collectionplan/last` | The `size` most recent collection plans, newest first, without the deleted ones |
| `POST` | `/collectionplan/update` | Update collection plan |
| `PUT` | `/collectionplan` | Create collection plan (date set to now when missing) |
| `DELETE` | `/collectionplan/{id}` | Soft delete: nothing is erased, the plan gets `isDeleted = true` (still readable with `GET`, restored by `POST /collectionplan/update` with `isDeleted = false`) |
| `GET` | `/youtubevideo/{id}` | Get YouTube video, deleted or not (404 when it does not exist) |
| `POST` | `/youtubevideo/search` | Search `title` and `description` (`term`, `size`; `*` or empty: newest videos); deleted videos left out |
| `POST` | `/youtubevideo/last` | The `size` most recent YouTube videos, newest first, without the deleted ones |
| `POST` | `/youtubevideo/update` | Update YouTube video |
| `PUT` | `/youtubevideo` | Create YouTube video (date set to now when missing) |
| `DELETE` | `/youtubevideo/{id}` | Soft delete: sets `isDeleted = true` (restored with `/youtubevideo/update` and `isDeleted = false`) |
| `GET` | `/document/{id}` | Get document, deleted or not (404 when it does not exist) |
| `POST` | `/document/search` | Fuzzy search on `title`, `description` and the content of every version (`contents.content`) (`term`, `size`; `*` or empty: newest documents); deleted documents left out |
| `POST` | `/document/last` | The `size` most recent documents, newest first, without the deleted ones |
| `POST` | `/document/update` | Update document |
| `PUT` | `/document` | Create document (date set to now when missing) |
| `DELETE` | `/document/{id}` | Soft delete: sets `isDeleted = true` (restored with `/document/update` and `isDeleted = false`) |
| `GET` | `/completion/{id}` | Get completion (404 when it does not exist) |
| `POST` | `/completion/search` | Search `prompt` and `completion` (`term`, `size`; `*` or empty: newest completions) |
| `POST` | `/completion/last` | The `size` most recent completions, newest first |
| `POST` | `/completion/update` | Update completion |
| `PUT` | `/completion` | Create completion (prompt, completion, model, word counts…) |
| `DELETE` | `/completion/{id}` | Delete completion |
| `GET` | `/relation/{id}` | Get relation with all its versions, deleted or not (404 when it does not exist) |
| `PUT` / `POST` | `/relation`, `/relation/update` | Save a version of a relation between windows of the chat (`Relation`, index `relation`, `category` `interdata` by default). A relation is **versioned in one document**: `versions` lists them oldest first (the last one is the current one), each with its `version` number, `date`, `userName`, `nature`, `title`, `description`, `probability` (-100 absolutely impossible to 100 absolutely certain, 0 to evaluate), optional `dateFrom` / `dateTo` (nullable dates of the link), `source` and `destinations` (one or more, in the order of the string; each with `windowName`, `objectId`, `information`, `context`). Without an id (or an unknown one) the relation is created with the versions brought, numbered from 1; with the id of a stored relation, the last version brought is added with the next number, the previous ones kept. The call returns once the relation can be searched (`refresh=wait_for`), so a search right after it finds it |
| `POST` | `/relation/search` | Fuzzy search on the nature, title, description and the window, object and information of the ends, in all the versions (`term`, `size`); deleted ones left out |
| `POST` | `/relation/last` | The `size` most recently saved relations, without the deleted ones |
| `DELETE` | `/relation/{id}` | Soft delete: sets `isDeleted = true`; returns once the searches leave it out (`refresh=wait_for`) |
| `GET` | `/annotation/{id}` | Get an annotation, deleted or not (404 when it does not exist) |
| `PUT` / `POST` | `/annotation`, `/annotation/update` | Save an annotation of any object of the application (`Annotation`, index `annotation`): `text`, `tags` (normalized to upper-case hashtags, no duplicates: `software` → `#SOFTWARE`, `work of art` → `#WORK_OF_ART`), the object annotated (`targetType`: `Entity`, `TextBlock`, `Image`, `ProjectItem`, `Idea`… required; `targetId`, `targetTitle`), `excerpt` (the part annotated: a block of text, a region of an image) and `context` (window and data as JSON text, as for a white note). Without an id it is created, with the id of a stored one it is replaced (an update without a date keeps the date it was written). Refused without a text and a tag, or without `targetType`. Returns once it can be searched |
| `POST` | `/annotation/search` | Fuzzy search on the text, tags, object annotated (type, id, title), excerpt and comment (`term`, `size`); deleted ones left out |
| `POST` | `/annotation/last` | The `size` most recent annotations, without the deleted ones |
| `DELETE` | `/annotation/{id}` | Soft delete: sets `isDeleted = true` (left out of the searches at once) |
| `GET` | `/imageanalysis/{id}` | Get image analysis, deleted or not (404 when it does not exist) |
| `GET` | `/imageanalysis/image?path=…` | The analysis of the picture at this path (404 when it has none yet) |
| `POST` | `/imageanalysis/search` | Fuzzy search on `title`, `fileName`, `aiAnalysis`, `comments.text`, `annotations.comment`, `locationName`, `imagePath` (`term`, `size`); deleted ones left out |
| `POST` | `/imageanalysis/last` | The `size` most recent image analyses, without the deleted ones |
| `PUT` / `POST` | `/imageanalysis`, `/imageanalysis/update` | Save the analysis of a picture (`ImageAnalysis`, index `imageanalysis`): one per picture, its id is the SHA-256 of the path of the picture; the link `imageUrl` (file://) is set when missing; `category` is `image`, or `subimage` for the part of a picture cut out by an annotation rectangle (`parentId`, `parentImagePath`, and the origin in its metadata) |
| `DELETE` | `/imageanalysis/{id}` | Soft delete: sets `isDeleted = true` |
| `GET` | `/image/status` | State of the Fooocus API: version, default model, `busy` (503 when it does not answer) |
| `GET` | `/image/options` | Styles, performances, aspect ratios and models an image generation accepts, and the defaults |
| `POST` | `/image/generate` | Generates images from a prompt with Fooocus (body: `ImageGenerationRequest`, see [Images](#images-fooocus)); 400 for an empty prompt, 502 when Fooocus fails |

The multi-field searches (`/idea/search`, `/diary/search`, `/task/search`, `/intel/search`, `/youtubevideo/search`, `/document/search`, `/completion/search`) take the logical operators `and`, `or` (any case; `and` binds tighter than `or`) and parentheses: `recette and (sauce or soupe)`. Each term between operators is searched as before (all its words, fuzzy, or as a part of the text, in any of the fields); a "quoted text" is one term even with `and` / `or` inside; a missing closing parenthesis is accepted.

Search bodies use `SearchCriterias`:

```json
{ "size": 100, "term": "*" }
```

Notes:

- `/search` routes only use `term`; `size` is ignored there.
- `/last` routes apply no sort: they return the first `size` hits in search-relevance order, not necessarily the most recent documents.
- `update` / `PUT` routes return a `ServiceReturn` (`ReturnedId`, `Message`); `ReturnedId` is `"-1"` on failure.

### Images (Fooocus)

The `/image` routes generate images with [Fooocus](https://github.com/lllyasviel/Fooocus) (Stable Diffusion XL), through a small REST API that runs in the Python environment of an installed Fooocus: [`Fooocus/fooocus_api.py`](Fooocus/fooocus_api.py). It starts Fooocus without its web interface (same models, presets, styles and image log, in the `outputs` folder of Fooocus) and listens on `127.0.0.1:8888` (`--host`, `--port`; `--fooocus` or `FOOOCUS_HOME` for the folder of Fooocus, the Pinokio install `~/pinokio/api/fooocus.git/app` by default). Start it before using the routes (the first generation loads the model, about a minute):

```bash
~/pinokio/api/fooocus.git/app/env/bin/python Fooocus/fooocus_api.py
```

Or with a Fooocus cloned in `~/kaonomi/fooocus` (a Python 3.10 venv in its `env` folder, made with `uv venv --python 3.10 --seed env` plus `fastapi`, `uvicorn` and `pygit2`; its first start installs torch and the Fooocus packages and downloads the models, about 8 GB):

```bash
cd ~/kaonomi/fooocus && env/bin/python ~/Projects/kaonomi/KAONOMI.Server/Fooocus/fooocus_api.py --fooocus ~/kaonomi/fooocus
```

Its own routes are `GET /health`, `GET /options` and `POST /generate` (docs at `http://127.0.0.1:8888/docs`). One generation runs at a time; the others wait their turn. Only text to image: the image inputs of Fooocus (upscale, vary, inpaint, image prompts, enhance) are left off.

`POST /image/generate` body (every field but `prompt` is optional; empty fields take the defaults of Fooocus):

```json
{
  "prompt": "a small white robot reading a book in a cozy library, warm light",
  "negativePrompt": null,
  "styles": ["Fooocus V2", "Fooocus Enhance", "Fooocus Sharp"],
  "performance": "Speed",
  "aspectRatio": "1152*896",
  "imageNumber": 1,
  "seed": -1,
  "outputFormat": "png",
  "baseModel": "juggernautXL_v8Rundiffusion.safetensors",
  "guidanceScale": 4,
  "includeBase64": true
}
```

The answer (`ImageGenerationResult`) lists the `images` (`path` of the file, `base64` of its data unless `includeBase64` is false), the `seed` used (`-1` asks for a random one), the `model` and the `seconds` taken; `error` explains a failure. Each generation is logged. On an Apple M5 Max, one 1152 × 896 image takes about a minute in `Speed`, 20 s in `Extreme Speed`. `Extreme Speed`, `Lightning` and `Hyper-SD` need a LoRA that Fooocus downloads from Hugging Face the first time they are used (about 400 MB each).

### Message flow (ticker)

`MessageFlow` documents feed the scrolling ticker of the Chat client:

```json
{
  "text": "CAC 40 -1.2%",
  "color": "red",
  "priority": 0,
  "url": "https://example.com/news"
}
```

| Field | Meaning |
| --- | --- |
| `text` | Message shown in the ticker |
| `color` | `red` (falling, ▼), `green` (rising, ▲); any other value is shown in white |
| `priority` | `0` low, `1` medium, `2` high |
| `url` | Optional link opened when the message is clicked |

## Agents

The active dialog sequence wires inbound guardrail → processor → outbound guardrail (and related steps defined in `Program.cs`). Additional VAgents live in this project (preprocessing, entities, symbolic analysis, forecast, cache, emotions, context update, etc.) for use in sequences.

## Dependencies

- `KAONOMI.Core`
- ASP.NET Core, Swagger, M-A-R-I

## See also

- [Solution README](../README.md)
- [KAONOMI.Chat](../KAONOMI.Chat/README.md)
