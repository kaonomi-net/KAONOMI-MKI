using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Daily;

/// <summary>
/// The board of /game:solitaire: a square grid like the paper of a notebook, the points on its crossings, the lines in
/// green, the point being added ringed in the accent colour. Under the pointer, the crossing is ringed; with a point to
/// add, the line a click on a point would draw is shown, green when it is allowed, red (dashed) when it is not.
/// A click adds a point or ends its line; a right click or Escape takes the point away; Cmd/Ctrl+Z undoes a move.
/// </summary>
public sealed class Game1Canvas : Control
{
    private static readonly IBrush LineColor = DailyUi.Solid("#4CAF50");
    private static readonly IBrush PreviewColor = DailyUi.Solid("#2E7D32");
    private static readonly IBrush Refused = DailyUi.Solid("#E53935");

    private readonly Game1WindowViewModel _vm;
    private (int X, int Y)? _hovered;

    public Game1Canvas(Game1WindowViewModel vm)
    {
        _vm = vm;
        Focusable = true;
        ClipToBounds = true;
        MinWidth = 420;
        MinHeight = 420;
        Action changed = () => Ui.OnMain(InvalidateVisual);
        System.ComponentModel.PropertyChangedEventHandler hints = (_, e) =>
        {
            if (e.PropertyName == nameof(Game1WindowViewModel.ShowHints))
                Ui.OnMain(InvalidateVisual);
        };
        DailyUi.Hook(this,
            () =>
            {
                vm.Changed += changed;
                vm.PropertyChanged += hints;
            },
            () =>
            {
                vm.Changed -= changed;
                vm.PropertyChanged -= hints;
            });
    }

    // ── Where the crossings are ──

    /// <summary>The side of a cell, the whole grid in the canvas with half a cell of margin.</summary>
    private double Cell => Math.Max(4, Math.Min(Bounds.Width, Bounds.Height) / Game1WindowViewModel.Size);

    private Point Origin
    {
        get
        {
            var side = Cell * (Game1WindowViewModel.Size - 1);
            return new Point((Bounds.Width - side) / 2, (Bounds.Height - side) / 2);
        }
    }

    private Point At(int x, int y) => new(Origin.X + x * Cell, Origin.Y + y * Cell);

    private (int X, int Y)? CrossingAt(Point position)
    {
        var x = (int)Math.Round((position.X - Origin.X) / Cell);
        var y = (int)Math.Round((position.Y - Origin.Y) / Cell);
        if (x < 0 || y < 0 || x >= Game1WindowViewModel.Size || y >= Game1WindowViewModel.Size)
            return null;
        var at = At(x, y);
        var distance = Math.Sqrt(Math.Pow(at.X - position.X, 2) + Math.Pow(at.Y - position.Y, 2));
        return distance <= Cell * 0.45 ? (x, y) : null;
    }

    // ── Drawing ──

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Ui.Field, new Rect(Bounds.Size));
        var cell = Cell;
        var n = Game1WindowViewModel.Size;
        var grid = new Pen(Ui.Edge, 1);
        for (var i = 0; i < n; i++)
        {
            context.DrawLine(grid, At(i, 0), At(i, n - 1));
            context.DrawLine(grid, At(0, i), At(n - 1, i));
        }

        // The crossings where a point can be added.
        if (_vm.ShowHints)
        {
            var hint = new Pen(Ui.Accent, 1.5);
            foreach (var (x, y) in _vm.Playable)
                context.DrawEllipse(null, hint, At(x, y), cell * 0.2, cell * 0.2);
        }

        var width = Math.Max(2, cell * 0.12);
        var line = new Pen(LineColor, width) { LineCap = PenLineCap.Round };
        foreach (var move in _vm.Moves)
        {
            var (ex, ey) = move.Line.End;
            context.DrawLine(line, At(move.Line.X, move.Line.Y), At(ex, ey));
        }

        DrawPreview(context, width);

        var radius = Math.Max(2.5, cell * 0.13);
        var ink = Ui.TextBright;
        foreach (var (x, y) in _vm.Points)
            context.DrawEllipse(ink, null, At(x, y), radius, radius);

        // The number of each move beside the point it added.
        if (cell >= 18)
        {
            var size = Math.Max(8, cell * 0.32);
            for (var i = 0; i < _vm.Moves.Count; i++)
            {
                var (x, y) = _vm.Moves[i].Point;
                var text = DailyUi.Text((i + 1).ToString(), size, Ui.Muted);
                var at = At(x, y);
                context.DrawText(text, new Point(at.X + radius + 1, at.Y - radius - 1 - text.Height));
            }
        }

        if (_vm.Pending is { } pending)
        {
            var at = At(pending.X, pending.Y);
            context.DrawEllipse(Ui.Accent, null, at, radius * 1.2, radius * 1.2);
            context.DrawEllipse(null, new Pen(Ui.Accent, 2), at, radius * 2.2, radius * 2.2);
        }
        else if (_hovered is { } hovered && !_vm.Points.Contains(hovered))
        {
            var playable = _vm.Playable.Contains(hovered);
            context.DrawEllipse(null, new Pen(playable ? Ui.Accent : Ui.Muted, 1.5), At(hovered.X, hovered.Y), radius * 1.8, radius * 1.8);
        }
    }

    /// <summary>With a point to add and a point under the pointer: the line a click would draw, green or red.</summary>
    private void DrawPreview(DrawingContext context, double width)
    {
        if (_vm.Pending is not { } pending || _hovered is not { } end || !_vm.Points.Contains(end))
            return;
        var (line, _) = _vm.LineTo(pending, end);
        if (line is not null)
        {
            var (ex, ey) = line.End;
            context.DrawLine(new Pen(PreviewColor, width) { LineCap = PenLineCap.Round }, At(line.X, line.Y), At(ex, ey));
        }
        else
            context.DrawLine(new Pen(Refused, 2, new DashStyle([4, 3], 0)), At(pending.X, pending.Y), At(end.X, end.Y));
    }

    // ── Input ──

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var hovered = CrossingAt(e.GetPosition(this));
        if (hovered != _hovered)
        {
            _hovered = hovered;
            InvalidateVisual();
            Cursor = hovered is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            if (_vm.Pending is { } pending && hovered is { } end && _vm.Points.Contains(end))
                ToolTip.SetTip(this, _vm.LineTo(pending, end).Reason);
            else
                ToolTip.SetTip(this, null);
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = null;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            if (CrossingAt(point.Position) is { } crossing)
                _vm.Click(crossing.X, crossing.Y);
            e.Handled = true;
        }
        else if (point.Properties.IsRightButtonPressed)
        {
            _vm.CancelPending();
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            _vm.CancelPending();
            e.Handled = true;
        }
        else if (DailyUi.IsCommand(e, Key.Z))
        {
            Ui.Run(_vm.UndoCommand);
            e.Handled = true;
        }
    }
}
