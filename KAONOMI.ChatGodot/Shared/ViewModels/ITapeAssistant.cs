using System.Threading;
using System.Threading.Tasks;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// What the ribbon and the right-click menu of the rich text editor (<c>TapeRibbon</c>, <c>RichTapeEditor</c>) ask from
/// the view model of their window: the LLM of the chat (evaluate, translate, correct, rewrite a selection, generate code)
/// and Fooocus (generate an image). Implemented by the tape of <c>/work</c> and by the journal.
/// </summary>
public interface ITapeAssistant
{
    /// <summary>Name of the model of the chat (null when none).</summary>
    string? CurrentModelName { get; }

    /// <summary>True when the LLM can be asked (evaluate, translate, rewrite, generate code).</summary>
    bool CanEvaluate { get; }

    /// <summary>True when images can be generated with Fooocus.</summary>
    bool CanGenerateImages { get; }

    /// <summary>The criticism of <paramref name="text"/> and ideas to improve it, in Markdown.</summary>
    Task<string> EvaluateAsync(string text, CancellationToken cancellationToken);

    /// <summary><paramref name="text"/> translated into <paramref name="language"/>.</summary>
    Task<string> TranslateAsync(string text, string language, CancellationToken cancellationToken);

    /// <summary>The language of <paramref name="text"/> (its English name).</summary>
    Task<string> DetectLanguageAsync(string text, CancellationToken cancellationToken);

    /// <summary><paramref name="text"/> corrected (<c>spelling</c>) or rewritten (<c>readability</c>).</summary>
    Task<string> RewriteAsync(string text, string mode, CancellationToken cancellationToken, string? language = null);

    /// <summary>Code written for <paramref name="prompt"/>: its language and the code.</summary>
    Task<(string Language, string Code)> GenerateCodeAsync(string prompt, string language, CancellationToken cancellationToken);

    /// <summary>An image generated for <paramref name="prompt"/>, saved as a file; returns its path.</summary>
    Task<string> GenerateImageAsync(string prompt, string aspectRatio, string performance, CancellationToken cancellationToken);
}
