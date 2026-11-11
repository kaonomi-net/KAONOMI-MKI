using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace KAONOMI.Chat.Views;

/// <summary>
/// Keeps the main chat window fixed and always visible: it stays on top, at its docked position (moves are undone)
/// and in the normal state; every other window is kept clear of it (moved to its left, and narrowed if needed).
/// The other windows have no owner and are not on top: they stack normally, except dialogs owned by the chat.
/// </summary>
internal static class WindowGuard
{
    private static Window? _main;
    private static PixelPoint? _pinnedPosition;
    private static bool _adjusting;

    /// <summary>Protects <paramref name="main"/> and starts tracking every window opened afterwards.</summary>
    public static void Install(Window main)
    {
        _main = main;
        main.Topmost = true;

        // Moves (drag, Windows snap shortcuts) are undone; maximize / full screen go back to normal.
        main.PositionChanged += (_, _) =>
        {
            if (_pinnedPosition is { } pinned && main.Position != pinned && main.WindowState == WindowState.Normal)
                main.Position = pinned;
        };
        main.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty
                && main.WindowState is WindowState.Maximized or WindowState.FullScreen)
                main.WindowState = WindowState.Normal;
        };

        // Global tracking of the other windows (tool windows, dialogs), including future ones.
        Window.WindowOpenedEvent.AddClassHandler(typeof(Window), OnWindowOpened);
    }

    /// <summary>Records the main window's docked position: any later move is undone.</summary>
    public static void Pin(Window main) => _pinnedPosition = main.Position;

    /// <summary>Lets the main window be moved (re-docked) until the next <see cref="Pin"/>.</summary>
    public static void Unpin() => _pinnedPosition = null;

    private static void OnWindowOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not Window window || ReferenceEquals(window, _main))
            return;

        // The systray menu (Avalonia TrayPopupRoot, a Window) opens next to its icon, over the chat strip: left alone.
        if (window.GetType().Name == "TrayPopupRoot")
            return;

        // A dialog owned by the always-on-top chat (evaluation, note…) must be on top as well, otherwise it opens behind
        // its owner and cannot be reached. Owner is not set yet at this point, hence the check once the window is shown.
        Dispatcher.UIThread.Post(() =>
        {
            if (ReferenceEquals(window.Owner, _main))
            {
                window.Topmost = true;
                window.Activate();
            }
        });

        KeepClear(window);
        window.PositionChanged += OnTrackedWindowChanged;
        window.Resized += OnTrackedWindowChanged;
        window.Closed += (_, _) =>
        {
            window.PositionChanged -= OnTrackedWindowChanged;
            window.Resized -= OnTrackedWindowChanged;
        };
    }

    private static void OnTrackedWindowChanged(object? sender, EventArgs e)
    {
        if (sender is Window window)
            KeepClear(window);
    }

    /// <summary>
    /// Moves <paramref name="window"/> out of the main window (to the free side of the main window's screen, the left
    /// one for the docked chat), narrowing it when it is wider than that free space. Windows on other screens are left alone.
    /// </summary>
    public static void KeepClear(Window window)
    {
        if (_adjusting || _main is not { IsVisible: true } main || ReferenceEquals(window, main)
            || window.WindowState != WindowState.Normal || main.WindowState != WindowState.Normal)
            return;

        var mainRect = FrameRect(main);
        var childRect = FrameRect(window);
        if (!mainRect.Intersects(childRect) || main.Screens.ScreenFromWindow(main) is not { } screen)
            return;

        _adjusting = true;
        try
        {
            var area = screen.WorkingArea;
            var free = FreeArea(area, mainRect);

            // Narrow / shorten the window when it does not fit (never below its minimum size).
            var scaling = window.DesktopScaling;
            var frame = window.FrameSize ?? window.ClientSize;
            var extraWidth = Math.Max(0, frame.Width - window.ClientSize.Width);
            var extraHeight = Math.Max(0, frame.Height - window.ClientSize.Height);
            if (childRect.Width > free.Width)
                window.Width = Math.Max(window.MinWidth, free.Width / scaling - extraWidth);
            if (childRect.Height > free.Height)
                window.Height = Math.Max(window.MinHeight, free.Height / scaling - extraHeight);

            var width = (int)Math.Ceiling((window.Width + extraWidth) * scaling);
            var height = (int)Math.Ceiling((window.Height + extraHeight) * scaling);
            var x = Math.Clamp(childRect.X, free.X, Math.Max(free.X, free.Right - width));
            var y = Math.Clamp(childRect.Y, area.Y, Math.Max(area.Y, area.Bottom - height));
            window.Position = new PixelPoint(x, y);
        }
        finally
        {
            _adjusting = false;
        }
    }

    /// <summary>
    /// Free space of the chat's screen next to the docked chat (its widest side), in screen pixels; the whole working
    /// area of that screen when the docked chat is hidden (systray, or full chat open); null before the chat is set up.
    /// The <see cref="ReservedZone"/> (full chat docked in a zone) is left out.
    /// </summary>
    public static PixelRect? FreeArea()
    {
        if (_main is not { } main || (main.Screens.ScreenFromWindow(main) ?? ScreenPlacement.LargestScreen(main)) is not { } screen)
            return null;
        var free = main.IsVisible ? FreeArea(screen.WorkingArea, FrameRect(main)) : screen.WorkingArea;
        if (ReservedZone is not { } zone || !zone.Intersects(free))
            return free;
        // The zone is a whole-height column of the free space: what is left is its widest side.
        var left = Math.Clamp(zone.X, free.X, free.Right) - free.X;
        var right = free.Right - Math.Clamp(zone.Right, free.X, free.Right);
        return left >= right
            ? new PixelRect(free.X, free.Y, left, free.Height)
            : new PixelRect(free.Right - right, free.Y, right, free.Height);
    }

    /// <summary>
    /// Zone of the free space taken by the full chat when it is docked there (no vertical screen, <c>/fullchat</c>):
    /// the workspace keeps clear of it; null otherwise.
    /// </summary>
    public static PixelRect? ReservedZone { get; set; }

    /// <summary>Widest side of <paramref name="area"/> left by <paramref name="mainRect"/> (it may already be outside it, Windows AppBar).</summary>
    private static PixelRect FreeArea(PixelRect area, PixelRect mainRect)
    {
        var leftSpace = Math.Clamp(mainRect.X, area.X, area.Right) - area.X;
        var rightSpace = area.Right - Math.Clamp(mainRect.Right, area.X, area.Right);
        return leftSpace >= rightSpace
            ? new PixelRect(area.X, area.Y, leftSpace, area.Height)
            : new PixelRect(area.Right - rightSpace, area.Y, rightSpace, area.Height);
    }

    /// <summary>Window bounds including the title bar and borders, in screen pixels.</summary>
    private static PixelRect FrameRect(Window window)
    {
        var size = window.FrameSize ?? window.ClientSize;
        return new PixelRect(window.Position, PixelSize.FromSize(size, window.DesktopScaling));
    }
}
