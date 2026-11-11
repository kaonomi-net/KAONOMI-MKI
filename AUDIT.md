# Audit du code KAONOMI — 11 novembre 2026

Audit par lecture du code, sans outil d'analyse automatique. Quatre périmètres : le serveur et le cœur, la passerelle
LLM et les moteurs, le code partagé des deux chats, les vues Godot et Avalonia. Chaque constat a été vérifié dans le
code, sauf mention « soupçon ». Le prototype tourne en local sans authentification : ce n'est pas compté comme défaut.

Trois niveaux : **grave** (perte de données, plantage, réponse fausse), **moyen** (échec masqué, coût important),
**faible** (cas limite, dette).

## 1. Ce qui a été corrigé pendant l'audit

### Optimisations des deux chats (code partagé `KAONOMI.ChatGodot/Shared`)

| Fichier | Avant | Maintenant |
|---|---|---|
| `Services/HttpJson.cs` | Chaque réponse JSON était désérialisée sur le fil de l'interface | Les réponses de plus de 16 Ko le sont sur le pool de threads |
| `Services/HttpJson.cs` | Chaque corps de requête était sérialisé en chaîne puis recopié sur le fil de l'interface (images en base64 comprises) | Le corps est écrit directement dans la requête, au moment de l'envoi, hors du fil de l'interface |
| `ViewModels/MainViewModel.cs` (affichage progressif) | À chaque mot affiché, tout le texte était reconstruit deux fois et redécoupé en lignes (coût quadratique) | Les lignes sont comptées au fil des mots, le texte construit une fois par mot |
| `ViewModels/MainViewModel.cs` | Le Markdown de chaque réponse était analysé deux fois pour y trouver le code | Une seule fois |
| `ViewModels/MainViewModel.cs` (voix) | Une erreur pendant le résumé parlé laissait la préparation « en cours » pour toujours : la bouche de l'animation ne suivait plus les mots | Toute erreur est rattrapée, la préparation est toujours libérée |
| `ViewModels/MainViewModel.cs` (bandeau) | Le bandeau était vidé et rempli chaque minute, même sans changement | Reconstruit seulement quand un message change |
| `ViewModels/MainViewModel.cs` (prompt système) | Une source d'annulation par frappe, jamais libérée ; un enregistrement en retard pouvait écraser un texte plus récent | Libérée ; c'est le texte courant qui est enregistré |
| `Services/SpeechService.cs` | Le découpage en phrases (une douzaine d'expressions régulières) tournait sur le fil de l'interface | Sur le pool de threads |
| `Services/SpeechService.cs` | Sous Windows, chaque phrase lancée par un `powershell.exe` neuf | PortAudio d'abord (sortie par défaut ou choisie) ; PowerShell reste le repli |
| `Services/AudioDevices.cs`, `SpeechRecognitionService.cs` | Un tableau alloué à chaque rappel audio (lecture et micro) | Un tampon réutilisé |
| `ViewModels/SystemWindowViewModel.cs` | Une dizaine de requêtes `/systemlog` toutes les 5 s, l'une après l'autre ; un pinceau neuf chaque seconde | Toutes les 30 s, envoyées ensemble ; trois pinceaux fixes |
| `ViewModels/GatewayWindowViewModel.cs` | Journal et cache vidés puis remplis ligne par ligne toutes les 3 s | Rien quand les lignes sont les mêmes |
| `ViewModels/ProcessWindowViewModel.cs` | Un instantané de tous les processus de la machine par processus géré (11), toutes les 3 s, jamais libéré | Recherche par nom, sur le pool de threads, poignées libérées |
| `ViewModels/FooocusWindowViewModel.cs` | Images décodées et écrites sur le fil de l'interface | Sur le pool de threads |

### Optimisations des vues Avalonia (`KAONOMI.Chat`)

| Fichier | Avant | Maintenant |
|---|---|---|
| `Themes/ThemeBackground.cs`, `Avatar/Avatar3D.cs` | Les deux contrôles OpenGL se redessinaient sans pause, à la fréquence de l'écran | 30 images par seconde au plus |
| `Chat/ChatPanel.cs` | Un panneau fermé (le chat plein écran vertical) restait en mémoire avec son animation, son minuteur toujours actif | Minuteur et abonnements suivent l'affichage du panneau |
| `Chat/ChatPanel.cs` | Horloge, date et prochaine alarme recalculées 4 fois par seconde | Une fois par seconde |
| `Chat/ChatPanel.cs` | Pendant l'affichage progressif, toute la réponse était remise en page à chaque mot | 10 fois par seconde au plus |
| `Chat/ChatPanel.cs` | Les pouces étaient réévalués à chaque mot de la réponse | Seulement quand leur état change |
| `Chat/VoiceMode.cs` | Chaque entrée en mode vocal laissait un abonnement sur le chat | Retiré à la sortie |
| `MainWindow.cs` | Deux animations 3D dessinées quand le chat vertical est ouvert | Une seule |

### Optimisations des vues Godot (`KAONOMI.ChatGodot/Scripts`)

| Fichier | Avant | Maintenant |
|---|---|---|
| `FaceAvatar.cs`, `JarvisAvatar.cs` | Les noms des paramètres de shader recréés à chaque image ; calculs faits même animation cachée | Noms créés une fois ; rien quand l'animation n'est pas visible |
| `Eve/Bind.cs` | Une recherche par réflexion à chaque notification et à chaque frappe | Propriétés mises en cache |
| `Eve/ChatPanel.cs` | Texte et largeur du bandeau relus dans le moteur à chaque image ; pouces réévalués à chaque mot | Relus quand le texte change ; pouces liés à leurs trois propriétés |
| `Main.cs` | `config.yml` lu et analysé une seconde fois au démarrage | Les réglages déjà chargés sont réutilisés |

### Défauts corrigés hors des chats

- **Contexte de conversation (serveur)** : une question retapée à l'identique (« continue », « oui ») supprimait
  l'échange précédent du contexte. Le remplacement n'a plus lieu que si le chat signale une relance après un pouce
  vers le bas (nouveau champ `regenerate`).
- **Contexte de conversation (serveur)** : un message d'erreur du modèle (« [Kaonomi Error] … ») était enregistré
  comme un échange. Il est maintenant ignoré.
- **Panne du modèle** (`Core/Helpers/HelperOllamaClient.cs`, `Server/DialogProcessorVAgent.cs`) : elle était enregistrée
  comme une vraie réponse. Elle porte maintenant `finish_reason = "error"`, sans ligne de tokens ; le contexte de la
  conversation l'ignore et les chats l'affichent comme une erreur, sans la lire à voix haute. Vérifié passerelle
  arrêtée.
- **`/aiassistant`** (`Server/Program.cs`) : il renvoyait une réponse vide en 200 quand la séquence échouait, sans
  cause dans le journal. Il renvoie maintenant une erreur 500 avec la cause, qui est aussi journalisée ; même chose
  quand la séquence se termine sans réponse. Ce chemin n'a pas pu être déclenché en test. L'identifiant du dialogue ne
  passe plus par un champ statique partagé entre les requêtes.
- **`LogSave`** (`Core/SessionManager.cs`) : un journal impossible à écrire interrompait le tour de dialogue. L'échec
  est maintenant écrit sur la console et le tour continue.
- **Passerelle LLM** (`Cache/SemanticCache.cs`) : la sauvegarde du cache, lancée par un minuteur, pouvait faire tomber
  le processus (fichier tenu par un antivirus, deux sauvegardes qui se chevauchent) et sérialisait tout le cache sous
  le verrou des requêtes. Elle est maintenant protégée, une à la fois, hors du verrou, sans indentation.

## 2. Ce qui reste à faire

### Serveur et cœur (`KAONOMI.Server`, `KAONOMI.Core`)

| Niveau | Constat | Où |
|---|---|---|
| Moyen | Environ 90 allers-retours synchrones au moteur de recherche par tour ; 8 des 13 agents ne font rien et réécrivent quand même le dialogue | tous les `Dialog*VAgent.cs` |
| Moyen | `ServiceReturn.Success` reste `true` quand un enregistrement échoue sur les anciens points d'entrée | `Core/Models/ServiceReturn.cs:13` |
| Moyen | Deux chemins de recherche ne vérifient pas `IsValid` : une panne ressemble à « aucun résultat », ce qui peut créer des doublons à la publication | `Core/Data/ElasticRepository.cs:487`, `:812` |
| Moyen | Le NER tourne 3 à 4 fois par tour | `DialogEntityVAgent.cs:102`, `DialogProcessorVAgent.cs:104`, `DialogContextUpdateVAgent.cs:117` |
| Moyen | Lectures une par une des entités d'ontologie, deux fois par tour, alors que l'ontologie est déjà en mémoire | `Server/Knowledge/EntityKnowledge.cs:25`, `DialogEntityVAgent.cs:164` |
| Moyen | Un chargement raté des données de référence (ontologie, intels, patterns) n'est pas retenté avant 5 à 10 minutes | `Ner/NerService.cs:187`, `Intels/IntelMatcher.cs:191` |
| Moyen | Trois agents analysent encore tout le message, historique écrit par le chat compris | `DialogPreProcessingVAgent.cs:94`, `DialogEntityVAgent.cs:98`, `DialogSymbolicAnalysisVAgent.cs:94` |
| Faible | Une requête `/aiassistant` sans message utilisateur est acceptée et reçoit une réponse construite sur le seul contexte de la conversation | `Server/Program.cs` |
| Faible | Lecture-modification-écriture sans contrôle de version sur le contexte de conversation (soupçon sur la fréquence) | `DialogContextUpdateVAgent.cs:120` |
| Faible | Statistiques « top » calculées en mémoire sur 10 000 documents au plus | `Core/SessionManager.cs:533` |
| Faible | `/search` et `/recent` bloquent sur `Task.WaitAll` et masquent toute erreur | `Core/SessionManager.cs:677` |
| Faible | `POST /interview/transcript` avec `paragraphs: null` supprime la transcription puis échoue | `Server/ApiEndpoints.cs:819` |
| Faible | Les surcharges `Save` de liste sont paresseuses (rien n'est écrit si le résultat n'est pas parcouru) | `Core/Data/ElasticRepository.cs:21` |
| Faible | `GET /log/since` peut charger jusqu'à un million de lignes | `Core/SessionManager.cs:205` |

Vérifié sans défaut : pas d'injection dans les requêtes (DSL NEST, saisies échappées, noms d'indices filtrés), clients
HTTP partagés, expressions régulières des utilisateurs limitées à 100 ms.

