[www.kaonomi.net](https://www.kaonomi.net)

# KAONOMI — Prérequis

Tout ce dont KAONOMI a besoin pour fonctionner, et comment l'installer. Pour le moteur de recherche et la base de
données, vous avez le choix : installer le vrai produit, ou utiliser le moteur fourni avec KAONOMI (rien à installer).
Le chat vérifie lui-même la plupart de cette liste : tapez `/setup` dans le chat pour voir ce qui manque (il peut
télécharger ou installer certaines parties pour vous).

Version anglaise : [PREREQUISITES.md](PREREQUISITES.md).

| Élément | Sert à | Option A (installer) | Option B (intégré) |
|---|---|---|---|
| SDK .NET 8 | tout | obligatoire | — |
| Moteur de recherche | dialogues, logs, idées, intels, wiki, scores… | Elasticsearch 7.14 | `KAONOMI.SearchEngine` |
| Base de données | le cluster Orleans (les 7 hôtes) | MongoDB 8 | `KAONOMI.MongoEngine` |
| LLM | le chat, les agents, traductions, analyses | Ollama + modèles | — |
| Images | `/fooocus` (facultatif) | Fooocus + son API | — |
| Reconnaissance vocale (STT) | le micro du chat | modèle Whisper | — |
| Synthèse vocale (TTS) | la voix du chat | Python + Piper + voix | — |
| Chat | le client de bureau | Godot 4.7.2 .NET | — |

Tous les services n'écoutent que sur la machine locale. Ports utilisés : 9200 (moteur de recherche), 27017 (base de
données), 11434 (Ollama), 8888 (API Fooocus), 49165/49166 (KAONOMI.Server), 11111–11117 et 30000–30006 (hôtes Orleans).

---

## 1. SDK .NET 8 (obligatoire)

Tous les projets ciblent `net8.0`.

- Téléchargement : <https://dotnet.microsoft.com/download/dotnet/8.0>
- Vérification : `dotnet --version` (SDK 8.0.x ou plus récent).
- Tout compiler depuis le dossier `kaonomi` : `dotnet build KAONOMI.slnx`

---

## 2. Moteur de recherche : Elasticsearch 7.14 **ou** KAONOMI.SearchEngine

KAONOMI enregistre ses documents (dialogues, logs, idées, intels, plans de collecte, scores de jeu…) dans un moteur de
recherche de type Elasticsearch, via le client NEST 7.17, à l'adresse `http://localhost:9200`.

### Option B — KAONOMI.SearchEngine (recommandé, rien à installer)

Un petit moteur de recherche compatible avec l'API REST d'Elasticsearch 7.14, écrit en .NET (Lucene).

- Lancement : `dotnet run --project KAONOMI.SearchEngine`
- Il écoute sur `127.0.0.1:9200` et range ses index dans `~/kaonomi/data`
  (`C:\Users\<utilisateur>\kaonomi\data` sous Windows, `/Users/<utilisateur>/kaonomi/data` sous macOS,
  `/home/<utilisateur>/kaonomi/data` sous Linux), créé au premier démarrage.
- Réglages : `KAONOMI.SearchEngine/config/searchengine.yml` (port, dossier des données, sécurité, rafraîchissement…).
- Les index sont créés à la première écriture : aucun schéma à préparer.
- Pour y copier les données d'un Elasticsearch existant (moteur arrêté) :
  `dotnet run --project KAONOMI.SearchEngine -- import --from http://localhost:9200 [--user u --password p]`

### Option A — Elasticsearch 7.14

1. Télécharger Elasticsearch **7.14.x** (anciennes versions) : <https://www.elastic.co/downloads/past-releases#elasticsearch>
2. Le décompresser, puis dans `config/elasticsearch.yml`, configurer un nœud unique sur cette machine :
   ```yaml
   discovery.type: single-node
   network.host: 127.0.0.1
   http.port: 9200
   xpack.security.enabled: false   # ou la laisser active et donner l'utilisateur et le mot de passe ci-dessous
   ```
