using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace KAONOMI.Chat.Services;

/// <summary>A sub-folder of the picture library.</summary>
public sealed record PictureFolder(string Name, string Path);

/// <summary>A picture file of the library: its path, size and last change.</summary>
public sealed record PictureFile(string Path, long Size, DateTime Modified)
{
    /// <summary>File name with its extension.</summary>
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Files of the picture library of <c>/pictures</c>: the sub-folders and pictures of a folder, and their decoding into
/// thumbnails and previews. JPEG, PNG, GIF, BMP, WebP and ICO are decoded directly; HEIC, HEIF, TIFF and PSD are
/// converted to JPEG first with <c>sips</c> on macOS (cached in the temp folder), and shown without a thumbnail elsewhere.
/// </summary>
public static class PictureLibrary
{
    /// <summary>Extensions decoded directly (Skia).</summary>
    private static readonly HashSet<string> DirectFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".gif", ".bmp", ".webp", ".ico"
    };

    /// <summary>Extensions converted with sips on macOS before decoding.</summary>
    private static readonly HashSet<string> ConvertedFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif", ".tif", ".tiff", ".psd"
    };

    /// <summary>Most decodings running at the same time (thumbnails and previews).</summary>
    private static readonly SemaphoreSlim Decoders = new(Math.Clamp(Environment.ProcessorCount / 2, 2, 6));

    /// <summary>Folder of the JPEG copies made by sips (thumbnails and previews of HEIC, TIFF, PSD).</summary>
    private static string CacheDirectory => Path.Combine(Path.GetTempPath(), "kaonomi-pictures");

    /// <summary>True when the file is a picture of the library (by its extension).</summary>
    public static bool IsPicture(string path) =>
        Path.GetExtension(path) is { Length: > 0 } extension && (DirectFormats.Contains(extension) || ConvertedFormats.Contains(extension));

    /// <summary>True when a thumbnail and a preview can be made for the file on this system.</summary>
    public static bool CanDecode(string path) =>
        DirectFormats.Contains(Path.GetExtension(path)) || (OperatingSystem.IsMacOS() && ConvertedFormats.Contains(Path.GetExtension(path)));

    /// <summary>
    /// The visible sub-folders (hidden ones and macOS packages such as the Photos library are left out) and the pictures
    /// of <paramref name="folder"/>, read without opening the files.
    /// </summary>
    public static (IReadOnlyList<PictureFolder> Folders, IReadOnlyList<PictureFile> Pictures) List(string folder)
    {
        var directory = new DirectoryInfo(folder);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };

        var folders = directory.EnumerateDirectories("*", options)
            .Where(d => !d.Name.StartsWith('.') && !IsPackage(d.Name))
            .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(d => new PictureFolder(d.Name, d.FullName))
            .ToList();
        var pictures = directory.EnumerateFiles("*", options)
            .Where(f => !f.Name.StartsWith('.') && IsPicture(f.Name))
            .Select(f => new PictureFile(f.FullName, f.Length, f.LastWriteTime))
            .ToList();
        return (folders, pictures);
    }

    /// <summary>macOS packages shown as files by the Finder (Photos library, applications…).</summary>
    private static bool IsPackage(string name) =>
        name.EndsWith(".photoslibrary", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".aplibrary", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".migratedphotolibrary", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The picture decoded to <paramref name="width"/> pixels wide (aspect kept, never enlarged), or null when it cannot be
    /// decoded. Runs on a background thread, a few at a time.
    /// </summary>
    public static async Task<Bitmap?> DecodeAsync(string path, int width, CancellationToken cancellationToken)
    {
        await Decoders.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => Decode(path, width, cancellationToken), cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            Decoders.Release();
        }
    }

    private static Bitmap? Decode(string path, int width, CancellationToken cancellationToken)
    {
        var source = path;
        if (!DirectFormats.Contains(Path.GetExtension(path)))
        {
            if (!OperatingSystem.IsMacOS() || !ConvertedFormats.Contains(Path.GetExtension(path)))
                return null;
            source = ConvertWithSips(path, width, cancellationToken);
            if (source is null)
                return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(source);
        return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.MediumQuality);
    }

    /// <summary>
    /// JPEG copy of <paramref name="path"/> at most <paramref name="width"/> pixels on its longest side, made by sips and
    /// kept in the cache (named after the path, size, date and width); null when sips fails.
    /// </summary>
    private static string? ConvertWithSips(string path, int width, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        var key = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{width}")));
        var target = Path.Combine(CacheDirectory, key + ".jpg");
        if (File.Exists(target))
            return target;

        Directory.CreateDirectory(CacheDirectory);
        var start = new ProcessStartInfo("/usr/bin/sips")
        {
            ArgumentList = { "-s", "format", "jpeg", "-s", "formatOptions", "85", "-Z", width.ToString(), path, "--out", target },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var sips = Process.Start(start);
        if (sips is null)
            return null;
        using var kill = cancellationToken.Register(() =>
        {
            try { sips.Kill(); } catch (InvalidOperationException) { }
        });
        sips.StandardOutput.ReadToEnd();
        sips.StandardError.ReadToEnd();
        sips.WaitForExit();
        return sips.ExitCode == 0 && File.Exists(target) ? target : null;
    }

    /// <summary>Formats re-encoded with Skia to rotate them (on macOS, sips rotates every format it can write).</summary>
    private static readonly Dictionary<string, SkiaSharp.SKEncodedImageFormat> SkiaWritable = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = SkiaSharp.SKEncodedImageFormat.Jpeg,
        [".jpeg"] = SkiaSharp.SKEncodedImageFormat.Jpeg,
        [".jpe"] = SkiaSharp.SKEncodedImageFormat.Jpeg,
        [".png"] = SkiaSharp.SKEncodedImageFormat.Png,
        [".webp"] = SkiaSharp.SKEncodedImageFormat.Webp
    };

    /// <summary>Formats sips writes (and therefore rotates) on macOS.</summary>
    private static readonly HashSet<string> SipsWritable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".gif", ".bmp", ".tif", ".tiff", ".heic", ".heif"
    };

    /// <summary>True when the picture can be rotated and saved on this system.</summary>
    public static bool CanRotate(string path) => OperatingSystem.IsMacOS()
        ? SipsWritable.Contains(Path.GetExtension(path))
        : SkiaWritable.ContainsKey(Path.GetExtension(path));

    /// <summary>
    /// Rotates the picture by <paramref name="degrees"/> clockwise (90, 180 or 270) and saves it in place, in its own
    /// format: with sips on macOS (the metadata is kept), else re-encoded with Skia (JPEG quality 92, PNG, WebP).
    /// Throws with the reason when it fails.
    /// </summary>
    public static Task RotateAsync(string path, int degrees) => Task.Run(() =>
    {
        degrees = ((degrees % 360) + 360) % 360;
        if (degrees == 0)
            return;
        if (!CanRotate(path))
            throw new NotSupportedException($"{Path.GetExtension(path).TrimStart('.').ToUpperInvariant()} pictures cannot be rotated here");

        if (OperatingSystem.IsMacOS())
        {
            var start = new ProcessStartInfo("/usr/bin/sips")
            {
                ArgumentList = { "-r", degrees.ToString(), path },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var sips = Process.Start(start) ?? throw new InvalidOperationException("sips could not be started");
            sips.StandardOutput.ReadToEnd();
            var error = sips.StandardError.ReadToEnd();
            sips.WaitForExit();
            if (sips.ExitCode != 0)
                throw new IOException($"sips failed: {error.Trim()}");
            return;
        }

        using var source = SkiaSharp.SKBitmap.Decode(path) ?? throw new IOException("The picture cannot be decoded");
        var turned = degrees == 180;
        using var rotated = new SkiaSharp.SKBitmap(turned ? source.Width : source.Height, turned ? source.Height : source.Width);
        using (var canvas = new SkiaSharp.SKCanvas(rotated))
        {
            canvas.Translate(rotated.Width / 2f, rotated.Height / 2f);
            canvas.RotateDegrees(degrees);
            canvas.Translate(-source.Width / 2f, -source.Height / 2f);
            canvas.DrawBitmap(source, 0, 0);
        }
        using var image = SkiaSharp.SKImage.FromBitmap(rotated);
        using var data = image.Encode(SkiaWritable[Path.GetExtension(path)], 92) ?? throw new IOException("The picture cannot be encoded");
        // Written next to it first, so that a failure never leaves a broken file.
        var temporary = path + ".rotating";
        using (var stream = File.Create(temporary))
            data.SaveTo(stream);
        File.Move(temporary, path, overwrite: true);
    });

    /// <summary>Opens the file (or folder) with the default application of the system.</summary>
    public static void OpenWithSystem(string path) => Process.Start(OperatingSystem.IsWindows()
        ? new ProcessStartInfo(path) { UseShellExecute = true }
        : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", [path]) { UseShellExecute = false });

    /// <summary>Shows the file in the Finder / Explorer (its folder on Linux).</summary>
    public static void Reveal(string path)
    {
        if (OperatingSystem.IsMacOS())
            Process.Start(new ProcessStartInfo("open", ["-R", path]) { UseShellExecute = false });
        else if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        else
            OpenWithSystem(Path.GetDirectoryName(path)!);
    }

    /// <summary>
    /// Moves the file to the trash of the system (it can be restored from there): ~/.Trash on macOS, the Recycle Bin on
    /// Windows, ~/.local/share/Trash on Linux. Returns false when no trash is available.
    /// </summary>
    public static bool MoveToTrash(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return true;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var trash = OperatingSystem.IsMacOS() ? Path.Combine(home, ".Trash") : Path.Combine(home, ".local", "share", "Trash", "files");
        if (!Directory.Exists(trash))
            return false;

        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var target = Path.Combine(trash, name + extension);
        for (var i = 2; File.Exists(target); i++)
            target = Path.Combine(trash, $"{name} {i}{extension}");
        File.Move(path, target);
        return true;
    }

    /// <summary>Size in B, KB, MB or GB.</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB"
    };
}
