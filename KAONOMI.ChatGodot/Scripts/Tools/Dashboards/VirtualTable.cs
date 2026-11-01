using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Godot;
using KAONOMI.ChatGodot.Eve;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;

namespace KAONOMI.ChatGodot.Tools.Dashboards;

/// <summary>
/// A column of a <see cref="VirtualTable"/>: its title, its width (0 or less: a share of the free width), the text of a
/// row, and optionally its colour, a badge colour behind it, right alignment, a mono or bold font.
/// </summary>
public sealed record TableColumn(string Title, float Width, Func<object, string> Text)
{
    public Func<object, Color?>? Color { get; init; }
    public Func<object, Color?>? Badge { get; init; }
    public bool Right { get; init; }
    public bool Mono { get; init; }
    public bool Bold { get; init; }
}

/// <summary>
/// A table drawn by hand that only draws the rows in view (the logs dashboard holds 100,000+ rows): a header, rows
/// with an alternate shade, a selection (click, arrows, Page Up / Down, Home / End), a tooltip per row, the
/// mouse wheel and a scroll bar, Ctrl / ⌘ + C copies the selected row, a double-click (or Enter) activates it.
/// </summary>
public partial class VirtualTable : Control
{
    private const float RowHeight = 22;
    private const float HeaderHeight = 24;
    private const int FontSize = 12;

    private readonly VScrollBar _bar = new();
    private readonly Dictionary<(int Row, int Column), (float Width, string Text)> _trimmed = new();
    private List<TableColumn> _columns = [];
    private IReadOnlyList<object> _rows = [];
    private int _selected = -1;
    private Action? _unfollow;
    private bool _dirty;
    private Func<IEnumerable>? _source;

    public VirtualTable()
    {
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
        FocusMode = FocusModeEnum.All;
        ClipContents = true;
        CustomMinimumSize = new Vector2(0, 120);
        TooltipText = " ";
        _bar.ValueChanged += _ => QueueRedraw();
        AddChild(_bar);
        Resized += Layout;
    }

    /// <summary>The tooltip of a row.</summary>
    public Func<object, string>? Tooltip { get; set; }

    /// <summary>The text copied for a row (all the cells, tab-separated, by default).</summary>
    public Func<object, string>? CopyText { get; set; }

    /// <summary>Text shown when there is no row.</summary>
    public string EmptyText { get; set; } = string.Empty;

    /// <summary>Raised on a double-click (or Enter) on a row.</summary>
    public event Action<object>? Activated;

    /// <summary>The selected row, or null.</summary>
    public object? Selected => _selected >= 0 && _selected < _rows.Count ? _rows[_selected] : null;

    public VirtualTable Columns(params TableColumn[] columns)
    {
        _columns = columns.ToList();
        _trimmed.Clear();
        QueueRedraw();
        return this;
    }

    /// <summary>Shows <paramref name="rows"/>.</summary>
    public void SetRows(IEnumerable rows)
    {
        var selected = Selected;
        _rows = rows.Cast<object>().ToList();
        _trimmed.Clear();
        _selected = selected is null ? -1 : IndexOf(selected);
        Layout();
        QueueRedraw();
    }

    private int IndexOf(object row)
    {
        for (var i = 0; i < _rows.Count; i++)
            if (ReferenceEquals(_rows[i], row))
                return i;
        return -1;
    }

    /// <summary>Follows an observable collection: the rows are read again once per frame after it changes.</summary>
    public VirtualTable Follow(IEnumerable items)
    {
        _unfollow?.Invoke();
        _source = () => items;
        if (items is INotifyCollectionChanged observable)
        {
            NotifyCollectionChangedEventHandler handler = (_, _) => Bind.OnMain(MarkDirty);
            observable.CollectionChanged += handler;
            _unfollow = () => observable.CollectionChanged -= handler;
        }
        SetRows(items);
        return this;
    }

    private void MarkDirty()
    {
        if (_dirty || !IsInstanceValid(this))
            return;
        _dirty = true;
        Callable.From(() =>
        {
            _dirty = false;
            if (IsInstanceValid(this) && _source is not null)
                SetRows(_source());
        }).CallDeferred();
    }

