using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace KAONOMI.Chat.Services;

/// <summary>The kinds of pieces; on the board a white piece is positive, a black one negative.</summary>
public enum ChessPiece : sbyte
{
    None = 0,
    Pawn = 1,
    Knight = 2,
    Bishop = 3,
    Rook = 4,
    Queen = 5,
    King = 6
}

/// <summary>A move from a square to another (0 = a1, 7 = h1, 63 = h8), with the piece a pawn becomes on the last rank.</summary>
public readonly record struct ChessMove(int From, int To, ChessPiece Promotion = ChessPiece.None)
{
    /// <summary>The move in the UCI notation: e2e4, e7e8q.</summary>
    public string Uci => ChessPosition.SquareName(From) + ChessPosition.SquareName(To) + Promotion switch
    {
        ChessPiece.Queen => "q",
        ChessPiece.Rook => "r",
        ChessPiece.Bishop => "b",
        ChessPiece.Knight => "n",
        _ => string.Empty
    };

    public override string ToString() => Uci;
}

/// <summary>
/// A position of chess and its rules (FIDE): the moves of the pieces, castling (the king and the rook unmoved, the
/// squares between empty, the king neither in check nor crossing or landing on an attacked square), the capture en
/// passant, the promotion, check; a move is legal when it does not leave its own king in check. Immutable:
/// <see cref="Play"/> gives a new position. Written and read in the FEN notation.
/// </summary>
public sealed class ChessPosition
{
    public const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    /// <summary>The castling rights.</summary>
    public const int WhiteKingSide = 1, WhiteQueenSide = 2, BlackKingSide = 4, BlackQueenSide = 8;

