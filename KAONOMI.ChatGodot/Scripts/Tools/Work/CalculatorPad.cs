using System;
using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Work;

/// <summary>
/// The scientific calculator of /work (the Calculator button of Home), in a window of its own, the port of the
/// CalculatorPad of the Avalonia chat: its header (DEG / RAD, Copy), the display (the expression above, its result
/// below, red on an error), the scientific keys on the left and the standard ones on the right, the recent calculations
/// (a click puts one back, Clear empties them). The keyboard works too: digits, operators, parentheses, letters of the
/// functions (x and * multiply, / and : divide), Enter = result, Backspace = last character, Escape / Delete = clear,
/// Ctrl / ⌘ + C = copy the result. It shares the view model of Work: a result with = also goes to the tape.
/// </summary>
public partial class CalculatorPad : PanelContainer
{
    private readonly CalculatorViewModel? _vm;

    /// <summary>The font of the keys: the one of the chat, then fonts with the symbols it lacks (π, ⁻¹, ˣ, ∛, ₂…).</summary>
    private static readonly SystemFont KeyFont = new()
    {
        FontNames = Eve.Eve.Body.FontNames,
        Fallbacks =
        [
            new SystemFont { FontNames = ["Helvetica Neue", "Arial Unicode MS", "Apple Symbols", "Segoe UI Symbol", "DejaVu Sans", "Noto Sans Math", "sans-serif"] },
            Eve.Eve.Emoji
        ]
    };

    public CalculatorPad()
    {
    }