    public override void _ExitTree()
    {
        _unfollow?.Invoke();
        _unfollow = null;
    }

    public override void _EnterTree()
    {
        if (_unfollow is null && _source?.Invoke() is { } items)
            Follow(items);
    }

    private float BodyHeight => Math.Max(0, Size.Y - HeaderHeight);

    private void Layout()
    {
        var width = _bar.GetCombinedMinimumSize().X;
        _bar.Position = new Vector2(Size.X - width, HeaderHeight);
        _bar.Size = new Vector2(width, BodyHeight);
        _bar.MaxValue = _rows.Count * RowHeight;
        _bar.Page = BodyHeight;
        _bar.Step = 1;
        _bar.Visible = _rows.Count * RowHeight > BodyHeight;
        if (!_bar.Visible)
            _bar.Value = 0;
        QueueRedraw();
    }

    /// <summary>The x and width of each column across <paramref name="total"/>.</summary>
    private (float X, float Width)[] Widths(float total)
    {
        var fixedWidth = _columns.Where(c => c.Width > 0).Sum(c => c.Width);
        var flexible = _columns.Count(c => c.Width <= 0);
        var shares = Math.Max(1, flexible);
        // The fixed columns shrink when the free columns would get less than 150 pixels each.
        var scale = flexible > 0 && fixedWidth > 0 && fixedWidth + 150 * flexible > total ? Math.Max(0.4f, (total - 150 * flexible) / fixedWidth) : 1f;
        var free = Math.Max(60, total - fixedWidth * scale) / shares;
        var x = 8f;
        var result = new (float, float)[_columns.Count];
        for (var i = 0; i < _columns.Count; i++)
        {
            var width = _columns[i].Width > 0 ? _columns[i].Width * scale : free;
            result[i] = (x, width - 8);
            x += width;
        }
        return result;
    }

    private Font FontOf(TableColumn column) => column.Mono ? EveStyle.Mono : column.Bold ? EveStyle.Display : GetThemeDefaultFont();

    private string Trim(int row, int index, string text, Font font, float width)
    {
        if (_trimmed.TryGetValue((row, index), out var cached) && Math.Abs(cached.Width - width) < 0.5f)
            return cached.Text;
        var result = text.Replace('\n', ' ').Replace('\r', ' ');
        if (font.GetStringSize(result, HorizontalAlignment.Left, -1, FontSize).X > width)
        {
            int low = 0, high = result.Length;
            while (low < high)
            {
                var middle = (low + high + 1) / 2;
                if (font.GetStringSize(result[..middle] + "…", HorizontalAlignment.Left, -1, FontSize).X <= width)
                    low = middle;
                else
                    high = middle - 1;
            }
            result = result[..low] + "…";
        }
        _trimmed[(row, index)] = (width, result);
        return result;
    }

