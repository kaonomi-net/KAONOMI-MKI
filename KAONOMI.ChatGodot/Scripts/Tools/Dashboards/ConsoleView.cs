using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.Chat.Models;
using KAONOMI.ChatGodot.Eve;
using KAONOMI.ChatGodot.Tools.Data;
using KAONOMI.ChatGodot.Tools.Knowledge;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;

namespace KAONOMI.ChatGodot.Tools.Dashboards;

/// <summary>
/// /cmd: a console. One screen, as in a terminal: what the shell writes scrolls in the mono font, and the command is
/// typed on the line of its prompt (<see cref="ConsoleScreen"/>). STOP (or Ctrl+C with nothing selected) kills what
/// runs and starts a new shell, CLEAR (or Ctrl+L) empties the screen, 📝 ANNOTATE annotates the text selected (as
/// the right click does); the status bar names the shell and its folder, or why it ended.
/// </summary>
[ToolView(typeof(ConsoleWindowViewModel), "Console", "⌨", 900, 560)]
public sealed class ConsoleView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (ConsoleWindowViewModel)viewModel;
        var screen = new ConsoleScreen(vm);

        var stop = Ui.Action("■ STOP", screen.Stop, tooltip: "Kills what runs (Ctrl+C) and starts a new shell");
        var clear = Ui.Command("CLEAR", vm.ClearCommand, tooltip: "Empties the screen (Ctrl+L)");
        var annotate = Ui.Action("📝 ANNOTATE", screen.Annotate, tooltip: "Annotates the text selected (right click: Annotate selection…)");
        // A click on it keeps the selection of the screen (the screen keeps the focus).
        annotate.FocusMode = Control.FocusModeEnum.None;
        annotate.Disabled = true;
        screen.Edit.CaretChanged += () => annotate.Disabled = !screen.Edit.HasSelection();
        var toolbar = Ui.Toolbar(Ui.Row(6,
            Ui.Fill(Ui.Label(vm, nameof(vm.ShellName), EveStyle.Muted, 12)),
            DashUi.Dot(EveStyle.Green, vm, nameof(vm.IsRunning)),
            DashUi.Dot(EveStyle.Red, vm, nameof(vm.IsRunning), invert: true),
            annotate, stop, clear));

        var page = Ui.Column(6,
            toolbar,
            Ui.FillHeight(Ui.Margin(screen.Edit, 10, 4, 10, 6)),
            Ui.StatusBar(vm, nameof(vm.Status)));
        page.Ready += () => screen.Edit.CallDeferred(Control.MethodName.GrabFocus);
        return page;
    }
}

/// <summary>
/// The screen of the console: a text where everything before the line being typed is the shell's (it can be selected
/// and copied, not changed) and the end is the command line. A key that edits moves to the end first; Enter sends the
/// line (each line of a paste in turn), ↑ / ↓ go through the lines typed before, Home goes to the start of the line
/// typed. What the shell writes while a line is being typed goes in before it, so the line stays last. The right click
/// opens its own menu (copy, annotate the selection, paste at the command line, select all): the one of Godot would
/// cut and paste in the shell's text.
/// </summary>
internal sealed class ConsoleScreen
{
    /// <summary>The lines kept on screen (the oldest go first).</summary>
    private const int MaxLines = 5000;

    private readonly ConsoleWindowViewModel _vm;
    private readonly ConcurrentQueue<string> _pending = new();
    private int _flushing;
    // Where the line typed begins: everything before it is the shell's.
    private int _startLine;
    private int _startColumn;

