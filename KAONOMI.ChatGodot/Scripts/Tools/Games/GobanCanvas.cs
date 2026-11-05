using System;
using System.ComponentModel;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.ChatGodot.Tools.Games;

/// <summary>
/// The board of /game:go: a goban of wood, its lines, star points and coordinates (A to T without I, 1 at the
/// bottom), the stones lit from the top left, the last stone marked by a ring. Under the pointer a pale stone shows
/// where a click plays (red cross where the move is not allowed: taken, suicide, ko). After two passes, a click marks
/// a group dead (pale, crossed) or alive again, and each point shows its owner by a small square.
/// </summary>
public sealed partial class GobanCanvas : Control
{
    private static readonly Color Wood = new(0.86f, 0.70f, 0.43f);
    private static readonly Color WoodDark = new(0.80f, 0.63f, 0.36f);
    private static readonly Color Line = new(0.18f, 0.13f, 0.08f, 0.9f);

    private readonly GoWindowViewModel _vm;
    private (int X, int Y)? _hover;

    public GobanCanvas(GoWindowViewModel vm)
    {
        _vm = vm;
        MouseFilter = MouseFilterEnum.Stop;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(360, 360);
        Action changed = () => Eve.Bind.OnMain(QueueRedraw);
        PropertyChangedEventHandler property = (_, e) =>
        {
            if (e.PropertyName is nameof(GoWindowViewModel.IsThinking) or nameof(GoWindowViewModel.CanMove))
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

    // ── Geometry: the lines are inside a margin that holds the coordinates ──

    private int N => _vm.Game.Size;

    /// <summary>The distance between two lines.</summary>
    private float Step => Math.Max(6, Math.Min(Size.X, Size.Y) / (N + 1.4f));

    private Vector2 Origin
    {
        get
        {
            var span = Step * (N - 1);
            return new Vector2((Size.X - span) / 2, (Size.Y - span) / 2);
        }
    }

    private Vector2 PointAt(int x, int y) => Origin + new Vector2(x * Step, (N - 1 - y) * Step);

    private (int X, int Y)? PointUnder(Vector2 at)
    {
        var local = (at - Origin) / Step;
        var x = (int)MathF.Round(local.X);
        var y = N - 1 - (int)MathF.Round(local.Y);
        return x >= 0 && y >= 0 && x < N && y < N && local.DistanceTo(new Vector2(x, N - 1 - y)) < 0.5f ? (x, y) : null;
    }

    // ── Drawing ──

    public override void _Draw()
    {
        var game = _vm.Game;
        var board = game.Current;
        var step = Step;
        var span = step * (N - 1);
        var origin = Origin;

        // The wood, a few grains, the lines and the star points.
        var plank = new Rect2(origin - new Vector2(step * 0.7f, step * 0.7f), new Vector2(span + step * 1.4f, span + step * 1.4f));
        DrawRect(plank, Wood);
        for (var k = 0; k < 9; k++)
        {
            var y = plank.Position.Y + plank.Size.Y * (k + 0.5f) / 9;
            DrawLine(new Vector2(plank.Position.X, y), new Vector2(plank.End.X, y + step * 0.3f * MathF.Sin(k * 1.7f)), WoodDark with { A = 0.35f }, step * 0.18f);
        }
        var width = Math.Max(1, step * 0.035f);
        for (var k = 0; k < N; k++)
        {
            DrawLine(origin + new Vector2(k * step, 0), origin + new Vector2(k * step, span), Line, k == 0 || k == N - 1 ? width * 1.8f : width);
            DrawLine(origin + new Vector2(0, k * step), origin + new Vector2(span, k * step), Line, k == 0 || k == N - 1 ? width * 1.8f : width);
        }
        foreach (var (sx, sy) in board.StarPoints())
            DrawCircle(PointAt(sx, sy), Math.Max(2, step * 0.11f), Line);

        // The coordinates around the board.
        var font = Eve.Eve.Body;
        var fontSize = Mathf.Clamp((int)(step * 0.36f), 9, 16);
        var ink = Eve.Eve.Muted;
        for (var k = 0; k < N; k++)
        {
            var column = GoBoard.Columns[k].ToString();
            var row = (k + 1).ToString();
            var top = PointAt(k, N - 1);
            var bottom = PointAt(k, 0);
            DrawString(font, new Vector2(top.X - step / 2, plank.Position.Y - fontSize * 0.35f), column, HorizontalAlignment.Center, step, fontSize, ink);
            DrawString(font, new Vector2(bottom.X - step / 2, plank.End.Y + fontSize * 1.05f), column, HorizontalAlignment.Center, step, fontSize, ink);
            var left = PointAt(0, k);
            var right = PointAt(N - 1, k);
            DrawString(font, new Vector2(plank.Position.X - step * 0.95f, left.Y + fontSize * 0.35f), row, HorizontalAlignment.Right, step * 0.8f, fontSize, ink);
            DrawString(font, new Vector2(plank.End.X + step * 0.15f, right.Y + fontSize * 0.35f), row, HorizontalAlignment.Left, step * 0.8f, fontSize, ink);
        }

        // While counting: the owner of each point, the dead stones.
        sbyte[,]? owner = null;
        // (not after a resignation: nothing was counted)
        if (game.Phase == GoPhase.Scoring || (game.Phase == GoPhase.Over && !game.Result.EndsWith("+R", StringComparison.Ordinal)))
            owner = game.Count().Owner;

        var radius = step * 0.48f;
        for (var y = 0; y < N; y++)
        {
            for (var x = 0; x < N; x++)
            {
                var stone = board[x, y];
                var center = PointAt(x, y);
                var dead = stone != 0 && Contains(game, x, y);
                if (stone != 0)
                    DrawStone(center, radius, stone > 0, dead ? 0.38f : 1f);
                if (dead)
                {
                    var d = radius * 0.45f;
                    var cross = stone > 0 ? Colors.White : new Color(0.1f, 0.1f, 0.1f);
                    DrawLine(center - new Vector2(d, d), center + new Vector2(d, d), cross, Math.Max(2, step * 0.07f));
                    DrawLine(center - new Vector2(d, -d), center + new Vector2(d, -d), cross, Math.Max(2, step * 0.07f));
                }
                if (owner is not null && (stone == 0 || dead) && owner[x, y] != 0)
                {
                    var half = step * 0.17f;
                    DrawRect(new Rect2(center - new Vector2(half, half), new Vector2(half * 2, half * 2)),
                        owner[x, y] > 0 ? new Color(0.08f, 0.08f, 0.1f, 0.9f) : new Color(0.98f, 0.97f, 0.94f, 0.95f));
                }
            }
        }

        // The last stone, and the stone under the pointer.
        if (game.LastStone is { } last && board[last.X, last.Y] != 0)
            DrawArc(PointAt(last.X, last.Y), radius * 0.45f, 0, Mathf.Tau, 32, board[last.X, last.Y] > 0 ? Colors.White : Colors.Black, Math.Max(1.5f, step * 0.06f), true);
        if (_hover is { } hover && _vm.CanMove && board[hover.X, hover.Y] == 0)
        {
            var center = PointAt(hover.X, hover.Y);
            if (game.WhyNot(hover.X, hover.Y) is null)
                DrawStone(center, radius, true, 0.45f);
            else
            {
                var d = radius * 0.4f;
                var red = new Color(0.85f, 0.12f, 0.1f, 0.85f);
                DrawLine(center - new Vector2(d, d), center + new Vector2(d, d), red, Math.Max(2, step * 0.07f));
                DrawLine(center - new Vector2(d, -d), center + new Vector2(d, -d), red, Math.Max(2, step * 0.07f));
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

    /// <summary>A stone: a soft shadow, the body, a light from the top left.</summary>
    private void DrawStone(Vector2 center, float radius, bool black, float alpha)
    {
        DrawCircle(center + new Vector2(radius * 0.08f, radius * 0.12f), radius, new Color(0, 0, 0, 0.28f * alpha));
        if (black)
        {
            DrawCircle(center, radius, new Color(0.07f, 0.07f, 0.08f, alpha));
            DrawCircle(center - new Vector2(radius * 0.32f, radius * 0.32f), radius * 0.38f, new Color(1, 1, 1, 0.10f * alpha));
            DrawCircle(center - new Vector2(radius * 0.36f, radius * 0.36f), radius * 0.16f, new Color(1, 1, 1, 0.16f * alpha));
        }
        else
        {
            DrawCircle(center, radius, new Color(0.80f, 0.79f, 0.76f, alpha));
            DrawCircle(center - new Vector2(radius * 0.06f, radius * 0.06f), radius * 0.92f, new Color(0.96f, 0.95f, 0.92f, alpha));
            DrawCircle(center - new Vector2(radius * 0.3f, radius * 0.3f), radius * 0.35f, new Color(1, 1, 1, 0.55f * alpha));
        }
    }

    // ── The pointer ──

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion motion:
            {
                var hover = PointUnder(motion.Position);
                if (hover != _hover)
                {
                    _hover = hover;
                    QueueRedraw();
                }
                break;
            }
            case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
            {
                if (PointUnder(press.Position) is not { } point)
                    break;
                if (_vm.IsScoring)
                    _vm.ToggleDead(point.X, point.Y);
                else if (_vm.CanMove)
                    _vm.TryPlay(point.X, point.Y);
                else
                    _vm.Say(_vm.IsThinking ? "Wait: White is thinking" : _vm.Game.Phase == GoPhase.Over ? "The game is over: NEW GAME" : "It is White's turn", false);
                QueueRedraw();
                AcceptEvent();
                break;
            }
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationMouseExit && _hover is not null)
        {
            _hover = null;
            QueueRedraw();
        }
    }
}