    private static readonly (int Df, int Dr)[] KnightSteps = [(1, 2), (2, 1), (2, -1), (1, -2), (-1, -2), (-2, -1), (-2, 1), (-1, 2)];
    private static readonly (int Df, int Dr)[] KingSteps = [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)];
    private static readonly (int Df, int Dr)[] RookRays = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    private static readonly (int Df, int Dr)[] BishopRays = [(1, 1), (1, -1), (-1, 1), (-1, -1)];
    private static readonly ChessPiece[] Promotions = [ChessPiece.Queen, ChessPiece.Rook, ChessPiece.Bishop, ChessPiece.Knight];

    private readonly sbyte[] _board = new sbyte[64];
    private List<ChessMove>? _legal;

    private ChessPosition()
    {
    }

    public static ChessPosition Start { get; } = FromFen(StartFen);

    /// <summary>True when White is to move.</summary>
    public bool WhiteToMove { get; private set; }

    /// <summary>The castling rights still kept (<see cref="WhiteKingSide"/>…).</summary>
    public int Castling { get; private set; }

    /// <summary>The square a pawn may capture en passant on (the one a pawn just crossed by two steps), or -1.</summary>
    public int EnPassant { get; private set; } = -1;

    /// <summary>The half-moves since the last capture or pawn move (the fifty-move rule).</summary>
    public int HalfmoveClock { get; private set; }

    /// <summary>The number of the move, starting at 1 and growing after each move of Black.</summary>
    public int FullmoveNumber { get; private set; } = 1;

    /// <summary>The piece on <paramref name="square"/>: positive for White, negative for Black, 0 when empty.</summary>
    public sbyte this[int square] => _board[square];

    public static int File(int square) => square & 7;

    public static int Rank(int square) => square >> 3;

    public static int SquareOf(int file, int rank) => rank * 8 + file;

    public static string SquareName(int square) => $"{(char)('a' + File(square))}{(char)('1' + Rank(square))}";

    /// <summary>The square named <paramref name="name"/> (e4), or -1.</summary>
    public static int ParseSquare(string name) =>
        name.Length == 2 && name[0] is >= 'a' and <= 'h' && name[1] is >= '1' and <= '8' ? SquareOf(name[0] - 'a', name[1] - '1') : -1;

    public static ChessPiece Kind(sbyte piece) => (ChessPiece)Math.Abs(piece);

    // ── FEN ──

    /// <summary>Reads a position in the FEN notation (throws <see cref="FormatException"/> when it is not one).</summary>
    public static ChessPosition FromFen(string fen)
    {
        var parts = fen.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new FormatException("A FEN has at least the board and the side to move");
        var position = new ChessPosition();
        var rows = parts[0].Split('/');
        if (rows.Length != 8)
            throw new FormatException("The board of a FEN has 8 rows");
        for (var row = 0; row < 8; row++)
        {
            var rank = 7 - row;
            var file = 0;
            foreach (var ch in rows[row])
            {
                if (char.IsDigit(ch))
                {
                    file += ch - '0';
                    continue;
                }
                if (file > 7)
                    throw new FormatException($"Row {row + 1} of the FEN is too long");
                var kind = char.ToLowerInvariant(ch) switch
                {
                    'p' => ChessPiece.Pawn,
                    'n' => ChessPiece.Knight,
                    'b' => ChessPiece.Bishop,
                    'r' => ChessPiece.Rook,
                    'q' => ChessPiece.Queen,
                    'k' => ChessPiece.King,
                    _ => throw new FormatException($"'{ch}' is not a piece")
                };
                position._board[SquareOf(file, rank)] = (sbyte)(char.IsUpper(ch) ? (int)kind : -(int)kind);
                file++;
            }
            if (file != 8)
                throw new FormatException($"Row {row + 1} of the FEN does not have 8 squares");
        }
        position.WhiteToMove = parts[1] == "w";
        var castling = parts.Length > 2 ? parts[2] : "-";
        position.Castling = (castling.Contains('K') ? WhiteKingSide : 0) | (castling.Contains('Q') ? WhiteQueenSide : 0)
            | (castling.Contains('k') ? BlackKingSide : 0) | (castling.Contains('q') ? BlackQueenSide : 0);
        position.EnPassant = parts.Length > 3 && parts[3] != "-" ? ParseSquare(parts[3]) : -1;
        position.HalfmoveClock = parts.Length > 4 && int.TryParse(parts[4], out var half) ? half : 0;
        position.FullmoveNumber = parts.Length > 5 && int.TryParse(parts[5], out var full) ? Math.Max(full, 1) : 1;
        if (position.KingSquare(true) < 0 || position.KingSquare(false) < 0)
            throw new FormatException("Each side needs its king");
        return position;
    }

    public string ToFen() => $"{Placement()} {(WhiteToMove ? 'w' : 'b')} {CastlingText()} {(EnPassant >= 0 ? SquareName(EnPassant) : "-")} {HalfmoveClock} {FullmoveNumber}";

    /// <summary>
    /// What makes two positions the same for the repetitions: the pieces, the side to move, the castling rights and a
    /// capture en passant that can really be played.
    /// </summary>
    public string Key
    {
        get
        {
            var ep = EnPassant >= 0 && LegalMoves().Any(m => m.To == EnPassant && Kind(_board[m.From]) == ChessPiece.Pawn) ? SquareName(EnPassant) : "-";
            return $"{Placement()} {(WhiteToMove ? 'w' : 'b')} {CastlingText()} {ep}";
        }
    }

    private string Placement()
    {
        var text = new StringBuilder();
        for (var rank = 7; rank >= 0; rank--)
        {
            var empty = 0;
            for (var file = 0; file < 8; file++)
            {
                var piece = _board[SquareOf(file, rank)];
                if (piece == 0)
                {
                    empty++;
                    continue;
                }
                if (empty > 0)
                    text.Append(empty);
                empty = 0;
                text.Append(Letter(piece));
            }
            if (empty > 0)
                text.Append(empty);
            if (rank > 0)
                text.Append('/');
        }
        return text.ToString();
    }

    private string CastlingText()
    {
        var text = (Castling & WhiteKingSide) != 0 ? "K" : string.Empty;
        text += (Castling & WhiteQueenSide) != 0 ? "Q" : string.Empty;
        text += (Castling & BlackKingSide) != 0 ? "k" : string.Empty;
        text += (Castling & BlackQueenSide) != 0 ? "q" : string.Empty;
        return text.Length == 0 ? "-" : text;
    }

    /// <summary>The FEN letter of a piece: P N B R Q K for White, lower case for Black.</summary>
    public static char Letter(sbyte piece)
    {
        var letter = Kind(piece) switch
        {
            ChessPiece.Pawn => 'p',
            ChessPiece.Knight => 'n',
            ChessPiece.Bishop => 'b',
            ChessPiece.Rook => 'r',
            ChessPiece.Queen => 'q',
            ChessPiece.King => 'k',
            _ => '.'
        };
        return piece > 0 ? char.ToUpperInvariant(letter) : letter;
    }

    // ── Attacks ──

    public int KingSquare(bool white)
    {
        var king = (sbyte)(white ? (int)ChessPiece.King : -(int)ChessPiece.King);
        return Array.IndexOf(_board, king);
    }

    /// <summary>True when the side to move has its king attacked.</summary>
    public bool InCheck => IsAttacked(KingSquare(WhiteToMove), !WhiteToMove);

    /// <summary>True when a piece of <paramref name="byWhite"/>'s side attacks <paramref name="square"/>.</summary>
    public bool IsAttacked(int square, bool byWhite)
    {
        if (square < 0)
            return false;
        var sign = byWhite ? 1 : -1;
        int f = File(square), r = Rank(square);
        // A pawn attacks forward diagonally: a white one from the rank below.
        var pawnRank = r - sign;
        foreach (var df in new[] { -1, 1 })
            if (At(f + df, pawnRank) == sign * (int)ChessPiece.Pawn)
                return true;
        foreach (var (df, dr) in KnightSteps)
            if (At(f + df, r + dr) == sign * (int)ChessPiece.Knight)
                return true;
        foreach (var (df, dr) in KingSteps)
            if (At(f + df, r + dr) == sign * (int)ChessPiece.King)
                return true;
        if (RayHits(f, r, RookRays, sign, ChessPiece.Rook) || RayHits(f, r, BishopRays, sign, ChessPiece.Bishop))
            return true;
        return false;
    }

    private bool RayHits(int f, int r, (int Df, int Dr)[] rays, int sign, ChessPiece slider)
    {
        foreach (var (df, dr) in rays)
        {
            for (int nf = f + df, nr = r + dr; nf is >= 0 and < 8 && nr is >= 0 and < 8; nf += df, nr += dr)
            {
                var piece = _board[SquareOf(nf, nr)];
                if (piece == 0)
                    continue;
                if (piece == sign * (int)slider || piece == sign * (int)ChessPiece.Queen)
                    return true;
                break;
            }
        }
        return false;
    }

    /// <summary>The piece at (file, rank), or <see cref="sbyte.MinValue"/> off the board.</summary>
    private int At(int file, int rank) => file is >= 0 and < 8 && rank is >= 0 and < 8 ? _board[SquareOf(file, rank)] : sbyte.MinValue;

    // ── Moves ──

    /// <summary>The legal moves of the side to move (kept once computed).</summary>
    public IReadOnlyList<ChessMove> LegalMoves()
    {
        if (_legal is not null)
            return _legal;
        var legal = new List<ChessMove>();
        foreach (var move in PseudoLegalMoves())
        {
            var next = Play(move);
            if (!next.IsAttacked(next.KingSquare(WhiteToMove), next.WhiteToMove))
                legal.Add(move);
        }
        _legal = legal;
        return legal;
    }

    /// <summary>The moves of the pieces, castling and en passant included, before checking that the king stays safe.</summary>
    private IEnumerable<ChessMove> PseudoLegalMoves()
    {
        var sign = WhiteToMove ? 1 : -1;
        for (var from = 0; from < 64; from++)
        {
            var piece = _board[from];
            if (piece == 0 || Math.Sign(piece) != sign)
                continue;
            int f = File(from), r = Rank(from);
            switch (Kind(piece))
            {
                case ChessPiece.Pawn:
                {
                    var lastRank = WhiteToMove ? 7 : 0;
                    var startRank = WhiteToMove ? 1 : 6;
                    var ahead = r + sign;
                    if (At(f, ahead) == 0)
                    {
                        foreach (var m in PawnMoves(from, SquareOf(f, ahead), ahead == lastRank))
                            yield return m;
                        if (r == startRank && At(f, ahead + sign) == 0)
                            yield return new ChessMove(from, SquareOf(f, ahead + sign));
                    }
                    foreach (var df in new[] { -1, 1 })
                    {
                        var target = At(f + df, ahead);
                        if (target == sbyte.MinValue)
                            continue;
                        var to = SquareOf(f + df, ahead);
                        if ((target != 0 && Math.Sign(target) == -sign) || to == EnPassant)
                            foreach (var m in PawnMoves(from, to, ahead == lastRank))
                                yield return m;
                    }
                    break;
                }
                case ChessPiece.Knight:
                    foreach (var m in Steps(from, KnightSteps, sign))
                        yield return m;
                    break;
                case ChessPiece.Bishop:
                    foreach (var m in Slides(from, BishopRays, sign))
                        yield return m;
                    break;
                case ChessPiece.Rook:
                    foreach (var m in Slides(from, RookRays, sign))
                        yield return m;
                    break;
                case ChessPiece.Queen:
                    foreach (var m in Slides(from, RookRays, sign).Concat(Slides(from, BishopRays, sign)))
                        yield return m;
                    break;
                case ChessPiece.King:
                    foreach (var m in Steps(from, KingSteps, sign))
                        yield return m;
                    foreach (var m in CastlingMoves(from))
                        yield return m;
                    break;
            }
        }
    }

    private static IEnumerable<ChessMove> PawnMoves(int from, int to, bool promotes)
    {
        if (!promotes)
        {
            yield return new ChessMove(from, to);
            yield break;
        }
        foreach (var piece in Promotions)
            yield return new ChessMove(from, to, piece);
    }

    private IEnumerable<ChessMove> Steps(int from, (int Df, int Dr)[] steps, int sign)
    {
        foreach (var (df, dr) in steps)
        {
            var target = At(File(from) + df, Rank(from) + dr);
            if (target == sbyte.MinValue || (target != 0 && Math.Sign(target) == sign))
                continue;
            yield return new ChessMove(from, SquareOf(File(from) + df, Rank(from) + dr));
        }
    }

    private IEnumerable<ChessMove> Slides(int from, (int Df, int Dr)[] rays, int sign)
    {
        foreach (var (df, dr) in rays)
        {
            for (int nf = File(from) + df, nr = Rank(from) + dr; nf is >= 0 and < 8 && nr is >= 0 and < 8; nf += df, nr += dr)
            {
                var target = _board[SquareOf(nf, nr)];
                if (target != 0 && Math.Sign(target) == sign)
                    break;
                yield return new ChessMove(from, SquareOf(nf, nr));
                if (target != 0)
                    break;
            }
        }
    }

    private IEnumerable<ChessMove> CastlingMoves(int from)
    {
        var white = WhiteToMove;
        var home = white ? 4 : 60;
        if (from != home || IsAttacked(home, !white))
            yield break;
        var rook = (sbyte)(white ? (int)ChessPiece.Rook : -(int)ChessPiece.Rook);
        var kingSide = white ? WhiteKingSide : BlackKingSide;
        var queenSide = white ? WhiteQueenSide : BlackQueenSide;
        if ((Castling & kingSide) != 0 && _board[home + 3] == rook && _board[home + 1] == 0 && _board[home + 2] == 0
            && !IsAttacked(home + 1, !white) && !IsAttacked(home + 2, !white))
            yield return new ChessMove(home, home + 2);
        if ((Castling & queenSide) != 0 && _board[home - 4] == rook && _board[home - 1] == 0 && _board[home - 2] == 0 && _board[home - 3] == 0
            && !IsAttacked(home - 1, !white) && !IsAttacked(home - 2, !white))
            yield return new ChessMove(home, home - 2);
    }

    /// <summary>The position after <paramref name="move"/> (a move of <see cref="LegalMoves"/>; nothing is checked here).</summary>
    public ChessPosition Play(ChessMove move)
    {
        var next = new ChessPosition
        {
            WhiteToMove = !WhiteToMove,
            Castling = Castling,
            EnPassant = -1,
            HalfmoveClock = HalfmoveClock + 1,
            FullmoveNumber = FullmoveNumber + (WhiteToMove ? 0 : 1)
        };
        Array.Copy(_board, next._board, 64);
        var b = next._board;
        var piece = b[move.From];
        var sign = Math.Sign(piece);
        var kind = Kind(piece);
        var captured = b[move.To];
        if (kind == ChessPiece.Pawn && move.To == EnPassant && captured == 0)
            b[move.To - 8 * sign] = 0;
        b[move.To] = move.Promotion != ChessPiece.None ? (sbyte)(sign * (int)move.Promotion) : piece;
        b[move.From] = 0;
        if (kind == ChessPiece.King && Math.Abs(move.To - move.From) == 2)
        {
            // Castling: the rook jumps over the king.
            var (rookFrom, rookTo) = move.To > move.From ? (move.From + 3, move.From + 1) : (move.From - 4, move.From - 1);
            b[rookTo] = b[rookFrom];
            b[rookFrom] = 0;
        }
        if (kind == ChessPiece.King)
            next.Castling &= sign > 0 ? ~(WhiteKingSide | WhiteQueenSide) : ~(BlackKingSide | BlackQueenSide);
        foreach (var square in new[] { move.From, move.To })
        {
            next.Castling &= square switch
            {
                0 => ~WhiteQueenSide,
                7 => ~WhiteKingSide,
                56 => ~BlackQueenSide,
                63 => ~BlackKingSide,
                _ => ~0
            };
        }
        if (kind == ChessPiece.Pawn && Math.Abs(move.To - move.From) == 16)
            next.EnPassant = (move.From + move.To) / 2;
        if (kind == ChessPiece.Pawn || captured != 0)
            next.HalfmoveClock = 0;
        return next;
    }

    /// <summary>True when <paramref name="move"/> takes a piece (en passant included).</summary>
    public bool IsCapture(ChessMove move) =>
        _board[move.To] != 0 || (Kind(_board[move.From]) == ChessPiece.Pawn && move.To == EnPassant);

    // ── Notation ──

    /// <summary>The move in the standard algebraic notation: e4, Nf3, exd5, O-O, e8=Q+, Qxf7#.</summary>
    public string ToSan(ChessMove move)
    {
        var piece = _board[move.From];
        var kind = Kind(piece);
        string san;
        if (kind == ChessPiece.King && Math.Abs(move.To - move.From) == 2)
            san = move.To > move.From ? "O-O" : "O-O-O";
        else
        {
            var text = new StringBuilder();
            var capture = IsCapture(move);
            if (kind == ChessPiece.Pawn)
            {
                if (capture)
                    text.Append((char)('a' + File(move.From)));
            }
            else
            {
                text.Append(char.ToUpperInvariant(Letter(piece)));
                var rivals = LegalMoves().Where(m => m.To == move.To && m.From != move.From && _board[m.From] == piece).ToList();
                if (rivals.Count > 0)
                {
                    if (rivals.All(m => File(m.From) != File(move.From)))
                        text.Append((char)('a' + File(move.From)));
                    else if (rivals.All(m => Rank(m.From) != Rank(move.From)))
                        text.Append((char)('1' + Rank(move.From)));
                    else
                        text.Append(SquareName(move.From));
                }
            }
            if (capture)
                text.Append('x');
            text.Append(SquareName(move.To));
            if (move.Promotion != ChessPiece.None)
                text.Append('=').Append(char.ToUpperInvariant(Letter((sbyte)move.Promotion)));
            san = text.ToString();
        }
        var next = Play(move);
        if (next.InCheck)
            san += next.LegalMoves().Count == 0 ? "#" : "+";
        return san;
    }

    /// <summary>
    /// The legal move written <paramref name="text"/>, in the algebraic notation (Nf3, exd5, O-O, 0-0, e8=Q, e8Q, with
    /// or without + # ! ?) or in UCI (g1f3, e7e8q); null when it is none of them.
    /// </summary>
    public ChessMove? ParseMove(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var cleaned = text.Trim().TrimEnd('+', '#', '!', '?').Replace("0-0-0", "O-O-O").Replace("0-0", "O-O").Replace("e.p.", string.Empty).Trim();
        var legal = LegalMoves();
        if (cleaned.Length is 4 or 5 && ParseSquare(cleaned[..2]) is var from and >= 0 && ParseSquare(cleaned[2..4]) is var to and >= 0)
        {
            var promotion = cleaned.Length == 5 ? char.ToLowerInvariant(cleaned[4]) switch
            {
                'q' => ChessPiece.Queen,
                'r' => ChessPiece.Rook,
                'b' => ChessPiece.Bishop,
                'n' => ChessPiece.Knight,
                _ => ChessPiece.None
            } : ChessPiece.None;
            foreach (var m in legal)
                if (m.From == from && m.To == to && m.Promotion == promotion)
                    return m;
        }
        static string Bare(string san) => san.TrimEnd('+', '#').Replace("=", string.Empty);
        var wanted = Bare(cleaned);
        foreach (var m in legal)
            if (Bare(ToSan(m)) == wanted)
                return m;
        return null;
    }

    /// <summary>True when neither side has enough pieces left to mate (K-K, K+B-K, K+N-K, K+B-K+B on squares of one colour).</summary>
    public bool InsufficientMaterial()
    {
        var others = new List<(sbyte Piece, int Square)>();
        for (var square = 0; square < 64; square++)
        {
            var piece = _board[square];
            if (piece == 0 || Kind(piece) == ChessPiece.King)
                continue;
            if (Kind(piece) is ChessPiece.Pawn or ChessPiece.Rook or ChessPiece.Queen)
                return false;
            others.Add((piece, square));
        }
        if (others.Count <= 1)
            return true;
        // Only bishops, all on squares of the same colour.
        return others.All(o => Kind(o.Piece) == ChessPiece.Bishop)
            && others.Select(o => (File(o.Square) + Rank(o.Square)) % 2).Distinct().Count() == 1;
    }

    /// <summary>The board as text, rank 8 at the top, for the prompt of the LLM.</summary>
    public string Diagram()
    {
        var text = new StringBuilder();
        for (var rank = 7; rank >= 0; rank--)
        {
            text.Append(rank + 1).Append(' ');
            for (var file = 0; file < 8; file++)
                text.Append(Letter(_board[SquareOf(file, rank)])).Append(file < 7 ? " " : string.Empty);
            text.Append('\n');
        }
        text.Append("  a b c d e f g h");
        return text.ToString();
    }

    /// <summary>The number of positions after <paramref name="depth"/> half-moves (to check the move generator).</summary>
    public long Perft(int depth)
    {
        if (depth == 0)
            return 1;
        var moves = LegalMoves();
        if (depth == 1)
            return moves.Count;
        long total = 0;
        foreach (var move in moves)
            total += Play(move).Perft(depth - 1);
        return total;
    }
}

