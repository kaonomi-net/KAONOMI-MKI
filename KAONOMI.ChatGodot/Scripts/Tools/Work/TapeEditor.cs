using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Views;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Work;

/// <summary>
/// WYSIWYG editor of the tape of /work, the port of the RichTapeEditor of the Avalonia chat: the document
/// (<see cref="TapeDocument"/>) is laid out and drawn by this control itself (Godot's TextEdit has neither formatting
/// to the character nor pictures inside the text). Text formatted to the character (font, size, bold, italic,
/// underline, strikethrough, color, link) in paragraphs (left, centered, right; Normal, Title, Subtitle, Heading 1 to 3),
/// pictures as blocks (resizable by the grip of their corner, croppable), blocks of code in the syntax coloring of their
/// language. The mouse and the keyboard work as in a word processor: caret, selection (Shift, drag, double-click word,
/// triple-click paragraph), Backspace / Delete (Alt / Ctrl for words), Enter, copy / cut / paste (the formatting kept
/// inside the application), undo / redo, Ctrl / ⌘ + B, I, U, Ctrl / ⌘ + Alt + 0…3; Ctrl / ⌘ + click opens a link, a
/// double-click opens a picture or edits a block of code. <see cref="Text"/> is the tape in its file format, written
/// back (<see cref="TextCommitted"/>) after each change.
/// </summary>
public partial class TapeEditor : Control
{
    public const string RewriteSpelling = "spelling";
    public const string RewriteReadability = "readability";

    /// <summary>Languages of 🌐 Translate: the name given to the LLM and the one shown in the menu.</summary>
    public static readonly (string Language, string Label)[] TranslationLanguages =
        [("English", "English 🇬🇧"), ("German", "Deutsch 🇩🇪"), ("Dutch", "Nederlands 🇳🇱")];

    /// <summary>Size of the text without a size of its own.</summary>
    public const double DefaultSize = 13;

    private const float PaddingX = 14;
    private const float PaddingY = 10;
    private const float ImageMargin = 6;
    private const float GripSize = 12;
    private const int MinImageWidth = 60;
    private const float CodePadding = 10;
    private const float CodeLabelHeight = 14;
    private const float CropHandleSize = 9;
    private const float MinCropPixels = 12;

    private static Color CaretColor => Eve.Eve.Cyan;
    private static Color SelectionColor => Eve.Eve.Cyan with { A = 0.28f };
    private static Color InactiveSelectionColor => Eve.Eve.IsLight ? Eve.Eve.Sunken(0.08f) : new Color(1, 1, 1, 0.1f);
    private static Color GripColor => Eve.Eve.Cyan with { A = 0.8f };
    private static Color PlaceholderColor => Eve.Eve.Muted with { A = 0.8f };
    private static Color CodeBackground => Eve.Eve.IsLight ? new Color(0.95f, 0.95f, 0.95f) : Eve.Eve.Sunken(0.35f);
    private static Color CodeBorder => Eve.Eve.Edge with { A = 0.5f };

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
    /// True: a plain click on a link opens it (released where it was pressed, without dragging: a selection made
    /// across a link does not open it), as in a wiki; false: only ⌘/Ctrl + click opens it, as in /work and the journal.
    /// </summary>
    public bool LinksOpenOnClick { get; set; }

    /// <summary>The link under a plain press (<see cref="LinksOpenOnClick"/>), opened when the button is released there.</summary>
    private (string Address, Vector2 At)? _pressedLink;

    /// <summary>Loads the picture of a local path (null when it cannot be read).</summary>
    public Func<string, Task<Texture2D?>>? LoadImage { get; set; }

    /// <summary>Opens a picture (double-click).</summary>
    public Action<string>? OpenImage { get; set; }

    /// <summary>Opens the address of a link (Ctrl / ⌘ + click).</summary>
    public Action<string>? OpenLink { get; set; }

    /// <summary>Crops a picture: its local path and the part kept (0 to 1); returns the path of the cropped copy or null.</summary>
    public Func<string, Rect2, Task<string?>>? CropImage { get; set; }

    /// <summary>Raised after each change with the tape in its file format.</summary>
    public event Action<string>? TextCommitted;

    /// <summary>Raised when the caret or the selection moves, or the formatting changes (the toolbar follows).</summary>
    public event Action? SelectionChanged;

    /// <summary>Raised when the crop frame shows or goes away.</summary>
    public event Action? CropChanged;

    /// <summary>A double-click on a block of code: its language, its code, and the action that replaces them (false when the tape changed meanwhile).</summary>
    public event Action<string, string, Func<string, string, bool>>? EditCodeRequested;

    /// <summary>A right click, at this point of the editor: the view shows the menu.</summary>
    public event Action<Vector2>? ContextMenuRequested;

    /// <summary>Ctrl / ⌘ + wheel: +1 zooms in, -1 zooms out.</summary>
    public event Action<int>? ZoomRequested;

    // ── State ──

