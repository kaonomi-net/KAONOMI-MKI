using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace KAONOMI.UI.Desk;

/// <summary>
/// The desktop between the bar of commands and the chat: every tool opens there in a window that moves (its header) and
/// resizes (its corner). Flat, as asked: no 3D cards at the back; a window minimised goes to the task bar at the bottom,
/// a click there brings it back. Ctrl+Tab goes to the next window.
/// </summary>
public sealed class DeskArea : Grid
{
    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly StackPanel _taskbar = new() { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(6, 4) };
    private readonly List<DeskWindow> _windows = [];

    public DeskArea()
    {
        RowDefinitions = new RowDefinitions("*,Auto");
        // Transparent (but hit): the animated background of the theme shows between the windows.
        Background = Brushes.Transparent;
        Children.Add(_canvas);
        var bar = new Border
        {
            Child = new ScrollViewer { Content = _taskbar, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
            Background = Ui.Header,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(0, 1, 0, 0),
            MinHeight = 34
        };
        SetRow(bar, 1);
        Children.Add(bar);
        _canvas.SizeChanged += (_, _) =>
        {
            foreach (var window in _windows.Where(w => w.IsMaximized))
                Place(window, new Rect(_canvas.Bounds.Size));
        };
    }

    /// <summary>The windows, the one in front last.</summary>
    public IReadOnlyList<DeskWindow> Windows => _windows;

    /// <summary>The active window (in front of the others, not minimised), or null.</summary>
    public DeskWindow? Front => _windows.LastOrDefault(w => !w.IsMinimized);

    /// <summary>Raised when a window opens, closes or comes in front.</summary>
    public event Action? Changed;

    /// <summary>Raised with a window whose ❐ was clicked (the same tool in a second window).</summary>
    public event Action<DeskWindow>? CloneRequested;

    public DeskWindow? Find(string key) => _windows.FirstOrDefault(w => w.Key == key);

    /// <summary>Opens <paramref name="content"/> in a new window (or brings back the window of <paramref name="key"/>).</summary>
    public DeskWindow Open(string key, string title, string icon, Control content, Size size, object? viewModel = null)
    {
        if (Find(key) is { } open)
        {
            BringToFront(open);
            return open;
        }
        var window = new DeskWindow(key, title, icon, content, viewModel);
        window.CloseClicked += () => _ = CloseAsync(window);
        window.MinimizeClicked += () => Minimize(window);
        window.MaximizeClicked += () => ToggleMaximize(window);
        window.CloneClicked += () => CloneRequested?.Invoke(window);
        window.Activated += () => BringToFront(window);
        window.Moved += delta => Move(window, delta);
        window.Resized += delta => Resize(window, delta);
        _windows.Add(window);
        _canvas.Children.Add(window);

        // A cascade from the top left, the window kept inside the desktop.
        var area = _canvas.Bounds.Size;
        if (area.Width < 50)
            area = new Size(1000, 700);
        var offset = 24 * (_windows.Count % 8);
        var width = Math.Min(size.Width, Math.Max(320, area.Width - 20));
        var height = Math.Min(size.Height, Math.Max(240, area.Height - 20));
        Place(window, new Rect(Math.Min(20 + offset, Math.Max(0, area.Width - width)), Math.Min(16 + offset, Math.Max(0, area.Height - height)), width, height));

        var task = new Button { Content = $"{icon} {title}", FontSize = 11, Padding = new Thickness(8, 2), MaxWidth = 200 };
        task.Click += (_, _) =>
        {
            if (!window.IsMinimized && ReferenceEquals(Front, window))
                Minimize(window);
            else
                BringToFront(window);
        };
        window.TaskButton = task;
        _taskbar.Children.Add(task);
        BringToFront(window);
        return window;
    }

    public void BringToFront(DeskWindow window)
    {
        window.IsMinimized = false;
        window.IsVisible = true;
        _windows.Remove(window);
        _windows.Add(window);
        for (var i = 0; i < _windows.Count; i++)
        {
            _windows[i].ZIndex = i;
            _windows[i].IsActive = i == _windows.Count - 1;
            _windows[i].TaskButton?.Classes.Set("active", i == _windows.Count - 1);
        }
        Changed?.Invoke();
    }

    public void Minimize(DeskWindow window)
    {
        window.IsMinimized = true;
        window.IsVisible = false;
        window.IsActive = false;
        if (Front is { } next)
            BringToFront(next);
        else
            Changed?.Invoke();
    }

    public void ToggleMaximize(DeskWindow window)
    {
        if (window.IsMaximized)
        {
            window.IsMaximized = false;
            Place(window, window.Restore);
        }
        else
        {
            window.Restore = window.Rect;
            window.IsMaximized = true;
            Place(window, new Rect(_canvas.Bounds.Size));
        }
        BringToFront(window);
    }

    /// <summary>The next window in front (Ctrl+Tab).</summary>
    public void Cycle()
    {
        if (_windows.Count > 1)
            BringToFront(_windows[0]);
        else if (_windows.Count == 1)
            BringToFront(_windows[0]);
    }

    /// <summary>
    /// Closes <paramref name="window"/>; a view model with changes not saved (<c>IUnsavedChanges</c>) asks first. False
    /// when the user kept it.
    /// </summary>
    public async System.Threading.Tasks.Task<bool> CloseAsync(DeskWindow window)
    {
        if (window.ViewModel is KAONOMI.Chat.ViewModels.IUnsavedChanges { IsDirty: true }
            && !await Dialogs.Confirm("Close", $"{window.Title} has changes that are not saved. Close it and lose them?", "Close without saving"))
            return false;
        Close(window);
        return true;
    }

    public void Close(DeskWindow window)
    {
        if (!_windows.Remove(window))
            return;
        _canvas.Children.Remove(window);
        if (window.TaskButton is { } task)
            _taskbar.Children.Remove(task);
        window.RaiseClosed();
        if (Front is { } next)
            BringToFront(next);
        else
            Changed?.Invoke();
    }

    public void CloseAll()
    {
        foreach (var window in _windows.ToList())
            Close(window);
    }

    /// <summary>The windows whose view model has changes not saved.</summary>
    public List<DeskWindow> UnsavedWindows() =>
        _windows.Where(w => w.ViewModel is KAONOMI.Chat.ViewModels.IUnsavedChanges { IsDirty: true }).ToList();

    public void Place(DeskWindow window, Rect rect)
    {
        window.Rect = rect;
        Canvas.SetLeft(window, rect.X);
        Canvas.SetTop(window, rect.Y);
        window.Width = rect.Width;
        window.Height = rect.Height;
    }

    private void Move(DeskWindow window, Vector delta)
    {
        if (window.IsMaximized)
            return;
        var r = window.Rect;
        var area = _canvas.Bounds.Size;
        // The header always stays reachable inside the desktop.
        var x = Math.Clamp(r.X + delta.X, 60 - r.Width, Math.Max(0, area.Width - 60));
        var y = Math.Clamp(r.Y + delta.Y, 0, Math.Max(0, area.Height - 30));
        Place(window, new Rect(x, y, r.Width, r.Height));
    }

    private void Resize(DeskWindow window, Vector delta)
    {
        if (window.IsMaximized)
            return;
        var r = window.Rect;
        Place(window, new Rect(r.X, r.Y, Math.Max(260, r.Width + delta.X), Math.Max(160, r.Height + delta.Y)));
    }
}

/// <summary>A window of the desktop: its header (icon, title, ❐ clone, minimise, maximise, close), its content, its resize corner.</summary>
public sealed class DeskWindow : Border
{
    private readonly Border _header;
    private Point? _drag, _resize;

