# KAONOMI.LLMGateway

A gateway in front of Ollama, with a **semantic cache** and a **router of the models by the entities of the prompt**.
It speaks the API of Ollama (`/api/chat`, `/api/generate`) and the one of OpenAI (`/v1/chat/completions`); every
other request (`/api/tags`, `/api/show`, `/api/embed`…) goes to Ollama unchanged. A client of Ollama uses it by
changing its address only.

```bash
cd KAONOMI.LLMGateway && dotnet run          # http://localhost:11435 → http://localhost:11434
```

In front of LM Studio: `dotnet run -- --upstream http://localhost:1234 --provider lmstudio` (or `upstream` and
`upstreamProvider` in `config/llmgateway.json`). LM Studio speaks the API of OpenAI only: the gateway then reads its
models from `/v1/models`, embeds with `/v1/embeddings` (`cache.embeddingModel` is an identifier of LM Studio, e.g.
`text-embedding-nomic-embed-text-v1.5`), answers `/v1/chat/completions`, and refuses `/api/chat` and `/api/generate`
with a 400 (its clients are set to `llmProvider` `lmstudio`).

Then point the server and the agents to it: `"ollamaUrl": "http://localhost:11435"` in their `config.json`. The chat
drives it with `/gateway` (and starts it from `/process`).

## The router

The entities of the last prompt of the user are read with rules, without a model (a fraction of a millisecond):

| Type | What |
|---|---|
| `CODE` | a fenced block (its language as value), a line of code, a stack trace, inline code |
| `PROGRAMMING_LANGUAGE` | C#, Python, SQL, JavaScript… named in the prose |
| `MATH` | a formula, a calculation, an equation, an integral… |
| `URL`, `EMAIL`, `IP`, `PHONE`, `FILE` | addresses and file names |
| `DATE`, `TIME`, `MONEY`, `PERCENT`, `NUMBER` | values |
| `PERSON`, `PROPER_NOUN`, `HASHTAG`, `MENTION` | names |
| `LANGUAGE` | `fr` or `en` |
| `IMAGE` | a picture in the request |
| `COMPLEX` | a long sentence (18 words or more) with a condition or a reason (`si`, `sinon`, `donc`, `parce que`, `if`, `because`…) and one more link (`et`, `ou`, `mais`, `and`, `or`…) |
| any | the names of the `gazetteers` of the settings (`ORG`, `PRODUCT`…) |

A rule names entities by type (`CODE`) or by type and value (`LANGUAGE:fr`, `CODE:python`). It matches when the
prompt holds `minCount` entities of `any`, every one of `all`, and none of `none`. The rules are tried by priority,
the highest first; a rule whose model is not installed in Ollama is skipped. With `overrideRequested`, a rule replaces
the model asked by the client; without it, only the model `auto` is routed. `auto` without a matching rule goes to
`defaultModel`.

## The semantic cache

The last prompt is embedded with the embedding model of Ollama set in `cache.embeddingModel`
(`ollama pull nomic-embed-text`), or, when it is not installed, with a local embedding of the words (the same question
asked again with a few words changed, not a synonym). An answer is given back when a cached prompt is close enough
(`threshold`, or `lexicalThreshold` for the local embedding), for the same model (after the router), after the same
system prompt and history, and with the same entities (`entityGuard`: "the capital of France" is not "the capital of
Spain", 2 + 2 is not 2 + 3). The cache answers a prompt only once it keeps `minAnswers` answers of the model for
prompts that close (3 by default; until then the prompt goes to the model and its answer is kept with the others), and
then gives back one of them drawn at random, never the one it gave last: its answers vary. `minAnswers: 1` gives back
the first answer each time. A request with a picture, tools or a context of `/api/generate` is not cached; the header
`X-Gateway-Cache: off` skips the cache for a request. A cached answer comes back whole or streamed, as asked.

Each answer says what was done: `X-Gateway-Model`, `X-Gateway-Rule`, `X-Gateway-Cache` (HIT, MISS, BYPASS, OFF) and
`X-Gateway-Score`.

## Export and import of the phrases

`GET /gateway/cache/export` gives the phrases of the cache, each with its possible answers; `POST /gateway/cache/import`
adds the phrases of such a file (an answer already kept is skipped). The window `/gateway` of the chat does both
(⇩ EXPORT, ⇧ IMPORT) and searches the phrases in a popup (🔎 PHRASES).

```json
{
  "format": "kaonomi-llmgateway-cache",
  "version": 1,
  "phrases": [
    { "prompt": "JARVIS please bonjour", "answers": [ "Bonjour.", "Salut !", "Bonjour, que puis-je faire ?" ] },
    { "prompt": "What is KAONOMI?", "model": "gpt-oss:20b", "system": "You are Kaonomi…", "answers": [ "…" ] }
  ]
}
```

A phrase exported keeps its `model` and its `context` (the hash of its system prompt and history), and comes back as
it was. A phrase written by hand needs its `prompt` and its `answers`: without `model` it holds for every model, and
without `context` (or a `system` prompt to make one) for every system prompt and history. With `minAnswers` at 3, a
phrase imported with three answers is answered by the cache at once.

## The API to drive it

| | |
|---|---|
| `GET /gateway/status` | Ollama, the embedder, the counters of the cache and of the rules |
| `GET`, `PUT /gateway/settings` | the cache and the routing (without the rules) |
| `GET`, `PUT /gateway/rules` | the rules |
| `GET /gateway/cache?take=&search=`, `DELETE /gateway/cache[/{id}]` | the cached answers |
| `GET /gateway/cache/phrases?search=`, `GET /gateway/cache/export?search=`, `POST /gateway/cache/import` | the phrases and their answers |
| `POST /gateway/feedback/dislike` `{prompt, answer, model}` | a disliked answer (the 👎 of the chat): it leaves the cache; when the default model gave it, `escalationModel` in the answer names the model to ask again (`routing.escalationModel`) |
| `GET /gateway/log?take=` | the last requests: model asked and chosen, rule, entities, cache |
| `POST /gateway/analyze` `{prompt, model}` | what the gateway would do, without asking a model |
| `GET /gateway/models`, `GET /gateway/entity-types`, `POST /gateway/reset` | |

The settings are in `config/llmgateway.json`; what is changed through the API is kept in
`~/kaonomi/llmgateway/state.json` (`POST /gateway/reset` forgets it), and the cache in `cache.json` next to it.
