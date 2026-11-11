using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.Views;

/// <summary>
/// Own window of a tool view (<c>/n</c> commands: <c>/ncache</c>, <c>/nlog</c>…), outside the workspace tabs. Sized
/// for its view and centered in the free space next to the chat, it behaves as a normal window (not on top).
/// </summary>
public sealed class ToolWindow : Window
{
    /// <summary>Share of the free space next to the chat a tool window may take: it stays smaller than the workspace.</summary>
    private const double MaxShareOfFreeArea = 0.85;

    private readonly Size _preferredSize;
    private readonly WindowPlacement? _restorePlacement;

    /// <summary>
    /// Hosts <paramref name="view"/> (its view model already set) of the tool <paramref name="key"/> under
    /// <paramref name="title"/>; <paramref name="restorePlacement"/> is the place saved in screens.yml, if any.
    /// </summary>
    public ToolWindow(string key, Control view, string title, Size preferredSize, WindowPlacement? restorePlacement = null)
    {
        Key = key;
        _preferredSize = preferredSize;
        _restorePlacement = restorePlacement;
        Title = $"Kaonomi · {title}";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://KAONOMI.Chat/Assets/kaonomi-logo.ico")));
        MinWidth = 420;
        MinHeight = 300;
        Width = preferredSize.Width;
        Height = preferredSize.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = view;
    }

    /// <summary>Tool shown (<c>cache</c>, <c>logDashboard</c>…).</summary>
    public string Key { get; }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!ScreenPlacement.Restore(this, _restorePlacement) && WindowGuard.FreeArea() is { } free)
            ScreenPlacement.CenterIn(this, free, _preferredSize, MaxShareOfFreeArea);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        // Releases the view model (refresh loops, performance counters).
        ((Content as Control)?.DataContext as IDisposable)?.Dispose();
        base.OnClosed(e);
    }
}
