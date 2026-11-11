using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Daily;

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
        var model = Ui.Combo(vm.Models, vm, nameof(vm.Model));
        model.MinWidth = 220;
        model.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(model, "The model of Ollama playing Black (the one of the chat at first)");
        var retry = Ui.Command("↻ RETRY", vm.RetryCommand, tooltip: "Ask the model again for Black's move", primary: true).Visible(vm, nameof(vm.BlackStuck));
        var header = DailyUi.Toolbar(
            [Ui.Label("Chess", Ui.TextBright, 14, bold: true), Ui.Caption("BLACK:", Ui.Muted, 11), model],
            [retry,
             Ui.Command("↶ UNDO", vm.UndoCommand, tooltip: "Take back your last move and the model's answer"),
             Ui.Command("⚑ RESIGN", vm.ResignCommand, tooltip: "Give up the game"),
             Ui.Button("📋 PGN", () =>
             {
                 Dialogs.Copy(vm.Pgn());
                 vm.Say("The game is copied in PGN (paste it into any chess program)", false);
             }, "Copy the game in the PGN notation"),
             Ui.Command("✚ NEW GAME", vm.NewGameCommand, tooltip: "Start again, you with White", primary: true)]);

        // ── Right: players, thinking, comment, moves ──
        var blackName = Ui.Label("", Ui.TextBright, 14, bold: true);
        Ui.When(blackName, vm, () => blackName.Text = $"♚  {vm.Model ?? "LLM"}", nameof(vm.Model));
        var blackTaken = TakenLabel();
        var whiteName = Ui.Label("♔  You", Ui.TextBright, 14, bold: true);
        var whiteTaken = TakenLabel();

        var thinking = Ui.Label("", Ui.Accent, 12).Visible(vm, nameof(vm.IsThinking));
        DailyUi.Every(thinking, TimeSpan.FromMilliseconds(500), () =>
        {
            if (vm.IsThinking)
                thinking.Text = $"{vm.Model} is thinking… {(int)(DateTimeOffset.Now - vm.ThinkingSince).TotalSeconds} s";
        });

        var comment = Ui.Label("", Ui.Text, 12, wrap: true);
        Ui.When(comment, vm, () =>
        {
            comment.Text = vm.Comment.Length > 0 ? $"“{vm.Comment}”" : string.Empty;
            comment.IsVisible = vm.Comment.Length > 0;
        }, nameof(vm.Comment));

        var moves = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto"), ColumnSpacing = 14, RowSpacing = 3 };
        var movesScroll = Ui.Scroll(moves);
        var result = Ui.Label("", Ui.Warm, 13, wrap: true, bold: true);

        void Refresh()
        {
            var rows = vm.Rows;
            var last = vm.Game.San.Count - 1;
            moves.Children.Clear();
            moves.RowDefinitions.Clear();
            void Add(Control c, int row, int column)
            {
                Grid.SetRow(c, row);
                Grid.SetColumn(c, column);
                moves.Children.Add(c);
            }
            for (var k = 0; k < rows.Count; k++)
            {
                var (number, white, black) = rows[k];
                moves.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Add(Ui.Label($"{number}.", Ui.Muted, 13), k, 0);
                Add(MoveLabel(white, k * 2 == last), k, 1);
                Add(MoveLabel(black, k * 2 + 1 == last), k, 2);
            }
            if (rows.Count == 0)
            {
                moves.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Add(Ui.Label("No move yet: your turn", Ui.Muted, 12), 0, 1);
                Grid.SetColumnSpan(moves.Children[^1], 2);
            }
            result.Text = vm.Game.IsOver ? $"{vm.Game.Result} · {vm.Game.Reason}" : string.Empty;
            result.IsVisible = vm.Game.IsOver;
            blackTaken.Text = Taken(vm, white: true);
            whiteTaken.Text = Taken(vm, white: false);
            var balance = vm.Balance();
            whiteTaken.Text += balance > 0 ? $"  +{balance}" : string.Empty;
            blackTaken.Text += balance < 0 ? $"  +{-balance}" : string.Empty;
            // The last move in view.
            Dispatcher.UIThread.Post(movesScroll.ScrollToEnd, DispatcherPriority.Background);
        }
        Action changed = () => Ui.OnMain(Refresh);

        var top = Ui.Column(8, blackName, blackTaken, thinking, comment, DailyUi.Separator(), Ui.Caption("MOVES", Ui.Warm, 11));
        var bottom = Ui.Column(8, result, DailyUi.Separator(), whiteTaken, whiteName);
        var side = DailyUi.Page(1, top, movesScroll, bottom);
        var sidePanel = Ui.Box(side, 12);
        sidePanel.Width = 300;
        sidePanel.Margin = new Thickness(0, 10, 10, 10);

        board.Margin = new Thickness(10, 10, 10, 10);
        var body = Ui.Dock((sidePanel, Avalonia.Controls.Dock.Right), (board, Avalonia.Controls.Dock.Left));
        var status = DailyUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError));
        var page = DailyUi.Page(1, header, body, status);
        DailyUi.Hook(page, () => vm.Changed += changed, () => vm.Changed -= changed);
        Refresh();
        return page;
    }

    private static TextBlock MoveLabel(string san, bool last)
    {
        var label = DailyUi.Mono(san, last ? Ui.Accent : Ui.TextBright, 13);
        label.MinWidth = 76;
        return label;
    }

    private static TextBlock TakenLabel()
    {
        var label = Ui.Label("", Ui.Text, 18);
        label.FontFamily = ChessBoard.PieceFont;
        label.MinHeight = 24;
        return label;
    }

    /// <summary>The pieces of <paramref name="white"/>'s side taken by the other one, as symbols.</summary>
    private static string Taken(ChessWindowViewModel vm, bool white) =>
        string.Concat(vm.Taken(white).Select(kind => white ? ChessBoard.HollowGlyph(kind) : ChessBoard.Glyph(kind)));
}