    public ConsoleScreen(ConsoleWindowViewModel vm)
    {
        _vm = vm;
        Edit = new TextEdit
        {
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            ContextMenuEnabled = false,
            MiddleMousePasteEnabled = false,
            DragAndDropSelectionEnabled = false,
            CaretMoveOnRightClick = false,
            // The selection stays while 📝 ANNOTATE or a menu is clicked.
            DeselectOnFocusLossEnabled = false,
            EmptySelectionClipboardEnabled = false,
            HighlightCurrentLine = false,
            CaretBlink = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        Edit.AddThemeFontOverride("font", EveStyle.Mono);
        Edit.AddThemeFontSizeOverride("font_size", 13);
        Edit.AddThemeColorOverride("font_color", EveStyle.Text);
        Edit.AddThemeColorOverride("selection_color", EveStyle.Cyan with { A = 0.3f });
        Edit.AddThemeColorOverride("caret_color", EveStyle.Cyan);
        Edit.AddThemeStyleboxOverride("normal", EveStyle.Box(EveStyle.Sunken(0.35f), EveStyle.Edge with { A = 0.35f }, 8));
        Edit.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        Edit.GuiInput += OnInput;

        vm.Output += Received;
        vm.Cleared += Cleared;
        Bind.OnGone(Edit, () =>
        {
            vm.Output -= Received;
            vm.Cleared -= Cleared;
        });
    }

    public TextEdit Edit { get; }

    /// <summary>STOP: the line typed stays as it is (followed by ^C), what runs is killed, a new shell starts.</summary>
    public void Stop()
    {
        Commit();
        Bind.Run(_vm.RestartCommand);
        Edit.GrabFocus();
    }

    /// <summary>📝 Annotate selection…: the annotation of the text selected, with the console as its context.</summary>
    public void Annotate()
    {
        var selection = Edit.GetSelectedText();
        if (selection.Trim().Length > 0)
            Annotations.Open(Edit, AnnotationTarget.Text(selection, "Console"));
    }

    private void ShowMenu(Vector2 at)
    {
        var selected = Edit.HasSelection();
        new ContextMenu()
            .Item("📋 Copy", () => DisplayServer.ClipboardSet(Edit.GetSelectedText()), selected)
            .Item(Annotations.MenuText.Replace("…", " selection…"), Annotate, selected)
            .Separator()
            .Item("📥 Paste", Paste, DisplayServer.ClipboardHas())
            .Item("Select all", Edit.SelectAll)
            .Show(Edit, at);
    }

    /// <summary>Pastes at the end of the command line (never in the shell's text).</summary>
    private void Paste()
    {
        CaretToEnd();
        Edit.InsertTextAtCaret(DisplayServer.ClipboardGet());
        Edit.ClearUndoHistory();
        Edit.GrabFocus();
    }

    private (int Line, int Column) End
    {
        get
        {
            var line = Edit.GetLineCount() - 1;
            return (line, Edit.GetLine(line).Length);
        }
    }

    private bool BeforeStart(int line, int column) => line < _startLine || line == _startLine && column < _startColumn;

    /// <summary>The line being typed (several when a paste brought new lines).</summary>
    private string Typed()
    {
        var (last, column) = End;
        if (BeforeStart(last, column))
            return "";
        var lines = new List<string>();
        for (var i = _startLine; i <= last; i++)
        {
            var text = Edit.GetLine(i);
            lines.Add(i == _startLine ? text[System.Math.Min(_startColumn, text.Length)..] : text);
        }
        return string.Join("\n", lines);
    }

    private void Replace(string typed)
    {
        var (line, column) = End;
        if (!BeforeStart(line, column))
            Edit.RemoveText(_startLine, _startColumn, line, column);
        Edit.InsertText(typed, _startLine, _startColumn);
        Edit.ClearUndoHistory();
        CaretToEnd();
    }

    /// <summary>What is typed becomes the shell's: the next line starts after it.</summary>
    private void Commit() => (_startLine, _startColumn) = End;

    private void CaretToEnd()
    {
        var (line, column) = End;
        Edit.Deselect();
        Edit.SetCaretLine(line);
        Edit.SetCaretColumn(column);
    }

    private void ScrollToEnd() => Callable.From(() =>
    {
        if (GodotObject.IsInstanceValid(Edit))
            Edit.ScrollVertical = Edit.GetVScrollBar().MaxValue;
    }).CallDeferred();

    // The pieces come from the threads of the shell: gathered, then written once per frame.
    private void Received(string text)
    {
        _pending.Enqueue(text);
        if (Interlocked.Exchange(ref _flushing, 1) == 0)
            Callable.From(Flush).CallDeferred();
    }

    private void Flush()
    {
        Interlocked.Exchange(ref _flushing, 0);
        if (!GodotObject.IsInstanceValid(Edit))
            return;
        var output = new StringBuilder();
        while (_pending.TryDequeue(out var piece))
            output.Append(piece);
        if (output.Length == 0)
            return;

        var bar = Edit.GetVScrollBar();
        var following = !bar.Visible || bar.Value + bar.Page >= bar.MaxValue - 1;
        var typing = !BeforeStart(Edit.GetCaretLine(), Edit.GetCaretColumn()) && !Edit.HasSelection();
        var typed = Typed();

        var (line, column) = End;
        if (!BeforeStart(line, column))
            Edit.RemoveText(_startLine, _startColumn, line, column);
        Edit.InsertText(output.ToString(), _startLine, _startColumn);
        Commit();
        Edit.InsertText(typed, _startLine, _startColumn);

        var extra = Edit.GetLineCount() - MaxLines;
        if (extra > 0 && extra <= _startLine)
        {
            Edit.RemoveText(0, 0, extra, 0);
            _startLine -= extra;
        }
        Edit.ClearUndoHistory();
        if (typing)
            CaretToEnd();
        if (following)
            ScrollToEnd();
    }

    private void Cleared() => Bind.OnMain(() =>
    {
        if (!GodotObject.IsInstanceValid(Edit))
            return;
        Edit.Text = "";
        Edit.ClearUndoHistory();
        (_startLine, _startColumn) = (0, 0);
    });

    private void OnInput(InputEvent e)
    {
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } click)
        {
            ShowMenu(click.Position);
            Edit.AcceptEvent();
            return;
        }
        if (e is not InputEventKey { Pressed: true } key)
            return;
        var ctrl = key.CtrlPressed || key.MetaPressed;
        switch (key.Keycode)
        {
            case Key.Enter or Key.KpEnter:
                Submit();
                Edit.AcceptEvent();
                return;
            case Key.Up or Key.Down when !ctrl && !key.ShiftPressed:
                _vm.Browse(older: key.Keycode == Key.Up);
                Replace(_vm.Input);
                Edit.AcceptEvent();
                return;
            case Key.Home when !ctrl && !key.ShiftPressed && !BeforeStart(Edit.GetCaretLine(), Edit.GetCaretColumn()):
                Edit.Deselect();
                Edit.SetCaretLine(_startLine);
                Edit.SetCaretColumn(_startColumn);
                Edit.AcceptEvent();
                return;
            case Key.C when ctrl && !Edit.HasSelection():
                Stop();
                Edit.AcceptEvent();
                return;
            case Key.L when ctrl:
                Bind.Run(_vm.ClearCommand);
                Edit.AcceptEvent();
                return;
            case Key.Z or Key.Y when ctrl:
                // No undo: it would take back what the shell wrote.
                Edit.AcceptEvent();
                return;
        }

