using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace KAONOMI.Chat.Services;

/// <summary>A move of go: a point of the board (x from the left, y from the bottom), or a pass.</summary>
public readonly record struct GoMove(int X, int Y, bool IsPass = false)
{
    public static GoMove Pass { get; } = new(-1, -1, true);

    /// <summary>The move as players write it: D4 (no I among the columns), or pass.</summary>
    public string Name => IsPass ? "pass" : GoBoard.PointName(X, Y);

    public override string ToString() => Name;
}

/// <summary>
/// A position of go: the stones on a square board (9, 13 or 19), +1 Black, -1 White, 0 empty, and the stones each
/// side has taken. Immutable: <see cref="Place"/> gives a new board.
/// </summary>
public sealed class GoBoard
{
    /// <summary>The letters of the columns, I left out as on every board.</summary>
    public const string Columns = "ABCDEFGHJKLMNOPQRST";

    private readonly sbyte[] _points;

    public GoBoard(int size)
    {
        if (size is < 5 or > 19)
            throw new ArgumentOutOfRangeException(nameof(size), "A board of go is 5 to 19 lines");
        Size = size;
        _points = new sbyte[size * size];
    }

    private GoBoard(GoBoard other)
    {
        Size = other.Size;
        _points = (sbyte[])other._points.Clone();
        TakenByBlack = other.TakenByBlack;
        TakenByWhite = other.TakenByWhite;
    }

    public int Size { get; }

    /// <summary>The white stones Black has taken, and the black stones White has taken.</summary>
    public int TakenByBlack { get; private set; }

    public int TakenByWhite { get; private set; }

    /// <summary>The stone at (x, y): 1 Black, -1 White, 0 empty.</summary>
    public sbyte this[int x, int y] => _points[y * Size + x];

    public bool OnBoard(int x, int y) => x >= 0 && y >= 0 && x < Size && y < Size;

    public static string PointName(int x, int y) => $"{Columns[x]}{y + 1}";

    /// <summary>The point written <paramref name="name"/> (D4, d4), or null.</summary>
    public (int X, int Y)? ParsePoint(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length < 2)
            return null;
        var text = name.Trim().ToUpperInvariant();
        var x = Columns.IndexOf(text[0]);
        if (x < 0 || x >= Size || !int.TryParse(text[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var row) || row < 1 || row > Size)
            return null;
        return (x, row - 1);
    }

    /// <summary>The stones as one text (to compare positions for the ko).</summary>
    public string Key => _key ??= string.Concat(_points.Select(p => p switch { 1 => 'X', -1 => 'O', _ => '.' }));

    private string? _key;

    public IEnumerable<(int X, int Y)> Neighbours(int x, int y)
    {
        if (x > 0) yield return (x - 1, y);
        if (x < Size - 1) yield return (x + 1, y);
        if (y > 0) yield return (x, y - 1);
        if (y < Size - 1) yield return (x, y + 1);
    }

    /// <summary>The group of the stone at (x, y) and its liberties (the empty points touching it).</summary>
    public (List<(int X, int Y)> Stones, HashSet<(int X, int Y)> Liberties) Group(int x, int y)
    {
        var colour = this[x, y];
        var stones = new List<(int, int)>();
        var liberties = new HashSet<(int, int)>();
        if (colour == 0)
            return (stones, liberties);
        var seen = new HashSet<(int, int)> { (x, y) };
        var stack = new Stack<(int X, int Y)>();
        stack.Push((x, y));
        while (stack.Count > 0)
        {
            var point = stack.Pop();
            stones.Add(point);
            foreach (var next in Neighbours(point.X, point.Y))
            {
                var stone = this[next.X, next.Y];
                if (stone == 0)
                    liberties.Add(next);
                else if (stone == colour && seen.Add(next))
                    stack.Push(next);
            }
        }
        return (stones, liberties);
    }

