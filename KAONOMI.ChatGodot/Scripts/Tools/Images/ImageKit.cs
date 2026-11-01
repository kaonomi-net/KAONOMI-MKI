using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Images;

/// <summary>
/// The pieces shared by the views of the pictures: Avalonia bitmaps of the view models turned into Godot textures off
/// the main thread, the opening of a picture in the image analysis window, the keys of a window, the files dropped on
/// the chat, the drag data of a picture.
/// </summary>
public static class ImageKit
{
    // ── Bitmaps → textures ──

    /// <summary>
    /// The pixels of <paramref name="bitmap"/> copied now (on the calling thread: the view model may release the bitmap
    /// right after), then turned into a Godot image in the background. Null when it cannot be read.
    /// </summary>
    public static Task<Image?> ToImageAsync(Avalonia.Media.Imaging.Bitmap? bitmap)
    {
        if (bitmap is null)
            return Task.FromResult<Image?>(null);
        ImagePixels pixels;
        try
        {
            pixels = ImagePixels.From(bitmap);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or NullReferenceException or ArgumentException or NotSupportedException)
        {
            return Task.FromResult<Image?>(null);
        }
        return Task.Run(() => ToImage(pixels));
    }

    /// <summary>Premultiplied BGRA pixels as a Godot RGBA image.</summary>
    public static Image? ToImage(ImagePixels pixels)
    {
        if (pixels.Width <= 0 || pixels.Height <= 0)
            return null;
        var data = (byte[])pixels.Data.Clone();
        for (var i = 0; i < data.Length; i += 4)
        {
            var b = data[i];
            var r = data[i + 2];
            var a = data[i + 3];
            if (a is > 0 and < 255)
            {
                r = (byte)Math.Min(255, r * 255 / a);
                b = (byte)Math.Min(255, b * 255 / a);
                data[i + 1] = (byte)Math.Min(255, data[i + 1] * 255 / a);
            }
            data[i] = r;
            data[i + 2] = b;
        }
        return Image.CreateFromData(pixels.Width, pixels.Height, false, Image.Format.Rgba8, data);
    }

    /// <summary>
    /// Shows in <paramref name="rect"/> the bitmap of the property <paramref name="name"/> of <paramref name="source"/>,
    /// converted in the background whenever it changes (the last one wins); <paramref name="shown"/> is told the texture.
    /// </summary>
    public static TextureRect BindBitmap(TextureRect rect, INotifyPropertyChanged source, string name, Action<ImageTexture?>? shown = null)
    {
        var version = 0;
        Bind.When(rect, source, async () =>
        {
            var current = ++version;
            var bitmap = Bind.Get(source, name) as Avalonia.Media.Imaging.Bitmap;
            if (bitmap is null)
            {
                rect.Texture = null;
                shown?.Invoke(null);
                return;
            }
            var image = await ToImageAsync(bitmap);
            Bind.OnMain(() =>
            {
                if (current != version || !GodotObject.IsInstanceValid(rect))
                    return;
                var texture = image is null ? null : Update(rect.Texture as ImageTexture, image);
                rect.Texture = texture;
                shown?.Invoke(texture);
            });
        }, name);
        return rect;
    }

    /// <summary><paramref name="texture"/> updated with <paramref name="image"/> when it has its size, else a new texture.</summary>
    public static ImageTexture Update(ImageTexture? texture, Image image)
    {
        if (texture is not null && texture.GetWidth() == image.GetWidth() && texture.GetHeight() == image.GetHeight())
        {
            texture.Update(image);
            return texture;
        }
        return ImageTexture.CreateFromImage(image);
    }

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

    // ── Keys ──

    /// <summary>
    /// Calls <paramref name="handle"/> with the keys pressed while the window of <paramref name="root"/> is in use: in
    /// front, and the keyboard in it (or nowhere, the chat having none). True from it: the key is used.
    /// </summary>
    public static void Keys(Control root, Shell shell, Func<InputEventKey, bool> handle) =>
        root.AddChild(new KeyCatcher(root, shell, handle));

    /// <summary>True when the key of the shortcut of the system is held: ⌘ on macOS, Ctrl elsewhere.</summary>
    public static bool Command(InputEventKey key) => OperatingSystem.IsMacOS() ? key.MetaPressed || key.CtrlPressed : key.CtrlPressed;

