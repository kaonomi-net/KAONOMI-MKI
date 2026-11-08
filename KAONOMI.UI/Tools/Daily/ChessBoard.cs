using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Daily;

/// <summary>
/// The board of /game:chess, White at the bottom. A click on a white piece shows its legal moves (a dot, a ring on a
/// capture), a click on one of them plays it; a piece can be dragged too. The last move is lit, the square of a king in
/// check is red, a pawn reaching the last rank asks for its piece. The pieces are the chess symbols of the system font,
/// filled, white with a dark outline or black with light lines.
/// </summary>
public sealed class ChessBoard : Control
{
    private static readonly IBrush Light = new SolidColorBrush(Color.FromRgb(237, 219, 186));
    private static readonly IBrush Dark = new SolidColorBrush(Color.FromRgb(179, 135, 99));
    private static readonly IBrush LastMoveLight = new SolidColorBrush(Color.FromRgb(242, 220, 120));
    private static readonly IBrush LastMoveDark = new SolidColorBrush(Color.FromRgb(214, 178, 82));
    private static readonly IBrush SelectedBrush = new SolidColorBrush(Color.FromRgb(120, 176, 214));
    private static readonly IBrush CheckBrush = new SolidColorBrush(Color.FromRgb(222, 70, 60));
    private static readonly IBrush HintBrush = new SolidColorBrush(Color.FromRgb(90, 80, 70));
    private static readonly IBrush WhiteBody = new SolidColorBrush(Color.FromRgb(252, 250, 242));
    private static readonly IBrush WhiteRim = new SolidColorBrush(Color.FromRgb(20, 20, 23));
    private static readonly IBrush BlackBody = new SolidColorBrush(Color.FromRgb(28, 26, 28));
    private static readonly IBrush BlackLines = new SolidColorBrush(Color.FromRgb(200, 196, 188));
    private static readonly IBrush BannerBrush = new SolidColorBrush(Color.FromRgb(12, 14, 18));
    private static readonly IBrush BannerTitle = new SolidColorBrush(Color.FromRgb(250, 220, 107));

    /// <summary>A font with the chess symbols (U+2654…U+265F), as text and not as emoji.</summary>
    public static readonly FontFamily PieceFont = new("Segoe UI Symbol, Arial Unicode MS, DejaVu Sans, Noto Sans Symbols 2, Apple Symbols");

    private readonly ChessWindowViewModel _vm;
    private readonly Dictionary<(string, int), Geometry> _glyphs = [];
    private int _selected = -1;
    private IReadOnlyList<ChessMove> _targets = [];
    private int _dragFrom = -1;
    private Point _dragAt;
    private bool _dragging;

    public ChessBoard(ChessWindowViewModel vm)
    {
        _vm = vm;
        Focusable = true;
        ClipToBounds = true;
        MinWidth = 360;
        MinHeight = 360;
        Action changed = () => Ui.OnMain(() =>
        {
            Deselect();
            InvalidateVisual();
        });
        PropertyChangedEventHandler property = (_, e) =>
        {
            if (e.PropertyName is nameof(ChessWindowViewModel.IsThinking) or nameof(ChessWindowViewModel.CanMove))
                Ui.OnMain(InvalidateVisual);
        };
        DailyUi.Hook(this,
            () =>
            {
                _vm.Changed += changed;
                _vm.PropertyChanged += property;
            },
            () =>
            {
                _vm.Changed -= changed;
                _vm.PropertyChanged -= property;
            });
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
        ChessPiece.King => "♔︎",
        ChessPiece.Queen => "♕︎",
        ChessPiece.Rook => "♖︎",
        ChessPiece.Bishop => "♗︎",
        ChessPiece.Knight => "♘︎",
        ChessPiece.Pawn => "♙︎",
        _ => string.Empty
    };

    // ── Geometry ──

    private double Cell => Math.Max(8, Math.Min(Bounds.Width, Bounds.Height) / 8);

    private Point Origin => new((Bounds.Width - Cell * 8) / 2, (Bounds.Height - Cell * 8) / 2);

