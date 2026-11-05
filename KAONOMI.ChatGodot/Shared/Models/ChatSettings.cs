using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KAONOMI.Chat.Models;

/// <summary>3D animation shown in the Kaonomi band (<c>animation</c> in config.yml).</summary>
public enum SceneAnimation
{
    /// <summary>The 3D Kaonomi robot face whose mouth follows the voice and the streamed words.</summary>
    Mari,

    /// <summary>Photo portrait of Jerome in 3D, whose mouth follows the voice and the streamed words.</summary>
    Jerome,

    /// <summary>Red eye of a computer in 3D: its core, halo and flare glow with the voice and the streamed words.</summary>
    Hal,

    /// <summary>
    /// Cute-horror robot doll holding a bloody knife, in 3D: her robot jaw drops with the voice, her spiral pupils spin,
    /// her hearts glow and a glint runs along the blade.
    /// </summary>
    MarieKilleuse,

    /// <summary>
    /// Robot boy with green eyes and glowing chest lights, in 3D: his mouth follows the voice, he blinks, and his eyes
    /// and lights glow with the voice.
    /// </summary>
    Musuko,

    /// <summary>
    /// Worn white robot with a mask face (two eye holes, red marks, a mouth slot), in 3D: the chin under the slot drops
    /// with the voice, and an amber light shines in the eye holes and the mouth while it speaks.
    /// </summary>
    Kaonami,

    /// <summary>
    /// White humanoid robot with a tablet on its chest, in 3D: its mouth opens with the voice, the rings of its eyes and
    /// its ear light up in blue while it speaks, and a voice wave runs on its tablet.
    /// </summary>
    Pepper,

    /// <summary>
    /// Pale android child with glowing green eyes and green mechanical ears and shoulders, in 3D: its mouth follows the
    /// voice, its pupils dilate and its eyes glow brighter with the voice, it blinks, and a green scan line runs over it.
    /// </summary>
    Green,

    /// <summary>
    /// Jerome filmed (one frame of a video, cut out), in 3D: his jaw drops with the voice and the streamed words, and he
    /// blinks every few seconds.
    /// </summary>
    JeromeLive,

    /// <summary>
    /// Marvin, the gloomy white robot with a round head and two green eye slits under a visor line, in 3D: he has no
    /// mouth; his eyes glow brighter with the voice and breathe slowly when he is quiet, a green light runs along the
    /// visor while he speaks, and his eyes go dark for a moment now and then.
    /// </summary>
    Marvin,

    /// <summary>
    /// Circuit, an android woman whose skin opens on circuits, in 3D: her jaw drops with the voice and the streamed
    /// words, she blinks every few seconds, and her green circuits glow with the voice while a light runs down them.
    /// </summary>
    Circuit,

    /// <summary>
    /// Motuko, an android woman with dark hair in a white armour, in 3D: her jaw drops with the voice and the streamed
    /// words, she blinks every few seconds, and the amber lights of her armour glow with the voice.
    /// </summary>
    Motuko,

    /// <summary>
    /// Kaonomi, a heavy stone-grey robot with an egg-shaped head in a hood and a single yellow eye, in 3D: it has no
    /// mouth; its eye glows brighter with the voice and breathes slowly when it is quiet, and a ring of light turns
    /// around the eye while it speaks.
    /// </summary>
    Kaonomi,

    /// <summary>
    /// Lin, a woman with dark hair tied back in green scrubs (a photo, cut out), in 3D: her jaw drops with the voice and
    /// the streamed words, and she blinks every few seconds.
    /// </summary>
    Lin,

    /// <summary>Holographic display of an AI assistant: reticle, gyroscope rings, glowing core and voice spectrum.</summary>
    Jarvis,

    /// <summary>No animation: the band only shows its background and the clock.</summary>
    None
}

/// <summary>
/// Settings read from <c>config.json</c> (server, model, prompt) and <c>config.yml</c>
/// (Ollama URL and JSON client directories), next to the executable.
/// Values in <c>config.yml</c> override the matching JSON fields when present.
/// </summary>
public sealed class ChatSettings
{
    [JsonPropertyName("ollamaUrl")]
    /// <summary>Base URL of the local Ollama API.</summary>
    public string OllamaUrl { get; set; } = "http://localhost:11434";

    [JsonPropertyName("serverUrl")]
    /// <summary>
    /// Base URL of the Kaonomi REST server (<c>serverUrl</c> in config.yml; a <c>serverUrl</c> left in config.json is
    /// still read, the YAML value wins).
    /// </summary>
    public string ServerUrl { get; set; } = "https://localhost:49165";

    [JsonPropertyName("searchEngineUrl")]
    /// <summary>Address of KAONOMI.SearchEngine (<c>searchEngineUrl</c> in config.yml), stopped by <c>/bye</c> before the chat closes.</summary>
    public string SearchEngineUrl { get; set; } = "http://localhost:9200";

    [JsonPropertyName("llmGatewayUrl")]
    /// <summary>Address of KAONOMI.LLMGateway (<c>llmGatewayUrl</c> in config.yml), driven by <c>/gateway</c> and started from <c>/process</c>.</summary>
    public string LlmGatewayUrl { get; set; } = "http://localhost:11435";

    [JsonPropertyName("mongoEngineAddress")]
    /// <summary>Address of KAONOMI.MongoEngine (<c>mongoEngineAddress</c> in config.yml), stopped by <c>/bye</c> after the search engine.</summary>
    public string MongoEngineAddress { get; set; } = "localhost:27017";

    [JsonPropertyName("byeStopsEngines")]
    /// <summary><c>/bye</c> stops the search engine, then the Mongo engine, before the chat closes (<c>byeStopsEngines</c> in config.yml).</summary>
    public bool ByeStopsEngines { get; set; } = true;

    [JsonPropertyName("llmmodel")]
    /// <summary>Default LLM model name from configuration.</summary>
    public string Model { get; set; } = "mistral-small3.2:latest";

    [JsonPropertyName("systemPrompt")]
    /// <summary>Optional system message prepended to each chat request.</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>Language of the application (<c>language</c> in config.yml), e.g. FR or EN.</summary>
    public string Language { get; set; } = "FR";