### Passerelle LLM (`KAONOMI.LLMGateway`)

| Niveau | Constat | Où |
|---|---|---|
| Grave | Les prompts longs sont tronqués avant l'embedding : deux questions différentes partageant un long début obtiennent le même vecteur | `Cache/Embedders.cs:113` |
| Grave | La clé de cache ignore des éléments qui changent la réponse (`options`, `max_tokens`, `stop`, `suffix`, messages après le dernier message utilisateur) | `Proxy/LlmRequest.cs:47` |
| Moyen | Les entrées gardées avec un autre moteur d'embedding ne sont jamais recalculées : elles occupent le cache sans jamais répondre (301 entrées aujourd'hui, dont 16 dans ce cas) | `Cache/SemanticCache.cs:125`, `Gateway.cs:100` |
| Faible | Recherche et éviction linéaires sous un seul verrou : sans effet à 5 000 entrées, sensible vers 50 000 ; l'éviction peut retirer des entrées écrites à la main | `Cache/SemanticCache.cs:116`, `:184` |
| Faible | Soupçon : un flux OpenAI portant une erreur puis `[DONE]` pourrait être mis en cache | `Proxy/LlmProxy.cs:231` |
| Faible | Soupçon : une réponse compressée en amont serait corrompue par le relais | `Proxy/LlmProxy.cs:380` |

