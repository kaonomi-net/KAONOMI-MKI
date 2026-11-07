using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using KAONOMI.ChatGodot.Tools.Writing;

namespace KAONOMI.ChatGodot.Tools.Games;

/// <summary>
/// /iq: the tests in the manner of the IQ tests. Home: the five tests (FULL, NUMBERS, MATRICES, WORDS, MEMORY), what
/// they are, the last results. A question: the test and its number, the time left, the question (a series of numbers,
/// a matrix of shapes drawn by <see cref="IqMatrix"/>, a sentence) and its choices (keys 1–6 or A–F too), SKIP, QUIT;
/// memory: the digits shown a while, then typed back (Enter). The end: the score, by test, and the review of every
/// question with its right answer and why. The score is indicative, not a measured IQ.
/// </summary>
[ToolView(typeof(IqWindowViewModel), "IQ tests", "🧩", 1100, 780)]
public sealed class IqView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (IqWindowViewModel)viewModel;
        var holder = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        Bind.When(holder, vm, () =>
        {
            foreach (var child in holder.GetChildren())
                child.QueueFree();
            var content = vm.Stage switch
            {
                IqStage.Home => Home(vm),
                IqStage.Result => Result(vm),
                _ => Question(vm)
            };
            content.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            content.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
            holder.AddChild(content);
        }, nameof(vm.Stage), nameof(vm.Current));

        var page = Ui.Column(0, Ui.FillHeight(holder));
        page.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        page.AddChild(new IqTicker(vm));
        WritingUi.Shortcuts(page, key =>
        {
            // Training: Enter or Space goes on after the right answer explained.
            if (vm.Stage == IqStage.Feedback && key.Keycode is Key.Enter or Key.KpEnter or Key.Space)
            {
                Bind.Run(vm.NextCommand);
                return true;
            }
            if (vm.Stage != IqStage.Question || key.CtrlPressed || key.MetaPressed || key.AltPressed)
                return false;
            var choice = key.Keycode switch
            {
                >= Key.Key1 and <= Key.Key6 => (int)(key.Keycode - Key.Key1),
                >= Key.Kp1 and <= Key.Kp6 => (int)(key.Keycode - Key.Kp1),
                >= Key.A and <= Key.F => (int)(key.Keycode - Key.A),
                _ => -1
            };
            if (choice < 0 || vm.Current is not { } q || choice >= q.Choices.Count)
                return false;
            Bind.Run(vm.AnswerCommand, choice);
            return true;
        });
        return page;
    }

    // ── Home ──

    private static Control Home(IqWindowViewModel vm)
    {
        Control Test(string which, string title, string text, bool primary = false)
        {
            var button = Ui.Command(title, vm.StartCommand, primary, $"Start: {title}", () => which);
            button.CustomMinimumSize = new Vector2(190, 46);
            var row = Ui.Row(14, button, Ui.Fill(Eve.Eve.Text_(text, Eve.Eve.Text, 13, wrap: true)));
            return row;
        }
        var tests = Ui.Column(10,
            Test("Full", "▶ FULL TEST", "6 questions of each test, 24 in all, from the easiest to the hardest: the indicative IQ of the whole.", primary: true),
            Test("Numbers", "NUMBER SERIES", "Find the next number of a series: +, ×, differences that grow, two series in turn, squares, sums…"),
            Test("Matrices", "MATRICES", "Complete a 3 × 3 grid of shapes: their kind, number, fill and angle follow rules along the rows."),
            Test("Words", "WORDS", "Analogies (A is to B as C is to…), the odd one out, a little logic; in the language of the chat."),
            Test("Memory", "MEMORY", "Remember a series of digits, longer and longer, then type it back."));

        var history = new VBoxContainer();
        history.AddThemeConstantOverride("separation", 2);
        foreach (var result in vm.History.Take(12))
        {
            var line = Eve.Eve.Text_(result.Line, Eve.Eve.Text, 12);
            line.AddThemeFontOverride("font", Eve.Eve.Mono);
            history.AddChild(line);
        }
        if (vm.History.Count == 0)
            history.AddChild(Eve.Eve.Text_("No test taken yet.", Eve.Eve.Muted, 12));

        var bank = IqWindowViewModel.Bank;
        var source = bank is null
            ? "The questions are made at random for each test."
            : $"The questions are drawn at random from a bank of {bank.Questions.Count} questions, the easiest first.";
        var training = Ui.Check("  Training: the time stops at each answer, the right one is shown and explained, then NEXT", vm, nameof(vm.IsTraining),
            "Without it, the test runs on from one question to the next, and the answers are explained at the end");
        var column = Ui.Column(14,
            Eve.Eve.Caption("IQ TESTS", Eve.Eve.TextBright, 16),
            Eve.Eve.Text_($"Each test has {IqTests.QuestionsPerTest} questions, harder and harder, {IqTests.SecondsPerQuestion} s each (keys 1–6 or A–F answer). {source}", Eve.Eve.Muted, 12, wrap: true),
            training,
            tests,
            Eve.Eve.Separator(),
            Eve.Eve.Caption("LAST RESULTS", Eve.Eve.Amber, 11),
            history,
            Eve.Eve.Separator(),
            Eve.Eve.Text_("The score is an indication for fun and training, on the scale of the IQ (100 = the middle, 15 = one deviation). " +
                "It is not a measured IQ: that needs a standardized test, given and scored by a psychologist.", Eve.Eve.Muted, 11, wrap: true));
        return Ui.Scroll(Ui.Margin(column, 24, 18, 24, 18));
    }

    // ── A question ──

    private static Control Question(IqWindowViewModel vm)
    {
        if (vm.Current is not { } q)
            return Ui.Placeholder("");
        var feedback = vm.Stage == IqStage.Feedback;
        var time = Ui.Label(vm, nameof(vm.TimeLabel), feedback ? Eve.Eve.Muted : Eve.Eve.Amber, 20);
        time.AddThemeFontOverride("font", Eve.Eve.Mono);
        if (feedback)
            time.TooltipText = "The time is stopped (training)";
        var header = Ui.Toolbar(Ui.Row(12,
            Ui.Fill(Ui.Label(vm, nameof(vm.Progress), Eve.Eve.TextBright, 14)),
            Bind.Visible(Ui.Badge("TRAINING", Eve.Eve.Green), vm, nameof(vm.IsTraining)),
            time,
            Ui.Command("SKIP", vm.SkipCommand, tooltip: "Next question (this one counts as missed)"),
            Ui.Command("QUIT", vm.QuitCommand, tooltip: "Back to the choice of a test (nothing kept)")));

        Control body = q.Kind switch
        {
            IqTestKind.Matrices => MatrixQuestion(vm, q),
            IqTestKind.Memory => feedback ? MemoryFeedback(vm, q) : MemoryQuestion(vm, q),
            _ => ChoiceQuestion(vm, q)
        };
        var page = Ui.Column(0, header, Ui.FillHeight(Ui.Margin(body, 24, 18, 24, 12)));
        if (feedback)
            page.AddChild(Explained(vm, q));
        return page;
    }

    /// <summary>Training: right or wrong, the right answer and why, and NEXT.</summary>
    private static Control Explained(IqWindowViewModel vm, IqQuestion q)
    {
        var right = vm.GivenRight;
        var verdict = Eve.Eve.Text_(right ? "✔  RIGHT" : vm.GivenText == "time out" ? "⏱  TIME OUT" : vm.GivenChoice < 0 && q.Kind != IqTestKind.Memory ? "—  SKIPPED" : "✘  WRONG",
            right ? Eve.Eve.Green : Eve.Eve.Red, 22);
        verdict.AddThemeFontOverride("font", Eve.Eve.Display);
        var answer = q.Kind == IqTestKind.Memory ? q.Digits! : q.Choices[q.Answer];
        var why = q.Kind == IqTestKind.Memory
            ? $"The {q.Digits!.Length} digits were {string.Join(" ", q.Digits.ToCharArray())}; you typed {(vm.GivenText == "—" ? "nothing" : string.Join(" ", vm.GivenText.ToCharArray()))}."
            : $"The right answer: {answer} — {Reason(q.Explanation, answer)}.";
        var next = Ui.Command("NEXT  ▶", vm.NextCommand, primary: true, tooltip: "The next question (Enter or Space)");
        next.CustomMinimumSize = new Vector2(180, 52);
        next.AddThemeFontSizeOverride("font_size", 18);
        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(next))
                next.GrabFocus();
        }).CallDeferred();
        var text = Ui.Column(6, verdict, Eve.Eve.Text_(why, Eve.Eve.TextBright, 16, wrap: true));
        text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", Eve.Eve.Box((right ? Eve.Eve.Green : Eve.Eve.Red) with { A = 0.12f }, (right ? Eve.Eve.Green : Eve.Eve.Red) with { A = 0.6f }, 8));
        panel.AddChild(Ui.Margin(Ui.Row(18, text, next), 16, 12, 16, 12));
        return Ui.Margin(panel, 24, 0, 24, 18);
    }

    /// <summary>The explanation without the answer it starts with ("chaise: c'est un meuble…" → "c'est un meuble…").</summary>
    private static string Reason(string explanation, string answer) =>
        explanation.StartsWith(answer + ":", StringComparison.Ordinal) ? explanation[(answer.Length + 1)..].Trim() : explanation;

    /// <summary>A choice in training: green when it is the right one, red when it was given and wrong; no click any more.</summary>
    private static void Mark(IqWindowViewModel vm, IqQuestion q, Button button, int index)
    {
        if (vm.Stage != IqStage.Feedback)
            return;
        Color? colour = index == q.Answer ? Eve.Eve.Green : index == vm.GivenChoice ? Eve.Eve.Red : null;
        button.Disabled = true;
        if (colour is { } c)
            foreach (var state in new[] { "normal", "disabled", "hover", "pressed" })
            {
                var box = Eve.Eve.Box(c with { A = 0.22f }, c, 6);
                box.SetBorderWidthAll(3);
                button.AddThemeStyleboxOverride(state, box);
            }
        else
            button.Modulate = new Color(1, 1, 1, 0.45f);
        button.AddThemeColorOverride("font_disabled_color", Eve.Eve.TextBright);
    }

    /// <summary>Training, memory: the digits that were shown and those typed.</summary>
    private static Control MemoryFeedback(IqWindowViewModel vm, IqQuestion q)
    {
        var shown = Eve.Eve.Text_(string.Join(" ", q.Digits!.ToCharArray()), Eve.Eve.Green, 54);
        shown.AddThemeFontOverride("font", Eve.Eve.Mono);
        shown.HorizontalAlignment = HorizontalAlignment.Center;
        var typed = Eve.Eve.Text_(vm.GivenText == "—" ? "(nothing typed)" : string.Join(" ", vm.GivenText.ToCharArray()), vm.GivenRight ? Eve.Eve.Green : Eve.Eve.Red, 34);
        typed.AddThemeFontOverride("font", Eve.Eve.Mono);
        typed.HorizontalAlignment = HorizontalAlignment.Center;
        return Ui.Column(24, Ui.Spring(), Eve.Eve.Caption("THE DIGITS", Eve.Eve.Muted, 11), shown, Eve.Eve.Caption("YOU TYPED", Eve.Eve.Muted, 11), typed);
    }

    private static Control ChoiceQuestion(IqWindowViewModel vm, IqQuestion q)
    {
        var text = Eve.Eve.Text_(q.Text, Eve.Eve.TextBright, q.Kind == IqTestKind.Numbers ? 34 : 22, wrap: true);
        if (q.Kind == IqTestKind.Numbers)
            text.AddThemeFontOverride("font", Eve.Eve.Mono);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        var choices = Ui.Grid(2, 16, 12);
        for (var i = 0; i < q.Choices.Count; i++)
        {
            var index = i;
            var button = Ui.Action($"{i + 1}.  {q.Choices[i]}", () => Bind.Run(vm.AnswerCommand, index));
            button.CustomMinimumSize = new Vector2(260, 60);
            button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            button.AddThemeFontSizeOverride("font_size", q.Kind == IqTestKind.Numbers ? 24 : 17);
            Mark(vm, q, button, i);
            choices.AddChild(button);
        }
        var column = Ui.Column(40, Ui.Spring(), text, choices);
        column.Alignment = BoxContainer.AlignmentMode.Center;
        return column;
    }

    private static Control MatrixQuestion(IqWindowViewModel vm, IqQuestion q)
    {
        var grid = new IqMatrix(q.Cells!) { CustomMinimumSize = new Vector2(360, 360), SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter };
        var choices = Ui.Grid(6, 10, 10);
        for (var i = 0; i < q.CellChoices!.Count; i++)
        {
            var index = i;
            var button = new Button { Text = "", CustomMinimumSize = new Vector2(110, 130), TooltipText = $"{(char)('A' + i)} (key {(char)('A' + i)} or {i + 1})" };
            var cell = new IqMatrix([q.CellChoices[i]]) { Label = ((char)('A' + i)).ToString(), MouseFilter = Control.MouseFilterEnum.Ignore };
            cell.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            button.AddChild(cell);
            button.Pressed += () => Bind.Run(vm.AnswerCommand, index);
            Mark(vm, q, button, i);
            choices.AddChild(button);
        }
        var center = new CenterContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        center.AddChild(choices);
        var text = Eve.Eve.Text_(q.Text, Eve.Eve.TextBright, 18);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        var top = new CenterContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        top.AddChild(grid);
        return Ui.Column(18, text, top, center);
    }

    private static Control MemoryQuestion(IqWindowViewModel vm, IqQuestion q)
    {
        var text = Eve.Eve.Text_(q.Text, Eve.Eve.TextBright, 18);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        Control middle;
        if (vm.Stage == IqStage.Memorize)
        {
            var digits = Eve.Eve.Text_(string.Join(" ", q.Digits!.ToCharArray()), Eve.Eve.Cyan, 54);
            digits.AddThemeFontOverride("font", Eve.Eve.Mono);
            digits.HorizontalAlignment = HorizontalAlignment.Center;
            middle = digits;
        }
        else
        {
            var edit = Ui.Line(vm, nameof(vm.Typed), "Type the digits, then Enter");
            edit.AddThemeFontOverride("font", Eve.Eve.Mono);
            edit.AddThemeFontSizeOverride("font_size", 34);
            edit.Alignment = HorizontalAlignment.Center;
            edit.CustomMinimumSize = new Vector2(520, 70);
            edit.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            edit.TextSubmitted += _ => Bind.Run(vm.SubmitDigitsCommand);
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(edit))
                    edit.GrabFocus();
            }).CallDeferred();
            var ok = Ui.Command("OK", vm.SubmitDigitsCommand, primary: true, tooltip: "Check (Enter)");
            ok.CustomMinimumSize = new Vector2(120, 50);
            ok.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
            middle = Ui.Column(16, edit, ok);
        }
        var column = Ui.Column(40, Ui.Spring(), text, middle);
        return column;
    }

    // ── The end ──

    private static Control Result(IqWindowViewModel vm)
    {
        var reviews = new VBoxContainer();
        reviews.AddThemeConstantOverride("separation", 6);
        foreach (var r in vm.Reviews)
        {
            var mark = Eve.Eve.Text_(r.Mark, r.IsRight ? Eve.Eve.Green : Eve.Eve.Red, 16);
            mark.CustomMinimumSize = new Vector2(24, 0);
            var detail = Ui.Column(1,
                Eve.Eve.Text_($"{r.Number}. {r.Test} · {r.Question}", Eve.Eve.TextBright, 12, wrap: true),
                Eve.Eve.Text_(r.IsRight ? $"{r.Given} — {r.Why}" : $"your answer: {r.Given} · right: {r.Right} — {r.Why}", Eve.Eve.Muted, 11, wrap: true));
            detail.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            reviews.AddChild(Ui.Row(8, mark, detail));
        }
        var column = Ui.Column(12,
            Ui.Label(vm, nameof(vm.ResultTitle), Eve.Eve.Cyan, 24),
            Ui.Label(vm, nameof(vm.ResultDetail), Eve.Eve.Text, 13, wrap: true),
            Ui.Row(10,
                Ui.Command("↺ AGAIN · FULL TEST", vm.StartCommand, primary: true, parameter: () => "Full"),
                Ui.Command("◀ THE TESTS", vm.QuitCommand)),
            Eve.Eve.Text_("Indicative score, for fun and training: not a measured IQ.", Eve.Eve.Muted, 11),
            Eve.Eve.Separator(),
            Eve.Eve.Caption("REVIEW", Eve.Eve.Amber, 11),
            reviews);
        return Ui.Scroll(Ui.Margin(column, 24, 18, 24, 18));
    }
}