    /// <summary>Answers read aloud with Piper (<c>text2speech</c> in config.yml; off when missing).</summary>
    public bool TtsEnabled { get; set; }

    /// <summary>Piper executable (<c>tts.piper</c> in config.yml). Relative paths are resolved from the executable folder.</summary>
    public string TtsPiperPath { get; set; } = "piper";

    /// <summary>Folder of the Piper voice models (<c>tts.voicesDirectory</c> in config.yml).</summary>
    public string TtsVoicesDirectory { get; set; } = "voices";

    /// <summary>
    /// Piper voice of each language (<c>tts.voices</c> in config.yml), e.g. FR: a voice name of
    /// <see cref="TtsVoicesDirectory"/> (<c>fr_FR-siwis-medium</c>) or the path of an .onnx file.
    /// </summary>
    public Dictionary<string, string> TtsVoices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Speaker of each language for the voices that have several (<c>tts.speakers</c>): name or number.</summary>
    public Dictionary<string, string> TtsSpeakers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Speech speed (<c>tts.speed</c> in config.yml): 1 = normal, 1.2 = 20 % faster. Kept between 0.25 and 4.</summary>
    public double TtsSpeed { get; set; } = 1;

    /// <summary>Pitch of the voice (<c>tts.pitch</c> in config.yml): 1 = normal, 1.15 = 15 % higher. Kept between 0.5 and 2.</summary>
    public double TtsPitch { get; set; } = 1;

    /// <summary>Robotic effect on the voice (<c>tts.robot</c> in config.yml): 0 = natural, 1 = plainly robotic. Kept between 0 and 1.</summary>
    public double TtsRobot { get; set; }

    /// <summary>Vibrato of the voice (<c>tts.vibrato</c> in config.yml): 0 = steady, 1 = a singing vibrato. Kept between 0 and 1.</summary>
    public double TtsVibrato { get; set; }

    /// <summary>
    /// Sheen of an AI assistant on the voice (<c>tts.assistant</c> in config.yml): 0 = none, 1 = the voice of a film's AI
    /// (soft doubling, small-room reverb, presence). Kept between 0 and 1.
    /// </summary>
    public double TtsAssistant { get; set; }

    /// <summary>
    /// Horror-film treatment of the voice (<c>tts.horror</c> in config.yml): 0 = none, 1 = full (a demonic shadow an
    /// octave below, breath, stutters of a broken doll, a dark hall reverb). Kept between 0 and 1.
    /// </summary>
    public double TtsHorror { get; set; }

    /// <summary>
    /// Absolute path to the Piper executable: <see cref="TtsPiperPath"/> when it exists, otherwise the same venv for
    /// this system (venv/bin/piper on macOS / Linux, venv\Scripts\piper.exe on Windows), the standard install in
    /// ~/kaonomi/piper, or piper found in the PATH; <see cref="TtsPiperPath"/> as written when none exists.
    /// </summary>
    public string ResolvedTtsPiperPath
    {
        get
        {
            var configured = SafeResolve(TtsPiperPath);
            foreach (var candidate in PiperCandidates(TtsPiperPath))
                if (candidate is not null && File.Exists(candidate))
                    return candidate;
            return configured ?? TtsPiperPath;
        }
    }

