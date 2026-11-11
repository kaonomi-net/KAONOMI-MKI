using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia;
using Avalonia.VisualTree;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.Views;

/// <summary>
/// Window of the tool views (cache, ontology, dashboards…): each one opens in a tab. It fills the free space of the
/// screen next to the docked chat and behaves as a normal window (not on top). On a large screen it is split into
/// equal panes, each with its own tabs: 4 (2 × 2) on a 4K screen, 2 or 3 columns side by side on an ultrawide screen
/// (3440 × 1440). A new tool goes to the first empty pane, then to the pane with the fewest tabs; a tool can be moved
/// to another pane by dragging its tab there, or with the "Move to" menu of its tab. The panes start equal; the bars
/// between them resize them, double-click makes them equal again.
/// </summary>
public partial class WorkspaceWindow : Window
{
    /// <summary>Tab strips of the window: one, or one per pane of the split layout (left to right, top to bottom).</summary>
    private readonly List<TabControl> _panes = [];

    /// <summary>Borders around the panes of the split layout (highlighted under a dragged tab), in the order of <see cref="_panes"/>.</summary>
    private readonly List<Border> _cells = [];

    /// <summary>Names of the panes of the split layout, for the "Move to" menu.</summary>
    private readonly string[] _paneNames;

    /// <summary>Pane of the last tool opened or selected (its selected tab is the window's selected tab).</summary>
    private TabControl _activePane;

    /// <summary>Tab being dragged by its header, with the pointer position where the drag started.</summary>
    private (TabItem Tab, Avalonia.Point Start, bool Moving)? _drag;

    /// <summary>Distance the pointer must travel before a press on a tab header becomes a drag.</summary>
    private const double DragThreshold = 8;

    /// <summary>Initializes the single tab strip (required by the XAML loader).</summary>
    public WorkspaceWindow() : this(columns: 1, rows: 1)
    {
    }

