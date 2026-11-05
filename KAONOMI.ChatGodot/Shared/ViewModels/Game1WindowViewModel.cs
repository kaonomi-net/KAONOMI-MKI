using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>A line of the game: 5 points, 4 cells long, from (<see cref="X"/>, <see cref="Y"/>) along (<see cref="Dx"/>, <see cref="Dy"/>).</summary>
public sealed record Game1Line(int X, int Y, int Dx, int Dy)
{
    /// <summary>The 5 points of the line, from its start.</summary>
    public IEnumerable<(int X, int Y)> Points => Enumerable.Range(0, Game1WindowViewModel.LinePoints).Select(i => (X + i * Dx, Y + i * Dy));

    /// <summary>The 4 segments of one cell of the line (each the cell from a point to the next).</summary>
    public IEnumerable<(int X, int Y, int Dx, int Dy)> Segments => Enumerable.Range(0, Game1WindowViewModel.LinePoints - 1).Select(i => (X + i * Dx, Y + i * Dy, Dx, Dy));

    public (int X, int Y) End => (X + (Game1WindowViewModel.LinePoints - 1) * Dx, Y + (Game1WindowViewModel.LinePoints - 1) * Dy);
}

/// <summary>A move: the point added, the line drawn through it, the seconds since the move before and the points it gained.</summary>
public sealed record Game1Move((int X, int Y) Point, Game1Line Line, double Seconds, int Gained);

/// <summary>How a game ended.</summary>
public enum Game1End { None, Stopped, Blocked }

/// <summary>
/// /game:solitaire: the morpion solitaire (lines of five) on a square grid. The game starts with a cross of 36 black points.
/// A move adds a point on an empty crossing and draws through it a line of 4 cells (5 points, all black once the
/// point is added), horizontal, vertical or diagonal. A line never goes over a line or a piece of one (no cell of a
/// line is drawn twice in the same direction); it may cross another line, and it may touch one at a point. Every
/// point, those of the lines included, can be used again.
/// <para>The score (<see cref="PointsFor"/>): each line is worth more than the one before (10 + 2 × its number: the
/// late lines are the hard ones to find), times a speed bonus from ×2 (a move played at once) down to ×1 (a long
/// thought: thinking never costs points). The game ends with STOP or when no move is left; its score is then saved
/// on the Kaonomi server with the comment of the player, unless the hints were shown.</para>
/// </summary>
public partial class Game1WindowViewModel : ViewModelBase
{
    /// <summary>The points of a line (4 cells).</summary>
    public const int LinePoints = 5;

    /// <summary>The crossings of the side of the grid.</summary>
    public const int Size = 30;

    /// <summary>The name of the game in the scores of the server.</summary>
    public const string GameName = "game1";

    /// <summary>The base points of the first line, and what each next line adds to them.</summary>
    public const int BasePoints = 10, PointsPerLine = 2;

    /// <summary>The seconds at which the speed bonus is half (×1.5).</summary>
    public const double SpeedSeconds = 15;

    /// <summary>The four directions of a line: right, down, down-right, up-right.</summary>
    private static readonly (int Dx, int Dy)[] Directions = [(1, 0), (0, 1), (1, 1), (1, -1)];

    private readonly HashSet<(int X, int Y)> _points = [];
    private readonly HashSet<(int X, int Y, int Dx, int Dy)> _segments = [];
    private readonly List<Game1Move> _moves = [];
    private readonly KaonomiAssistantService? _server;
    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset _started;
    private DateTimeOffset _lastMove;
    private DateTimeOffset? _ended;

    public Game1WindowViewModel() : this(null)
    {
    }

    /// <param name="server">Where the scores are saved and read (none: they are not).</param>
    /// <param name="now">The clock (the time of the computer by default; a fake one for the tests).</param>
    public Game1WindowViewModel(KaonomiAssistantService? server, Func<DateTimeOffset>? now = null)
    {
        _server = server;
        _now = now ?? (() => DateTimeOffset.Now);
        NewGame();
    }

    /// <summary>Raised when the board changes (a point, a line, the pending point, the hints): the canvas redraws.</summary>
    public event Action? Changed;

    /// <summary>Raised once when the game ends (STOP, or no move left): the view asks for a comment, then <see cref="SaveScoreAsync"/>.</summary>
    public event Action<Game1End>? Finished;

    /// <summary>The black points of the grid.</summary>
    public IReadOnlyCollection<(int X, int Y)> Points => _points;

    /// <summary>The moves played, in their order (their lines and the points they added).</summary>
    public IReadOnlyList<Game1Move> Moves => _moves;

