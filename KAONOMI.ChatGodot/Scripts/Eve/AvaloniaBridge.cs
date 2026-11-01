using System;
using System.IO;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Godot;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The view models and services of the Avalonia chat are shared as they are: a few of them decode pictures with Avalonia
/// bitmaps or post work on the Avalonia dispatcher. Avalonia is started here headless (no window, Skia for the pictures)
/// on the thread of Godot, its dispatcher pumped at every frame, and its bitmaps turned into Godot textures.
/// </summary>
public static class AvaloniaBridge
{
    private static bool _started;

    /// <summary>Starts Avalonia headless on the current (main) thread, once.</summary>
    public static void Initialize()
    {
        Bind.MainThread = System.Environment.CurrentManagedThreadId;
        if (_started)
            return;
        _started = true;
        try
        {
            AppBuilder.Configure<Avalonia.Application>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithoutStarting();
        }
        catch (Exception ex)
        {
            GD.PushWarning($"Avalonia (pictures, dispatcher) could not start: {ex.Message}");
        }
    }

    /// <summary>The method of the dispatcher turning the timers due into jobs (headless, nothing else calls it).</summary>
    private static readonly System.Reflection.MethodInfo? PromoteTimers =
        typeof(Avalonia.Threading.Dispatcher).GetMethod("PromoteTimers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
        ?? typeof(Avalonia.Threading.Dispatcher).GetMethod("OnOSTimer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    /// <summary>Runs the work posted on the Avalonia dispatcher and its timers due (called at every frame).</summary>
    public static void Pump()
    {
        if (!_started)
            return;
        try
        {
            if (PromoteTimers is { } promote && promote.GetParameters().Length == 0)
                promote.Invoke(Avalonia.Threading.Dispatcher.UIThread, null);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
        catch (Exception ex)
        {
            GD.PushWarning("Avalonia dispatcher: " + ex.Message);
        }
    }

    /// <summary>An Avalonia bitmap as a Godot texture (null when there is none).</summary>
    public static ImageTexture? ToTexture(Avalonia.Media.Imaging.Bitmap? bitmap)
    {
        if (bitmap is null)
            return null;
        try
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream);
            var image = new Image();
            return image.LoadPngFromBuffer(stream.ToArray()) == Error.Ok ? ImageTexture.CreateFromImage(image) : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// A picture file as a texture, reduced to <paramref name="maxSize"/> pixels on its longest side when given (0: as
    /// it is); null when it cannot be read.
    /// </summary>
    public static ImageTexture? LoadTexture(string path, int maxSize = 0)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var image = new Image();
            if (image.Load(path) != Error.Ok)
            {
                // Formats Godot does not read (HEIC…): through Avalonia.
                using var stream = File.OpenRead(path);
                return ToTexture(new Avalonia.Media.Imaging.Bitmap(stream));
            }
            if (maxSize > 0 && Math.Max(image.GetWidth(), image.GetHeight()) > maxSize)
            {
                var scale = (float)maxSize / Math.Max(image.GetWidth(), image.GetHeight());
                image.Resize(Math.Max(1, (int)(image.GetWidth() * scale)), Math.Max(1, (int)(image.GetHeight() * scale)), Image.Interpolation.Bilinear);
            }
            return ImageTexture.CreateFromImage(image);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>A Godot color from an Avalonia color.</summary>
    public static Godot.Color ToColor(Avalonia.Media.Color color) => Godot.Color.Color8(color.R, color.G, color.B, color.A);

    /// <summary>A Godot color from an Avalonia brush (a solid one; grey otherwise).</summary>
    public static Godot.Color ToColor(Avalonia.Media.IBrush? brush) =>
        brush is Avalonia.Media.ISolidColorBrush solid ? ToColor(solid.Color) : new Godot.Color(0.6f, 0.6f, 0.6f);
}