    /// <summary>
    /// Initializes the window: one tab strip (1 × 1), or <paramref name="columns"/> × <paramref name="rows"/> equal
    /// panes (2 × 2 on a 4K screen, 3 × 1 or 2 × 1 on an ultrawide screen).
    /// </summary>
    public WorkspaceWindow(int columns, int rows)
    {
        InitializeComponent();
        _columns = Math.Max(1, columns);
        _rows = Math.Max(1, rows);
        _paneNames = PaneNamesOf(_columns, _rows);
        if (!IsSplit)
        {
            _panes.Add(Tabs);
        }
        else
        {
            // Panes in the even columns / rows; the bars in the odd ones resize them.
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions(string.Join($",{SplitterSize},", Enumerable.Repeat("*", _columns))),
                RowDefinitions = new RowDefinitions(string.Join($",{SplitterSize},", Enumerable.Repeat("*", _rows))),
                Margin = new Avalonia.Thickness(3)
            };
            _grid = grid;
            // A pane never shrinks below a usable size when a bar is dragged.
            for (var c = 0; c < _columns; c++)
                grid.ColumnDefinitions[c * 2].MinWidth = 240;
            for (var r = 0; r < _rows; r++)
                grid.RowDefinitions[r * 2].MinHeight = 160;
            for (var i = 0; i < _columns * _rows; i++)
            {
                var pane = new TabControl { Classes = { "pane" } };
                pane.GotFocus += (_, _) => _activePane = pane;
                pane.SelectionChanged += (_, e) =>
                {
                    // Only the tabs of the pane (the lists of the tool views raise the same event).
                    if (ReferenceEquals(e.Source, pane))
                        LayoutChanged?.Invoke();
                };
                var hint = new TextBlock
                {
                    Text = "Free pane · the next tool opens here",
                    Opacity = 0.45,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false
                };
                pane.Items.CollectionChanged += (_, _) => hint.IsVisible = pane.Items.Count == 0;
                var cell = new Border { Classes = { "pane" }, Child = new Panel { Children = { hint, pane } } };
                Grid.SetColumn(cell, i % _columns * 2);
                Grid.SetRow(cell, i / _columns * 2);
                grid.Children.Add(cell);
                _panes.Add(pane);
                _cells.Add(cell);
            }

            // A vertical bar between two columns, a horizontal bar between two rows, across the whole grid.
            var splitters = new List<GridSplitter>();
            for (var c = 1; c < _columns; c++)
            {
                var vertical = new GridSplitter { Classes = { "paneSplitter" }, ResizeDirection = GridResizeDirection.Columns };
                Grid.SetColumn(vertical, c * 2 - 1);
                Grid.SetRowSpan(vertical, _rows * 2 - 1);
                splitters.Add(vertical);
            }
            for (var r = 1; r < _rows; r++)
            {
                var horizontal = new GridSplitter { Classes = { "paneSplitter" }, ResizeDirection = GridResizeDirection.Rows };
                Grid.SetRow(horizontal, r * 2 - 1);
                Grid.SetColumnSpan(horizontal, _columns * 2 - 1);
                splitters.Add(horizontal);
            }
            foreach (var splitter in splitters)
            {
                ToolTip.SetTip(splitter, "Drag to resize the panes; double-click to make them equal again");
                splitter.DragCompleted += (_, _) => LayoutChanged?.Invoke();
                splitter.DoubleTapped += (_, _) =>
                {
                    ColumnShares = [];
                    RowShares = [];
                    LayoutChanged?.Invoke();
                };
                grid.Children.Add(splitter);
            }
            Content = grid;
        }
        _activePane = _panes[0];
    }

    /// <summary>Number of columns and rows of panes (1 × 1 for the single tab strip).</summary>
    private readonly int _columns, _rows;

    /// <summary>Number of columns and rows of panes (1 × 1 for the single tab strip).</summary>
    public (int Columns, int Rows) PaneGrid => (_columns, _rows);

    /// <summary>True when the window is split into several panes (4K or ultrawide screen).</summary>
    public bool IsSplit => _columns * _rows > 1;

    /// <summary>Names of the panes, for the "Move to" menu: by corner (2 × 2), by side (one row), else by number.</summary>
    private static string[] PaneNamesOf(int columns, int rows) => (columns, rows) switch
    {
        (2, 2) => ["top left", "top right", "bottom left", "bottom right"],
        (2, 1) => ["left", "right"],
        (3, 1) => ["left", "middle", "right"],
        _ => Enumerable.Range(1, columns * rows).Select(i => $"#{i}").ToArray()
    };

    /// <summary>Width (and height) of the bars between the panes of the split layout, in device-independent pixels.</summary>
    private const int SplitterSize = 6;

    /// <summary>Smallest share of the width or height a pane keeps when its bar is moved.</summary>
    private const double MinSplit = 0.1;

    /// <summary>Grid of the split layout (null for the single tab strip).</summary>
    private readonly Grid? _grid;

    /// <summary>
    /// Split layout: share of the width taken by each column of panes, left to right (equal shares when set to a list
    /// of another length, e.g. empty); empty for the single tab strip.
    /// </summary>
    public IReadOnlyList<double> ColumnShares
    {
        get => _grid is null ? [] : Shares(_grid.ColumnDefinitions.Where((_, i) => i % 2 == 0).Select(d => (d.ActualWidth, d.Width)).ToList());
        set
        {
            if (_grid is not null)
                Apply(Normalize(value, _columns), (i, length) => _grid.ColumnDefinitions[i * 2].Width = length);
        }
    }

    /// <summary>Split layout: share of the height taken by each row of panes, top to bottom (as <see cref="ColumnShares"/>).</summary>
    public IReadOnlyList<double> RowShares
    {
        get => _grid is null ? [] : Shares(_grid.RowDefinitions.Where((_, i) => i % 2 == 0).Select(d => (d.ActualHeight, d.Height)).ToList());
        set
        {
            if (_grid is not null)
                Apply(Normalize(value, _rows), (i, length) => _grid.RowDefinitions[i * 2].Height = length);
        }
    }

    /// <summary>Shares of the sizes: the laid-out sizes, or their star values before the first layout.</summary>
    private static IReadOnlyList<double> Shares(IReadOnlyList<(double Actual, GridLength Star)> sizes)
    {
        var actual = sizes.Sum(s => s.Actual);
        var star = sizes.Sum(s => s.Star.Value);
        return sizes.Select(s => actual > 0 ? Math.Round(s.Actual / actual, 3)
            : star > 0 ? Math.Round(s.Star.Value / star, 3) : Math.Round(1.0 / sizes.Count, 3)).ToList();
    }

    /// <summary>
    /// <paramref name="count"/> shares summing to 1, each at least <see cref="MinSplit"/>: <paramref name="shares"/>
    /// when it has that many finite values, equal shares otherwise.
    /// </summary>
    private static double[] Normalize(IReadOnlyList<double> shares, int count)
    {
        if (shares.Count != count || shares.Any(s => !double.IsFinite(s) || s <= 0))
            return Enumerable.Repeat(1.0 / count, count).ToArray();
        var clamped = shares.Select(s => Math.Max(MinSplit, s)).ToArray();
        var total = clamped.Sum();
        return clamped.Select(s => s / total).ToArray();
    }

    /// <summary>Sets the star size of each pane column / row to its share.</summary>
    private static void Apply(double[] shares, Action<int, GridLength> set)
    {
        for (var i = 0; i < shares.Length; i++)
            set(i, new GridLength(shares[i], GridUnitType.Star));
    }

    /// <summary>Place saved in screens.yml to reopen the window at; the free space next to the chat otherwise.</summary>
    public WindowPlacement? RestorePlacement { get; init; }

    private IEnumerable<TabItem> AllTabs => _panes.SelectMany(p => p.Items.OfType<TabItem>());

    /// <summary>Keys of the open tabs, in order (pane after pane).</summary>
    public IReadOnlyList<string> TabKeys => AllTabs.Select(t => ((TabKey)t.Tag!).Key).ToList();

    /// <summary>Key of the selected tab (of the active pane in the split layout).</summary>
    public string? SelectedKey => (_activePane.SelectedItem as TabItem)?.Tag is TabKey key ? key.Key : null;

    /// <summary>Selects the tab registered under <paramref name="key"/>, when it is open.</summary>
    public void Select(string key)
    {
        if (Find(key) is { } found)
        {
            found.Pane.SelectedItem = found.Tab;
            _activePane = found.Pane;
        }
    }

    /// <summary>Split layout: the pane of each open tab (0 top left … left to right, top to bottom); empty for the single tab strip.</summary>
    public IReadOnlyDictionary<string, int> PaneOfTabs => !IsSplit
        ? new Dictionary<string, int>()
        : _panes.SelectMany((pane, index) => pane.Items.OfType<TabItem>().Select(t => (((TabKey)t.Tag!).Key, index)))
            .ToDictionary(x => x.Key, x => x.index);

    /// <summary>Split layout: the key of the selected tab of each pane (null for an empty pane); empty otherwise.</summary>
    public IReadOnlyList<string?> PaneSelections => !IsSplit
        ? []
        : _panes.Select(p => (p.SelectedItem as TabItem)?.Tag is TabKey key ? key.Key : null).ToList();

    /// <summary>Selects, in each pane of the split layout, the tab of <paramref name="keys"/> (one per pane, null to skip).</summary>
    public void SelectInPanes(IReadOnlyList<string?> keys)
    {
        for (var i = 0; i < Math.Min(keys.Count, _panes.Count); i++)
            if (keys[i] is { } key && _panes[i].Items.OfType<TabItem>().FirstOrDefault(t => t.Tag is TabKey k && k.Key == key) is { } tab)
                _panes[i].SelectedItem = tab;
    }

    /// <summary>Raised when a tab is opened, closed, moved to another pane or selected (the layout may be saved).</summary>
    public event Action? LayoutChanged;

    /// <summary>Title of the selected tab (used as the window name of the notes).</summary>
    public string? SelectedTitle => (_activePane.SelectedItem as TabItem)?.Tag is TabKey key ? key.Title : null;

    /// <summary>
    /// Shows the view registered under <paramref name="key"/>: its tab is selected when it is already open (the new
    /// <paramref name="viewModel"/> is then dropped), otherwise a tab is added with <paramref name="createView"/> (in
    /// the split layout, in <paramref name="pane"/> when given (its pane in screens.yml), else in the first empty pane, else
    /// the pane with the fewest tabs).
    /// </summary>
    public void Open(string key, string title, object viewModel, Func<Control> createView, int? pane = null)
    {
        if (Find(key) is { } existing)
        {
            if (!ReferenceEquals((existing.Tab.Content as Control)?.DataContext, viewModel))
                (viewModel as IDisposable)?.Dispose();
            existing.Pane.SelectedItem = existing.Tab;
            _activePane = existing.Pane;
            return;
        }

        var target = pane is >= 0 && pane < _panes.Count ? _panes[pane.Value] : _panes.OrderBy(p => p.Items.Count).First();
        var view = createView();
        view.DataContext = viewModel;
        var tab = new TabItem { Tag = new TabKey(key, title), Content = view };
        tab.Header = BuildHeader(tab, title);
        // Middle click closes the tab, as in a browser (in the window holding it then: tabs move to a new window when
        // the screens change).
        tab.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Middle && TopLevel.GetTopLevel(tab) is WorkspaceWindow window)
                window.CloseTab(tab);
        };
        target.Items.Add(tab);
        target.SelectedItem = tab;
        _activePane = target;
        LayoutChanged?.Invoke();
    }

    /// <summary>
    /// Moves every tab of <paramref name="other"/> into this window, views and their state included (nothing is released):
    /// each one in the pane of the same number when there is one, else in the pane with the fewest tabs; the selected
    /// tabs stay selected. <paramref name="other"/> is left empty, to be closed.
    /// </summary>
    public void TakeTabsFrom(WorkspaceWindow other)
    {
        for (var i = 0; i < other._panes.Count; i++)
        {
            var source = other._panes[i];
            var selected = source.SelectedItem;
            foreach (var tab in source.Items.OfType<TabItem>().ToList())
            {
                source.Items.Remove(tab);
                var target = i < _panes.Count ? _panes[i] : _panes.OrderBy(p => p.Items.Count).First();
                // The header (drag, "Move to" menu, close button) belongs to the window and its panes.
                tab.Header = BuildHeader(tab, ((TabKey)tab.Tag!).Title);
                target.Items.Add(tab);
                if (ReferenceEquals(tab, selected) || target.SelectedItem is null)
                    target.SelectedItem = tab;
            }
        }
        _activePane = _panes.FirstOrDefault(p => p.Items.Count > 0) ?? _panes[0];
    }

    /// <summary>The tab registered under <paramref name="key"/> and its pane, or null.</summary>
    private (TabControl Pane, TabItem Tab)? Find(string key)
    {
        foreach (var pane in _panes)
            if (pane.Items.OfType<TabItem>().FirstOrDefault(t => t.Tag is TabKey k && k.Key == key) is { } tab)
                return (pane, tab);
        return null;
    }

    /// <summary>
    /// Tab title with its close button; in the split layout, the header can be dragged to another pane and its context menu
    /// moves the tool to another pane.
    /// </summary>
    private Control BuildHeader(TabItem tab, string title)
    {
        var close = new Button
        {
            Classes = { "closeTab" },
            Content = new PathIcon { Width = 10, Height = 10, Data = this.FindResource("Icon.Close") as Geometry }
        };
        ToolTip.SetTip(close, "Close");
        close.Click += (_, _) => CloseTab(tab);

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Background = Brushes.Transparent,   // the whole header catches the pointer (drag)
            Children = { new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center }, close }
        };
        if (!IsSplit)
            return header;

        ToolTip.SetTip(header, "Drag this tab to another pane to move the tool, or right-click: Move to…");

        // Context menu: "Move to" each other pane (rebuilt when it opens, the tab may have moved since).
        var menu = new ContextMenu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            var current = PaneOf(tab);
            for (var i = 0; i < _panes.Count; i++)
            {
                var target = _panes[i];
                var item = new MenuItem { Header = $"Move to the {_paneNames[i]} pane", IsEnabled = !ReferenceEquals(target, current) };
                item.Click += (_, _) => MoveTab(tab, target);
                menu.Items.Add(item);
            }
        };
        header.ContextMenu = menu;

        // Drag: press on the header, move beyond the threshold, release over another pane.
        header.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is Visual source && close.IsVisualAncestorOf(source) || ReferenceEquals(e.Source, close))
                return;
            _drag = (tab, e.GetPosition(this), false);
            e.Pointer.Capture(header);
        };
        header.PointerMoved += (_, e) =>
        {
            if (_drag is not { } drag || !ReferenceEquals(drag.Tab, tab))
                return;
            var position = e.GetPosition(this);
            if (!drag.Moving && Distance(position, drag.Start) < DragThreshold)
                return;
            _drag = drag with { Moving = true };
            Cursor = new Cursor(StandardCursorType.DragMove);
            HighlightDropTarget(PaneAt(position));
        };
        header.PointerReleased += (_, e) =>
        {
            if (_drag is not { } drag || !ReferenceEquals(drag.Tab, tab))
                return;
            _drag = null;
            e.Pointer.Capture(null);
            Cursor = Cursor.Default;
            HighlightDropTarget(null);
            if (drag.Moving && PaneAt(e.GetPosition(this)) is { } target)
                MoveTab(tab, target);
        };
        header.PointerCaptureLost += (_, _) =>
        {
            if (_drag is { } drag && ReferenceEquals(drag.Tab, tab))
            {
                _drag = null;
                Cursor = Cursor.Default;
                HighlightDropTarget(null);
            }
        };
        return header;
    }

    private static double Distance(Avalonia.Point a, Avalonia.Point b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Pane whose cell contains <paramref name="position"/> (window coordinates), or null.</summary>
    private TabControl? PaneAt(Avalonia.Point position)
    {
        for (var i = 0; i < _cells.Count; i++)
            if (_cells[i].TranslatePoint(new Avalonia.Point(0, 0), this) is { } origin
                && new Avalonia.Rect(origin, _cells[i].Bounds.Size).Contains(position))
                return _panes[i];
        return null;
    }

    /// <summary>Highlights the cell of <paramref name="pane"/> as the drop target (none when null).</summary>
    private void HighlightDropTarget(TabControl? pane)
    {
        for (var i = 0; i < _cells.Count; i++)
            _cells[i].Classes.Set("dropTarget", ReferenceEquals(_panes[i], pane));
    }

    /// <summary>Pane holding <paramref name="tab"/>, or null.</summary>
    private TabControl? PaneOf(TabItem tab) => _panes.FirstOrDefault(p => p.Items.Contains(tab));

    /// <summary>
    /// Moves a tool to another pane: its tab (and view, with its state) leaves its pane and is selected in
    /// <paramref name="target"/>.
    /// </summary>
    private void MoveTab(TabItem tab, TabControl target)
    {
        if (PaneOf(tab) is not { } source || ReferenceEquals(source, target))
            return;
        source.Items.Remove(tab);
        target.Items.Add(tab);
        target.SelectedItem = tab;
        _activePane = target;
        LayoutChanged?.Invoke();
        if (tab.Tag is TabKey key)
            ActivityLog.Info("Workspace", $"{key.Title} moved to the {_paneNames[_panes.IndexOf(target)]} pane");
    }

    /// <summary>Removes the tab and releases its view model (refresh loops, counters); the window closes with its last tab.</summary>
    private void CloseTab(TabItem tab)
    {
        if (_panes.FirstOrDefault(p => p.Items.Contains(tab)) is not { } pane)
            return;
        pane.Items.Remove(tab);
        ((tab.Content as Control)?.DataContext as IDisposable)?.Dispose();
        if (!AllTabs.Any())
            Close();
        else
            LayoutChanged?.Invoke();
    }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Back at its saved place, or takes the free space of the screen next to the chat.
        if (!ScreenPlacement.Restore(this, RestorePlacement) && WindowGuard.FreeArea() is { } free)
            ScreenPlacement.Fill(this, free);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        foreach (var pane in _panes)
        {
            foreach (var tab in pane.Items.OfType<TabItem>())
                ((tab.Content as Control)?.DataContext as IDisposable)?.Dispose();
            pane.Items.Clear();
        }
        base.OnClosed(e);
    }

    /// <summary>Identity of a tab: the key used to find it again and its title.</summary>
    private sealed record TabKey(string Key, string Title);
}
