using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Views;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Work;

/// <summary>
/// WYSIWYG editor of the tape of /work, the port of the TapeEditor of the Godot chat: the document
/// (<see cref="TapeDocument"/>) is laid out and drawn by this control itself (a TextBox has neither formatting to the
/// character nor pictures inside the text). Text formatted to the character (font, size, bold, italic, underline,
/// strikethrough, color, link) in paragraphs (left, centered, right; Normal, Title, Subtitle, Heading 1 to 3), pictures
/// as blocks (resizable by the grip of their corner, croppable), blocks of code in the syntax coloring of their
/// language. The mouse and the keyboard work as in a word processor: caret, selection (Shift, drag, double-click word,
/// triple-click paragraph), Backspace / Delete (Alt / Ctrl for words), Enter, copy / cut / paste (the formatting kept
/// inside the application), undo / redo, Ctrl / ⌘ + B, I, U, Ctrl / ⌘ + Alt + 0…3; Ctrl / ⌘ + click opens a link, a
/// double-click opens a picture or edits a block of code. <see cref="Text"/> is the tape in its file format, written
/// back (<see cref="TextCommitted"/>) after each change. Put it in a <see cref="ScrollViewer"/>: it is as high as its
/// document.
/// </summary>
public sealed class TapeEditor : Control
{
    public const string RewriteSpelling = "spelling";
    public const string RewriteReadability = "readability";

    /// <summary>Languages of 🌐 Translate: the name given to the LLM and the one shown in the menu.</summary>
    public static readonly (string Language, string Label)[] TranslationLanguages =
        [("English", "English 🇬🇧"), ("German", "Deutsch 🇩🇪"), ("Dutch", "Nederlands 🇳🇱")];

    /// <summary>Size of the text without a size of its own.</summary>
    public const double DefaultSize = 13;

    private const double PaddingX = 14;
    private const double PaddingY = 10;
    private const double ImageMargin = 6;
    private const double GripSize = 12;
    private const int MinImageWidth = 60;
    private const double CodePadding = 10;
    private const double CodeLabelHeight = 14;
    private const double CropHandleSize = 9;
    private const double MinCropPixels = 12;

    private static readonly IBrush CaretBrush = Ui.Accent;
    private static readonly IBrush SelectionBrush = new SolidColorBrush(Color.FromRgb(30, 70, 84));
    private static readonly IBrush InactiveSelectionBrush = new SolidColorBrush(Color.FromRgb(40, 46, 52));
    private static readonly IBrush GripBrush = Ui.Accent;
    private static readonly IBrush PlaceholderBrush = Ui.Muted;
    private static readonly IBrush CodeBackground = new SolidColorBrush(Color.FromRgb(12, 16, 20));
    private static readonly IPen CodeBorder = new Pen(Ui.Edge, 1);
    private static readonly IPen SelectedPen = new Pen(Ui.Accent, 2);
    private static readonly IBrush CropShade = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    private static readonly IPen CropPen = new Pen(Brushes.White, 1.5);
    private static readonly IPen ThirdsPen = new Pen(new SolidColorBrush(Color.FromRgb(170, 170, 170)), 0.75);
    private static readonly IPen HandlePen = new Pen(Ui.Accent, 1.5);

    private static readonly Cursor IBeam = new(StandardCursorType.Ibeam);
    private static readonly Cursor Wait = new(StandardCursorType.Wait);
    private static readonly Cursor Hand = new(StandardCursorType.Hand);
    private static readonly Cursor Arrow = new(StandardCursorType.Arrow);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor WestEast = new(StandardCursorType.SizeWestEast);
    private static readonly Cursor NorthSouth = new(StandardCursorType.SizeNorthSouth);
    private static readonly Cursor TopLeft = new(StandardCursorType.TopLeftCorner);
    private static readonly Cursor TopRight = new(StandardCursorType.TopRightCorner);
    private static readonly Cursor BottomRight = new(StandardCursorType.BottomRightCorner);

    /// <summary>A selection kept while a long task runs on it: its ends and the tape at that time.</summary>
    public sealed class TapeSelection
    {
        internal TapeSelection(TapePosition start, TapePosition end, string text) => (Start, End, Text) = (start, end, text);

        internal TapePosition Start { get; }
        internal TapePosition End { get; }
        internal string Text { get; }
    }

    /// <summary>A place in the tape: a block and a caret offset in it (0 or 1 around a picture or a block of code).</summary>
    internal readonly record struct TapePosition(int Block, int Offset) : IComparable<TapePosition>
    {
        public int CompareTo(TapePosition other) => Block != other.Block ? Block.CompareTo(other.Block) : Offset.CompareTo(other.Offset);

        public static bool operator <(TapePosition a, TapePosition b) => a.CompareTo(b) < 0;

        public static bool operator >(TapePosition a, TapePosition b) => a.CompareTo(b) > 0;
    }

