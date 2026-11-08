using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media.Imaging;
using KAONOMI.Chat.Models;

namespace KAONOMI.UI.Tools.Work;

/// <summary>
/// The photos of the elements of the infrastructure diagrams (/infra): <c>Assets/infra/&lt;kind&gt;.jpg</c>, 256 × 256
/// (photos of Wikimedia Commons and pictures generated with Fooocus, see <c>Assets/infra/CREDITS.md</c>), read once.
/// They are the files of the Godot chat: <c>Assets/infra</c> next to the program when they were copied there, else the
/// folder of KAONOMI.ChatGodot found above it. Null for a kind without photo: its card shows its emoji.
/// </summary>
public static class InfraPictures
{
    private static readonly Dictionary<BpmnElementKind, Bitmap?> Cache = [];
    private static string? _folder;
    private static bool _searched;

    /// <summary>The folder of the photos (null when not found).</summary>
    public static string? Folder
    {
        get
        {
            if (_searched)
                return _folder;
            _searched = true;
            var local = Path.Combine(AppContext.BaseDirectory, "Assets", "infra");
            if (Directory.Exists(local))
                return _folder = local;
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "KAONOMI.ChatGodot", "Assets", "infra");
                if (Directory.Exists(candidate))
                    return _folder = candidate;
            }
            return null;
        }
    }

    public static Bitmap? Of(BpmnElementKind kind)
    {
        if (!kind.IsInfra())
            return null;
        if (Cache.TryGetValue(kind, out var cached))
            return cached;
        Bitmap? bitmap = null;
        if (Folder is { } folder && Path.Combine(folder, $"{kind}.jpg") is var path && File.Exists(path))
        {
            try
            {
                bitmap = new Bitmap(path);
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or NotSupportedException)
            {
                bitmap = null;
            }
        }
        Cache[kind] = bitmap;
        return bitmap;
    }
}
