using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// /cmd: a console. One screen, as in a terminal: what the shell writes scrolls in the mono font, and the command is
/// typed on the line of its prompt (<see cref="ConsoleScreen"/>). STOP (or Ctrl+C with nothing selected) kills what
/// runs and starts a new shell, CLEAR (or Ctrl+L) empties the screen, 📝 ANNOTATE annotates the text selected (as the
/// right click does); the status bar names the shell and its folder, or why it ended.
/// </summary>
[ToolView(typeof(ConsoleWindowViewModel), "Console", "⌨", 900, 560)]
public sealed class ConsoleView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (ConsoleWindowViewModel)viewModel;
        var screen = new ConsoleScreen(vm);

        var stop = DataUi.Action("■ STOP", screen.Stop, "Kills what runs (Ctrl+C) and starts a new shell");
        var clear = DataUi.Command("CLEAR", vm.ClearCommand, "Empties the screen (Ctrl+L)");
        var annotate = DataUi.Action("📝 ANNOTATE", screen.Annotate, "Annotates the text selected (right click: Annotate selection…)");
        // A click on it keeps the selection of the screen.
        annotate.Focusable = false;
        annotate.IsEnabled = false;
        screen.Box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.SelectionStartProperty || e.Property == TextBox.SelectionEndProperty)
                annotate.IsEnabled = screen.Box.SelectionStart != screen.Box.SelectionEnd;
        };
        foreach (var button in new[] { stop, clear })
            button.Focusable = false;
        var toolbar = DataUi.Toolbar(DataUi.Bar([], DataUi.Bound(vm, nameof(vm.ShellName), Ui.Muted, 12),
            DataUi.Dot(Ui.Green, vm, nameof(vm.IsRunning)),
            DataUi.Dot(Ui.Red, vm, nameof(vm.IsRunning), invert: true),
            annotate, stop, clear));

        screen.Box.Margin = new Thickness(10, 4, 10, 6);
        var page = Ui.Dock((toolbar, Dock.Top), (DataUi.StatusBar(vm, nameof(vm.Status)), Dock.Bottom), (screen.Box, Dock.Top));
        DataUi.FocusWhenShown(screen.Box);
        return page;
    }
}

/// <summary>
/// The screen of the console: a text where everything before the line being typed is the shell's (it can be selected
/// and copied, not changed) and the end is the command line. A key that edits moves to the end first; Enter sends the
/// line (each line of a paste in turn), ↑ / ↓ go through the lines typed before, Home goes to the start of the line
/// typed. What the shell writes while a line is being typed goes in before it, so the line stays last. The right click
/// opens its own menu (copy, annotate the selection, paste at the command line, select all).
/// </summary>
internal sealed class ConsoleScreen
{
    /// <summary>The lines kept on screen (the oldest go first).</summary>
    private const int MaxLines = 5000;

    private readonly ConsoleWindowViewModel _vm;
    private readonly ConcurrentQueue<string> _pending = new();
    private int _flushing;
    // Where the line typed begins: everything before it is the shell's.
    private int _start;