    /// <summary>
    /// The board after a stone of <paramref name="colour"/> at (x, y): the groups of the other side left without
    /// liberty are taken off. Null when the point is taken or the stone would kill its own group (suicide).
    /// </summary>
    public GoBoard? Place(int x, int y, sbyte colour)
    {
        if (!OnBoard(x, y) || this[x, y] != 0)
            return null;
        var next = new GoBoard(this);
        next._points[y * Size + x] = colour;
        var taken = 0;
        foreach (var (nx, ny) in Neighbours(x, y))
        {
            if (next[nx, ny] != -colour)
                continue;
            var (stones, liberties) = next.Group(nx, ny);
            if (liberties.Count > 0)
                continue;
            foreach (var (sx, sy) in stones)
                next._points[sy * Size + sx] = 0;
            taken += stones.Count;
        }
        if (taken == 0 && next.Group(x, y).Liberties.Count == 0)
            return null;
        if (colour > 0)
            next.TakenByBlack += taken;
        else
            next.TakenByWhite += taken;
        return next;
    }

    /// <summary>The board as text, row 1 at the bottom, X Black and O White, for the prompt of the LLM.</summary>
    public string Diagram((int X, int Y)? last = null)
    {
        var text = new StringBuilder();
        var header = "    " + string.Join(" ", Columns.Take(Size).Select(c => c.ToString()));
        text.Append(header).Append('\n');
        for (var y = Size - 1; y >= 0; y--)
        {
            text.Append((y + 1).ToString(CultureInfo.InvariantCulture).PadLeft(2)).Append("  ");
            for (var x = 0; x < Size; x++)
            {
                var stone = this[x, y];
                var mark = stone switch { 1 => 'X', -1 => 'O', _ => '.' };
                text.Append(mark).Append(x < Size - 1 ? " " : string.Empty);
            }
            text.Append("  ").Append(y + 1).Append('\n');
        }
        text.Append(header);
        return text.ToString();
    }

    /// <summary>The points of the star points (hoshi), drawn on the board.</summary>
    public IEnumerable<(int X, int Y)> StarPoints()
    {
        var edge = Size >= 13 ? 3 : 2;
        var far = Size - 1 - edge;
        var middle = Size / 2;
        var lines = Size % 2 == 1 && Size >= 9 ? new[] { edge, middle, far } : new[] { edge, far };
        foreach (var x in lines)
            foreach (var y in lines)
                if (Size >= 19 || x == y || x + y == Size - 1)
                    yield return (x, y);
    }
}

/// <summary>The steps of a game of go.</summary>
public enum GoPhase
{
    Playing,

    /// <summary>Two passes in a row: the dead stones are marked, then the score is counted.</summary>
    Scoring,

    Over
}

/// <summary>The score of a game: stones and territory of each side (area scoring), komi added to White.</summary>
public sealed record GoScore(int BlackStones, int BlackTerritory, int WhiteStones, int WhiteTerritory, double Komi)
{
    public double Black => BlackStones + BlackTerritory;

    public double White => WhiteStones + WhiteTerritory + Komi;

    /// <summary>The result as SGF writes it: B+3.5, W+0.5.</summary>
    public string Result => Black > White
        ? string.Create(CultureInfo.InvariantCulture, $"B+{Black - White:0.#}")
        : string.Create(CultureInfo.InvariantCulture, $"W+{White - Black:0.#}");
}

/// <summary>
/// A game of go, Chinese rules: Black plays first; a stone takes the groups of the other side it leaves without
/// liberty; suicide is not allowed; a move may not bring back a whole-board position seen before (positional
/// superko, which covers the ko); two passes in a row end the play, the dead stones are marked, and the score is
/// the stones plus the territory of each side (area scoring), with the komi for White.
/// </summary>
public sealed class GoGame
{
    private readonly List<GoBoard> _boards;
    private readonly List<GoMove> _moves = [];
    private readonly HashSet<(int X, int Y)> _dead = [];

    public GoGame(int size = 19, double komi = 7.5)
    {
        Komi = komi;
        _boards = [new GoBoard(size)];
    }

    public double Komi { get; }

    public int Size => Current.Size;

    public GoBoard Current => _boards[^1];

    public IReadOnlyList<GoMove> Moves => _moves;

    /// <summary>The side to move: 1 Black, -1 White.</summary>
    public sbyte ToMove => (sbyte)(_moves.Count % 2 == 0 ? 1 : -1);

    public GoPhase Phase { get; private set; }

    /// <summary>The result (B+3.5, W+R…), empty while the game goes on.</summary>
    public string Result { get; private set; } = string.Empty;