/// <summary>How a game of chess ended.</summary>
public enum ChessOutcome
{
    Playing,
    WhiteWins,
    BlackWins,
    Draw
}

/// <summary>
/// A game of chess: the positions one after the other, the moves in algebraic notation, and its end (checkmate,
/// stalemate, the fifty-move rule, a threefold repetition, too little material, a resignation).
/// </summary>
public sealed class ChessGame
{
    private readonly List<ChessPosition> _positions = [ChessPosition.Start];
    private readonly List<ChessMove> _moves = [];
    private readonly List<string> _san = [];

    public ChessPosition Current => _positions[^1];

    public IReadOnlyList<ChessMove> Moves => _moves;

    /// <summary>The moves in the standard algebraic notation (SAN).</summary>
    public IReadOnlyList<string> San => _san;

    public ChessOutcome Outcome { get; private set; }

    /// <summary>Why the game ended (empty while it goes on).</summary>
    public string Reason { get; private set; } = string.Empty;

    public bool IsOver => Outcome != ChessOutcome.Playing;

    /// <summary>The result as PGN writes it: 1-0, 0-1, 1/2-1/2 or *.</summary>
    public string Result => Outcome switch
    {
        ChessOutcome.WhiteWins => "1-0",
        ChessOutcome.BlackWins => "0-1",
        ChessOutcome.Draw => "1/2-1/2",
        _ => "*"
    };

