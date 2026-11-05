using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// /game:go: a game of go against the LLM. The user plays Black (who starts), the model chosen (the one of the chat by
/// default) plays White: it is given the board, the moves so far, the stones in atari and the list of its legal
/// points, and its answer is held to that list or a pass (structured output of Ollama). Chinese rules
/// (<see cref="GoGame"/>): captures, no suicide, no repeated position (ko), two passes end the play; the user then
/// marks the dead stones and COUNT gives the score (stones and territory, komi 7.5 for White).
/// </summary>
public partial class GoWindowViewModel : ViewModelBase, IDisposable
{
    public static readonly TimeSpan ThinkTimeout = TimeSpan.FromMinutes(4);

    public const int Attempts = 3;

    public const double Komi = 7.5;

    public static IReadOnlyList<int> Sizes { get; } = [9, 13, 19];

    private readonly OllamaModelService? _ollama;
    private readonly string _language;
    private CancellationTokenSource? _thinking;
    private bool _disposed;

    public GoWindowViewModel() : this(null, [], null, "FR")
    {
    }

    /// <param name="ollama">Where the model playing White runs (none: White does not play, for the tests).</param>
    /// <param name="models">The models of the chat (its list itself: the models loaded later show up too).</param>
    /// <param name="model">The model chosen at first (the one of the chat).</param>
    /// <param name="language">The language of the chat, for the comments of the model.</param>
    public GoWindowViewModel(OllamaModelService? ollama, IEnumerable<string> models, string? model, string language)
    {
        _ollama = ollama;
        _language = SpeechLanguage.Name(language);
        Models = models as ObservableCollection<string> ?? new ObservableCollection<string>(models);
        Model = model is { Length: > 0 } && Models.Contains(model) ? model : Models.FirstOrDefault() ?? model;
        Game = new GoGame(BoardSize, Komi);
        Status = "You play Black: click a point · komi 7.5 for White";
    }

    /// <summary>Raised when the board changes: the board and the list are drawn again.</summary>
    public event Action? Changed;

    public GoGame Game { get; private set; }

    public ObservableCollection<string> Models { get; }

    [ObservableProperty]
    private string? _model;