/// <summary>Runs the time of the questions of /iq down, every frame.</summary>
public partial class IqTicker(IqWindowViewModel vm) : Node
{
    public override void _Process(double delta) => vm.Tick(delta);
}

/// <summary>
/// A matrix of /iq drawn: 9 cells in a 3 × 3 grid (the missing one a "?"), or a single cell (a choice, with its
/// letter). A cell holds 1 to 3 shapes (circle, square, triangle, diamond, hexagon), filled or outlined, turned by
/// their angle.
/// </summary>
public partial class IqMatrix(IReadOnlyList<IqCell?> cells) : Control
{
    public string Label { get; set; } = string.Empty;

    public override void _Draw()
    {
        var size = Size;
        var ink = Eve.Eve.TextBright;
        if (cells.Count == 1)
        {
            var top = string.IsNullOrEmpty(Label) ? 0 : 22;
            if (top > 0)
                DrawString(Eve.Eve.Display, new Vector2(0, 16), Label, HorizontalAlignment.Center, size.X, 14, Eve.Eve.Amber);
            DrawCell(cells[0], new Rect2(6, top + 4, size.X - 12, size.Y - top - 10), ink);
            return;
        }
        var side = Math.Min(size.X, size.Y);
        var origin = new Vector2((size.X - side) / 2, (size.Y - side) / 2);
        var cell = side / 3;
        for (var i = 0; i < 9; i++)
        {
            var rect = new Rect2(origin + new Vector2(i % 3 * cell, i / 3 * cell), new Vector2(cell, cell));
            DrawRect(rect.Grow(-3), Eve.Eve.Edge with { A = 0.12f });
            DrawRect(rect.Grow(-3), Eve.Eve.Edge with { A = 0.6f }, false, 1.5f);
            if (cells[i] is { } c)
                DrawCell(c, rect.Grow(-10), ink);
            else
                DrawString(Eve.Eve.Display, rect.Position + new Vector2(0, cell * 0.64f), "?", HorizontalAlignment.Center, cell, (int)(cell * 0.5f), Eve.Eve.Amber);
        }
    }

