using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Media;

/// <summary>
/// The pieces shared by the views of the pictures: the opening of a picture in the image analysis window, the files
/// dropped on a view, the drag data of a picture, a picture read at a reduced size, the metadata of a picture.
/// </summary>
public static class ImageKit
{
    // ── Image analysis ──

    /// <summary>
    /// Opens the analysis of the picture at <paramref name="path"/> made by <paramref name="create"/> in its own window
    /// (named Image-&lt;title&gt;); <paramref name="fallback"/> (the viewer of the system) when there is no analysis.
    /// </summary>
    public static void OpenAnalysis(Shell shell, Func<string, ImageAnalysisViewModel>? create, string? path, Action? fallback)
    {
        if (string.IsNullOrEmpty(path))
            return;
        if (create is null)
        {
            fallback?.Invoke();
            return;
        }
        var analysis = create(path);
        shell.Open(analysis, title: analysis.Title);
    }

    // ── Pictures ──

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".heif", ".tif", ".tiff"
    };

    /// <summary>True when <paramref name="path"/> has the extension of a picture.</summary>
    public static bool IsPicture(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>The picture at <paramref name="path"/>, at most <paramref name="maxWidth"/> pixels wide (0: as it is); null when it cannot be read.</summary>
    public static Bitmap? Load(string path, int maxWidth = 0)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var stream = File.OpenRead(path);
            if (maxWidth <= 0)
                return new Bitmap(stream);
            var bitmap = Bitmap.DecodeToWidth(stream, maxWidth, BitmapInterpolationMode.MediumQuality);
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or ArgumentException or NullReferenceException)
        {
            return null;
        }
    }

    // ── Drag and drop ──

    /// <summary>The drag data of pictures inside the chat: their paths as text, one per line.</summary>
    public static DataTransfer DragData(IEnumerable<string> paths)
    {
        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateText(string.Join('\n', paths)));
        return data;
    }

    /// <summary>The local paths of the pictures in dragged data (files, or paths given as text).</summary>
    public static IReadOnlyList<string> PicturePaths(IDataTransfer? data)
    {
        var paths = new List<string>();
        if (data is null)
            return paths;
        if (data.TryGetFiles() is { } files)
            paths.AddRange(files.Select(f => f.TryGetLocalPath()).OfType<string>());
        if (data.TryGetText() is { } text)
            paths.AddRange(text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return paths.Where(p => IsPicture(p) && File.Exists(p)).Distinct().ToList();
    }

    /// <summary>The local paths of the files and folders in dragged data (the Finder / Explorer).</summary>
    public static IReadOnlyList<string> FilePaths(IDataTransfer? data)
    {
        if (data?.TryGetFiles() is not { } files)
            return [];
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    /// <summary>
    /// Calls <paramref name="dropped"/> with the files dropped on <paramref name="root"/> (from the Finder / Explorer;
    /// the pictures dragged inside the chat are left to the controls that take them).
    /// </summary>
    public static void FilesDropped(Control root, Action<string[]> dropped)
    {
        DragDrop.SetAllowDrop(root, true);
        root.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (!e.Handled && FilePaths(e.DataTransfer).Count > 0)
                e.DragEffects = DragDropEffects.Copy;
        });
        root.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (e.Handled)
                return;
            var files = FilePaths(e.DataTransfer);
            if (files.Count == 0)
                return;
            e.Handled = true;
            dropped([.. files]);
        });
    }

    // ── Metadata ──

    /// <summary>
    /// The groups of metadata of a picture (the name of each group, then its keys and values), the keys
    /// <paramref name="keyWidth"/> wide.
    /// </summary>
    public static Control Metadata(IEnumerable<MetadataGroup> groups, IBrush groupColor, double keyWidth = 140, IBrush? keyColor = null, IBrush? valueColor = null)
    {
        var column = Ui.Column(8);
        foreach (var group in groups)
        {
            var block = Ui.Column(1, Ui.Label(group.Name, groupColor, 12));
            foreach (var entry in group.Entries)
            {
                var key = Ui.Label(entry.Key, keyColor ?? Ui.Muted, 11);
                key.Width = keyWidth;
                ToolTip.SetTip(key, entry.Key);
                var value = new SelectableTextBlock { Text = entry.Value, Foreground = valueColor ?? Ui.TextBright, FontSize = 11, TextWrapping = TextWrapping.Wrap };
                block.Children.Add(Ui.Dock((key, Dock.Left), (value, Dock.Left)));
                key.Margin = new Thickness(0, 0, 6, 0);
            }
            column.Children.Add(block);
        }
        return column;
    }
}

/// <summary>
/// A picture to click: a click, a double-click, the keys while it has the focus, and a drag of its file inside the chat.
/// Selected, it gets an edge in the accent colour.
/// </summary>
public sealed class PictureTile : Border
{
    private bool _selected;
    private PointerPressedEventArgs? _press;
    private Point _pressAt;
    private bool _dragging;

    public PictureTile()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        Refresh();
    }

    /// <summary>A click (the first press).</summary>
    public event Action? Clicked;

    /// <summary>A double-click.</summary>
    public event Action? DoubleClicked;

    /// <summary>A click released where it was pressed (not the start of a drag).</summary>
    public event Action? ClickReleased;

    /// <summary>A right click.</summary>
    public event Action? RightClicked;

    /// <summary>A key while the tile has the focus; true: used.</summary>
    public Func<KeyEventArgs, bool>? KeyPressed { get; set; }

    /// <summary>The file dragged from the tile (none: no drag).</summary>
    public Func<string?>? DragPath { get; set; }

    /// <summary>Raised when the file was taken by a drop.</summary>
    public event Action<string>? Dropped;

    /// <summary>False: no frame (a large preview).</summary>
    public bool Framed { get; set; } = true;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
                return;
            _selected = value;
            Refresh();
        }
    }

    public void Refresh()
    {
        if (!Framed)
        {
            BorderThickness = new Thickness(0);
            Padding = new Thickness(0);
            return;
        }
        Background = Ui.Field;
        BorderBrush = _selected ? Ui.Accent : Ui.Edge;
        BorderThickness = new Thickness(_selected ? 2 : 1);
        Padding = new Thickness(_selected ? 4 : 5);
        CornerRadius = new CornerRadius(2);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            RightClicked?.Invoke();
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed)
            return;
        Focus();
        if (e.ClickCount >= 2)
        {
            _press = null;
            DoubleClicked?.Invoke();
        }
        else
        {
            _press = e;
            _pressAt = e.GetPosition(this);
            Clicked?.Invoke();
        }
        e.Handled = true;
    }

    protected override async void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_press is not { } press || _dragging || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var moved = e.GetPosition(this) - _pressAt;
        if (Math.Abs(moved.X) + Math.Abs(moved.Y) < 6)
            return;
        _press = null;
        if (DragPath?.Invoke() is not { Length: > 0 } path)
            return;
        _dragging = true;
        try
        {
            var result = await DragDrop.DoDragDropAsync(press, ImageKit.DragData([path]), DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            if (result != DragDropEffects.None)
                Dropped?.Invoke(path);
        }
        finally
        {
            _dragging = false;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_press is not null && e.InitialPressMouseButton == MouseButton.Left)
        {
            var moved = e.GetPosition(this) - _pressAt;
            if (Math.Abs(moved.X) + Math.Abs(moved.Y) < 6)
                ClickReleased?.Invoke();
        }
        _press = null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && KeyPressed?.Invoke(e) == true)
            e.Handled = true;
    }
}
