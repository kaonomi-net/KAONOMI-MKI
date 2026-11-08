using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// A column of a <see cref="VirtualTable"/>: its title, its width (0 or less: a share of the free width), the text of a
/// row, and optionally its colour, a badge colour behind it, right alignment, a mono or bold font.
/// </summary>
public sealed record TableColumn(string Title, double Width, Func<object, string> Text)
{
    public Func<object, IBrush?>? Color { get; init; }
    public Func<object, IBrush?>? Badge { get; init; }
    public bool Right { get; init; }
    public bool Mono { get; init; }
    public bool Bold { get; init; }
}

/// <summary>
/// A table drawn by hand that only draws the rows in view (the logs dashboard holds 100,000+ rows): a header, rows
/// with an alternate shade, a selection (click, arrows, Page Up / Down, Home / End), a tooltip per row, the mouse wheel
/// and a scroll bar, Ctrl / ⌘ + C copies the selected row, a double-click (or Enter) activates it.
/// </summary>
public sealed class VirtualTable : Grid
{
    private readonly TableBody _body;
    private readonly ScrollBar _bar = new() { Orientation = Orientation.Vertical, SmallChange = TableBody.RowHeight, LargeChange = 200 };
    private Action? _unfollow;
    private Func<IEnumerable>? _source;
    private bool _dirty;

    public VirtualTable()
    {
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        RowDefinitions = new RowDefinitions($"{TableBody.HeaderHeight},*");
        MinHeight = 120;
        ClipToBounds = true;
        _body = new TableBody(this);
        SetRowSpan(_body, 2);
        Children.Add(_body);
        SetColumn(_bar, 1);
        SetRow(_bar, 1);
        Children.Add(_bar);
        _bar.Scroll += (_, _) => _body.InvalidateVisual();
        _bar.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
                _body.InvalidateVisual();
        };
        AttachedToVisualTree += (_, _) =>
        {
            if (_unfollow is null && _source?.Invoke() is { } items)
                Follow(items);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _unfollow?.Invoke();
            _unfollow = null;
        };
    }

    internal ScrollBar Bar => _bar;

    /// <summary>The tooltip of a row.</summary>
    public Func<object, string>? Tooltip { get; set; }

    /// <summary>The text copied for a row (all the cells, tab-separated, by default).</summary>
    public Func<object, string>? CopyText { get; set; }

    /// <summary>Text shown when there is no row.</summary>
    public string EmptyText { get; set; } = string.Empty;

    /// <summary>Raised on a double-click (or Enter) on a row.</summary>
    public event Action<object>? Activated;

    internal void Activate(object row) => Activated?.Invoke(row);

    internal List<TableColumn> TableColumns { get; private set; } = [];

    internal IReadOnlyList<object> Rows { get; private set; } = [];

    internal int SelectedIndex { get; set; } = -1;

    /// <summary>The selected row, or null.</summary>
    public object? Selected => SelectedIndex >= 0 && SelectedIndex < Rows.Count ? Rows[SelectedIndex] : null;

    public VirtualTable Columns(params TableColumn[] columns)
    {
        TableColumns = columns.ToList();
        _body.InvalidateVisual();
        return this;
    }

    /// <summary>Shows <paramref name="rows"/> (the selected row stays selected when it is still there).</summary>
    public void SetRows(IEnumerable? rows)
    {
        var selected = Selected;
        try
        {
            Rows = rows?.Cast<object>().ToList() ?? [];
        }
        catch (InvalidOperationException)
        {
            return;
        }
        SelectedIndex = -1;
        if (selected is not null)
            for (var i = 0; i < Rows.Count; i++)
                if (ReferenceEquals(Rows[i], selected))
                {
                    SelectedIndex = i;
                    break;
                }
        UpdateBar();
        _body.InvalidateVisual();
    }

    /// <summary>Follows an observable collection: the rows are read again once after a burst of changes.</summary>
    public VirtualTable Follow(IEnumerable items)
    {
        _unfollow?.Invoke();
        _unfollow = null;
        _source = () => items;
        if (items is INotifyCollectionChanged observable)
        {
            NotifyCollectionChangedEventHandler handler = (_, _) => Ui.OnMain(MarkDirty);
            observable.CollectionChanged += handler;
            _unfollow = () => observable.CollectionChanged -= handler;
        }
        SetRows(items);
        return this;
    }

    private void MarkDirty()
    {
        if (_dirty)
            return;
        _dirty = true;
        Dispatcher.UIThread.Post(() =>
        {
            _dirty = false;
            if (_source is not null)
                SetRows(_source());
        }, DispatcherPriority.Background);
    }

    private double _height;

    internal double BodyHeight => Math.Max(0, (_height > 0 ? _height : Bounds.Height) - TableBody.HeaderHeight);

    internal void UpdateBar()
    {
        var total = Rows.Count * TableBody.RowHeight;
        _bar.Maximum = Math.Max(0, total - BodyHeight);
        _bar.ViewportSize = BodyHeight;
        _bar.LargeChange = Math.Max(TableBody.RowHeight, BodyHeight - TableBody.RowHeight);
        _bar.IsVisible = total > BodyHeight;
        if (!_bar.IsVisible)
            _bar.Value = 0;
        else if (_bar.Value > _bar.Maximum)
            _bar.Value = _bar.Maximum;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _height = finalSize.Height;
        var size = base.ArrangeOverride(finalSize);
        UpdateBar();
        return size;
    }

    internal void Copy(object row) =>
        Dialogs.Copy(CopyText?.Invoke(row) ?? string.Join('\t', TableColumns.Select(c => c.Text(row))));
}

