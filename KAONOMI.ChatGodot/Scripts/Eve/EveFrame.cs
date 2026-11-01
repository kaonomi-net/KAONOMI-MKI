using System;
using Godot;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The frame of a window of the desktop, in the style of EVE Online: a header with the icon and the title in small
/// spaced capitals, a button sending the window to the back and a close button, the content below, a grip at the bottom
/// right corner to resize it. Dragging the header moves the window. It lives in the viewport of its window
/// (<see cref="DeckWindow"/>), which draws it in front or on its card in 3D.
/// </summary>
public partial class EveFrame : PanelContainer
{
    private readonly Label _title;
    private readonly Label _icon;
    private bool _dragging, _resizing;

    /// <summary>The header was dragged by this many pixels.</summary>
    public event Action<Vector2>? Moved;

    /// <summary>The grip was dragged by this many pixels.</summary>
    public event Action<Vector2>? Resized;

    /// <summary>Close was clicked.</summary>
    public event Action? CloseClicked;

    /// <summary>"Send to the back" was clicked.</summary>
    public event Action? BackClicked;

    /// <summary>"Add a note" was clicked.</summary>
    public event Action? NoteClicked;

    /// <summary>📁+ clicked: the window, with its content, goes into a folder.</summary>
    public event Action? FolderClicked;

    /// <summary>❐ was clicked: the window is cloned (the same tool in a second window).</summary>
    public event Action? CloneClicked;

    private Button _clone = null!;

    /// <summary>Whether ❐ is shown (a window that cannot be cloned hides it).</summary>
    public bool CanClone
    {
        get => _clone.Visible;
        set => _clone.Visible = value;
    }

    /// <summary>"Full size" was clicked (the whole desktop, or back to the size before).</summary>
    public event Action? MaximizeClicked;

    private Button _maximize = null!;

    /// <summary>The header was double-clicked (the window takes the whole desktop, or its size before).</summary>
    public event Action? HeaderDoubleClicked;

    /// <summary>"Behind, faded" was clicked.</summary>
    public event Action? FadeClicked;

    /// <summary>An edge or a corner was dragged: the window moves by the first vector and grows by the second.</summary>
    public event Action<Vector2, Vector2>? Edged;