    [Flags]
    private enum CropHandle
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8,
        Move = 16
    }

    // ── What the view gives and listens to ──

    /// <summary>Text shown while the tape is empty.</summary>
    public string Placeholder { get; set; } = string.Empty;

    /// <summary>
    /// True for a text that is Markdown written elsewhere (the journal): the blocks left unchanged are written back exactly
    /// as they were read (<see cref="TapeDocument.KeepSource"/>), the changed ones in the format of the tape. Set it before
    /// <see cref="Text"/>.
    /// </summary>
    public bool KeepSource { get; set; }

    /// <summary>
    /// True: a plain click on a link opens it (released where it was pressed, without dragging), as in a wiki; false:
    /// only ⌘/Ctrl + click opens it, as in /work and the journal.
    /// </summary>
    public bool LinksOpenOnClick { get; set; }

    /// <summary>The link under a plain press (<see cref="LinksOpenOnClick"/>), opened when the button is released there.</summary>
    private (string Address, Point At)? _pressedLink;

    /// <summary>Loads the picture of a local path (null when it cannot be read).</summary>
    public Func<string, Task<Bitmap?>>? LoadImage { get; set; }

    /// <summary>Opens a picture (double-click).</summary>
    public Action<string>? OpenImage { get; set; }

    /// <summary>Opens the address of a link (Ctrl / ⌘ + click).</summary>
    public Action<string>? OpenLink { get; set; }

    /// <summary>Crops a picture: its local path and the part kept (0 to 1); returns the path of the cropped copy or null.</summary>
    public Func<string, Rect, Task<string?>>? CropImage { get; set; }

    /// <summary>Raised after each change with the tape in its file format.</summary>
    public event Action<string>? TextCommitted;

    /// <summary>Raised when the caret or the selection moves, or the formatting changes (the toolbar follows).</summary>
    public event Action? SelectionChanged;

    /// <summary>Raised when the crop frame shows or goes away.</summary>
    public event Action? CropChanged;

    /// <summary>A double-click on a block of code: its language, its code, and the action that replaces them (false when the tape changed meanwhile).</summary>
    public event Action<string, string, Func<string, string, bool>>? EditCodeRequested;

    /// <summary>A right click, at this point of the editor: the view shows the menu.</summary>
    public event Action<Point>? ContextMenuRequested;

    /// <summary>Ctrl / ⌘ + wheel: +1 zooms in, -1 zooms out.</summary>
    public event Action<int>? ZoomRequested;

    // ── State ──

    private TapeDocument _document = TapeDocument.Parse(string.Empty);
    private string _lastText = string.Empty;
    private TapePosition _caret;
    private TapePosition _anchor;
    private TapeStyle? _pendingStyle;
    private double? _desiredX;
    private double _zoom = 1;
    private bool _busy;

    private readonly List<BlockLayout> _layouts = [];
    private double _layoutWidth = -1;
    private double _availableWidth = -1;
    private bool _layoutDirty = true;
    private readonly Dictionary<string, List<TextLine>> _paragraphCache = new();

    private readonly Dictionary<string, Bitmap?> _images = new();
    private readonly HashSet<string> _loading = [];

    private readonly List<(string Text, TapePosition Caret)> _undo = [];
    private readonly List<(string Text, TapePosition Caret)> _redo = [];
    private DateTime _lastTyping;
    private bool _typing;

    private bool _selecting;
    private (int Block, double StartX, int StartWidth)? _resizing;
    private (int Block, Rect Part)? _crop;
    private (CropHandle Handle, Point Start, Rect Part)? _cropDrag;

    /// <summary>The last copy, to paste it with its formatting (the clipboard of the system gets its text).</summary>
    private static (string Plain, string Rich)? _clip;

    /// <summary>True until the caret was shown once after the tape was read (the editor may have no size yet).</summary>
    private bool _revealCaret;

    public TapeEditor()
    {
        Focusable = true;
        Cursor = IBeam;
        ClipToBounds = true;
        GotFocus += (_, _) => InvalidateVisual();
        LostFocus += (_, _) => InvalidateVisual();
    }

    // ── Text ──

    /// <summary>The tape, in the format of <c>calculator-tape.txt</c>. Set from outside, it is read again (caret at the end, undo emptied).</summary>
    public string Text
    {
        get => _lastText;
        set
        {
            value ??= string.Empty;
            if (value == _lastText)
                return;
            _lastText = value;
            _crop = null;
            _cropDrag = null;
            _document = TapeDocument.Parse(value, KeepSource);
            _caret = _anchor = DocumentEnd;
            _pendingStyle = null;
            _undo.Clear();
            _redo.Clear();
            _revealCaret = true;
            Relayout();
            WorkUi.Later(BringCaretIntoView);
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>The zoom of the page (text, pictures, code).</summary>
    public double Zoom
    {
        get => _zoom;
        set
        {
            if (Math.Abs(value - _zoom) < 0.0001)
                return;
            _zoom = value;
            _paragraphCache.Clear();
            Relayout();
            WorkUi.Later(BringCaretIntoView);
        }
    }

    /// <summary>True while a long task runs on the selection: the pointer shows the wait cursor.</summary>
    public bool IsBusy
    {
        get => _busy;
        set
        {
            _busy = value;
            Cursor = value ? Wait : IBeam;
        }
    }

    private bool HasSelection => _caret != _anchor;

    /// <summary>True when something is selected.</summary>
    public bool HasSelectedContent => HasSelection;

    private (TapePosition Start, TapePosition End) Selection => _caret < _anchor ? (_caret, _anchor) : (_anchor, _caret);

    private TapePosition DocumentEnd => new(_document.Blocks.Count - 1, _document.Blocks[^1].Length);

    private TapePosition Clamp(TapePosition position)
    {
        var block = Math.Clamp(position.Block, 0, _document.Blocks.Count - 1);
        return new TapePosition(block, Math.Clamp(position.Offset, 0, _document.Blocks[block].Length));
    }

    /// <summary>The selected text, without its formatting: one line per paragraph, pictures as their name.</summary>
    public string SelectedText => string.Join("\n", SelectedBlocks().Select(b => b switch
    {
        TapeParagraph p => p.Text,
        TapeImage i => $"[picture: {i.Alt}]",
        TapeCode c => c.Code,
        _ => string.Empty
    })).Trim();

    /// <summary>The current selection, kept for <see cref="ReplaceSelection"/> or <see cref="InsertAfter"/>.</summary>
    public TapeSelection CaptureSelection() => new(Selection.Start, Selection.End, _document.Serialize());

    /// <summary>Replaces the text of <paramref name="selection"/> (one paragraph per line); false when the tape changed since.</summary>
    public bool ReplaceSelection(TapeSelection selection, string text)
    {
        if (selection.Text != _document.Serialize())
            return false;
        Remember();
        _anchor = Clamp(selection.Start);
        _caret = Clamp(selection.End);
        InsertBlocks(PlainParagraphs(text, CurrentStyleForTyping()));
        Commit();
        Focus();
        return true;
    }

    /// <summary>Adds <paramref name="text"/> as new paragraphs after the paragraph where <paramref name="selection"/> ends.</summary>
    public bool InsertAfter(TapeSelection selection, string text)
    {
        if (selection.Text != _document.Serialize())
            return false;
        Remember();
        var end = Clamp(selection.End);
        var index = end.Block + 1;
        var blocks = PlainParagraphs(text, TapeStyle.Plain);
        _document.Blocks.InsertRange(index, blocks);
        _anchor = new TapePosition(index, 0);
        _caret = new TapePosition(index + blocks.Count - 1, blocks[^1].Length);
        Commit();
        Focus();
        return true;
    }

    private static List<TapeBlock> PlainParagraphs(string text, TapeStyle style) =>
        text.Replace("\r\n", "\n").Split('\n').Select(line =>
        {
            var paragraph = TapeDocument.EmptyParagraph(style);
            paragraph.Insert(0, line, style);
            return (TapeBlock)paragraph;
        }).ToList();

    private void Commit()
    {
        _lastText = _document.Serialize();
        TextCommitted?.Invoke(_lastText);
        Relayout();
        BringCaretIntoView();
        SelectionChanged?.Invoke();
    }

    private void Remember(bool typing = false)
    {
        var now = DateTime.UtcNow;
        if (typing && _typing && now - _lastTyping < TimeSpan.FromSeconds(1.5))
        {
            _lastTyping = now;
            return;
        }
        _typing = typing;
        _lastTyping = now;
        _undo.Add((_document.Serialize(), _caret));
        if (_undo.Count > 200)
            _undo.RemoveAt(0);
        _redo.Clear();
    }

    public void Undo() => Restore(_undo, _redo);

    public void Redo() => Restore(_redo, _undo);

    private void Restore(List<(string Text, TapePosition Caret)> from, List<(string Text, TapePosition Caret)> to)
    {
        if (from.Count == 0)
            return;
        to.Add((_document.Serialize(), _caret));
        var (text, caret) = from[^1];
        from.RemoveAt(from.Count - 1);
        _document = TapeDocument.Parse(text, KeepSource);
        _caret = _anchor = Clamp(caret);
        _typing = false;
        _pendingStyle = null;
        Commit();
    }

    /// <summary>Replaces the whole tape (a document opened, a JSON imported), as an edit that Ctrl / ⌘ + Z undoes.</summary>
    public void ReplaceAll(string text)
    {
        Remember();
        CancelCrop();
        _document = TapeDocument.Parse(text, KeepSource);
        _caret = _anchor = new TapePosition(0, 0);
        _pendingStyle = null;
        Commit();
        Focus();
    }

    // ── Formatting (toolbar) ──

    /// <summary>Style at the caret, or of the first character of the selection.</summary>
    public TapeStyle CurrentStyle
    {
        get
        {
            if (!HasSelection && _pendingStyle is not null)
                return _pendingStyle;
            var (start, end) = Selection;
            for (var block = start.Block; block <= end.Block; block++)
                if (_document.Blocks[block] is TapeParagraph paragraph)
                {
                    var offset = block == start.Block ? start.Offset : 0;
                    if (HasSelection && offset < paragraph.Length)
                        return paragraph.StyleAt(offset + 1);
                    if (!HasSelection)
                        return paragraph.StyleAt(offset);
                }
            return TapeStyle.Plain;
        }
    }

    /// <summary>The formatting shown at the caret: <see cref="CurrentStyle"/> with the one of its paragraph style.</summary>
    public TapeStyle EffectiveStyle => _document.Blocks[Selection.Start.Block] is TapeParagraph paragraph
        ? paragraph.Effective(CurrentStyle)
        : CurrentStyle;

    public TapeParagraphKind CurrentParagraphKind =>
        _document.Blocks[Selection.Start.Block] is TapeParagraph paragraph ? paragraph.Kind : TapeParagraphKind.Normal;

    public void SetParagraphKind(TapeParagraphKind kind)
    {
        Remember();
        var (start, end) = Selection;
        for (var block = start.Block; block <= end.Block; block++)
            if (_document.Blocks[block] is TapeParagraph paragraph)
                paragraph.Kind = kind;
        Commit();
        Focus();
    }

    public TapeAlignment CurrentAlignment =>
        _document.Blocks[_caret.Block] switch
        {
            TapeParagraph paragraph => paragraph.Alignment,
            TapeImage image => image.Alignment,
            _ => TapeAlignment.Left
        };

    /// <summary>Changes the style of the selection, or of the text typed next when nothing is selected.</summary>
    public void ApplyStyle(Func<TapeStyle, TapeStyle> change)
    {
        if (!HasSelection)
        {
            _pendingStyle = change(CurrentStyle);
            if (_document.Blocks[_caret.Block] is TapeParagraph { Length: 0 } empty)
            {
                Remember();
                empty.Runs[0].Style = _pendingStyle;
                Commit();
            }
            SelectionChanged?.Invoke();
            Focus();
            return;
        }
        Remember();
        var (start, end) = Selection;
        for (var block = start.Block; block <= end.Block; block++)
            if (_document.Blocks[block] is TapeParagraph paragraph)
                paragraph.Restyle(block == start.Block ? start.Offset : 0, block == end.Block ? end.Offset : paragraph.Length, change);
        Commit();
        Focus();
    }

    public void SetAlignment(TapeAlignment alignment)
    {
        Remember();
        var (start, end) = Selection;
        for (var block = start.Block; block <= end.Block; block++)
            if (_document.Blocks[block] is TapeParagraph paragraph)
                paragraph.Alignment = alignment;
            else if (_document.Blocks[block] is TapeImage image)
                image.Alignment = alignment;
        Commit();
        Focus();
    }

    public string? CurrentLink => CurrentStyle.Link;

    /// <summary>With nothing selected and the caret in a link, selects the whole link; true when something is selected afterwards.</summary>
    public bool SelectLinkAtCaret()
    {
        if (HasSelection)
            return true;
        if (_document.Blocks[_caret.Block] is not TapeParagraph paragraph)
            return false;
        var range = paragraph.LinkRange(_caret.Offset) ?? paragraph.LinkRange(_caret.Offset - 1);
        if (range is not { } link)
            return false;
        _anchor = new TapePosition(_caret.Block, link.Start);
        _caret = new TapePosition(_caret.Block, link.End);
        Changed();
        return true;
    }

    /// <summary>Types <paramref name="text"/> at the caret in the style there (the selection replaced): a dictation, for one.</summary>
    public void InsertPlainText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        Remember();
        InsertText(text);
        _desiredX = null;
        Commit();
    }

    /// <summary>Types <paramref name="text"/> at the caret as a link to <paramref name="address"/> (the selection replaced).</summary>
    public void InsertLink(string text, string address)
    {
        var link = TapeStyle.NormalizeLink(address);
        if (link is null || string.IsNullOrEmpty(text))
            return;
        Remember();
        var style = (_pendingStyle ?? CurrentStyleForTyping()) with { Link = link };
        InsertText(text, style);
        _pendingStyle = style with { Link = null };
        Commit();
        Focus();
    }

    /// <summary>Makes the selection a link to <paramref name="address"/> (null removes it); with nothing selected, the address is typed as a link.</summary>
    public void SetLink(string? address)
    {
        var link = TapeStyle.NormalizeLink(address);
        if (HasSelection)
        {
            ApplyStyle(s => s with { Link = link });
            return;
        }
        if (link is null)
            return;
        Remember();
        var style = (_pendingStyle ?? CurrentStyleForTyping()) with { Link = link };
        InsertText(address!.Trim(), style);
        _pendingStyle = style with { Link = null };
        Commit();
        Focus();
    }

    // ── Crop ──

    public bool IsImageSelected
    {
        get
        {
            var (start, end) = Selection;
            return start.Block == end.Block && start.Offset == 0 && end.Offset == 1 && _document.Blocks[start.Block] is TapeImage;
        }
    }

    public bool IsCropping => _crop is not null;

    public void StartCrop()
    {
        EnsureLayout();
        if (!IsImageSelected || _layouts.Count <= Selection.Start.Block || _layouts[Selection.Start.Block].Image is null)
            return;
        _crop = (Selection.Start.Block, new Rect(0, 0, 1, 1));
        Focus();
        InvalidateVisual();
        CropChanged?.Invoke();
    }

    public void CancelCrop()
    {
        if (_crop is null)
            return;
        _crop = null;
        _cropDrag = null;
        InvalidateVisual();
        CropChanged?.Invoke();
    }

    /// <summary>Crops the picture to the frame: a cropped copy takes its place, at the same scale (undo brings the whole picture back).</summary>
    public async void ApplyCrop()
    {
        if (_crop is not { } crop || _document.Blocks[crop.Block] is not TapeImage image || CropImage is not { } cropImage)
        {
            CancelCrop();
            return;
        }
        _crop = null;
        _cropDrag = null;
        InvalidateVisual();
        CropChanged?.Invoke();
        if (crop.Part.Width >= 0.999 && crop.Part.Height >= 0.999)
            return;
        var path = await cropImage(JournalMarkdown.LocalPath(image.Url), crop.Part);
        if (path is null || crop.Block >= _document.Blocks.Count || !ReferenceEquals(_document.Blocks[crop.Block], image))
            return;
        Remember();
        var width = Math.Max(MinImageWidth, (int)Math.Round(image.Width * crop.Part.Width));
        var cropped = (TapeImage)TapeDocument.Parse(JournalMarkdown.ImageMarkdown(path, width)).Blocks[0];
        cropped.Alignment = image.Alignment;
        _document.Blocks[crop.Block] = cropped;
        _anchor = new TapePosition(crop.Block, 0);
        _caret = new TapePosition(crop.Block, 1);
        Commit();
    }

    private Rect? CropFrame()
    {
        if (_crop is not { } crop || crop.Block >= _layouts.Count || _layouts[crop.Block].ImageRect is not { } image)
            return null;
        return new Rect(image.X + crop.Part.X * image.Width, image.Y + crop.Part.Y * image.Height,
            crop.Part.Width * image.Width, crop.Part.Height * image.Height);
    }

    private CropHandle CropHandleAt(Point point)
    {
        if (CropFrame() is not { } frame)
            return CropHandle.None;
        const double grab = 7;
        var handle = CropHandle.None;
        if (Math.Abs(point.X - frame.X) <= grab) handle |= CropHandle.Left;
        else if (Math.Abs(point.X - frame.Right) <= grab) handle |= CropHandle.Right;
        if (Math.Abs(point.Y - frame.Y) <= grab) handle |= CropHandle.Top;
        else if (Math.Abs(point.Y - frame.Bottom) <= grab) handle |= CropHandle.Bottom;
        if (handle != CropHandle.None && frame.Inflate(grab).Contains(point))
            return handle;
        return frame.Contains(point) ? CropHandle.Move : CropHandle.None;
    }

    private void DragCrop(Point point)
    {
        if (_crop is not { } crop || _cropDrag is not { } drag || _layouts[crop.Block].ImageRect is not { } image)
            return;
        var dx = (point.X - drag.Start.X) / image.Width;
        var dy = (point.Y - drag.Start.Y) / image.Height;
        double left = drag.Part.X, top = drag.Part.Y, right = drag.Part.Right, bottom = drag.Part.Bottom;
        var minWidth = MinCropPixels / image.Width;
        var minHeight = MinCropPixels / image.Height;
        if (drag.Handle == CropHandle.Move)
        {
            dx = Math.Clamp(dx, -left, 1 - right);
            dy = Math.Clamp(dy, -top, 1 - bottom);
            (left, right, top, bottom) = (left + dx, right + dx, top + dy, bottom + dy);
        }
        else
        {
            if (drag.Handle.HasFlag(CropHandle.Left)) left = Math.Clamp(left + dx, 0, right - minWidth);
            if (drag.Handle.HasFlag(CropHandle.Right)) right = Math.Clamp(right + dx, left + minWidth, 1);
            if (drag.Handle.HasFlag(CropHandle.Top)) top = Math.Clamp(top + dy, 0, bottom - minHeight);
            if (drag.Handle.HasFlag(CropHandle.Bottom)) bottom = Math.Clamp(bottom + dy, top + minHeight, 1);
        }
        _crop = (crop.Block, new Rect(left, top, right - left, bottom - top));
        InvalidateVisual();
    }

    private static Cursor CropCursor(CropHandle handle) => handle switch
    {
        CropHandle.Move => MoveCursor,
        CropHandle.Left or CropHandle.Right => WestEast,
        CropHandle.Top or CropHandle.Bottom => NorthSouth,
        CropHandle.Left | CropHandle.Top or CropHandle.Right | CropHandle.Bottom => TopLeft,
        CropHandle.Right | CropHandle.Top or CropHandle.Left | CropHandle.Bottom => TopRight,
        _ => Arrow
    };

    // ── Editing ──

    private void DeleteSelection()
    {
        if (!HasSelection)
            return;
        var (start, end) = Selection;
        _caret = _anchor = DeleteRange(start, end);
    }

    private TapePosition DeleteRange(TapePosition start, TapePosition end)
    {
        var blocks = _document.Blocks;
        var first = blocks[start.Block];
        var last = blocks[end.Block];

        var prefix = new List<TapeBlock>();
        if (first is TapeParagraph firstParagraph)
            prefix.Add(firstParagraph.Slice(0, start.Offset));
        else if (start.Offset == 1)
            prefix.Add(first);

        var suffix = new List<TapeBlock>();
        if (last is TapeParagraph lastParagraph)
            suffix.Add(lastParagraph.Slice(end.Offset, lastParagraph.Length));
        else if (end.Offset == 0)
            suffix.Add(last);

        TapePosition caret;
        if (prefix.Count > 0 && prefix[^1] is TapeParagraph head && suffix.Count > 0 && suffix[0] is TapeParagraph tail)
        {
            if (head.Length == 0 && tail.Length == 0)
                tail = TapeDocument.EmptyParagraph(head.Runs[0].Style);
            head.Append(tail);
            suffix.RemoveAt(0);
            caret = new TapePosition(start.Block + prefix.Count - 1, start.Offset);
        }
        else if (prefix.Count > 0)
            caret = new TapePosition(start.Block + prefix.Count - 1, prefix[^1] is TapeParagraph ? start.Offset : 1);
        else
            caret = new TapePosition(start.Block, 0);

        blocks.RemoveRange(start.Block, end.Block - start.Block + 1);
        blocks.InsertRange(start.Block, prefix.Concat(suffix));
        if (blocks.Count == 0)
            blocks.Add(TapeDocument.EmptyParagraph());
        if (caret.Block >= blocks.Count)
            caret = new TapePosition(blocks.Count - 1, blocks[^1].Length);
        return Clamp(caret);
    }

    private TapeParagraph ParagraphAtCaret()
    {
        if (_document.Blocks[_caret.Block] is TapeParagraph paragraph)
            return paragraph;
        var created = TapeDocument.EmptyParagraph();
        var index = _caret.Offset == 0 ? _caret.Block : _caret.Block + 1;
        _document.Blocks.Insert(index, created);
        _caret = _anchor = new TapePosition(index, 0);
        return created;
    }

    private void InsertText(string text, TapeStyle? forced = null)
    {
        var style = forced ?? _pendingStyle ?? CurrentStyleForTyping();
        DeleteSelection();
        var paragraph = ParagraphAtCaret();
        paragraph.Insert(_caret.Offset, text, style);
        _caret = _anchor = new TapePosition(_caret.Block, _caret.Offset + text.Length);
        _pendingStyle = null;
    }

    private TapeStyle CurrentStyleForTyping()
    {
        var (start, _) = Selection;
        if (_document.Blocks[start.Block] is not TapeParagraph paragraph)
            return TapeStyle.Plain;
        var style = paragraph.StyleAt(start.Offset);
        var insideLink = style.Link is not null && start.Offset > 0 && start.Offset < paragraph.Length
                         && paragraph.StyleOfCharacter(start.Offset - 1).Link == style.Link
                         && paragraph.StyleOfCharacter(start.Offset).Link == style.Link;
        return insideLink || style.Link is null ? style : style with { Link = null };
    }

    private void NewParagraph()
    {
        DeleteSelection();
        if (_document.Blocks[_caret.Block] is TapeParagraph paragraph)
        {
            var tail = paragraph.SplitOff(_caret.Offset);
            tail.Alignment = paragraph.Alignment;
            if (tail.Length == 0 && paragraph.Kind != TapeParagraphKind.Normal)
                tail.Kind = TapeParagraphKind.Normal;
            if (_pendingStyle is not null && tail.Length == 0)
                tail.Runs[0].Style = _pendingStyle;
            _document.Blocks.Insert(_caret.Block + 1, tail);
            _caret = _anchor = new TapePosition(_caret.Block + 1, 0);
        }
        else
        {
            var index = _caret.Offset == 0 ? _caret.Block : _caret.Block + 1;
            _document.Blocks.Insert(index, TapeDocument.EmptyParagraph());
            _caret = _anchor = new TapePosition(_caret.Offset == 0 ? index + 1 : index, 0);
        }
    }

    private void InsertBlocks(IReadOnlyList<TapeBlock> fragment)
    {
        if (fragment.Count == 0)
            return;
        DeleteSelection();
        var blocks = _document.Blocks;
        if (blocks[_caret.Block] is not TapeParagraph paragraph)
        {
            var index = _caret.Offset == 0 ? _caret.Block : _caret.Block + 1;
            blocks.InsertRange(index, fragment);
            var lastIndex = index + fragment.Count - 1;
            _caret = _anchor = new TapePosition(lastIndex, blocks[lastIndex].Length);
            return;
        }
        var tail = paragraph.SplitOff(_caret.Offset);
        tail.Alignment = paragraph.Alignment;
        var insertAt = _caret.Block + 1;
        var first = 0;
        if (fragment[0] is TapeParagraph head)
        {
            paragraph.Append(head);
            first = 1;
        }
        blocks.InsertRange(insertAt, fragment.Skip(first));
        var lastBlock = _caret.Block + fragment.Count - first;
        if (blocks[lastBlock] is TapeParagraph lastParagraph)
        {
            var offset = lastParagraph.Length;
            if (tail.Length > 0)
                lastParagraph.Append(tail);
            _caret = _anchor = new TapePosition(lastBlock, offset);
        }
        else
        {
            blocks.Insert(lastBlock + 1, tail);
            _caret = _anchor = new TapePosition(lastBlock + 1, 0);
        }
    }

    /// <summary>Inserts pictures as blocks at the caret.</summary>
    public void InsertImages(IReadOnlyList<string> paths) => InsertImages(paths, null);

    /// <summary>Inserts pictures at the point <paramref name="at"/> of the editor (pictures dropped there).</summary>
    public void InsertImagesAt(IReadOnlyList<string> paths, Point at) => InsertImages(paths, PositionAt(at));

    private void InsertImages(IReadOnlyList<string> paths, TapePosition? at)
    {
        if (paths.Count == 0)
            return;
        InsertAtoms(paths.Select(p => TapeDocument.Parse(JournalMarkdown.ImageMarkdown(p, TapeDocument.DefaultImageWidth)).Blocks[0]).ToList(), at);
    }

    /// <summary>Inserts a block of code at the caret.</summary>
    public void InsertCode(string language, string code) =>
        InsertAtoms([new TapeCode(CodeLanguage(language), code.Replace("\r\n", "\n").TrimEnd('\n'))], null);

    /// <summary>The language as written in the file: the TextMate id, empty for plain text.</summary>
    private static string CodeLanguage(string language) =>
        language.Trim().ToLowerInvariant() is "" or "text" or "plain" or "plaintext" or "auto" ? string.Empty : KAONOMI.Chat.Services.CodeHighlighter.Normalize(language);

    private void InsertAtoms(List<TapeBlock> atoms, TapePosition? at)
    {
        Remember();
        CancelCrop();
        _caret = _anchor = Clamp(at ?? _caret);
        if (_document.Blocks[_caret.Block] is TapeParagraph paragraph && _caret.Offset > 0 && _caret.Offset < paragraph.Length)
            NewParagraph();
        var index = _document.Blocks[_caret.Block] is TapeParagraph && _caret.Offset == 0 ? _caret.Block : _caret.Block + 1;
        _document.Blocks.InsertRange(index, atoms);
        if (index + atoms.Count >= _document.Blocks.Count)
            _document.Blocks.Add(TapeDocument.EmptyParagraph());
        _caret = _anchor = new TapePosition(index + atoms.Count - 1, 1);
        Commit();
        Focus();
    }

    private List<TapeBlock> SelectedBlocks()
    {
        var (start, end) = Selection;
        var result = new List<TapeBlock>();
        for (var block = start.Block; block <= end.Block; block++)
        {
            var from = block == start.Block ? start.Offset : 0;
            var to = block == end.Block ? end.Offset : _document.Blocks[block].Length;
            switch (_document.Blocks[block])
            {
                case TapeParagraph paragraph:
                    result.Add(paragraph.Slice(from, to));
                    break;
                case { } atom when from == 0 && to == 1:
                    result.Add(atom.Clone());
                    break;
            }
        }
        return result;
    }

    private IClipboard? Clipboard => TopLevel.GetTopLevel(this)?.Clipboard;

    /// <summary>Copies (or cuts) the selection: its text to the clipboard of the system, its formatting kept for a paste here.</summary>
    public void Copy(bool cut)
    {
        if (!HasSelection)
            return;
        var fragment = SelectedBlocks();
        var plain = string.Join("\n", fragment.Select(b => b is TapeParagraph p ? p.Text : TapeDocument.SerializeBlock(b)));
        var rich = new TapeDocument();
        rich.Blocks.AddRange(fragment);
        _clip = (plain, rich.Serialize());
        if (cut)
        {
            Remember();
            DeleteSelection();
            Commit();
        }
        if (Clipboard is { } clipboard)
            _ = clipboard.SetTextAsync(plain);
    }

    /// <summary>Pastes the clipboard at the caret (with its formatting when it is the last copy made here).</summary>
    public async void Paste()
    {
        string? text = null;
        try
        {
            if (Clipboard is { } clipboard)
                text = await clipboard.TryGetTextAsync();
        }
        catch (Exception)
        {
            // Nothing readable on the clipboard.
        }
        if (string.IsNullOrEmpty(text))
            return;
        Remember();
        List<TapeBlock> fragment;
        if (_clip is { } clip && clip.Plain.Replace("\r\n", "\n") == text.Replace("\r\n", "\n"))
            fragment = TapeDocument.Parse(clip.Rich).Blocks;
        else
            fragment = PlainParagraphs(text, _pendingStyle ?? CurrentStyleForTyping());
        InsertBlocks(fragment);
        Commit();
    }

    // ── Keyboard ──

    private static bool IsMac => OperatingSystem.IsMacOS();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        var command = WorkUi.IsCommand(e.KeyModifiers);
        var word = IsMac ? alt : e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var key = e.Key;

        if (_crop is not null)
        {
            if (key == Key.Enter)
                ApplyCrop();
            else if (key == Key.Escape)
                CancelCrop();
            e.Handled = true;
            return;
        }

        var handled = true;
        switch (key)
        {
            case Key.Left:
                Move(command && IsMac ? LineEdge(_caret, start: true)
                    : !shift && HasSelection ? Selection.Start
                    : word ? WordEdge(_caret, forward: false) : Step(_caret, -1), shift);
                break;
            case Key.Right:
                Move(command && IsMac ? LineEdge(_caret, start: false)
                    : !shift && HasSelection ? Selection.End
                    : word ? WordEdge(_caret, forward: true) : Step(_caret, 1), shift);
                break;
            case Key.Up:
                Move(command ? new TapePosition(0, 0) : Vertical(-1), shift, keepX: true);
                break;
            case Key.Down:
                Move(command ? DocumentEnd : Vertical(1), shift, keepX: true);
                break;
            case Key.Home:
                Move(command ? new TapePosition(0, 0) : LineEdge(_caret, start: true), shift);
                break;
            case Key.End:
                Move(command ? DocumentEnd : LineEdge(_caret, start: false), shift);
                break;
            case Key.PageUp:
            case Key.PageDown:
                Move(Vertical(key == Key.PageUp ? -1 : 1, pages: true), shift, keepX: true);
                break;
            case Key.Back:
                Remember(typing: true);
                if (!HasSelection)
                    _anchor = word ? WordEdge(_caret, forward: false) : BackspaceStart(_caret);
                DeleteSelection();
                Commit();
                break;
            case Key.Delete:
                Remember(typing: true);
                if (!HasSelection)
                    _anchor = word ? WordEdge(_caret, forward: true) : DeleteEnd(_caret);
                DeleteSelection();
                Commit();
                break;
            case Key.Enter:
                Remember();
                NewParagraph();
                Commit();
                break;
            case Key.Tab when !command:
                Remember(typing: true);
                InsertText("\t");
                Commit();
                break;
            case Key.A when command:
                _anchor = new TapePosition(0, 0);
                _caret = DocumentEnd;
                Changed();
                break;
            case Key.C when command:
                Copy(cut: false);
                break;
            case Key.X when command:
                Copy(cut: true);
                break;
            case Key.V when command:
                Paste();
                break;
            case Key.Z when command && shift:
            case Key.Y when command:
                Redo();
                break;
            case Key.Z when command:
                Undo();
                break;
            case Key.B when command:
                ApplyStyle(s => s with { Bold = !CurrentStyle.Bold });
                break;
            case Key.I when command:
                ApplyStyle(s => s with { Italic = !CurrentStyle.Italic });
                break;
            case Key.U when command:
                ApplyStyle(s => s with { Underline = !CurrentStyle.Underline });
                break;
            case Key.D0 or Key.NumPad0 when command && alt:
                SetParagraphKind(TapeParagraphKind.Normal);
                break;
            case Key.D1 or Key.NumPad1 when command && alt:
                SetParagraphKind(TapeParagraphKind.Heading1);
                break;
            case Key.D2 or Key.NumPad2 when command && alt:
                SetParagraphKind(TapeParagraphKind.Heading2);
                break;
            case Key.D3 or Key.NumPad3 when command && alt:
                SetParagraphKind(TapeParagraphKind.Heading3);
                break;
            default:
                handled = false;
                break;
        }
        if (handled)
            e.Handled = true;
    }

    /// <summary>Typed text (the shortcuts of the system and of the chat go on).</summary>
    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text) || _crop is not null)
            return;
        var text = new string(e.Text.Where(c => c >= 32 && c != 127).ToArray());
        if (text.Length == 0)
            return;
        Remember(typing: true);
        InsertText(text);
        _desiredX = null;
        Commit();
        e.Handled = true;
    }

    private void Move(TapePosition to, bool extend, bool keepX = false)
    {
        if (!keepX)
            _desiredX = null;
        _caret = Clamp(to);
        if (!extend)
            _anchor = _caret;
        _pendingStyle = null;
        _typing = false;
        Changed();
    }

    private void Changed()
    {
        InvalidateVisual();
        BringCaretIntoView();
        SelectionChanged?.Invoke();
    }

    private TapePosition Step(TapePosition position, int direction)
    {
        var block = _document.Blocks[position.Block];
        if (direction < 0)
        {
            if (position.Offset > 0)
            {
                var offset = position.Offset - 1;
                if (block is TapeParagraph paragraph && offset > 0 && char.IsLowSurrogate(paragraph.Text[offset]))
                    offset--;
                return position with { Offset = offset };
            }
            return position.Block > 0 ? new TapePosition(position.Block - 1, _document.Blocks[position.Block - 1].Length) : position;
        }
        if (position.Offset < block.Length)
        {
            var offset = position.Offset + 1;
            if (block is TapeParagraph paragraph && offset < paragraph.Length && char.IsLowSurrogate(paragraph.Text[offset]))
                offset++;
            return position with { Offset = offset };
        }
        return position.Block < _document.Blocks.Count - 1 ? new TapePosition(position.Block + 1, 0) : position;
    }

    private TapePosition BackspaceStart(TapePosition position)
    {
        if (position.Offset == 0 && position.Block > 0 && _document.Blocks[position.Block - 1] is not TapeParagraph)
            return new TapePosition(position.Block - 1, 0);
        return Step(position, -1);
    }

    private TapePosition DeleteEnd(TapePosition position)
    {
        var block = _document.Blocks[position.Block];
        if (position.Offset == block.Length && position.Block < _document.Blocks.Count - 1 && _document.Blocks[position.Block + 1] is not TapeParagraph)
            return new TapePosition(position.Block + 1, 1);
        return Step(position, 1);
    }

    private TapePosition WordEdge(TapePosition position, bool forward)
    {
        if (_document.Blocks[position.Block] is not TapeParagraph paragraph)
            return Step(position, forward ? 1 : -1);
        var text = paragraph.Text;
        var offset = position.Offset;
        if (forward)
        {
            if (offset >= text.Length)
                return Step(position, 1);
            while (offset < text.Length && !char.IsLetterOrDigit(text[offset]))
                offset++;
            while (offset < text.Length && char.IsLetterOrDigit(text[offset]))
                offset++;
        }
        else
        {
            if (offset == 0)
                return Step(position, -1);
            while (offset > 0 && !char.IsLetterOrDigit(text[offset - 1]))
                offset--;
            while (offset > 0 && char.IsLetterOrDigit(text[offset - 1]))
                offset--;
        }
        return position with { Offset = offset };
    }

    private TapePosition LineEdge(TapePosition position, bool start)
    {
        EnsureLayout();
        if (_layouts[position.Block].Lines is not { } lines)
            return position with { Offset = start ? 0 : 1 };
        var length = _document.Blocks[position.Block].Length;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (position.Offset < line.End || i == lines.Count - 1)
            {
                if (start)
                    return position with { Offset = Math.Min(line.Start, length) };
                var end = i == lines.Count - 1 ? length : Math.Max(line.Start, line.End - Math.Max(1, line.Trailing));
                return position with { Offset = Math.Min(end, length) };
            }
        }
        return position;
    }

    private TapePosition Vertical(int direction, bool pages = false)
    {
        EnsureLayout();
        var caret = CaretRect(_caret);
        _desiredX ??= caret.X;
        var page = this.FindAncestorOfType<ScrollViewer>() is { } scroll ? scroll.Viewport.Height : 300;
        var distance = pages ? Math.Max(caret.Height, page) - caret.Height : 1;
        var y = direction < 0 ? caret.Y - distance : caret.Bottom + distance;
        if (!pages && _document.Blocks[_caret.Block] is not TapeParagraph)
        {
            var layout = _layouts[_caret.Block];
            y = direction < 0 ? layout.Top - 1 : layout.Top + layout.Height + 1;
        }
        if (y < PaddingY * _zoom)
            return new TapePosition(0, 0);
        if (y > TotalHeight - PaddingY * _zoom)
            return DocumentEnd;
        return PositionAt(new Point(_desiredX.Value, y));
    }

    // ── Mouse ──

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (WorkUi.IsCommand(e.KeyModifiers))
        {
            ZoomRequested?.Invoke(e.Delta.Y > 0 ? 1 : -1);
            e.Handled = true;
            return;
        }
        base.OnPointerWheelChanged(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed)
        {
            Focus();
            e.Handled = true;
            ContextMenuRequested?.Invoke(point.Position);
            return;
        }
        if (!point.Properties.IsLeftButtonPressed)
            return;
        e.Handled = true;
        e.Pointer.Capture(this);
        OnPress(point.Position, e.ClickCount, e.KeyModifiers);
    }

    private void OnPress(Point position, int clicks, KeyModifiers modifiers)
    {
        Focus();
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);

        if (_crop is not null)
        {
            var handle = CropHandleAt(position);
            if (handle == CropHandle.Move && clicks >= 2)
                ApplyCrop();
            else if (handle != CropHandle.None && CropFrame() is not null)
                _cropDrag = (handle, position, _crop.Value.Part);
            else
                CancelCrop();
            return;
        }

        EnsureLayout();
        for (var i = 0; i < _layouts.Count; i++)
            if (_layouts[i].ImageRect is { } rect && GripRect(rect).Inflate(3).Contains(position) && _document.Blocks[i] is TapeImage image)
            {
                Remember();
                _resizing = (i, position.X, image.Width);
                return;
            }

        if (WorkUi.IsCommand(modifiers) && LinkAt(position) is { } address)
        {
            OpenLink?.Invoke(address);
            return;
        }
        // In a wiki a plain click opens the link, once released at the same place (the caret goes there meanwhile).
        _pressedLink = LinksOpenOnClick && clicks == 1 && !shift && !alt && LinkAt(position) is { } plain ? (plain, position) : null;

        var at = PositionAt(position);
        if (clicks >= 2 && _document.Blocks[at.Block] is TapeImage clicked)
        {
            OpenImage?.Invoke(JournalMarkdown.LocalPath(clicked.Url));
            return;
        }
        if (clicks >= 2 && _document.Blocks[at.Block] is TapeCode code)
        {
            var before = _document.Serialize();
            var index = at.Block;
            EditCodeRequested?.Invoke(code.Language, code.Code, (language, text) =>
            {
                if (before != _document.Serialize())
                    return false;
                Remember();
                _document.Blocks[index] = new TapeCode(CodeLanguage(language), text.Replace("\r\n", "\n").TrimEnd('\n'));
                _anchor = new TapePosition(index, 0);
                _caret = new TapePosition(index, 1);
                Commit();
                Focus();
                return true;
            });
            return;
        }
        if (clicks == 2)
        {
            _anchor = WordEdge(at with { Offset = Math.Min(at.Offset + 1, _document.Blocks[at.Block].Length) }, forward: false);
            _caret = WordEdge(_anchor, forward: true);
            if (_caret.Block != _anchor.Block)
                _caret = at;
            Changed();
            return;
        }
        if (clicks >= 3)
        {
            _anchor = new TapePosition(at.Block, 0);
            _caret = new TapePosition(at.Block, _document.Blocks[at.Block].Length);
            Changed();
            return;
        }

        if (_document.Blocks[at.Block] is not TapeParagraph && BoxOf(_layouts[at.Block]) is { } box && box.Contains(position))
        {
            _anchor = new TapePosition(at.Block, 0);
            _caret = new TapePosition(at.Block, 1);
            Changed();
            return;
        }

        Move(at, shift);
        _selecting = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        if (_crop is not null)
        {
            if (_cropDrag is not null)
                DragCrop(position);
            else
                Cursor = CropCursor(CropHandleAt(position));
            return;
        }
        if (_resizing is { } resizing && _document.Blocks[resizing.Block] is TapeImage image)
        {
            var available = (int)Math.Max(MinImageWidth, (LayoutWidth - 2 * PaddingX * _zoom) / _zoom);
            var factor = image.Alignment == TapeAlignment.Center ? 2 : 1;
            image.Width = Math.Clamp(resizing.StartWidth + (int)Math.Round((position.X - resizing.StartX) / _zoom * factor), MinImageWidth, available);
            Relayout();
            return;
        }
        if (_selecting)
        {
            _caret = PositionAt(position);
            Changed();
            return;
        }
        if (_busy)
            return;
        EnsureLayout();
        var overGrip = _layouts.Any(l => l.ImageRect is { } rect && GripRect(rect).Inflate(3).Contains(position));
        var overLink = !overGrip && (LinksOpenOnClick || WorkUi.IsCommand(e.KeyModifiers)) && LinkAt(position) is not null;
        Cursor = overGrip ? BottomRight : overLink ? Hand : IBeam;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;
        e.Pointer.Capture(null);
        _cropDrag = null;
        if (_resizing is not null)
        {
            _resizing = null;
            Commit();
        }
        _selecting = false;
        // A plain click on a link (pressed and released at the same place): it opens.
        if (_pressedLink is { } pressed)
        {
            _pressedLink = null;
            var at = e.GetPosition(this);
            if (Math.Sqrt(Math.Pow(at.X - pressed.At.X, 2) + Math.Pow(at.Y - pressed.At.Y, 2)) < 4 && !HasSelection)
                OpenLink?.Invoke(pressed.Address);
        }
    }

    private string? LinkAt(Point point)
    {
        EnsureLayout();
        var index = _layouts.FindIndex(l => point.Y >= l.Top && point.Y < l.Top + l.Height);
        if (index < 0 || _layouts[index].Lines is not { } lines || _document.Blocks[index] is not TapeParagraph paragraph)
            return null;
        var y = point.Y - _layouts[index].Top;
        var line = lines.FirstOrDefault(l => y >= l.Top && y < l.Top + l.Height);
        if (line is null)
            return null;
        var x = point.X - PaddingX * _zoom - line.Shift;
        for (var k = line.Start; k < line.End; k++)
            if (x >= line.Xs[k - line.Start] && x < line.Xs[k - line.Start + 1])
                return paragraph.StyleOfCharacter(k).Link;
        return null;
    }

    private TapePosition PositionAt(Point point)
    {
        EnsureLayout();
        var index = _layouts.FindIndex(l => point.Y < l.Top + l.Height);
        if (index < 0)
            return DocumentEnd;
        var layout = _layouts[index];
        if (layout.Lines is { } lines)
        {
            var y = Math.Max(0, point.Y - layout.Top);
            var lineIndex = lines.FindIndex(l => y < l.Top + l.Height);
            if (lineIndex < 0)
                lineIndex = lines.Count - 1;
            var line = lines[lineIndex];
            var x = point.X - PaddingX * _zoom - line.Shift;
            var best = line.Start;
            var bestDistance = double.MaxValue;
            var last = lineIndex == lines.Count - 1 ? line.End : Math.Max(line.Start, line.End - 1);
            for (var k = line.Start; k <= last; k++)
            {
                var distance = Math.Abs(line.Xs[k - line.Start] - x);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = k;
                }
            }
            return new TapePosition(index, Math.Clamp(best, 0, _document.Blocks[index].Length));
        }
        return new TapePosition(index, BoxOf(layout) is { } rect && point.X > rect.Center.X ? 1 : 0);
    }

    // ── Caret ──

    private void BringCaretIntoView()
    {
        if (TopLevel.GetTopLevel(this) is null)
            return;
        EnsureLayout();
        if (_layouts.Count == 0)
            return;
        this.BringIntoView(CaretRect(_caret).Inflate(8));
    }

    private Rect CaretRect(TapePosition position)
    {
        var layout = _layouts[Math.Clamp(position.Block, 0, _layouts.Count - 1)];
        if (layout.Lines is { } lines)
        {
            var offset = Math.Clamp(position.Offset, 0, _document.Blocks[position.Block].Length);
            var line = lines.FirstOrDefault(l => offset < l.End) ?? lines[^1];
            var x = PaddingX * _zoom + line.Shift + line.Xs[Math.Clamp(offset - line.Start, 0, line.Xs.Length - 1)];
            return new Rect(x, layout.Top + line.Top, 1.5, line.Height);
        }
        var box = BoxOf(layout) ?? new Rect(PaddingX * _zoom, layout.Top, 1, layout.Height);
        return new Rect(position.Offset == 0 ? box.X - 3 : box.Right + 2, box.Y, 1.5, box.Height);
    }

    private static Rect? BoxOf(BlockLayout layout) => layout.ImageRect ?? layout.CodeRect;

    private static Rect GripRect(Rect image) => new(image.Right - GripSize, image.Bottom - GripSize, GripSize, GripSize);

    // ── Layout ──

    /// <summary>A line of a paragraph: its characters, the x of each caret place, the pieces drawn.</summary>
    private sealed class TextLine
    {
        public double Top, Height, Ascent, Shift;
        public int Start, End, Trailing;
        public double[] Xs = [];
        public readonly List<Piece> Pieces = [];
    }

    /// <summary>Characters of one style on a line.</summary>
    private sealed class Piece
    {
        public FormattedText Text = null!;
        public double X, Width, Size, Ascent;
        public IBrush Brush = null!;
        public bool Underline, Strike;
    }

    private sealed class BlockLayout
    {
        public double Top, Height;
        public List<TextLine>? Lines;
        public Bitmap? Image;
        public Rect? ImageRect;
        public string? Missing;
        public Rect? CodeRect;
        public List<(double Y, List<(FormattedText Text, double X)> Pieces)>? Code;
        public string? CodeLabel;
    }

    private double TotalHeight => _layouts.Count == 0 ? 2 * PaddingY * _zoom : _layouts[^1].Top + _layouts[^1].Height + PaddingY * _zoom;

    private double LayoutWidth => _availableWidth > 0 ? _availableWidth : Bounds.Width > 0 ? Bounds.Width : 600;

    private void Relayout()
    {
        _layoutDirty = true;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void EnsureLayout()
    {
        if (!_layoutDirty && _layouts.Count == _document.Blocks.Count && Math.Abs(LayoutWidth - _layoutWidth) <= 0.5)
            return;
        BuildLayout(LayoutWidth);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!double.IsInfinity(availableSize.Width) && availableSize.Width > 0)
            _availableWidth = availableSize.Width;
        EnsureLayout();
        return new Size(double.IsInfinity(availableSize.Width) ? _layoutWidth : availableSize.Width, TotalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (finalSize.Width > 0 && Math.Abs(finalSize.Width - _availableWidth) > 0.5)
        {
            _availableWidth = finalSize.Width;
            _layoutDirty = true;
            InvalidateMeasure();
        }
        if (_revealCaret && finalSize.Width > 0)
        {
            _revealCaret = false;
            WorkUi.Later(BringCaretIntoView);
        }
        return finalSize;
    }

    private void BuildLayout(double width)
    {
        _layouts.Clear();
        _layoutWidth = width;
        _layoutDirty = false;
        var z = _zoom;
        var textWidth = Math.Max(40, width - 2 * PaddingX * z);
        var top = PaddingY * z;
        if (_paragraphCache.Count > 4000)
            _paragraphCache.Clear();
        for (var index = 0; index < _document.Blocks.Count; index++)
        {
            var block = _document.Blocks[index];
            if (index > 0 && block is TapeParagraph { Kind: not TapeParagraphKind.Normal } styled)
                top += TapeParagraph.SpaceBefore(styled.Kind) * z;
            var layout = new BlockLayout { Top = top };
            switch (block)
            {
                case TapeParagraph paragraph:
                    layout.Lines = LayoutParagraph(paragraph, textWidth);
                    layout.Height = Math.Max(1, layout.Lines.Count == 0 ? 1 : layout.Lines[^1].Top + layout.Lines[^1].Height);
                    break;
                case TapeImage image:
                {
                    var path = JournalMarkdown.LocalPath(image.Url);
                    layout.Image = ImageFor(path);
                    var shownWidth = Math.Min(image.Width * z, textWidth);
                    double shownHeight;
                    if (layout.Image is { } bitmap)
                        shownHeight = shownWidth * bitmap.Size.Height / Math.Max(1, bitmap.Size.Width);
                    else
                    {
                        shownHeight = 28 * z;
                        layout.Missing = _loading.Contains(path) ? $"Loading {image.Alt}…" : $"Picture not found: {path}";
                    }
                    var shift = image.Alignment switch
                    {
                        TapeAlignment.Center => (textWidth - shownWidth) / 2,
                        TapeAlignment.Right => textWidth - shownWidth,
                        _ => 0
                    };
                    layout.ImageRect = new Rect(PaddingX * z + shift, top + ImageMargin * z, shownWidth, shownHeight);
                    layout.Height = shownHeight + 2 * ImageMargin * z;
                    break;
                }
                case TapeCode code:
                {
                    var (lines, height) = LayoutCode(code, textWidth - 2 * CodePadding * z);
                    layout.Code = lines;
                    layout.CodeLabel = string.IsNullOrEmpty(code.Language) ? "code" : code.Language;
                    layout.CodeRect = new Rect(PaddingX * z, top + ImageMargin * z, textWidth, height + 2 * CodePadding * z + CodeLabelHeight * z);
                    layout.Height = layout.CodeRect.Value.Height + 2 * ImageMargin * z;
                    break;
                }
            }
            _layouts.Add(layout);
            top += layout.Height;
        }
    }

    // ── Measures of the text (cached) ──

    private static readonly Dictionary<(Typeface, double, string), double> Widths = new();
    private static readonly Dictionary<(Typeface, double), (double Height, double Ascent)> Metrics = new();

    private static FormattedText Format(string text, Typeface typeface, double size, IBrush? brush) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, size, brush);

    private static double Measure(Typeface typeface, double size, string text)
    {
        if (Widths.Count > 40000)
            Widths.Clear();
        if (!Widths.TryGetValue((typeface, size, text), out var width))
            Widths[(typeface, size, text)] = width = Format(text, typeface, size, null).WidthIncludingTrailingWhitespace;
        return width;
    }

    private static (double Height, double Ascent) MetricsOf(Typeface typeface, double size)
    {
        if (!Metrics.TryGetValue((typeface, size), out var metrics))
        {
            var sample = Format("Ág", typeface, size, null);
            Metrics[(typeface, size)] = metrics = (sample.Height, sample.Baseline);
        }
        return metrics;
    }

    /// <summary>The lines of a paragraph wrapped in <paramref name="width"/>: words kept whole when they fit, cut otherwise.</summary>
    private List<TextLine> LayoutParagraph(TapeParagraph paragraph, double width)
    {
        var key = $"{width:0.#}|{paragraph.Kind}|{paragraph.Alignment}|" + string.Join("\u0001", paragraph.Runs.Select(r => r.Text + "\u0002" + r.Style));
        if (_paragraphCache.TryGetValue(key, out var cached))
            return cached;

        var text = paragraph.Text;
        var n = text.Length;
        var runOf = new int[n];
        var styles = paragraph.Runs.Select(r => paragraph.Effective(r.Style)).ToList();
        var fonts = styles.Select(FontFor).ToList();
        var sizes = styles.Select(SizeFor).ToList();
        var position = 0;
        for (var r = 0; r < paragraph.Runs.Count; r++)
            for (var k = 0; k < paragraph.Runs[r].Text.Length; k++)
                runOf[position++] = r;

        // The advance of each character, measured word by word (the shaping of a word kept).
        var advance = new double[n];
        var i = 0;
        while (i < n)
        {
            var run = runOf[i];
            var font = fonts[run];
            var size = sizes[run];
            if (char.IsWhiteSpace(text[i]))
            {
                advance[i] = text[i] == '\t' ? 4 * Measure(font, size, " ") : Measure(font, size, text[i].ToString());
                i++;
                continue;
            }
            var start = i;
            while (i < n && runOf[i] == run && !char.IsWhiteSpace(text[i]))
                i++;
            var word = text[start..i];
            if (word.Length <= 48)
            {
                double previous = 0;
                for (var k = 1; k <= word.Length; k++)
                {
                    if (k < word.Length && char.IsHighSurrogate(word[k - 1]))
                        continue;
                    var w = Measure(font, size, word[..k]);
                    advance[start + k - 1] = w - previous;
                    previous = w;
                }
            }
            else
                for (var k = 0; k < word.Length; k++)
                {
                    if (char.IsLowSurrogate(word[k]))
                        continue;
                    var length = k + 1 < word.Length && char.IsHighSurrogate(word[k]) ? 2 : 1;
                    advance[start + k + length - 1] = Measure(font, size, word.Substring(k, length));
                }
        }

        // Lines: a break after a space when the next word does not fit, inside a word too long for a line.
        var ranges = new List<(int Start, int End)>();
        var lineStart = 0;
        double x = 0;
        var lastBreak = -1;
        for (i = 0; i < n; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                x += advance[i];
                lastBreak = i + 1;
                continue;
            }
            if (x + advance[i] > width && i > lineStart)
            {
                var breakAt = lastBreak > lineStart ? lastBreak : i;
                if (breakAt < n && char.IsLowSurrogate(text[breakAt]) && breakAt > lineStart + 1)
                    breakAt--;
                ranges.Add((lineStart, breakAt));
                lineStart = breakAt;
                x = 0;
                for (var k = lineStart; k < i; k++)
                    x += advance[k];
                lastBreak = -1;
            }
            x += advance[i];
        }
        ranges.Add((lineStart, n));

        var emptyStyle = paragraph.Effective(paragraph.Runs.Count > 0 ? paragraph.Runs[0].Style : TapeStyle.Plain);
        var lines = new List<TextLine>();
        double top = 0;
        foreach (var (from, to) in ranges)
        {
            var line = new TextLine { Start = from, End = to, Top = top, Xs = new double[to - from + 1] };
            for (var k = from; k < to; k++)
                line.Xs[k - from + 1] = line.Xs[k - from] + advance[k];
            var trailing = 0;
            for (var k = to - 1; k >= from && char.IsWhiteSpace(text[k]); k--)
                trailing++;
            line.Trailing = trailing;
            var visible = line.Xs[to - from - trailing];

            double height = 0, ascent = 0;
            if (to == from)
                (height, ascent) = MetricsOf(FontFor(emptyStyle), SizeFor(emptyStyle));
            for (var k = from; k < to; k++)
            {
                var run = runOf[k];
                var (h, a) = MetricsOf(fonts[run], sizes[run]);
                height = Math.Max(height, h);
                ascent = Math.Max(ascent, a);
            }
            line.Height = height;
            line.Ascent = ascent;
            line.Shift = paragraph.Alignment switch
            {
                TapeAlignment.Center => Math.Max(0, (width - visible) / 2),
                TapeAlignment.Right => Math.Max(0, width - visible),
                _ => 0
            };

            // Pieces of one run (tabs are spaces, not drawn).
            var k2 = from;
            while (k2 < to)
            {
                if (text[k2] == '\t')
                {
                    k2++;
                    continue;
                }
                var run = runOf[k2];
                var start = k2;
                while (k2 < to && runOf[k2] == run && text[k2] != '\t')
                    k2++;
                var style = styles[run];
                var color = style.Link is not null && style.Color is null ? WorkPalette.Link : WorkPalette.Adapt(style.Color, WorkPalette.DefaultText);
                var brush = WorkPalette.BrushOf(color);
                var formatted = Format(text[start..k2], fonts[run], sizes[run], brush);
                line.Pieces.Add(new Piece
                {
                    Text = formatted,
                    X = line.Xs[start - from],
                    Width = formatted.WidthIncludingTrailingWhitespace,
                    Size = sizes[run],
                    Ascent = formatted.Baseline,
                    Brush = brush,
                    Underline = style.Underline || style.Link is not null,
                    Strike = style.Strike
                });
            }
            lines.Add(line);
            top += height;
        }
        _paragraphCache[key] = lines;
        return lines;
    }

    private static readonly Typeface MonoFace = new(WorkPalette.Mono);

    /// <summary>The lines of a block of code, cut at the width, in the colors of its language.</summary>
    private (List<(double Y, List<(FormattedText Text, double X)> Pieces)> Lines, double Height) LayoutCode(TapeCode code, double width)
    {
        var size = Math.Round(13 * _zoom);
        var charWidth = Math.Max(1, Measure(MonoFace, size, "M"));
        var columns = Math.Max(8, (int)(width / charWidth));
        var lineHeight = MetricsOf(MonoFace, size).Height;
        var colors = WorkPalette.CodeColors(code.Language, code.Code);
        var lines = new List<(double, List<(FormattedText, double)>)>();
        double y = 0;
        var offset = 0;
        foreach (var raw in code.Code.Split('\n'))
        {
            var start = 0;
            do
            {
                var length = Math.Min(columns, raw.Length - start);
                var pieces = new List<(FormattedText, double)>();
                var k = start;
                while (k < start + length)
                {
                    var color = colors[offset + k];
                    var from = k;
                    while (k < start + length && colors[offset + k] == color)
                        k++;
                    pieces.Add((Format(raw[from..k].Replace("\t", "    "), MonoFace, size, WorkPalette.BrushOf(color ?? WorkPalette.DefaultText)), (from - start) * charWidth));
                }
                lines.Add((y, pieces));
                y += lineHeight;
                start += length;
            } while (start < raw.Length);
            offset += raw.Length + 1;
        }
        return (lines, y);
    }

    private readonly Dictionary<(string?, bool, bool), Typeface> _fonts = new();

    private Typeface FontFor(TapeStyle style)
    {
        var key = (style.Font, style.Bold, style.Italic);
        if (_fonts.TryGetValue(key, out var font))
            return font;
        var family = style.Font is { Length: > 0 } name ? new FontFamily($"{name}, {WorkPalette.TextNames}") : WorkPalette.Text;
        // A family without a bold or an italic face: Avalonia makes the face bolder or slanted.
        font = new Typeface(family, style.Italic ? FontStyle.Italic : FontStyle.Normal, style.Bold ? FontWeight.Bold : FontWeight.Normal);
        _fonts[key] = font;
        return font;
    }

    private double SizeFor(TapeStyle style) => Math.Max(4, Math.Round((style.Size ?? DefaultSize) * _zoom));

    private Bitmap? ImageFor(string path)
    {
        if (_images.TryGetValue(path, out var bitmap))
            return bitmap;
        if (LoadImage is not { } load || !_loading.Add(path))
            return null;
        _ = LoadAsync();
        return null;

        async Task LoadAsync()
        {
            Bitmap? loaded = null;
            try
            {
                loaded = await load(path);
            }
            catch (Exception)
            {
                // Shown as "picture not found".
            }
            Ui.OnMain(() =>
            {
                _images[path] = loaded;
                _loading.Remove(path);
                Relayout();
            });
        }
    }

    // ── Drawing ──

    public override void Render(DrawingContext context)
    {
        EnsureLayout();
        // The whole area takes the clicks.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        var z = _zoom;
        var padX = PaddingX * z;
        if (_document.Blocks.Count == 1 && _document.Blocks[0].Length == 0 && !string.IsNullOrEmpty(Placeholder))
        {
            var placeholder = Format(Placeholder, MonoFace, SizeFor(TapeStyle.Plain), PlaceholderBrush);
            placeholder.MaxTextWidth = Math.Max(40, Bounds.Width - 2 * padX);
            context.DrawText(placeholder, new Point(padX, PaddingY * z));
        }

        var (start, end) = Selection;
        var focused = IsFocused;
        var selection = focused ? SelectionBrush : InactiveSelectionBrush;
        for (var i = 0; i < _layouts.Count; i++)
        {
            var layout = _layouts[i];
            var selected = HasSelection && i >= start.Block && i <= end.Block;
            if (layout.Lines is { } lines)
            {
                if (selected)
                {
                    var from = i == start.Block ? start.Offset : 0;
                    var to = i == end.Block ? end.Offset : _document.Blocks[i].Length;
                    foreach (var line in lines)
                    {
                        var a = Math.Max(from, line.Start);
                        var b = Math.Min(to, line.End);
                        if (b > a)
                        {
                            var x0 = padX + line.Shift + line.Xs[a - line.Start];
                            var x1 = padX + line.Shift + line.Xs[b - line.Start];
                            context.FillRectangle(selection, new Rect(x0, layout.Top + line.Top, x1 - x0, line.Height));
                        }
                    }
                    if (i < end.Block)
                    {
                        var caret = CaretRect(new TapePosition(i, _document.Blocks[i].Length));
                        context.FillRectangle(selection, new Rect(caret.X, caret.Y, 6, caret.Height));
                    }
                }
                foreach (var line in lines)
                {
                    var baseline = layout.Top + line.Top + line.Ascent;
                    foreach (var piece in line.Pieces)
                    {
                        var x = padX + line.Shift + piece.X;
                        context.DrawText(piece.Text, new Point(x, baseline - piece.Ascent));
                        if (piece.Underline || piece.Strike)
                        {
                            var pen = new Pen(piece.Brush, Math.Max(1, piece.Size / 14));
                            if (piece.Underline)
                                context.DrawLine(pen, new Point(x, baseline + 2 * z), new Point(x + piece.Width, baseline + 2 * z));
                            if (piece.Strike)
                            {
                                var y = baseline - piece.Ascent * 0.3;
                                context.DrawLine(pen, new Point(x, y), new Point(x + piece.Width, y));
                            }
                        }
                    }
                }
            }
            else if (layout.CodeRect is { } box && layout.Code is { } code)
            {
                context.DrawRectangle(CodeBackground, CodeBorder, box, 4, 4);
                var label = Format(layout.CodeLabel ?? "code", MonoFace, Math.Max(6, Math.Round(10 * z)), Ui.Muted);
                context.DrawText(label, new Point(box.Right - CodePadding * z - label.WidthIncludingTrailingWhitespace, box.Y + 4 * z));
                var origin = new Point(box.X + CodePadding * z, box.Y + CodePadding * z + CodeLabelHeight * z - 4 * z);
                foreach (var (y, pieces) in code)
                    foreach (var (text, x) in pieces)
                        context.DrawText(text, origin + new Point(x, y));
                var codeSelected = selected && (i > start.Block || start.Offset == 0) && (i < end.Block || end.Offset == 1);
                if (codeSelected)
                    context.DrawRectangle(null, SelectedPen, box);
            }
            else if (layout.ImageRect is { } rect)
            {
                if (layout.Image is { } bitmap)
                    context.DrawImage(bitmap, rect);
                else if (layout.Missing is { } missing)
                {
                    var text = Format(missing, FontFor(TapeStyle.Plain with { Italic = true }), SizeFor(TapeStyle.Plain) - 1, PlaceholderBrush);
                    text.MaxTextWidth = Math.Max(40, Bounds.Width - 2 * padX);
                    text.MaxLineCount = 1;
                    context.DrawText(text, rect.TopLeft);
                }
                var imageSelected = selected && (i > start.Block || start.Offset == 0) && (i < end.Block || end.Offset == 1);
                if (imageSelected)
                    context.DrawRectangle(null, SelectedPen, rect);
                if ((imageSelected || _resizing?.Block == i) && _crop is null)
                    context.FillRectangle(GripBrush, GripRect(rect));
            }
        }

        if (CropFrame() is { } frame && _layouts[_crop!.Value.Block].ImageRect is { } picture)
        {
            DrawCrop(context, picture, frame);
            return;
        }

        if (focused && _layouts.Count > 0)
            context.FillRectangle(CaretBrush, CaretRect(_caret));
    }

    /// <summary>The crop frame: the cut parts shaded, the frame, its rule of thirds and its eight handles.</summary>
    private static void DrawCrop(DrawingContext context, Rect picture, Rect frame)
    {
        context.FillRectangle(CropShade, new Rect(picture.X, picture.Y, picture.Width, Math.Max(0, frame.Y - picture.Y)));
        context.FillRectangle(CropShade, new Rect(picture.X, frame.Bottom, picture.Width, Math.Max(0, picture.Bottom - frame.Bottom)));
        context.FillRectangle(CropShade, new Rect(picture.X, frame.Y, Math.Max(0, frame.X - picture.X), frame.Height));
        context.FillRectangle(CropShade, new Rect(frame.Right, frame.Y, Math.Max(0, picture.Right - frame.Right), frame.Height));
        context.DrawRectangle(null, CropPen, frame);
        for (var i = 1; i <= 2; i++)
        {
            var x = frame.X + frame.Width * i / 3;
            var y = frame.Y + frame.Height * i / 3;
            context.DrawLine(ThirdsPen, new Point(x, frame.Y), new Point(x, frame.Bottom));
            context.DrawLine(ThirdsPen, new Point(frame.X, y), new Point(frame.Right, y));
        }
        var center = frame.Center;
        foreach (var x in new[] { frame.X, center.X, frame.Right })
            foreach (var y in new[] { frame.Y, center.Y, frame.Bottom })
                if (x != center.X || y != center.Y)
                {
                    var handle = new Rect(x - CropHandleSize / 2, y - CropHandleSize / 2, CropHandleSize, CropHandleSize);
                    context.FillRectangle(Brushes.White, handle);
                    context.DrawRectangle(null, HandlePen, handle);
                }
    }
}
