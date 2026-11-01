using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.ViewModels;

/// <summary>View model for browsing and exporting code blocks from an assistant reply.</summary>
public partial class CodeWindowViewModel : ViewModelBase, INoteContextProvider
{
    /// <summary>Design-time constructor with a sample snippet.</summary>
    public CodeWindowViewModel() : this([new CodeSnippet("csharp", "Console.WriteLine(\"Hello\");")])
    {
    }

    /// <summary>Builds tabs from the extracted snippets and selects the first.</summary>
    public CodeWindowViewModel(IReadOnlyList<CodeSnippet> snippets)
    {
        Snippets = snippets.Select((s, i) => new CodeSnippetViewModel(s, i + 1)).ToList();
        _selectedSnippet = Snippets.FirstOrDefault();
    }

    /// <summary>One view model per fenced code block.</summary>
    public IReadOnlyList<CodeSnippetViewModel> Snippets { get; }

    /// <summary>Window title reflecting block count and language.</summary>
    public string Title => Snippets.Count == 1
        ? $"Code {Snippets[0].Snippet.DisplayLanguage}"
        : $"Generated code · {Snippets.Count} blocks";

    [ObservableProperty]
    private CodeSnippetViewModel? _selectedSnippet;

    [ObservableProperty]
    private string _status = string.Empty;

    partial void OnSelectedSnippetChanged(CodeSnippetViewModel? value) => Status = string.Empty;
    /// <inheritdoc />
    public string NoteWindowName => "Code";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        snippetCount = Snippets.Count,
        selected = SelectedSnippet is { } snippet ? new { language = snippet.Snippet.Language, code = snippet.Snippet.Code } : null
    };

}

/// <summary>Presentation wrapper for one <see cref="CodeSnippet"/> in the code window.</summary>
public sealed class CodeSnippetViewModel
{
    /// <summary>Associates a snippet with its one-based index in the list.</summary>
    public CodeSnippetViewModel(CodeSnippet snippet, int index)
    {
        Snippet = snippet;
        Index = index;
    }

    /// <summary>Underlying language and source text.</summary>
    public CodeSnippet Snippet { get; }

    /// <summary>One-based position in the snippet list.</summary>
    public int Index { get; }

    /// <summary>Short label for the snippet tab or list header.</summary>
    public string Header => $"{Index} · {Snippet.DisplayLanguage}";

    /// <summary>Secondary line with language and line count.</summary>
    public string Details => $"{Snippet.DisplayLanguage} · {Snippet.LineCount} line(s)";

    /// <summary>Default file name for save-as, including extension.</summary>
    public string SuggestedFileName => $"code{(Index > 1 ? Index.ToString() : "")}{Snippet.FileExtension}";

    /// <summary>The snippet wrapped in a fence long enough not to collide with backticks inside the code.</summary>
    public string Markdown
    {
        get
        {
            var longestRun = 0;
            var run = 0;
            foreach (var c in Snippet.Code)
            {
                run = c == '`' ? run + 1 : 0;
                if (run > longestRun) longestRun = run;
            }

            var fence = new string('`', System.Math.Max(3, longestRun + 1));
            return $"{fence}{Snippet.Language}\n{Snippet.Code}\n{fence}";
        }
    }

}