    /// <summary>Plays <paramref name="move"/> if it is legal; returns its notation, or null.</summary>
    public string? Play(ChessMove move)
    {
        if (IsOver || !Current.LegalMoves().Contains(move))
            return null;
        var san = Current.ToSan(move);
        _positions.Add(Current.Play(move));
        _moves.Add(move);
        _san.Add(san);
        Judge();
        return san;
    }

    /// <summary>Takes back the last <paramref name="count"/> half-moves (the end of the game with them).</summary>
    public void Undo(int count)
    {
        for (var k = 0; k < count && _moves.Count > 0; k++)
        {
            _positions.RemoveAt(_positions.Count - 1);
            _moves.RemoveAt(_moves.Count - 1);
            _san.RemoveAt(_san.Count - 1);
        }
        Outcome = ChessOutcome.Playing;
        Reason = string.Empty;
        Judge();
    }

    public void Resign(bool white)
    {
        if (IsOver)
            return;
        Outcome = white ? ChessOutcome.BlackWins : ChessOutcome.WhiteWins;
        Reason = white ? "White resigned" : "Black resigned";
    }

    /// <summary>How many times the current position has been seen.</summary>
    public int Repetitions
    {
        get
        {
            var key = Current.Key;
            return _positions.Count(p => p.Key == key);
        }
    }