    /// <summary>True when the window of <paramref name="root"/> is in front.</summary>
    public static bool IsFront(Control root, Shell shell) => shell.WindowOf(root) is { IsFront: true };

    /// <summary>True when the window of <paramref name="root"/> is the active one of the deck and the chat is not being typed in (<see cref="Shell.HasKeyboard"/>).</summary>
    public static bool HasKeyboard(Control root, Shell shell) => shell.HasKeyboard(root);

    // ── Files dropped on the chat ──

    /// <summary>
    /// Calls <paramref name="dropped"/> with the files dropped on the chat (from the Finder / Explorer) while the window
    /// of <paramref name="root"/> is in front (Godot gives the files dropped on its window, not where).
    /// </summary>
    public static void FilesDropped(Control root, Shell shell, Action<string[]> dropped)
    {
        void Handler(string[] files)
        {
            if (GodotObject.IsInstanceValid(root) && IsFront(root, shell))
                dropped(files);
        }
        // The window of the chat, once (the view leaves and enters the tree each time its window goes back or front).
        var window = shell.Root.GetTree().Root;
        window.FilesDropped += Handler;
        Bind.OnGone(root, () =>
        {
            if (GodotObject.IsInstanceValid(window))
                window.FilesDropped -= Handler;
        });
    }