    public EveFrame(string title, string icon, Control content)
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // A viewport does not pass the theme of the chat on: the frame takes it itself.
        Theme = Eve.Shared;
        AddThemeStyleboxOverride("panel", Eve.WindowBox(true));
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 0);
        AddChild(column);

        // Header.
        var header = new PanelContainer { MouseDefaultCursorShape = CursorShape.Move };
        var headerBox = Eve.Box(Eve.Header, Eve.Edge with { A = 0f }, 0, 0);
        headerBox.BorderWidthBottom = 1;
        headerBox.BorderColor = Eve.Edge with { A = 0.6f };
        headerBox.ContentMarginLeft = 10;
        headerBox.ContentMarginRight = 4;
        headerBox.ContentMarginTop = headerBox.ContentMarginBottom = 3;
        header.AddThemeStyleboxOverride("panel", headerBox);
        column.AddChild(header);
        var bar = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        bar.AddThemeConstantOverride("separation", 8);
        header.AddChild(bar);
        _icon = new Label { Text = icon, MouseFilter = MouseFilterEnum.Ignore };
        _icon.AddThemeFontSizeOverride("font_size", 14);
        bar.AddChild(_icon);
        _title = Eve.Caption(title, Eve.TextBright, 12);
        _title.MouseFilter = MouseFilterEnum.Ignore;
        _title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.TooltipText = "Drag: move · double-click: full size";
        _title.ClipText = true;
        bar.AddChild(_title);
        _bar = bar;
        _clone = HeaderButton("❐", "Clone: the same tool in a second window, beside this one (a window of an object shows the same object)", () => CloneClicked?.Invoke());
        bar.AddChild(_clone);
        bar.AddChild(HeaderButton("📝", "Add a note about this window (sent to the server with its data)", () => NoteClicked?.Invoke()));
        bar.AddChild(FolderButton());
        bar.AddChild(HeaderButton("◐", "Behind the other windows, faded and smaller (a click on it brings it back)", () => FadeClicked?.Invoke()));
        bar.AddChild(HeaderButton("▁", "Send to the back (it stays open, in 3D behind)", () => BackClicked?.Invoke()));
        _maximize = HeaderButton("□", "Full size: the whole desktop, between the bar of commands and the chat", () => MaximizeClicked?.Invoke());
        bar.AddChild(_maximize);
        bar.AddChild(HeaderButton("✕", "Close", () => CloseClicked?.Invoke()));
        header.GuiInput += OnHeaderInput;

        // A thin cyan line under the header, as on the windows of EVE.
        var accent = new ColorRect { Color = Eve.Cyan with { A = 0.5f }, CustomMinimumSize = new Vector2(0, 1), MouseFilter = MouseFilterEnum.Ignore };
        column.AddChild(accent);

        var margin = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        foreach (var side in new[] { "left", "right", "top", "bottom" })
            margin.AddThemeConstantOverride($"margin_{side}", 10);
        content.SizeFlagsVertical = SizeFlags.ExpandFill;
        content.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        // A content larger than its window (a small screen) scrolls instead of being cut.
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(content);
        margin.AddChild(scroll);
        column.AddChild(margin);

        // Resize grip at the bottom right, and every edge and corner (above the content).
        var grip = new Grip();
        grip.GuiInput += OnGripInput;
        AddChild(grip);
        var edges = new Control { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(edges);
        foreach (var (horizontal, vertical) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, -1), (-1, 1), (1, 1) })
            edges.AddChild(new Edge(horizontal, vertical, (move, grow) => Edged?.Invoke(move, grow)));
    }

    /// <summary>Shows whether the window takes the whole desktop (❐: back to its size) or not (□: full size).</summary>
    public void SetMaximized(bool maximized)
    {
        _maximize.Text = maximized ? "❐" : "□";
        _maximize.TooltipText = maximized
            ? "Back to the size it had"
            : "Full size: the whole desktop, between the bar of commands and the chat";
    }

    private HBoxContainer _bar = null!;

    /// <summary>Adds a button of the window to its header, before the standard ones (❐ 📝 ◐ ▁ □ ✕).</summary>
    public Button AddHeaderButton(string text, string tooltip, Action pressed)
    {
        var button = HeaderButton(text, tooltip, pressed);
        _bar.AddChild(button);
        // Right after the title: before 📝 and the other standard buttons.
        _bar.MoveChild(button, _bar.GetChildCount() - 7);
        return button;
    }

    /// <summary>The title shown in the header.</summary>
    public string Title
    {
        get => _title.Text;
        set => _title.Text = value.ToUpperInvariant();
    }

    /// <summary>📁 with a + laid on it, at the bottom right: adds the window to a folder.</summary>
    private Button FolderButton()
    {
        var button = HeaderButton("📁", "Add this window, with its content, to a folder (an existing one, or a new one)", () => FolderClicked?.Invoke());
        var plus = new Label { Text = "+", MouseFilter = MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        plus.AddThemeFontSizeOverride("font_size", 14);
        plus.AddThemeColorOverride("font_color", Eve.Cyan);
        plus.AddThemeColorOverride("font_outline_color", Eve.Header);
        plus.AddThemeConstantOverride("outline_size", 4);
        plus.SetAnchorsPreset(LayoutPreset.BottomRight);
        plus.OffsetLeft = -14;
        plus.OffsetTop = -21;
        plus.OffsetRight = -2;
        plus.OffsetBottom = -5;
        button.AddChild(plus);
        return button;
    }

    private static Button HeaderButton(string text, string tooltip, Action pressed)
    {
        var button = new Button { Text = text, TooltipText = tooltip, Flat = true, CustomMinimumSize = new Vector2(24, 20), MouseDefaultCursorShape = CursorShape.PointingHand, FocusMode = FocusModeEnum.None };
        button.AddThemeFontSizeOverride("font_size", 11);
        button.AddThemeColorOverride("font_color", Eve.Muted);
        button.AddThemeColorOverride("font_hover_color", Eve.TextBright);
        button.Pressed += pressed;
        return button;
    }

    private void OnHeaderInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, DoubleClick: true, Pressed: true }:
                HeaderDoubleClicked?.Invoke();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                _dragging = button.Pressed;
                break;
            case InputEventMouseMotion motion when _dragging:
                Moved?.Invoke(motion.Relative);
                break;
        }
    }

    private void OnGripInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                _resizing = button.Pressed;
                break;
            case InputEventMouseMotion motion when _resizing:
                Resized?.Invoke(motion.Relative);
                break;
        }
    }

    /// <summary>
    /// A thin invisible band along an edge (or a square at a corner) of the window: dragging it resizes the window from
    /// that side. <c>horizontal</c> / <c>vertical</c>: -1 left / top, 1 right / bottom, 0 not on that axis.
    /// </summary>
    private partial class Edge : Control
    {
        // Thin bands (8 px at the sides and the bottom, 14 px squares at the corners), inside the margin around the
        // content (10): the edge resizes the window and the content keeps all of its area. At the top, thinner still: the
        // header and its buttons (full size, close) stay under the pointer.
        private const float Thickness = 8;
        private const float Corner = 14;
        private const float TopThickness = 5;
        private const float TopCorner = 10;
        // The bands of the left and right sides start under the header (its buttons are near its right end).
        private const float HeaderHeight = 36;
        private readonly int _h, _v;
        private readonly Action<Vector2, Vector2> _dragged;
        private bool _dragging;

        public Edge() : this(1, 1, (_, _) => { })
        {
        }

        public Edge(int horizontal, int vertical, Action<Vector2, Vector2> dragged)
        {
            _h = horizontal;
            _v = vertical;
            _dragged = dragged;
            MouseFilter = MouseFilterEnum.Stop;
            MouseDefaultCursorShape = (horizontal, vertical) switch
            {
                (0, _) => CursorShape.Vsize,
                (_, 0) => CursorShape.Hsize,
                (-1, -1) or (1, 1) => CursorShape.Fdiagsize,
                _ => CursorShape.Bdiagsize
            };
            // Anchored to its side of the parent (the whole window).
            AnchorLeft = horizontal == 1 ? 1 : 0;
            AnchorRight = horizontal == -1 ? 0 : 1;
            AnchorTop = vertical == 1 ? 1 : 0;
            AnchorBottom = vertical == -1 ? 0 : 1;
            // Width across the edge (x for the left / right sides, y for the top / bottom ones) and along it.
            var top = vertical == -1;
            var across = horizontal != 0 && vertical != 0 ? (top ? TopCorner : Corner) : top ? TopThickness : Thickness;
            OffsetLeft = horizontal == 1 ? -across : horizontal == 0 ? Corner : 0;
            OffsetRight = horizontal == -1 ? across : horizontal == 0 ? -Corner : 0;
            OffsetTop = vertical == 1 ? -across : vertical == 0 ? HeaderHeight : 0;
            OffsetBottom = vertical == -1 ? across : vertical == 0 ? -Corner : 0;
        }

        public override void _GuiInput(InputEvent e)
        {
            switch (e)
            {
                case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                    _dragging = button.Pressed;
                    AcceptEvent();
                    break;
                case InputEventMouseMotion motion when _dragging:
                    var d = motion.Relative;
                    var move = new Vector2(_h == -1 ? d.X : 0, _v == -1 ? d.Y : 0);
                    var grow = new Vector2(_h * d.X, _v * d.Y);
                    _dragged(move, grow);
                    AcceptEvent();
                    break;
            }
        }
    }

    /// <summary>The corner grip: three small diagonal ticks.</summary>
    private partial class Grip : Control
    {
        public Grip()
        {
            CustomMinimumSize = new Vector2(16, 16);
            SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
            SizeFlagsVertical = SizeFlags.ShrinkEnd;
            MouseDefaultCursorShape = CursorShape.Fdiagsize;
        }

        public override void _Draw()
        {
            var color = Eve.Edge with { A = 0.9f };
            for (var i = 1; i <= 3; i++)
                DrawLine(new Vector2(16 - i * 4, 15), new Vector2(15, 16 - i * 4), color, 1);
        }
    }
}
