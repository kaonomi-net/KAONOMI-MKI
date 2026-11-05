using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.ChatGodot.Tools.Games;

/// <summary>
/// The board of /game:chess, White at the bottom. A click on a white piece shows its legal moves (a dot, a ring on a
/// capture), a click on one of them plays it; a piece can be dragged too. The last move is lit, a king in check
/// glows red, a pawn reaching the last rank asks for its piece. The pieces are the chess symbols of the system font,
/// filled, white with a dark outline or black with a light one.
/// </summary>
public sealed partial class ChessBoard : Control
{
    private static readonly Color Light = new(0.93f, 0.86f, 0.73f);
    private static readonly Color Dark = new(0.70f, 0.53f, 0.39f);
    private static readonly Color LastMoveTint = new(0.96f, 0.84f, 0.25f, 0.42f);
    private static readonly Color SelectedTint = new(0.30f, 0.68f, 0.90f, 0.55f);
    private static readonly Color HintColor = new(0.10f, 0.12f, 0.14f, 0.32f);

    /// <summary>A font with the chess symbols (U+2654…U+265F), as text and not as emoji.</summary>
    public static readonly SystemFont PieceFont = new()
    {
        FontNames = ["Arial Unicode MS", "Segoe UI Symbol", "DejaVu Sans", "Noto Sans Symbols 2", "Apple Symbols"],
        Antialiasing = TextServer.FontAntialiasing.Gray
    };

    private readonly ChessWindowViewModel _vm;
    private int _selected = -1;
    private IReadOnlyList<ChessMove> _targets = [];
    private int _dragFrom = -1;
    private Vector2 _dragAt;
    private bool _dragging;

    public ChessBoard(ChessWindowViewModel vm)
    {
        _vm = vm;
        FocusMode = FocusModeEnum.Click;
        MouseFilter = MouseFilterEnum.Stop;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(360, 360);
        Action changed = () => Eve.Bind.OnMain(() =>
        {
            Deselect();
            QueueRedraw();
        });
        PropertyChangedEventHandler property = (_, e) =>
        {
            if (e.PropertyName is nameof(ChessWindowViewModel.IsThinking) or nameof(ChessWindowViewModel.CanMove))
                Eve.Bind.OnMain(QueueRedraw);
        };
        _vm.Changed += changed;
        _vm.PropertyChanged += property;
        TreeExiting += () =>
        {
            _vm.Changed -= changed;
            _vm.PropertyChanged -= property;
        };
    }

    /// <summary>The filled symbol of a kind of piece (the same for both sides: the colour is drawn), shown as text.</summary>
    public static string Glyph(ChessPiece kind) => kind switch
    {
        ChessPiece.King => "♚︎",
        ChessPiece.Queen => "♛︎",
        ChessPiece.Rook => "♜︎",
        ChessPiece.Bishop => "♝︎",
        ChessPiece.Knight => "♞︎",
        ChessPiece.Pawn => "♟︎",
        _ => string.Empty
    };

    /// <summary>The hollow symbol of a kind of piece (♔ ♕ ♖ ♗ ♘ ♙), shown as text.</summary>
    public static string HollowGlyph(ChessPiece kind) => kind switch
    {
        ChessPiece.King => "\u2654\uFE0E",
        ChessPiece.Queen => "\u2655\uFE0E",
        ChessPiece.Rook => "\u2656\uFE0E",
        ChessPiece.Bishop => "\u2657\uFE0E",
        ChessPiece.Knight => "\u2658\uFE0E",
        ChessPiece.Pawn => "\u2659\uFE0E",
        _ => string.Empty
    };

    // ── Geometry ──

    private float Cell => Math.Max(8, Math.Min(Size.X, Size.Y) / 8f);

    private Vector2 Origin => new((Size.X - Cell * 8) / 2, (Size.Y - Cell * 8) / 2);

    private Rect2 SquareRect(int square) =>
        new(Origin + new Vector2(ChessPosition.File(square) * Cell, (7 - ChessPosition.Rank(square)) * Cell), new Vector2(Cell, Cell));

    private int SquareAt(Vector2 at)
    {
        var local = (at - Origin) / Cell;
        if (local.X < 0 || local.Y < 0 || local.X >= 8 || local.Y >= 8)
            return -1;
        return ChessPosition.SquareOf((int)local.X, 7 - (int)local.Y);
    }