    // ── Drag and drop of pictures ──

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".heif", ".tif", ".tiff"
    };

    /// <summary>True when <paramref name="path"/> has the extension of a picture.</summary>
    public static bool IsPicture(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// The drag data of pictures inside the chat: the format of the files dragged in Godot
    /// (<c>{"type": "files", "files": [...]}</c>), with their paths as text.
    /// </summary>
    public static Godot.Collections.Dictionary DragData(IEnumerable<string> paths)
    {
        var list = paths.ToArray();
        return new Godot.Collections.Dictionary
        {
            ["type"] = "files",
            ["files"] = list,
            ["text"] = string.Join('\n', list)
        };
    }

    /// <summary>The local paths of the pictures in dragged data (files, or paths given as text).</summary>
    public static IReadOnlyList<string> PicturePaths(Variant data)
    {
        var paths = new List<string>();
        if (data.VariantType == Variant.Type.Dictionary)
        {
            var dictionary = data.AsGodotDictionary();
            if (dictionary.TryGetValue("files", out var files))
                paths.AddRange(files.AsStringArray());
            else if (dictionary.TryGetValue("text", out var text))
                paths.AddRange(text.AsString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        else if (data.VariantType is Variant.Type.String)
            paths.AddRange(data.AsString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        else if (data.VariantType is Variant.Type.PackedStringArray)
            paths.AddRange(data.AsStringArray());
        return paths.Where(p => IsPicture(p) && File.Exists(p)).Distinct().ToList();
    }

    /// <summary>
    /// The text of an image line of the journal / the calculator tape for <paramref name="path"/>:
    /// <c>![name|width](path)</c> (as <c>JournalMarkdown.ImageMarkdown</c> of the Avalonia chat).
    /// </summary>
    public static string ImageMarkdown(string path, int width = 480)
    {
        var alt = Path.GetFileNameWithoutExtension(path).Replace("]", string.Empty).Replace("|", string.Empty);
        var link = path.Contains(' ') || path.Contains(')') ? $"<{path}>" : path;
        return $"![{alt}|{width}]({link})";
    }

    /// <summary>
    /// Inserts the image lines of <paramref name="paths"/> at the start of the line holding <paramref name="index"/>
    /// (after the text when the index is at its end); returns the text and the position after the lines (as
    /// <c>PictureDragDrop.Insert</c> of the Avalonia chat).
    /// </summary>
    public static (string Text, int Caret) InsertImages(string text, int index, IReadOnlyList<string> paths)
    {
        index = Math.Clamp(index, 0, text.Length);
        var lines = string.Join('\n', paths.Select(p => ImageMarkdown(p))) + "\n";
        int at;
        if (index == text.Length)
        {
            at = text.Length;
            if (text.Length > 0 && text[^1] != '\n')
                lines = "\n" + lines;
        }
        else
            at = index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
        return (text[..at] + lines + text[at..], at + lines.Length);
    }

    /// <summary>
    /// Lets <paramref name="box"/> receive pictures dragged inside the chat (from /pictures or /fooocus):
    /// <paramref name="apply"/> gets the new text, the caret after the image lines (index in the text) and the number of
    /// pictures inserted. The pictures go to the start of the line under the pointer.
    /// </summary>
    public static void AcceptPictures(TextEdit box, Action<string, int, int> apply)
    {
        box.SetDragForwarding(
            Callable.From<Vector2, Variant>(_ => default),
            Callable.From<Vector2, Variant, bool>((_, data) => PicturePaths(data).Count > 0),
            Callable.From<Vector2, Variant>((at, data) =>
            {
                var paths = PicturePaths(data);
                if (paths.Count == 0)
                    return;
                var position = box.GetLineColumnAtPos(new Vector2I((int)at.X, (int)at.Y));
                var index = 0;
                for (var line = 0; line < position.Y && line < box.GetLineCount(); line++)
                    index += box.GetLine(line).Length + 1;
                index += position.X;
                var (text, caret) = InsertImages(box.Text, index, paths);
                apply(text, caret, paths.Count);
            }));
    }

    // ── Small controls ──

    /// <summary>A group of the toolbar: a box with a hairline edge around <paramref name="children"/>.</summary>
    public static PanelContainer Group(params Control[] children)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Raised(0.02f), Eve.Eve.Edge with { A = 0.35f }, 3));
        panel.AddChild(Ui.Row(3, children));
        return panel;
    }

    /// <summary>A card of the side panel: a box with a caption above <paramref name="children"/>.</summary>
    public static PanelContainer Card(string? caption, params Control[] children)
    {
        var panel = new PanelContainer();
        var box = Eve.Eve.Box(Eve.Eve.Sunken(0.2f), Eve.Eve.Edge with { A = 0.35f }, 8);
        box.ContentMarginLeft = box.ContentMarginRight = 10;
        panel.AddThemeStyleboxOverride("panel", box);
        var column = Ui.Column(6);
        if (caption is not null)
            column.AddChild(Eve.Eve.Caption(caption, Eve.Eve.Amber));
        foreach (var child in children)
            column.AddChild(child);
        panel.AddChild(column);
        return panel;
    }

    /// <summary>A toggle button bound both ways to the bool property <paramref name="name"/>.</summary>
    public static Button Toggle(string text, INotifyPropertyChanged source, string name, string tooltip = "")
    {
        var button = Eve.Eve.Button(text, tooltip: tooltip);
        return Bind.Check(button, source, name);
    }

    /// <summary>
    /// The groups of metadata of a picture (the name of each group, then its keys and values). The keys and values take
    /// the colours of the theme, or <paramref name="keyColor"/> / <paramref name="valueColor"/> when the panel under
    /// them has a fixed colour (the dark overlay over a picture).
    /// </summary>
    public static Control Metadata(IEnumerable<MetadataGroup> groups, Color groupColor, int keyWidth = 140, Color? keyColor = null, Color? valueColor = null)
    {
        var column = Ui.Column(8);
        foreach (var group in groups)
        {
            var block = Ui.Column(1, Eve.Eve.Text_(group.Name, groupColor, 12));
            foreach (var entry in group.Entries)
            {
                var key = Eve.Eve.Text_(entry.Key, keyColor ?? Eve.Eve.Muted, 11);
                key.CustomMinimumSize = new Vector2(keyWidth, 0);
                key.ClipText = true;
                key.TooltipText = entry.Key;
                key.MouseFilter = Control.MouseFilterEnum.Pass;
                var value = Eve.Eve.Text_(entry.Value, valueColor ?? Eve.Eve.TextBright, 11, wrap: true);
                value.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
                block.AddChild(Ui.Row(6, key, value));
            }
            column.AddChild(block);
        }
        return column;
    }
}

/// <summary>Receives the keys of a window for its view (see <see cref="ImageKit.Keys"/>).</summary>
public partial class KeyCatcher : Control
{
    private readonly Control? _root;
    private readonly Shell? _shell;
    private readonly Func<InputEventKey, bool>? _handle;

    public KeyCatcher()
    {
    }