    private void DrawCell(IqCell? cell, Rect2 area, Color ink)
    {
        if (cell is null)
            return;
        // 1: in the middle; 2: side by side; 3: in a triangle.
        var r = Math.Min(area.Size.X, area.Size.Y) * (cell.Count == 1 ? 0.3f : 0.2f);
        var centre = area.GetCenter();
        Vector2[] spots = cell.Count switch
        {
            1 => [centre],
            2 => [centre + new Vector2(-r * 1.15f, 0), centre + new Vector2(r * 1.15f, 0)],
            _ => [centre + new Vector2(0, -r * 1.1f), centre + new Vector2(-r * 1.2f, r * 0.9f), centre + new Vector2(r * 1.2f, r * 0.9f)]
        };
        foreach (var spot in spots)
            DrawShape(cell.Shape, spot, r, Mathf.DegToRad(cell.Rotation), cell.Filled, ink);
    }

    private void DrawShape(IqShape shape, Vector2 centre, float r, float angle, bool filled, Color ink)
    {
        if (shape == IqShape.Circle)
        {
            if (filled)
                DrawCircle(centre, r, ink);
            else
                DrawArc(centre, r, 0, Mathf.Tau, 48, ink, 2.5f, true);
            return;
        }
        var (sides, start) = shape switch
        {
            IqShape.Square => (4, Mathf.Pi / 4),
            IqShape.Diamond => (4, 0f),
            IqShape.Triangle => (3, -Mathf.Pi / 2),
            _ => (6, 0f)
        };
        // A diamond is taller than wide, so that it is never taken for a square turned by 45°.
        var stretch = shape == IqShape.Diamond ? new Vector2(0.72f, 1.15f) : Vector2.One;
        var points = new Vector2[sides];
        for (var k = 0; k < sides; k++)
        {
            var a = start + k * Mathf.Tau / sides;
            // The shape first, then turned by its angle.
            points[k] = centre + new Vector2(Mathf.Cos(a) * stretch.X, Mathf.Sin(a) * stretch.Y).Rotated(angle) * r;
        }
        if (filled)
            DrawColoredPolygon(points, ink);
        else
            DrawPolyline([.. points, points[0]], ink, 2.5f, true);
    }
}