/// <summary>The drawn part of a <see cref="VirtualTable"/>: the header and the rows in view.</summary>
internal sealed class TableBody : Control
{
    public const double RowHeight = 22;
    public const double HeaderHeight = 24;
    private const double FontSize = 12;

    private static readonly Typeface Normal = new(FontFamily.Default);
    private static readonly Typeface Bold = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface Mono = new(Ui.Mono);

    private readonly VirtualTable _table;
    private int _tipRow = -1;

    public TableBody(VirtualTable table)
    {
        _table = table;
        Focusable = true;
        ClipToBounds = true;
        Background = Brushes.Transparent;
    }

    public static readonly StyledProperty<IBrush?> BackgroundProperty = Border.BackgroundProperty.AddOwner<TableBody>();

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    private double Offset => _table.Bar.IsVisible ? _table.Bar.Value : 0;

    // The scroll bar has its own column of the grid.
    private static double BarWidth => 0;

    /// <summary>The x and width of each column across <paramref name="total"/>.</summary>
    private (double X, double Width)[] Widths(double total)
    {
        var columns = _table.TableColumns;
        var fixedWidth = columns.Where(c => c.Width > 0).Sum(c => c.Width);
        var flexible = columns.Count(c => c.Width <= 0);
        var shares = Math.Max(1, flexible);
        // The fixed columns shrink when the free columns would get less than 150 pixels each.
        var scale = flexible > 0 && fixedWidth > 0 && fixedWidth + 150 * flexible > total ? Math.Max(0.4, (total - 150 * flexible) / fixedWidth) : 1;
        var free = Math.Max(60, total - fixedWidth * scale) / shares;
        var x = 8.0;
        var result = new (double, double)[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var width = columns[i].Width > 0 ? columns[i].Width * scale : free;
            result[i] = (x, Math.Max(4, width - 8));
            x += width;
        }
        return result;
    }

