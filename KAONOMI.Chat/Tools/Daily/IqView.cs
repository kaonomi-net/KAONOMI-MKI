using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Daily;

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
        var holder = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var page = new Border { Child = holder };
        Ui.When(holder, vm, () =>
        {
            holder.Content = vm.Stage switch
            {
                IqStage.Home => Home(vm),
                IqStage.Result => Result(vm),
                _ => Question(vm)
            };
            // The keys of the answers go to the page while nothing else wants them.
            if (vm.Stage is not (IqStage.Recall or IqStage.Feedback))
                Dispatcher.UIThread.Post(() => page.Focus());
        }, nameof(vm.Stage), nameof(vm.Current));

        // The time of the questions runs down.
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed;
        DailyUi.Every(page, TimeSpan.FromMilliseconds(50), () =>
        {
            var now = clock.Elapsed;
            vm.Tick((now - last).TotalSeconds);
            last = now;
        });

        DailyUi.Shortcuts(page, key =>
        {
            // Training: Enter or Space goes on after the right answer explained.
            if (vm.Stage == IqStage.Feedback && key.Key is Key.Enter or Key.Space)
            {
                Ui.Run(vm.NextCommand);
                return true;
            }
            if (vm.Stage != IqStage.Question || key.KeyModifiers != KeyModifiers.None && key.KeyModifiers != KeyModifiers.Shift)
                return false;
            var choice = key.Key switch
            {
                >= Key.D1 and <= Key.D6 => key.Key - Key.D1,
                >= Key.NumPad1 and <= Key.NumPad6 => key.Key - Key.NumPad1,
                >= Key.A and <= Key.F => key.Key - Key.A,
                _ => -1
            };
            if (choice < 0 || vm.Current is not { } q || choice >= ChoiceCount(q))
                return false;
            Ui.Run(vm.AnswerCommand, choice);
            return true;
        });
        return page;
    }

    private static int ChoiceCount(IqQuestion q) => q.Kind == IqTestKind.Matrices ? q.CellChoices?.Count ?? 0 : q.Choices.Count;

    // ── Home ──

    private static Control Home(IqWindowViewModel vm)
    {
        Control Test(string which, string title, string text, bool primary = false)
        {
            var button = Ui.Command(title, vm.StartCommand, which, $"Start: {title}", primary);
            button.MinWidth = 190;
            button.MinHeight = 46;
            button.Margin = new Thickness(0, 0, 14, 0);
            return Ui.Dock((button, Avalonia.Controls.Dock.Left), (Ui.Label(text, Ui.Text, 13, wrap: true), Avalonia.Controls.Dock.Left));
        }
        var tests = Ui.Column(10,
            Test("Full", "▶ FULL TEST", "6 questions of each test, 24 in all, from the easiest to the hardest: the indicative IQ of the whole.", primary: true),
            Test("Numbers", "NUMBER SERIES", "Find the next number of a series: +, ×, differences that grow, two series in turn, squares, sums…"),
            Test("Matrices", "MATRICES", "Complete a 3 × 3 grid of shapes: their kind, number, fill and angle follow rules along the rows."),
            Test("Words", "WORDS", "Analogies (A is to B as C is to…), the odd one out, a little logic; in the language of the chat."),
            Test("Memory", "MEMORY", "Remember a series of digits, longer and longer, then type it back."));

        var history = new StackPanel { Spacing = 2 };
        foreach (var result in vm.History.Take(12))
            history.Children.Add(DailyUi.Mono(result.Line, Ui.Text, 12));
        if (vm.History.Count == 0)
            history.Children.Add(Ui.Label("No test taken yet.", Ui.Muted, 12));

        var bank = IqWindowViewModel.Bank;
        var source = bank is null
            ? "The questions are made at random for each test."
            : $"The questions are drawn at random from a bank of {bank.Questions.Count} questions, the easiest first.";
        var training = Ui.Check("Training: the time stops at each answer, the right one is shown and explained, then NEXT", vm, nameof(vm.IsTraining));
        ToolTip.SetTip(training, "Without it, the test runs on from one question to the next, and the answers are explained at the end");
        var column = Ui.Column(14,
            Ui.Caption("IQ TESTS", Ui.TextBright, 16),
            Ui.Label($"Each test has {IqTests.QuestionsPerTest} questions, harder and harder, {IqTests.SecondsPerQuestion} s each (keys 1–6 or A–F answer). {source}", Ui.Muted, 12, wrap: true),
            training,
            tests,
            DailyUi.Separator(),
            Ui.Caption("LAST RESULTS", Ui.Warm, 11),
            history,
            DailyUi.Separator(),
            Ui.Label("The score is an indication for fun and training, on the scale of the IQ (100 = the middle, 15 = one deviation). " +
                "It is not a measured IQ: that needs a standardized test, given and scored by a psychologist.", Ui.Muted, 11, wrap: true));
        column.Margin = new Thickness(24, 18);
        return Ui.Scroll(column);
    }

    // ── A question ──

    private static Control Question(IqWindowViewModel vm)
    {
        if (vm.Current is not { } q)
            return DailyUi.Placeholder("");
        var feedback = vm.Stage == IqStage.Feedback;
        var time = DailyUi.Mono(string.Empty, feedback ? Ui.Muted : Ui.Warm, 20).Bind(TextBlock.TextProperty, vm, nameof(vm.TimeLabel));
        if (feedback)
            ToolTip.SetTip(time, "The time is stopped (training)");
        var header = DailyUi.Toolbar(
            [Ui.Bound(vm, nameof(vm.Progress), Ui.TextBright, 14)],
            [DailyUi.Badge("TRAINING", Ui.Green).Visible(vm, nameof(vm.IsTraining)),
             time,
             Ui.Command("SKIP", vm.SkipCommand, tooltip: "Next question (this one counts as missed)"),
             Ui.Command("QUIT", vm.QuitCommand, tooltip: "Back to the choice of a test (nothing kept)")]);

        Control body = q.Kind switch
        {
            IqTestKind.Matrices => MatrixQuestion(vm, q),
            IqTestKind.Memory => feedback ? MemoryFeedback(vm, q) : MemoryQuestion(vm, q),
            _ => ChoiceQuestion(vm, q)
        };
        body.Margin = new Thickness(24, 18, 24, 12);
        return feedback ? DailyUi.Page(1, header, Ui.Scroll(body), Explained(vm, q)) : DailyUi.Page(1, header, Ui.Scroll(body));
    }

    /// <summary>Training: right or wrong, the right answer and why, and NEXT.</summary>
    private static Control Explained(IqWindowViewModel vm, IqQuestion q)
    {
        var right = vm.GivenRight;
        var colour = right ? Ui.Green : Ui.Red;
        var verdict = Ui.Label(right ? "✔  RIGHT" : vm.GivenText == "time out" ? "⏱  TIME OUT" : vm.GivenChoice < 0 && q.Kind != IqTestKind.Memory ? "—  SKIPPED" : "✘  WRONG",
            colour, 22, bold: true);
        var answer = q.Kind == IqTestKind.Memory ? q.Digits! : q.Choices.Count > q.Answer ? q.Choices[q.Answer] : ((char)('A' + q.Answer)).ToString();
        var why = q.Kind == IqTestKind.Memory
            ? $"The {q.Digits!.Length} digits were {string.Join(" ", q.Digits.ToCharArray())}; you typed {(vm.GivenText == "—" ? "nothing" : string.Join(" ", vm.GivenText.ToCharArray()))}."
            : $"The right answer: {answer} — {Reason(q.Explanation, answer)}.";
        var next = Ui.Command("NEXT  ▶", vm.NextCommand, tooltip: "The next question (Enter or Space)", primary: true);
        next.MinWidth = 180;
        next.MinHeight = 52;
        next.FontSize = 18;
        next.VerticalAlignment = VerticalAlignment.Center;
        next.Margin = new Thickness(18, 0, 0, 0);
        next.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => next.Focus());
        var text = Ui.Column(6, verdict, Ui.Label(why, Ui.TextBright, 16, wrap: true));
        return new Border
        {
            Margin = new Thickness(24, 0, 24, 18),
            Padding = new Thickness(16, 12),
            BorderBrush = colour,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(4),
            Background = Ui.Panel,
            Child = Ui.Dock((next, Avalonia.Controls.Dock.Right), (text, Avalonia.Controls.Dock.Left))
        };
    }

    /// <summary>The explanation without the answer it starts with ("chaise: c'est un meuble…" → "c'est un meuble…").</summary>
    private static string Reason(string explanation, string answer) =>
        explanation.StartsWith(answer + ":", StringComparison.Ordinal) ? explanation[(answer.Length + 1)..].Trim() : explanation;

    /// <summary>A choice, in a frame: in training green when it is the right one, red when it was given and wrong; no click any more.</summary>
    private static Border Choice(IqWindowViewModel vm, IqQuestion q, Button button, int index)
    {
        var frame = new Border { Child = button, BorderThickness = new Thickness(3), BorderBrush = Brushes.Transparent, CornerRadius = new CornerRadius(6) };
        if (vm.Stage != IqStage.Feedback)
            return frame;
        button.IsHitTestVisible = false;
        button.Focusable = false;
        IBrush? colour = index == q.Answer ? Ui.Green : index == vm.GivenChoice ? Ui.Red : null;
        if (colour is not null)
            frame.BorderBrush = colour;
        else
            button.Foreground = Ui.Muted;
        return frame;
    }

    /// <summary>Training, memory: the digits that were shown and those typed.</summary>
    private static Control MemoryFeedback(IqWindowViewModel vm, IqQuestion q)
    {
        var shown = DailyUi.Mono(string.Join(" ", q.Digits!.ToCharArray()), Ui.Green, 54);
        shown.HorizontalAlignment = HorizontalAlignment.Center;
        var typed = DailyUi.Mono(vm.GivenText == "—" ? "(nothing typed)" : string.Join(" ", vm.GivenText.ToCharArray()), vm.GivenRight ? Ui.Green : Ui.Red, 34);
        typed.HorizontalAlignment = HorizontalAlignment.Center;
        var a = Ui.Caption("THE DIGITS", Ui.Muted, 11);
        var b = Ui.Caption("YOU TYPED", Ui.Muted, 11);
        a.HorizontalAlignment = b.HorizontalAlignment = HorizontalAlignment.Center;
        var column = Ui.Column(24, a, shown, b, typed);
        column.VerticalAlignment = VerticalAlignment.Center;
        return column;
    }

    private static Control ChoiceQuestion(IqWindowViewModel vm, IqQuestion q)
    {
        var numbers = q.Kind == IqTestKind.Numbers;
        var text = Ui.Label(q.Text, Ui.TextBright, numbers ? 34 : 22, wrap: true);
        if (numbers)
            text.FontFamily = Ui.Mono;
        text.TextAlignment = TextAlignment.Center;
        text.HorizontalAlignment = HorizontalAlignment.Stretch;
        var choices = new UniformGrid { Columns = 2 };
        for (var i = 0; i < q.Choices.Count; i++)
        {
            var index = i;
            var button = Ui.Button($"{i + 1}.  {q.Choices[i]}", () => Ui.Run(vm.AnswerCommand, index));
            button.MinWidth = 260;
            button.MinHeight = 60;
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
            button.VerticalContentAlignment = VerticalAlignment.Center;
            button.FontSize = numbers ? 24 : 17;
            var frame = Choice(vm, q, button, i);
            frame.Margin = new Thickness(8, 6);
            choices.Children.Add(frame);
        }
        var column = Ui.Column(40, text, choices);
        column.VerticalAlignment = VerticalAlignment.Center;
        return column;
    }

    private static Control MatrixQuestion(IqWindowViewModel vm, IqQuestion q)
    {
        var grid = new IqMatrix(q.Cells!) { Width = 360, Height = 360, HorizontalAlignment = HorizontalAlignment.Center };
        var choices = new UniformGrid { Columns = 6, HorizontalAlignment = HorizontalAlignment.Center };
        for (var i = 0; i < q.CellChoices!.Count; i++)
        {
            var index = i;
            var letter = ((char)('A' + i)).ToString();
            var button = new Button
            {
                Content = new IqMatrix([q.CellChoices[i]]) { Label = letter, IsHitTestVisible = false },
                Width = 110,
                Height = 130,
                Padding = new Thickness(2),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch
            };
            ToolTip.SetTip(button, $"{letter} (key {letter} or {i + 1})");
            button.Click += (_, _) => Ui.Run(vm.AnswerCommand, index);
            var frame = Choice(vm, q, button, i);
            frame.Margin = new Thickness(5);
            choices.Children.Add(frame);
        }
        var text = Ui.Label(q.Text, Ui.TextBright, 18, wrap: true);
        text.TextAlignment = TextAlignment.Center;
        text.HorizontalAlignment = HorizontalAlignment.Stretch;
        return Ui.Column(18, text, grid, choices);
    }

    private static Control MemoryQuestion(IqWindowViewModel vm, IqQuestion q)
    {
        var text = Ui.Label(q.Text, Ui.TextBright, 18, wrap: true);
        text.TextAlignment = TextAlignment.Center;
        text.HorizontalAlignment = HorizontalAlignment.Stretch;
        Control middle;
        if (vm.Stage == IqStage.Memorize)
        {
            var digits = DailyUi.Mono(string.Join(" ", q.Digits!.ToCharArray()), Ui.Accent, 54);
            digits.HorizontalAlignment = HorizontalAlignment.Center;
            middle = digits;
        }
        else
        {
            var edit = Ui.Edit(vm, nameof(vm.Typed), "Type the digits, then Enter");
            edit.FontFamily = Ui.Mono;
            edit.FontSize = 34;
            edit.TextAlignment = TextAlignment.Center;
            edit.Width = 520;
            edit.MinHeight = 70;
            edit.HorizontalAlignment = HorizontalAlignment.Center;
            edit.AddHandler(InputElement.KeyDownEvent, (object? _, KeyEventArgs e) =>
            {
                if (e.Key == Key.Enter)
                {
                    // The text typed is in the view model before it is checked.
                    vm.Typed = edit.Text ?? string.Empty;
                    Ui.Run(vm.SubmitDigitsCommand);
                    e.Handled = true;
                }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            edit.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => edit.Focus());
            var ok = Ui.Command("OK", vm.SubmitDigitsCommand, tooltip: "Check (Enter)", primary: true);
            ok.MinWidth = 120;
            ok.MinHeight = 50;
            ok.HorizontalAlignment = HorizontalAlignment.Center;
            ok.HorizontalContentAlignment = HorizontalAlignment.Center;
            middle = Ui.Column(16, edit, ok);
        }
        var column = Ui.Column(40, text, middle);
        column.VerticalAlignment = VerticalAlignment.Center;
        return column;
    }

    // ── The end ──

    private static Control Result(IqWindowViewModel vm)
    {
        var reviews = new StackPanel { Spacing = 6 };
        foreach (var r in vm.Reviews)
        {
            var mark = Ui.Label(r.Mark, r.IsRight ? Ui.Green : Ui.Red, 16);
            mark.MinWidth = 24;
            mark.VerticalAlignment = VerticalAlignment.Top;
            var detail = Ui.Column(1,
                Ui.Label($"{r.Number}. {r.Test} · {r.Question}", Ui.TextBright, 12, wrap: true),
                Ui.Label(r.IsRight ? $"{r.Given} — {r.Why}" : $"your answer: {r.Given} · right: {r.Right} — {r.Why}", Ui.Muted, 11, wrap: true));
            reviews.Children.Add(Ui.Dock((mark, Avalonia.Controls.Dock.Left), (detail, Avalonia.Controls.Dock.Left)));
        }
        var column = Ui.Column(12,
            Ui.Bound(vm, nameof(vm.ResultTitle), Ui.Accent, 24),
            Ui.Bound(vm, nameof(vm.ResultDetail), Ui.Text, 13, wrap: true),
            Ui.Row(10,
                Ui.Command("↺ AGAIN · FULL TEST", vm.StartCommand, "Full", primary: true),
                Ui.Command("◀ THE TESTS", vm.QuitCommand)),
            Ui.Label("Indicative score, for fun and training: not a measured IQ.", Ui.Muted, 11),
            DailyUi.Separator(),
            Ui.Caption("REVIEW", Ui.Warm, 11),
            reviews);
        column.Margin = new Thickness(24, 18);
        return Ui.Scroll(column);
    }
}