    /// <summary>The size of the board of the next game (a game without a move changes at once).</summary>
    [ObservableProperty]
    private int _boardSize = 19;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMove))]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand), nameof(PassCommand), nameof(ResignCommand))]
    private bool _isThinking;

    [ObservableProperty]
    private string _comment = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    private bool _whiteStuck;

    public DateTimeOffset ThinkingSince { get; private set; }

    /// <summary>True when the user may play: the play going on, Black to move, the model not thinking.</summary>
    public bool CanMove => !IsThinking && Game.Phase == GoPhase.Playing && Game.ToMove > 0;

    /// <summary>True while the dead stones are marked (after two passes).</summary>
    public bool IsScoring => Game.Phase == GoPhase.Scoring;

    /// <summary>The moves by pairs: its number, Black's move, White's move.</summary>
    public IReadOnlyList<(int Number, string Black, string White)> Rows
    {
        get
        {
            var rows = new List<(int, string, string)>();
            var moves = Game.Moves;
            for (var k = 0; k < moves.Count; k += 2)
                rows.Add((k / 2 + 1, moves[k].Name, k + 1 < moves.Count ? moves[k + 1].Name : string.Empty));
            return rows;
        }
    }

    partial void OnBoardSizeChanged(int value)
    {
        // A game not started yet takes the new size at once.
        if (Game.Moves.Count == 0 && Game.Size != value && Sizes.Contains(value))
            NewGame();
    }

    /// <summary>Plays the user's stone at (x, y) if it is allowed, then lets the model answer.</summary>
    public bool TryPlay(int x, int y)
    {
        if (!CanMove)
            return false;
        if (Game.WhyNot(x, y) is { } why)
        {
            Say(why, true);
            return false;
        }
        Game.Play(new GoMove(x, y));
        Comment = string.Empty;
        AfterMove($"You played {GoBoard.PointName(x, y)}");
        _ = WhiteMoveAsync();
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanPass))]
    private void Pass()
    {
        if (!CanMove)
            return;
        Game.Play(GoMove.Pass);
        Comment = string.Empty;
        AfterMove("You passed");
        if (Game.Phase == GoPhase.Playing)
            _ = WhiteMoveAsync();
    }

    private bool CanPass() => CanMove;

    /// <summary>While counting: marks the group at (x, y) dead, or alive again.</summary>
    public void ToggleDead(int x, int y)
    {
        if (!IsScoring)
            return;
        Game.ToggleDead(x, y);
        var (score, _) = Game.Count();
        Changed?.Invoke();
        Say(string.Create(CultureInfo.InvariantCulture, $"Counting: Black {score.Black:0.#} · White {score.White:0.#} — click the dead groups, then COUNT"), false);
    }

    [RelayCommand]
    private void Count()
    {
        if (!IsScoring)
            return;
        Game.Finish();
        AfterMove("Counted");
    }

    private void AfterMove(string what)
    {
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(CanMove));
        OnPropertyChanged(nameof(IsScoring));
        PassCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        ResignCommand.NotifyCanExecuteChanged();
        Changed?.Invoke();
        if (Game.Phase == GoPhase.Over)
            Say($"{Game.Result} · {Game.Reason}", false);
        else if (Game.Phase == GoPhase.Scoring)
        {
            var (score, _) = Game.Count();
            Say(string.Create(CultureInfo.InvariantCulture,
                $"{what} · two passes: the play is over. Click the dead groups (Black {score.Black:0.#} · White {score.White:0.#}), then COUNT"), false);
        }
        else
            Say(what, false);
    }

    // ── White: the model ──

    private async Task WhiteMoveAsync()
    {
        if (_ollama is null || Game.Phase != GoPhase.Playing || Game.ToMove > 0)
            return;
        if (string.IsNullOrWhiteSpace(Model))
        {
            WhiteStuck = true;
            Say("No model to play White: choose one (Ollama started?), then RETRY", true);
            return;
        }
        _thinking?.Cancel();
        var cancel = new CancellationTokenSource();
        _thinking = cancel;
        var game = Game;
        var model = Model;
        WhiteStuck = false;
        ThinkingSince = DateTimeOffset.Now;
        IsThinking = true;
        Say($"{model} is thinking…", false);
        string? failure = null;
        try
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                try
                {
                    var (move, comment) = await AskAsync(game, model, failure, cancel.Token);
                    if (cancel.IsCancellationRequested || !ReferenceEquals(game, Game))
                        return;
                    if (move is { } chosen && Game.Play(chosen))
                    {
                        Comment = comment;
                        IsThinking = false;
                        AfterMove(chosen.IsPass ? $"{model} passed" : $"{model} played {chosen.Name}");
                        return;
                    }
                    failure = "your answer was not one of the legal points";
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
                {
                    if (cancel.IsCancellationRequested)
                        return;
                    failure = ex is OperationCanceledException ? $"no answer within {ThinkTimeout.TotalMinutes:0} minutes" : ex.Message;
                    ActivityLog.Error("Go", $"{model} gave no move (attempt {attempt})", ex);
                    if (ex is HttpRequestException { StatusCode: null })
                        break;
                }
                if (attempt < Attempts)
                    Say($"{model} is thinking again ({failure})…", false);
            }
            IsThinking = false;
            WhiteStuck = true;
            Say($"{model} gave no legal move ({failure}): RETRY, or choose another model", true);
        }
        finally
        {
            if (ReferenceEquals(_thinking, cancel))
            {
                _thinking = null;
                if (!WhiteStuck && !cancel.IsCancellationRequested)
                    IsThinking = false;
            }
            cancel.Dispose();
        }
    }

    private async Task<(GoMove? Move, string Comment)> AskAsync(GoGame game, string model, string? failure, CancellationToken cancellationToken)
    {
        var legal = game.LegalMoves().Select(m => m.Name).ToList();
        var board = game.Current;
        var system =
            $"You are a strong go (baduk, weiqi) player. You play White against a human who plays Black, on a {board.Size}x{board.Size} board, "
            + $"Chinese rules (area scoring), komi {Komi.ToString(CultureInfo.InvariantCulture)} for you. "
            + "Look at the board: save your groups in atari, capture the black stones in atari, cut and connect, make two eyes, take the corners, "
            + "then the sides, then the centre; answer Black's threats; never fill your own eyes. "
            + "Pass only when no move gains anything any more. "
            + "Answer with a JSON object: {\"move\": your point copied exactly from the list of legal points (a column letter and a row number, like D4), or \"pass\", "
            + $"\"comment\": one short sentence to your opponent about your move, in {_language}}}.";
        var prompt = new StringBuilder();
        prompt.Append("Board (X = Black, O = White, . = empty; columns A to ").Append(GoBoard.Columns[board.Size - 1])
            .Append(" without I, row 1 at the bottom):\n").Append(board.Diagram()).Append("\n\n");
        prompt.Append("Stones taken: Black has taken ").Append(board.TakenByBlack).Append(", you (White) have taken ").Append(board.TakenByWhite).Append(".\n");
        prompt.Append("Moves so far: ").Append(game.Moves.Count == 0 ? "none" : game.MoveText(80)).Append('\n');
        if (game.Moves.Count > 0)
            prompt.Append("Black's last move: ").Append(game.Moves[^1].Name).Append(game.Moves[^1].IsPass ? " (if you pass too, the game ends and is counted)" : string.Empty).Append('\n');
        foreach (var (colour, who) in new[] { ((sbyte)-1, "Your (White) groups in atari, to save"), ((sbyte)1, "Black groups in atari, that you can capture") })
        {
            var atari = game.InAtari(colour).ToList();
            if (atari.Count > 0)
                prompt.Append(who).Append(": ").Append(string.Join("; ", atari.Select(a =>
                    $"{string.Join(" ", a.Stones.Select(s => GoBoard.PointName(s.X, s.Y)))} (last liberty {GoBoard.PointName(a.Liberty.X, a.Liberty.Y)})"))).Append('\n');
        }
        prompt.Append("\nYour legal points (White): ").Append(string.Join(", ", legal)).Append(", or pass\n");
        if (failure is not null)
            prompt.Append("\nYour previous answer was refused (").Append(failure).Append("): choose a point from the list above.\n");
        prompt.Append("\nWhite to play.");

        var choices = new JsonArray(legal.Append("pass").Select(m => (JsonNode)m).ToArray());
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["move"] = new JsonObject { ["type"] = "string", ["enum"] = choices },
                ["comment"] = new JsonObject { ["type"] = "string" }
            },
            ["required"] = new JsonArray("move", "comment")
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ThinkTimeout);
        var json = await _ollama!.ChatJsonAsync(model, system, prompt.ToString(), schema, timeout.Token);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var text = root.TryGetProperty("move", out var move) ? move.GetString()?.Trim() : null;
        var comment = root.TryGetProperty("comment", out var said) ? said.GetString() ?? string.Empty : string.Empty;
        if (string.Equals(text, "pass", StringComparison.OrdinalIgnoreCase))
            return (GoMove.Pass, comment.Trim());
        return (board.ParsePoint(text) is { } point ? new GoMove(point.X, point.Y) : null, comment.Trim());
    }

    // ── Commands ──

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry() => _ = WhiteMoveAsync();

    private bool CanRetry() => WhiteStuck && !IsThinking;

    [RelayCommand]
    private void NewGame()
    {
        StopThinking();
        Game = new GoGame(Sizes.Contains(BoardSize) ? BoardSize : 19, Komi);
        Comment = string.Empty;
        WhiteStuck = false;
        AfterMove($"New game on {Game.Size}×{Game.Size}: you play Black");
    }

    /// <summary>Takes back the user's last move and the model's answer (or the move it is thinking about).</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        StopThinking();
        Game.Undo(Game.ToMove > 0 ? 2 : 1);
        Comment = string.Empty;
        WhiteStuck = false;
        AfterMove(Game.Moves.Count == 0 ? "Back to the start" : "Move taken back: your turn");
    }

    private bool CanUndo() => Game.Moves.Count > 0;

    [RelayCommand(CanExecute = nameof(CanResign))]
    private void Resign()
    {
        StopThinking();
        Game.Resign(1);
        AfterMove("You resigned");
    }

    private bool CanResign() => Game.Phase != GoPhase.Over;

    private void StopThinking()
    {
        _thinking?.Cancel();
        _thinking = null;
        IsThinking = false;
    }

    /// <summary>The game in SGF.</summary>
    public string Sgf() => Game.Sgf("You", Model ?? "LLM");

    public void Say(string message, bool error)
    {
        Status = message;
        IsError = error;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopThinking();
    }
}