    public DeskWindow(string key, string title, string icon, Control content, object? viewModel)
    {
        Key = key;
        Title = title;
        Icon = icon;
        Content = content;
        ViewModel = viewModel;
        Background = Ui.Panel;
        BorderBrush = Ui.Edge;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(2);

        var titleText = Ui.Label($"{icon}  {title}", Ui.TextBright, 12, bold: true);
        var buttons = Ui.Row(0,
            HeaderButton("❐", "Clone: the same tool in a second window", () => CloneClicked?.Invoke()),
            HeaderButton("—", "Minimise (to the task bar)", () => MinimizeClicked?.Invoke()),
            HeaderButton("▢", "Maximise / restore (double-click on the header)", () => MaximizeClicked?.Invoke()),
            HeaderButton("✕", "Close", () => CloseClicked?.Invoke()));
        var headerGrid = new DockPanel();
        DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Right);
        headerGrid.Children.Add(buttons);
        headerGrid.Children.Add(titleText);
        _header = new Border
        {
            Child = headerGrid,
            Background = Ui.Header,
            Padding = new Thickness(10, 2, 2, 2),
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = new Cursor(StandardCursorType.SizeAll)
        };
        _header.PointerPressed += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                MaximizeClicked?.Invoke();
                return;
            }
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _drag = e.GetPosition(Parent as Visual);
                e.Pointer.Capture(_header);
            }
        };
        _header.PointerMoved += (_, e) =>
        {
            if (_drag is not { } start)
                return;
            var now = e.GetPosition(Parent as Visual);
            Moved?.Invoke(now - start);
            _drag = now;
        };
        _header.PointerReleased += (_, e) =>
        {
            _drag = null;
            e.Pointer.Capture(null);
        };

        var grip = new Border
        {
            Width = 14,
            Height = 14,
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = new Cursor(StandardCursorType.BottomRightCorner),
            Child = new TextBlock { Text = "◢", FontSize = 9, Foreground = Ui.Muted, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom }
        };
        grip.PointerPressed += (_, e) =>
        {
            _resize = e.GetPosition(Parent as Visual);
            e.Pointer.Capture(grip);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (_resize is not { } start)
                return;
            var now = e.GetPosition(Parent as Visual);
            Resized?.Invoke(now - start);
            _resize = now;
        };
        grip.PointerReleased += (_, e) =>
        {
            _resize = null;
            e.Pointer.Capture(null);
        };

        var body = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        body.Children.Add(_header);
        var holder = new Border { Child = content, ClipToBounds = true };
        Grid.SetRow(holder, 1);
        body.Children.Add(holder);
        var grip0 = grip;
        Grid.SetRow(grip0, 1);
        body.Children.Add(grip0);
        Child = body;
        // A click anywhere in the window brings it in front.
        AddHandler(PointerPressedEvent, (_, _) => Activated?.Invoke(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public string Key { get; }
    public string Title { get; }
    public string Icon { get; }
    public Control Content { get; }
    public object? ViewModel { get; }
    public Rect Rect { get; set; }
    public Rect Restore { get; set; }
    public bool IsMaximized { get; set; }
    public bool IsMinimized { get; set; }
    internal Button? TaskButton { get; set; }

    private bool _isActive;

    /// <summary>The active window: its edge in the accent colour.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            BorderBrush = value ? Ui.Accent : Ui.Edge;
        }
    }

    public event Action? CloseClicked, MinimizeClicked, MaximizeClicked, CloneClicked, Activated, Closed;
    public event Action<Vector>? Moved, Resized;

    internal void RaiseClosed() => Closed?.Invoke();

    private static Button HeaderButton(string text, string tooltip, Action click)
    {
        var button = new Button { Content = text, Background = Brushes.Transparent, Padding = new Thickness(8, 1), FontSize = 12, MinHeight = 0, Focusable = false };
        ToolTip.SetTip(button, tooltip);
        button.Click += (_, e) =>
        {
            e.Handled = true;
            click();
        };
        return button;
    }
}
