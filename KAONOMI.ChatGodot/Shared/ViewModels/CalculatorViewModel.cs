using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>A calculation of the history: the expression and its result.</summary>
public sealed record CalculatorEntry(string Expression, string Result);

/// <summary>
/// Scientific calculator of the <c>/work</c> window (<c>/nwork</c> in its own window): the expression is
/// typed with the keys or the keyboard (functions, constants, <c>Ans</c>, see <see cref="CalculatorEngine"/>), its
/// result is shown as it is typed, <c>=</c> keeps it in the history and continues from it; angles in degrees or radians.
/// Every calculation is also written on the tape (<see cref="Tape"/>), kept in a file across the restarts.
/// </summary>
public partial class CalculatorViewModel : ViewModelBase, INoteContextProvider, IDisposable, ITapeAssistant
{
    /// <summary>Most calculations kept in the history.</summary>
    private const int HistorySize = 50;

    /// <summary>Expression being typed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayExpression))]
    private string _expression = string.Empty;

    /// <summary>Result of <see cref="Expression"/> (live preview), or the error once <c>=</c> fails.</summary>
    [ObservableProperty]
    private string _result = "0";

    [ObservableProperty]
    private bool _isError;

    /// <summary>Angles of sin, cos, tan and their inverses in degrees (true) or radians (false).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AngleLabel))]
    private bool _isDegrees = true;

    /// <summary>DEG or RAD, on the key that switches them.</summary>
    public string AngleLabel => IsDegrees ? "DEG" : "RAD";

    /// <summary>True right after <c>=</c>: a number or a function starts a new expression, an operator continues from the result.</summary>
    private bool _justEvaluated;

    /// <summary>Last result (<c>Ans</c>).</summary>
    private double _ans;

    /// <summary>Keys that type several characters: ⌫ removes them at once.</summary>
    private static readonly string[] Tokens =
    [
        "asin(", "acos(", "atan(", "sinh(", "cosh(", "tanh(", "sin(", "cos(", "tan(",
        "log2(", "log(", "ln(", "exp(", "cbrt(", "abs(", "mod", "Ans"
    ];

    /// <summary>Keys that continue from the result after <c>=</c> (the others start a new expression).</summary>
    private static bool ContinuesResult(string key) =>
        key[0] is '+' or '−' or '-' or '×' or '÷' or '^' or '%' or '!' or ')' || key == "mod";

    /// <summary>Expression shown above the result ("0" when empty).</summary>
    public string DisplayExpression => Expression.Length == 0 ? "0" : Expression;

    /// <summary>
    /// The tape: every calculation done with <c>=</c> since the tape was last cleared, one per line, under a line with
    /// the date and time of each calculator session. Editable (notes can be added); saved to
    /// <c>calculator-tape.txt</c> at each change.
    /// </summary>
    [ObservableProperty]
    private string _tape = CalculatorTapeStore.Load();

    /// <summary>True once this window has written its session line on the tape.</summary>
    private bool _tapeSessionStarted;

    partial void OnTapeChanged(string value) => CalculatorTapeStore.Save(value);

    /// <summary>Empties the tape (and its file).</summary>
    [RelayCommand]
    private void ClearTape()
    {
        Tape = string.Empty;
        _tapeSessionStarted = false;
        // An empty tape is a new document: the next save creates one.
        ForgetDocument();
    }

