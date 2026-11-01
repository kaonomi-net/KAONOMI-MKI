using System;
using System.IO;
using Godot;
using KAONOMI.Chat.Services;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The wallpaper of the desktop: a picture behind the windows (a right click on a picture → 🖼 Set as wallpaper; a
/// right click on the empty desktop → Remove the wallpaper). The picture is copied in the data folder of the chat, so
/// that it stays when the file is moved, and comes back at the next start.
/// </summary>
public static class Wallpaper
{
    /// <summary>The item of the context menus of the pictures.</summary>
    public const string MenuText = "🖼 Set as wallpaper";

    /// <summary>Raised when the wallpaper changes (or is removed).</summary>
    public static event Action? Changed;

    /// <summary>The copy of the wallpaper, or null when there is none.</summary>
    public static string? Current => Preferences.Wallpaper is { } path && File.Exists(path) ? path : null;

    /// <summary>Makes the picture of <paramref name="source"/> the wallpaper.</summary>
    public static void Set(string source)
    {
        try
        {
            // A new name each time: the copy before goes (and no texture of an old one is reused).
            var copy = Path.Combine(Preferences.Folder, $"wallpaper-{DateTime.Now:yyyyMMdd-HHmmss}{Path.GetExtension(source)}");
            Directory.CreateDirectory(Preferences.Folder);
            File.Copy(source, copy, true);
            var old = Preferences.Wallpaper;
            Preferences.Wallpaper = copy;
            if (old is not null && old != copy && File.Exists(old))
                File.Delete(old);
            ActivityLog.Info("Wallpaper", $"Wallpaper of the desktop: {Path.GetFileName(source)}");
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ActivityLog.Error("Wallpaper", $"The wallpaper could not be set from {source}", ex);
        }
    }

    /// <summary>Removes the wallpaper (the desktop shows its theme again).</summary>
    public static void Clear()
    {
        var old = Preferences.Wallpaper;
        Preferences.Wallpaper = null;
        try
        {
            if (old is not null && File.Exists(old))
                File.Delete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning("The old wallpaper was not deleted: " + ex.Message);
        }
        ActivityLog.Info("Wallpaper", "Wallpaper of the desktop removed");
        Changed?.Invoke();
    }

    /// <summary>The picture of the wallpaper, or null (none, or it cannot be read).</summary>
    public static Texture2D? Load()
    {
        if (Current is not { } path)
            return null;
        var image = Image.LoadFromFile(path);
        return image is null || image.IsEmpty() ? null : ImageTexture.CreateFromImage(image);
    }
}