    /// <summary>How the game ended, in words.</summary>
    public string Reason { get; private set; } = string.Empty;

    /// <summary>The stones marked dead while counting.</summary>
    public IReadOnlyCollection<(int X, int Y)> Dead => _dead;

    /// <summary>The last stone played (null after a pass or at the start).</summary>
    public (int X, int Y)? LastStone => _moves.Count > 0 && !_moves[^1].IsPass ? (_moves[^1].X, _moves[^1].Y) : null;

    /// <summary>The board after the move at (x, y) of the side to move, or null when it is not allowed (taken, suicide, ko).</summary>
    public GoBoard? Try(int x, int y)
    {
        if (Phase != GoPhase.Playing)
            return null;
        var next = Current.Place(x, y, ToMove);
        if (next is null)
            return null;
        var key = next.Key;
        return _boards.Any(b => b.Key == key) ? null : next;
    }

    /// <summary>Why the move at (x, y) is not allowed (null when it is).</summary>
    public string? WhyNot(int x, int y)
    {
        if (Phase != GoPhase.Playing)
            return "The play is over";
        if (!Current.OnBoard(x, y))
            return "Off the board";
        if (Current[x, y] != 0)
            return "The point is taken";
        var next = Current.Place(x, y, ToMove);
        if (next is null)
            return "Suicide: the stone would have no liberty";
        var key = next.Key;
        return _boards.Any(b => b.Key == key) ? "Ko: this would repeat an earlier position" : null;
    }

    /// <summary>The points where the side to move may play.</summary>
    public IReadOnlyList<GoMove> LegalMoves()
    {
        var moves = new List<GoMove>();
        var keys = _boards.Select(b => b.Key).ToHashSet();
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
                if (Current[x, y] == 0 && Current.Place(x, y, ToMove) is { } next && !keys.Contains(next.Key))
                    moves.Add(new GoMove(x, y));
        return moves;
    }

    /// <summary>Plays <paramref name="move"/> if it is allowed; false otherwise.</summary>
    public bool Play(GoMove move)
    {
        if (Phase != GoPhase.Playing)
            return false;
        if (move.IsPass)
        {
            _boards.Add(Current);
            _moves.Add(move);
            if (_moves.Count >= 2 && _moves[^2].IsPass)
            {
                Phase = GoPhase.Scoring;
                _dead.Clear();
            }
            return true;
        }
        if (Try(move.X, move.Y) is not { } next)
            return false;
        _boards.Add(next);
        _moves.Add(move);
        return true;
    }

    /// <summary>Takes back the last <paramref name="count"/> moves (back to the play).</summary>
    public void Undo(int count)
    {
        for (var k = 0; k < count && _moves.Count > 0; k++)
        {
            _boards.RemoveAt(_boards.Count - 1);
            _moves.RemoveAt(_moves.Count - 1);
        }
        Phase = GoPhase.Playing;
        Result = Reason = string.Empty;
        _dead.Clear();
    }

    public void Resign(sbyte colour)
    {
        if (Phase == GoPhase.Over)
            return;
        Phase = GoPhase.Over;
        Result = colour > 0 ? "W+R" : "B+R";
        Reason = colour > 0 ? "Black resigned" : "White resigned";
    }

    /// <summary>Marks the group of (x, y) dead, or alive again (while counting).</summary>
    public void ToggleDead(int x, int y)
    {
        if (Phase != GoPhase.Scoring || Current[x, y] == 0)
            return;
        var (stones, _) = Current.Group(x, y);
        var dead = !_dead.Contains((x, y));
        foreach (var stone in stones)
        {
            if (dead)
                _dead.Add(stone);
            else
                _dead.Remove(stone);
        }
    }

