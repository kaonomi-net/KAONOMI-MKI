using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Views;

/// <summary>Helpers to place windows on the largest screen. Call them once the window is opened (frame size known).</summary>
internal static class ScreenPlacement
{
    /// <summary>
    /// Largest screen: the one with the widest ratio (width / height, so an ultrawide screen comes first), then the most
    /// pixels; null when no screen is reported.
    /// </summary>
    public static Screen? LargestScreen(Window window) =>
        window.Screens.All
            .OrderByDescending(s => Math.Round((double)s.Bounds.Width / Math.Max(1, s.Bounds.Height), 2))
            .ThenByDescending(s => (long)s.Bounds.Width * s.Bounds.Height)
            .FirstOrDefault();

    /// <summary>True when <paramref name="screen"/> is 4K or more (3840 × 2160 pixels, landscape or portrait).</summary>
    public static bool Is4K(Screen screen) =>
        Math.Max(screen.Bounds.Width, screen.Bounds.Height) >= 3840
        && Math.Min(screen.Bounds.Width, screen.Bounds.Height) >= 2160;

    /// <summary>
    /// True when <paramref name="screen"/> is ultrawide: landscape, 21:9 or wider (width at least 2.3 × height) and
    /// 3440 × 1440 pixels or more.
    /// </summary>
    public static bool IsUltrawide(Screen screen) =>
        screen.Bounds.Width >= 3440
        && screen.Bounds.Height >= 1440
        && screen.Bounds.Width >= 2.3 * screen.Bounds.Height;

    /// <summary>
    /// True when <paramref name="screen"/> is super ultrawide: landscape, 32:9 or wider (width at least 3.2 × height) and
    /// 5120 × 1440 pixels or more.
    /// </summary>
    public static bool IsSuperUltrawide(Screen screen) =>
        screen.Bounds.Width >= 5120
        && screen.Bounds.Height >= 1440
        && screen.Bounds.Width >= 3.2 * screen.Bounds.Height;

    /// <summary>
    /// Sizes the window to <paramref name="width"/> x <paramref name="height"/> (client size, clamped to the
    /// working area) and centers it, frame included, on the largest screen.
    /// </summary>
    public static void CenterOnLargestScreen(Window window, double width, double height)
    {
        if (LargestScreen(window) is not { } screen)
            return;

        var area = screen.WorkingArea;
        var scaling = screen.Scaling;
        var (frameWidth, frameHeight) = FrameExtra(window);

        window.Width = Math.Max(window.MinWidth, Math.Min(width, area.Width / scaling - frameWidth));
        window.Height = Math.Max(window.MinHeight, Math.Min(height, area.Height / scaling - frameHeight));

        var widthPx = (int)Math.Ceiling((window.Width + frameWidth) * scaling);
        var heightPx = (int)Math.Ceiling((window.Height + frameHeight) * scaling);
        window.Position = new PixelPoint(
            area.X + Math.Max(0, (area.Width - widthPx) / 2),
            area.Y + Math.Max(0, (area.Height - heightPx) / 2));
    }

    /// <summary>Docks the window to the right edge of the largest screen, using its full working-area height.</summary>
    public static void DockRightOnLargestScreen(Window window)
    {
        if (LargestScreen(window) is { } screen)
            DockRight(window, screen);
    }

    /// <summary>Docks the window to the right edge of <paramref name="screen"/>, using its full working-area height.</summary>
    public static void DockRight(Window window, Screen screen)
    {
        var area = screen.WorkingArea;
        var scaling = screen.Scaling;
        var (frameWidth, frameHeight) = FrameExtra(window);

        window.Height = Math.Max(window.MinHeight, area.Height / scaling - frameHeight);
        var widthPx = (int)Math.Ceiling((window.Width + frameWidth) * scaling);
        window.Position = new PixelPoint(area.Right - widthPx, area.Y);
    }

    /// <summary>Sizes and moves the window so that it fills <paramref name="area"/> (screen pixels), frame included.</summary>
    public static void Fill(Window window, PixelRect area)
    {
        var scaling = window.DesktopScaling;
        var (frameWidth, frameHeight) = FrameExtra(window);

        window.Width = Math.Max(window.MinWidth, area.Width / scaling - frameWidth);
        window.Height = Math.Max(window.MinHeight, area.Height / scaling - frameHeight);
        window.Position = area.Position;
    }

