using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>Part of an example line, highlighted when the regex detected it.</summary>
public sealed record TextSegment(string Text, bool IsMatch);

/// <summary>Result of the generated regex on one example line.</summary>
public sealed record RegexExampleResult(string Line, bool IsMatch, string Detail, IReadOnlyList<TextSegment> Segments)
{
    /// <summary>✓ or ✗.</summary>
    public string Mark => IsMatch ? "✓" : "✗";
}

/// <summary>
/// Regex assistant of the symbolic pattern window: the user types example lines and the extraction goal, the local
/// Ollama model selected in the chat window is asked directly (<c>POST /api/chat</c>, JSON output <c>{"regex": …}</c>)
/// for a .NET regular expression, only the regex is kept, tested on the examples and shown; "Add" appends it to the
/// regex patterns of the selected document.
/// </summary>
public partial class RegexAssistantViewModel : ViewModelBase
{
    private const string SystemPrompt =
        "You are an expert in .NET regular expressions (System.Text.RegularExpressions). "
        + "Write ONE regular expression that matches the example lines given by the user and extracts what the goal asks for. "
        + "Use named capture groups (?<name>...) for the extracted values. Make it general enough for similar lines, "
        + "not only the exact examples. When the goal is a word, surround it with word boundaries (\\b) so that it is not "
        + "found inside a longer word, and write variants with optional parts or longest alternatives first "
        + "(tests? rather than test|tests). Answer with a JSON object whose \"regex\" field holds the regular expression only: "
        + "no explanation, no Markdown, no surrounding slashes, no flags.";