    /// <summary>
    /// The score with the dead stones taken off: each side's stones and the empty points only it surrounds; the
    /// owner of each point (1, -1, 0 neutral) for the drawing.
    /// </summary>
    public (GoScore Score, sbyte[,] Owner) Count()
    {
        var size = Size;
        var board = new sbyte[size, size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                board[x, y] = _dead.Contains((x, y)) ? (sbyte)0 : Current[x, y];
        var owner = new sbyte[size, size];
        var seen = new bool[size, size];
        int blackStones = 0, whiteStones = 0, blackTerritory = 0, whiteTerritory = 0;
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                if (board[x, y] == 1)
                {
                    blackStones++;
                    owner[x, y] = 1;
                    continue;
                }
                if (board[x, y] == -1)
                {
                    whiteStones++;
                    owner[x, y] = -1;
                    continue;
                }
                if (seen[x, y])
                    continue;
                // An empty region: whose stones border it.
                var region = new List<(int X, int Y)>();
                bool touchesBlack = false, touchesWhite = false;
                var stack = new Stack<(int X, int Y)>();
                stack.Push((x, y));
                seen[x, y] = true;
                while (stack.Count > 0)
                {
                    var point = stack.Pop();
                    region.Add(point);
                    foreach (var (nx, ny) in Current.Neighbours(point.X, point.Y))
                    {
                        var stone = board[nx, ny];
                        if (stone == 1)
                            touchesBlack = true;
                        else if (stone == -1)
                            touchesWhite = true;
                        else if (!seen[nx, ny])
                        {
                            seen[nx, ny] = true;
                            stack.Push((nx, ny));
                        }
                    }
                }
                var who = touchesBlack == touchesWhite ? (sbyte)0 : touchesBlack ? (sbyte)1 : (sbyte)-1;
                foreach (var (rx, ry) in region)
                    owner[rx, ry] = who;
                if (who == 1)
                    blackTerritory += region.Count;
                else if (who == -1)
                    whiteTerritory += region.Count;
            }
        }
        return (new GoScore(blackStones, blackTerritory, whiteStones, whiteTerritory, Komi), owner);
    }

    /// <summary>Ends the counting: the result is the score with the dead stones marked.</summary>
    public GoScore Finish()
    {
        var (score, _) = Count();
        Phase = GoPhase.Over;
        Result = score.Result;
        Reason = string.Create(CultureInfo.InvariantCulture,
            $"Black {score.Black:0.#} ({score.BlackStones} stones + {score.BlackTerritory} territory) · White {score.White:0.#} ({score.WhiteStones} + {score.WhiteTerritory} + komi {score.Komi:0.#})");
        return score;
    }

    /// <summary>The groups of <paramref name="colour"/> with a single liberty: their stones and that liberty.</summary>
    public IEnumerable<(List<(int X, int Y)> Stones, (int X, int Y) Liberty)> InAtari(sbyte colour)
    {
        var seen = new HashSet<(int, int)>();
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                if (Current[x, y] != colour || seen.Contains((x, y)))
                    continue;
                var (stones, liberties) = Current.Group(x, y);
                foreach (var stone in stones)
                    seen.Add(stone);
                if (liberties.Count == 1)
                    yield return (stones, liberties.First());
            }
    }

    /// <summary>The moves numbered for the prompt: 1. B D4 2. W Q16 3. B pass…</summary>
    public string MoveText(int last = int.MaxValue)
    {
        var text = new StringBuilder();
        var start = Math.Max(0, _moves.Count - last);
        for (var k = start; k < _moves.Count; k++)
            text.Append(k + 1).Append(". ").Append(k % 2 == 0 ? "B " : "W ").Append(_moves[k].Name).Append(' ');
        return text.ToString().TrimEnd();
    }

    /// <summary>The game in SGF (read by every go program).</summary>
    public string Sgf(string black, string white)
    {
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"(;GM[1]FF[4]CA[UTF-8]AP[Kaonomi]RU[Chinese]SZ[{Size}]KM[{Komi:0.#}]");
        text.Append($"PB[{Escape(black)}]PW[{Escape(white)}]DT[{DateTime.Now:yyyy-MM-dd}]");
        if (Result.Length > 0)
            text.Append($"RE[{Result}]");
        text.Append('\n');
        for (var k = 0; k < _moves.Count; k++)
        {
            var move = _moves[k];
            var point = move.IsPass ? string.Empty : $"{(char)('a' + move.X)}{(char)('a' + (Size - 1 - move.Y))}";
            text.Append(';').Append(k % 2 == 0 ? 'B' : 'W').Append('[').Append(point).Append(']');
        }
        text.Append(")\n");
        return text.ToString();
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("]", "\\]");
}
