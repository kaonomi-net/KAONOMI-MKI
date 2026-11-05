using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
/// /game:chess: a game of chess against the LLM. The user plays White on the board; after each of their moves the model
/// chosen (the one of the chat by default) plays Black: it is given the position (FEN and a diagram), the moves so
/// far and the list of Black's legal moves, and its answer is held to that list (structured output of Ollama). The
/// rules (<see cref="ChessGame"/>) are applied to both sides: legal moves only, castling, en passant, promotion,
/// checkmate, stalemate and the draws. The moves are listed in algebraic notation; PGN copies the game.
/// </summary>
public partial class ChessWindowViewModel : ViewModelBase, IDisposable
{
    /// <summary>How long the model may think about a move, and how many times it is asked before giving up.</summary>
    public static readonly TimeSpan ThinkTimeout = TimeSpan.FromMinutes(4);

    public const int Attempts = 3;

    private readonly OllamaModelService? _ollama;
    private readonly string _language;
    private CancellationTokenSource? _thinking;
    private bool _disposed;

    public ChessWindowViewModel() : this(null, [], null, "FR")
    {
    }

    /// <param name="ollama">Where the model playing Black runs (none: Black does not play, for the tests).</param>
    /// <param name="models">The models of the chat, to choose the one playing Black.</param>
    /// <param name="model">The model chosen at first (the one of the chat).</param>
    /// <param name="language">The language of the chat (FR, EN…), for the comments of the model.</param>
    public ChessWindowViewModel(OllamaModelService? ollama, IEnumerable<string> models, string? model, string language)
    {
        _ollama = ollama;
        _language = SpeechLanguage.Name(language);
        // The list of the chat itself when given: the models it loads later show up here too.
        Models = models as ObservableCollection<string> ?? new ObservableCollection<string>(models);
        Model = model is { Length: > 0 } && Models.Contains(model) ? model : Models.FirstOrDefault() ?? model;
        Status = "You play White: click a piece, then its square (or drag it)";
    }

    /// <summary>Raised when the board changes (a move, a take-back, a new game): the board and the list are drawn again.</summary>
    public event Action? Changed;

    public ChessGame Game { get; private set; } = new();

    public ObservableCollection<string> Models { get; }

    /// <summary>The model playing Black.</summary>
    [ObservableProperty]
    private string? _model;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    /// <summary>True while the model looks for Black's move.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMove))]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand), nameof(ResignCommand))]
    private bool _isThinking;

    /// <summary>What the model said with its last move.</summary>
    [ObservableProperty]
    private string _comment = string.Empty;

    /// <summary>True when the model failed to give a move: RETRY asks it again.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    private bool _blackStuck;

    /// <summary>When the model started thinking (for the clock of the view).</summary>
    public DateTimeOffset ThinkingSince { get; private set; }

    /// <summary>True when the user may move a piece: White to move, the game going on, the model not thinking.</summary>
    public bool CanMove => !IsThinking && !Game.IsOver && Game.Current.WhiteToMove;

    /// <summary>The last move played (null at the start), shown on the board.</summary>
    public ChessMove? LastMove => Game.Moves.Count > 0 ? Game.Moves[^1] : null;

    /// <summary>The moves by pairs: its number, White's move, Black's move (empty when not played yet).</summary>
    public IReadOnlyList<(int Number, string White, string Black)> Rows
    {
        get
        {
            var rows = new List<(int, string, string)>();
            for (var k = 0; k < Game.San.Count; k += 2)
                rows.Add((k / 2 + 1, Game.San[k], k + 1 < Game.San.Count ? Game.San[k + 1] : string.Empty));
            return rows;
        }
    }

    /// <summary>The legal moves of the piece on <paramref name="square"/> (none when the user may not move it).</summary>
    public IReadOnlyList<ChessMove> MovesFrom(int square) =>
        CanMove ? Game.Current.LegalMoves().Where(m => m.From == square).ToList() : [];

    /// <summary>True when the move from <paramref name="from"/> to <paramref name="to"/> is a promotion (the view asks for the piece).</summary>
    public bool IsPromotion(int from, int to) => MovesFrom(from).Any(m => m.To == to && m.Promotion != ChessPiece.None);