3. Le lancer : `bin/elasticsearch` (`bin\elasticsearch.bat` sous Windows). Vérification : <http://localhost:9200>
4. Ne **pas** lancer `KAONOMI.SearchEngine` en même temps (les deux utilisent le port 9200).
5. Si la sécurité est active, indiquer l'utilisateur et le mot de passe dans le `config.json` de chaque projet
   (`KAONOMI.Server`, les 6 hôtes d'agents, `KAONOMI.Admin`), dans `dataRepository`, `dialogRepository` et `adminRepository` :
   ```json
   "dataRepository": { "Url": "http://localhost:9200", "User": "elastic", "Password": "…" }
   ```
   Le chat joint le moteur de recherche à l'adresse `searchEngineUrl` de `KAONOMI.ChatGodot/config.yml`.

---

## 3. Base de données : MongoDB 8 **ou** KAONOMI.MongoEngine

Les 7 hôtes de KAONOMI (le serveur et les 6 hôtes d'agents) forment un cluster Orleans dont la table des membres est
dans MongoDB, base `kaonomi`, à l'adresse `mongodb://localhost:27017`. Rien d'autre de KAONOMI n'y est enregistré.

### Option B — KAONOMI.MongoEngine (recommandé, rien à installer)

Un petit serveur qui parle le protocole de MongoDB, fait pour ce dont Orleans a besoin.

- Lancement : `dotnet run --project KAONOMI.MongoEngine`
- Il écoute sur `127.0.0.1:27017` ; ses données sont dans `KAONOMI.MongoEngine/data` (réglages : `config/mongoengine.yml`).

### Option A — MongoDB 8

1. Télécharger MongoDB Community Server 8 : <https://www.mongodb.com/try/download/community>
   (macOS : `brew tap mongodb/brew && brew install mongodb-community@8.0`).
2. Lancer `mongod` sur `127.0.0.1:27017` (la valeur par défaut). Vérification : `mongosh --eval "db.runCommand({ ping: 1 })"`
3. Ne **pas** lancer `KAONOMI.MongoEngine` en même temps (les deux utilisent le port 27017).
4. Aucune base à créer : les hôtes écrivent leur table des membres au démarrage.

---

## 4. Ollama et les modèles (obligatoire)

Le LLM du chat, de la séquence d'agents du serveur et des outils IA (traduction, analyses, analyse d'images).

1. Installer Ollama : <https://ollama.com/download> — il écoute sur `http://localhost:11434`.
2. Télécharger les modèles :
   ```bash
   ollama pull gpt-oss:20b          # le modèle du serveur (llmmodel dans KAONOMI.Server/config.json)
   ollama pull mistral-small3.2     # le modèle de vision : analyse d'images, revue IA, menu du texte (visionmodel dans config.yml)
   ```
   Tout autre modèle de chat peut servir : il se choisit dans le chat (`llmmodel` / `model` dans les fichiers de config).
3. Vérification : `ollama list`

Les modèles demandent un bon GPU ou un Mac Apple Silicon (environ 13 Go pour `gpt-oss:20b`, 14 Go pour `mistral-small3.2`).

---

## 5. Fooocus — images (facultatif)

Les images de `/fooocus` (le chat fonctionne sans). Le serveur Kaonomi parle à l'API de Fooocus sur
`http://127.0.0.1:8888` (`fooocusUrl` dans `KAONOMI.Server/config.json`).

1. Installer Git et Python **3.10** (Fooocus exige cette version), avec un GPU NVIDIA (ou Apple Silicon).
2. Cloner Fooocus dans `~/kaonomi/fooocus` :
   ```bash
   git clone https://github.com/lllyasviel/Fooocus.git ~/kaonomi/fooocus
   cd ~/kaonomi/fooocus
   uv venv --python 3.10 --seed env        # ou : python3.10 -m venv env
   env/bin/pip install fastapi uvicorn pygit2   # env\Scripts\pip sous Windows
   ```
3. Lancer l'API (depuis le dossier de Fooocus) :
   ```bash
   env/bin/python <kaonomi>/KAONOMI.Server/Fooocus/fooocus_api.py --fooocus ~/kaonomi/fooocus
   ```
   Son premier démarrage installe torch et les modèles (environ 8 Go).

Projet : <https://github.com/lllyasviel/Fooocus>

---

## 6. Reconnaissance vocale (STT) — Whisper

Le micro du chat est transcrit en local par Whisper (whisper.cpp via Whisper.net, sur le GPU avec Vulkan quand c'est
possible, sinon sur le CPU).

1. Télécharger le modèle `ggml-small-q5_1.bin` dans `~/kaonomi/models/` :
   <https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin>
   (`speech.model` dans `KAONOMI.ChatGodot/config.yml` ; `/setup` peut le télécharger).
2. Un micro, autorisé dans les réglages de confidentialité du système.
3. Pour le GPU : un pilote graphique à jour avec Vulkan (NVIDIA, AMD, Intel). Les bibliothèques natives viennent avec
   la compilation (runtimes Whisper.net, PortAudio) : rien d'autre à installer. Sous macOS sans la bibliothèque
   fournie : `brew install portaudio`.

---

## 7. Synthèse vocale (TTS) — Piper

La voix du chat est produite en local par Piper.

1. Python **3.9 ou plus récent** : <https://www.python.org/downloads/> (macOS : `brew install python`).
2. Piper dans un venv `~/kaonomi/piper/venv` (`/setup` peut le construire) :
   ```bash
   python3 -m venv ~/kaonomi/piper/venv
   ~/kaonomi/piper/venv/bin/pip install piper-tts
   ```
   Sous Windows : `py -3 -m venv %USERPROFILE%\kaonomi\piper\venv` puis
   `%USERPROFILE%\kaonomi\piper\venv\Scripts\pip install piper-tts`.
3. Les voix dans `~/kaonomi/piper/voices` (une par langue du chat, et une anglaise pour l'intro) :
   ```bash
   ~/kaonomi/piper/venv/bin/python -m piper.download_voices --data-dir ~/kaonomi/piper/voices fr_FR-siwis-medium en_GB-alan-medium
   ```
   Autres voix par défaut : `de_DE-thorsten-medium`, `nl_BE-nathalie-medium`. Liste : <https://github.com/OHF-voice/piper1-gpl/blob/main/VOICES.md>
4. Dans `KAONOMI.ChatGodot/config.yml` : `tts.piper` (l'exécutable `piper` du venv) et `tts.voicesDirectory`.

---

## 8. Le chat — Godot 4.7.2 .NET

Pour lancer le client de bureau depuis les sources :

- Godot **4.7.2 .NET** (la version « mono », avec C#) : <https://godotengine.org/download>
- Lancement : `Godot_v4.7.2-stable_mono --path KAONOMI.ChatGodot` (ou ouvrir le projet dans l'éditeur Godot).
- Le dossier de données `~/kaonomi` (modèles, voix, collections de prompts, résultats JSON…) : `/setup` crée ce qui manque.

---

## 9. Ordre de démarrage

```bash
dotnet run --project KAONOMI.MongoEngine      # ou MongoDB 8
dotnet run --project KAONOMI.SearchEngine     # ou Elasticsearch 7.14
# les 6 hôtes d'agents, avant le serveur
dotnet run --project KAONOMI.Dreamer
dotnet run --project KAONOMI.Vision
dotnet run --project KAONOMI.Reflex
dotnet run --project KAONOMI.Metacognition
dotnet run --project KAONOMI.EarlyWarningSystem
dotnet run --project KAONOMI.ACHEngine
dotnet run --project KAONOMI.Server           # https://localhost:49165, Swagger sur /swagger
# puis le chat (Godot), avec Ollama lancé
```

Lancer les 6 hôtes d'agents avant le serveur (ou en même temps) : le serveur refuse de démarrer tant que des hôtes
arrêtés depuis quelques minutes sont encore inscrits comme actifs dans le cluster, et un dialogue a besoin des agents
des 6 hôtes pour répondre.