    public KeyCatcher(Control root, Shell shell, Func<InputEventKey, bool> handle)
    {
        _root = root;
        _shell = shell;
        _handle = handle;
        MouseFilter = MouseFilterEnum.Ignore;
        Size = Vector2.Zero;
    }

    public override void _ShortcutInput(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } key || _root is null || _shell is null || _handle is null)
            return;
        // The active window of the deck only (every window receives every key), and not while the chat is typed in.
        if (!ImageKit.HasKeyboard(_root, _shell))
            return;
        // The keyboard is in this window, or nowhere (not in the chat).
        var focus = GetViewport().GuiGetFocusOwner();
        var inside = focus is not null && _root.IsAncestorOf(focus);
        var nowhere = focus is null && _shell.Root.GetViewport().GuiGetFocusOwner() is null;
        if ((inside || nowhere) && _handle(key))
            GetViewport().SetInputAsHandled();
    }
}

/// <summary>
/// A picture to click: a click, a double-click, the keys while it has the focus, and a drag of its file inside the chat
/// (the format of the files dragged in Godot). Selected, it gets a cyan edge.
/// </summary>
public partial class PictureTile : PanelContainer
{
    private bool _selected;

    public PictureTile()
    {
        FocusMode = FocusModeEnum.All;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        Refresh();
    }

    /// <summary>A click (the first press).</summary>
    public event Action? Clicked;

    /// <summary>A double-click.</summary>
    public event Action? DoubleClicked;

    /// <summary>A click released where it was pressed (not the start of a drag).</summary>
    public event Action? Tapped;

    private Vector2? _press;

    /// <summary>A key while the tile has the focus; true: used.</summary>
    public Func<InputEventKey, bool>? KeyPressed { get; set; }

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
            AddThemeStyleboxOverride("panel", new StyleBoxEmpty());
            return;
        }
        var box = _selected
            ? Eve.Eve.Box(Eve.Eve.Cyan with { A = 0.16f }, Eve.Eve.Cyan, 4, 2)
            : Eve.Eve.Box(Eve.Eve.Sunken(0.25f), Eve.Eve.Edge with { A = 0.3f }, 4);
        if (!_selected)
            box.SetContentMarginAll(5);
        AddThemeStyleboxOverride("panel", box);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } click)
        {
            GrabFocus();
            _press = click.DoubleClick ? null : click.Position;
            if (click.DoubleClick)
                DoubleClicked?.Invoke();
            else
                Clicked?.Invoke();
            AcceptEvent();
        }
        else if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release)
        {
            if (_press is { } start && start.DistanceTo(release.Position) < 6)
                Tapped?.Invoke();
            _press = null;
        }
        else if (e is InputEventKey { Pressed: true } key && KeyPressed?.Invoke(key) == true)
            AcceptEvent();
    }

    public override Variant _GetDragData(Vector2 atPosition)
    {
        _press = null;
        if (DragPath?.Invoke() is not { Length: > 0 } path)
            return default;
        var preview = Eve.Eve.Text_("🖼 " + System.IO.Path.GetFileName(path), Eve.Eve.TextBright, 12);
        SetDragPreview(preview);
        return ImageKit.DragData([path]);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationDragEnd && IsDragSuccessful() && DragPath?.Invoke() is { } path)
            Dropped?.Invoke(path);
    }
}

/// <summary>A panel telling whether the pointer is over it (its children included).</summary>
public partial class HoverPanel : PanelContainer
{
    private bool _hovered;

    public HoverPanel()
    {
        MouseEntered += () => SetHovered(true);
        MouseExited += () => SetHovered(false);
    }

    /// <summary>Raised when the pointer comes over the panel or leaves it.</summary>
    public event Action<bool>? HoverChanged;

    /// <summary>A double-click on the panel (or on a child letting it through).</summary>
    public event Action? DoubleClicked;

    public bool Hovered => _hovered;

    private void SetHovered(bool over)
    {
        // Entering a child raises an exit then an enter: the pointer is still over the panel.
        over = over || new Rect2(Vector2.Zero, Size).HasPoint(GetLocalMousePosition());
        if (over == _hovered)
            return;
        _hovered = over;
        HoverChanged?.Invoke(over);
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true, DoubleClick: true })
        {
            DoubleClicked?.Invoke();
            AcceptEvent();
        }
    }
}
