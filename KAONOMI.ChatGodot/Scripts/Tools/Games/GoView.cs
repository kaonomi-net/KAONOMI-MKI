using System;
using System.Globalization;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Games;

/// <summary>
/// /game:go: a game of go against the LLM, the user with Black. Header: the model playing White, the size of the
/// board, PASS, COUNT (after two passes), UNDO, RESIGN, RETRY (when the model gave no move), SGF (copied), NEW GAME.
/// Left: the goban (<see cref="GobanCanvas"/>). Right: the stones taken, what the model is doing and said, the list
/// of every move, the result.
/// </summary>
[ToolView(typeof(GoWindowViewModel), "Go", "⚫", 1160, 860)]
public sealed class GoView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (GoWindowViewModel)viewModel;
        var goban = new GobanCanvas(vm);

        // ── Header ──
        var model = Bind.Options(new OptionButton { TooltipText = "The model of Ollama playing White (the one of the chat at first)", CustomMinimumSize = new Vector2(220, 0) },
            vm.Models, vm, nameof(vm.Model));
        var size = Bind.Options(new OptionButton { TooltipText = "The size of the board: a game not started changes at once, otherwise at NEW GAME" },
            GoWindowViewModel.Sizes, vm, nameof(vm.BoardSize), s => $"{s}×{s}");
        var retry = Ui.Command("↻ RETRY", vm.RetryCommand, primary: true, tooltip: "Ask the model again for White's move");
        Bind.Visible(retry, vm, nameof(vm.WhiteStuck));
        var count = Ui.Command("✓ COUNT", vm.CountCommand, primary: true, tooltip: "Count the score with the groups marked dead (area scoring, komi 7.5)");
        Bind.Visible(count, vm, nameof(vm.IsScoring));
        var header = Ui.Toolbar(Ui.Row(8,
            Eve.Eve.Caption("Go", Eve.Eve.TextBright, 14),
            Eve.Eve.Caption("WHITE:", Eve.Eve.Muted, 11),
            model,
            size,
            Ui.Spring(),
            retry,
            count,
            Ui.Command("PASS", vm.PassCommand, tooltip: "Pass: two passes in a row end the play, then the dead stones are marked and counted"),
            Ui.Command("↶ UNDO", vm.UndoCommand, tooltip: "Take back your last move and the model's answer"),
            Ui.Command("⚑ RESIGN", vm.ResignCommand, tooltip: "Give up the game"),
            Ui.Action("📋 SGF", () =>
            {
                Dialogs.Copy(vm.Sgf());
                vm.Say("The game is copied in SGF (paste it into any go program)", false);
            }, tooltip: "Copy the game in the SGF format"),
            Ui.Command("✚ NEW GAME", vm.NewGameCommand, primary: true, tooltip: "Start again, you with Black")));

        // ── Right: players, thinking, comment, moves ──
        var whiteName = Eve.Eve.Text_("", Eve.Eve.TextBright, 14);
        whiteName.AddThemeFontOverride("font", Eve.Eve.Display);
        Bind.When(whiteName, vm, () => whiteName.Text = $"○  {vm.Model ?? "LLM"}", nameof(vm.Model));
        var whiteTaken = Eve.Eve.Text_("", Eve.Eve.Text, 12);
        var blackName = Eve.Eve.Text_("●  You", Eve.Eve.TextBright, 14);
        blackName.AddThemeFontOverride("font", Eve.Eve.Display);
        var blackTaken = Eve.Eve.Text_("", Eve.Eve.Text, 12);

        var thinking = Eve.Eve.Text_("", Eve.Eve.Cyan, 12);
        var progress = new ProgressBar { Indeterminate = true, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 4) };
        var thinkingRow = Ui.Column(4, thinking, progress);
        Bind.Visible(thinkingRow, vm, nameof(vm.IsThinking));
        var timer = new Timer { WaitTime = 0.5, Autostart = true };
        timer.Timeout += () =>
        {
            if (vm.IsThinking)
                thinking.Text = $"{vm.Model} is thinking… {(int)(DateTimeOffset.Now - vm.ThinkingSince).TotalSeconds} s";
        };

        var comment = Eve.Eve.Text_("", Eve.Eve.Text, 12, wrap: true);
        Bind.When(comment, vm, () =>
        {
            comment.Text = vm.Comment.Length > 0 ? $"“{vm.Comment}”" : string.Empty;
            comment.Visible = vm.Comment.Length > 0;
        }, nameof(vm.Comment));

        var moves = new GridContainer { Columns = 3, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        moves.AddThemeConstantOverride("h_separation", 14);
        moves.AddThemeConstantOverride("v_separation", 3);
        var movesScroll = Ui.Scroll(moves);
        movesScroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var result = Eve.Eve.Text_("", Eve.Eve.Amber, 13, wrap: true);
        result.AddThemeFontOverride("font", Eve.Eve.Display);
        var rules = Eve.Eve.Text_("Chinese rules: stones + territory, komi 7.5 · no suicide · no repeated position (ko) · two passes end the play", Eve.Eve.Muted, 11, wrap: true);

        void Refresh()
        {
            if (!GodotObject.IsInstanceValid(moves))
                return;
            foreach (var child in moves.GetChildren())
                child.QueueFree();
            var rows = vm.Rows;
            var last = vm.Game.Moves.Count - 1;
            for (var k = 0; k < rows.Count; k++)
            {
                var (number, black, white) = rows[k];
                moves.AddChild(Eve.Eve.Text_($"{number}.", Eve.Eve.Muted, 13));
                moves.AddChild(MoveLabel(black, k * 2 == last));
                moves.AddChild(MoveLabel(white, k * 2 + 1 == last));
            }
            if (rows.Count == 0)
            {
                moves.AddChild(Eve.Eve.Text_("", Eve.Eve.Muted, 12));
                moves.AddChild(Eve.Eve.Text_("No move yet: your turn", Eve.Eve.Muted, 12));
                moves.AddChild(Eve.Eve.Text_("", Eve.Eve.Muted, 12));
            }
            var board = vm.Game.Current;
            whiteTaken.Text = $"Stones taken: {board.TakenByWhite} · komi {GoWindowViewModel.Komi.ToString(CultureInfo.InvariantCulture)}";
            blackTaken.Text = $"Stones taken: {board.TakenByBlack}";
            result.Text = vm.Game.Phase == GoPhase.Over ? $"{vm.Game.Result} · {vm.Game.Reason}"
                : vm.Game.Phase == GoPhase.Scoring ? "Counting: click the dead groups, then COUNT" : string.Empty;
            result.Visible = result.Text.Length > 0;
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(movesScroll))
                    movesScroll.ScrollVertical = (int)movesScroll.GetVScrollBar().MaxValue;
            }).CallDeferred();
        }
        Action changed = () => Bind.OnMain(Refresh);
        vm.Changed += changed;

        var side = Ui.Column(8,
            whiteName,
            whiteTaken,
            thinkingRow,
            comment,
            Eve.Eve.Separator(),
            Eve.Eve.Caption("MOVES", Eve.Eve.Amber, 11),
            Ui.FillHeight(movesScroll),
            result,
            rules,
            Eve.Eve.Separator(),
            blackTaken,
            blackName);
        side.AddChild(timer);
        var sidePanel = new PanelContainer { CustomMinimumSize = new Vector2(290, 0), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        sidePanel.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.12f), Eve.Eve.Edge with { A = 0.35f }, 12));
        sidePanel.AddChild(side);

        var gobanBox = Ui.Margin(goban, 10, 10, 0, 10);
        gobanBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        gobanBox.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var body = Ui.Row(10, gobanBox, Ui.Margin(sidePanel, 0, 10, 10, 10));
        body.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var status = Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError));
        var page = Ui.Column(0, header, Ui.FillHeight(body), status);
        page.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        Bind.OnGone(page, () => vm.Changed -= changed);
        Refresh();
        return page;
    }


    private static Label MoveLabel(string move, bool last)
    {
        var label = Eve.Eve.Text_(move, last ? Eve.Eve.Cyan : Eve.Eve.TextBright, 13);
        label.AddThemeFontOverride("font", Eve.Eve.Mono);
        label.CustomMinimumSize = new Vector2(64, 0);
        return label;
    }
}