    public ConsoleScreen(ConsoleWindowViewModel vm)
    {
        _vm = vm;
        Box = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            IsUndoEnabled = false,
            FontFamily = Ui.Mono,
            FontSize = 13,
            CaretBrush = Ui.Accent,
            Background = Ui.Field,
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Text = string.Empty
        };
        // Its own menu: the one of the theme would cut and paste in the shell's text.
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => menu.ItemsSource = MenuItems();
        Box.ContextFlyout = menu;
        Box.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Box.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);

        Action<string> received = Received;
        Action cleared = Cleared;
        DataUi.Subscribe(Box, () =>
        {
            vm.Output += received;
            vm.Cleared += cleared;
        }, () =>
        {
            vm.Output -= received;
            vm.Cleared -= cleared;
        });
    }

    public TextBox Box { get; }

    private string Text => Box.Text ?? string.Empty;

    private bool HasSelection => Box.SelectionStart != Box.SelectionEnd;

    private int SelectionFrom => Math.Min(Box.SelectionStart, Box.SelectionEnd);

    private int SelectionTo => Math.Max(Box.SelectionStart, Box.SelectionEnd);

    /// <summary>STOP: the line typed stays as it is, what runs is killed, a new shell starts.</summary>
    public void Stop()
    {
        Commit();
        Ui.Run(_vm.RestartCommand);
        Box.Focus();
    }

    /// <summary>📝 Annotate selection…: the annotation of the text selected, with the console as its context.</summary>
    public void Annotate()
    {
        var selection = Box.SelectedText ?? string.Empty;
        if (selection.Trim().Length > 0)
            Annotations.Open(Box, AnnotationTarget.Text(selection, "Console"));
    }

    private object[] MenuItems()
    {
        MenuItem Item(string header, bool enabled, Action click)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => click();
            return item;
        }
        return
        [
            Item("📋 Copy", HasSelection, () => Dialogs.Copy(Box.SelectedText ?? string.Empty)),
            Item(Annotations.MenuText.Replace("…", " selection…"), HasSelection, Annotate),
            new Separator(),
            Item("📥 Paste", true, Paste),
            Item("Select all", Text.Length > 0, Box.SelectAll)
        ];
    }

    /// <summary>Pastes at the end of the command line (never in the shell's text).</summary>
    private async void Paste()
    {
        var clipboard = TopLevel.GetTopLevel(Box)?.Clipboard;
        if (clipboard is null)
            return;
        var text = await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard);
        if (string.IsNullOrEmpty(text))
            return;
        CaretToEnd();
        Box.Text = Text + text.Replace("\r\n", "\n");
        CaretToEnd();
        Box.Focus();
    }

    /// <summary>The line being typed (several when a paste brought new lines).</summary>
    private string Typed() => _start <= Text.Length ? Text[_start..] : string.Empty;

    private void Replace(string typed)
    {
        Box.Text = Text[..Math.Min(_start, Text.Length)] + typed;
        CaretToEnd();
    }

    /// <summary>What is typed becomes the shell's: the next line starts after it.</summary>
    private void Commit() => _start = Text.Length;

    private void CaretToEnd()
    {
        var end = Text.Length;
        Box.SelectionStart = end;
        Box.SelectionEnd = end;
        Box.CaretIndex = end;
    }

    private ScrollViewer? Scroller => Box.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() => Scroller?.ScrollToEnd(), DispatcherPriority.Background);

    // The pieces come from the threads of the shell: gathered, then written at once.
    private void Received(string text)
    {
        _pending.Enqueue(text);
        if (Interlocked.Exchange(ref _flushing, 1) == 0)
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Background);
    }

    private void Flush()
    {
        Interlocked.Exchange(ref _flushing, 0);
        var output = new StringBuilder();
        while (_pending.TryDequeue(out var piece))
            output.Append(piece);
        if (output.Length == 0)
            return;
        var written = output.ToString().Replace("\r\n", "\n").Replace("\r", string.Empty);

        var scroller = Scroller;
        var following = scroller is null || scroller.Offset.Y + scroller.Viewport.Height >= scroller.Extent.Height - 4;
        var typing = !HasSelection && Box.CaretIndex >= _start;
        var (from, to) = (Box.SelectionStart, Box.SelectionEnd);
        var typed = Typed();

        var shells = Text[..Math.Min(_start, Text.Length)] + written;
        // The oldest lines go past the limit.
        var removed = 0;
        var lines = 0;
        for (var i = shells.Length - 1; i >= 0; i--)
            if (shells[i] == '\n' && ++lines >= MaxLines)
            {
                removed = i + 1;
                break;
            }
        if (removed > 0)
            shells = shells[removed..];
        _start = shells.Length;
        Box.Text = shells + typed;
        if (typing)
            CaretToEnd();
        else
        {
            Box.SelectionStart = Math.Clamp(from - removed, 0, _start);
            Box.SelectionEnd = Math.Clamp(to - removed, 0, _start);
        }
        if (following)
            ScrollToEnd();
    }

    private void Cleared() => Ui.OnMain(() =>
    {
        Box.Text = string.Empty;
        _start = 0;
    });

    /// <summary>A character typed: at the end of the command line when the caret is in the shell's text.</summary>
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (HasSelection)
        {
            if (SelectionFrom >= _start)
                return;
            if (SelectionTo <= _start)
                CaretToEnd();
            else
                Box.SelectionStart = _start;
            return;
        }
        if (Box.CaretIndex < _start)
            CaretToEnd();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = DataUi.IsCommand(e);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (e.Key)
        {
            case Key.Enter or Key.Return:
                Submit();
                e.Handled = true;
                return;
            case Key.Up or Key.Down when !ctrl && !shift:
                _vm.Browse(older: e.Key == Key.Up);
                Replace(_vm.Input ?? string.Empty);
                e.Handled = true;
                return;
            case Key.Home when !ctrl && !shift && Box.CaretIndex >= _start:
                Box.SelectionStart = Box.SelectionEnd = Box.CaretIndex = _start;
                e.Handled = true;
                return;
            case Key.C when ctrl && !HasSelection:
                Stop();
                e.Handled = true;
                return;
            case Key.L when ctrl:
                Ui.Run(_vm.ClearCommand);
                e.Handled = true;
                return;
            case Key.Z or Key.Y when ctrl:
                // No undo: it would take back what the shell wrote.
                e.Handled = true;
                return;
            case Key.V when ctrl:
                Paste();
                e.Handled = true;
                return;
        }

        var erase = e.Key is Key.Back or Key.Delete;
        var edits = erase || e.Key == Key.Tab || ctrl && e.Key == Key.X;
        if (!edits)
            return;

        if (HasSelection)
        {
            if (SelectionFrom >= _start)
                return;
            if (SelectionTo <= _start)
            {
                // All of it is the shell's: it is not cut nor erased; a tab goes to the end.
                if (erase || e.Key == Key.X)
                {
                    e.Handled = true;
                    return;
                }
                CaretToEnd();
                return;
            }
            // Only the part typed changes.
            var to = SelectionTo;
            Box.SelectionStart = _start;
            Box.SelectionEnd = to;
            return;
        }

        var caret = Box.CaretIndex;
        if (caret < _start)
        {
            if (erase)
                e.Handled = true;
            else
                CaretToEnd();
            return;
        }
        if (e.Key == Key.Back && caret == _start)
            e.Handled = true;
    }

    /// <summary>Enter: the line typed goes to the shell (each line of a paste in turn) and stays on screen.</summary>
    private void Submit()
    {
        var typed = Typed();
        Box.Text = Text + "\n";
        CaretToEnd();
        Commit();
        ScrollToEnd();
        foreach (var line in typed.Split('\n'))
        {
            _vm.Input = line;
            Ui.Run(_vm.SendCommand);
        }
    }
}
