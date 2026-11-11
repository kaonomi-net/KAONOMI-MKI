using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Platform;
using KAONOMI.Chat.Services;

namespace KAONOMI.UI.Tools.Media;

/// <summary>
/// <see cref="MusicEngine"/> reads its helper (<c>music-player.swift</c> on macOS, <c>music-player.ps1</c> on Windows)
/// from the Avalonia resources of the KAONOMI.Chat assembly (<c>avares://KAONOMI.Chat/Assets/Audio/…</c>), which do
/// not exist in this application (the service is a shared source, without its resources). This wraps the Avalonia asset
/// loader so that these addresses are read from the folder <c>Assets/Audio</c> copied next to the application (or the
/// one of the Godot project); every other address goes to the usual loader.
/// </summary>
internal static class MusicAssets
{
    private const string Prefix = "avares://KAONOMI.Chat/Assets/Audio/";
    private static bool _installed;

    /// <summary>The folder of the helpers, or null when it cannot be found.</summary>
    public static string? AudioFolder { get; } = FindFolder();

    private static string? FindFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var folder in new[] { Path.Combine(dir.FullName, "Assets", "Audio"), Path.Combine(dir.FullName, "KAONOMI.ChatGodot", "Assets", "Audio") })
                if (File.Exists(Path.Combine(folder, "music-player.swift")) || File.Exists(Path.Combine(folder, "music-player.ps1")))
                    return folder;
        }
        return null;
    }

    /// <summary>Puts the wrapper in place (once; after Avalonia has started).</summary>
    public static void Install()
    {
        if (_installed)
            return;
        _installed = true;
        if (AudioFolder is null)
        {
            ActivityLog.Info("Music", "The folder Assets/Audio was not found: the player cannot start");
            return;
        }
        try
        {
            var locatorType = typeof(IAssetLoader).Assembly.GetType("Avalonia.AvaloniaLocator");
            var current = locatorType?.GetProperty("Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            var mutable = locatorType?.GetProperty("CurrentMutable", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (current is null || mutable is null)
                throw new InvalidOperationException("no Avalonia locator");
            var inner = current.GetType().GetMethod("GetService", [typeof(Type)])?.Invoke(current, [typeof(IAssetLoader)]) as IAssetLoader
                        ?? new StandardAssetLoader();
            if (inner is FolderAssetLoader)
                return;
            var bind = mutable.GetType().GetMethods().First(m => m.Name == "Bind" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                .MakeGenericMethod(typeof(IAssetLoader)).Invoke(mutable, null)!;
            var toConstant = bind.GetType().GetMethods().First(m => m.Name == "ToConstant" && m.GetParameters().Length == 1);
            if (toConstant.IsGenericMethodDefinition)
                toConstant = toConstant.MakeGenericMethod(typeof(IAssetLoader));
            toConstant.Invoke(bind, [new FolderAssetLoader(inner, AudioFolder)]);
        }
        catch (Exception ex)
        {
            ActivityLog.Error("Music", "The helper of the player cannot be provided", ex);
        }
    }

    /// <summary>The usual loader, except for the helpers of the music player read from <paramref name="folder"/>.</summary>
    private sealed class FolderAssetLoader(IAssetLoader inner, string folder) : IAssetLoader
    {
        private string? Local(Uri uri)
        {
            var text = uri.ToString();
            if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                return null;
            var path = Path.Combine(folder, Path.GetFileName(text[Prefix.Length..]));
            return File.Exists(path) ? path : null;
        }

        public void SetDefaultAssembly(Assembly assembly) => inner.SetDefaultAssembly(assembly);

        public bool Exists(Uri uri, Uri? baseUri = null) => Local(uri) is not null || inner.Exists(uri, baseUri);

        public Stream Open(Uri uri, Uri? baseUri = null) => Local(uri) is { } path ? File.OpenRead(path) : inner.Open(uri, baseUri);

        public (Stream stream, Assembly assembly) OpenAndGetAssembly(Uri uri, Uri? baseUri = null) =>
            Local(uri) is { } path ? (File.OpenRead(path), typeof(MusicAssets).Assembly) : inner.OpenAndGetAssembly(uri, baseUri);

        public Assembly? GetAssembly(Uri uri, Uri? baseUri = null) => Local(uri) is not null ? typeof(MusicAssets).Assembly : inner.GetAssembly(uri, baseUri);

        public IEnumerable<Uri> GetAssets(Uri uri, Uri? baseUri) => inner.GetAssets(uri, baseUri);

        public void InvalidateAssemblyCache(string name) => inner.InvalidateAssemblyCache(name);

        public void InvalidateAssemblyCache() => inner.InvalidateAssemblyCache();
    }
}
