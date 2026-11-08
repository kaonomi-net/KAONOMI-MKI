using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Work;

/// <summary>
/// The flyouts of a window of /work: the panels of the ribbon (color, link, save, open) under the tool that opened
/// them, and the right-click menu of the tape. A click outside a flyout, or Escape, closes it (the light dismiss of the
/// flyouts of Avalonia).
/// </summary>
public sealed class Overlay
{
    private Flyout? _flyout;
    private Action? _closed;

    /// <summary>True while a flyout is shown.</summary>
    public bool IsOpen => _flyout is { IsOpen: true };

    /// <summary>
    /// Shows <paramref name="content"/> in a panel under <paramref name="anchor"/>; <paramref name="closed"/> runs when it
    /// closes (by <see cref="Close"/>, a click outside or Escape).
    /// </summary>
    public void Show(Control content, Control anchor, Action? closed = null)
    {
        Close();
        _closed = closed;
        var flyout = new Flyout
        {
            Content = new Border { Child = content, Padding = new Thickness(10), Background = Ui.Panel },
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            ShowMode = FlyoutShowMode.Standard
        };
        flyout.Closed += (_, _) =>
        {
            if (!ReferenceEquals(_flyout, flyout))
                return;
            _flyout = null;
            var done = _closed;
            _closed = null;
            done?.Invoke();
        };
        _flyout = flyout;
        flyout.ShowAt(anchor);
    }

    /// <summary>Closes the flyout shown (if any).</summary>
    public void Close()
    {
        if (_flyout is { } flyout)
            flyout.Hide();
    }

    /// <summary>A menu item: its text, enabled or not, what it does (the menu closes first); null for a separator.</summary>
    public sealed record MenuItem(string Text, bool Enabled, Action? Action, string Shortcut = "", int Indent = 0);

    /// <summary>A menu of <paramref name="items"/> at the pointer, over <paramref name="target"/>.</summary>
    public static void Menu(IReadOnlyList<MenuItem?> items, Control target)
    {
        var flyout = new MenuFlyout();
        foreach (var item in items)
        {
            if (item is null)
            {
                flyout.Items.Add(new Separator());
                continue;
            }
            var header = new DockPanel { MinWidth = 260 };
            if (item.Shortcut.Length > 0)
            {
                var shortcut = Ui.Label(item.Shortcut, Ui.Muted, 11);
                shortcut.Margin = new Thickness(16, 0, 0, 0);
                DockPanel.SetDock(shortcut, Avalonia.Controls.Dock.Right);
                header.Children.Add(shortcut);
            }
            header.Children.Add(new TextBlock { Text = new string(' ', item.Indent * 4) + item.Text, VerticalAlignment = VerticalAlignment.Center });
            var menuItem = new Avalonia.Controls.MenuItem { Header = header, IsEnabled = item.Enabled };
            var action = item.Action;
            menuItem.Click += (_, _) => action?.Invoke();
            flyout.Items.Add(menuItem);
        }
        flyout.ShowAt(target, true);
    }
}

/// <summary>
/// The waiting popup of a long task of /work (the AI review, a spelling correction, a rewriting): a layer over the
/// window that keeps the clicks off it, a plain panel with what is being done, the time elapsed and Cancel. Only the end
/// of the task (<see cref="Finish"/>) or Cancel closes it.
/// </summary>
public sealed class BusyPopup : Border
{
    private readonly TextBlock _message;
    private readonly TextBlock _elapsed;
    private readonly Button _cancel;
    private readonly CancellationTokenSource? _source;
    private readonly DateTime _started = DateTime.UtcNow;
    private readonly DispatcherTimer _clock;
    private readonly Panel _host;

    private BusyPopup(Panel host, string heading, string message, CancellationTokenSource? cancel)
    {
        _host = host;
        _source = cancel;
        // The layer catches the pointer (nothing under it can be clicked) without hiding the window.
        Background = Brushes.Transparent;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        var title = Ui.Label(heading.ToUpperInvariant(), Ui.TextBright, 14, bold: true);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        _message = Ui.Label(message, Ui.Text, 12, wrap: true);
        _message.TextAlignment = TextAlignment.Center;
        _message.Width = 336;
        _elapsed = Ui.Label("0 s", Ui.Muted, 11);
        _elapsed.HorizontalAlignment = HorizontalAlignment.Center;
        _cancel = Ui.Button("CANCEL");
        _cancel.HorizontalAlignment = HorizontalAlignment.Center;
        _cancel.IsVisible = cancel is not null;
        _cancel.Click += (_, _) =>
        {
            IsCancelledByUser = true;
            _cancel.IsEnabled = false;
            _cancel.Content = "CANCELLING…";
            _source?.Cancel();
        };
        var glyph = Ui.Label("✨", Ui.Accent, 26);
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        Child = new Border
        {
            Width = 380,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Ui.Panel,
            BorderBrush = Ui.Accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(22),
            Child = Ui.Column(12, glyph, title, _message, _elapsed, _cancel)
        };

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) =>
        {
            var seconds = (int)(DateTime.UtcNow - _started).TotalSeconds;
            _elapsed.Text = seconds < 60 ? $"{seconds} s" : $"{seconds / 60} min {seconds % 60:00} s";
        };
        _clock.Start();
        // The keys go nowhere else while it is shown.
        AddHandler(KeyDownEvent, (_, e) => e.Handled = true, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        PointerPressed += (_, e) => e.Handled = true;
    }

    /// <summary>The step being done, under the heading.</summary>
    public string Message
    {
        get => _message.Text ?? string.Empty;
        set => _message.Text = value;
    }

    /// <summary>True once Cancel was clicked.</summary>
    public bool IsCancelledByUser { get; private set; }

    /// <summary>Shows a popup over <paramref name="host"/> (the whole window of the tool).</summary>
    public static BusyPopup Show(Panel host, string heading, string message, CancellationTokenSource? cancel)
    {
        var popup = new BusyPopup(host, heading, message, cancel);
        host.Children.Add(popup);
        return popup;
    }

    /// <summary>The task is over: the popup goes.</summary>
    public void Finish()
    {
        _clock.Stop();
        _host.Children.Remove(this);
    }
}