    private Rect SquareRect(int square) =>
        new(Origin.X + ChessPosition.File(square) * Cell, Origin.Y + (7 - ChessPosition.Rank(square)) * Cell, Cell, Cell);

    private int SquareAt(Point at)
    {
        var x = (at.X - Origin.X) / Cell;
        var y = (at.Y - Origin.Y) / Cell;
        if (x < 0 || y < 0 || x >= 8 || y >= 8)
            return -1;
        return ChessPosition.SquareOf((int)x, 7 - (int)y);
    }

    // ── Drawing ──

    public override void Render(DrawingContext context)
    {
        // The whole control takes the pointer.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var position = _vm.Game.Current;
        var cell = Cell;
        var coordinates = Math.Max(9, cell * 0.17);
        var check = position.InCheck ? position.KingSquare(position.WhiteToMove) : -1;
        for (var square = 0; square < 64; square++)
        {
            var rect = SquareRect(square);
            var light = (ChessPosition.File(square) + ChessPosition.Rank(square)) % 2 == 1;
            var fill = light ? Light : Dark;
            if (_vm.LastMove is { } last && (last.From == square || last.To == square))
                fill = light ? LastMoveLight : LastMoveDark;
            if (square == _selected)
                fill = SelectedBrush;
            // The king in check: its square red.
            if (square == check)
                fill = CheckBrush;
            context.FillRectangle(fill, rect);
            // The coordinates in the corners, as on a printed board: the files on the first rank, the ranks on file a.
            var ink = light ? Dark : Light;
            if (ChessPosition.Rank(square) == 0)
                DailyUi.DrawText(context, ((char)('a' + ChessPosition.File(square))).ToString(),
                    new Point(rect.Right - coordinates * 0.85, rect.Bottom - coordinates * 1.35), coordinates, ink);
            if (ChessPosition.File(square) == 0)
                DailyUi.DrawText(context, ((char)('1' + ChessPosition.Rank(square))).ToString(),
                    new Point(rect.X + coordinates * 0.3, rect.Y + coordinates * 0.1), coordinates, ink);
        }

        // The legal moves of the piece chosen.
        foreach (var target in _targets.Select(m => m.To).Distinct())
        {
            var rect = SquareRect(target);
            if (position.IsCapture(_targets.First(m => m.To == target)))
                context.DrawEllipse(null, new Pen(HintBrush, cell * 0.07), rect.Center, cell * 0.43, cell * 0.43);
            else
                context.DrawEllipse(HintBrush, null, rect.Center, cell * 0.14, cell * 0.14);
        }

        for (var square = 0; square < 64; square++)
        {
            var piece = position[square];
            if (piece == 0 || (_dragging && square == _dragFrom))
                continue;
            DrawPiece(context, piece, SquareRect(square).Center, cell);
        }
        if (_dragging && _dragFrom >= 0 && position[_dragFrom] != 0)
            DrawPiece(context, position[_dragFrom], _dragAt, cell * 1.08);

        if (_vm.Game.IsOver)
            DrawBanner(context, _vm.Game.Reason, _vm.Game.Result);
    }

