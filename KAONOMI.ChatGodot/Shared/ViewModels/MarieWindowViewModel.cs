using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>An escape from the maze of /game:marie: its time, the knock-downs, the life left, the day.</summary>
public sealed record MarieRun(double Seconds, int Knockdowns, int Health, DateTime Date);

/// <summary>
/// /game:marie: a first-person game in a maze of 25 × 25 cells, hunted by MarieKilleuse. The game itself is
/// <see cref="MarieGame"/>; this keeps the current one, starts new ones, and keeps the best escapes in
/// ~/kaonomi/marie-scores.json.
/// </summary>
public partial class MarieWindowViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _scoresPath;

    public MarieWindowViewModel() : this(null)
    {
    }

    public MarieWindowViewModel(string? scoresPath)
    {
        _scoresPath = scoresPath ?? Path.Combine(ChatSettings.KaonomiHome, "marie-scores.json");
        Best = Load();
        Game = new MarieGame(Environment.TickCount & 0x7fffffff);
    }

    /// <summary>Raised when a new game starts: the view builds its maze again.</summary>
    public event Action? Started;

    public MarieGame Game { get; private set; }

    /// <summary>The 10 fastest escapes.</summary>
    public IReadOnlyList<MarieRun> Best { get; private set; }

    [ObservableProperty]
    private string _status = "Click in the maze to play · ZQSD / WASD or the arrows to walk, the mouse to look, click to shoot · Esc: pause";

    [RelayCommand]
    private void NewGame()
    {
        Game = new MarieGame(Environment.TickCount & 0x7fffffff);
        Status = "New maze: find the exit at the far corner";
        Started?.Invoke();
    }

    /// <summary>Keeps the escape of the game if it is among the 10 fastest; its rank (1 = best), or 0.</summary>
    public int Record()
    {
        if (Game.Outcome != MarieOutcome.Escaped)
            return 0;
        var run = new MarieRun(Math.Round(Game.Time, 1), Game.Knockdowns, Game.Health, DateTime.Now);
        var list = Best.Append(run).OrderBy(r => r.Seconds).Take(10).ToList();
        var rank = list.IndexOf(run) + 1;
        Best = list;
        OnPropertyChanged(nameof(Best));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_scoresPath)!);
            File.WriteAllText(_scoresPath, JsonSerializer.Serialize(list, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error("Marie", "The best escapes could not be saved", ex);
        }
        return rank;
    }

    private List<MarieRun> Load()
    {
        try
        {
            return File.Exists(_scoresPath)
                ? JsonSerializer.Deserialize<List<MarieRun>>(File.ReadAllText(_scoresPath), JsonOptions) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            ActivityLog.Error("Marie", $"{_scoresPath} could not be read", ex);
            return [];
        }
    }
}