    private static FormattedText Format(string text, Typeface typeface, double size, IBrush brush, double width, bool right)
    {
        var formatted = new FormattedText(text.Replace('\n', ' ').Replace('\r', ' '), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size, brush)
        {
            MaxTextWidth = Math.Max(1, width),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
            TextAlignment = right ? TextAlignment.Right : TextAlignment.Left
        };
        return formatted;
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.FillRectangle(Background ?? Brushes.Transparent, new Rect(size));
        var widths = Widths(size.Width - BarWidth - 8);
        var columns = _table.TableColumns;
        var rows = _table.Rows;

        // Header.
        context.FillRectangle(Ui.Header, new Rect(0, 0, size.Width, HeaderHeight));
        context.DrawLine(new Pen(Ui.Edge, 1), new Point(0, HeaderHeight - 0.5), new Point(size.Width, HeaderHeight - 0.5));
        for (var i = 0; i < columns.Count; i++)
        {
            var (x, width) = widths[i];
            var title = Format(columns[i].Title.ToUpperInvariant(), Bold, 10, Ui.Muted, width, columns[i].Right);
            context.DrawText(title, new Point(x, (HeaderHeight - title.Height) / 2));
        }

        if (rows.Count == 0)
        {
            if (_table.EmptyText.Length > 0)
            {
                var empty = Format(_table.EmptyText, Normal, 13, Ui.Muted, size.Width, false);
                empty.TextAlignment = TextAlignment.Center;
                context.DrawText(empty, new Point(0, HeaderHeight + (size.Height - HeaderHeight - empty.Height) / 2));
            }
            return;
        }

        using var clip = context.PushClip(new Rect(0, HeaderHeight, size.Width, Math.Max(0, size.Height - HeaderHeight)));
        var offset = Offset;
        var body = size.Height - HeaderHeight;
        var first = Math.Max(0, (int)(offset / RowHeight));
        var last = Math.Min(rows.Count - 1, (int)((offset + body) / RowHeight) + 1);
        var selection = DataUi.Fade(Ui.Accent, IsFocused ? 0.24 : 0.15);
        var shade = DataUi.Fade(Ui.TextBright, 0.03);
        for (var r = first; r <= last; r++)
        {
            var top = HeaderHeight + r * RowHeight - offset;
            var rect = new Rect(0, top, size.Width - BarWidth, RowHeight);
            if (r == _table.SelectedIndex)
                context.FillRectangle(selection, rect);
            else if (r % 2 == 1)
                context.FillRectangle(shade, rect);
            var row = rows[r];
            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                var (x, width) = widths[i];
                var typeface = column.Mono ? Mono : column.Bold ? Bold : Normal;
                var text = column.Text(row) ?? string.Empty;
                if (column.Badge?.Invoke(row) is { } badge)
                {
                    var tag = Format(text, typeface, FontSize - 1, Ui.TextBright, width, false);
                    context.FillRectangle(badge, new Rect(x - 4, top + 3, Math.Min(width, tag.WidthIncludingTrailingWhitespace) + 8, RowHeight - 6), 2);
                    context.DrawText(tag, new Point(x, top + (RowHeight - tag.Height) / 2));
                    continue;
                }
                var cell = Format(text, typeface, FontSize, column.Color?.Invoke(row) ?? Ui.Text, width, column.Right);
                context.DrawText(cell, new Point(x, top + (RowHeight - cell.Height) / 2));
            }
        }
    }

    private int RowAt(Point position)
    {
        if (position.Y < HeaderHeight)
            return -1;
        var index = (int)((position.Y - HeaderHeight + Offset) / RowHeight);
        return index >= 0 && index < _table.Rows.Count ? index : -1;
    }

    private void Select(int index)
    {
        var rows = _table.Rows;
        if (rows.Count == 0)
            return;
        _table.SelectedIndex = Math.Clamp(index, 0, rows.Count - 1);
        var top = _table.SelectedIndex * RowHeight;
        var bar = _table.Bar;
        if (top < bar.Value)
            bar.Value = top;
        else if (top + RowHeight > bar.Value + _table.BodyHeight)
            bar.Value = Math.Min(bar.Maximum, top + RowHeight - _table.BodyHeight);
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var bar = _table.Bar;
        bar.Value = Math.Clamp(bar.Value - e.Delta.Y * RowHeight * 3, 0, bar.Maximum);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (RowAt(e.GetPosition(this)) is var index and >= 0)
        {
            Select(index);
            if (e.ClickCount == 2)
                _table.Activate(_table.Rows[index]);
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var index = RowAt(e.GetPosition(this));
        if (index == _tipRow)
            return;
        _tipRow = index;
        var tip = index >= 0 && _table.Tooltip?.Invoke(_table.Rows[index]) is { Length: > 0 } text ? text : null;
        ToolTip.SetTip(this, tip);
        ToolTip.SetIsOpen(this, tip is not null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _tipRow = -1;
        ToolTip.SetIsOpen(this, false);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsFocusedProperty)
            InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var page = Math.Max(1, (int)(_table.BodyHeight / RowHeight) - 1);
        var selected = _table.SelectedIndex;
        switch (e.Key)
        {
            case Key.Up: Select(selected - 1); break;
            case Key.Down: Select(selected + 1); break;
            case Key.PageUp: Select(selected - page); break;
            case Key.PageDown: Select(selected + page); break;
            case Key.Home: Select(0); break;
            case Key.End: Select(_table.Rows.Count - 1); break;
            case Key.Enter or Key.Return when _table.Selected is { } row: _table.Activate(row); break;
            case Key.C when DataUi.IsCommand(e) && _table.Selected is { } row: _table.Copy(row); break;
            default: return;
        }
        e.Handled = true;
    }
}