    private Geometry GlyphGeometry(string text, int size)
    {
        if (_glyphs.Count > 64)
            _glyphs.Clear();
        if (!_glyphs.TryGetValue((text, size), out var geometry))
        {
            var formatted = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(PieceFont), size, Brushes.Black);
            geometry = formatted.BuildGeometry(new Point(0, 0)) ?? new StreamGeometry();
            _glyphs[(text, size)] = geometry;
        }
        return geometry;
    }

    private void DrawPiece(DrawingContext context, sbyte piece, Point center, double cell)
    {
        var size = (int)(cell * 0.82);
        var kind = ChessPosition.Kind(piece);
        var body = GlyphGeometry(Glyph(kind), size);
        var bounds = body.Bounds;
        var offset = new Point(center.X - bounds.X - bounds.Width / 2, center.Y - bounds.Y - bounds.Height / 2);
        var outline = Math.Max(2, cell * 0.06);
        using (context.PushTransform(Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            if (piece > 0)
            {
                // White: a white body, its lines and rim drawn by a dark outline.
                context.DrawGeometry(null, new Pen(WhiteRim, outline * 2) { LineJoin = PenLineJoin.Round }, body);
                context.DrawGeometry(WhiteBody, null, body);
                return;
            }
            // Black: a solid black body (the outline fills the hollows of the symbol), then the lines of the hollow
            // symbol in light grey on top, as on a printed diagram.
            context.DrawGeometry(BlackBody, new Pen(BlackBody, outline * 2) { LineJoin = PenLineJoin.Round }, body);
            context.DrawGeometry(BlackLines, null, GlyphGeometry(HollowGlyph(kind), size));
        }
    }

    private void DrawBanner(DrawingContext context, string reason, string result)
    {
        var board = new Rect(Origin, new Size(Cell * 8, Cell * 8));
        var band = new Rect(board.X, board.Y + board.Height * 0.38, board.Width, board.Height * 0.24);
        context.FillRectangle(BannerBrush, band);
        var big = Math.Max(14, Cell * 0.42);
        var small = Math.Max(11, Cell * 0.22);
        DailyUi.DrawText(context, result, new Point(band.X, band.Y + band.Height * 0.12), big, BannerTitle, band.Width, bold: true);
        DailyUi.DrawText(context, reason, new Point(band.X, band.Y + band.Height * 0.62), small, Brushes.White, band.Width);
    }

    // ── The pointer ──

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            Deselect();
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed)
            return;
        e.Pointer.Capture(this);
        Press(SquareAt(point.Position), point.Position);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom < 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var at = e.GetPosition(this);
        var centre = SquareRect(_dragFrom).Center;
        _dragging = _dragging || Math.Sqrt(Math.Pow(at.X - centre.X, 2) + Math.Pow(at.Y - centre.Y, 2)) > Cell * 0.2;
        _dragAt = at;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;
        e.Pointer.Capture(null);
        var at = e.GetPosition(this);
        Release(SquareAt(at), at);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Deselect();
            InvalidateVisual();
            e.Handled = true;
        }
    }

    private void Press(int square, Point at)
    {
        if (square < 0)
            return;
        // A target of the piece chosen: the move (a click, then a click).
        if (_selected >= 0 && _targets.Any(m => m.To == square))
        {
            Move(_selected, square);
            return;
        }
        var moves = _vm.MovesFrom(square);
        if (moves.Count == 0)
        {
            Deselect();
            if (!_vm.CanMove)
                _vm.Say(_vm.IsThinking ? "Wait: Black is thinking" : _vm.Game.IsOver ? "The game is over: NEW GAME" : "It is Black's turn", false);
            InvalidateVisual();
            return;
        }
        _selected = square;
        _targets = moves;
        _dragFrom = square;
        _dragAt = at;
        _dragging = false;
        InvalidateVisual();
    }

    private void Release(int square, Point at)
    {
        var from = _dragFrom;
        var dragged = _dragging;
        _dragFrom = -1;
        _dragging = false;
        if (dragged && from >= 0 && square != from && _targets.Any(m => m.To == square))
        {
            Move(from, square);
            return;
        }
        InvalidateVisual();
    }

    private void Move(int from, int to)
    {
        if (_vm.IsPromotion(from, to))
        {
            AskPromotion(from, to);
            return;
        }
        Deselect();
        _vm.TryPlay(from, to);
        InvalidateVisual();
    }

    /// <summary>The piece a pawn becomes on the last rank: a small menu where the pointer is.</summary>
    private void AskPromotion(int from, int to)
    {
        var menu = new MenuFlyout();
        foreach (var kind in new[] { ChessPiece.Queen, ChessPiece.Rook, ChessPiece.Bishop, ChessPiece.Knight })
        {
            var item = new MenuItem
            {
                Header = new TextBlock { Text = $"{Glyph(kind)}  {kind}", FontFamily = PieceFont, FontSize = 20 }
            };
            item.Click += (_, _) =>
            {
                Deselect();
                _vm.TryPlay(from, to, kind);
                InvalidateVisual();
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(this, true);
    }

    private void Deselect()
    {
        _selected = -1;
        _targets = [];
        _dragFrom = -1;
        _dragging = false;
    }
}
