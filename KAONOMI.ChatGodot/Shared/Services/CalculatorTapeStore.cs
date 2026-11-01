using System;
using System.IO;
using System.Linq;
using System.Text;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>
/// The tape of the <c>/work</c> window: every calculation done with <c>=</c>, kept in
/// <c>calculator-tape.txt</c> next to the executable (beside config.yml), so that it survives the restarts.
/// </summary>
public static class CalculatorTapeStore
{
    private const string FileName = "calculator-tape.txt";

    /// <summary>File of the id of the document the tape was last saved to or loaded from (<c>calculator-document.txt</c>).</summary>
    private static string DocumentIdPath => Path.Combine(AppContext.BaseDirectory, "calculator-document.txt");

    /// <summary>Id of the document the tape belongs to (saved with 💾 or loaded with 📂), or null.</summary>
    public static string? LoadDocumentId()
    {
        try
        {
            return File.Exists(DocumentIdPath) && File.ReadAllText(DocumentIdPath).Trim() is { Length: > 0 } id ? id : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Remembers the document the tape belongs to (null forgets it).</summary>
    public static void SaveDocumentId(string? id)
    {
        try
        {
            if (id is null)
                File.Delete(DocumentIdPath);
            else
                File.WriteAllText(DocumentIdPath, id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error("Calculator", "calculator-document.txt not saved", ex);
        }
    }

    /// <summary>Full path of <c>calculator-tape.txt</c>.</summary>
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>The text of the tape; empty when there is none yet.</summary>
    public static string Load()
    {
        try
        {
            return File.Exists(FilePath) ? File.ReadAllText(FilePath) : string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error("Calculator", $"{FileName} not read", ex);
            return string.Empty;
        }
    }

    /// <summary>
    /// Brings the paths of the graph pictures of the tape (<c>![graph](path)</c>) written on another system to the
    /// standards of this one (see <see cref="OsPaths"/>); called at startup. Returns the number of lines changed.
    /// </summary>
    public static int AdaptPaths()
    {
        var text = Load();
        var adapted = OsPaths.TranslateLinks(text);
        if (adapted == text)
            return 0;
        Save(adapted);
        return text.Split('\n').Zip(adapted.Split('\n')).Count(pair => pair.First != pair.Second);
    }

    /// <summary>Writes <paramref name="text"/> to <c>calculator-tape.txt</c> (UTF-8, without BOM).</summary>
    public static void Save(string text)
    {
        try
        {
            File.WriteAllText(FilePath, text ?? string.Empty, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error("Calculator", $"{FileName} not saved", ex);
        }
    }
}
