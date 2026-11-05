using System;
using System.Linq;
using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Games;

/// <summary>
/// The board of /game:solitaire: a square grid like the paper of a notebook, the black points on its crossings, the lines in
/// green, the point being added ringed in the accent colour. Under the pointer, the crossing is lit; with a point to
/// add, the line a click on a black point would draw is shown, green when it is allowed, red when it is not.
/// A click adds a point or ends its line; a right click or Escape takes the point away; Cmd/Ctrl+Z undoes a move.
/// </summary>
public partial class Game1Canvas : Control
{
    private readonly Game1WindowViewModel _vm;
    private (int X, int Y)? _hovered;

    private static Color Paper => Eve.Eve.IsLight ? new Color(0.97f, 0.975f, 0.96f) : new Color(0.03f, 0.04f, 0.05f, 0.95f);
    private static Color GridColor => Eve.Eve.IsLight ? new Color(0.45f, 0.45f, 0.75f, 0.45f) : Eve.Eve.Edge with { A = 0.35f };
    private static Color Ink => Eve.Eve.IsLight ? new Color(0.05f, 0.05f, 0.06f) : Eve.Eve.TextBright;
    private static readonly Color LineColor = new("#4CAF50");
    private static readonly Color Refused = new("#E53935");

    public Game1Canvas(Game1WindowViewModel vm)
    {
        _vm = vm;
        FocusMode = FocusModeEnum.All;
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        CustomMinimumSize = new Vector2(420, 420);
        MouseExited += () => { _hovered = null; QueueRedraw(); };
        Action changed = () => Bind.OnMain(QueueRedraw);
        vm.Changed += changed;
        Bind.OnGone(this, () => vm.Changed -= changed);
    }

    // ── Where the crossings are ──

    /// <summary>The side of a cell, the whole grid in the canvas with half a cell of margin.</summary>
    private float Cell => Math.Max(4, Math.Min(Size.X, Size.Y) / Game1WindowViewModel.Size);

    private Vector2 Origin
    {
        get
        {
            var side = Cell * (Game1WindowViewModel.Size - 1);
            return (Size - new Vector2(side, side)) / 2;
        }
    }

    private Vector2 At(int x, int y) => Origin + new Vector2(x, y) * Cell;

    private (int X, int Y)? CrossingAt(Vector2 position)
    {
        var p = (position - Origin) / Cell;
        var x = (int)Math.Round(p.X);
        var y = (int)Math.Round(p.Y);
        if (x < 0 || y < 0 || x >= Game1WindowViewModel.Size || y >= Game1WindowViewModel.Size)
            return null;
        return (At(x, y).DistanceTo(position) <= Cell * 0.45f) ? (x, y) : null;
    }

    // ── Drawing ──

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), Paper);
        var cell = Cell;
        var n = Game1WindowViewModel.Size;
        for (var i = 0; i < n; i++)
        {
            DrawLine(At(i, 0), At(i, n - 1), GridColor, 1);
            DrawLine(At(0, i), At(n - 1, i), GridColor, 1);
        }

        // The crossings where a point can be added.
        if (_vm.ShowHints)
            foreach (var (x, y) in _vm.Playable)
                DrawArc(At(x, y), cell * 0.2f, 0, Mathf.Tau, 20, Eve.Eve.Cyan with { A = 0.55f }, 1.5f, true);

        var width = Math.Max(2, cell * 0.12f);
        foreach (var move in _vm.Moves)
        {
            var (ex, ey) = move.Line.End;
            DrawLine(At(move.Line.X, move.Line.Y), At(ex, ey), LineColor, width, true);
        }

        DrawPreview(width);

        var radius = Math.Max(2.5f, cell * 0.13f);
        foreach (var (x, y) in _vm.Points)
            DrawCircle(At(x, y), radius, Ink);

        // The number of each move beside the point it added.
        if (cell >= 18)
        {
            var size = (int)Math.Max(8, cell * 0.32f);
            for (var i = 0; i < _vm.Moves.Count; i++)
            {
                var (x, y) = _vm.Moves[i].Point;
                DrawString(Eve.Eve.Body, At(x, y) + new Vector2(radius + 1, -radius - 1), (i + 1).ToString(), HorizontalAlignment.Left, -1, size, Eve.Eve.Muted);
            }
        }

        if (_vm.Pending is { } pending)
        {
            DrawCircle(At(pending.X, pending.Y), radius * 1.2f, Eve.Eve.Cyan);
            DrawArc(At(pending.X, pending.Y), radius * 2.2f, 0, Mathf.Tau, 28, Eve.Eve.Cyan, 2, true);
        }
        else if (_hovered is { } hovered && !_vm.Points.Contains(hovered))
        {
            var playable = _vm.Playable.Contains(hovered);
            DrawArc(At(hovered.X, hovered.Y), radius * 1.8f, 0, Mathf.Tau, 24, (playable ? Eve.Eve.Cyan : Eve.Eve.Muted) with { A = 0.8f }, 1.5f, true);
        }
    }

    /// <summary>With a point to add and a black point under the pointer: the line a click would draw, green or red.</summary>
    private void DrawPreview(float width)
    {
        if (_vm.Pending is not { } pending || _hovered is not { } end || !_vm.Points.Contains(end))
            return;
        var (line, _) = _vm.LineTo(pending, end);
        if (line is not null)
        {
            var (ex, ey) = line.End;
            DrawLine(At(line.X, line.Y), At(ex, ey), LineColor with { A = 0.55f }, width, true);
        }
        else
            DrawDashedLine(At(pending.X, pending.Y), At(end.X, end.Y), Refused with { A = 0.8f }, 2, Cell * 0.2f, true, true);
    }

    // ── Input ──

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion motion:
            {
                var hovered = CrossingAt(motion.Position);
                if (hovered != _hovered)
                {
                    _hovered = hovered;
                    QueueRedraw();
                }
                MouseDefaultCursorShape = hovered is not null ? CursorShape.PointingHand : CursorShape.Arrow;
                if (_vm.Pending is { } pending && hovered is { } end && _vm.Points.Contains(end))
                    TooltipText = _vm.LineTo(pending, end).Reason;
                else
                    TooltipText = string.Empty;
                break;
            }
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } press:
                GrabFocus();
                if (CrossingAt(press.Position) is { } crossing)
                    _vm.Click(crossing.X, crossing.Y);
                AcceptEvent();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right }:
                _vm.CancelPending();
                AcceptEvent();
                break;
            case InputEventKey { Pressed: true, Keycode: Key.Escape }:
                _vm.CancelPending();
                AcceptEvent();
                break;
            case InputEventKey { Pressed: true, Keycode: Key.Z } key when key.CtrlPressed || key.MetaPressed:
                if (_vm.UndoCommand.CanExecute(null))
                    _vm.UndoCommand.Execute(null);
                AcceptEvent();
                break;
        }
    }
}