### Moteurs et hôtes d'agents

| Niveau | Constat | Où |
|---|---|---|
| Moyen | Reflex écrit jusqu'à 100 documents par seconde dans le moteur de recherche depuis des étapes vides ; les quatre autres hôtes environ 10 par seconde chacun | `KAONOMI.Reflex/Program.cs:86` |
| Moyen | MongoEngine : la compaction réécrit toute la collection sous son verrou toutes les 10 000 écritures ; un instantané de plus de 2 Go ne se recharge pas | `MongoEngine/Storage/Collection.cs:451`, `:517` |
| Moyen | MongoEngine : index uniques vérifiés en parcourant tous les documents ; aucune requête n'utilise d'index | `Collection.cs:322` |
| Moyen | MongoEngine : l'écriture est appliquée en mémoire avant d'être journalisée | `Collection.cs:344` |
| Moyen | SearchEngine : une suppression d'indice ratée peut ressusciter l'indice ou empêcher le démarrage | `SearchEngine/Engine/IndexManager.cs:112` |
| Moyen | WebScrapper : rien n'est enregistré avant la fin complète d'un passage ; Ctrl+C perd tout | `Agent.WebScrapper/Agents/WebScrapperAgents.cs:188` |
| Faible | MongoEngine : les curseurs ne sont libérés que lorsqu'une connexion se ferme | `MongoEngine/Engine/CommandHandler.cs:48` |
| Faible | MongoEngine : les noms de collection avec caractères hors Latin-1 ne se relisent pas ; `Users` et `users` se confondent sous Windows | `Collection.cs:54` |
| Faible | SearchEngine : chaque nouveau champ dynamique recopie le mapping et réécrit `meta.json` | `SearchEngine/Engine/Mapping.cs:89` |
| Faible | SearchEngine : un `_bulk` applique les premières actions puis rejette toute la requête | `SearchEngine/Http/EsHttpHandler.cs:514` |