    private TapeDocument _document = TapeDocument.Parse(string.Empty);
    private string _lastText = string.Empty;
    private TapePosition _caret;
    private TapePosition _anchor;
    private TapeStyle? _pendingStyle;
    private float? _desiredX;
    private float _zoom = 1;
    private bool _busy;

    private readonly List<BlockLayout> _layouts = [];
    private float _layoutWidth = -1;
    private bool _layoutDirty = true;
    private readonly Dictionary<string, List<TextLine>> _paragraphCache = new();

    private readonly Dictionary<string, Texture2D?> _images = new();
    private readonly HashSet<string> _loading = [];

    private double _blinkTime;
    private bool _caretVisible = true;

    private readonly List<(string Text, TapePosition Caret)> _undo = [];
    private readonly List<(string Text, TapePosition Caret)> _redo = [];
    private DateTime _lastTyping;
    private bool _typing;

    private bool _selecting;
    private (int Block, float StartX, int StartWidth)? _resizing;
    private (int Block, Rect2 Part)? _crop;
    private (CropHandle Handle, Vector2 Start, Rect2 Part)? _cropDrag;

    // Clicks counted by hand (Godot gives the double-click, not the triple one).
    private ulong _lastClickTime;
    private Vector2 _lastClickPosition;
    private int _clickCount;

    /// <summary>The last copy, to paste it with its formatting (the clipboard of the system gets its text).</summary>
    private static (string Plain, string Rich)? _clip;

