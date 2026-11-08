using System;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Daily;

/// <summary>
/// /game:solitaire: the morpion solitaire. Header: the clock, the lines drawn, the score (weighted by the lines and the speed of
/// the moves), the moves left and the best score of the session, HINTS (asked first: the score of the game is then not
/// saved), UNDO (⌘/Ctrl Z), STOP (give up), BEST SCORES (the 10 best on the server) and NEW GAME. Middle: the board
/// (<see cref="Game1Canvas"/>). At the end of a game (STOP or no move left), a dialog asks for a comment and the score
/// is saved on the Kaonomi server.
/// </summary>
[ToolView(typeof(Game1WindowViewModel), "Game 1 · Morpion solitaire", "✚", 980, 920)]
public sealed class Game1View : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (Game1WindowViewModel)viewModel;
        var canvas = new Game1Canvas(vm);

        // ── The clock, the lines, the score ──
        var clock = DailyUi.Mono("00:00", Ui.TextBright, 16);
        ToolTip.SetTip(clock, "The time of the game · the bonus is the one the next move gets now (×2 at once, ×1.5 after 15 s, towards ×1)");
        var lines = Ui.Label("", Ui.TextBright, 15, bold: true);
        var score = Ui.Label("", Ui.Accent, 15, bold: true);
        ToolTip.SetTip(score, $"Each line is worth ({Game1WindowViewModel.BasePoints} + {Game1WindowViewModel.PointsPerLine} × its number) × a speed bonus: "
            + $"1 + 1 / (1 + seconds since the move before / {Game1WindowViewModel.SpeedSeconds:0}) · ×2 at once, ×1.5 after 15 s, ×1.2 after a minute · "
            + "the more lines, the more each new one is worth; thinking never costs points");
        var info = Ui.Label("", Ui.Muted, 12);
        Ui.When(score, vm, () =>
        {
            lines.Text = $"LINES {vm.Lines}";
            score.Text = $"SCORE {vm.Score}";
            info.Text = vm.IsFinished
                ? $"Game over · best {vm.Best}{(vm.HintsUsed ? " · hints: not saved" : string.Empty)}"
                : $"{vm.MovesLeft} move(s) possible · best {vm.Best}{(vm.HintsUsed ? " · hints: not saved" : string.Empty)}";
        }, nameof(vm.Lines), nameof(vm.Score), nameof(vm.MovesLeft), nameof(vm.Best), nameof(vm.IsFinished), nameof(vm.HintsUsed), nameof(vm.End));

        // The clock runs while the game goes on.
        DailyUi.Every(clock, TimeSpan.FromMilliseconds(250), () =>
        {
            var elapsed = vm.Elapsed;
            clock.Text = vm.IsFinished
                ? $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}"
                : $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00} · ×{Game1WindowViewModel.SpeedBonus(vm.SinceLastMove.TotalSeconds):0.00}";
        });

        // ── Hints: asked first, the score of the game is then not saved ──
        var hints = new CheckBox { Content = "Hints", Focusable = false };
        ToolTip.SetTip(hints, "Ring the empty crossings where a point can be added · the score of the game is then not saved");
        Ui.When(hints, vm, () => hints.IsChecked = vm.ShowHints, nameof(vm.ShowHints));
        hints.Click += (_, _) =>
        {
            if (hints.IsChecked != true)
            {
                vm.ShowHints = false;
                return;
            }
            if (vm.HintsUsed)
            {
                vm.ShowHints = true;
                return;
            }
            hints.IsChecked = false;
            _ = AskHintsAsync(vm);
        };

        var header = DailyUi.Toolbar(
            [Ui.Label("Game 1", Ui.TextBright, 14, bold: true), clock, lines, score, info],
            [hints,
             Ui.Command("↶ UNDO", vm.UndoCommand, tooltip: "Take back the last move and its points, or the point being added (Ctrl+Z / ⌘Z)"),
             Ui.Command("■ STOP", vm.StopCommand, tooltip: "Give up: the game ends and its score is saved"),
             Ui.Button("🏆 BEST SCORES", () => _ = ShowBestAsync(shell, vm), "The 10 best scores saved on the Kaonomi server"),
             Ui.Command("✚ NEW GAME", vm.NewGameCommand, tooltip: "Start again from the cross of 36 points", primary: true)]);

        var rules = Ui.Label(
            "Add a point on an empty crossing, then click a black point: the line of 4 cells (5 points) goes from it through the new point, " +
            "horizontally, vertically or diagonally · a line never goes over a line or a piece of one, it may cross one · " +
            "the more lines and the faster the moves, the higher the score · right click or Escape: take the point away",
            Ui.Muted, 11, wrap: true);
        rules.Margin = new Thickness(10, 6, 10, 0);

        var board = new Border { Child = canvas, BorderBrush = Ui.Edge, BorderThickness = new Thickness(1), Margin = new Thickness(8) };

        var status = DailyUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError));
        var page = DailyUi.Page(2, header, rules, board, status);

        // The end of a game: the comment, then the score saved.
        Action<Game1End> finished = end => Ui.OnMain(() => _ = FinishedAsync(vm, end));
        DailyUi.Hook(page, () => vm.Finished += finished, () => vm.Finished -= finished);
        canvas.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => canvas.Focus());
        return page;
    }

    /// <summary>Asks whether to show the hints, the score of the game being then not saved.</summary>
    private static async Task AskHintsAsync(Game1WindowViewModel vm)
    {
        if (await Dialogs.Confirm("Show the hints?",
                "The hints ring every crossing where a point can be added.\n\nIf you show them, the score of this game will NOT be saved (nor be in the best scores).",
                "Show the hints", "Cancel"))
            vm.RevealHints();
    }

    /// <summary>The game is over: the comment of the player, then the score saved (unless the hints were shown).</summary>
    private static async Task FinishedAsync(Game1WindowViewModel vm, Game1End end)
    {
        var why = end == Game1End.Blocked ? "No move is left." : "You stopped the game.";
        var summary = $"{why}\n\nScore: {vm.Score} points · {vm.Lines} line(s) in {vm.Elapsed:mm\\:ss}";
        if (vm.HintsUsed)
        {
            await Dialogs.Message("Game over", summary + "\n\nThe hints were shown: the score is not saved.");
            return;
        }
        if (vm.Lines == 0)
        {
            await Dialogs.Message("Game over", summary + "\n\nNo line drawn: nothing to save.");
            return;
        }
        var comment = await Dialogs.Ask("Game over", summary + "\n\nYour comment on this game (optional), saved with the score:", string.Empty, multiline: true);
        var result = await vm.SaveScoreAsync(comment);
        await Dialogs.Message("Score", result);
    }

    /// <summary>The window of the 10 best scores saved on the server.</summary>
    private static async Task ShowBestAsync(Shell shell, Game1WindowViewModel vm)
    {
        var list = new StackPanel { Spacing = 4 };
        var state = Ui.Label("Reading the best scores…", Ui.Muted, 12, wrap: true);
        var content = DailyUi.Page(2, Ui.Caption("The 10 best scores · Game 1", Ui.Warm, 13), state, Ui.Scroll(list));
        content.RowSpacing = 10;
        content.Margin = new Thickness(16);
        shell.OpenControl("game1-best", "Game 1 · Best scores", "🏆", content, new Size(720, 520));
        try
        {
            var scores = await vm.BestScoresAsync();
            list.Children.Clear();
            state.Text = scores.Count == 0 ? "No score saved yet: finish a game (STOP or no move left) without the hints." : string.Empty;
            state.IsVisible = scores.Count == 0;
            var rank = 0;
            foreach (var s in scores)
            {
                rank++;
                var medal = rank switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => $"{rank}." };
                var time = TimeSpan.FromSeconds(s.DurationSeconds);
                var date = Ui.Label(s.Date?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty, Ui.Muted, 11);
                var endLabel = Ui.Label(s.End == "blocked" ? "no move left" : "stopped", Ui.Muted, 11);
                var head = Ui.Dock(
                    (endLabel, Avalonia.Controls.Dock.Right),
                    (Ui.Row(12,
                        Ui.Label(medal, Ui.TextBright, 15),
                        Ui.Label($"{s.Score} pts", Ui.Accent, 15),
                        Ui.Label($"{s.Lines} lines", Ui.TextBright, 13),
                        Ui.Label($"{(int)time.TotalMinutes:00}:{time.Seconds:00}", Ui.Text, 13),
                        date), Avalonia.Controls.Dock.Left));
                var row = Ui.Column(2, head);
                if (!string.IsNullOrWhiteSpace(s.Comment))
                {
                    var comment = Ui.Label($"“{s.Comment}”", Ui.Text, 12, wrap: true);
                    comment.Margin = new Thickness(30, 0, 0, 0);
                    row.Children.Add(comment);
                }
                row.Children.Add(DailyUi.Separator());
                list.Children.Add(row);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            state.Text = "The best scores could not be read: " + ex.Message;
            state.IsVisible = true;
        }
    }
}