### Chats : optimisations non faites

Écartées parce qu'elles changent un comportement visible, demandent une refonte, ou restent à mesurer.

| Chat | Constat | Où |
|---|---|---|
| Les deux | La conversation n'est pas virtualisée : chaque message garde ses contrôles (sensible seulement sur de longues sessions) | `Scripts/Eve/ChatPanel.cs:551`, `KAONOMI.Chat/Chat/ChatPanel.cs` |
| Les deux | Un processus Piper neuf par réponse lue (soupçon : la plus grande part du délai avant le premier mot) | `Shared/Services/SpeechService.cs:188` |
| Les deux | Chaque phrase lue : le fichier WAV relu jusqu'à 5 fois et réécrit jusqu'à 4 fois par les effets de voix | `Shared/Services/SpeechService.cs:275` |
| Les deux | Chaque visage 3D reconstruit sa grille et recharge sa texture (deux fois au démarrage côté Godot) | `Scripts/FaceAvatar.cs:159`, `KAONOMI.Chat/Avatar/Avatar3D.cs:106` |
| Les deux | Fenêtres restaurées toutes ensemble 300 ms après le démarrage | `KAONOMI.Chat/MainWindow.cs`, `Scripts/Main.cs:107` |
| Godot | Le graphe de relations 3D reconstruit son maillage à chaque image, même caché | `Scripts/Tools/Knowledge/RelationGraph3D.cs:898` |
| Godot | Toutes les fenêtres au premier plan redessinent leur contenu à chaque image (gain à mesurer) | `Scripts/Eve/WindowDeck.cs:29` |
| Avalonia | Les fenêtres réduites gardent minuteurs et liaisons actifs | `KAONOMI.Chat/Desk/DeskArea.cs:119` |
| Avalonia | Le fond animé continue sous le mode vocal | `KAONOMI.Chat/MainWindow.cs` |
| Avalonia | `Ui.Brush` cherche la ressource à chaque accès, y compris dans les rendus | `KAONOMI.Chat/Desk/Ui.cs:23` |
| Avalonia | `DailyUi.Items` détache et rattache toutes les lignes à chaque changement | `KAONOMI.Chat/Tools/Daily/DailyUi.cs:172` |
| Partagé | `ProcessWindowViewModel` modifie des propriétés observées depuis un fil de minuteur | `Shared/ViewModels/ProcessWindowViewModel.cs:78` |
| Partagé | L'enregistrement automatique de `/write` sérialise tout le manuscrit sur le fil de l'interface | `Shared/ViewModels/GhostWriterWindowViewModel.cs:236` |
| Partagé | `config.yml` lu et réécrit sur le fil de l'interface à chaque changement de preset | `Shared/ViewModels/MainViewModel.cs:180` |

## 3. Par où continuer

1. **Retirer de la séquence les 8 agents vides** : le plus gros gain de latence pour le moins de code.
2. **Le cache de la passerelle** : refuser le cache au-delà de la fenêtre de l'embedding, et compléter la clé de
   contexte.
3. **Reflex et les hôtes d'agents** : cesser d'écrire des documents depuis des étapes vides.

## 4. Limites de cet audit

- Lecture seule : aucun profilage, aucune mesure avant/après. Seule mesure prise : le chat Avalonia au repos après
  optimisation, 0,8 % de la machine (26 % d'un cœur) pour 342 Mo.
- Certains fichiers n'ont été que parcourus par recherche de motifs : environ 170 des 195 fichiers du code partagé, le
  CRUD répétitif de `SessionManager.cs` et `ApiEndpoints.cs`, la plupart des modèles, et les projets Admin,
  ImmuneSystem et les hôtes d'agents.
- Pas de revue de sécurité au-delà des injections dans les requêtes.
- Les optimisations appliquées compilent et un dialogue complet passe par le chat Avalonia et le serveur ; elles n'ont
  pas été vérifiées une à une à l'écran, et la lecture vocale par PortAudio n'a pas été écoutée.