    /// <summary>Plays the user's move if it is legal, then lets the model answer.</summary>
    public bool TryPlay(int from, int to, ChessPiece promotion = ChessPiece.None)
    {
        if (!CanMove)
            return false;
        var move = MovesFrom(from).FirstOrDefault(m => m.To == to && m.Promotion == promotion);
        if (move == default || Game.Play(move) is not { } san)
        {
            Say("This move is not allowed", true);
            return false;
        }
        Comment = string.Empty;
        AfterMove($"You played {san}");
        if (!Game.IsOver)
            _ = BlackMoveAsync();
        return true;
    }

    private void AfterMove(string what)
    {
        OnPropertyChanged(nameof(Rows));
        OnPropertyChanged(nameof(LastMove));
        OnPropertyChanged(nameof(CanMove));
        UndoCommand.NotifyCanExecuteChanged();
        ResignCommand.NotifyCanExecuteChanged();
        Changed?.Invoke();
        if (Game.IsOver)
            Say($"{Game.Reason} ({Game.Result})", false);
        else if (Game.Current.InCheck)
            Say($"{what}: check!", false);
        else
            Say(what, false);
    }

    // ── Black: the model ──

    /// <summary>Asks the model for Black's move (up to <see cref="Attempts"/> times) and plays it.</summary>
    private async Task BlackMoveAsync()
    {
        if (_ollama is null || Game.IsOver || Game.Current.WhiteToMove)
            return;
        if (string.IsNullOrWhiteSpace(Model))
        {
            BlackStuck = true;
            Say("No model to play Black: choose one (Ollama started?), then RETRY", true);
            return;
        }
        _thinking?.Cancel();
        var cancel = new CancellationTokenSource();
        _thinking = cancel;
        var game = Game;
        var model = Model;
        BlackStuck = false;
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
                    var (move, comment) = await AskAsync(game.Current, game, model, failure, cancel.Token);
                    if (cancel.IsCancellationRequested || !ReferenceEquals(game, Game))
                        return;
                    if (move is { } legal && Game.Play(legal) is { } san)
                    {
                        Comment = comment;
                        IsThinking = false;
                        AfterMove($"{model} played {san}");
                        return;
                    }
                    failure = "your answer was not one of the legal moves";
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException or InvalidOperationException)
                {
                    if (cancel.IsCancellationRequested)
                        return;
                    failure = ex is OperationCanceledException ? $"no answer within {ThinkTimeout.TotalMinutes:0} minutes" : ex.Message;
                    ActivityLog.Error("Chess", $"{model} gave no move (attempt {attempt})", ex);
                    if (ex is HttpRequestException { StatusCode: null })
                        break;
                }
                if (attempt < Attempts)
                    Say($"{model} is thinking again ({failure})…", false);
            }
            IsThinking = false;
            BlackStuck = true;
            Say($"{model} gave no legal move ({failure}): RETRY, or choose another model", true);
        }
        finally
        {
            if (ReferenceEquals(_thinking, cancel))
            {
                _thinking = null;
                if (!BlackStuck && !cancel.IsCancellationRequested)
                    IsThinking = false;
            }
            cancel.Dispose();
        }
    }

    private async Task<(ChessMove? Move, string Comment)> AskAsync(ChessPosition position, ChessGame game, string model, string? failure, CancellationToken cancellationToken)
    {
        var legal = position.LegalMoves().Select(position.ToSan).ToList();
        var system =
            "You are a strong chess player. You play Black against a human who plays White. "
            + "Look at the position: the threats against your pieces, the checks, the captures, the pieces left undefended, the mates. "
            + "Then choose the best move for Black. "
            + "Answer with a JSON object: {\"move\": your move, copied exactly from the list of legal moves (standard algebraic notation), "
            + $"\"comment\": one short sentence to your opponent about your move, in {_language}}}.";
        var prompt = new StringBuilder();
        prompt.Append("Position (FEN): ").Append(position.ToFen()).Append("\n\n");
        prompt.Append("Board (White in capitals, Black in lower case, rank 8 at the top):\n").Append(position.Diagram()).Append("\n\n");
        prompt.Append("Moves so far: ").Append(game.San.Count == 0 ? "none" : game.MoveText()).Append('\n');
        if (game.San.Count > 0)
            prompt.Append("White's last move: ").Append(game.San[^1]).Append(position.InCheck ? " (you are in check)" : string.Empty).Append('\n');
        prompt.Append("\nYour legal moves (Black): ").Append(string.Join(", ", legal)).Append('\n');
        if (failure is not null)
            prompt.Append("\nYour previous answer was refused (").Append(failure).Append("): choose a move from the list above.\n");
        prompt.Append("\nBlack to move.");

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["move"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(legal.Select(m => (JsonNode)m).ToArray()) },
                ["comment"] = new JsonObject { ["type"] = "string" }
            },
            ["required"] = new JsonArray("move", "comment")
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ThinkTimeout);
        var json = await _ollama!.ChatJsonAsync(model, system, prompt.ToString(), schema, timeout.Token);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var text = root.TryGetProperty("move", out var move) ? move.GetString() : null;
        var comment = root.TryGetProperty("comment", out var said) ? said.GetString() ?? string.Empty : string.Empty;
        return (position.ParseMove(text), comment.Trim());
    }

    // ── Commands ──

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry() => _ = BlackMoveAsync();

    private bool CanRetry() => BlackStuck && !IsThinking;

    [RelayCommand]
    private void NewGame()
    {
        StopThinking();
        Game = new ChessGame();
        Comment = string.Empty;
        BlackStuck = false;
        AfterMove("New game: you play White");
    }

    /// <summary>Takes back the user's last move and the model's answer (or the move it is thinking about).</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        StopThinking();
        // Back to a position where White is to move.
        Game.Undo(Game.Current.WhiteToMove ? 2 : 1);
        Comment = string.Empty;
        BlackStuck = false;
        AfterMove(Game.San.Count == 0 ? "Back to the start" : "Move taken back: your turn");
    }

    private bool CanUndo() => Game.Moves.Count > 0;

    [RelayCommand(CanExecute = nameof(CanResign))]
    private void Resign()
    {
        StopThinking();
        Game.Resign(white: true);
        AfterMove("You resigned");
    }

    private bool CanResign() => !Game.IsOver;

    private void StopThinking()
    {
        _thinking?.Cancel();
        _thinking = null;
        IsThinking = false;
    }

    /// <summary>The game in PGN.</summary>
    public string Pgn() => Game.Pgn("You", Model ?? "LLM");

    /// <summary>The pieces taken from <paramref name="white"/>'s side, the most valuable first.</summary>
    public IReadOnlyList<ChessPiece> Taken(bool white)
    {
        var start = new Dictionary<ChessPiece, int> { [ChessPiece.Queen] = 1, [ChessPiece.Rook] = 2, [ChessPiece.Bishop] = 2, [ChessPiece.Knight] = 2, [ChessPiece.Pawn] = 8 };
        var sign = white ? 1 : -1;
        var now = new Dictionary<ChessPiece, int>();
        for (var square = 0; square < 64; square++)
        {
            var piece = Game.Current[square];
            if (piece != 0 && Math.Sign(piece) == sign)
                now[ChessPosition.Kind(piece)] = now.GetValueOrDefault(ChessPosition.Kind(piece)) + 1;
        }
        // A promoted pawn is a pawn missing and a piece too many: the extra pieces are taken off the pawns.
        var extra = start.Keys.Where(k => k != ChessPiece.Pawn).Sum(k => Math.Max(0, now.GetValueOrDefault(k) - start[k]));
        var taken = new List<ChessPiece>();
        foreach (var (kind, count) in start)
        {
            var missing = count - now.GetValueOrDefault(kind);
            if (kind == ChessPiece.Pawn)
                missing -= extra;
            for (var k = 0; k < missing; k++)
                taken.Add(kind);
        }
        return taken;
    }

    /// <summary>White's material minus Black's (pawn 1, knight and bishop 3, rook 5, queen 9).</summary>
    public int Balance()
    {
        var total = 0;
        for (var square = 0; square < 64; square++)
        {
            var piece = Game.Current[square];
            total += Math.Sign(piece) * ChessPosition.Kind(piece) switch
            {
                ChessPiece.Pawn => 1,
                ChessPiece.Knight or ChessPiece.Bishop => 3,
                ChessPiece.Rook => 5,
                ChessPiece.Queen => 9,
                _ => 0
            };
        }
        return total;
    }

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
