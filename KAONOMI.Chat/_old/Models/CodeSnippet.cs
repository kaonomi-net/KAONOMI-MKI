using System;
using System.Collections.Generic;

namespace KAONOMI.Chat.Models;

/// <summary>
/// A fenced code block extracted from an assistant answer.
/// </summary>
public sealed record CodeSnippet(string Language, string Code)
{
    private static readonly Dictionary<string, (string Name, string Extension)> KnownLanguages =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["cs"] = ("C#", ".cs"),
            ["csharp"] = ("C#", ".cs"),
            ["c#"] = ("C#", ".cs"),
            ["py"] = ("Python", ".py"),
            ["python"] = ("Python", ".py"),
            ["cpp"] = ("C++", ".cpp"),
            ["c++"] = ("C++", ".cpp"),
            ["cxx"] = ("C++", ".cpp"),
            ["hpp"] = ("C++", ".hpp"),
            ["c"] = ("C", ".c"),
            ["h"] = ("C", ".h"),
            ["java"] = ("Java", ".java"),
            ["js"] = ("JavaScript", ".js"),
            ["javascript"] = ("JavaScript", ".js"),
            ["ts"] = ("TypeScript", ".ts"),
            ["typescript"] = ("TypeScript", ".ts"),
            ["go"] = ("Go", ".go"),
            ["golang"] = ("Go", ".go"),
            ["rs"] = ("Rust", ".rs"),
            ["rust"] = ("Rust", ".rs"),
            ["kt"] = ("Kotlin", ".kt"),
            ["kotlin"] = ("Kotlin", ".kt"),
            ["swift"] = ("Swift", ".swift"),
            ["php"] = ("PHP", ".php"),
            ["rb"] = ("Ruby", ".rb"),
            ["ruby"] = ("Ruby", ".rb"),
            ["fs"] = ("F#", ".fs"),
            ["fsharp"] = ("F#", ".fs"),
            ["vb"] = ("VB.NET", ".vb"),
            ["sql"] = ("SQL", ".sql"),
            ["sh"] = ("Shell", ".sh"),
            ["bash"] = ("Bash", ".sh"),
            ["shell"] = ("Shell", ".sh"),
            ["zsh"] = ("Shell", ".sh"),
            ["ps1"] = ("PowerShell", ".ps1"),
            ["powershell"] = ("PowerShell", ".ps1"),
            ["pwsh"] = ("PowerShell", ".ps1"),
            ["bat"] = ("Batch", ".bat"),
            ["cmd"] = ("Batch", ".cmd"),
            ["html"] = ("HTML", ".html"),
            ["css"] = ("CSS", ".css"),
            ["scss"] = ("SCSS", ".scss"),
            ["xml"] = ("XML", ".xml"),
            ["xaml"] = ("XAML", ".xaml"),
            ["axaml"] = ("AXAML", ".axaml"),
            ["json"] = ("JSON", ".json"),
            ["yaml"] = ("YAML", ".yaml"),
            ["yml"] = ("YAML", ".yml"),
            ["toml"] = ("TOML", ".toml"),
            ["ini"] = ("INI", ".ini"),
            ["md"] = ("Markdown", ".md"),
            ["markdown"] = ("Markdown", ".md"),
            ["dockerfile"] = ("Dockerfile", ".dockerfile"),
            ["r"] = ("R", ".r"),
            ["lua"] = ("Lua", ".lua"),
            ["dart"] = ("Dart", ".dart"),
            ["scala"] = ("Scala", ".scala"),
        };

    /// <summary>Human-readable language label for UI and save dialogs.</summary>
    public string DisplayLanguage =>
        KnownLanguages.TryGetValue(Language, out var known) ? known.Name
        : string.IsNullOrWhiteSpace(Language) ? "Text" : Language;

    /// <summary>Suggested file extension when saving this snippet.</summary>
    public string FileExtension =>
        KnownLanguages.TryGetValue(Language, out var known) ? known.Extension : ".txt";

    /// <summary>
    /// True for an HTML page: an <c>html</c> / <c>htm</c> block, or an untagged one starting with a doctype or an
    /// <c>&lt;html&gt;</c> element.
    /// </summary>
    public bool IsHtml =>
        Language.Equals("html", StringComparison.OrdinalIgnoreCase) || Language.Equals("htm", StringComparison.OrdinalIgnoreCase)
        || string.IsNullOrWhiteSpace(Language)
           && (Code.TrimStart().StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase)
               || Code.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase));

    /// <summary>Number of lines in the snippet source text.</summary>
    public int LineCount => Code.Split('\n').Length;
}
