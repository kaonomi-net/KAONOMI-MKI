using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Services;

/// <summary>
/// HTML code of an answer opened in the default web browser: each HTML block is written to a file of the temporary
/// folder (<c>kaonomi-html</c>) and opened with the default application of <c>.html</c> files.
/// </summary>
public static class HtmlPreview
{
    private static string Folder => Path.Combine(Path.GetTempPath(), "kaonomi-html");

    /// <summary>Opens every HTML block of <paramref name="snippets"/> in the browser; returns the files written.</summary>
    public static IReadOnlyList<string> Open(IEnumerable<CodeSnippet> snippets)
    {
        var html = snippets.Where(s => s.IsHtml).ToList();
        if (html.Count == 0)
            return [];

        Directory.CreateDirectory(Folder);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var files = new List<string>();
        for (var i = 0; i < html.Count; i++)
        {
            var path = Path.Combine(Folder, html.Count == 1 ? $"answer-{stamp}.html" : $"answer-{stamp}-{i + 1}.html");
            try
            {
                File.WriteAllText(path, html[i].Code);
                PictureLibrary.OpenWithSystem(path);
                files.Add(path);
                ActivityLog.Info("Chat", $"HTML code opened in the browser: {path}");
            }
            catch (Exception ex)
            {
                ActivityLog.Error("Chat", $"Could not open the HTML code in the browser ({path})", ex);
            }
        }
        return files;
    }
}