    /// <summary>The point being added: placed, waiting for its line (null: none).</summary>
    public (int X, int Y)? Pending { get; private set; }

    /// <summary>The empty crossings where a point can be added (a line exists through it), shown with "Hints".</summary>
    public IReadOnlyCollection<(int X, int Y)> Playable { get; private set; } = [];

    /// <summary>The lines drawn.</summary>
    [ObservableProperty]
    private int _lines;

    /// <summary>The score: the points of the lines, weighted by their number and the speed of the moves.</summary>
    [ObservableProperty]
    private int _score;

    /// <summary>The moves left (a point and a line through it).</summary>
    [ObservableProperty]
    private int _movesLeft;

    /// <summary>The best score of the session.</summary>
    [ObservableProperty]
    private int _best;

    /// <summary>How the game ended (<see cref="Game1End.None"/> while it goes on).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(UndoCommand))]
    private Game1End _end;

    /// <summary>True once the hints were shown in this game: its score will not be saved.</summary>
    [ObservableProperty]
    private bool _hintsUsed;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    /// <summary>The playable crossings ringed (only once <see cref="HintsUsed"/>: <see cref="RevealHints"/>).</summary>
    [ObservableProperty]
    private bool _showHints;

    partial void OnShowHintsChanged(bool value) => Changed?.Invoke();

    public bool IsFinished => End != Game1End.None;

    /// <summary>The time of the game: from its start to its end, or to now.</summary>
    public TimeSpan Elapsed => (_ended ?? _now()) - _started;

    /// <summary>The time since the last move (or the start), what the speed bonus of the next move depends on.</summary>
    public TimeSpan SinceLastMove => IsFinished ? TimeSpan.Zero : _now() - _lastMove;

    /// <summary>
    /// The points of the line number <paramref name="line"/> (1 for the first) played <paramref name="seconds"/> after
    /// the move before: (10 + 2 × line) × (1 + 1 / (1 + seconds / 15)), rounded: ×2 at once, ×1.5 after 15 s, ×1.2
    /// after a minute, towards ×1 for a long thought.
    /// </summary>
    public static int PointsFor(int line, double seconds) =>
        (int)Math.Round((BasePoints + PointsPerLine * line) * SpeedBonus(seconds), MidpointRounding.AwayFromZero);

    /// <summary>The speed bonus of a move played <paramref name="seconds"/> after the move before (2 down to 1).</summary>
    public static double SpeedBonus(double seconds) => 1 + 1 / (1 + Math.Max(0, seconds) / SpeedSeconds);

    // ── The game ──

    /// <summary>A new game: the cross of 36 points in the middle of the grid, the clock at zero, the hints hidden.</summary>
    [RelayCommand]
    private void NewGame()
    {
        _points.Clear();
        _segments.Clear();
        _moves.Clear();
        Pending = null;
        // The outline of a cross whose arms are 3 cells wide and 3 cells long (10 x 10 crossings).
        string[] cross =
        [
            "...####...",
            "...#..#...",
            "...#..#...",
            "####..####",
            "#........#",
            "#........#",
            "####..####",
            "...#..#...",
            "...#..#...",
            "...####..."
        ];
        var origin = (Size - cross.Length) / 2;
        for (var y = 0; y < cross.Length; y++)
            for (var x = 0; x < cross[y].Length; x++)
                if (cross[y][x] == '#')
                    _points.Add((origin + x, origin + y));
        _started = _lastMove = _now();
        _ended = null;
        End = Game1End.None;
        HintsUsed = false;
        ShowHints = false;
        Lines = 0;
        Score = 0;
        Refresh();
        Say("Click an empty crossing to add a point, then a black point to draw the line of 4 cells through it · the faster, the more points", false);
    }

    private bool CanUndo() => !IsFinished;

