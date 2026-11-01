using System;
using System.IO;
using System.Text;

namespace KAONOMI.Chat.Services;

/// <summary>
/// The system prompt of the chat, kept in <c>systemprompt.txt</c> next to the executable (beside config.yml): read at
/// startup, so that a manual change of the file is taken, and written whenever it is edited in the chat.
/// </summary>
public static class SystemPromptStore
{
    private const string FileName = "systemprompt.txt";

    /// <summary>Full path of <c>systemprompt.txt</c>.</summary>
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, FileName);

    /// <summary>
    /// The system prompt of <c>systemprompt.txt</c>; when the file does not exist yet, it is created with
    /// <paramref name="fallback"/> (systemPrompt of config.json), which is returned.
    /// </summary>
    public static string Load(string fallback)
    {
        try
        {
            if (File.Exists(FilePath))
                return File.ReadAllText(FilePath).TrimEnd('\r', '\n');
            Save(fallback);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error("Settings", $"{FileName} not read", ex);
        }
        return fallback;
    }

    /// <summary>Writes <paramref name="prompt"/> to <c>systemprompt.txt</c> (UTF-8, without BOM).</summary>
    public static void Save(string prompt)
    {
        try
        {
            File.WriteAllText(FilePath, (prompt ?? string.Empty) + Environment.NewLine, new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error("Settings", $"{FileName} not saved", ex);
        }
    }
}
