using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Work;

/// <summary>
/// The scientific calculator of /work (the Calculator button of Home), in a window of its own: its header (DEG / RAD,
/// Copy), the display (the expression above, its result below, red on an error), the scientific keys on the left and
/// the standard ones on the right, the recent calculations (a click puts one back, Clear empties them). The keyboard
/// works too: digits, operators, parentheses, letters of the functions (x and * multiply, / and : divide), Enter =
/// result, Backspace = last character, Escape / Delete = clear, Ctrl / ⌘ + C = copy the result. It shares the view
/// model of Work: a result with = also goes to the tape.
/// </summary>
public sealed class CalculatorPad : Border
{
    private readonly CalculatorViewModel _vm;

    /// <summary>The font of the keys: the one of the chat, then fonts with the symbols it lacks (π, ⁻¹, ˣ, ∛, ₂…).</summary>
    private static readonly FontFamily KeyFont = new("Inter, Segoe UI, Helvetica Neue, Arial Unicode MS, Apple Symbols, Segoe UI Symbol, DejaVu Sans, Noto Sans Math, sans-serif");

    public CalculatorPad(CalculatorViewModel vm)
    {
        _vm = vm;
        Focusable = true;
        Background = Ui.Panel;
        Padding = new Thickness(16);

        // Header: brand, DEG / RAD, Copy.
        var angle = Ui.Command("DEG", vm.ToggleAngleCommand, tooltip: "Angles of sin, cos, tan and their inverses: degrees (DEG) or radians (RAD)");
        angle.Bind(ContentControl.ContentProperty, vm, nameof(vm.AngleLabel));
        angle.MinWidth = 56;
        angle.Focusable = false;
        angle.Foreground = Ui.Warm;
        angle.Click += (_, _) => Focus();
        var copy = Ui.Button("COPY", CopyResult, "Copy the result (Ctrl / ⌘ + C)");
        copy.Focusable = false;
        var header = WorkUi.FillRow(6,
            Ui.Column(0, Ui.Caption("Kaonomi", Ui.Warm, 11), Ui.Label("Scientific calculator", Ui.Muted, 13)),
            angle, copy);

        // Display.
        var expression = Ui.Bound(vm, nameof(vm.DisplayExpression), Ui.Muted, 15, wrap: true);
        expression.FontFamily = Ui.Mono;
        expression.TextAlignment = TextAlignment.Right;
        expression.HorizontalAlignment = HorizontalAlignment.Stretch;
        var result = Ui.Bound(vm, nameof(vm.Result), Ui.TextBright, 40, wrap: true);
        result.FontFamily = Ui.Mono;
        result.FontWeight = FontWeight.Light;
        result.TextAlignment = TextAlignment.Right;
        result.HorizontalAlignment = HorizontalAlignment.Stretch;
        Ui.When(result, vm, () =>
        {
            result.Foreground = vm.IsError ? Ui.Red : Ui.TextBright;
            result.FontSize = vm.IsError ? 18 : 40;
        }, nameof(vm.IsError));
        var screenColumn = Ui.Column(2, expression, result);
        screenColumn.VerticalAlignment = VerticalAlignment.Bottom;
        var screen = new Border
        {
            MinHeight = 104,
            Background = Ui.Field,
            BorderBrush = Ui.Accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(14),
            Child = screenColumn
        };

        // Keys: the scientific ones on the left, the standard ones on the right.
        var keys = Ui.Column(8);
        keys.HorizontalAlignment = HorizontalAlignment.Center;
        (string Label, string Key, string Kind, string Tip)[][] rows =
        [
            [("sin", "sin(", "sci", ""), ("cos", "cos(", "sci", ""), ("tan", "tan(", "sci", ""), ("π", "π", "sci", ""), ("e", "e", "sci", "Euler's number 2.71828…"),
             ("", "", "gap", ""), ("C", "C", "fn", "Clear (Esc)"), ("(", "(", "fn", ""), (")", ")", "fn", ""), ("%", "%", "fn", "Hundredth of the value before it"), ("⌫", "⌫", "fn", "Delete the last character (Backspace)")],
            [("sin⁻¹", "asin(", "sci", ""), ("cos⁻¹", "acos(", "sci", ""), ("tan⁻¹", "atan(", "sci", ""), ("Ans", "Ans", "sci", "Last result"), ("mod", "mod", "sci", "Remainder of the division"),
             ("", "", "gap", ""), ("7", "7", "digit", ""), ("8", "8", "digit", ""), ("9", "9", "digit", ""), ("÷", "÷", "op", ""), ("√", "√", "fn", "Square root of the value after it")],
            [("sinh", "sinh(", "sci", ""), ("cosh", "cosh(", "sci", ""), ("tanh", "tanh(", "sci", ""), ("|x|", "abs(", "sci", "Absolute value"), ("n!", "!", "sci", "Factorial"),
             ("", "", "gap", ""), ("4", "4", "digit", ""), ("5", "5", "digit", ""), ("6", "6", "digit", ""), ("×", "×", "op", ""), ("xʸ", "^", "fn", "Power (^)")],
            [("ln", "ln(", "sci", "Natural logarithm"), ("log", "log(", "sci", "Base 10 logarithm"), ("eˣ", "exp(", "sci", ""), ("10ˣ", "10^", "sci", ""), ("∛", "cbrt(", "sci", "Cube root"),
             ("", "", "gap", ""), ("1", "1", "digit", ""), ("2", "2", "digit", ""), ("3", "3", "digit", ""), ("−", "−", "op", ""), ("±", "±", "fn", "Change the sign of the last number")],
            [("x²", "^2", "sci", ""), ("x³", "^3", "sci", ""), ("1/x", "^-1", "sci", "Inverse"), ("log₂", "log2(", "sci", "Base 2 logarithm"), ("Rand", "Rand", "sci", "Random number from 0 to 1"),
             ("", "", "gap", ""), ("0", "0", "wide", ""), (".", ".", "digit", ""), ("+", "+", "op", ""), ("=", "=", "eq", "Result (Enter)")]
        ];
        foreach (var row in rows)
        {
            var line = Ui.Row(8);
            foreach (var (label, key, kind, tip) in row)
                line.Children.Add(kind == "gap" ? new Panel { Width = 6 } : Key(label, key, kind, tip));
            keys.Children.Add(line);
        }

        // Recent calculations.
        var history = new ItemsControl
        {
            ItemsSource = vm.History,
            ItemTemplate = new FuncDataTemplate<CalculatorEntry>((entry, _) => entry is null ? new Panel() : Recall(entry))
        };
        var empty = Ui.Label("The calculations done with = are listed here", Ui.Muted, 11).Visible(vm, nameof(vm.HasHistory), invert: true);
        var historyScroll = Ui.Scroll(history);
        historyScroll.MaxHeight = 120;
        historyScroll.MinHeight = 84;
        historyScroll.Visible(vm, nameof(vm.HasHistory));
        var clear = Ui.Command("CLEAR", vm.ClearHistoryCommand);
        clear.Focusable = false;
        clear.FontSize = 10;
        clear.Visible(vm, nameof(vm.HasHistory));
        var recent = new Border
        {
            Background = Ui.Header,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            Padding = new Thickness(8),
            Child = Ui.Column(4, WorkUi.FillRow(6, Ui.Caption("Recent", Ui.Muted, 10), clear), empty, historyScroll)
        };

        Child = Ui.Scroll(Ui.Column(12, header, screen, keys, recent));
        AttachedToVisualTree += (_, _) => WorkUi.Later(() => Focus());
    }

