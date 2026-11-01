using System.Collections.Generic;
using System.IO;
using Godot;
using KAONOMI.Chat.Models;

namespace KAONOMI.ChatGodot.Tools.Work;

/// <summary>
/// The photos of the elements of the infrastructure diagrams (/infra): <c>Assets/infra/&lt;kind&gt;.jpg</c>, 256 × 256
/// (photos of Wikimedia Commons and pictures generated with Fooocus, see <c>Assets/infra/CREDITS.md</c>), read once.
/// Null for a kind without photo: its card shows its emoji.
/// </summary>
internal static class InfraPictures
{
    private static readonly Dictionary<BpmnElementKind, Texture2D?> Cache = [];

    public static Texture2D? Of(BpmnElementKind kind)
    {
        if (!kind.IsInfra())
            return null;
        if (Cache.TryGetValue(kind, out var cached))
            return cached;
        var resource = $"res://Assets/infra/{kind}.jpg";
        Texture2D? texture = null;
        // Imported by the editor (and in an exported chat), else read from the file as it is.
        if (ResourceLoader.Exists(resource))
            texture = ResourceLoader.Load<Texture2D>(resource);
        else if (ProjectSettings.GlobalizePath(resource) is var path && File.Exists(path) && Image.LoadFromFile(path) is { } image && !image.IsEmpty())
        {
            // Mipmaps: the photo stays smooth when the card is drawn smaller than it (the zoom of the canvas).
            image.GenerateMipmaps();
            texture = ImageTexture.CreateFromImage(image);
        }
        Cache[kind] = texture;
        return texture;
    }
}
