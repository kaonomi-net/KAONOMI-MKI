using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Games;

/// <summary>
/// /game1: the morpion solitaire. Header: the clock, the lines drawn, the score (weighted by the lines and the speed of
/// the moves), the moves left and the best score of the session, HINTS (asked first: the score of the game is then not
/// saved), UNDO (⌘/Ctrl Z), STOP (give up), BEST SCORES (the 10 best on the server) and NEW GAME. Middle: the board
/// (<see cref="Game1Canvas"/>). At the end of a game (STOP or no move left), a popup asks for a comment and the score
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
        var clock = Eve.Eve.Text_("00:00", Eve.Eve.TextBright, 16);
        clock.AddThemeFontOverride("font", Eve.Eve.Mono);
        clock.TooltipText = "The time of the game · the bonus is the one the next move gets now (×2 at once, ×1.5 after 15 s, towards ×1)";
        var lines = Eve.Eve.Text_("", Eve.Eve.TextBright, 15);
        lines.AddThemeFontOverride("font", Eve.Eve.Display);
        var score = Eve.Eve.Text_("", Eve.Eve.Cyan, 15);
        score.AddThemeFontOverride("font", Eve.Eve.Display);
        score.TooltipText = $"Each line is worth ({Game1WindowViewModel.BasePoints} + {Game1WindowViewModel.PointsPerLine} × its number) × a speed bonus: "
            + $"1 + 1 / (1 + seconds since the move before / {Game1WindowViewModel.SpeedSeconds:0}) · ×2 at once, ×1.5 after 15 s, ×1.2 after a minute · "
            + "the more lines, the more each new one is worth; thinking never costs points";
        var info = Eve.Eve.Text_("", Eve.Eve.Muted, 12);
        Bind.When(score, vm, () =>
        {
            lines.Text = $"LINES {vm.Lines}";
            score.Text = $"SCORE {vm.Score}";
            info.Text = vm.IsFinished
                ? $"Game over · best {vm.Best}{(vm.HintsUsed ? " · hints: not saved" : string.Empty)}"
                : $"{vm.MovesLeft} move(s) possible · best {vm.Best}{(vm.HintsUsed ? " · hints: not saved" : string.Empty)}";
        }, nameof(vm.Lines), nameof(vm.Score), nameof(vm.MovesLeft), nameof(vm.Best), nameof(vm.IsFinished), nameof(vm.HintsUsed));

        // The clock runs while the game goes on.
        var timer = new Timer { WaitTime = 0.25, Autostart = true };
        timer.Timeout += () =>
        {
            var elapsed = vm.Elapsed;
            clock.Text = vm.IsFinished
                ? $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}"
                : $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00} · ×{Game1WindowViewModel.SpeedBonus(vm.SinceLastMove.TotalSeconds):0.00}";
        };

        // ── Hints: asked first, the score of the game is then not saved ──
        var hints = new CheckBox { Text = "Hints", FocusMode = Control.FocusModeEnum.None, TooltipText = "Ring the empty crossings where a point can be added · the score of the game is then not saved" };
        Bind.When(hints, vm, () => hints.SetPressedNoSignal(vm.ShowHints), nameof(vm.ShowHints));
        hints.Toggled += on =>
        {
            if (!on)
            {
                vm.ShowHints = false;
                return;
            }
            if (vm.HintsUsed)
            {
                vm.ShowHints = true;
                return;
            }
            hints.SetPressedNoSignal(false);
            _ = AskHintsAsync(vm);
        };

        var stop = Ui.Command("■ STOP", vm.StopCommand, tooltip: "Give up: the game ends and its score is saved");
        var best = Ui.Action("🏆 BEST SCORES", () => _ = ShowBestAsync(shell, vm), tooltip: "The 10 best scores saved on the Kaonomi server");

        var header = Ui.Toolbar(Ui.Row(12,
            Eve.Eve.Caption("Game 1", Eve.Eve.TextBright, 14),
            clock,
            lines,
            score,
            Ui.Fill(info),
            hints,
            Ui.Command("↶ UNDO", vm.UndoCommand, tooltip: "Take back the last move and its points, or the point being added (Ctrl+Z / ⌘Z)"),
            stop,
            best,
            Ui.Command("✚ NEW GAME", vm.NewGameCommand, primary: true, tooltip: "Start again from the cross of 36 points")));

        var rules = Eve.Eve.Text_(
            "Add a point on an empty crossing, then click a black point: the line of 4 cells (5 points) goes from it through the new point, " +
            "horizontally, vertically or diagonally · a line never goes over a line or a piece of one, it may cross one · " +
            "the more lines and the faster the moves, the higher the score · right click or Escape: take the point away",
            Eve.Eve.Muted, 11, wrap: true);

        var board = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        board.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.2f), Eve.Eve.Edge with { A = 0.45f }, 0));
        board.AddChild(canvas);

        var status = Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError));
        var page = Ui.Column(0, header, Ui.Margin(rules, 10, 6, 10, 0), Ui.FillHeight(Ui.Margin(board, 8, 8, 8, 8)), status);
        page.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        page.AddChild(timer);

        // The end of a game: the comment, then the score saved.
        Action<Game1End> finished = end => Bind.OnMain(() => _ = FinishedAsync(vm, end));
        vm.Finished += finished;
        Bind.OnGone(page, () => vm.Finished -= finished);
        page.Ready += () => Callable.From(() => canvas.GrabFocus()).CallDeferred();
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
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 4);
        var state = Eve.Eve.Text_("Reading the best scores…", Eve.Eve.Muted, 12, wrap: true);
        var content = Ui.Margin(Ui.Column(10,
            Eve.Eve.Caption("The 10 best scores · Game 1", Eve.Eve.Amber, 13),
            state,
            Ui.FillHeight(Ui.Scroll(list))), 16);
        shell.OpenControl("game1-best", "Game 1 · Best scores", "🏆", content, new Vector2(720, 520));
        try
        {
            var scores = await vm.BestScoresAsync();
            if (!GodotObject.IsInstanceValid(list))
                return;
            foreach (var child in list.GetChildren())
                child.QueueFree();
            state.Text = scores.Count == 0 ? "No score saved yet: finish a game (STOP or no move left) without the hints." : string.Empty;
            state.Visible = scores.Count == 0;
            var rank = 0;
            foreach (var s in scores)
            {
                rank++;
                var medal = rank switch { 1 => "🥇", 2 => "🥈", 3 => "🥉", _ => $"{rank}." };
                var time = TimeSpan.FromSeconds(s.DurationSeconds);
                var head = Ui.Row(12,
                    Eve.Eve.Text_(medal, Eve.Eve.TextBright, 15),
                    Eve.Eve.Text_($"{s.Score} pts", Eve.Eve.Cyan, 15),
                    Eve.Eve.Text_($"{s.Lines} lines", Eve.Eve.TextBright, 13),
                    Eve.Eve.Text_($"{(int)time.TotalMinutes:00}:{time.Seconds:00}", Eve.Eve.Text, 13),
                    Ui.Fill(Eve.Eve.Text_(s.Date?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty, Eve.Eve.Muted, 11)),
                    Eve.Eve.Text_(s.End == "blocked" ? "no move left" : "stopped", Eve.Eve.Muted, 11));
                var row = Ui.Column(2, head);
                if (!string.IsNullOrWhiteSpace(s.Comment))
                    row.AddChild(Ui.Margin(Eve.Eve.Text_($"“{s.Comment}”", Eve.Eve.Text, 12, wrap: true), 30, 0, 0, 0));
                row.AddChild(Eve.Eve.Separator());
                list.AddChild(row);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            if (GodotObject.IsInstanceValid(state))
                state.Text = "The best scores could not be read: " + ex.Message;
        }
    }
}
