using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Daily;

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
        var model = Ui.Combo(vm.Models, vm, nameof(vm.Model));
        model.MinWidth = 220;
        model.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(model, "The model of Ollama playing White (the one of the chat at first)");
        var size = Ui.Combo(GoWindowViewModel.Sizes, vm, nameof(vm.BoardSize), s => $"{s}×{s}");
        size.MinWidth = 90;
        size.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(size, "The size of the board: a game not started changes at once, otherwise at NEW GAME");
        var retry = Ui.Command("↻ RETRY", vm.RetryCommand, tooltip: "Ask the model again for White's move", primary: true).Visible(vm, nameof(vm.WhiteStuck));
        var count = Ui.Command("✓ COUNT", vm.CountCommand, tooltip: "Count the score with the groups marked dead (area scoring, komi 7.5)", primary: true);
        // IsScoring follows the game: read again at each change.
        void ShowCount() => count.IsVisible = vm.IsScoring;
        var header = DailyUi.Toolbar(
            [Ui.Label("Go", Ui.TextBright, 14, bold: true), Ui.Caption("WHITE:", Ui.Muted, 11), model, size],
            [retry,
             count,
             Ui.Command("PASS", vm.PassCommand, tooltip: "Pass: two passes in a row end the play, then the dead stones are marked and counted"),
             Ui.Command("↶ UNDO", vm.UndoCommand, tooltip: "Take back your last move and the model's answer"),
             Ui.Command("⚑ RESIGN", vm.ResignCommand, tooltip: "Give up the game"),
             Ui.Button("📋 SGF", () =>
             {
                 Dialogs.Copy(vm.Sgf());
                 vm.Say("The game is copied in SGF (paste it into any go program)", false);
             }, "Copy the game in the SGF format"),
             Ui.Command("✚ NEW GAME", vm.NewGameCommand, tooltip: "Start again, you with Black", primary: true)]);

        // ── Right: players, thinking, comment, moves ──
        var whiteName = Ui.Label("", Ui.TextBright, 14, bold: true);
        Ui.When(whiteName, vm, () => whiteName.Text = $"○  {vm.Model ?? "LLM"}", nameof(vm.Model));
        var whiteTaken = Ui.Label("", Ui.Text, 12);
        var blackName = Ui.Label("●  You", Ui.TextBright, 14, bold: true);
        var blackTaken = Ui.Label("", Ui.Text, 12);

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
        var rules = Ui.Label("Chinese rules: stones + territory, komi 7.5 · no suicide · no repeated position (ko) · two passes end the play", Ui.Muted, 11, wrap: true);

        void Refresh()
        {
            var rows = vm.Rows;
            var last = vm.Game.Moves.Count - 1;
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
                var (number, black, white) = rows[k];
                moves.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Add(Ui.Label($"{number}.", Ui.Muted, 13), k, 0);
                Add(MoveLabel(black, k * 2 == last), k, 1);
                Add(MoveLabel(white, k * 2 + 1 == last), k, 2);
            }
            if (rows.Count == 0)
            {
                moves.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var none = Ui.Label("No move yet: your turn", Ui.Muted, 12);
                Grid.SetColumnSpan(none, 2);
                Add(none, 0, 1);
            }
            var board = vm.Game.Current;
            whiteTaken.Text = $"Stones taken: {board.TakenByWhite} · komi {GoWindowViewModel.Komi.ToString(CultureInfo.InvariantCulture)}";
            blackTaken.Text = $"Stones taken: {board.TakenByBlack}";
            result.Text = vm.Game.Phase == GoPhase.Over ? $"{vm.Game.Result} · {vm.Game.Reason}"
                : vm.Game.Phase == GoPhase.Scoring ? "Counting: click the dead groups, then COUNT" : string.Empty;
            result.IsVisible = result.Text.Length > 0;
            ShowCount();
            Dispatcher.UIThread.Post(movesScroll.ScrollToEnd, DispatcherPriority.Background);
        }
        Action changed = () => Ui.OnMain(Refresh);

        var top = Ui.Column(8, whiteName, whiteTaken, thinking, comment, DailyUi.Separator(), Ui.Caption("MOVES", Ui.Warm, 11));
        var bottom = Ui.Column(8, result, rules, DailyUi.Separator(), blackTaken, blackName);
        var side = DailyUi.Page(1, top, movesScroll, bottom);
        var sidePanel = Ui.Box(side, 12);
        sidePanel.Width = 290;
        sidePanel.Margin = new Thickness(0, 10, 10, 10);

        goban.Margin = new Thickness(10);
        var body = Ui.Dock((sidePanel, Avalonia.Controls.Dock.Right), (goban, Avalonia.Controls.Dock.Left));
        var status = DailyUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError));
        var page = DailyUi.Page(1, header, body, status);
        DailyUi.Hook(page, () => vm.Changed += changed, () => vm.Changed -= changed);
        Ui.When(count, vm, ShowCount);
        Refresh();
        return page;
    }

    private static TextBlock MoveLabel(string move, bool last)
    {
        var label = DailyUi.Mono(move, last ? Ui.Accent : Ui.TextBright, 13);
        label.MinWidth = 64;
        return label;
    }
}
