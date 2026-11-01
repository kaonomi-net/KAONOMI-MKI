using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace KAONOMI.Chat.Services;

/// <summary>A colored piece of code: its place in the code, its color (#RRGGBB) and style.</summary>
public readonly record struct CodeSpan(int Start, int Length, string Color, bool Bold, bool Italic);

/// <summary>
/// Syntax coloring of code with the TextMate grammars of VS Code (TextMateSharp) and its Light+ theme: C#, Python,
/// JavaScript, TypeScript, Java, C / C++, Go, Rust, SQL, shell, HTML, CSS, JSON, YAML, XML, Markdown…
/// </summary>
public static class CodeHighlighter
{
    /// <summary>Languages offered in the code windows (TextMate language ids).</summary>
    public static readonly string[] Languages =
    [
        "python", "csharp", "javascript", "typescript", "java", "cpp", "c", "go", "rust", "sql", "shellscript",
        "powershell", "html", "css", "json", "yaml", "xml", "markdown", "php", "ruby", "kotlin", "swift", "fsharp", "r", "lua"
    ];

    /// <summary>Usual names of the languages (Markdown fences) and the TextMate id they stand for.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = "csharp", ["c#"] = "csharp", ["py"] = "python", ["js"] = "javascript", ["ts"] = "typescript",
        ["sh"] = "shellscript", ["bash"] = "shellscript", ["zsh"] = "shellscript", ["shell"] = "shellscript",
        ["ps1"] = "powershell", ["pwsh"] = "powershell", ["c++"] = "cpp", ["h"] = "c", ["hpp"] = "cpp",
        ["yml"] = "yaml", ["md"] = "markdown", ["rb"] = "ruby", ["kt"] = "kotlin", ["fs"] = "fsharp", ["rs"] = "rust",
        ["golang"] = "go", ["htm"] = "html", ["xaml"] = "xml", ["axaml"] = "xml", ["jsx"] = "javascript", ["tsx"] = "typescript"
    };

    /// <summary>The TextMate id of <paramref name="language"/> (an alias such as <c>cs</c>, <c>py</c> or <c>js</c> included).</summary>
    public static string Normalize(string? language)
    {
        var id = (language ?? string.Empty).Trim().ToLowerInvariant();
        return Aliases.TryGetValue(id, out var alias) ? alias : id;
    }

    private static readonly Lazy<(RegistryOptions Options, Registry Registry, Theme Theme)> Engine = new(() =>
    {
        var options = new RegistryOptions(ThemeName.LightPlus);
        var registry = new Registry(options);
        return (options, registry, registry.GetTheme());
    });

    private static readonly ConcurrentDictionary<string, IGrammar?> Grammars = new();

    /// <summary>The colored pieces of <paramref name="code"/> in <paramref name="language"/>; none for an unknown language.</summary>
    public static IReadOnlyList<CodeSpan> Highlight(string? language, string code)
    {
        var spans = new List<CodeSpan>();
        try
        {
            var (options, registry, theme) = Engine.Value;
            var id = Normalize(language);
            var grammar = Grammars.GetOrAdd(id, key =>
                options.GetScopeByLanguageId(key) is { Length: > 0 } scope ? registry.LoadGrammar(scope) : null);
            if (grammar is null)
                return spans;

            IStateStack? state = null;
            var offset = 0;
            foreach (var line in code.Split('\n'))
            {
                var text = line.TrimEnd('\r');
                var result = grammar.TokenizeLine(text, state, TimeSpan.FromMilliseconds(200));
                state = result.RuleStack;
                foreach (var token in result.Tokens)
                {
                    var start = Math.Min(token.StartIndex, text.Length);
                    var end = Math.Min(token.EndIndex, text.Length);
                    if (end <= start)
                        continue;
                    // The most specific rule with a color wins; the style (bold, italic) as well.
                    int foreground = 0, style = -1;
                    foreach (var rule in theme.Match(token.Scopes))
                    {
                        if (foreground == 0 && rule.foreground > 0)
                            foreground = rule.foreground;
                        if (style < 0 && rule.fontStyle > 0)
                            style = (int)rule.fontStyle;
                    }
                    if (foreground > 0 && theme.GetColor(foreground) is { Length: >= 7 } color
                                       && !color.StartsWith("#000000", StringComparison.OrdinalIgnoreCase))
                        spans.Add(new CodeSpan(offset + start, end - start, color[..7].ToUpperInvariant(),
                            style > 0 && (style & 2) != 0, style > 0 && (style & 1) != 0));
                }
                offset += line.Length + 1;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No coloring rather than no code.
            ActivityLog.Error("Code", $"Syntax coloring of {language} failed", ex);
            spans.Clear();
        }
        return spans;
    }
}