    public override void _Draw()
    {
        var size = Size;
        var barWidth = _bar.Visible ? _bar.Size.X : 0;
        var widths = Widths(size.X - barWidth - 8);
        var font = GetThemeDefaultFont();
        var ascent = font.GetAscent(FontSize);

        // Header.
        DrawRect(new Rect2(0, 0, size.X, HeaderHeight), Eve.Eve.Raised(0.035f));
        DrawLine(new Vector2(0, HeaderHeight - 0.5f), new Vector2(size.X, HeaderHeight - 0.5f), EveStyle.Edge with { A = 0.4f });
        for (var i = 0; i < _columns.Count; i++)
        {
            var (x, width) = widths[i];
            var column = _columns[i];
            DrawString(EveStyle.Display, new Vector2(x, (HeaderHeight + ascent) / 2 - 1), column.Title.ToUpperInvariant(),
                column.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left, width, 10, EveStyle.Muted);
        }

        if (_rows.Count == 0)
        {
            if (EmptyText.Length > 0)
                DrawString(font, new Vector2(0, HeaderHeight + BodyHeight / 2), EmptyText, HorizontalAlignment.Center, size.X, 13, EveStyle.Muted);
            return;
        }

        var offset = (float)_bar.Value;
        var first = Math.Max(0, (int)(offset / RowHeight));
        var last = Math.Min(_rows.Count - 1, (int)((offset + BodyHeight) / RowHeight) + 1);
        for (var r = first; r <= last; r++)
        {
            var top = HeaderHeight + r * RowHeight - offset;
            if (top + RowHeight < HeaderHeight)
                continue;
            var rect = new Rect2(0, Math.Max(HeaderHeight, top), size.X - barWidth, Math.Min(RowHeight, top + RowHeight - HeaderHeight));
            if (r == _selected)
                DrawRect(rect, EveStyle.Cyan with { A = HasFocus() ? 0.22f : 0.14f });
            else if (r % 2 == 1)
                DrawRect(rect, Eve.Eve.Raised(0.02f));
            if (top < HeaderHeight)
                continue;
            var row = _rows[r];
            var baseline = top + (RowHeight + ascent) / 2 - 2;
            for (var i = 0; i < _columns.Count; i++)
            {
                var column = _columns[i];
                var (x, width) = widths[i];
                var cellFont = FontOf(column);
                var text = Trim(r, i, column.Text(row) ?? string.Empty, cellFont, width);
                var color = column.Color?.Invoke(row) ?? EveStyle.Text;
                if (column.Badge?.Invoke(row) is { } badge)
                {
                    var textWidth = cellFont.GetStringSize(text, HorizontalAlignment.Left, -1, FontSize - 1).X;
                    DrawRect(new Rect2(x - 4, top + 3, textWidth + 8, RowHeight - 6), badge);
                    DrawString(cellFont, new Vector2(x, baseline), text, HorizontalAlignment.Left, width, FontSize - 1, EveStyle.TextBright);
                    continue;
                }
                DrawString(cellFont, new Vector2(x, baseline), text, column.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left, width, FontSize, color);
            }
        }
    }

    private int RowAt(Vector2 position)
    {
        if (position.Y < HeaderHeight)
            return -1;
        var index = (int)((position.Y - HeaderHeight + _bar.Value) / RowHeight);
        return index >= 0 && index < _rows.Count ? index : -1;
    }

    public override string _GetTooltip(Vector2 atPosition)
    {
        var index = RowAt(atPosition);
        return index >= 0 && Tooltip?.Invoke(_rows[index]) is { Length: > 0 } tip ? tip : string.Empty;
    }

    private void Select(int index)
    {
        if (_rows.Count == 0)
            return;
        _selected = Math.Clamp(index, 0, _rows.Count - 1);
        var top = _selected * RowHeight;
        if (top < _bar.Value)
            _bar.Value = top;
        else if (top + RowHeight > _bar.Value + BodyHeight)
            _bar.Value = top + RowHeight - BodyHeight;
        QueueRedraw();
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp }:
                _bar.Value -= RowHeight * 3;
                AcceptEvent();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelDown }:
                _bar.Value += RowHeight * 3;
                AcceptEvent();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click:
                GrabFocus();
                if (RowAt(click.Position) is var index and >= 0)
                {
                    Select(index);
                    if (click.DoubleClick)
                        Activated?.Invoke(_rows[index]);
                }
                AcceptEvent();
                break;
            case InputEventKey { Pressed: true } key:
                var page = Math.Max(1, (int)(BodyHeight / RowHeight) - 1);
                switch (key.Keycode)
                {
                    case Key.Up: Select(_selected - 1); break;
                    case Key.Down: Select(_selected + 1); break;
                    case Key.Pageup: Select(_selected - page); break;
                    case Key.Pagedown: Select(_selected + page); break;
                    case Key.Home: Select(0); break;
                    case Key.End: Select(_rows.Count - 1); break;
                    case Key.Enter or Key.KpEnter when Selected is { } row: Activated?.Invoke(row); break;
                    case Key.C when (key.CtrlPressed || key.MetaPressed) && Selected is { } row:
                        Dialogs.Copy(CopyText?.Invoke(row) ?? string.Join('\t', _columns.Select(c => c.Text(row))));
                        break;
                    default: return;
                }
                AcceptEvent();
                break;
        }
    }
}