        var erase = key.Keycode is Key.Backspace or Key.Delete;
        var edits = erase || key.Keycode == Key.Tab || ctrl && key.Keycode is Key.V or Key.X || !ctrl && key.Unicode >= 32;
        if (!edits)
            return;

        if (Edit.HasSelection())
        {
            var (fromLine, fromColumn) = (Edit.GetSelectionFromLine(), Edit.GetSelectionFromColumn());
            var (toLine, toColumn) = (Edit.GetSelectionToLine(), Edit.GetSelectionToColumn());
            if (!BeforeStart(fromLine, fromColumn))
                return;
            if (BeforeStart(toLine, toColumn) || toLine == _startLine && toColumn == _startColumn)
            {
                // All of it is the shell's: it is not cut nor erased; a key typed goes to the end.
                if (erase || key.Keycode == Key.X)
                {
                    Edit.AcceptEvent();
                    return;
                }
                CaretToEnd();
                return;
            }
            // Only the part typed changes.
            Edit.Select(_startLine, _startColumn, toLine, toColumn);
            return;
        }

        var (line, column) = (Edit.GetCaretLine(), Edit.GetCaretColumn());
        if (BeforeStart(line, column))
        {
            if (erase)
                Edit.AcceptEvent();
            else
                CaretToEnd();
            return;
        }
        if (key.Keycode == Key.Backspace && line == _startLine && column == _startColumn)
            Edit.AcceptEvent();
    }

    /// <summary>Enter: the line typed goes to the shell (each line of a paste in turn) and stays on screen.</summary>
    private void Submit()
    {
        var typed = Typed();
        CaretToEnd();
        Edit.InsertTextAtCaret("\n");
        Edit.ClearUndoHistory();
        Commit();
        ScrollToEnd();
        foreach (var line in typed.Split('\n'))
        {
            _vm.Input = line;
            Bind.Run(_vm.SendCommand);
        }
    }
}
