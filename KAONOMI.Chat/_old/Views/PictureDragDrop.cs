using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace KAONOMI.Chat.Views;

/// <summary>
/// Drag and drop of pictures: from the picture library (<c>/pictures</c>) or the Finder / Explorer to a Markdown
/// text box (the journal editor, the calculator tape in Edit mode). A dropped picture becomes an image line
/// <c>![name|width](path)</c>, inserted at the start of the line under the pointer.
/// </summary>
public static class PictureDragDrop
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".heif", ".tif", ".tiff"
    };

    /// <summary>True when <paramref name="path"/> has the extension of a picture.</summary>
    public static bool IsPicture(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>The data dragged from the library: the files (for the other apps) and their paths as text.</summary>
    public static DataTransfer Create(IReadOnlyList<string> paths, IEnumerable<IStorageItem> files)
    {
        var data = new DataTransfer();
        foreach (var file in files)
            data.Add(DataTransferItem.CreateFile(file));
        data.Add(DataTransferItem.CreateText(string.Join('\n', paths)));
        return data;
    }

    /// <summary>The local paths of the pictures in the dragged data (files, or paths given as text).</summary>
    public static IReadOnlyList<string> PicturePaths(IDataTransfer data)
    {
        var paths = (data.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0 && data.TryGetText() is { } text)
            paths = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return paths.Where(p => IsPicture(p) && File.Exists(p)).Distinct().ToList();
    }

    /// <summary>
    /// Lets <paramref name="box"/> receive pictures: <paramref name="apply"/> gets the new text and the caret position
    /// after the image lines, and the number of pictures inserted.
    /// </summary>
    public static void Attach(TextBox box, Action<string, int, int> apply)
    {
        DragDrop.SetAllowDrop(box, true);
        box.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = PicturePaths(e.DataTransfer).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }, handledEventsToo: true);
        box.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var paths = PicturePaths(e.DataTransfer);
            e.Handled = true;
            if (paths.Count == 0)
                return;
            var text = box.Text ?? string.Empty;
            var (newText, caret) = Insert(text, IndexAt(box, e.GetPosition(box)) ?? box.CaretIndex, paths);
            apply(newText, caret, paths.Count);
        }, handledEventsToo: true);
    }

    /// <summary>
    /// Inserts the image lines of <paramref name="paths"/> at the start of the line holding <paramref name="index"/>
    /// (or after the text when the index is at its end); returns the text and the position after the lines.
    /// </summary>
    public static (string Text, int Caret) Insert(string text, int index, IReadOnlyList<string> paths)
    {
        index = Math.Clamp(index, 0, text.Length);
        var lines = string.Join('\n', paths.Select(p => JournalMarkdown.ImageMarkdown(p, JournalMarkdown.DefaultImageWidth))) + "\n";
        int at;
        if (index == text.Length)
        {
            at = text.Length;
            if (text.Length > 0 && text[^1] != '\n')
                lines = "\n" + lines;
        }
        else
        {
            at = index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
        }
        return (text[..at] + lines + text[at..], at + lines.Length);
    }

    /// <summary>The character of <paramref name="box"/> under <paramref name="position"/> (the end below the last line).</summary>
    private static int? IndexAt(TextBox box, Point position)
    {
        if (box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault() is not { } presenter
            || box.TranslatePoint(position, presenter) is not { } point)
            return null;
        var layout = presenter.TextLayout;
        if (point.Y > layout.Height)
            return box.Text?.Length ?? 0;
        var hit = layout.HitTestPoint(point);
        return hit.TextPosition;
    }
}