    // ── Drawing ──

    public override void _Draw()
    {
        var position = _vm.Game.Current;
        var cell = Cell;
        var coordinates = Mathf.Max(9, (int)(cell * 0.17f));
        var font = Eve.Eve.Body;
        for (var square = 0; square < 64; square++)
        {
            var rect = SquareRect(square);
            var light = (ChessPosition.File(square) + ChessPosition.Rank(square)) % 2 == 1;
            DrawRect(rect, light ? Light : Dark);
            if (_vm.LastMove is { } last && (last.From == square || last.To == square))
                DrawRect(rect, LastMoveTint);
            if (square == _selected)
                DrawRect(rect, SelectedTint);
            // The coordinates in the corners, as on a printed board: the files on the first rank, the ranks on file a.
            var ink = light ? Dark : Light;
            if (ChessPosition.Rank(square) == 0)
                DrawString(font, rect.Position + new Vector2(cell - coordinates * 0.75f, cell - coordinates * 0.3f),
                    ((char)('a' + ChessPosition.File(square))).ToString(), HorizontalAlignment.Left, -1, coordinates, ink);
            if (ChessPosition.File(square) == 0)
                DrawString(font, rect.Position + new Vector2(coordinates * 0.3f, coordinates * 1.05f),
                    ((char)('1' + ChessPosition.Rank(square))).ToString(), HorizontalAlignment.Left, -1, coordinates, ink);
        }

        // The king in check glows red.
        if (position.InCheck)
        {
            var king = SquareRect(position.KingSquare(position.WhiteToMove));
            for (var k = 6; k >= 1; k--)
                DrawCircle(king.GetCenter(), cell * 0.08f * k, new Color(0.95f, 0.1f, 0.1f, 0.13f));
        }

        // The legal moves of the piece chosen.
        foreach (var target in _targets.Select(m => m.To).Distinct())
        {
            var rect = SquareRect(target);
            if (position.IsCapture(_targets.First(m => m.To == target)))
                DrawArc(rect.GetCenter(), cell * 0.44f, 0, Mathf.Tau, 40, HintColor, cell * 0.08f, true);
            else
                DrawCircle(rect.GetCenter(), cell * 0.15f, HintColor);
        }

        for (var square = 0; square < 64; square++)
        {
            var piece = position[square];
            if (piece == 0 || (_dragging && square == _dragFrom))
                continue;
            DrawPiece(piece, SquareRect(square).GetCenter(), cell);
        }
        if (_dragging && _dragFrom >= 0 && position[_dragFrom] != 0)
            DrawPiece(position[_dragFrom], _dragAt, cell * 1.08f);

        if (_vm.Game.IsOver)
            DrawBanner(_vm.Game.Reason, _vm.Game.Result);
    }

    private void DrawPiece(sbyte piece, Vector2 center, float cell)
    {
        var size = (int)(cell * 0.82f);
        var text = Glyph(ChessPosition.Kind(piece));
        var width = PieceFont.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        var baseline = center + new Vector2(-width / 2, (PieceFont.GetAscent(size) - PieceFont.GetDescent(size)) / 2);
        var outline = Math.Max(2, (int)(cell * 0.06f));
        if (piece > 0)
        {
            // White: a white body, its lines and rim drawn by a dark outline.
            DrawStringOutline(PieceFont, baseline, text, HorizontalAlignment.Left, -1, size, outline, new Color(0.08f, 0.08f, 0.09f));
            DrawString(PieceFont, baseline, text, HorizontalAlignment.Left, -1, size, new Color(0.99f, 0.98f, 0.95f));
            return;
        }
        // Black: a solid black body (the outline fills the hollows of the symbol), then the lines of the hollow symbol
        // in light grey on top, as on a printed diagram.
        var ink = new Color(0.11f, 0.10f, 0.11f);
        DrawStringOutline(PieceFont, baseline, text, HorizontalAlignment.Left, -1, size, outline, ink);
        DrawString(PieceFont, baseline, text, HorizontalAlignment.Left, -1, size, ink);
        DrawString(PieceFont, baseline, HollowGlyph(ChessPosition.Kind(piece)), HorizontalAlignment.Left, -1, size, new Color(0.86f, 0.84f, 0.80f, 0.75f));
    }