/// <summary>
/// A matrix of /iq drawn: 9 cells in a 3 × 3 grid (the missing one a "?"), or a single cell (a choice, with its
/// letter). A cell holds 1 to 3 shapes (circle, square, triangle, diamond, hexagon), filled or outlined, turned by
/// their angle.
/// </summary>
public sealed class IqMatrix(IReadOnlyList<IqCell?> cells) : Control
{
    public string Label { get; set; } = string.Empty;

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        var ink = Ui.TextBright;
        if (cells.Count == 1)
        {
            var top = string.IsNullOrEmpty(Label) ? 0 : 22;
            if (top > 0)
                DailyUi.DrawText(context, Label, new Point(0, 2), 14, Ui.Warm, size.Width, bold: true);
            DrawCell(context, cells[0], new Rect(6, top + 4, Math.Max(1, size.Width - 12), Math.Max(1, size.Height - top - 10)), ink);
            return;
        }
        var side = Math.Min(size.Width, size.Height);
        var origin = new Point((size.Width - side) / 2, (size.Height - side) / 2);
        var cell = side / 3;
        var edge = new Pen(Ui.Edge, 1.5);
        for (var i = 0; i < 9; i++)
        {
            var rect = new Rect(origin.X + i % 3 * cell, origin.Y + i / 3 * cell, cell, cell).Deflate(3);
            context.DrawRectangle(Ui.Field, edge, rect);
            if (i < cells.Count && cells[i] is { } c)
                DrawCell(context, c, rect.Deflate(7), ink);
            else
            {
                var mark = DailyUi.Text("?", cell * 0.5, Ui.Warm, bold: true);
                context.DrawText(mark, new Point(rect.Center.X - mark.Width / 2, rect.Center.Y - mark.Height / 2));
            }
        }
    }

    private static void DrawCell(DrawingContext context, IqCell? cell, Rect area, IBrush ink)
    {
        if (cell is null)
            return;
        // 1: in the middle; 2: side by side; 3: in a triangle.
        var r = Math.Min(area.Width, area.Height) * (cell.Count == 1 ? 0.3 : 0.2);
        var centre = area.Center;
        Point[] spots = cell.Count switch
        {
            1 => [centre],
            2 => [centre + new Point(-r * 1.15, 0), centre + new Point(r * 1.15, 0)],
            _ => [centre + new Point(0, -r * 1.1), centre + new Point(-r * 1.2, r * 0.9), centre + new Point(r * 1.2, r * 0.9)]
        };
        foreach (var spot in spots)
            DrawShape(context, cell.Shape, spot, r, cell.Rotation * Math.PI / 180, cell.Filled, ink);
    }

    private static void DrawShape(DrawingContext context, IqShape shape, Point centre, double r, double angle, bool filled, IBrush ink)
    {
        var pen = new Pen(ink, 2.5) { LineJoin = PenLineJoin.Round };
        if (shape == IqShape.Circle)
        {
            context.DrawEllipse(filled ? ink : null, filled ? null : pen, centre, r, r);
            return;
        }
        var (sides, start) = shape switch
        {
            IqShape.Square => (4, Math.PI / 4),
            IqShape.Diamond => (4, 0.0),
            IqShape.Triangle => (3, -Math.PI / 2),
            _ => (6, 0.0)
        };
        // A diamond is taller than wide, so that it is never taken for a square turned by 45°.
        var (sx, sy) = shape == IqShape.Diamond ? (0.72, 1.15) : (1.0, 1.0);
        var points = new Point[sides];
        for (var k = 0; k < sides; k++)
        {
            var a = start + k * Math.Tau / sides;
            var x = Math.Cos(a) * sx;
            var y = Math.Sin(a) * sy;
            // The shape first, then turned by its angle.
            var rx = x * Math.Cos(angle) - y * Math.Sin(angle);
            var ry = x * Math.Sin(angle) + y * Math.Cos(angle);
            points[k] = centre + new Point(rx * r, ry * r);
        }
        context.DrawGeometry(filled ? ink : null, filled ? null : pen, DailyUi.Polygon(points));
    }
}