    /// <summary>
    /// New (Home): the document being worked on is saved first, then the tape is emptied for a new one. A tape already
    /// saved as a document gets a new version with its title, author, category, classification and description; a tape
    /// never saved becomes a document named after its first line. An empty tape is only started again. When the save
    /// fails (or there is no server), the tape is kept and the error is thrown. Returns what was done.
    /// </summary>
    public async Task<string> NewDocumentAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Tape))
        {
            ClearTape();
            return "New document";
        }
        if (_server is null)
            throw new InvalidOperationException("The Kaonomi server is not available: the document cannot be saved, the tape is kept.");
        var current = CurrentDocument;
        var title = current?.Title ?? FirstLineTitle(Tape);
        var saved = await SaveDocumentAsync(title, current?.Author, current?.Category, current?.SecurityClassification,
            current?.Description, asNew: current is null, cancellationToken);
        var version = saved.Contents?.Count > 0 ? saved.Contents.Max(c => c.Version) : 1;
        ClearTape();
        ActivityLog.Info("Calculator", $"New document: \"{saved.Title}\" saved (version {version}) and the tape emptied");
        return $"\"{saved.Title}\" saved (version {version}) · new document";
    }

    /// <summary>A title from the first line of text of a tape (its Markdown marks left out), at most 60 characters.</summary>
    private static string FirstLineTitle(string tape)
    {
        var line = tape.Split('\n').Select(l => l.Trim().TrimStart('#', '>', '-', '*', ' ').Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("![", StringComparison.Ordinal)) ?? "";
        line = System.Text.RegularExpressions.Regex.Replace(line, @"[*_`~]+", "").Trim();
        if (line.Length == 0)
            return $"Tape {DateTime.Now:yyyy-MM-dd HH:mm}";
        return line.Length > 60 ? line[..60].TrimEnd() + "…" : line;
    }

    /// <summary>Adds a calculation at the end of the tape, after the session line of this window.</summary>
    private void WriteOnTape(string expression, string result) => AppendToTape($"{expression} = {result}");

    /// <summary>Adds lines at the end of the tape, after the session line of this window.</summary>
    private void AppendToTape(params string[] lines)
    {
        var text = new System.Text.StringBuilder(Tape);
        if (text.Length > 0 && text[^1] != '\n')
            text.Append(Environment.NewLine);
        if (!_tapeSessionStarted)
        {
            if (text.Length > 0)
                text.Append(Environment.NewLine);
            text.Append(TapeDocument.PlainLine($"── {DateTime.Now:yyyy-MM-dd HH:mm} ({AngleLabel}) ──")).Append(Environment.NewLine);
            _tapeSessionStarted = true;
        }
        // Picture lines are written as they are; the text is escaped (a * of a formula is not italic).
        foreach (var line in lines)
            text.Append(Views.JournalMarkdown.ImageLine.IsMatch(line) ? line : TapeDocument.PlainLine(line)).Append(Environment.NewLine);
        Tape = text.ToString();
    }

    /// <summary>
    /// Folder of the pictures of the graphs added to the tape (<c>calculator.graphDirectory</c> of config.yml; by
    /// default the Pictures folder of the system, kaonomi/maths).
    /// </summary>
    public string GraphPicturesDirectory { get; }

    /// <summary>Adds a picture of the graph to the tape: the functions, the range, then the picture (a Markdown image line).</summary>
    public void AddGraphToTape(string picturePath, string range)
    {
        var functions = string.Join("; ", GraphFunctions.Select(f => f.Label));
        AppendToTape($"📈 y = {functions}   ({range})", Views.JournalMarkdown.ImageMarkdown(picturePath, 560));
        ActivityLog.Info("Calculator", $"Graph added to the tape: {picturePath}");
    }

    /// <summary>Pictures of the tape, decoded once.</summary>
    private readonly Dictionary<string, Task<Avalonia.Media.Imaging.Bitmap?>> _tapeImages = new();

    /// <summary>A picture of the tape (decoded once, at most 1600 pixels wide).</summary>
    public Task<Avalonia.Media.Imaging.Bitmap?> LoadTapeImageAsync(string path)
    {
        if (!_tapeImages.TryGetValue(path, out var image))
            _tapeImages[path] = image = PictureLibrary.DecodeAsync(path, 1600, System.Threading.CancellationToken.None);
        return image;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var image in _tapeImages.Values)
            if (image.IsCompletedSuccessfully)
                image.Result?.Dispose();
        _tapeImages.Clear();
    }

    // Graph.

    /// <summary>True when the right panel shows the graph (false: the tape).</summary>
    [ObservableProperty]
    private bool _isGraphMode;

    /// <summary>
    /// Functions of x to draw, separated by ";" or new lines (<c>y = sin(x); x^2/4 - 2</c>); "y =", "f(x) =" are
    /// optional. Angles in radians.
    /// </summary>
    [ObservableProperty]
    private string _graphInput = "sin(x); x^2/4 - 2";

    /// <summary>The curves of <see cref="GraphInput"/> (the valid ones).</summary>
    [ObservableProperty]
    private IReadOnlyList<Views.GraphFunction> _graphFunctions = [];

    /// <summary>The syntax errors of the functions, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGraphError))]
    private string _graphError = string.Empty;

    /// <summary>True when a function cannot be read.</summary>
    public bool HasGraphError => GraphError.Length > 0;

    /// <summary>Colors of the curves, in order.</summary>
    private static readonly string[] GraphColors = ["#2563EB", "#E11D48", "#059669", "#D97706", "#7C3AED", "#0891B2", "#DB2777", "#65A30D"];

    /// <summary>"y =", "f(x) =", "y1 =", "g(x)=" in front of a function.</summary>
    private static readonly System.Text.RegularExpressions.Regex FunctionName =
        new(@"^\s*[a-zA-Z]\w*\s*(\(\s*x\s*\))?\s*=\s*");

    partial void OnGraphInputChanged(string value) => BuildGraph();

    /// <summary>Shows the graph or the tape.</summary>
    [RelayCommand]
    private void ShowGraph(string? graph) => IsGraphMode = graph == "graph";

    /// <summary>Reads the functions of <see cref="GraphInput"/> into curves; the invalid ones are reported.</summary>
    private void BuildGraph()
    {
        var functions = new List<Views.GraphFunction>();
        var errors = new List<string>();
        foreach (var part in GraphInput.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var expression = FunctionName.Replace(part, string.Empty).Trim();
            if (expression.Length == 0)
                continue;
            if (CalculatorEngine.CheckFunction(expression) is { } error)
            {
                errors.Add($"{part}: {error}");
                continue;
            }
            var color = Avalonia.Media.Color.Parse(GraphColors[functions.Count % GraphColors.Length]);
            functions.Add(new Views.GraphFunction(expression, x => CalculatorEngine.EvaluateAt(expression, x), color));
        }
        GraphFunctions = functions;
        GraphError = string.Join("   ·   ", errors);
    }

    /// <summary>Calculations done with <c>=</c>, the newest first.</summary>
    public ObservableCollection<CalculatorEntry> History { get; } = [];

    /// <summary>Makes the analysis of a picture of the tape, for the image analysis window of /pictures (set by the chat).</summary>
    public Func<string, ImageAnalysisViewModel>? CreateAnalysis { get; set; }

    /// <summary>Builds the curves of the default functions; graph pictures in the default folder.</summary>
    public CalculatorViewModel() : this(Models.ChatSettings.DefaultCalculatorGraphDirectory, visionModel: Models.ChatSettings.DefaultVisionModel)
    {
    }

    /// <summary>
    /// Builds the curves of the default functions; the pictures of the graphs go to <paramref name="graphPicturesDirectory"/>.
    /// With <paramref name="server"/> and <paramref name="ollama"/>, the tape can be saved as a document (its description
    /// written by the model of <paramref name="model"/>) and documents loaded.
    /// </summary>
    public CalculatorViewModel(string graphPicturesDirectory, KaonomiAssistantService? server = null,
        OllamaModelService? ollama = null, Func<string?>? model = null, string visionModel = Models.ChatSettings.DefaultVisionModel)
    {
        GraphPicturesDirectory = graphPicturesDirectory;
        ReviewModel = visionModel;
        _server = server;
        _ollama = ollama;
        _model = model;
        BuildGraph();
        if (server is not null && CalculatorTapeStore.LoadDocumentId() is { } id)
            _ = RestoreDocumentAsync(id);
    }

    // Documents (💾 Save, 📂 Load).

    private readonly KaonomiAssistantService? _server;
    private readonly OllamaModelService? _ollama;
    private readonly Func<string?>? _model;

    /// <summary>The AI tools of the tape (evaluate, translate, rewrite, generate code and images), shared with the journal.</summary>
    private WritingAssistant Writing => _writing ??= new WritingAssistant(_ollama, _model, _server, GraphPicturesDirectory, "Calculator", "the tape");

    private WritingAssistant? _writing;

    /// <summary>Categories offered for a document (<c>category</c> of the server).</summary>
    public static readonly string[] DocumentCategories = ["math", "text", "physic", "webpage"];

    /// <summary>True when the tape can be saved to and loaded from the Kaonomi server.</summary>
    public bool CanUseDocuments => _server is not null;

    /// <summary>The document the tape belongs to (saved with 💾 or loaded with 📂), or null for a tape never saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DocumentCaption))]
    private DocumentEntry? _currentDocument;

    /// <summary>Line under TAPE: the document, its latest version and when it was saved; the usual line otherwise.</summary>
    public string DocumentCaption => CurrentDocument is { } document
        ? $"📄 {document.Title} · v{document.Latest?.Version ?? 0}" + (document.Date is { } date ? $" · {date.LocalDateTime:yyyy-MM-dd HH:mm}" : string.Empty)
        : "Every calculation, kept";

    /// <summary>Reads again, at startup, the document the tape belonged to (it is forgotten when it no longer exists).</summary>
    private async Task RestoreDocumentAsync(string id)
    {
        try
        {
            var document = await _server!.GetDocumentAsync(id);
            CurrentDocument = document is { IsDeleted: false } ? document : null;
            if (CurrentDocument is null)
                CalculatorTapeStore.SaveDocumentId(null);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // Server unreachable: the id stays, the document is read at the next start (or the next save reads it).
            ActivityLog.Error("Calculator", $"Document {id} of the tape not read", ex);
        }
    }

    /// <summary>
    /// The title offered when saving: the one of the document of the tape, else the first Title or heading of the tape,
    /// else its first line of text.
    /// </summary>
    public string SuggestedTitle()
    {
        if (CurrentDocument?.Title is { Length: > 0 } title)
            return title;
        if (_importedTitle is { Length: > 0 } imported)
            return imported;
        var paragraphs = TapeDocument.Parse(Tape).Blocks.OfType<TapeParagraph>().Where(p => p.Text.Trim().Length > 0).ToList();
        var heading = paragraphs.FirstOrDefault(p => p.Kind != TapeParagraphKind.Normal) ?? paragraphs.FirstOrDefault();
        var text = heading?.Text.Trim().Trim('─', ' ') ?? string.Empty;
        return text.Length > 80 ? text[..80] : text;
    }

    /// <summary>
    /// Description of the tape written by the model selected in the chat (the default model of config.json at first):
    /// two to four sentences, in the language of the tape.
    /// </summary>
    public async Task<string> WriteDescriptionAsync(string title, System.Threading.CancellationToken cancellationToken)
    {
        if (_ollama is null || _model?.Invoke() is not { Length: > 0 } model)
            throw new InvalidOperationException("No model is available to write the description.");
        // The text of the tape, its pictures as their names, at most about 12,000 characters.
        var text = string.Join("\n", TapeDocument.Parse(Tape).Blocks.Select(b => b switch
        {
            TapeParagraph p => p.Text,
            TapeImage i => $"[picture: {i.Alt}]",
            _ => string.Empty
        }));
        if (text.Length > 12000)
            text = text[..12000] + "\n…";
        const string system =
            "You write the description of a document for a document library. In 2 to 4 sentences, say what the document "
            + "is about and what it contains (calculations, results, graphs, notes), so that it can be found again. Write "
            + "in the language of the document. No Markdown, no lists, no title. Answer with a JSON object: "
            + "{\"description\": the text}.";
        var schema = System.Text.Json.Nodes.JsonNode.Parse("""
            {"type":"object","properties":{"description":{"type":"string"}},"required":["description"]}
            """)!;
        var json = await _ollama.ChatJsonAsync(model, system, $"Title: {title}\n\nDocument:\n{text}", schema, cancellationToken);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("description", out var description) && description.GetString() is { Length: > 0 } result
            ? result.Trim()
            : throw new System.Text.Json.JsonException("No \"description\" in the answer of the model.");
    }

    /// <summary>Prompt of 🧐 Evaluate (right click on selected text of the tape), followed by the text.</summary>
    public const string EvaluationPrompt =
        "Analyse ce texte et critique le, mathématiquement, et sur la forme. Puis suggère des pistes d'amélioration, sans générer la solution.";

    /// <summary>Name of the model used for the description and the evaluation (the one selected in the chat).</summary>
    public string? CurrentModelName => _model?.Invoke();

    /// <summary>True when the selected text of the tape can be evaluated by the LLM.</summary>
    public bool CanEvaluate => _ollama is not null && _model is not null;

    /// <summary>
    /// Evaluation of <paramref name="text"/> by the model selected in the chat (the default model at first): a criticism,
    /// mathematical and of the form, then ideas to improve it, without the solution. The answer is Markdown.
    /// </summary>
    public Task<string> EvaluateAsync(string text, System.Threading.CancellationToken cancellationToken) =>
        Writing.EvaluateAsync(text,
            "Tu es un professeur de mathématiques exigeant et bienveillant. Réponds en Markdown, dans la langue du texte évalué, avec des titres courts. "
            + "N'utilise pas LaTeX (ni \\( \\), ni \\[ \\], ni $) : écris les formules en texte avec les symboles Unicode (π, r², √, ≈, ×, ÷, ≤).",
            EvaluationPrompt, cancellationToken);

    /// <summary>Vision model of the AI review of the document (its text and its pictures).</summary>
    public string ReviewModel { get; }

    /// <summary>At most this many pictures of the tape are sent to the review, each at most this wide or high.</summary>
    private const int ReviewMaxPictures = 8;
    private const int ReviewPictureSize = 1280;

    /// <summary>True when the document can be reviewed by the AI (Ollama is available).</summary>
    public bool CanReview => _ollama is not null;

    /// <summary>
    /// 🤖 AI (Home): the whole document, its text and its pictures, criticised by <see cref="ReviewModel"/> (the vision model of config.yml, by default Mistral
    /// Small 3.2, through Ollama, on this machine): what is good, what is wrong, the coherence of the purpose, the role of
    /// the pictures, and what can be improved. Returns the review in Markdown and the number of pictures read.
    /// </summary>
    public async Task<(string Markdown, int Pictures)> ReviewDocumentAsync(System.Threading.CancellationToken cancellationToken)
    {
        if (_ollama is null)
            throw new InvalidOperationException("Ollama is not available for the review.");
        var document = TapeDocument.Parse(Tape);
        var pictures = new List<byte[]>();
        var names = new List<string>();
        foreach (var image in document.Blocks.OfType<TapeImage>())
        {
            if (pictures.Count >= ReviewMaxPictures)
                break;
            var path = Views.JournalMarkdown.LocalPath(image.Url);
            if (await Task.Run(() => PictureForReview(path), cancellationToken) is { } bytes)
            {
                pictures.Add(bytes);
                names.Add($"Image {pictures.Count} : {(string.IsNullOrWhiteSpace(image.Alt) ? System.IO.Path.GetFileName(path) : image.Alt)}");
            }
        }
        // The text without the picture lines: the pictures are sent to the model, in the order of the document.
        var text = System.Text.RegularExpressions.Regex.Replace(Tape, @"(<p[^>]*>)?!\[[^\]]*\]\([^)]*\)(</p>)?", "[image]").Trim();
        ActivityLog.Info("Calculator", $"AI review of the document asked to {ReviewModel}: {text.Length} chars, {pictures.Count} picture(s)");
        var answer = await _ollama.ChatWithImagesAsync(ReviewModel,
            "Tu es un relecteur expert, exigeant et bienveillant. Réponds en Markdown, dans la langue du document, avec des titres courts. "
            + "N'utilise pas LaTeX (ni \\( \\), ni \\[ \\], ni $) : écris les formules en texte avec les symboles Unicode (π, r², √, ≈, ×, ÷, ≤).",
            "Analyse ce document (son texte, puis ses images jointes dans l'ordre où elles y apparaissent, chaque [image] du texte marquant leur place) "
            + "et critique-le :\n"
            + "1. **Résumé** : son propos en deux ou trois phrases.\n"
            + "2. **Points forts**.\n"
            + "3. **Cohérence du propos** : la logique de l'ensemble, l'enchaînement des idées, les contradictions, les affirmations non justifiées, l'exactitude des calculs et des raisonnements.\n"
            + "4. **Images** : ce qu'elles montrent, leur pertinence et leur cohérence avec le texte.\n"
            + "5. **Points d'amélioration** : une liste concrète et priorisée (fond, structure, clarté, style, orthographe, illustrations).\n"
            + "6. **Note globale** sur 10, justifiée en une phrase.\n\n"
            + (names.Count > 0 ? "Images jointes :\n" + string.Join("\n", names) + "\n\n" : "Le document n'a pas d'image.\n\n")
            + $"Document :\n\"\"\"\n{text}\n\"\"\"",
            pictures, cancellationToken);
        ActivityLog.Info("Calculator", $"AI review received ({answer.Length} chars)");
        return (LatexText.Convert(answer).Trim(), pictures.Count);
    }

    /// <summary>
    /// A picture of the tape for the review: read (HEIC, TIFF… converted by the picture library), reduced to at most
    /// <see cref="ReviewPictureSize"/> pixels and encoded as PNG; null when it cannot be read.
    /// </summary>
    private static byte[]? PictureForReview(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path))
                return null;
            Avalonia.Media.Imaging.Bitmap? bitmap;
            try
            {
                using var stream = System.IO.File.OpenRead(path);
                bitmap = new Avalonia.Media.Imaging.Bitmap(stream);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException or System.IO.IOException)
            {
                bitmap = PictureLibrary.DecodeAsync(path, ReviewPictureSize, System.Threading.CancellationToken.None).GetAwaiter().GetResult();
            }
            if (bitmap is null)
                return null;
            using (bitmap)
            {
                var scale = Math.Min(1.0, (double)ReviewPictureSize / Math.Max(bitmap.PixelSize.Width, bitmap.PixelSize.Height));
                using var output = new System.IO.MemoryStream();
                if (scale < 1)
                {
                    using var reduced = bitmap.CreateScaledBitmap(new Avalonia.PixelSize(
                        Math.Max(1, (int)(bitmap.PixelSize.Width * scale)), Math.Max(1, (int)(bitmap.PixelSize.Height * scale))),
                        Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
                    reduced.Save(output);
                }
                else
                    bitmap.Save(output);
                return output.ToArray();
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ActivityLog.Error("Calculator", $"Picture of the tape not read for the review: {path}", ex);
            return null;
        }
    }

    /// <summary>True when images can be generated with Fooocus (through the Kaonomi server).</summary>
    public bool CanGenerateImages => _server is not null;

    /// <summary>
    /// An image generated by Fooocus for <paramref name="prompt"/> (through <c>POST /image/generate</c> of the Kaonomi
    /// server), of <paramref name="aspectRatio"/> (width*height) in <paramref name="performance"/>, saved in the pictures
    /// folder of the calculator (<see cref="GraphPicturesDirectory"/>, Pictures/kaonomi/maths by default) as
    /// fooocus-date-the-prompt.png; returns its path. Throws with the reason when no image comes back.
    /// </summary>
    public Task<string> GenerateImageAsync(string prompt, string aspectRatio, string performance, System.Threading.CancellationToken cancellationToken) =>
        Writing.GenerateImageAsync(prompt, aspectRatio, performance, cancellationToken);

    /// <summary>
    /// Code written by the model selected in the chat for <paramref name="prompt"/>, in <paramref name="language"/>
    /// ("auto": the language the request asks for, else the one the model finds best). Returns the language of the code
    /// (its Markdown fence) and the code.
    /// </summary>
    public Task<(string Language, string Code)> GenerateCodeAsync(string prompt, string language, System.Threading.CancellationToken cancellationToken) =>
        Writing.GenerateCodeAsync(prompt, language, cancellationToken);

    /// <summary>
    /// Translation of <paramref name="text"/> into <paramref name="language"/> (English, German, Dutch) by the model
    /// selected in the chat: the formulas, numbers and line breaks are kept; only the translation is returned.
    /// </summary>
    /// <summary>
    /// The language of <paramref name="text"/> (its English name: French, English, German…), identified by the LLM of the
    /// chat before its spelling is corrected.
    /// </summary>
    public Task<string> DetectLanguageAsync(string text, System.Threading.CancellationToken cancellationToken) =>
        Writing.DetectLanguageAsync(text, cancellationToken);

    /// <summary>The prompt of ✨ Rewrite for readability.</summary>
    public const string ReadabilityPrompt = "Optimise la lisibilité du texte";

    /// <summary>
    /// The selected text of the tape corrected (<paramref name="mode"/> <c>spelling</c>: its spelling, grammar and
    /// punctuation, nothing else) or rewritten (<c>readability</c>: <see cref="ReadabilityPrompt"/>) by the LLM of the
    /// chat, in its own language, the formulas, numbers and symbols kept.
    /// </summary>
    public Task<string> RewriteAsync(string text, string mode, System.Threading.CancellationToken cancellationToken,
        string? language = null) =>
        Writing.RewriteAsync(text, mode, cancellationToken, language);

    public Task<string> TranslateAsync(string text, string language, System.Threading.CancellationToken cancellationToken) =>
        Writing.TranslateAsync(text, language, cancellationToken);

    /// <summary>
    /// Saves the tape as a document: its whole text as a new version (the latest version number + 1), the date set to
    /// now, and the title, author, category, security classification and description given. The document of the tape is
    /// updated, unless <paramref name="asNew"/> (or the tape has none): a new document is created. Returns the saved
    /// document.
    /// </summary>
    public async Task<DocumentEntry> SaveDocumentAsync(string title, string? author, string? category, string? classification,
        string? description, bool asNew, System.Threading.CancellationToken cancellationToken = default)
    {
        if (_server is null)
            throw new InvalidOperationException("The Kaonomi server is not available.");

        // The versions saved so far are read again from the server, so that none is lost (another window may have saved).
        DocumentEntry document;
        if (!asNew && CurrentDocument?.Id is { } id && await _server.GetDocumentAsync(id, cancellationToken) is { } saved)
            document = saved;
        else
            document = new DocumentEntry { Contents = [] };
        document.Contents ??= [];
        var version = (document.Contents.Count == 0 ? 0 : document.Contents.Max(c => c.Version)) + 1;
        document.Contents.Add(new DocumentContent { Content = Tape, Version = version });
        document.Title = string.IsNullOrWhiteSpace(title) ? "noname" : title.Trim();
        document.Author = string.IsNullOrWhiteSpace(author) ? null : author.Trim();
        document.Category = string.IsNullOrWhiteSpace(category) ? "math" : category.Trim();
        document.SecurityClassification = string.IsNullOrWhiteSpace(classification) ? null : classification.Trim();
        document.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        document.Date = DateTimeOffset.Now;
        document.AppName = "KAONOMI.Chat";
        document.MachineName = Environment.MachineName;
        document.UserName = Environment.UserName;
        document.IsDeleted = false;

        var result = await _server.SaveDocumentAsync(document, cancellationToken);
        if (!result.Success || result.ReturnedId is null or "-1")
            throw new InvalidOperationException(result.Message ?? "The document was not saved.");
        document.Id = result.ReturnedId;
        CurrentDocument = document;
        CalculatorTapeStore.SaveDocumentId(document.Id);
        ActivityLog.Info("Calculator", $"Tape saved as the document \"{document.Title}\" ({document.Id}), version {version}");
        return document;
    }

    /// <summary>
    /// The 3 documents the user worked on last (saved last by this user, newest first; the newest of anyone when this
    /// user has none).
    /// </summary>
    public async Task<IReadOnlyList<DocumentEntry>> RecentDocumentsAsync(System.Threading.CancellationToken cancellationToken = default)
    {
        if (_server is null)
            return [];
        var last = await _server.GetLastDocumentsAsync(50, cancellationToken);
        var mine = last.Where(d => string.Equals(d.UserName, Environment.UserName, StringComparison.OrdinalIgnoreCase)).Take(3).ToList();
        return mine.Count > 0 ? mine : last.Take(3).ToList();
    }

    /// <summary>Fuzzy search of the documents on their title, description and content.</summary>
    public async Task<IReadOnlyList<DocumentEntry>> SearchDocumentsAsync(string term, System.Threading.CancellationToken cancellationToken = default) =>
        _server is null ? [] : await _server.SearchDocumentsAsync(term, 20, cancellationToken);

    /// <summary>
    /// Opens a document: its latest version becomes the text given back (for the tape), and it becomes the document of
    /// the tape (the next save adds a version to it).
    /// </summary>
    public string OpenDocument(DocumentEntry document)
    {
        CurrentDocument = document;
        CalculatorTapeStore.SaveDocumentId(document.Id);
        // The next calculation starts a new session line under the loaded text.
        _tapeSessionStarted = false;
        ActivityLog.Info("Calculator", $"Document \"{document.Title}\" ({document.Id}) opened, version {document.Latest?.Version ?? 0}");
        return document.Latest?.Content ?? string.Empty;
    }

    /// <summary>Title of the tape imported from a JSON file (offered when it is saved as a document).</summary>
    private string? _importedTitle;

    /// <summary>
    /// A tape imported from a JSON file is a new document: the next save creates one, with <paramref name="title"/> as
    /// the title offered.
    /// </summary>
    public void ImportedTape(string? title)
    {
        ForgetDocument();
        _importedTitle = title;
        _tapeSessionStarted = false;
    }

    /// <summary>Forgets the document of the tape (a new tape): the next save creates a new document.</summary>
    public void ForgetDocument()
    {
        CurrentDocument = null;
        CalculatorTapeStore.SaveDocumentId(null);
    }

    /// <summary>True when at least one calculation is in the history.</summary>
    public bool HasHistory => History.Count > 0;

    /// <summary>
    /// A key of the calculator: a digit, <c>.</c>, an operator (<c>+ − × ÷ ^ %</c>), a parenthesis, <c>√</c>,
    /// <c>±</c> (sign of the last number), <c>C</c> (clear), <c>⌫</c> (last character) or <c>=</c>.
    /// </summary>
    [RelayCommand]
    private void Press(string? key)
    {
        switch (key)
        {
            case null or "":
                return;
            case "C":
                Expression = string.Empty;
                _justEvaluated = false;
                break;
            case "⌫":
                if (_justEvaluated)
                    Expression = string.Empty;
                else if (Tokens.FirstOrDefault(t => Expression.EndsWith(t, StringComparison.Ordinal)) is { } token)
                    Expression = Expression[..^token.Length];
                else if (Expression.Length > 0)
                    Expression = Expression[..^1];
                _justEvaluated = false;
                break;
            case "Rand":
                Type(CalculatorEngine.Format(Math.Round(Random.Shared.NextDouble(), 6)));
                break;
            case "=":
                Evaluate();
                return;
            case "±":
                ToggleSign();
                break;
            default:
                Type(key == "," ? "." : key);
                break;
        }
        Preview();
    }

    /// <summary>Adds <paramref name="text"/> to the expression; after <c>=</c>, only an operator continues from the result.</summary>
    private void Type(string text)
    {
        if (_justEvaluated && !ContinuesResult(text))
            Expression = string.Empty;
        _justEvaluated = false;
        Expression += text;
    }

    /// <summary>Switches the angles between degrees and radians.</summary>
    [RelayCommand]
    private void ToggleAngle()
    {
        IsDegrees = !IsDegrees;
        Preview();
    }

    /// <summary>Puts a calculation of the history back in the expression.</summary>
    [RelayCommand]
    private void Reuse(CalculatorEntry? entry)
    {
        if (entry is null)
            return;
        Expression = entry.Expression;
        _justEvaluated = false;
        Preview();
    }

    /// <summary>Empties the history.</summary>
    [RelayCommand]
    private void ClearHistory()
    {
        History.Clear();
        OnPropertyChanged(nameof(HasHistory));
    }

    /// <summary>Shows the result of the expression as it is typed (nothing when it is still incomplete).</summary>
    private void Preview()
    {
        IsError = false;
        if (Expression.Length == 0)
        {
            Result = "0";
            return;
        }
        try
        {
            Result = CalculatorEngine.Format(CalculatorEngine.Evaluate(Expression, IsDegrees, _ans));
        }
        catch (Exception ex) when (ex is FormatException or ArithmeticException)
        {
            // Incomplete while typing ("12 +"): the last result stays.
        }
    }

    /// <summary><c>=</c>: shows the result or the error; a result goes to the history and becomes the expression.</summary>
    private void Evaluate()
    {
        if (Expression.Length == 0)
            return;
        try
        {
            var value = CalculatorEngine.Evaluate(Expression, IsDegrees, _ans);
            var result = CalculatorEngine.Format(value);
            _ans = value;
            WriteOnTape(Expression, result);
            History.Insert(0, new CalculatorEntry(Expression, result));
            while (History.Count > HistorySize)
                History.RemoveAt(History.Count - 1);
            OnPropertyChanged(nameof(HasHistory));
            Expression = result;
            Result = result;
            IsError = false;
            _justEvaluated = true;
        }
        catch (Exception ex) when (ex is FormatException or ArithmeticException)
        {
            Result = ex.Message;
            IsError = true;
        }
    }

    /// <summary>Changes the sign of the last number of the expression (or of the result after <c>=</c>).</summary>
    private void ToggleSign()
    {
        var end = Expression.Length;
        var start = end;
        while (start > 0 && (char.IsDigit(Expression[start - 1]) || Expression[start - 1] == '.'))
            start--;
        if (start == end)
        {
            Expression += "-";
            return;
        }

        // The sign is a "-" (the subtraction key types "−"), so it is never taken for a subtraction.
        var hasSign = start > 0 && Expression[start - 1] == '-';
        Expression = hasSign
            ? Expression[..(start - 1)] + Expression[start..]
            : Expression[..start] + "-" + Expression[start..];
        _justEvaluated = false;
    }

    /// <inheritdoc />
    public string NoteWindowName => "Calculator";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        expression = Expression,
        result = Result,
        angles = AngleLabel,
        history = History.Take(10).ToList()
    };
}