    /// <summary>A key of the calculator (it presses <paramref name="key"/>), in the look of its kind.</summary>
    private Button Key(string label, string key, string kind, string tip)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 64,
            Height = 46,
            Focusable = false,
            FontFamily = KeyFont,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1),
            BorderBrush = Ui.Edge
        };
        if (tip.Length > 0)
            ToolTip.SetTip(button, tip);
        switch (kind)
        {
            case "wide":
                button.MinWidth = 136;
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.Padding = new Thickness(26, 0, 0, 0);
                button.FontSize = 19;
                button.Foreground = Ui.TextBright;
                break;
            case "digit":
                button.FontSize = 19;
                button.Foreground = Ui.TextBright;
                break;
            case "fn":
                button.FontSize = 17;
                break;
            case "sci":
                button.FontSize = 15;
                button.Foreground = Ui.Muted;
                button.Background = Ui.Panel;
                break;
            case "op":
                button.FontSize = 22;
                button.Foreground = Ui.Warm;
                button.BorderBrush = Ui.Warm;
                break;
            case "eq":
                button.FontSize = 24;
                button.Classes.Add("primary");
                break;
        }
        button.Click += (_, _) => Press(key);
        return button;
    }

    /// <summary>A calculation of the history: a click puts it back.</summary>
    private Button Recall(CalculatorEntry entry)
    {
        var text = new TextBlock { FontFamily = Ui.Mono, FontSize = 12 };
        text.Inlines!.Add(new Run(entry.Expression) { Foreground = Ui.Muted });
        text.Inlines.Add(new Run(" = ") { Foreground = Ui.Edge });
        text.Inlines.Add(new Run(entry.Result) { FontWeight = FontWeight.Bold, Foreground = Ui.TextBright });
        var button = new Button
        {
            Content = text,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Focusable = false,
            Padding = new Thickness(6, 3),
            MinHeight = 22
        };
        ToolTip.SetTip(button, "Put this calculation back");
        button.Click += (_, _) =>
        {
            Ui.Run(_vm.ReuseCommand, entry);
            Focus();
        };
        return button;
    }

    private void Press(string key)
    {
        Ui.Run(_vm.PressCommand, key);
        Focus();
    }

    private void CopyResult()
    {
        if (!_vm.IsError)
            Dialogs.Copy(_vm.Result);
        Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;
        if (WorkUi.IsCommand(e.KeyModifiers) && e.Key == Avalonia.Input.Key.C)
        {
            CopyResult();
            e.Handled = true;
            return;
        }
        string? key = e.Key switch
        {
            Avalonia.Input.Key.Enter => "=",
            Avalonia.Input.Key.Back => "⌫",
            Avalonia.Input.Key.Escape or Avalonia.Input.Key.Delete => "C",
            _ => null
        };
        if (key is null)
            return;
        Ui.Run(_vm.PressCommand, key);
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text) || e.Text.Length != 1 || e.Text[0] < 32)
            return;
        var text = e.Text;
        string? key = text switch
        {
            "*" or "x" or "X" => "×",
            "/" or ":" => "÷",
            "-" => "−",
            "," => ".",
            "=" => "=",
            _ when char.IsDigit(text[0]) || "+.()%^!π√".Contains(text[0]) => text,
            _ when char.IsLetter(text[0]) => text,
            _ => null
        };
        if (key is null)
            return;
        Ui.Run(_vm.PressCommand, key);
        e.Handled = true;
    }
}