    /// <summary>Takes back the last move (its line, its point and its points), or the point being added.</summary>
    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (Pending is not null)
        {
            CancelPending();
            return;
        }
        if (_moves.Count == 0)
            return;
        var last = _moves[^1];
        _moves.RemoveAt(_moves.Count - 1);
        foreach (var segment in last.Line.Segments)
            _segments.Remove(Normalize(segment));
        _points.Remove(last.Point);
        Lines = _moves.Count;
        Score -= last.Gained;
        // The time spent before the move taken back gives no bonus to the next one.
        _lastMove = _now();
        Refresh();
        Say($"Move {_moves.Count + 1} taken back (−{last.Gained})", false);
    }

    private bool CanStop() => !IsFinished;

    /// <summary>Gives up: the game ends with its score as it is.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop() => Finish(Game1End.Stopped);

    /// <summary>
    /// Shows the hints (the playable crossings ringed): the score of the game will not be saved (the view asks first).
    /// </summary>
    public void RevealHints()
    {
        HintsUsed = true;
        ShowHints = true;
        Say("Hints shown: the score of this game will not be saved", false);
    }

    /// <summary>The point being added is taken away (right click, Escape).</summary>
    public void CancelPending()
    {
        if (Pending is null)
            return;
        Pending = null;
        Changed?.Invoke();
        Say("Point taken away: click an empty crossing to add one", false);
    }

    /// <summary>
    /// A click on the crossing (<paramref name="x"/>, <paramref name="y"/>): on an empty one, the point to add (only
    /// where a line can go through it); with a point to add, a black point ends its line (or the point itself: taken away).
    /// </summary>
    public void Click(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Size || y >= Size)
            return;
        if (IsFinished)
        {
            Say($"The game is over ({Score} points, {Lines} line(s)) · NEW GAME starts again", false);
            return;
        }
        var at = (x, y);
        if (Pending is { } pending)
        {
            if (at == pending)
            {
                CancelPending();
                return;
            }
            if (_points.Contains(at))
            {
                Draw(pending, at);
                return;
            }
        }
        if (_points.Contains(at))
        {
            Say("This crossing already has a point: click an empty crossing to add one", false);
            return;
        }
        if (!LinesThrough(at).Any())
        {
            Say("No line of 4 cells can go through this crossing: choose another one", true);
            return;
        }
        Pending = at;
        Changed?.Invoke();
        Say("Now click a black point 1 to 4 cells away, in a straight line: the line of 4 cells goes from it through the new point", false);
    }

    /// <summary>
    /// The line asked by a click on the black point <paramref name="end"/> with the point <paramref name="added"/>: from
    /// <paramref name="end"/>, 4 cells towards the added point (which is inside it); null and the reason when it is not a
    /// possible line.
    /// </summary>
    public (Game1Line? Line, string Reason) LineTo((int X, int Y) added, (int X, int Y) end)
    {
        var dx = added.X - end.X;
        var dy = added.Y - end.Y;
        var steps = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (steps == 0 || steps > LinePoints - 1 || (dx != 0 && Math.Abs(dx) != steps) || (dy != 0 && Math.Abs(dy) != steps))
            return (null, "The line must be straight (horizontal, vertical or diagonal) and its end 1 to 4 cells from the new point");
        var line = Canonical(new Game1Line(end.X, end.Y, Math.Sign(dx), Math.Sign(dy)));
        return Check(line, added);
    }

    /// <summary>Whether <paramref name="line"/> can be drawn with the point <paramref name="added"/>, and why not.</summary>
    private (Game1Line? Line, string Reason) Check(Game1Line line, (int X, int Y) added)
    {
        foreach (var p in line.Points)
        {
            if (p.X < 0 || p.Y < 0 || p.X >= Size || p.Y >= Size)
                return (null, "The line goes out of the grid");
            if (p != added && !_points.Contains(p))
                return (null, "The line must go through 5 black points (the new one included): a point is missing");
        }
        if (line.Segments.Any(s => _segments.Contains(Normalize(s))))
            return (null, "Not allowed: the line would go over a line or a piece of one (crossing a line is allowed)");
        return (line, string.Empty);
    }

    private void Draw((int X, int Y) added, (int X, int Y) end)
    {
        var (line, reason) = LineTo(added, end);
        if (line is null)
        {
            Say(reason, true);
            return;
        }
        var now = _now();
        var seconds = (now - _lastMove).TotalSeconds;
        var gained = PointsFor(_moves.Count + 1, seconds);
        _lastMove = now;
        _points.Add(added);
        foreach (var segment in line.Segments)
            _segments.Add(Normalize(segment));
        _moves.Add(new Game1Move(added, line, seconds, gained));
        Pending = null;
        Lines = _moves.Count;
        Score += gained;
        Refresh();
        if (MovesLeft == 0)
        {
            Finish(Game1End.Blocked);
            return;
        }
        Say($"Line {Lines}: +{gained} ({BasePoints + PointsPerLine * Lines} × {SpeedBonus(seconds):0.00} for {seconds:0} s) · {MovesLeft} move(s) possible", false);
    }

    /// <summary>The game ends: the clock stops, the best score of the session is kept, the view is told.</summary>
    private void Finish(Game1End end)
    {
        if (IsFinished)
            return;
        Pending = null;
        _ended = _now();
        End = end;
        if (Score > Best)
            Best = Score;
        Changed?.Invoke();
        var why = end == Game1End.Blocked ? "no move is left" : "stopped";
        Say($"Game over ({why}): {Score} points, {Lines} line(s) in {Elapsed:mm\\:ss}" + (HintsUsed ? " · hints shown: the score is not saved" : string.Empty), false);
        Finished?.Invoke(end);
    }

    /// <summary>
    /// Saves the score of the finished game on the Kaonomi server with <paramref name="comment"/>; the message to show.
    /// Not saved when the hints were shown, without a server, or before the game is over.
    /// </summary>
    public async Task<string> SaveScoreAsync(string? comment)
    {
        if (!IsFinished)
            return "The game is not over";
        if (HintsUsed)
            return "Hints shown: the score is not saved";
        if (Lines == 0)
            return "No line drawn: nothing to save";
        if (_server is null)
            return "No Kaonomi server: the score is not saved";
        var entry = new GameScoreEntry
        {
            AppName = "KAONOMI.Chat",
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            Date = _ended,
            Game = GameName,
            Score = Score,
            Lines = Lines,
            DurationSeconds = Math.Round(Elapsed.TotalSeconds, 1),
            End = End == Game1End.Blocked ? "blocked" : "stopped",
            Comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()
        };
        try
        {
            var result = await _server.SaveGameScoreAsync(entry);
            if (!result.IsSuccess)
                return result.Message ?? "The score was not saved";
            ActivityLog.Info("Game1", $"Score saved: {Score} points, {Lines} line(s), {Elapsed:mm\\:ss} ({result.ReturnedId})");
            Say($"Score saved: {Score} points, {Lines} line(s) in {Elapsed:mm\\:ss}", false);
            return $"Score saved: {Score} points";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ActivityLog.Error("Game1", "The score was not saved", ex);
            Say("The score was not saved: " + ex.Message, true);
            return "The score was not saved: " + ex.Message;
        }
    }

    /// <summary>The 10 best scores of the game on the Kaonomi server, the highest first.</summary>
    public async Task<IReadOnlyList<GameScoreEntry>> BestScoresAsync()
    {
        if (_server is null)
            throw new InvalidOperationException("No Kaonomi server: the best scores cannot be read");
        return await _server.GetBestGameScoresAsync(GameName, 10);
    }

    /// <summary>The possible lines through the empty crossing <paramref name="added"/> (the point at any of the 5 places).</summary>
    public IEnumerable<Game1Line> LinesThrough((int X, int Y) added)
    {
        if (_points.Contains(added))
            yield break;
        foreach (var (dx, dy) in Directions)
            for (var place = 0; place < LinePoints; place++)
            {
                var line = new Game1Line(added.X - place * dx, added.Y - place * dy, dx, dy);
                if (Check(line, added).Line is not null)
                    yield return line;
            }
    }

    /// <summary>The moves left, the playable crossings, then a redraw.</summary>
    private void Refresh()
    {
        var playable = new HashSet<(int X, int Y)>();
        var count = 0;
        // A point can only be added within 4 cells of the points already there.
        var near = new HashSet<(int X, int Y)>();
        foreach (var (px, py) in _points)
            for (var dy = -(LinePoints - 1); dy < LinePoints; dy++)
                for (var dx = -(LinePoints - 1); dx < LinePoints; dx++)
                    near.Add((px + dx, py + dy));
        foreach (var cell in near)
        {
            if (cell.X < 0 || cell.Y < 0 || cell.X >= Size || cell.Y >= Size || _points.Contains(cell))
                continue;
            var lines = LinesThrough(cell).Count();
            if (lines == 0)
                continue;
            playable.Add(cell);
            count += lines;
        }
        Playable = playable;
        MovesLeft = count;
        Changed?.Invoke();
    }

    /// <summary>A line written from its other end when it goes left or up (the four directions of <see cref="Directions"/>).</summary>
    private static Game1Line Canonical(Game1Line line)
    {
        if (Directions.Contains((line.Dx, line.Dy)))
            return line;
        var (x, y) = line.End;
        return new Game1Line(x, y, -line.Dx, -line.Dy);
    }

    /// <summary>A segment of one cell written from its end that comes first in <see cref="Directions"/>.</summary>
    private static (int X, int Y, int Dx, int Dy) Normalize((int X, int Y, int Dx, int Dy) s) =>
        Directions.Contains((s.Dx, s.Dy)) ? s : (s.X + s.Dx, s.Y + s.Dy, -s.Dx, -s.Dy);

    private void Say(string text, bool error)
    {
        Status = text;
        IsError = error;
    }
}
