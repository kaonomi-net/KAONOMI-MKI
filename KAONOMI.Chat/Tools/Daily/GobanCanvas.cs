using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Daily;

/// <summary>
/// The board of /game:go: a goban of wood, its lines, star points and coordinates (A to T without I, 1 at the
/// bottom), the stones flat, the last stone marked by a ring. Under the pointer a ring shows where a click plays (a
/// red cross where the move is not allowed: taken, suicide, ko). After two passes, a click marks a group dead (greyed,
/// crossed) or alive again, and each point shows its owner by a small square.
/// </summary>
public sealed class GobanCanvas : Control
{
    private static readonly IBrush Wood = new SolidColorBrush(Color.FromRgb(219, 179, 110));
    private static readonly IBrush LineBrush = new SolidColorBrush(Color.FromRgb(46, 33, 20));
    private static readonly IBrush BlackStone = new SolidColorBrush(Color.FromRgb(18, 18, 20));
    private static readonly IBrush WhiteStone = new SolidColorBrush(Color.FromRgb(245, 243, 235));
    private static readonly IBrush WhiteEdge = new SolidColorBrush(Color.FromRgb(140, 136, 128));
    private static readonly IBrush DeadBlack = new SolidColorBrush(Color.FromRgb(110, 98, 80));
    private static readonly IBrush DeadWhite = new SolidColorBrush(Color.FromRgb(232, 212, 170));
    private static readonly IBrush Refused = new SolidColorBrush(Color.FromRgb(217, 31, 26));

    private readonly GoWindowViewModel _vm;
    private (int X, int Y)? _hover;