    /// <summary>Ollama structured output: <c>{"regex": "…"}</c>.</summary>
    private static readonly JsonNode AnswerSchema = new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { ["regex"] = new JsonObject { ["type"] = "string" } },
        ["required"] = new JsonArray("regex")
    };

    private readonly OllamaModelService _ollama;
    private readonly Func<string?> _model;
    private readonly Func<SymbolicPatternEntryViewModel?> _target;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Asks <paramref name="ollama"/> directly with the model chosen in the chat window, adds the regex to the document
    /// returned by <paramref name="target"/> (the selected one) and saves regexes in <paramref name="directory"/>.
    /// </summary>
    public RegexAssistantViewModel(
        OllamaModelService ollama,
        Func<string?> model,
        Func<SymbolicPatternEntryViewModel?> target,
        string directory)
    {
        _ollama = ollama;
        _model = model;
        _target = target;
        Directory = directory;
    }

    /// <summary>Folder of the saved regexes (<c>regexAssistant.directory</c> in config.yml).</summary>
    public string Directory { get; }

    /// <summary>File name suggested when saving: the goal, without characters forbidden in file names.</summary>
    public string SuggestedFileName
    {
        get
        {
            var invalid = Path.GetInvalidFileNameChars();
            var words = Goal.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => new string(w.Where(c => !invalid.Contains(c)).ToArray()))
                .Where(w => w.Length > 0)
                .Take(8);
            var name = string.Join('-', words);
            if (name.Length > 60)
                name = name[..60];
            return (name.Length == 0 ? $"regex-{DateTime.Now:yyyyMMdd-HHmmss}" : name) + ".json";
        }
    }

    /// <summary>Writes the regex, the goal and the example lines to <paramref name="stream"/> as JSON.</summary>
    public async Task SaveAsync(Stream stream, string fileName)
    {
        var saved = new SavedRegex
        {
            Goal = Goal.Trim(),
            Examples = ExampleLines(),
            Regex = GeneratedRegex,
            Date = DateTimeOffset.Now
        };

        try
        {
            await JsonSerializer.SerializeAsync(stream, saved, JsonOptions);
            IsError = false;
            Status = $"Saved to {fileName}";
            ActivityLog.Info("Symbol", $"Regex assistant: regex saved to {fileName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            IsError = true;
            Status = $"Not saved: {ex.Message}";
            ActivityLog.Error("Symbol", $"Regex assistant: save to {fileName} failed", ex);
        }
    }

    /// <summary>Reads a saved regex (goal, example lines, regex) from <paramref name="stream"/> and shows it, tested.</summary>
    public async Task LoadAsync(Stream stream, string fileName)
    {
        try
        {
            var saved = await JsonSerializer.DeserializeAsync<SavedRegex>(stream, JsonOptions)
                        ?? throw new JsonException("The file is empty.");
            Goal = saved.Goal;
            Examples = string.Join(Environment.NewLine, saved.Examples);
            GeneratedRegex = saved.Regex;
            var matched = ExampleResults.Count(r => r.IsMatch);
            IsError = IsInvalid;
            Status = IsInvalid
                ? $"Loaded from {fileName}, but the regex does not compile"
                : $"Loaded from {fileName} · matches {matched} of {ExampleResults.Count} example line(s)";
            ActivityLog.Info("Symbol", $"Regex assistant: regex loaded from {fileName}");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            IsError = true;
            Status = $"Not loaded: {ex.Message}";
            ActivityLog.Error("Symbol", $"Regex assistant: load from {fileName} failed", ex);
        }
    }

    /// <summary>Example lines, one per line.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private string _examples = string.Empty;

    /// <summary>What must be extracted, e.g. "the order number and the amount".</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private string _goal = string.Empty;

    /// <summary>Generated regex (editable before it is added).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    [NotifyPropertyChangedFor(nameof(HasRegex))]
    private string _generatedRegex = string.Empty;

    /// <summary>Why the generated regex does not compile, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInvalid))]
    private string? _regexError;

    [ObservableProperty]
    private string _status = "Type example lines and the extraction goal, then Generate";

    [ObservableProperty]
    private bool _isError;

    /// <summary>The generated regex tested on each example line.</summary>
    public ObservableCollection<RegexExampleResult> ExampleResults { get; } = [];

    /// <summary>True when a regex is shown.</summary>
    public bool HasRegex => !string.IsNullOrWhiteSpace(GeneratedRegex);

    /// <summary>True when the shown regex does not compile.</summary>
    public bool IsInvalid => RegexError is not null;

    partial void OnGeneratedRegexChanged(string value) => Test();

    partial void OnExamplesChanged(string value) => Test();

    private bool CanGenerate() => !string.IsNullOrWhiteSpace(Examples) && !string.IsNullOrWhiteSpace(Goal);

    /// <summary>Asks the Ollama model for the regex and keeps only the regex from the answer.</summary>
    [RelayCommand(CanExecute = nameof(CanGenerate), IncludeCancelCommand = true)]
    private async Task GenerateAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_model()))
        {
            IsError = true;
            Status = "Select a model in the chat window first";
            return;
        }

        var model = _model()!;
        var lines = ExampleLines();
        var prompt = "Goal of the extraction: " + Goal.Trim() + "\n\n"
                     + "Example lines (one per line, between the markers):\n"
                     + "<<<\n" + string.Join("\n", lines) + "\n>>>\n\n"
                     + "Answer with the JSON object {\"regex\": \"...\"} only.";

        IsError = false;
        Status = $"Asking {model}…";
        try
        {
            var answer = await _ollama.ChatJsonAsync(model, SystemPrompt, prompt, AnswerSchema, cancellationToken);
            var regex = ExtractRegex(RegexField(answer));
            if (regex is null)
            {
                IsError = true;
                Status = "The answer did not contain a regular expression";
                ActivityLog.Error("Symbol", $"Regex assistant: no regex in the answer of {model}");
                return;
            }

            GeneratedRegex = regex;
            var matched = ExampleResults.Count(r => r.IsMatch);
            IsError = IsInvalid;
            Status = IsInvalid
                ? "The generated regex does not compile: edit it or generate again"
                : $"Regex generated · matches {matched} of {ExampleResults.Count} example line(s)";
            ActivityLog.Info("Symbol", $"Regex assistant ({model}): {ActivityLog.Excerpt(regex)} · {matched}/{ExampleResults.Count} example(s) matched");
        }
        catch (OperationCanceledException)
        {
            Status = "Generation cancelled";
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Ollama unreachable at {_ollama.BaseAddress} ({ex.Message})";
            ActivityLog.Error("Symbol", "Regex assistant: Ollama unreachable", ex);
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = ex.Message;
            ActivityLog.Error("Symbol", "Regex assistant failed", ex);
        }
    }

    private bool CanAdd() => !string.IsNullOrWhiteSpace(GeneratedRegex);

    /// <summary>Appends the regex to the regex patterns of the selected document (an empty row is reused).</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        if (_target() is not { } entry)
        {
            IsError = true;
            Status = "Select or create a symbolic pattern first";
            return;
        }

        if (IsInvalid)
        {
            IsError = true;
            Status = "Not added: the regex does not compile";
            return;
        }

        if (entry.RegexPatterns.Any(p => p.Pattern == GeneratedRegex))
        {
            IsError = false;
            Status = "This regex is already in the list";
            return;
        }

        if (entry.RegexPatterns.FirstOrDefault(p => string.IsNullOrWhiteSpace(p.Pattern)) is { } empty)
            empty.Pattern = GeneratedRegex;
        else
            entry.RegexPatterns.Add(new RegexPatternItemViewModel(GeneratedRegex));

        IsError = false;
        Status = "Added to the regex patterns (Save to keep it)";
        ActivityLog.Info("Symbol", $"Regex assistant: regex added to {entry.Id ?? "(new)"}");
    }

    /// <summary>Tests the regex on every example line (match, then the named groups or the matched text).</summary>
    private void Test()
    {
        ExampleResults.Clear();
        RegexError = null;
        if (string.IsNullOrWhiteSpace(GeneratedRegex))
            return;

        Regex regex;
        try
        {
            regex = new Regex(GeneratedRegex, RegexOptions.None, TimeSpan.FromSeconds(1));
        }
        catch (ArgumentException ex)
        {
            RegexError = $"Invalid regular expression: {ex.Message}";
            return;
        }

        foreach (var line in ExampleLines())
        {
            try
            {
                var matches = regex.Matches(line).Where(m => m.Success).ToList();
                if (matches.Count == 0)
                {
                    ExampleResults.Add(new RegexExampleResult(line, false, "no match", [new TextSegment(line, false)]));
                    continue;
                }

                var match = matches[0];
                var groups = regex.GetGroupNames().Where(n => !int.TryParse(n, out _))
                    .Select(n => $"{n} = \"{match.Groups[n].Value}\"").ToList();
                var detail = groups.Count > 0 ? string.Join(", ", groups) : $"\"{match.Value}\"";
                if (matches.Count > 1)
                    detail += $"  ({matches.Count} matches)";
                ExampleResults.Add(new RegexExampleResult(line, true, detail, Highlight(line, matches)));
            }
            catch (RegexMatchTimeoutException)
            {
                ExampleResults.Add(new RegexExampleResult(line, false, "timeout (regex too slow)", [new TextSegment(line, false)]));
            }
        }
    }

    /// <summary>
    /// Splits <paramref name="line"/> into plain and highlighted parts. For each match, the captured groups are
    /// highlighted (what is extracted); a match without a captured group is highlighted as a whole.
    /// </summary>
    internal static IReadOnlyList<TextSegment> Highlight(string line, IEnumerable<Match> matches)
    {
        var spans = new List<(int Start, int End)>();
        foreach (var match in matches)
        {
            var groups = match.Groups.Cast<Group>().Skip(1).Where(g => g.Success && g.Length > 0).ToList();
            if (groups.Count == 0 && match.Length > 0)
                spans.Add((match.Index, match.Index + match.Length));
            spans.AddRange(groups.Select(g => (g.Index, g.Index + g.Length)));
        }

        // Overlapping spans (nested groups) are merged.
        var merged = new List<(int Start, int End)>();
        foreach (var span in spans.OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, span.End));
            else
                merged.Add(span);
        }

        var segments = new List<TextSegment>();
        var position = 0;
        foreach (var (start, end) in merged)
        {
            if (start > position)
                segments.Add(new TextSegment(line[position..start], false));
            segments.Add(new TextSegment(line[start..end], true));
            position = end;
        }
        if (position < line.Length)
            segments.Add(new TextSegment(line[position..], false));
        return segments;
    }

    private List<string> ExampleLines() =>
        Examples.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

    /// <summary>The "regex" field of the JSON answer; the raw answer when it is not that JSON object.</summary>
    internal static string? RegexField(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return null;
        try
        {
            return JsonNode.Parse(answer)?["regex"]?.GetValue<string>() is { } regex ? RestoreEscapes(regex) : answer;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return answer;
        }
    }

    /// <summary>
    /// Models often write <c>\b</c>, <c>\t</c>… with a single backslash inside the JSON string, which JSON decodes as
    /// control characters (backspace, tab…). A regex never contains those raw, so they are turned back into the regex
    /// escapes the model meant (backspace → <c>\b</c>, form feed → <c>\f</c>, tab → <c>\t</c>, new line → <c>\n</c>,
    /// carriage return → <c>\r</c>).
    /// </summary>
    private static string RestoreEscapes(string regex) => regex
        .Replace("\b", @"\b")
        .Replace("\f", @"\f")
        .Replace("\t", @"\t")
        .Replace("\n", @"\n")
        .Replace("\r", @"\r");

    /// <summary>
    /// Keeps only the regex of a model answer: drops reasoning blocks, code fences, labels ("Regex:"), quotes,
    /// C# string prefixes and /…/flags delimiters; returns the first remaining non-empty line.
    /// </summary>
    internal static string? ExtractRegex(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            return null;

        var text = Regex.Replace(answer, @"<think>.*?</think>", string.Empty, RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Content of the first code fence, when there is one.
        var fence = Regex.Match(text, @"```[^\n]*\n(?<code>.*?)```", RegexOptions.Singleline);
        if (fence.Success)
            text = fence.Groups["code"].Value;

        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null)
            return null;

        line = Regex.Replace(line, @"^(regex|regular expression|pattern)\s*[:=]\s*", string.Empty, RegexOptions.IgnoreCase);
        if (line.Length > 1 && line[0] == '`' && line[^1] == '`')
            line = line.Trim('`');
        if (line.StartsWith("@\"") && line.EndsWith('"') && line.Length > 2)
            line = line[2..^1].Replace("\"\"", "\"");
        else if (line.Length > 1 && (line[0] == '"' && line[^1] == '"' || line[0] == '\'' && line[^1] == '\''))
            line = line[1..^1];
        var slashes = Regex.Match(line, @"^/(?<body>.+)/[a-z]*$");
        if (slashes.Success)
            line = slashes.Groups["body"].Value;

        return line.Length == 0 ? null : line;
    }
}
