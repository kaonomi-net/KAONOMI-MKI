[www.kaonomi.net](https://www.kaonomi.net)

# KAONOMI.ImmuneSystem

A console application that walks the folders of the machine and finds:

- **images generated (or edited) by an AI**, from what the images declare about themselves;
- **code in Office files** (Word, Excel, PowerPoint, Visio, Office 2007+ and 97-2003, RTF): macros, and every other way a document can hold or run code;
- in the **browser histories** of the user: the generative AI services visited, and the images and Office files downloaded, tied to what the scan found in them.

Everything found goes into one JSON report. The project is self-contained: .NET 8, one NuGet package (`Microsoft.Data.Sqlite`, to read the histories), no other KAONOMI project, no service (no Ollama, no Elasticsearch). It only reads files: it never opens them in Office, never runs a macro and never sends anything on the network.

## Run

```bash
dotnet run --project KAONOMI.ImmuneSystem                         # your home folder
dotnet run --project KAONOMI.ImmuneSystem -- ~/Downloads ~/Documents
dotnet run --project KAONOMI.ImmuneSystem -- --machine            # the whole machine
dotnet run --project KAONOMI.ImmuneSystem -- --out - --quiet | jq '.aiImages[].path'
```

| Option | Role |
| --- | --- |
| `folder…` | Folders to scan (default: the home folder) |
| `--machine` | The whole machine: every fixed drive on Windows, `/` on macOS and Linux (without `/System`, `/usr`, `/Applications`, `/private/var`, `/Volumes`, `/proc`…) |
| `--images-only`, `--office-only` | Only one of the two searches |
| `--all` | Also the hidden folders (`.git`, `.cache`…), `Library`, `AppData`, `node_modules`, `obj`, application bundles (`.app`) and the Photos library, left out by default |
| `--out <file.json>` | Where the report goes (default `immune-system-<yyyyMMdd-HHmmss>.json` in the current folder); `-` writes it on the standard output |
| `--min-confidence low\|medium\|high` | Keeps only the AI images at least this sure (default `low`) |
| `--no-code` | Does not copy the code into the report, only what it does |
| `--max-code <n>` | Characters of code kept per finding (default 20,000) |
| `--no-code-text` | Does not look for source code written as text in Word and PowerPoint |
| `--no-history` | Does not read the browser histories |
| `--history-home <folder>` | Reads the browser profiles of this home folder (default: yours) |
| `--max-size <MB>` | Office files larger than this are skipped (default 200) |
| `--threads <n>` | Files read at the same time (default: the number of cores) |
| `--quiet` | Nothing on the console |
| `--progress` | For a program that runs the scan (the chat's [`/immune`](../KAONOMI.ChatGodot/README.md)): JSON lines on the error output, `{"event":"progress",…}` 4 times a second (files, folders, images and Office files read, findings, current folder), `{"event":"found","type":"image"\|"office",…}` for each finding, `{"event":"report","path":…}` at the end; a line `cancel` on the standard input stops the scan and still writes the report |

The progress and the findings are written on the error output, so `--out -` gives a clean JSON on the standard output. Ctrl+C stops the scan and still writes the report (`"interrupted": true`).

Exit code: `0` nothing found, `1` something found, `2` bad command line.

## AI images

Formats: JPEG, PNG, WebP, HEIC/HEIF, AVIF, JPEG XL, TIFF, GIF, BMP. Only the metadata are read (the APPn segments of a JPEG, the text chunks of a PNG, the boxes of a HEIC…), not the pixels, so a large photo library is scanned quickly.

| Clue | Confidence |
| --- | --- |
| IPTC digital source type `trainedAlgorithmicMedia` (in XMP or in a C2PA / Content Credentials manifest): Gemini, ChatGPT / DALL·E, Adobe Firefly, Meta AI, Microsoft Designer… | high, `ai-generated` |
| IPTC digital source type `compositeWithTrainedAlgorithmicMedia` (Photoshop generative fill…) | high, `ai-edited` |
| Generation parameters left by the generator: AUTOMATIC1111 / Forge (`parameters`, or EXIF UserComment), ComfyUI (`prompt` + `workflow`), InvokeAI, NovelAI, Fooocus, Midjourney (`Description` with `Job ID`) | high |
| The name of a generator in the metadata (Midjourney, DALL·E, OpenAI, Stable Diffusion, Firefly, Imagen, FLUX, Ideogram, Leonardo, Grok…), or "AI generated" | medium (high with a second clue) |
| The file name given by a generator: `Gemini_Generated_Image_…`, `DALL·E 2024-…`, `ChatGPT Image …`, `ComfyUI_00001_`, `00012-123456789.png`, `OIG3.…`, Midjourney names… | low |

The report gives, for each image, the generator, whether it carries Content Credentials, the prompt when the generator left it, and the clues.

Limits: an image whose metadata were removed (a screenshot, a copy sent through a messaging app, a re-save by an editor) cannot be recognised this way. Invisible watermarks (Google SynthID, the watermarks of Meta and OpenAI) need their maker's detectors and are not read. The tool does not guess from the pixels, so it has no false positives from the look of an image. The signature of a C2PA manifest is not checked.

## Code in Office files

The file is recognised from its first bytes (a `.doc` that is not a Word file, such as the `.doc` files of Lucene, is ignored). Embedded documents are opened too, up to three levels (a workbook with macros inside a Word document is reported with its path `word/embeddings/…xlsm!/xl/vbaProject.bin/VBA/Module1`).

| Kind | What | Severity |
| --- | --- | --- |
| `vba-macro` | VBA modules, decompressed from the `vbaProject.bin` ([MS-OVBA]) or the `Macros` / `_VBA_PROJECT_CUR` storage of 97-2003 files, with their source | `low`; `medium` if they run by themselves or call the system; `high` if both |
| `xlm-macro` | Excel 4.0 macro sheets (`xl/macrosheets`, BOUNDSHEET of `.xls`, hidden or very hidden), their formulas, and the `Auto_Open` name that starts them | `medium`, `high` with `EXEC`, `CALL`, `REGISTER`… or very hidden |
| `python-in-excel` | `=PY()` cells | `low` |
| `power-query` | Power Query queries, with their M code (decoded from the `DataMashup`) | `low`, `medium` when they read the web or a database |
| `dde` | DDE fields in Word (`DDEAUTO`), DDE links and `app|'cmd'!A1` formulas in Excel | `high` |
| `remote-template` | Template loaded from the web or a network share when the document opens | `high` |
| `external-object` | OLE object, frame or sub-document loaded from outside | `medium` / `high` |
| `activex` | ActiveX controls (their events run VBA) | `medium` |
| `ole-object` | Equation Editor 3.0 objects (CVE-2017-11882), OLE objects of an RTF (with `\objupdate`) | `medium` / `high` |
| `embedded-file` | Programs and scripts embedded in the package or in a Package object (`.exe`, `.js`, `.vbs`, `.ps1`, `.hta`, `.bat`, `.sh`, `.scpt`…) | `high` |
| `ribbon-callbacks` | Custom ribbon calling macros (`onLoad` runs at opening) | `low` / `medium` |
| `office-addin` | Office JavaScript add-in referenced by the document | `info` |
| `code-text` | Source code written as text in Word documents and PowerPoint slides and notes: blocks of three lines or more in a monospaced font or a code style that look like code, with the language (C#, Python, JavaScript, SQL, VBA, Shell, HTML…) | `info` |
| `encrypted` | File protected by a password: it cannot be inspected | `info` |

For the macros, the report lists what makes them run by themselves (`autoExec`: `AutoOpen`, `Document_Open`, `Workbook_Open`, `Auto_Open`, ActiveX events…), what they can do (`suspicious`: run programs, PowerShell, `CreateObject`, downloads, writing or deleting files, Windows API, memory, changing their own code, registry, start with Windows, `MacScript`…), and the addresses they name (`iocs`). A severity is how much the code can do, not a verdict: the Solver add-in of Microsoft Excel is `high` (it starts with `Auto_Open` and calls `Application.Run`).

Not covered: VBA of PowerPoint 97-2003 files (`.ppt`), the formulas of `.xlsb` macro sheets (binary; the sheet is reported), p-code without source (VBA stomping), Office Scripts (kept in OneDrive, not in the file), OpenDocument files.

## Browser histories

The histories of the current user are read: the Chromium browsers (Chrome, Chrome Beta and Canary, Chromium, Edge, Brave, Arc, Vivaldi, Opera, Opera GX, Dia, Comet; every profile), Firefox, Zen and LibreWolf, and Safari, on macOS, Windows and Linux. Each database is copied first (the browser keeps it locked), with its write-ahead log so the last visits are there, and the copy is deleted after.

- **AI services**: about 70 generative AI services in six kinds (`chat`: ChatGPT, Claude, Gemini, Copilot, Perplexity, Le Chat, DeepSeek, Grok…; `image`: Midjourney, Leonardo, Ideogram, Firefly, Bing Image Creator, ImageFX, Civitai…; `video`: Sora, Runway, Pika, Luma, Kling…; `audio`: Suno, Udio, ElevenLabs; `code`: GitHub Copilot, Cursor, v0, Bolt, Lovable…; `writing`: Gamma, Jasper, QuillBot…). For each one: the visits, the first and the last, the browsers, the visits per month. Only these counts are kept: no address, no page title, nothing about the other sites (only their total, to give the share of AI).
- **Downloads** (Chromium and Firefox): the images and Office files, with their source, the page that offered them, the date, whether they are still there, and what the scan found in them (`ai-image`, `office-code`).
- **Origins**: each AI image and each Office file found gets the address it was downloaded from (`origin`): the browser downloads, else what the system wrote on the file (macOS: `kMDItemWhereFroms`, else its quarantine event, or at least "downloaded with Chrome"; Windows: `Zone.Identifier`).
- **Clue**: an image downloaded from an AI service is an AI image (medium confidence), even when its metadata say nothing.

On macOS, Safari's history is protected: give Full Disk Access (System Settings › Privacy & Security) to the terminal or the app that runs the scan, else the profile is reported unreadable with that hint. A process started from a sandbox may not read the other browsers either.

## Report

```json
{
  "tool": "KAONOMI.ImmuneSystem",
  "version": "1.0.0",
  "machine": "MacBook-Pro",
  "user": "jerome",
  "os": "Darwin 27.0.0 …",
  "startedAt": "2026-11-04T10:05:12",
  "finishedAt": "2026-11-04T10:05:14",
  "durationSeconds": 1.3,
  "interrupted": false,
  "roots": ["/Users/jerome"],
  "options": { "machine": false, "images": true, "office": true, "all": false, "minConfidence": "low", "…": "…" },
  "stats": { "directories": 5663, "files": 60032, "imagesInspected": 923, "officeFilesInspected": 102,
             "skippedTooLarge": 0, "errors": 0, "aiImages": 7, "officeFilesWithCode": 0 },
  "aiImages": [
    {
      "path": "/Users/jerome/Downloads/Gemini_Generated_Image_3tnf5z3tnf5z3tnf.jpeg",
      "size": 3590896,
      "modified": "2026-10-30T23:04:11",
      "format": "jpeg",
      "kind": "ai-generated",
      "confidence": "high",
      "generator": "Google Generative AI (Gemini / Imagen)",
      "contentCredentials": true,
      "evidence": [
        { "source": "c2pa", "detail": "IPTC digital source type: trainedAlgorithmicMedia (created by a generative AI)", "confidence": "high" },
        { "source": "c2pa", "detail": "Names the generator: Google Generative AI", "confidence": "medium" },
        { "source": "file-name", "detail": "Named as the files of Gemini", "confidence": "low" }
      ]
    }
  ],
  "officeFiles": [
    {
      "path": "/Users/jerome/Documents/budget.xlsm",
      "format": "xlsm",
      "container": "ooxml",
      "severity": "high",
      "findings": [
        {
          "kind": "xlm-macro",
          "severity": "high",
          "location": "xl/macrosheets/sheet1.xml",
          "description": "Excel 4.0 (XLM) macro sheet, 2 formula(s)",
          "autoExec": ["Auto_Open → Macro1!$A$1"],
          "suspicious": ["Runs programs (EXEC)"],
          "lines": 2,
          "code": "EXEC(\"calc.exe\")\nHALT()"
        }
      ]
    }
  ],
  "browsers": {
    "visits": 48210, "aiVisits": 3120, "from": "2025-11-02T09:12:40", "to": "2026-11-04T10:31:14",
    "profiles": [
      { "browser": "Chrome", "profile": "Default", "path": "…/Google/Chrome/Default/History", "readable": true, "visits": 41003, "aiVisits": 2950 },
      { "browser": "Safari", "profile": "Default", "path": "…/Safari/History.db", "readable": false, "error": "Protected by macOS: give Full Disk Access…" }
    ],
    "aiServices": [
      { "name": "ChatGPT", "category": "chat", "visits": 2210, "firstVisit": "2025-11-03T08:01:12", "lastVisit": "2026-11-04T09:58:40",
        "browsers": ["Chrome"], "months": { "2026-09": 240, "2026-10": 310, "2026-11": 28 } }
    ],
    "downloads": [
      { "browser": "Chrome", "profile": "Default", "file": "/Users/jerome/Downloads/image.png", "kind": "image",
        "source": "https://files.oaiusercontent.com/…", "referrer": "https://chatgpt.com/c/…", "time": "2026-11-03T10:31:14",
        "size": 1830221, "exists": true, "service": "ChatGPT", "finding": "ai-image" }
    ]
  },
  "errors": []
}
```

The AI images are sorted by confidence, the Office files by severity, then by path. `errors` lists the first 500 files that could not be read.

## Files

| File | Role |
| --- | --- |
| `Program.cs` | Command line, parallel scan, progress, report |
| `Report.cs` | The records of the JSON report |
| `Scanning/Options.cs`, `Scanning/FileWalker.cs` | Options; the walk of the folders (no links followed, folders left out) |
| `Images/ImageMetadata.cs` | Reads the metadata of JPEG, PNG, WebP, HEIC/AVIF/JXL (and the beginning and end of the other formats) |
| `Images/AiImageDetector.cs` | The clues of an AI image |
| `Office/CompoundFile.cs` | Reader of compound files ([MS-CFB]) |
| `Office/VbaProject.cs` | VBA modules and their decompression ([MS-OVBA]) |
| `Office/CodeAnalysis.cs` | Auto-run names, suspicious calls and addresses in VBA and XLM |
| `Office/CodeText.cs` | Source code written as text in Word and PowerPoint |
| `Office/OfficeInspector.cs` | The inspection of a package, a compound file or an RTF |
| `History/HistoryScanner.cs` | Finds and reads the browser histories (copies, SQLite), counts the AI visits, lists the downloads |
| `History/AiServices.cs` | The generative AI services, by kind, recognised by their address |
| `History/FileOrigin.cs` | Where a file was downloaded from, as the system wrote it (macOS, Windows) |
| `History/BrowserReport.cs` | The records of the `browsers` part of the report |