    public GobanCanvas(GoWindowViewModel vm)
    {
        _vm = vm;
        ClipToBounds = true;
        MinWidth = 360;
        MinHeight = 360;
        Action changed = () => Ui.OnMain(InvalidateVisual);
        PropertyChangedEventHandler property = (_, e) =>
        {
            if (e.PropertyName is nameof(GoWindowViewModel.IsThinking) or nameof(GoWindowViewModel.CanMove))
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

    // ── Geometry: the lines are inside a margin that holds the coordinates ──

    private int N => _vm.Game.Size;

    /// <summary>The distance between two lines.</summary>
    private double Step => Math.Max(6, Math.Min(Bounds.Width, Bounds.Height) / (N + 1.4));

    private Point Origin
    {
        get
        {
            var span = Step * (N - 1);
            return new Point((Bounds.Width - span) / 2, (Bounds.Height - span) / 2);
        }
    }

    private Point PointAt(int x, int y) => new(Origin.X + x * Step, Origin.Y + (N - 1 - y) * Step);

    private (int X, int Y)? PointUnder(Point at)
    {
        var lx = (at.X - Origin.X) / Step;
        var ly = (at.Y - Origin.Y) / Step;
        var x = (int)Math.Round(lx);
        var row = (int)Math.Round(ly);
        var y = N - 1 - row;
        var distance = Math.Sqrt((lx - x) * (lx - x) + (ly - row) * (ly - row));
        return x >= 0 && y >= 0 && x < N && y < N && distance < 0.5 ? (x, y) : null;
    }

    // ── Drawing ──

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var game = _vm.Game;
        var board = game.Current;
        var step = Step;
        var span = step * (N - 1);
        var origin = Origin;

        // The wood, the lines and the star points.
        var plank = new Rect(origin.X - step * 0.7, origin.Y - step * 0.7, span + step * 1.4, span + step * 1.4);
        context.FillRectangle(Wood, plank);
        var width = Math.Max(1, step * 0.035);
        var thin = new Pen(LineBrush, width);
        var thick = new Pen(LineBrush, width * 1.8);
        for (var k = 0; k < N; k++)
        {
            var pen = k == 0 || k == N - 1 ? thick : thin;
            context.DrawLine(pen, new Point(origin.X + k * step, origin.Y), new Point(origin.X + k * step, origin.Y + span));
            context.DrawLine(pen, new Point(origin.X, origin.Y + k * step), new Point(origin.X + span, origin.Y + k * step));
        }
        foreach (var (sx, sy) in board.StarPoints())
        {
            var r = Math.Max(2, step * 0.11);
            context.DrawEllipse(LineBrush, null, PointAt(sx, sy), r, r);
        }

        // The coordinates around the board.
        var fontSize = Math.Clamp(step * 0.36, 9, 16);
        var ink = Ui.Muted;
        for (var k = 0; k < N; k++)
        {
            var column = GoBoard.Columns[k].ToString();
            var row = (k + 1).ToString();
            var top = PointAt(k, N - 1);
            var bottom = PointAt(k, 0);
            DailyUi.DrawText(context, column, new Point(top.X - step / 2, plank.Y - fontSize * 1.45), fontSize, ink, step);
            DailyUi.DrawText(context, column, new Point(bottom.X - step / 2, plank.Bottom + fontSize * 0.1), fontSize, ink, step);
            var left = PointAt(0, k);
            var right = PointAt(N - 1, k);
            var text = DailyUi.Text(row, fontSize, ink);
            context.DrawText(text, new Point(plank.X - step * 0.15 - text.Width, left.Y - text.Height / 2));
            context.DrawText(text, new Point(plank.Right + step * 0.15, right.Y - text.Height / 2));
        }

        // While counting: the owner of each point, the dead stones (not after a resignation: nothing was counted).
        sbyte[,]? owner = null;
        if (game.Phase == GoPhase.Scoring || (game.Phase == GoPhase.Over && !game.Result.EndsWith("+R", StringComparison.Ordinal)))
            owner = game.Count().Owner;

        var radius = step * 0.48;
        var crossWidth = Math.Max(2, step * 0.07);
        for (var y = 0; y < N; y++)
        {
            for (var x = 0; x < N; x++)
            {
                var stone = board[x, y];
                var center = PointAt(x, y);
                var dead = stone != 0 && Contains(game, x, y);
                if (stone != 0)
                    DrawStone(context, center, radius, stone > 0, dead);
                if (dead)
                {
                    var d = radius * 0.45;
                    var cross = new Pen(stone > 0 ? Brushes.White : BlackStone, crossWidth);
                    context.DrawLine(cross, new Point(center.X - d, center.Y - d), new Point(center.X + d, center.Y + d));
                    context.DrawLine(cross, new Point(center.X - d, center.Y + d), new Point(center.X + d, center.Y - d));
                }
                if (owner is not null && (stone == 0 || dead) && owner[x, y] != 0)
                {
                    var half = step * 0.17;
                    context.DrawRectangle(owner[x, y] > 0 ? BlackStone : WhiteStone, owner[x, y] > 0 ? null : new Pen(WhiteEdge, 1),
                        new Rect(center.X - half, center.Y - half, half * 2, half * 2));
                }
            }
        }

        // The last stone, and the point under the pointer.
        if (game.LastStone is { } last && board[last.X, last.Y] != 0)
        {
            var r = radius * 0.45;
            context.DrawEllipse(null, new Pen(board[last.X, last.Y] > 0 ? Brushes.White : Brushes.Black, Math.Max(1.5, step * 0.06)), PointAt(last.X, last.Y), r, r);
        }
        if (_hover is { } hover && _vm.CanMove && board[hover.X, hover.Y] == 0)
        {
            var center = PointAt(hover.X, hover.Y);
            if (game.WhyNot(hover.X, hover.Y) is null)
                context.DrawEllipse(null, new Pen(BlackStone, Math.Max(2, step * 0.08)), center, radius * 0.9, radius * 0.9);
            else
            {
                var d = radius * 0.4;
                var red = new Pen(Refused, crossWidth);
                context.DrawLine(red, new Point(center.X - d, center.Y - d), new Point(center.X + d, center.Y + d));
                context.DrawLine(red, new Point(center.X - d, center.Y + d), new Point(center.X + d, center.Y - d));
            }
        }
    }

    private static bool Contains(GoGame game, int x, int y)
    {
        foreach (var point in game.Dead)
            if (point.X == x && point.Y == y)
                return true;
        return false;
    }

    /// <summary>A stone, flat: black, or white with a grey rim; greyed when marked dead.</summary>
    private static void DrawStone(DrawingContext context, Point center, double radius, bool black, bool dead)
    {
        if (black)
            context.DrawEllipse(dead ? DeadBlack : BlackStone, null, center, radius, radius);
        else
            context.DrawEllipse(dead ? DeadWhite : WhiteStone, new Pen(WhiteEdge, Math.Max(1, radius * 0.06)), center, radius, radius);
    }

    // ── The pointer ──

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var hover = PointUnder(e.GetPosition(this));
        if (hover != _hover)
        {
            _hover = hover;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is not null)
        {
            _hover = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || PointUnder(point.Position) is not { } at)
            return;
        if (_vm.IsScoring)
            _vm.ToggleDead(at.X, at.Y);
        else if (_vm.CanMove)
            _vm.TryPlay(at.X, at.Y);
        else
            _vm.Say(_vm.IsThinking ? "Wait: White is thinking" : _vm.Game.Phase == GoPhase.Over ? "The game is over: NEW GAME" : "It is White's turn", false);
        InvalidateVisual();
        e.Handled = true;
    }
}