    public TapeEditor()
    {
        FocusMode = FocusModeEnum.All;
        MouseDefaultCursorShape = CursorShape.Ibeam;
        ClipContents = true;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
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
            Callable.From(BringCaretIntoView).CallDeferred();
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>True until the caret was shown once after the tape was read (the editor may have no size yet).</summary>
    private bool _revealCaret;

    /// <summary>The zoom of the page (text, pictures, code).</summary>
    public float Zoom
    {
        get => _zoom;
        set
        {
            if (Math.Abs(value - _zoom) < 0.0001f)
                return;
            _zoom = value;
            _paragraphCache.Clear();
            Relayout();
            Callable.From(BringCaretIntoView).CallDeferred();
        }
    }

    /// <summary>True while a long task runs on the selection: the pointer shows the wait cursor.</summary>
    public bool IsBusy
    {
        get => _busy;
        set
        {
            _busy = value;
            MouseDefaultCursorShape = value ? CursorShape.Busy : CursorShape.Ibeam;
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
        GrabFocus();
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
        GrabFocus();
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
        ResetBlink();
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
        GrabFocus();
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
        GrabFocus();
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
            GrabFocus();
            return;
        }
        Remember();
        var (start, end) = Selection;
        for (var block = start.Block; block <= end.Block; block++)
            if (_document.Blocks[block] is TapeParagraph paragraph)
                paragraph.Restyle(block == start.Block ? start.Offset : 0, block == end.Block ? end.Offset : paragraph.Length, change);
        Commit();
        GrabFocus();
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
        GrabFocus();
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
        GrabFocus();
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
        GrabFocus();
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
        _crop = (Selection.Start.Block, new Rect2(0, 0, 1, 1));
        GrabFocus();
        QueueRedraw();
        CropChanged?.Invoke();
    }

    public void CancelCrop()
    {
        if (_crop is null)
            return;
        _crop = null;
        _cropDrag = null;
        QueueRedraw();
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
        QueueRedraw();
        CropChanged?.Invoke();
        if (crop.Part.Size.X >= 0.999f && crop.Part.Size.Y >= 0.999f)
            return;
        var path = await cropImage(JournalMarkdown.LocalPath(image.Url), crop.Part);
        if (!IsInstanceValid(this) || path is null || crop.Block >= _document.Blocks.Count || !ReferenceEquals(_document.Blocks[crop.Block], image))
            return;
        Remember();
        var width = Math.Max(MinImageWidth, (int)Math.Round(image.Width * crop.Part.Size.X));
        var cropped = (TapeImage)TapeDocument.Parse(JournalMarkdown.ImageMarkdown(path, width)).Blocks[0];
        cropped.Alignment = image.Alignment;
        _document.Blocks[crop.Block] = cropped;
        _anchor = new TapePosition(crop.Block, 0);
        _caret = new TapePosition(crop.Block, 1);
        Commit();
    }

    private Rect2? CropFrame()
    {
        if (_crop is not { } crop || crop.Block >= _layouts.Count || _layouts[crop.Block].ImageRect is not { } image)
            return null;
        return new Rect2(image.Position.X + crop.Part.Position.X * image.Size.X, image.Position.Y + crop.Part.Position.Y * image.Size.Y,
            crop.Part.Size.X * image.Size.X, crop.Part.Size.Y * image.Size.Y);
    }

    private CropHandle CropHandleAt(Vector2 point)
    {
        if (CropFrame() is not { } frame)
            return CropHandle.None;
        const float grab = 7;
        var handle = CropHandle.None;
        if (Math.Abs(point.X - frame.Position.X) <= grab) handle |= CropHandle.Left;
        else if (Math.Abs(point.X - frame.End.X) <= grab) handle |= CropHandle.Right;
        if (Math.Abs(point.Y - frame.Position.Y) <= grab) handle |= CropHandle.Top;
        else if (Math.Abs(point.Y - frame.End.Y) <= grab) handle |= CropHandle.Bottom;
        if (handle != CropHandle.None && frame.Grow(grab).HasPoint(point))
            return handle;
        return frame.HasPoint(point) ? CropHandle.Move : CropHandle.None;
    }

    private void DragCrop(Vector2 point)
    {
        if (_crop is not { } crop || _cropDrag is not { } drag || _layouts[crop.Block].ImageRect is not { } image)
            return;
        var dx = (point.X - drag.Start.X) / image.Size.X;
        var dy = (point.Y - drag.Start.Y) / image.Size.Y;
        float left = drag.Part.Position.X, top = drag.Part.Position.Y, right = drag.Part.End.X, bottom = drag.Part.End.Y;
        var minWidth = MinCropPixels / image.Size.X;
        var minHeight = MinCropPixels / image.Size.Y;
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
        _crop = (crop.Block, new Rect2(left, top, right - left, bottom - top));
        QueueRedraw();
    }

    private static CursorShape CropCursor(CropHandle handle) => handle switch
    {
        CropHandle.Move => CursorShape.Move,
        CropHandle.Left or CropHandle.Right => CursorShape.Hsize,
        CropHandle.Top or CropHandle.Bottom => CursorShape.Vsize,
        CropHandle.Left | CropHandle.Top or CropHandle.Right | CropHandle.Bottom => CursorShape.Fdiagsize,
        CropHandle.Right | CropHandle.Top or CropHandle.Left | CropHandle.Bottom => CursorShape.Bdiagsize,
        _ => CursorShape.Arrow
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
    public void InsertImagesAt(IReadOnlyList<string> paths, Vector2 at) => InsertImages(paths, PositionAt(at));

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
        GrabFocus();
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
        DisplayServer.ClipboardSet(plain);
    }

    /// <summary>Pastes the clipboard at the caret (with its formatting when it is the last copy made here).</summary>
    public void Paste()
    {
        var text = DisplayServer.ClipboardGet();
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

    private static bool IsMac => OS.GetName() == "macOS";

    private static bool IsCommand(InputEventWithModifiers e) => IsMac ? e.MetaPressed : e.CtrlPressed;

    private void OnKey(InputEventKey e)
    {
        var shift = e.ShiftPressed;
        var command = IsCommand(e);
        var word = IsMac ? e.AltPressed : e.CtrlPressed;
        var key = e.Keycode;

        if (_crop is not null)
        {
            if (key is Key.Enter or Key.KpEnter)
                ApplyCrop();
            else if (key == Key.Escape)
                CancelCrop();
            AcceptEvent();
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
            case Key.Pageup:
            case Key.Pagedown:
                Move(Vertical(key == Key.Pageup ? -1 : 1, pages: true), shift, keepX: true);
                break;
            case Key.Backspace:
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
            case Key.Enter or Key.KpEnter:
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
            case Key.Key0 or Key.Kp0 when command && e.AltPressed:
                SetParagraphKind(TapeParagraphKind.Normal);
                break;
            case Key.Key1 or Key.Kp1 when command && e.AltPressed:
                SetParagraphKind(TapeParagraphKind.Heading1);
                break;
            case Key.Key2 or Key.Kp2 when command && e.AltPressed:
                SetParagraphKind(TapeParagraphKind.Heading2);
                break;
            case Key.Key3 or Key.Kp3 when command && e.AltPressed:
                SetParagraphKind(TapeParagraphKind.Heading3);
                break;
            default:
                handled = false;
                break;
        }
        if (handled)
        {
            AcceptEvent();
            return;
        }

        // Typed text (the shortcuts of the system and of the chat go on).
        if (!command && !(e.CtrlPressed && !IsMac) && e.Unicode >= 32 && e.Unicode != 127)
        {
            var text = char.ConvertFromUtf32((int)e.Unicode);
            Remember(typing: true);
            InsertText(text);
            _desiredX = null;
            Commit();
            AcceptEvent();
        }
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
        ResetBlink();
        QueueRedraw();
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
        _desiredX ??= caret.Position.X;
        var page = GetParent() is Control parent ? parent.Size.Y : 300;
        if (FindScroll() is { } scroll)
            page = scroll.Size.Y;
        var distance = pages ? Math.Max(caret.Size.Y, page) - caret.Size.Y : 1;
        var y = direction < 0 ? caret.Position.Y - distance : caret.End.Y + distance;
        if (!pages && _document.Blocks[_caret.Block] is not TapeParagraph)
        {
            var layout = _layouts[_caret.Block];
            y = direction < 0 ? layout.Top - 1 : layout.Top + layout.Height + 1;
        }
        if (y < PaddingY * _zoom)
            return new TapePosition(0, 0);
        if (y > TotalHeight - PaddingY * _zoom)
            return DocumentEnd;
        return PositionAt(new Vector2(_desiredX.Value, y));
    }

    // ── Mouse ──

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventKey { Pressed: true } key:
                OnKey(key);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.WheelUp or MouseButton.WheelDown } wheel:
                OnWheel(wheel);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } right:
                GrabFocus();
                AcceptEvent();
                ContextMenuRequested?.Invoke(right.Position);
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } press:
                OnPress(press);
                break;
            case InputEventMouseButton { Pressed: false, ButtonIndex: MouseButton.Left } release:
                OnRelease();
                // A plain click on a link (pressed and released at the same place): it opens.
                if (_pressedLink is { } pressed)
                {
                    _pressedLink = null;
                    if (release.Position.DistanceTo(pressed.At) < 4 && !HasSelection)
                        OpenLink?.Invoke(pressed.Address);
                }
                break;
            case InputEventMouseMotion motion:
                OnMotion(motion);
                break;
        }
    }

    private void OnWheel(InputEventMouseButton wheel)
    {
        var up = wheel.ButtonIndex == MouseButton.WheelUp;
        if (IsCommand(wheel))
        {
            ZoomRequested?.Invoke(up ? 1 : -1);
            AcceptEvent();
            return;
        }
        if (FindScroll() is { } scroll)
        {
            scroll.ScrollVertical -= (int)((up ? 1 : -1) * 48 * Math.Max(0.25f, wheel.Factor));
            AcceptEvent();
        }
    }

    private void OnPress(InputEventMouseButton e)
    {
        GrabFocus();
        AcceptEvent();
        var position = e.Position;

        // Clicks counted here: 1, 2 (word, open), 3 (paragraph).
        var now = Time.GetTicksMsec();
        _clickCount = now - _lastClickTime < 450 && position.DistanceTo(_lastClickPosition) < 5 ? _clickCount + 1 : 1;
        _lastClickTime = now;
        _lastClickPosition = position;
        var clicks = _clickCount;

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
            if (_layouts[i].ImageRect is { } rect && GripRect(rect).Grow(3).HasPoint(position) && _document.Blocks[i] is TapeImage image)
            {
                Remember();
                _resizing = (i, position.X, image.Width);
                return;
            }

        if (IsCommand(e) && LinkAt(position) is { } address)
        {
            OpenLink?.Invoke(address);
            return;
        }
        // In a wiki a plain click opens the link, once released at the same place (the caret goes there meanwhile).
        _pressedLink = LinksOpenOnClick && clicks == 1 && !e.ShiftPressed && !e.AltPressed && LinkAt(position) is { } plain ? (plain, position) : null;

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
                if (!IsInstanceValid(this) || before != _document.Serialize())
                    return false;
                Remember();
                _document.Blocks[index] = new TapeCode(CodeLanguage(language), text.Replace("\r\n", "\n").TrimEnd('\n'));
                _anchor = new TapePosition(index, 0);
                _caret = new TapePosition(index, 1);
                Commit();
                GrabFocus();
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

        if (_document.Blocks[at.Block] is not TapeParagraph && BoxOf(_layouts[at.Block]) is { } box && box.HasPoint(position))
        {
            _anchor = new TapePosition(at.Block, 0);
            _caret = new TapePosition(at.Block, 1);
            Changed();
            return;
        }

        Move(at, e.ShiftPressed);
        _selecting = true;
    }

    private void OnMotion(InputEventMouseMotion e)
    {
        var position = e.Position;
        if (_crop is not null)
        {
            if (_cropDrag is not null)
                DragCrop(position);
            else
                MouseDefaultCursorShape = CropCursor(CropHandleAt(position));
            return;
        }
        if (_resizing is { } resizing && _document.Blocks[resizing.Block] is TapeImage image)
        {
            var available = (int)Math.Max(MinImageWidth, (Size.X - 2 * PaddingX * _zoom) / _zoom);
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
        var overGrip = _layouts.Any(l => l.ImageRect is { } rect && GripRect(rect).Grow(3).HasPoint(position));
        var overLink = !overGrip && (LinksOpenOnClick || IsCommand(e)) && LinkAt(position) is not null;
        MouseDefaultCursorShape = overGrip ? CursorShape.Fdiagsize : overLink ? CursorShape.PointingHand : CursorShape.Ibeam;
    }

    private void OnRelease()
    {
        _cropDrag = null;
        if (_resizing is not null)
        {
            _resizing = null;
            Commit();
        }
        _selecting = false;
    }

    private string? LinkAt(Vector2 point)
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

    private TapePosition PositionAt(Vector2 point)
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
            var bestDistance = float.MaxValue;
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
        return new TapePosition(index, BoxOf(layout) is { } rect && point.X > rect.GetCenter().X ? 1 : 0);
    }

    // ── Focus and caret ──

    public override void _Notification(int what)
    {
        if (what == NotificationFocusEnter || what == NotificationFocusExit)
            ResetBlink();
        else if (what == NotificationResized && Math.Abs(Size.X - _layoutWidth) > 0.5f)
            Relayout();
    }

    public override void _Process(double delta)
    {
        if (!HasFocus())
            return;
        _blinkTime += delta;
        if (_blinkTime >= 0.53)
        {
            _blinkTime = 0;
            _caretVisible = !_caretVisible;
            QueueRedraw();
        }
    }

    private void ResetBlink()
    {
        _caretVisible = true;
        _blinkTime = 0;
        QueueRedraw();
    }

    private ScrollContainer? FindScroll()
    {
        for (var node = GetParent(); node is not null; node = node.GetParent())
            if (node is ScrollContainer scroll)
                return scroll;
        return null;
    }

    private void BringCaretIntoView()
    {
        if (!IsInstanceValid(this) || !IsInsideTree() || FindScroll() is not { } scroll)
            return;
        EnsureLayout();
        if (_layouts.Count == 0)
            return;
        var caret = CaretRect(_caret).Grow(8);
        var top = GlobalPosition.Y + caret.Position.Y - scroll.GlobalPosition.Y;
        var bottom = top + caret.Size.Y;
        if (top < 0)
            scroll.ScrollVertical += (int)Math.Floor(top);
        else if (bottom > scroll.Size.Y)
            scroll.ScrollVertical += (int)Math.Ceiling(bottom - scroll.Size.Y);
    }

    private Rect2 CaretRect(TapePosition position)
    {
        var layout = _layouts[position.Block];
        if (layout.Lines is { } lines)
        {
            var offset = Math.Clamp(position.Offset, 0, _document.Blocks[position.Block].Length);
            var line = lines.FirstOrDefault(l => offset < l.End) ?? lines[^1];
            var x = PaddingX * _zoom + line.Shift + line.Xs[Math.Clamp(offset - line.Start, 0, line.Xs.Length - 1)];
            return new Rect2(x, layout.Top + line.Top, 1.5f, line.Height);
        }
        var box = BoxOf(layout) ?? new Rect2(PaddingX * _zoom, layout.Top, 1, layout.Height);
        return new Rect2(position.Offset == 0 ? box.Position.X - 3 : box.End.X + 2, box.Position.Y, 1.5f, box.Size.Y);
    }

    private static Rect2? BoxOf(BlockLayout layout) => layout.ImageRect ?? layout.CodeRect;

    private static Rect2 GripRect(Rect2 image) => new(image.End.X - GripSize, image.End.Y - GripSize, GripSize, GripSize);

    // ── Layout ──

    /// <summary>A line of a paragraph: its characters, the x of each caret place, the pieces drawn.</summary>
    private sealed class TextLine
    {
        public float Top, Height, Ascent, Shift;
        public int Start, End, Trailing;
        public float[] Xs = [];
        public readonly List<Piece> Pieces = [];
    }

    /// <summary>Characters of one style on a line.</summary>
    private sealed class Piece
    {
        public string Text = string.Empty;
        public float X;
        public Font Font = null!;
        public int Size;
        public Color Color;
        public bool Underline, Strike;
    }

    private sealed class BlockLayout
    {
        public float Top, Height;
        public List<TextLine>? Lines;
        public Texture2D? Image;
        public Rect2? ImageRect;
        public string? Missing;
        public Rect2? CodeRect;
        public List<(float Y, List<(string Text, float X, Color Color)> Pieces)>? Code;
        public string? CodeLabel;
    }

    private float TotalHeight => _layouts.Count == 0 ? 2 * PaddingY * _zoom : _layouts[^1].Top + _layouts[^1].Height + PaddingY * _zoom;

    private void Relayout()
    {
        _layoutDirty = true;
        if (Size.X > 0)
            EnsureLayout();
        QueueRedraw();
    }

    private void EnsureLayout()
    {
        if (!_layoutDirty && _layouts.Count == _document.Blocks.Count && Math.Abs(Size.X - _layoutWidth) <= 0.5f)
            return;
        BuildLayout(Size.X > 0 ? Size.X : 600);
        var height = TotalHeight;
        if (Math.Abs(CustomMinimumSize.Y - height) > 0.5f)
            CustomMinimumSize = new Vector2(0, height);
        if (_revealCaret && Size.X > 0 && IsInsideTree())
        {
            _revealCaret = false;
            // Once the scroll knows the new height.
            GetTree().CreateTimer(0.05).Connect(SceneTreeTimer.SignalName.Timeout, Callable.From(BringCaretIntoView));
        }
    }

    private void BuildLayout(float width)
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
                top += (float)TapeParagraph.SpaceBefore(styled.Kind) * z;
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
                    float shownHeight;
                    if (layout.Image is { } texture)
                        shownHeight = shownWidth * texture.GetHeight() / Math.Max(1f, texture.GetWidth());
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
                    layout.ImageRect = new Rect2(PaddingX * z + shift, top + ImageMargin * z, shownWidth, shownHeight);
                    layout.Height = shownHeight + 2 * ImageMargin * z;
                    break;
                }
                case TapeCode code:
                {
                    var (lines, height) = LayoutCode(code, textWidth - 2 * CodePadding * z);
                    layout.Code = lines;
                    layout.CodeLabel = string.IsNullOrEmpty(code.Language) ? "code" : code.Language;
                    layout.CodeRect = new Rect2(PaddingX * z, top + ImageMargin * z, textWidth, height + 2 * CodePadding * z + CodeLabelHeight * z);
                    layout.Height = layout.CodeRect.Value.Size.Y + 2 * ImageMargin * z;
                    break;
                }
            }
            _layouts.Add(layout);
            top += layout.Height;
        }
    }

    /// <summary>The lines of a paragraph wrapped in <paramref name="width"/>: words kept whole when they fit, cut otherwise.</summary>
    private List<TextLine> LayoutParagraph(TapeParagraph paragraph, float width)
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
        var advance = new float[n];
        var i = 0;
        while (i < n)
        {
            var run = runOf[i];
            var font = fonts[run];
            var size = sizes[run];
            if (char.IsWhiteSpace(text[i]))
            {
                advance[i] = text[i] == '\t'
                    ? 4 * font.GetStringSize(" ", HorizontalAlignment.Left, -1, size).X
                    : font.GetStringSize(text[i].ToString(), HorizontalAlignment.Left, -1, size).X;
                i++;
                continue;
            }
            var start = i;
            while (i < n && runOf[i] == run && !char.IsWhiteSpace(text[i]))
                i++;
            var word = text[start..i];
            if (word.Length <= 48)
            {
                float previous = 0;
                for (var k = 1; k <= word.Length; k++)
                {
                    if (k < word.Length && char.IsHighSurrogate(word[k - 1]))
                        continue;
                    var w = font.GetStringSize(word[..k], HorizontalAlignment.Left, -1, size).X;
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
                    advance[start + k + length - 1] = font.GetStringSize(word.Substring(k, length), HorizontalAlignment.Left, -1, size).X;
                }
        }

        // Lines: a break after a space when the next word does not fit, inside a word too long for a line.
        var ranges = new List<(int Start, int End)>();
        var lineStart = 0;
        float x = 0;
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
        float top = 0;
        foreach (var (from, to) in ranges)
        {
            var line = new TextLine { Start = from, End = to, Top = top, Xs = new float[to - from + 1] };
            for (var k = from; k < to; k++)
                line.Xs[k - from + 1] = line.Xs[k - from] + advance[k];
            var trailing = 0;
            for (var k = to - 1; k >= from && char.IsWhiteSpace(text[k]); k--)
                trailing++;
            line.Trailing = trailing;
            var visible = line.Xs[to - from - trailing];

            float height = 0, ascent = 0;
            if (to == from)
            {
                var font = FontFor(emptyStyle);
                var size = SizeFor(emptyStyle);
                height = font.GetHeight(size);
                ascent = font.GetAscent(size);
            }
            for (var k = from; k < to; k++)
            {
                var run = runOf[k];
                height = Math.Max(height, fonts[run].GetHeight(sizes[run]));
                ascent = Math.Max(ascent, fonts[run].GetAscent(sizes[run]));
            }
            line.Height = height;
            line.Ascent = ascent;
            line.Shift = paragraph.Alignment switch
            {
                TapeAlignment.Center => Math.Max(0, (width - visible) / 2),
                TapeAlignment.Right => Math.Max(0, width - visible),
                _ => 0
            };

            // Pieces of one run, one word each with the spaces after it (tabs are spaces, not drawn): each word drawn
            // where its caret places were measured. A whole run drawn at once drifts from them along the line (a space
            // measured alone is a little wider than in the text), and the caret showed a character too far right.
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
                while (k2 < to && runOf[k2] == run && text[k2] != '\t'
                       && !(k2 > start && char.IsWhiteSpace(text[k2 - 1]) && !char.IsWhiteSpace(text[k2])))
                    k2++;
                var style = styles[run];
                line.Pieces.Add(new Piece
                {
                    Text = text[start..k2],
                    X = line.Xs[start - from],
                    Font = fonts[run],
                    Size = sizes[run],
                    Color = style.Link is not null && style.Color is null ? WorkPalette.Link : WorkPalette.Adapt(style.Color, WorkPalette.DefaultText),
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

    /// <summary>The font of the blocks of code (the text itself is in Arial by default: <see cref="WorkPalette.TextNames"/>).</summary>
    private static readonly Font MonoFont = new SystemFont { FontNames = WorkPalette.MonoNames, Antialiasing = TextServer.FontAntialiasing.Gray, Fallbacks = [Eve.Eve.Emoji] };

    /// <summary>The lines of a block of code, cut at the width, in the colors of its language.</summary>
    private (List<(float Y, List<(string Text, float X, Color Color)> Pieces)> Lines, float Height) LayoutCode(TapeCode code, float width)
    {
        var font = MonoFont;
        var size = (int)Math.Round(13 * _zoom);
        var charWidth = Math.Max(1, font.GetStringSize("M", HorizontalAlignment.Left, -1, size).X);
        var columns = Math.Max(8, (int)(width / charWidth));
        var lineHeight = font.GetHeight(size);
        var colors = WorkPalette.CodeColors(code.Language, code.Code);
        var lines = new List<(float, List<(string, float, Color)>)>();
        float y = 0;
        var offset = 0;
        foreach (var raw in code.Code.Split('\n'))
        {
            var start = 0;
            do
            {
                var length = Math.Min(columns, raw.Length - start);
                var pieces = new List<(string, float, Color)>();
                var k = start;
                while (k < start + length)
                {
                    var color = colors[offset + k];
                    var from = k;
                    while (k < start + length && colors[offset + k] == color)
                        k++;
                    pieces.Add((raw[from..k].Replace("\t", "    "), (from - start) * charWidth, color ?? WorkPalette.DefaultText));
                }
                lines.Add((y, pieces));
                y += lineHeight;
                start += length;
            } while (start < raw.Length);
            offset += raw.Length + 1;
        }
        return (lines, y);
    }

    private readonly Dictionary<(string?, bool, bool), Font> _fonts = new();

    private Font FontFor(TapeStyle style)
    {
        var key = (style.Font, style.Bold, style.Italic);
        if (_fonts.TryGetValue(key, out var font))
            return font;
        var names = style.Font is { Length: > 0 } family ? new[] { family }.Concat(WorkPalette.TextNames).ToArray() : WorkPalette.TextNames;
        var system = new SystemFont
        {
            FontNames = names,
            FontWeight = style.Bold ? 700 : 400,
            FontItalic = style.Italic,
            Antialiasing = TextServer.FontAntialiasing.Gray,
            Fallbacks = [Eve.Eve.Emoji]
        };
        font = system;
        // A family without a bold or an italic face: the face is made bolder or slanted.
        var found = system.GetFontStyle();
        var embolden = style.Bold && !found.HasFlag(TextServer.FontStyle.Bold);
        var slant = style.Italic && !found.HasFlag(TextServer.FontStyle.Italic);
        if (embolden || slant)
            font = new FontVariation
            {
                BaseFont = system,
                VariationEmbolden = embolden ? 0.7f : 0,
                VariationTransform = slant ? new Transform2D(1, 0.2f, 0, 1, 0, 0) : Transform2D.Identity
            };
        _fonts[key] = font;
        return font;
    }

    private int SizeFor(TapeStyle style) => Math.Max(4, (int)Math.Round((style.Size ?? DefaultSize) * _zoom));

    private Texture2D? ImageFor(string path)
    {
        if (_images.TryGetValue(path, out var texture))
            return texture;
        if (LoadImage is not { } load || !_loading.Add(path))
            return null;
        _ = LoadAsync();
        return null;

        async Task LoadAsync()
        {
            Texture2D? loaded = null;
            try
            {
                loaded = await load(path);
            }
            catch (Exception)
            {
                // Shown as "picture not found".
            }
            Bind.OnMain(() =>
            {
                if (!IsInstanceValid(this))
                    return;
                _images[path] = loaded;
                _loading.Remove(path);
                Relayout();
            });
        }
    }

    // ── Drawing ──

    public override void _Draw()
    {
        EnsureLayout();
        var z = _zoom;
        var padX = PaddingX * z;
        if (_document.Blocks.Count == 1 && _document.Blocks[0].Length == 0 && !string.IsNullOrEmpty(Placeholder))
        {
            var font = MonoFont;
            var size = SizeFor(TapeStyle.Plain);
            DrawMultilineString(font, new Vector2(padX, PaddingY * z + font.GetAscent(size)), Placeholder, HorizontalAlignment.Left,
                Math.Max(40, Size.X - 2 * padX), size, -1, PlaceholderColor);
        }

        var (start, end) = Selection;
        var selection = HasFocus() ? SelectionColor : InactiveSelectionColor;
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
                            DrawRect(new Rect2(x0, layout.Top + line.Top, x1 - x0, line.Height), selection);
                        }
                    }
                    if (i < end.Block)
                    {
                        var caret = CaretRect(new TapePosition(i, _document.Blocks[i].Length));
                        DrawRect(new Rect2(caret.Position.X, caret.Position.Y, 6, caret.Size.Y), selection);
                    }
                }
                foreach (var line in lines)
                {
                    var baseline = layout.Top + line.Top + line.Ascent;
                    foreach (var piece in line.Pieces)
                    {
                        var x = padX + line.Shift + piece.X;
                        DrawString(piece.Font, new Vector2(x, baseline), piece.Text, HorizontalAlignment.Left, -1, piece.Size, piece.Color);
                        if (piece.Underline || piece.Strike)
                        {
                            var w = piece.Font.GetStringSize(piece.Text, HorizontalAlignment.Left, -1, piece.Size).X;
                            var thickness = Math.Max(1, piece.Size / 14f);
                            if (piece.Underline)
                                DrawLine(new Vector2(x, baseline + 2 * z), new Vector2(x + w, baseline + 2 * z), piece.Color, thickness);
                            if (piece.Strike)
                            {
                                var y = baseline - piece.Font.GetAscent(piece.Size) * 0.3f;
                                DrawLine(new Vector2(x, y), new Vector2(x + w, y), piece.Color, thickness);
                            }
                        }
                    }
                }
            }
            else if (layout.CodeRect is { } box && layout.Code is { } code)
            {
                var style = new StyleBoxFlat { BgColor = CodeBackground, BorderColor = CodeBorder };
                style.SetBorderWidthAll(1);
                style.SetCornerRadiusAll(4);
                DrawStyleBox(style, box);
                var font = MonoFont;
                var labelSize = Math.Max(6, (int)Math.Round(10 * z));
                var label = layout.CodeLabel ?? "code";
                var labelWidth = font.GetStringSize(label, HorizontalAlignment.Left, -1, labelSize).X;
                DrawString(font, new Vector2(box.End.X - CodePadding * z - labelWidth, box.Position.Y + 4 * z + font.GetAscent(labelSize)), label,
                    HorizontalAlignment.Left, -1, labelSize, Eve.Eve.Muted);
                var size = (int)Math.Round(13 * z);
                var ascent = font.GetAscent(size);
                var origin = new Vector2(box.Position.X + CodePadding * z, box.Position.Y + CodePadding * z + CodeLabelHeight * z - 4 * z);
                foreach (var (y, pieces) in code)
                    foreach (var (text, x, color) in pieces)
                        DrawString(font, origin + new Vector2(x, y + ascent), text, HorizontalAlignment.Left, -1, size, color);
                var codeSelected = selected && (i > start.Block || start.Offset == 0) && (i < end.Block || end.Offset == 1);
                if (codeSelected)
                {
                    DrawRect(box, selection);
                    DrawRect(box, Eve.Eve.Cyan, false, 2);
                }
            }
            else if (layout.ImageRect is { } rect)
            {
                if (layout.Image is { } texture)
                    DrawTextureRect(texture, rect, false);
                else if (layout.Missing is { } missing)
                {
                    var font = FontFor(TapeStyle.Plain with { Italic = true });
                    var size = SizeFor(TapeStyle.Plain) - 1;
                    DrawString(font, rect.Position + new Vector2(0, font.GetAscent(size)), missing, HorizontalAlignment.Left,
                        Math.Max(40, Size.X - 2 * padX), size, PlaceholderColor);
                }
                var imageSelected = selected && (i > start.Block || start.Offset == 0) && (i < end.Block || end.Offset == 1);
                if (imageSelected)
                {
                    DrawRect(rect, selection);
                    DrawRect(rect, Eve.Eve.Cyan, false, 2);
                }
                if ((imageSelected || _resizing?.Block == i) && _crop is null)
                    DrawRect(GripRect(rect), GripColor);
            }
        }

        if (CropFrame() is { } frame && _layouts[_crop!.Value.Block].ImageRect is { } picture)
        {
            DrawCrop(picture, frame);
            return;
        }

        if (HasFocus() && _caretVisible && _layouts.Count > 0)
            DrawRect(CaretRect(_caret), CaretColor);
    }

    /// <summary>The crop frame: the cut parts shaded, the frame, its rule of thirds and its eight handles.</summary>
    private void DrawCrop(Rect2 picture, Rect2 frame)
    {
        var shade = Eve.Eve.Sunken(0.6f);
        DrawRect(new Rect2(picture.Position.X, picture.Position.Y, picture.Size.X, frame.Position.Y - picture.Position.Y), shade);
        DrawRect(new Rect2(picture.Position.X, frame.End.Y, picture.Size.X, picture.End.Y - frame.End.Y), shade);
        DrawRect(new Rect2(picture.Position.X, frame.Position.Y, frame.Position.X - picture.Position.X, frame.Size.Y), shade);
        DrawRect(new Rect2(frame.End.X, frame.Position.Y, picture.End.X - frame.End.X, frame.Size.Y), shade);
        DrawRect(frame, Colors.White, false, 1.5f);
        var thirds = new Color(1, 1, 1, 0.5f);
        for (var i = 1; i <= 2; i++)
        {
            var x = frame.Position.X + frame.Size.X * i / 3;
            var y = frame.Position.Y + frame.Size.Y * i / 3;
            DrawLine(new Vector2(x, frame.Position.Y), new Vector2(x, frame.End.Y), thirds, 0.75f);
            DrawLine(new Vector2(frame.Position.X, y), new Vector2(frame.End.X, y), thirds, 0.75f);
        }
        var center = frame.GetCenter();
        foreach (var x in new[] { frame.Position.X, center.X, frame.End.X })
            foreach (var y in new[] { frame.Position.Y, center.Y, frame.End.Y })
                if (x != center.X || y != center.Y)
                {
                    var handle = new Rect2(x - CropHandleSize / 2, y - CropHandleSize / 2, CropHandleSize, CropHandleSize);
                    DrawRect(handle, Colors.White);
                    DrawRect(handle, Eve.Eve.Cyan, false, 1.5f);
                }
    }
}
