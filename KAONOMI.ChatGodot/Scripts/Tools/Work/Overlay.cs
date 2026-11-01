using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Work;

/// <summary>
/// The layer above the content of a window of /work: the flyouts of the ribbon (color, link, save, open), the
/// right-click menu of the tape, the waiting popup. Drawn inside the window itself (each window of the desktop lives in
/// a viewport of its own), so that it follows the window to the front and to the back. A click outside a flyout (in
/// its window, in another window, in the chat), or Escape, closes it.
/// </summary>
public partial class Overlay : Control
{
    private Control? _catcher;
    private Control? _panel;
    private Action? _closed;

    /// <summary>True while a mouse button is down, to see each new click (the one that opened the flyout is ignored).</summary>
    private bool _buttonDown;

    public Overlay()
    {
        // The theme of the chat: an overlay added at the root of a viewport (the text menu, a drop-down) would otherwise
        // get the default theme of Godot, light grey text made for a dark background.
        Theme = Eve.Eve.Shared;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    /// <summary>True while a flyout is shown.</summary>
    public bool IsOpen => _panel is not null && IsInstanceValid(_panel);

    /// <summary>
    /// Shows <paramref name="content"/> in a panel under <paramref name="anchor"/> (or at <paramref name="at"/>, a point
    /// of the overlay); <paramref name="closed"/> runs when it closes.
    /// </summary>
    public PanelContainer Show(Control content, Control? anchor = null, Vector2? at = null, Action? closed = null)
    {
        Close();
        _closed = closed;
        _catcher = new Control { MouseFilter = MouseFilterEnum.Stop };
        _catcher.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _catcher.GuiInput += e =>
        {
            if (e is InputEventMouseButton { Pressed: true })
            {
                _catcher.AcceptEvent();
                Close();
            }
        };
        AddChild(_catcher);

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.PanelSolid, Eve.Eve.EdgeBright with { A = 0.6f }, 10));
        panel.AddChild(content);
        panel.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
            {
                panel.AcceptEvent();
                Close();
            }
        };
        _panel = panel;
        AddChild(panel);
        // The click that opened the flyout may still be held: only the next ones close it.
        _buttonDown = AnyButtonDown();
        Callable.From(() => Place(panel, anchor, at)).CallDeferred();
        return panel;
    }

    private static bool AnyButtonDown() =>
        Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsMouseButtonPressed(MouseButton.Right) || Input.IsMouseButtonPressed(MouseButton.Middle);

    /// <summary>
    /// A click anywhere outside the flyout closes it: the catcher sees only the clicks of this window, a click in
    /// another window or in the chat never reaches it (each window of the desktop is a viewport of its own).
    /// </summary>
    public override void _Process(double delta)
    {
        var down = AnyButtonDown();
        var pressed = down && !_buttonDown;
        _buttonDown = down;
        if (!pressed || _panel is not { } panel || !IsInstanceValid(panel))
            return;
        if (!new Rect2(Vector2.Zero, panel.Size).HasPoint(panel.GetLocalMousePosition()))
            Close();
    }

    private void Place(Control panel, Control? anchor, Vector2? at)
    {
        if (!IsInstanceValid(panel))
            return;
        panel.Size = panel.GetCombinedMinimumSize();
        var position = at ?? (anchor is not null && IsInstanceValid(anchor)
            ? anchor.GlobalPosition - GlobalPosition + new Vector2(0, anchor.Size.Y + 2)
            : (Size - panel.Size) / 2);
        position.X = Math.Clamp(position.X, 4, Math.Max(4, Size.X - panel.Size.X - 4));
        position.Y = Math.Clamp(position.Y, 4, Math.Max(4, Size.Y - panel.Size.Y - 4));
        panel.Position = position;
    }

    /// <summary>Closes the flyout shown (if any).</summary>
    public void Close()
    {
        if (_catcher is not null && IsInstanceValid(_catcher))
            _catcher.QueueFree();
        if (_panel is not null && IsInstanceValid(_panel))
            _panel.QueueFree();
        _catcher = null;
        _panel = null;
        var closed = _closed;
        _closed = null;
        closed?.Invoke();
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (IsOpen && e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            Close();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>A menu item: its text, enabled or not, what it does (the menu closes first); null for a separator.</summary>
    public sealed record MenuItem(string Text, bool Enabled, Action? Action, string Shortcut = "", int Indent = 0);

    /// <summary>A menu of <paramref name="items"/> at <paramref name="at"/> (a point of the overlay); <paramref name="closed"/> runs when it closes.</summary>
    public void Menu(IReadOnlyList<MenuItem?> items, Vector2 at, Action? closed = null)
    {
        var column = new VBoxContainer { CustomMinimumSize = new Vector2(280, 0) };
        column.AddThemeConstantOverride("separation", 0);
        foreach (var item in items)
        {
            if (item is null)
            {
                column.AddChild(Ui.Margin(Eve.Eve.Separator(), 0, 4, 0, 4));
                continue;
            }
            var button = new Button
            {
                Text = new string(' ', item.Indent * 4) + item.Text,
                Flat = true,
                Alignment = HorizontalAlignment.Left,
                Disabled = !item.Enabled,
                FocusMode = FocusModeEnum.None,
                MouseDefaultCursorShape = CursorShape.PointingHand
            };
            button.AddThemeStyleboxOverride("normal", new StyleBoxEmpty { ContentMarginLeft = 8, ContentMarginRight = 8, ContentMarginTop = 4, ContentMarginBottom = 4 });
            if (item.Shortcut.Length > 0)
            {
                var shortcut = Eve.Eve.Text_(item.Shortcut, Eve.Eve.Muted, 11);
                shortcut.SetAnchorsAndOffsetsPreset(LayoutPreset.RightWide);
                shortcut.OffsetLeft = -60;
                shortcut.OffsetRight = -8;
                shortcut.HorizontalAlignment = HorizontalAlignment.Right;
                shortcut.VerticalAlignment = VerticalAlignment.Center;
                shortcut.MouseFilter = MouseFilterEnum.Ignore;
                button.AddChild(shortcut);
            }
            var action = item.Action;
            button.Pressed += () =>
            {
                Close();
                action?.Invoke();
            };
            column.AddChild(button);
        }
        Show(column, at: at, closed: closed);
    }
}

/// <summary>
/// The waiting popup of a long task of /work (the AI review, a spelling correction, a rewriting), in the EVE look: a dim
/// layer over the window, a panel with a ring of light turning around a pulsing ✨, what is being done, an
/// indeterminate bar, the time elapsed and Cancel. Only the end of the task (<see cref="Finish"/>) or Cancel closes it.
/// </summary>
public partial class BusyPopup : Control
{
    private readonly Label _message;
    private readonly Label _elapsed;
    private readonly Button _cancel;
    private readonly CancellationTokenSource? _source;
    private readonly ulong _started = Time.GetTicksMsec();
    private Spinner? _spinner;

    public BusyPopup()
        : this("", "", null)
    {
    }

    public BusyPopup(string heading, string message, CancellationTokenSource? cancel)
    {
        _source = cancel;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var dim = new ColorRect { Color = Eve.Eve.Sunken(0.55f), MouseFilter = MouseFilterEnum.Ignore };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var center = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(380, 0) };
        var box = Eve.Eve.Box(Eve.Eve.PanelSolid, Eve.Eve.Cyan with { A = 0.55f }, 22);
        box.ShadowColor = Eve.Eve.Sunken(0.6f);
        box.ShadowSize = 20;
        panel.AddThemeStyleboxOverride("panel", box);
        center.AddChild(panel);

        _spinner = new Spinner { CustomMinimumSize = new Vector2(72, 72), SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        var title = Eve.Eve.Caption(heading, Eve.Eve.TextBright, 14);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        _message = Eve.Eve.Text_(message, Eve.Eve.Text, 12, wrap: true);
        _message.HorizontalAlignment = HorizontalAlignment.Center;
        _message.CustomMinimumSize = new Vector2(336, 0);
        var bar = new ProgressBar { Indeterminate = true, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 4) };
        _elapsed = Eve.Eve.Text_("0 s", Eve.Eve.Muted, 11);
        _elapsed.HorizontalAlignment = HorizontalAlignment.Center;
        _cancel = Eve.Eve.Button("CANCEL");
        _cancel.SizeFlagsHorizontal = SizeFlags.ShrinkCenter;
        _cancel.Visible = cancel is not null;
        _cancel.Pressed += () =>
        {
            IsCancelledByUser = true;
            _cancel.Disabled = true;
            _cancel.Text = "CANCELLING…";
            _source?.Cancel();
        };
        panel.AddChild(Ui.Column(12, _spinner, title, _message, bar, _elapsed, _cancel));
    }

    /// <summary>The step being done, under the heading.</summary>
    public string Message
    {
        get => _message.Text;
        set => _message.Text = value;
    }

    /// <summary>True once Cancel was clicked.</summary>
    public bool IsCancelledByUser { get; private set; }

    /// <summary>Shows a popup over <paramref name="host"/> (the whole window of the tool).</summary>
    public static BusyPopup Show(Control host, string heading, string message, CancellationTokenSource? cancel)
    {
        var popup = new BusyPopup(heading, message, cancel);
        host.AddChild(popup);
        return popup;
    }

    /// <summary>The task is over: the popup goes.</summary>
    public void Finish()
    {
        if (IsInstanceValid(this))
            QueueFree();
    }

    public override void _Process(double delta)
    {
        var seconds = (int)((Time.GetTicksMsec() - _started) / 1000);
        _elapsed.Text = seconds < 60 ? $"{seconds} s" : $"{seconds / 60} min {seconds % 60:00} s";
    }

    public override void _GuiInput(InputEvent e) => AcceptEvent();

    /// <summary>The ring turning around the pulsing sparkle.</summary>
    private partial class Spinner : Control
    {
        private float _time;

        public override void _Process(double delta)
        {
            _time += (float)delta;
            QueueRedraw();
        }

        public override void _Draw()
        {
            var center = Size / 2;
            var radius = Math.Min(Size.X, Size.Y) / 2 - 4;
            DrawArc(center, radius, 0, Mathf.Tau, 64, Eve.Eve.Edge with { A = 0.5f }, 4, true);
            var start = _time / 1.1f * Mathf.Tau - Mathf.Pi / 2;
            DrawArc(center, radius, start, start + Mathf.DegToRad(100), 32, Eve.Eve.Cyan, 4, true);
            var pulse = 0.5f + 0.5f * Mathf.Sin(_time / 1.4f * Mathf.Tau);
            var size = (int)Math.Round(20 + 7 * pulse);
            var font = Eve.Eve.Emoji;
            var text = "✨";
            var width = font.GetStringSize(text, HorizontalAlignment.Left, -1, size);
            DrawString(font, center - new Vector2(width.X / 2, -font.GetAscent(size) / 2 + 2), text, HorizontalAlignment.Left, -1, size,
                new Color(1, 1, 1, 0.6f + 0.4f * pulse));
        }
    }
}
