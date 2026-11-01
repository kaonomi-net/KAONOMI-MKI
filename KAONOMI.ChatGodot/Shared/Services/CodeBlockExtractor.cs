using System.Collections.Generic;
using System.Linq;
using KAONOMI.Chat.Models;
using Markdig;
using Markdig.Syntax;

namespace KAONOMI.Chat.Services;

/// <summary>Parses Markdown and collects fenced code blocks from assistant replies.</summary>
public static class CodeBlockExtractor
{
    /// <summary>Returns all non-empty fenced code blocks in the given Markdown text.</summary>
    public static IReadOnlyList<CodeSnippet> Extract(string markdown)
    {
        if (string.IsNullOrEmpty(markdown) || !markdown.Contains("```") && !markdown.Contains("~~~"))
            return [];

        return Markdown.Parse(markdown)
            .Descendants<FencedCodeBlock>()
            .Select(block => new CodeSnippet(
                // "csharp title=Program.cs" -> "csharp"
                (block.Info ?? string.Empty).Trim().Split(' ')[0],
                block.Lines.ToString().TrimEnd()))
            .Where(snippet => snippet.Code.Length > 0)
            .ToList();
    }
}