    private void DrawBanner(string reason, string result)
    {
        var board = new Rect2(Origin, new Vector2(Cell * 8, Cell * 8));
        var band = new Rect2(board.Position + new Vector2(0, board.Size.Y * 0.38f), new Vector2(board.Size.X, board.Size.Y * 0.24f));
        DrawRect(band, new Color(0.04f, 0.05f, 0.07f, 0.82f));
        var font = Eve.Eve.Display;
        var big = Mathf.Max(14, (int)(Cell * 0.42f));
        var small = Mathf.Max(11, (int)(Cell * 0.22f));
        DrawString(font, new Vector2(band.Position.X, band.Position.Y + band.Size.Y * 0.48f), result,
            HorizontalAlignment.Center, band.Size.X, big, new Color(0.98f, 0.86f, 0.42f));
        DrawString(Eve.Eve.Body, new Vector2(band.Position.X, band.Position.Y + band.Size.Y * 0.8f), reason,
            HorizontalAlignment.Center, band.Size.X, small, Colors.White);
    }

    // ── The pointer ──

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                Press(SquareAt(press.Position), press.Position);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release:
                Release(SquareAt(release.Position), release.Position);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true }:
                Deselect();
                QueueRedraw();
                AcceptEvent();
                break;
            case InputEventMouseMotion motion when _dragFrom >= 0 && (motion.ButtonMask & MouseButtonMask.Left) != 0:
                _dragging = _dragging || motion.Position.DistanceTo(SquareRect(_dragFrom).GetCenter()) > Cell * 0.2f;
                _dragAt = motion.Position;
                QueueRedraw();
                break;
            case InputEventKey { Pressed: true, Keycode: Key.Escape }:
                Deselect();
                QueueRedraw();
                AcceptEvent();
                break;
        }
    }

    private void Press(int square, Vector2 at)
    {
        if (square < 0)
            return;
        // A target of the piece chosen: the move (a click, then a click).
        if (_selected >= 0 && _targets.Any(m => m.To == square))
        {
            Move(_selected, square, at);
            return;
        }
        var moves = _vm.MovesFrom(square);
        if (moves.Count == 0)
        {
            Deselect();
            if (!_vm.CanMove)
                _vm.Say(_vm.IsThinking ? "Wait: Black is thinking" : _vm.Game.IsOver ? "The game is over: NEW GAME" : "It is Black's turn", false);
            QueueRedraw();
            return;
        }
        _selected = square;
        _targets = moves;
        _dragFrom = square;
        _dragAt = at;
        _dragging = false;
        QueueRedraw();
    }

    private void Release(int square, Vector2 at)
    {
        var from = _dragFrom;
        var dragged = _dragging;
        _dragFrom = -1;
        _dragging = false;
        if (dragged && from >= 0 && square != from && _targets.Any(m => m.To == square))
        {
            Move(from, square, at);
            return;
        }
        QueueRedraw();
    }

    private void Move(int from, int to, Vector2 at)
    {
        if (_vm.IsPromotion(from, to))
        {
            AskPromotion(from, to, at);
            return;
        }
        Deselect();
        _vm.TryPlay(from, to);
        QueueRedraw();
    }

    /// <summary>The piece a pawn becomes on the last rank: a small menu where the pointer is.</summary>
    private void AskPromotion(int from, int to, Vector2 at)
    {
        var menu = new PopupMenu();
        var choices = new[] { ChessPiece.Queen, ChessPiece.Rook, ChessPiece.Bishop, ChessPiece.Knight };
        foreach (var (kind, index) in choices.Select((k, i) => (k, i)))
            menu.AddItem($"{Glyph(kind)}  {kind}", index);
        menu.AddThemeFontOverride("font", PieceFont);
        menu.AddThemeFontSizeOverride("font_size", 20);
        menu.IdPressed += id =>
        {
            Deselect();
            _vm.TryPlay(from, to, choices[id]);
            QueueRedraw();
        };
        menu.PopupHide += () => menu.CallDeferred(Node.MethodName.QueueFree);
        AddChild(menu);
        menu.Position = (Vector2I)(GetScreenTransform() * at);
        menu.Popup();
    }

    private void Deselect()
    {
        _selected = -1;
        _targets = [];
        _dragFrom = -1;
        _dragging = false;
    }
}