    private void Judge()
    {
        var position = Current;
        if (position.LegalMoves().Count == 0)
        {
            if (position.InCheck)
            {
                Outcome = position.WhiteToMove ? ChessOutcome.BlackWins : ChessOutcome.WhiteWins;
                Reason = position.WhiteToMove ? "Checkmate: Black wins" : "Checkmate: White wins";
            }
            else
            {
                Outcome = ChessOutcome.Draw;
                Reason = "Stalemate: draw";
            }
        }
        else if (position.InsufficientMaterial())
        {
            Outcome = ChessOutcome.Draw;
            Reason = "Draw: neither side can mate";
        }
        else if (position.HalfmoveClock >= 100)
        {
            Outcome = ChessOutcome.Draw;
            Reason = "Draw: fifty moves without a capture or a pawn move";
        }
        else if (Repetitions >= 3)
        {
            Outcome = ChessOutcome.Draw;
            Reason = "Draw: the same position three times";
        }
    }

    /// <summary>The moves numbered (1. e4 e5 2. Nf3), as in PGN.</summary>
    public string MoveText()
    {
        var text = new StringBuilder();
        for (var k = 0; k < _san.Count; k++)
        {
            if (k % 2 == 0)
                text.Append(k / 2 + 1).Append(". ");
            text.Append(_san[k]).Append(' ');
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>The game in PGN, with the names of the players.</summary>
    public string Pgn(string white, string black)
    {
        var date = DateTime.Now.ToString("yyyy.MM.dd", CultureInfo.InvariantCulture);
        var text = new StringBuilder();
        text.Append("[Event \"Kaonomi chess\"]\n[Site \"KAONOMI\"]\n");
        text.Append($"[Date \"{date}\"]\n[Round \"-\"]\n[White \"{white}\"]\n[Black \"{black}\"]\n[Result \"{Result}\"]\n");
        if (Reason.Length > 0)
            text.Append($"[Termination \"{Reason}\"]\n");
        text.Append('\n').Append(MoveText());
        text.Append(_san.Count > 0 ? " " : string.Empty).Append(Result).Append('\n');
        return text.ToString();
    }
}