    public CalculatorPad(CalculatorViewModel vm)
    {
        _vm = vm;
        FocusMode = FocusModeEnum.All;
        AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.12f), new Color(0, 0, 0, 0), 16, 0));

        // Header: brand, DEG / RAD, Copy.
        var angle = Bind.Text(Eve.Eve.Button("DEG", tooltip: "Angles of sin, cos, tan and their inverses: degrees (DEG) or radians (RAD)"), vm, nameof(vm.AngleLabel));
        angle.CustomMinimumSize = new Vector2(56, 0);
        angle.FocusMode = FocusModeEnum.None;
        angle.AddThemeColorOverride("font_color", Eve.Eve.Amber);
        Bind.Command(angle, vm.ToggleAngleCommand);
        angle.Pressed += GrabFocus;
        var copy = Ui.Action("COPY", CopyResult, tooltip: "Copy the result (Ctrl / ⌘ + C)");
        copy.FocusMode = FocusModeEnum.None;
        var header = Ui.Row(6,
            Ui.Fill(Ui.Column(0, Eve.Eve.Caption("Kaonomi", Eve.Eve.Amber, 11), Eve.Eve.Text_("Scientific calculator", Eve.Eve.Muted, 13))),
            angle, copy);

        // Display.
        var expression = Ui.Label(vm, nameof(vm.DisplayExpression), Eve.Eve.Muted, 15, wrap: true);
        expression.AddThemeFontOverride("font", Eve.Eve.Mono);
        expression.HorizontalAlignment = HorizontalAlignment.Right;
        var result = Ui.Label(vm, nameof(vm.Result), Eve.Eve.TextBright, 40, wrap: true);
        result.AddThemeFontOverride("font", new SystemFont { FontNames = Eve.Eve.Mono.FontNames, FontWeight = 300 });
        result.HorizontalAlignment = HorizontalAlignment.Right;
        Bind.When(result, vm, () =>
        {
            result.AddThemeColorOverride("font_color", vm.IsError ? Eve.Eve.Red : Eve.Eve.TextBright);
            result.AddThemeFontSizeOverride("font_size", vm.IsError ? 18 : 40);
        }, nameof(vm.IsError));
        var screen = new PanelContainer { CustomMinimumSize = new Vector2(0, 104) };
        screen.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Field, Eve.Eve.Cyan with { A = 0.35f }, 14));
        var screenColumn = Ui.Column(2, expression, result);
        screenColumn.Alignment = BoxContainer.AlignmentMode.End;
        screen.AddChild(screenColumn);

        // Keys: the scientific ones on the left, the standard ones on the right.
        var keys = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ShrinkCenter };
        keys.AddThemeConstantOverride("separation", 8);
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
                line.AddChild(kind == "gap" ? new Control { CustomMinimumSize = new Vector2(6, 0) } : Key(label, key, kind, tip));
            keys.AddChild(line);
        }

        // Recent calculations.
        var history = new VBoxContainer();
        history.AddThemeConstantOverride("separation", 0);
        var empty = Eve.Eve.Text_("The calculations done with = are listed here", Eve.Eve.Muted, 11);
        Bind.Items(history, vm.History, entry => Recall(entry), empty);
        var historyScroll = Ui.Scroll(history);
        historyScroll.CustomMinimumSize = new Vector2(0, 84);
        historyScroll.SizeFlagsVertical = SizeFlags.Fill;
        Bind.Visible(historyScroll, vm, nameof(vm.HasHistory));
        var clear = Ui.Command("CLEAR", vm.ClearHistoryCommand);
        clear.FocusMode = FocusModeEnum.None;
        clear.AddThemeFontSizeOverride("font_size", 10);
        Bind.Visible(clear, vm, nameof(vm.HasHistory));
        var recent = new PanelContainer();
        recent.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Raised(0.02f), Eve.Eve.Edge with { A = 0.3f }, 8));
        recent.AddChild(Ui.Column(4, Ui.Row(6, Ui.Fill(Eve.Eve.Caption("Recent", Eve.Eve.Muted, 10)), clear), empty, historyScroll));

        AddChild(Ui.Column(12, header, screen, keys, recent));
        Ready += () => Callable.From(GrabFocus).CallDeferred();
    }

    /// <summary>A key of the calculator (it presses <paramref name="key"/>), in the look of its kind.</summary>
    private Button Key(string label, string key, string kind, string tip)
    {
        var button = new Button
        {
            Text = label,
            TooltipText = tip,
            CustomMinimumSize = new Vector2(64, 46),
            FocusMode = FocusModeEnum.None,
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        button.AddThemeFontOverride("font", KeyFont);
        switch (kind)
        {
            case "wide":
                button.CustomMinimumSize = new Vector2(136, 46);
                button.Alignment = HorizontalAlignment.Left;
                button.AddThemeFontSizeOverride("font_size", 19);
                button.AddThemeColorOverride("font_color", Eve.Eve.TextBright);
                var wide = Eve.Eve.Box(Eve.Eve.Raised(0.07f), Eve.Eve.Edge, 6);
                wide.ContentMarginLeft = 26;
                button.AddThemeStyleboxOverride("normal", wide);
                break;
            case "digit":
                button.AddThemeFontSizeOverride("font_size", 19);
                button.AddThemeColorOverride("font_color", Eve.Eve.TextBright);
                button.AddThemeStyleboxOverride("normal", Eve.Eve.Box(Eve.Eve.Raised(0.07f), Eve.Eve.Edge, 6));
                break;
            case "fn":
                button.AddThemeFontSizeOverride("font_size", 17);
                button.AddThemeStyleboxOverride("normal", Eve.Eve.Box(Eve.Eve.Raised(0.045f), Eve.Eve.Edge, 6));
                break;
            case "sci":
                button.AddThemeFontSizeOverride("font_size", 15);
                button.AddThemeColorOverride("font_color", Eve.Eve.Muted.Lightened(0.25f));
                button.AddThemeStyleboxOverride("normal", Eve.Eve.Box(Eve.Eve.Raised(0.015f), Eve.Eve.Edge with { A = 0.35f }, 6));
                break;
            case "op":
                button.AddThemeFontSizeOverride("font_size", 22);
                button.AddThemeColorOverride("font_color", Eve.Eve.Amber);
                button.AddThemeColorOverride("font_hover_color", Eve.Eve.Amber.Lightened(0.2f));
                button.AddThemeStyleboxOverride("normal", Eve.Eve.Box(Eve.Eve.Amber with { A = 0.1f }, Eve.Eve.Amber with { A = 0.45f }, 6));
                button.AddThemeStyleboxOverride("hover", Eve.Eve.Box(Eve.Eve.Amber with { A = 0.2f }, Eve.Eve.Amber, 6));
                button.AddThemeStyleboxOverride("pressed", Eve.Eve.Box(Eve.Eve.Amber with { A = 0.35f }, Eve.Eve.Amber, 6));
                break;
            case "eq":
                button.AddThemeFontSizeOverride("font_size", 24);
                Eve.Eve.StylePrimary(button);
                break;
        }
        button.Pressed += () => Press(key);
        return button;
    }

    /// <summary>A calculation of the history: a click puts it back.</summary>
    private Button Recall(CalculatorEntry entry)
    {
        var button = new Button
        {
            Flat = true,
            Alignment = HorizontalAlignment.Left,
            TooltipText = "Put this calculation back",
            FocusMode = FocusModeEnum.None,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        var text = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            AutowrapMode = TextServer.AutowrapMode.Off,
            MouseFilter = MouseFilterEnum.Ignore,
            Text = $"[color=#{Eve.Eve.Muted.ToHtml(false)}]{Escape(entry.Expression)}[/color][color=#{Eve.Eve.Muted.Darkened(0.2f).ToHtml(false)}] = [/color][b]{Escape(entry.Result)}[/b]"
        };
        text.AddThemeFontOverride("normal_font", Eve.Eve.Mono);
        text.AddThemeFontOverride("bold_font", new SystemFont { FontNames = Eve.Eve.Mono.FontNames, FontWeight = 700 });
        text.AddThemeFontSizeOverride("normal_font_size", 12);
        text.AddThemeFontSizeOverride("bold_font_size", 12);
        text.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        text.OffsetLeft = 6;
        text.OffsetTop = 3;
        button.CustomMinimumSize = new Vector2(0, 22);
        button.AddChild(text);
        button.Pressed += () =>
        {
            Bind.Run(_vm?.ReuseCommand, entry);
            GrabFocus();
        };
        return button;
    }

    private static string Escape(string text) => text.Replace("[", "[lb]");

    private void Press(string key)
    {
        Bind.Run(_vm?.PressCommand, key);
        GrabFocus();
    }

    private void CopyResult()
    {
        if (_vm is { IsError: false } vm)
            DisplayServer.ClipboardSet(vm.Result);
        GrabFocus();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (_vm is null || @event is not InputEventKey { Pressed: true } e)
            return;
        var command = OS.GetName() == "macOS" ? e.MetaPressed : e.CtrlPressed;
        if (command && e.Keycode == Godot.Key.C)
        {
            CopyResult();
            AcceptEvent();
            return;
        }
        string? key = e.Keycode switch
        {
            Godot.Key.Enter or Godot.Key.KpEnter => "=",
            Godot.Key.Backspace => "⌫",
            Godot.Key.Escape or Godot.Key.Delete => "C",
            _ => null
        };
        if (key is null && !command && e.Unicode >= 32)
        {
            var text = char.ConvertFromUtf32((int)e.Unicode);
            key = text switch
            {
                "*" or "x" or "X" => "×",
                "/" or ":" => "÷",
                "-" => "−",
                "," => ".",
                "=" => "=",
                _ when text.Length == 1 && (char.IsDigit(text[0]) || "+.()%^!π√".Contains(text[0])) => text,
                _ when text.Length == 1 && char.IsLetter(text[0]) => text,
                _ => null
            };
        }
        if (key is null)
            return;
        Bind.Run(_vm.PressCommand, key);
        AcceptEvent();
    }
}