    /// <summary>Places where Piper may be, most likely first.</summary>
    private static IEnumerable<string?> PiperCandidates(string configured)
    {
        var windows = OperatingSystem.IsWindows();
        var executable = windows ? "piper.exe" : "piper";
        yield return SafeResolve(configured);

        // The venv of the other system: bin/piper <-> Scripts\piper.exe.
        var normalized = configured.Replace('\\', '/');
        yield return SafeResolve(windows
            ? normalized.Replace("/bin/piper", "/Scripts/piper.exe")
            : normalized.Replace("/Scripts/piper.exe", "/bin/piper"));

        // Standard install (python3 -m venv ~/kaonomi/piper/venv && venv/bin/pip install piper-tts).
        yield return Path.Combine(KaonomiHome, "piper", "venv", windows ? "Scripts" : "bin", executable);

        // piper in the PATH.
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(folder))
                yield return Path.Combine(folder.Trim(), executable);
    }

    /// <summary><see cref="ResolveDirectory"/>, or null when the path is not valid on this system.</summary>
    private static string? SafeResolve(string path)
    {
        try
        {
            return ResolveDirectory(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Absolute path to the voice model of <see cref="Language"/>: the one of <c>tts.voices</c>, else a voice of this
    /// language installed in <see cref="TtsVoicesDirectory"/> (<see cref="InstalledTtsVoice"/>); null when there is none.
    /// </summary>
    public string? ResolvedTtsVoice => TtsVoices.TryGetValue(Language, out var voice)
        ? voice.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) || voice.Contains('/') || voice.Contains('\\')
            ? ResolveDirectory(voice)
            : Path.Combine(ResolveDirectory(TtsVoicesDirectory), voice + ".onnx")
        : InstalledTtsVoice(Language);

    /// <summary>Locales of the Piper voices of each language, the preferred first.</summary>
    private static readonly Dictionary<string, string[]> VoiceLocales = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FR"] = ["fr_FR"],
        ["DE"] = ["de_DE"],
        ["EN"] = ["en_US", "en_GB"],
        ["NL"] = ["nl_NL", "nl_BE"],
        ["JA"] = ["ja_JP"]
    };

    /// <summary>
    /// A voice of <paramref name="language"/> installed in <see cref="TtsVoicesDirectory"/> (the voices with one speaker
    /// first, then the best quality: high, medium, low, x_low), or null when there is none.
    /// </summary>
    public string? InstalledTtsVoice(string language)
    {
        var directory = ResolveDirectory(TtsVoicesDirectory);
        if (!VoiceLocales.TryGetValue(language, out var locales) || !Directory.Exists(directory))
            return null;
        static int Quality(string name) => name.EndsWith("-high") ? 0 : name.EndsWith("-medium") ? 1 : name.EndsWith("-low") ? 2 : 3;
        // Voices of many speakers (mls, libritts, vctk…) start on a random one: a single voice is preferred.
        static bool ManySpeakers(string name) => name.Contains("-mls") || name.Contains("libritts") || name.Contains("vctk") || name.Contains("arctic") || name.Contains("aru-");
        return locales
            .SelectMany(locale => Directory.EnumerateFiles(directory, locale + "-*.onnx")
                .Select(path => (Path: path, Name: Path.GetFileNameWithoutExtension(path), Locale: Array.IndexOf(locales, locale))))
            .Where(v => File.Exists(v.Path + ".json"))
            .OrderBy(v => v.Locale).ThenBy(v => ManySpeakers(v.Name)).ThenBy(v => Quality(v.Name)).ThenBy(v => v.Name, StringComparer.Ordinal)
            .Select(v => v.Path)
            .FirstOrDefault();
    }

    /// <summary>Speaker of <see cref="Language"/> (name or number), or null for the first one.</summary>
    public string? ResolvedTtsSpeaker => TtsSpeakers.TryGetValue(Language, out var speaker) ? speaker : null;

    /// <summary>Theme applied at startup (<c>theme</c> in config.yml; kaonomi when missing): kaonomi, white, black, cyberpunk, matrix, apple, windows, linux, glass, swift, mari, jarvis, kandinsky, pepper, hal9000, steam.</summary>
    public string Theme { get; set; } = "kaonomi";

    /// <summary>
    /// 3D animation of the Kaonomi band (<c>animation</c> in config.yml; MARI when missing or unknown):
    /// MARI = the 3D robot face, JEROME = the 3D photo portrait, HAL = the red eye of a computer,
    /// MARIEKILLEUSE = the robot doll with a knife, MUSUKO = the robot boy, KAONAMI = the masked robot, PEPPER = the robot with a tablet, CIRCUIT = the android woman with green circuits, MOTUKO = the android woman in a white armour, KAONOMI = the hooded robot with one yellow eye, LIN = the woman in green scrubs,
    /// JARVIS = a holographic display (reticle, gyroscope rings, glowing core, voice
    /// spectrum), NONE = no animation.
    /// </summary>
    public SceneAnimation Animation { get; set; } = SceneAnimation.Mari;

    /// <summary>
    /// Personality of the preset in use (<c>persona</c> in config.yml, written by the presets of the <c>/config</c>
    /// window): added after the system prompt of every request; empty for none.
    /// </summary>
    public string Persona { get; set; } = string.Empty;

    /// <summary>
    /// The presets of the avatars (<c>presets</c> in config.yml): the voice, its effects, the 3D animation and the
    /// personality added to the system prompt; empty when config.yml has none (<see cref="ChatPreset.Defaults"/> are then used).
    /// </summary>
    public IReadOnlyList<ChatPreset> Presets { get; set; } = [];

    /// <summary>
    /// True: the machine does not go to sleep while the chat runs (<c>keepawake</c> in config.yml: on / off, on when
    /// missing); the screen can still turn off.
    /// </summary>
    public bool KeepAwake { get; set; } = true;

    /// <summary>The Model, Preset and History zone of the chat is shown (<c>chatoptions</c> in config.yml, on when missing).</summary>
    public bool ChatOptionsVisible { get; set; } = true;

    /// <summary>Vision model used when config.yml gives none.</summary>
    public const string DefaultVisionModel = "mistral-small3.2";

    /// <summary>
    /// Vision model of Ollama (<c>visionmodel</c> in config.yml, <see cref="DefaultVisionModel"/> when missing): the AI
    /// analysis of a picture (✨ Analyse of the image analysis window) and the AI review of the Work document (✨ AI).
    /// </summary>
    public string VisionModel { get; set; } = DefaultVisionModel;

    /// <summary>Most previous exchanges that <see cref="HistoryExchanges"/> accepts.</summary>
    public const int MaxHistoryExchanges = 20;

    /// <summary>
    /// Number of previous exchanges (a prompt and its answer) written into each prompt under "Discussions précédentes"
    /// (<c>historyExchanges</c> in config.yml, 5 when missing, from 0 to <see cref="MaxHistoryExchanges"/>); set
    /// from the drop-down above the chat, which writes it back to config.yml.
    /// </summary>
    public int HistoryExchanges { get; set; } = 5;

    /// <summary>
    /// Entity types offered in the ontology editor (<c>ontology.entityTypes</c> in config.yml).
    /// Defaults to the OntoNotes 5 NER labels when the file has none.
    /// </summary>
    public List<string> EntityTypes { get; set; } =
    [
        "PERSON", "NORP", "FAC", "ORG", "GPE", "LOC", "PRODUCT", "EVENT", "WORK_OF_ART", "LAW",
        "LANGUAGE", "DATE", "TIME", "PERCENT", "MONEY", "QUANTITY", "ORDINAL", "CARDINAL", "OTHERS"
    ];

    /// <summary>Shows the answers word by word, paced as if read aloud (<c>streaming.enabled</c> in config.yml).</summary>
    public bool StreamingEnabled { get; set; } = true;

    /// <summary>Duration of one spoken syllable for the simulated streaming, in ms (<c>streaming.syllableMs</c>).</summary>
    public int StreamingSyllableMs { get; set; } = 200;

    /// <summary>
    /// Display speed of the answers (<c>streaming.speed</c>): 1 = reading pace, 2 = twice as fast, 0.5 = twice as slow.
    /// Kept between 0.1 and 20.
    /// </summary>
    public double StreamingSpeed { get; set; } = 1;

    /// <summary>Folder for JSON client responses. Relative paths are resolved from the executable folder.</summary>
    public string JsonResultDirectory { get; set; } = "jsonresult";

    /// <summary>Folder for saved JSON requests. Relative paths are resolved from the executable folder.</summary>
    public string JsonCollectionDirectory { get; set; } = "jsoncollection";

    /// <summary>Folder of the saved prompts (<c>prompts.directory</c> in config.yml). Relative paths are resolved from the executable folder.</summary>
    public string PromptDirectory { get; set; } = "prompts";

    /// <summary>Absolute path to the saved prompts folder.</summary>
    public string ResolvedPromptDirectory => ResolveDirectory(PromptDirectory);

    /// <summary>
    /// <c>prompts.directprompt</c> in config.yml: true sends a prompt loaded with "Load prompt" at once, false leaves it
    /// in the input box until Enter is pressed.
    /// </summary>
    public bool DirectPrompt { get; set; } = true;

    /// <summary>
    /// Folder of the regexes saved by the regex assistant (<c>regexAssistant.directory</c> in config.yml). Relative paths
    /// are resolved from the executable folder.
    /// </summary>
    public string RegexDirectory { get; set; } = "regexes";

    /// <summary>Absolute path to the saved regexes folder.</summary>
    public string ResolvedRegexDirectory => ResolveDirectory(RegexDirectory);

    /// <summary>Pictures folder of the system (<c>~/Pictures</c> on macOS and Linux, the Pictures library on Windows).</summary>
    public static string SystemPicturesDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) is { Length: > 0 } pictures
            ? pictures
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");

    /// <summary>Default folder of the pictures of the graphs of the calculator: the Pictures folder of the system, kaonomi/maths.</summary>
    public static string DefaultCalculatorGraphDirectory => Path.Combine(SystemPicturesDirectory, "kaonomi", "maths");

    /// <summary>Folder of the images generated with <c>/fooocus</c>: the Pictures folder of the system, kaonomi/fooocus.</summary>
    public static string FooocusDirectory => Path.Combine(SystemPicturesDirectory, "kaonomi", "fooocus");

    /// <summary>
    /// Folder of the installed Fooocus whose Python runs the Fooocus API (<c>fooocus.directory</c> in config.yml): the
    /// folder of its launch.py, or the folder of the portable Windows build (the one of run.bat). Empty: looked for in
    /// the FOOOCUS_HOME variable, <c>~/kaonomi/fooocus</c> and the Pinokio install (see <see cref="FindFooocus"/>).
    /// </summary>
    public string FooocusHome { get; set; } = string.Empty;

    /// <summary>
    /// The installed Fooocus: the folder of its launch.py and its Python (the venv in its env folder, env\Scripts on
    /// Windows and env/bin elsewhere, or the python_embeded of the portable Windows build next to it); null when none of
    /// <see cref="FooocusHome"/>, FOOOCUS_HOME, <c>~/kaonomi/fooocus</c> or the Pinokio install holds one.
    /// </summary>
    public (string Folder, string Python)? FindFooocus()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string?[] candidates = [FooocusHome, Environment.GetEnvironmentVariable("FOOOCUS_HOME"), Path.Combine(KaonomiHome, "fooocus"),
            Path.Combine(home, "pinokio", "api", "fooocus.git", "app")];
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            var root = candidate.Trim();
            if (root == "~" || root.StartsWith("~/", StringComparison.Ordinal))
                root = Path.Combine(home, root.TrimStart('~').TrimStart('/'));
            root = ResolveDirectory(root);
            // The portable Windows build: Fooocus in a folder of the same name, beside python_embeded.
            var folder = File.Exists(Path.Combine(root, "launch.py")) ? root : Path.Combine(root, "Fooocus");
            if (!File.Exists(Path.Combine(folder, "launch.py")))
                continue;
            string[] pythons =
            [
                Path.Combine(folder, "env", "Scripts", "python.exe"),
                Path.Combine(folder, "env", "bin", OperatingSystem.IsWindows() ? "python.exe" : "python"),
                Path.Combine(Path.GetDirectoryName(folder) ?? folder, "python_embeded", "python.exe")
            ];
            if (pythons.FirstOrDefault(File.Exists) is { } python)
                return (folder, python);
        }
        return null;
    }

    /// <summary>
    /// Folder of the pictures of the graphs taken with 📷 Camera in the calculator (<c>calculator.graphDirectory</c> in
    /// config.yml; <see cref="DefaultCalculatorGraphDirectory"/> when missing). Created when the first picture is saved.
    /// </summary>
    public string CalculatorGraphDirectory { get; set; } = DefaultCalculatorGraphDirectory;

    /// <summary>
    /// Absolute path to the folder of the graph pictures: <see cref="CalculatorGraphDirectory"/>, or the default one when
    /// the path is written for another system (a Windows path on macOS…).
    /// </summary>
    public string ResolvedCalculatorGraphDirectory
    {
        get
        {
            var path = CalculatorGraphDirectory.Trim();
            return path.Length == 0 || OsPaths.IsForOtherSystem(path) ? DefaultCalculatorGraphDirectory : ResolveDirectory(path);
        }
    }

    /// <summary>Languages of the voice input (push-to-talk microphone of the chat): French, German, English.</summary>
    public static readonly string[] SpeechLanguages = ["fr", "de", "en", "ja"];

    /// <summary>Language of the voice input at start (<c>speech.language</c> in config.yml: fr, de or en; fr by default).</summary>
    public string SpeechLanguage { get; set; } = "fr";

    /// <summary>
    /// Whisper model of the voice input (<c>speech.model</c> in config.yml, a ggml file of whisper.cpp; by default
    /// <c>~/kaonomi/models/ggml-small-q5_1.bin</c>, multilingual).
    /// </summary>
    public string SpeechModel { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "kaonomi", "models", "ggml-small-q5_1.bin");

    /// <summary>Music folder of the system (<c>~/Music</c> on macOS and Linux, the Music library on Windows).</summary>
    public static string SystemMusicDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) is { Length: > 0 } music
            ? music
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");

    /// <summary>
    /// Root folder of the music player of <c>/music</c> (<c>music.directory</c> in config.yml; the Music folder of the
    /// system when missing). Relative paths are resolved from the executable folder.
    /// </summary>
    public string MusicDirectory { get; set; } = SystemMusicDirectory;

    /// <summary>Absolute path to the music folder: <see cref="MusicDirectory"/> when it exists, the Music folder of the system otherwise.</summary>
    public string ResolvedMusicDirectory => IsUsableDirectory(MusicDirectory) ? ResolveDirectory(MusicDirectory) : SystemMusicDirectory;

    /// <summary>
    /// Root folder of the picture library of <c>/pictures</c> (<c>pictures.directory</c> in config.yml; the Pictures
    /// folder of the system when missing). Relative paths are resolved from the executable folder.
    /// </summary>
    public string PicturesDirectory { get; set; } = SystemPicturesDirectory;

    /// <summary>
    /// Absolute path to the picture library: <see cref="PicturesDirectory"/> when it exists on this system, the Pictures
    /// folder of the system otherwise (a path written for another system, or a folder that was removed).
    /// </summary>
    public string ResolvedPicturesDirectory => IsUsableDirectory(PicturesDirectory) ? ResolveDirectory(PicturesDirectory) : SystemPicturesDirectory;

    /// <summary>Absolute path to the JSON client result folder.</summary>
    public string ResolvedJsonResultDirectory => ResolveDirectory(JsonResultDirectory);

    /// <summary>Absolute path to the JSON client saved-request folder.</summary>
    public string ResolvedJsonCollectionDirectory => ResolveDirectory(JsonCollectionDirectory);

    /// <summary>Loads settings from JSON next to the executable, then applies YAML overrides.</summary>
    public static ChatSettings Load(string jsonFileName = "config.json", string yamlFileName = "config.yml")
    {
        var settings = LoadJson(jsonFileName);
        ApplyYaml(settings, yamlFileName);
        return settings;
    }

    private static ChatSettings LoadJson(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path))
            return new ChatSettings();

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<ChatSettings>(stream, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            }) ?? new ChatSettings();
        }
        catch (JsonException)
        {
            return new ChatSettings();
        }
    }

    private static void ApplyYaml(ChatSettings settings, string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path))
            return;

        try
        {
            var yaml = File.ReadAllText(path);
            var document = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<ChatYamlDocument>(yaml);

            if (document is null)
                return;

            // An URL that is not absolute (no scheme, a space…) would end the application in new Uri(): the default is kept.
            if (!string.IsNullOrWhiteSpace(document.OllamaUrl) && Uri.TryCreate(document.OllamaUrl.Trim(), UriKind.Absolute, out _))
                settings.OllamaUrl = document.OllamaUrl.Trim();

            if (!string.IsNullOrWhiteSpace(document.ServerUrl) && Uri.TryCreate(document.ServerUrl.Trim(), UriKind.Absolute, out _))
                settings.ServerUrl = document.ServerUrl.Trim();

            if (!string.IsNullOrWhiteSpace(document.SearchEngineUrl) && Uri.TryCreate(document.SearchEngineUrl.Trim(), UriKind.Absolute, out _))
                settings.SearchEngineUrl = document.SearchEngineUrl.Trim();

            if (!string.IsNullOrWhiteSpace(document.MongoEngineAddress))
                settings.MongoEngineAddress = document.MongoEngineAddress.Trim();

            if (!string.IsNullOrWhiteSpace(document.LlmGatewayUrl) && Uri.TryCreate(document.LlmGatewayUrl.Trim(), UriKind.Absolute, out _))
                settings.LlmGatewayUrl = document.LlmGatewayUrl.Trim();

            if (!string.IsNullOrWhiteSpace(document.ByeStopsEngines))
                settings.ByeStopsEngines = document.ByeStopsEngines.Trim().ToLowerInvariant() is not ("false" or "off" or "no" or "0");

            if (document.Streaming?.Enabled is { } streamingEnabled)
                settings.StreamingEnabled = streamingEnabled;

            if (document.Streaming?.SyllableMs is > 0 and var syllableMs)
                settings.StreamingSyllableMs = syllableMs;

            if (document.Streaming?.Speed is > 0 and var speed)
                settings.StreamingSpeed = Math.Clamp(speed, 0.1, 20);

            if (!string.IsNullOrWhiteSpace(document.Language))
                settings.Language = document.Language.Trim();

            if (!string.IsNullOrWhiteSpace(document.Theme))
                settings.Theme = document.Theme.Trim();

            if (ParseSwitch(document.ChatOptions) is { } chatOptions)
                settings.ChatOptionsVisible = chatOptions;

            if (ParseSwitch(document.KeepAwake) is { } keepAwake)
                settings.KeepAwake = keepAwake;

            if (!string.IsNullOrWhiteSpace(document.VisionModel))
                settings.VisionModel = document.VisionModel.Trim();

            // The presets of the avatars (/config, /remote): a voice, its effects, a 3D animation and a personality.
            if (document.Presets is { Count: > 0 } presets)
            {
                settings.Presets = presets.Where(p => !string.IsNullOrWhiteSpace(p.Name)).Select(p => new ChatPreset(
                    p.Name!.Trim(), p.Description?.Trim() ?? string.Empty, p.Voice?.Trim() ?? string.Empty,
                    Math.Clamp(p.Pitch ?? 1, 0.5, 2), Math.Clamp(p.Speed ?? 1, 0.25, 4), Math.Clamp(p.Robot ?? 0, 0, 1),
                    Enum.TryParse<SceneAnimation>(p.Animation?.Trim(), ignoreCase: true, out var animation) ? animation : SceneAnimation.Mari,
                    Math.Clamp(p.Vibrato ?? 0, 0, 1), Math.Clamp(p.Assistant ?? 0, 0, 1), Math.Clamp(p.Horror ?? 0, 0, 1),
                    p.Persona?.Trim() ?? string.Empty)).ToList();
                ChatPreset.Configure(settings.Presets);
            }

            if (document.HistoryExchanges is { } historyExchanges)
                settings.HistoryExchanges = Math.Clamp(historyExchanges, 0, MaxHistoryExchanges);

            if (!string.IsNullOrWhiteSpace(document.Animation))
            {
                if (Enum.TryParse<SceneAnimation>(document.Animation.Trim(), ignoreCase: true, out var animation)
                    && Enum.IsDefined(animation))
                    settings.Animation = animation;
                else
                    System.Diagnostics.Debug.WriteLine($"Unknown animation \"{document.Animation}\" in config.yml, MARI used");
            }

            if (!string.IsNullOrWhiteSpace(document.Persona))
                settings.Persona = document.Persona.Trim();

            if (ParseSwitch(document.Text2Speech) is { } text2Speech)
                settings.TtsEnabled = text2Speech;

            if (!string.IsNullOrWhiteSpace(document.Tts?.Piper))
                settings.TtsPiperPath = document.Tts.Piper.Trim();

            if (!string.IsNullOrWhiteSpace(document.Tts?.VoicesDirectory))
                settings.TtsVoicesDirectory = document.Tts.VoicesDirectory.Trim();

            foreach (var (language, voice) in document.Tts?.Voices ?? [])
                if (!string.IsNullOrWhiteSpace(language) && !string.IsNullOrWhiteSpace(voice))
                    settings.TtsVoices[language.Trim()] = voice.Trim();

            foreach (var (language, speaker) in document.Tts?.Speakers ?? [])
                if (!string.IsNullOrWhiteSpace(language) && !string.IsNullOrWhiteSpace(speaker))
                    settings.TtsSpeakers[language.Trim()] = speaker.Trim();

            if (document.Tts?.Speed is > 0 and var ttsSpeed)
                settings.TtsSpeed = Math.Clamp(ttsSpeed, 0.25, 4);

            if (document.Tts?.Pitch is > 0 and var ttsPitch)
                settings.TtsPitch = Math.Clamp(ttsPitch, 0.5, 2);

            if (document.Tts?.Robot is >= 0 and var ttsRobot)
                settings.TtsRobot = Math.Clamp(ttsRobot, 0, 1);
            if (document.Tts?.Vibrato is >= 0 and var ttsVibrato)
                settings.TtsVibrato = Math.Clamp(ttsVibrato, 0, 1);
            if (document.Tts?.Assistant is >= 0 and var ttsAssistant)
                settings.TtsAssistant = Math.Clamp(ttsAssistant, 0, 1);
            if (document.Tts?.Horror is >= 0 and var ttsHorror)
                settings.TtsHorror = Math.Clamp(ttsHorror, 0, 1);

            var entityTypes = document.Ontology?.EntityTypes?
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (entityTypes is { Count: > 0 })
                settings.EntityTypes = entityTypes;

            if (!string.IsNullOrWhiteSpace(document.Json?.ResultDirectory))
                settings.JsonResultDirectory = document.Json.ResultDirectory.Trim();

            if (!string.IsNullOrWhiteSpace(document.Json?.CollectionDirectory))
                settings.JsonCollectionDirectory = document.Json.CollectionDirectory.Trim();

            if (!string.IsNullOrWhiteSpace(document.Prompts?.Directory))
                settings.PromptDirectory = document.Prompts.Directory.Trim();

            if (ParseSwitch(document.Prompts?.DirectPrompt) is { } directPrompt)
                settings.DirectPrompt = directPrompt;

            if (!string.IsNullOrWhiteSpace(document.RegexAssistant?.Directory))
                settings.RegexDirectory = document.RegexAssistant.Directory.Trim();

            if (!string.IsNullOrWhiteSpace(document.Pictures?.Directory))
                settings.PicturesDirectory = document.Pictures.Directory.Trim();

            if (!string.IsNullOrWhiteSpace(document.Music?.Directory))
                settings.MusicDirectory = document.Music.Directory.Trim();

            if (document.Speech?.Language?.Trim().ToLowerInvariant() is { } speechLanguage && SpeechLanguages.Contains(speechLanguage))
                settings.SpeechLanguage = speechLanguage;

            if (!string.IsNullOrWhiteSpace(document.Speech?.Model))
            {
                var model = document.Speech.Model.Trim();
                // ~/ is the home folder of the user.
                if (model == "~" || model.StartsWith("~/", StringComparison.Ordinal))
                    model = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), model.TrimStart('~').TrimStart('/'));
                settings.SpeechModel = ResolveDirectory(model);
            }

            if (!string.IsNullOrWhiteSpace(document.Calculator?.GraphDirectory))
                settings.CalculatorGraphDirectory = document.Calculator.GraphDirectory.Trim();

            if (!string.IsNullOrWhiteSpace(document.Fooocus?.Directory))
                settings.FooocusHome = document.Fooocus.Directory.Trim();

            RelocateDirectories(settings, document, path);
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or IOException or UnauthorizedAccessException)
        {
            // Keep JSON / default values when the YAML file is invalid or cannot be read.
        }
    }

    /// <summary>
    /// Writes <c>theme: &lt;name&gt;</c> into config.yml next to the executable. Only that top-level line is
    /// replaced (or appended), so comments and the other settings are kept as they are.
    /// </summary>
    public static void SaveTheme(string theme, string yamlFileName = "config.yml") =>
        SaveTopLevel("theme", theme, "# Last theme used, restored at startup (written on exit).", yamlFileName);

    /// <summary>
    /// Writes <c>historyExchanges: &lt;count&gt;</c> into config.yml next to the executable (the choice of the
    /// drop-down above the chat, restored at startup).
    /// </summary>
    public static void SaveHistoryExchanges(int count, string yamlFileName = "config.yml") =>
        SaveTopLevel("historyExchanges", count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "# Number of previous exchanges (prompt + answer) written into each prompt, 0 to 20 (set from the chat).",
            yamlFileName);

    /// <summary>
    /// Writes <c>chatoptions: on / off</c> into config.yml next to the executable (the Model, Preset and History zone of
    /// the chat shown or hidden, restored at startup).
    /// </summary>
    public static void SaveChatOptions(bool visible, string yamlFileName = "config.yml") =>
        SaveTopLevel("chatoptions", visible ? "on" : "off",
            "# on: the Model, Preset and History zone of the chat is shown; off: it is hidden (set from the chat).", yamlFileName);

    /// <summary>
    /// Writes <c>key: value</c> into config.yml next to the executable. Only that top-level line is changed (comments
    /// and other keys are kept); when the key is missing it is added at the end, under <paramref name="comment"/>.
    /// </summary>
    private static void SaveTopLevel(string key, string value, string comment, string yamlFileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, yamlFileName);
        var entry = $"{key}: {value}";
        var keyLine = new Regex($@"^{Regex.Escape(key)}\s*:", RegexOptions.IgnoreCase);

        try
        {
            var text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            var newLine = text.Contains("\r\n") ? "\r\n" : Environment.NewLine;
            var lines = text.Length == 0 ? [] : text.Split(["\r\n", "\n"], StringSplitOptions.None).ToList();

            // Top-level key only (no indentation), so a nested key of the same name is never touched.
            var index = lines.FindIndex(line => keyLine.IsMatch(line));
            if (index >= 0)
            {
                if (lines[index].Trim() == entry)
                    return;
                lines[index] = entry;
            }
            else
            {
                while (lines.Count > 0 && lines[^1].Length == 0)
                    lines.RemoveAt(lines.Count - 1);
                if (lines.Count > 0)
                    lines.Add(string.Empty);
                lines.Add(comment);
                lines.Add(entry);
                lines.Add(string.Empty);
            }

            File.WriteAllText(path, string.Join(newLine, lines));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Failing to remember a setting must never prevent the application from working or closing.
            System.Diagnostics.Debug.WriteLine($"Could not save {key} to {path}: {ex.Message}");
        }
    }

    /// <summary>Folder of the Kaonomi data in the home directory (<c>~/kaonomi</c>).</summary>
    public static string KaonomiHome =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "kaonomi");

    /// <summary>
    /// Paths of config.yml adapted at startup, written for another system (e.g. a macOS path on Windows) or missing:
    /// "section.key: old path -> new path".
    /// </summary>
    [JsonIgnore]
    public List<string> RelocatedDirectories { get; } = [];

    /// <summary>What a path of config.yml is, which tells where it goes when it cannot be used as written.</summary>
    private enum PathKind
    {
        /// <summary>Folder of Kaonomi data: created; in ~/kaonomi under its usual name when it cannot be mapped.</summary>
        Data,

        /// <summary>Pictures or music library: the folder of the system when the mapped one does not exist.</summary>
        Library,

        /// <summary>Folder created when it is first used: the default one when it cannot be mapped.</summary>
        Lazy,

        /// <summary>Piper executable: left as it is when it cannot be mapped (the usual places are searched).</summary>
        Executable
    }

    /// <summary>The paths of config.yml: section, key, kind, where it goes when it cannot be mapped, and how to read / set it.</summary>
    private static readonly (string Section, string Key, PathKind Kind, Func<string?> Fallback, Func<ChatYamlDocument, string?> Read, Action<ChatSettings, string> Set)[] YamlPaths =
    [
        ("json", "resultDirectory", PathKind.Data, () => Path.Combine(KaonomiHome, "jsonresult"), d => d.Json?.ResultDirectory, (s, v) => s.JsonResultDirectory = v),
        ("json", "collectionDirectory", PathKind.Data, () => Path.Combine(KaonomiHome, "jsoncollection"), d => d.Json?.CollectionDirectory, (s, v) => s.JsonCollectionDirectory = v),
        ("prompts", "directory", PathKind.Data, () => Path.Combine(KaonomiHome, "promptscollection"), d => d.Prompts?.Directory, (s, v) => s.PromptDirectory = v),
        ("regexAssistant", "directory", PathKind.Data, () => Path.Combine(KaonomiHome, "regexcollection"), d => d.RegexAssistant?.Directory, (s, v) => s.RegexDirectory = v),
        ("tts", "voicesDirectory", PathKind.Data, () => Path.Combine(KaonomiHome, "piper", "voices"), d => d.Tts?.VoicesDirectory, (s, v) => s.TtsVoicesDirectory = v),
        ("tts", "piper", PathKind.Executable, () => null, d => d.Tts?.Piper, (s, v) => s.TtsPiperPath = v),
        ("pictures", "directory", PathKind.Library, () => SystemPicturesDirectory, d => d.Pictures?.Directory, (s, v) => s.PicturesDirectory = v),
        ("music", "directory", PathKind.Library, () => SystemMusicDirectory, d => d.Music?.Directory, (s, v) => s.MusicDirectory = v),
        ("calculator", "graphDirectory", PathKind.Lazy, () => DefaultCalculatorGraphDirectory, d => d.Calculator?.GraphDirectory, (s, v) => s.CalculatorGraphDirectory = v),
    ];

    /// <summary>
    /// Brings every path of config.yml to the standards of this system, at startup: a path written on another system
    /// (Windows ↔ macOS / Linux) is mapped to the same place here (<see cref="OsPaths.Translate"/>: home folder, standard
    /// folders, Piper venv), or to its usual place when it cannot be mapped; a missing data folder is created in
    /// ~/kaonomi. The setting and its line in config.yml are changed (the voices given as .onnx paths too).
    /// </summary>
    private static void RelocateDirectories(ChatSettings settings, ChatYamlDocument document, string yamlPath)
    {
        foreach (var (section, key, kind, fallback, read, set) in YamlPaths)
        {
            var configured = read(document)?.Trim();
            if (string.IsNullOrEmpty(configured))
                continue;

            string? target = null;
            if (OsPaths.IsForOtherSystem(configured))
            {
                var translated = OsPaths.Translate(configured);
                target = kind switch
                {
                    PathKind.Library => translated is not null && Directory.Exists(translated) ? translated : fallback(),
                    PathKind.Executable => translated,
                    _ => translated ?? fallback()
                };
            }
            else if (kind == PathKind.Data && !IsUsableDirectory(configured))
                target = fallback();

            if (target is null || target == configured)
                continue;

            if (kind == PathKind.Data)
            {
                try
                {
                    Directory.CreateDirectory(target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    System.Diagnostics.Debug.WriteLine($"Could not create {target}: {ex.Message}");
                    continue;
                }
            }

            set(settings, target);
            SetYamlValue(yamlPath, target, section, key);
            settings.RelocatedDirectories.Add($"{section}.{key}: {configured} -> {target}");
        }

        // Voices given as the path of an .onnx file.
        foreach (var (language, voice) in document.Tts?.Voices ?? [])
        {
            if (string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(voice)
                || !OsPaths.IsForOtherSystem(voice) || OsPaths.Translate(voice) is not { } translated)
                continue;
            settings.TtsVoices[language.Trim()] = translated;
            SetYamlValue(yamlPath, translated, "tts", "voices", language.Trim());
            settings.RelocatedDirectories.Add($"tts.voices.{language.Trim()}: {voice.Trim()} -> {translated}");
        }
    }

    /// <summary>True when <paramref name="path"/> is written for this system and the directory exists.</summary>
    private static bool IsUsableDirectory(string path)
    {
        if (OsPaths.IsForOtherSystem(path))
            return false;

        try
        {
            return Directory.Exists(ResolveDirectory(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces the value of the key at <paramref name="keys"/> (a top-level section, then the keys nested under it) in
    /// config.yml; comments and the other lines are kept as they are.
    /// </summary>
    private static void SetYamlValue(string path, string value, params string[] keys)
    {
        try
        {
            var text = File.ReadAllText(path);
            var newLine = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
            // Indentation of the keys found so far (keys[0 .. depth - 1]).
            var indents = new int[keys.Length];
            var depth = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                if (YamlKeyLine.Match(lines[i]) is not { Success: true } match)
                    continue;
                var indent = match.Groups["indent"].Length;
                while (depth > 0 && indent <= indents[depth - 1])
                    depth--;
                if (match.Groups["key"].Value != keys[depth] || depth == 0 && indent > 0)
                    continue;
                if (depth == keys.Length - 1)
                {
                    lines[i] = $"{match.Groups["indent"].Value}{keys[depth]}: {YamlScalar(value)}";
                    File.WriteAllText(path, string.Join(newLine, lines));
                    return;
                }
                indents[depth++] = indent;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not update {string.Join('.', keys)} in {path}: {ex.Message}");
        }
    }

    /// <summary>A key line of YAML (not a comment): its indentation and its key.</summary>
    private static readonly Regex YamlKeyLine = new(@"^(?<indent>[ ]*)(?<key>[^\s#:][^:]*?)\s*:(\s|$)");

    /// <summary><paramref name="value"/> as a YAML scalar: quoted when it holds characters YAML would read otherwise.</summary>
    private static string YamlScalar(string value) =>
        value.Contains(": ") || value.Contains(" #") || value.Length > 0 && "'\"&*!|>%@`{[,?-".Contains(value[0])
            ? $"'{value.Replace("'", "''")}'"
            : value;

    /// <summary>Resolves a directory path relative to the executable when not rooted.</summary>
    public static string ResolveDirectory(string path)
    {
        var trimmed = string.IsNullOrWhiteSpace(path) ? "." : path.Trim();
        return Path.GetFullPath(Path.IsPathRooted(trimmed)
            ? trimmed
            : Path.Combine(AppContext.BaseDirectory, trimmed));
    }

    private sealed class ChatYamlDocument
    {
        public string? OllamaUrl { get; set; }

        public string? ServerUrl { get; set; }

        public string? SearchEngineUrl { get; set; }

        public string? MongoEngineAddress { get; set; }

        public string? LlmGatewayUrl { get; set; }

        /// <summary>Read as text so that on / off work as well as true / false.</summary>
        public string? ByeStopsEngines { get; set; }

        public StreamingSection? Streaming { get; set; }

        public string? Language { get; set; }

        /// <summary>Read as text so that on / off work as well as true / false.</summary>
        [YamlMember(Alias = "text2speech")]
        public string? Text2Speech { get; set; }

        public TtsSection? Tts { get; set; }

        public string? Theme { get; set; }

        public string? Animation { get; set; }

        public string? Persona { get; set; }

        public int? HistoryExchanges { get; set; }

        [YamlMember(Alias = "visionmodel")]
        public string? VisionModel { get; set; }

        /// <summary>Read as text so that on / off work as well as true / false.</summary>
        [YamlMember(Alias = "keepawake")]
        public string? KeepAwake { get; set; }

        [YamlMember(Alias = "chatoptions")]
        public string? ChatOptions { get; set; }

        public List<PresetSection>? Presets { get; set; }

        public JsonDirectories? Json { get; set; }

        public OntologySection? Ontology { get; set; }

        public PromptsSection? Prompts { get; set; }

        public PromptsSection? RegexAssistant { get; set; }

        public PromptsSection? Pictures { get; set; }

        public PromptsSection? Music { get; set; }

        public CalculatorSection? Calculator { get; set; }

        public SpeechSection? Speech { get; set; }

        public FooocusSection? Fooocus { get; set; }
    }

    /// <summary>A preset of the <c>presets</c> section: the avatar, its voice and its personality.</summary>
    private sealed class PresetSection
    {
        public string? Name { get; set; }

        public string? Description { get; set; }

        /// <summary>French Piper voice (tts.voices.FR when the preset is applied).</summary>
        public string? Voice { get; set; }

        public double? Pitch { get; set; }

        public double? Speed { get; set; }

        public double? Robot { get; set; }

        public double? Vibrato { get; set; }

        public double? Assistant { get; set; }

        public double? Horror { get; set; }

        /// <summary>3D animation (MARI, JARVIS, HAL… as the <c>animation</c> key).</summary>
        public string? Animation { get; set; }

        /// <summary>Personality added to the system prompt.</summary>
        public string? Persona { get; set; }
    }

    private sealed class SpeechSection
    {
        public string? Language { get; set; }

        public string? Model { get; set; }
    }

    private sealed class StreamingSection
    {
        public bool? Enabled { get; set; }

        public int? SyllableMs { get; set; }

        public double? Speed { get; set; }
    }

    /// <summary>A YAML on / off value: true, yes, on, 1 or false, no, off, 0 (any case); null otherwise.</summary>
    private static bool? ParseSwitch(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "on" or "1" => true,
        "false" or "no" or "off" or "0" => false,
        _ => null
    };

    private sealed class CalculatorSection
    {
        public string? GraphDirectory { get; set; }
    }

    private sealed class FooocusSection
    {
        public string? Directory { get; set; }
    }

    private sealed class PromptsSection
    {
        public string? Directory { get; set; }

        /// <summary>Read as text so that on / off work as well as true / false.</summary>
        [YamlMember(Alias = "directprompt")]
        public string? DirectPrompt { get; set; }
    }

    private sealed class TtsSection
    {
        public string? Piper { get; set; }

        public string? VoicesDirectory { get; set; }

        public Dictionary<string, string>? Voices { get; set; }

        public Dictionary<string, string>? Speakers { get; set; }

        public double? Speed { get; set; }

        public double? Pitch { get; set; }

        public double? Robot { get; set; }

        public double? Vibrato { get; set; }

        public double? Assistant { get; set; }

        public double? Horror { get; set; }
    }

    private sealed class OntologySection
    {
        public List<string>? EntityTypes { get; set; }
    }

    private sealed class JsonDirectories
    {
        public string? ResultDirectory { get; set; }

        public string? CollectionDirectory { get; set; }
    }
}