    /// <summary>
    /// Sizes the window to <paramref name="size"/> (client size), limited to <paramref name="maxShare"/> of
    /// <paramref name="area"/> (screen pixels, frame included), and centers it there.
    /// </summary>
    public static void CenterIn(Window window, PixelRect area, Size size, double maxShare)
    {
        var scaling = window.DesktopScaling;
        var (frameWidth, frameHeight) = FrameExtra(window);

        window.Width = Math.Max(window.MinWidth, Math.Min(size.Width, area.Width * maxShare / scaling - frameWidth));
        window.Height = Math.Max(window.MinHeight, Math.Min(size.Height, area.Height * maxShare / scaling - frameHeight));

        var widthPx = (int)Math.Ceiling((window.Width + frameWidth) * scaling);
        var heightPx = (int)Math.Ceiling((window.Height + frameHeight) * scaling);
        window.Position = new PixelPoint(
            area.X + Math.Max(0, (area.Width - widthPx) / 2),
            area.Y + Math.Max(0, (area.Height - heightPx) / 2));
    }

    /// <summary>Identity of <paramref name="screen"/>, saved in screens.yml.</summary>
    public static ScreenInfo Describe(Screen screen) => new()
    {
        Name = screen.DisplayName,
        X = screen.Bounds.X,
        Y = screen.Bounds.Y,
        Width = screen.Bounds.Width,
        Height = screen.Bounds.Height
    };

    /// <summary>
    /// The screen described by <paramref name="info"/>: same name and bounds, else same bounds, else same name; null
    /// when it is no longer connected.
    /// </summary>
    public static Screen? Find(Window window, ScreenInfo? info)
    {
        if (info is null)
            return null;

        var bounds = new PixelRect(info.X, info.Y, info.Width, info.Height);
        var screens = window.Screens.All;
        return screens.FirstOrDefault(s => s.Bounds == bounds && s.DisplayName == info.Name)
               ?? screens.FirstOrDefault(s => s.Bounds == bounds)
               ?? (string.IsNullOrEmpty(info.Name) ? null : screens.FirstOrDefault(s => s.DisplayName == info.Name));
    }

    /// <summary>Place of the window (screen, position, size, maximized), saved in screens.yml.</summary>
    public static WindowPlacement Capture(Window window) => new()
    {
        Screen = window.Screens.ScreenFromWindow(window) is { } screen ? Describe(screen) : null,
        X = window.Position.X,
        Y = window.Position.Y,
        Width = window.Width,
        Height = window.Height,
        Maximized = window.WindowState is WindowState.Maximized or WindowState.FullScreen
    };

    /// <summary>
    /// Puts the window back at <paramref name="placement"/>, kept inside the working area of its screen. Returns false
    /// (nothing changed) when there is no placement or its screen is no longer connected.
    /// </summary>
    public static bool Restore(Window window, WindowPlacement? placement)
    {
        if (placement is null || Find(window, placement.Screen) is not { } screen)
            return false;

        var area = screen.WorkingArea;
        var scaling = screen.Scaling;
        var (frameWidth, frameHeight) = FrameExtra(window);

        window.Width = Math.Max(window.MinWidth, Math.Min(placement.Width, area.Width / scaling - frameWidth));
        window.Height = Math.Max(window.MinHeight, Math.Min(placement.Height, area.Height / scaling - frameHeight));

        var widthPx = (int)Math.Ceiling((window.Width + frameWidth) * scaling);
        var heightPx = (int)Math.Ceiling((window.Height + frameHeight) * scaling);
        window.Position = new PixelPoint(
            Math.Clamp(placement.X, area.X, Math.Max(area.X, area.Right - widthPx)),
            Math.Clamp(placement.Y, area.Y, Math.Max(area.Y, area.Bottom - heightPx)));

        if (placement.Maximized)
            window.WindowState = WindowState.Maximized;
        return true;
    }

    /// <summary>Title bar and borders, which are outside Width/Height, in device-independent pixels.</summary>
    private static (double Width, double Height) FrameExtra(Window window)
    {
        var frame = window.FrameSize ?? new Size(window.Width, window.Height);
        return (Math.Max(0, frame.Width - window.ClientSize.Width), Math.Max(0, frame.Height - window.ClientSize.Height));
    }
}
