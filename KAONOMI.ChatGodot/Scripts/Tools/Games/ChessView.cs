using System;
using System.Linq;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Games;

/// <summary>
/// /game:chess: a game against the LLM, the user with White. Header: the model playing Black, NEW GAME, UNDO (the last
/// move and the answer), RESIGN, RETRY (when the model gave no move), PGN (copied). Left: the board
/// (<see cref="ChessBoard"/>). Right: the pieces taken, what the model is doing and said, and the list of every move
/// in algebraic notation, the last one lit.
/// </summary>
[ToolView(typeof(ChessWindowViewModel), "Chess", "♞", 1120, 800)]
public sealed class ChessView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (ChessWindowViewModel)viewModel;
        var board = new ChessBoard(vm);

        // ── Header ──
        var model = Bind.Options(new OptionButton { TooltipText = "The model of Ollama playing Black (the one of the chat at first)", CustomMinimumSize = new Vector2(220, 0) },
            vm.Models, vm, nameof(vm.Model));
        var retry = Ui.Command("↻ RETRY", vm.RetryCommand, primary: true, tooltip: "Ask the model again for Black's move");
        Bind.Visible(retry, vm, nameof(vm.BlackStuck));
        var header = Ui.Toolbar(Ui.Row(8,
            Eve.Eve.Caption("Chess", Eve.Eve.TextBright, 14),
            Eve.Eve.Caption("BLACK:", Eve.Eve.Muted, 11),
            model,
            Ui.Spring(),
            retry,
            Ui.Command("↶ UNDO", vm.UndoCommand, tooltip: "Take back your last move and the model's answer"),
            Ui.Command("⚑ RESIGN", vm.ResignCommand, tooltip: "Give up the game"),
            Ui.Action("📋 PGN", () =>
            {
                Dialogs.Copy(vm.Pgn());
                vm.Say("The game is copied in PGN (paste it into any chess program)", false);
            }, tooltip: "Copy the game in the PGN notation"),
            Ui.Command("✚ NEW GAME", vm.NewGameCommand, primary: true, tooltip: "Start again, you with White")));

        // ── Right: players, thinking, comment, moves ──
        var blackName = Eve.Eve.Text_("", Eve.Eve.TextBright, 14);
        blackName.AddThemeFontOverride("font", Eve.Eve.Display);
        Bind.When(blackName, vm, () => blackName.Text = $"♚  {vm.Model ?? "LLM"}", nameof(vm.Model));
        var blackTaken = TakenLabel();
        var whiteName = Eve.Eve.Text_("♔  You", Eve.Eve.TextBright, 14);
        whiteName.AddThemeFontOverride("font", Eve.Eve.Display);
        var whiteTaken = TakenLabel();

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

        void Refresh()
        {
            if (!GodotObject.IsInstanceValid(moves))
                return;
            foreach (var child in moves.GetChildren())
                child.QueueFree();
            var rows = vm.Rows;
            var last = vm.Game.San.Count - 1;
            for (var k = 0; k < rows.Count; k++)
            {
                var (number, white, black) = rows[k];
                moves.AddChild(Eve.Eve.Text_($"{number}.", Eve.Eve.Muted, 13));
                moves.AddChild(MoveLabel(white, k * 2 == last));
                moves.AddChild(MoveLabel(black, k * 2 + 1 == last));
            }
            if (rows.Count == 0)
            {
                moves.AddChild(Eve.Eve.Text_("", Eve.Eve.Muted, 12));
                moves.AddChild(Eve.Eve.Text_("No move yet: your turn", Eve.Eve.Muted, 12));
                moves.AddChild(Eve.Eve.Text_("", Eve.Eve.Muted, 12));
            }
            result.Text = vm.Game.IsOver ? $"{vm.Game.Result} · {vm.Game.Reason}" : string.Empty;
            result.Visible = vm.Game.IsOver;
            blackTaken.Text = Taken(vm, white: true);
            whiteTaken.Text = Taken(vm, white: false);
            var balance = vm.Balance();
            whiteTaken.Text += balance > 0 ? $"  +{balance}" : string.Empty;
            blackTaken.Text += balance < 0 ? $"  +{-balance}" : string.Empty;
            // The last move in view.
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(movesScroll))
                    movesScroll.ScrollVertical = (int)movesScroll.GetVScrollBar().MaxValue;
            }).CallDeferred();
        }
        Action changed = () => Bind.OnMain(Refresh);
        vm.Changed += changed;

        var side = Ui.Column(8,
            blackName,
            blackTaken,
            thinkingRow,
            comment,
            Eve.Eve.Separator(),
            Eve.Eve.Caption("MOVES", Eve.Eve.Amber, 11),
            Ui.FillHeight(movesScroll),
            result,
            Eve.Eve.Separator(),
            whiteTaken,
            whiteName);
        side.AddChild(timer);
        var sidePanel = new PanelContainer { CustomMinimumSize = new Vector2(300, 0), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        sidePanel.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.12f), Eve.Eve.Edge with { A = 0.35f }, 12));
        sidePanel.AddChild(side);

        var boardBox = Ui.Margin(board, 10, 10, 0, 10);
        boardBox.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        boardBox.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var body = Ui.Row(10, boardBox, Ui.Margin(sidePanel, 0, 10, 10, 10));
        body.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var status = Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError));
        var page = Ui.Column(0, header, Ui.FillHeight(body), status);
        page.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        Bind.OnGone(page, () => vm.Changed -= changed);
        Refresh();
        return page;
    }


    private static Label MoveLabel(string san, bool last)
    {
        var label = Eve.Eve.Text_(san, last ? Eve.Eve.Cyan : Eve.Eve.TextBright, 13);
        label.AddThemeFontOverride("font", Eve.Eve.Mono);
        label.CustomMinimumSize = new Vector2(76, 0);
        return label;
    }

    private static Label TakenLabel()
    {
        var label = Eve.Eve.Text_("", Eve.Eve.Text, 18);
        label.AddThemeFontOverride("font", ChessBoard.PieceFont);
        label.CustomMinimumSize = new Vector2(0, 24);
        return label;
    }

    /// <summary>The pieces of <paramref name="white"/>'s side taken by the other one, as symbols.</summary>
    private static string Taken(ChessWindowViewModel vm, bool white) =>
        string.Concat(vm.Taken(white).Select(kind => white ? ChessBoard.HollowGlyph(kind) : ChessBoard.Glyph(kind)));

}
