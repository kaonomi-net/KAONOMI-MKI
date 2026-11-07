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

/// <summary>A test taken: when, which, the right answers, the time, the indicative IQ.</summary>
public sealed record IqResult(DateTime Date, string Test, int Right, int Total, int Seconds, int Score)
{
    public string Line => $"{Date:yyyy-MM-dd HH:mm}  ·  {Test}  ·  {Right}/{Total}  ·  {Seconds / 60}:{Seconds % 60:00}  ·  IQ ≈ {Score}";
}

/// <summary>A question answered, for the review at the end: what was asked, answered, right, and why.</summary>
public sealed record IqReview(int Number, string Test, string Question, string Given, string Right, bool IsRight, string Why)
{
    public string Mark => IsRight ? "✔" : "✘";
}

/// <summary>Where /iq is: the choice of a test, a question, the digits to remember, the right answer explained (training), the results.</summary>
public enum IqStage
{
    Home,
    Question,
    Memorize,
    Recall,
    Feedback,
    Result
}

/// <summary>
/// /iq: a series of tests in the manner of the IQ tests: number series, matrices, words, memory, each of 10 questions
/// harder and harder, or the full test (6 of each), drawn at random from the bank of 1000 questions
/// (<see cref="IqBank"/>; made on the spot by <see cref="IqTests"/> without it). Each question has 60 seconds (the
/// memory: its digits shown a while, then typed). In training, the time stops at each answer and the right one is shown
/// with its explanation before NEXT. At the end: the right answers by test, the time, an indicative IQ (not a measured
/// one) and the review of every question with its explanation; the results are kept in ~/kaonomi/iq-results.json.
/// </summary>
public partial class IqWindowViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static IqBank? _bank;
    private static bool _bankRead;

    /// <summary>
    /// The bank shipped with the chat (set by the chat at its start: <c>Data/iq-questions.json</c> of its project);
    /// <c>~/kaonomi/iq-questions.json</c> is read first when it is there (a bank of one's own).
    /// </summary>
    public static string? BankPath { get; set; }

    /// <summary>The bank of questions, read once (null: none, the questions are made on the spot).</summary>
    public static IqBank? Bank
    {
        get
        {
            if (_bankRead)
                return _bank;
            _bankRead = true;
            var own = Path.Combine(ChatSettings.KaonomiHome, "iq-questions.json");
            _bank = IqBank.Load(own) ?? (BankPath is { } shipped ? IqBank.Load(shipped) : null);
            if (_bank is not null)
                ActivityLog.Info("IQ", $"Bank of {_bank.Questions.Count} questions read");
            return _bank;
        }
    }

    private readonly string _language;
    private readonly string _resultsPath;
    private List<IqQuestion> _questions = [];
    private readonly List<IqReview> _reviews = [];
    private string _testName = string.Empty;
    private double _elapsed;

    public IqWindowViewModel() : this("FR", null)
    {
    }

    public IqWindowViewModel(string language, string? resultsPath = null)
    {
        _language = language;
        _resultsPath = resultsPath ?? Path.Combine(ChatSettings.KaonomiHome, "iq-results.json");
        History = Load();
    }

    [ObservableProperty]
    private IqStage _stage = IqStage.Home;

    [ObservableProperty]
    private IqQuestion? _current;

    [ObservableProperty]
    private int _index;

    /// <summary>Seconds left for the question (or for looking at the digits).</summary>
    [ObservableProperty]
    private double _timeLeft;

    /// <summary>The digits typed back (memory).</summary>
    [ObservableProperty]
    private string _typed = string.Empty;

    /// <summary>Training: the time stops at each answer, the right one is shown and explained before NEXT.</summary>
    [ObservableProperty]
    private bool _isTraining;

    /// <summary>The choice given to the question shown in <see cref="IqStage.Feedback"/> (-1: none, the time ran out or SKIP).</summary>
    [ObservableProperty]
    private int _givenChoice = -1;

    /// <summary>The answer given (the digits typed, for the memory), shown in <see cref="IqStage.Feedback"/>.</summary>
    [ObservableProperty]
    private string _givenText = string.Empty;

    [ObservableProperty]
    private bool _givenRight;

    [ObservableProperty]
    private string _resultTitle = string.Empty;

    [ObservableProperty]
    private string _resultDetail = string.Empty;

    public IReadOnlyList<IqReview> Reviews => _reviews;

    public IReadOnlyList<IqResult> History { get; private set; }

    public int Count => _questions.Count;

    /// <summary>"Matrices · question 3 / 10".</summary>
    public string Progress => Current is null ? string.Empty : $"{IqTests.Name(Current.Kind)}  ·  question {Index + 1} / {Count}";

    /// <summary>"0:42".</summary>
    public string TimeLabel => $"{(int)Math.Ceiling(TimeLeft) / 60}:{(int)Math.Ceiling(TimeLeft) % 60:00}";

    public bool IsMatrix => Current?.Kind == IqTestKind.Matrices;

    public bool HasChoices => Current is { Choices.Count: > 0 } && !IsMatrix;

    /// <summary>NUMBERS, MATRICES, WORDS, MEMORY: one test; FULL: 6 questions of each, 24 in all.</summary>
    [RelayCommand]
    private void Start(string which)
    {
        var random = new Random(Environment.TickCount & 0x7fffffff);
        var bank = Bank;
        // From the bank at random, the levels rising; without the bank, made on the spot.
        IEnumerable<IqQuestion> Draw(IqTestKind kind, int count) => bank is not null
            ? bank.Pick(kind, count, random, _language)
            : IqTests.Make(kind, random.Next(), _language).Where((_, i) => count == IqTests.QuestionsPerTest || i % 2 == 0 || i == IqTests.QuestionsPerTest - 1).Take(count);
        if (Enum.TryParse<IqTestKind>(which, out var kind))
        {
            _questions = Draw(kind, IqTests.QuestionsPerTest).ToList();
            _testName = IqTests.Name(kind);
        }
        else
        {
            _questions = Enum.GetValues<IqTestKind>().SelectMany(k => Draw(k, 6)).ToList();
            _testName = "Full test";
        }
        if (IsTraining)
            _testName = "Training · " + _testName;
        _reviews.Clear();
        _elapsed = 0;
        Show(0);
    }

    /// <summary>A choice clicked (its index).</summary>
    [RelayCommand]
    private void Answer(int choice)
    {
        if (Current is not { } q || Stage != IqStage.Question)
            return;
        var given = choice >= 0 && choice < q.Choices.Count ? q.Choices[choice] : "—";
        Record(q, given, choice == q.Answer, choice);
    }

    /// <summary>NEXT (training): from the right answer explained to the next question, or to the results.</summary>
    [RelayCommand]
    private void Next()
    {
        if (Stage != IqStage.Feedback)
            return;
        if (Index + 1 < _questions.Count)
            Show(Index + 1);
        else
            Finish();
    }

    /// <summary>OK (or Enter) after typing the digits back.</summary>
    [RelayCommand]
    private void SubmitDigits()
    {
        if (Current is not { } q || Stage != IqStage.Recall)
            return;
        var typed = new string(Typed.Where(char.IsDigit).ToArray());
        Record(q, typed.Length == 0 ? "—" : typed, typed == q.Digits);
    }

    /// <summary>SKIP: the question counts as missed.</summary>
    [RelayCommand]
    private void Skip()
    {
        if (Current is { } q && Stage is IqStage.Question or IqStage.Recall or IqStage.Memorize)
            Record(q, "—", false);
    }

    /// <summary>QUIT: back to the choice of a test, nothing kept.</summary>
    [RelayCommand]
    private void Quit()
    {
        Stage = IqStage.Home;
        Current = null;
    }

    /// <summary>Called every frame by the view: the time of the question runs down.</summary>
    public void Tick(double delta)
    {
        if (Stage is not (IqStage.Question or IqStage.Memorize or IqStage.Recall))
            return;
        _elapsed += delta;
        if (Stage == IqStage.Recall)
            return;
        var before = (int)Math.Ceiling(TimeLeft);
        TimeLeft = Math.Max(0, TimeLeft - delta);
        if ((int)Math.Ceiling(TimeLeft) != before)
            OnPropertyChanged(nameof(TimeLabel));
        if (TimeLeft > 0)
            return;
        if (Stage == IqStage.Memorize)
        {
            // The digits hidden: type them back.
            Stage = IqStage.Recall;
            Typed = string.Empty;
        }
        else if (Current is { } q)
            Record(q, "time out", false);
    }

    private void Show(int index)
    {
        Index = index;
        Current = _questions[index];
        Typed = string.Empty;
        if (Current.Kind == IqTestKind.Memory)
        {
            TimeLeft = IqTests.ShowSeconds(Current);
            Stage = IqStage.Memorize;
        }
        else
        {
            TimeLeft = IqTests.SecondsPerQuestion;
            Stage = IqStage.Question;
        }
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(TimeLabel));
        OnPropertyChanged(nameof(IsMatrix));
        OnPropertyChanged(nameof(HasChoices));
        OnPropertyChanged(nameof(Count));
    }

    private void Record(IqQuestion q, string given, bool right, int choice = -1)
    {
        var expected = q.Kind == IqTestKind.Memory ? q.Digits! : q.Choices[q.Answer];
        _reviews.Add(new IqReview(_reviews.Count + 1, IqTests.Name(q.Kind), q.Text, given, expected, right,
            q.Kind == IqTestKind.Memory ? $"{q.Digits!.Length} digits" : q.Explanation));
        if (IsTraining)
        {
            // The time stopped: the right answer shown and explained, until NEXT.
            GivenChoice = choice;
            GivenText = given;
            GivenRight = right;
            Stage = IqStage.Feedback;
            return;
        }
        if (Index + 1 < _questions.Count)
            Show(Index + 1);
        else
            Finish();
    }

    private void Finish()
    {
        var right = _reviews.Count(r => r.IsRight);
        var total = _reviews.Count;
        var score = IqTests.Score(total == 0 ? 0 : (double)right / total);
        var seconds = (int)Math.Round(_elapsed);
        var byTest = _reviews.GroupBy(r => r.Test)
            .Select(g => $"{g.Key}: {g.Count(r => r.IsRight)}/{g.Count()} (IQ ≈ {IqTests.Score((double)g.Count(r => r.IsRight) / g.Count())})");
        ResultTitle = $"{_testName}: {right} / {total}  ·  indicative IQ ≈ {score}";
        ResultDetail = string.Join("   ·   ", byTest) + $"   ·   time {seconds / 60}:{seconds % 60:00}";
        History = History.Prepend(new IqResult(DateTime.Now, _testName, right, total, seconds, score)).Take(50).ToList();
        OnPropertyChanged(nameof(History));
        OnPropertyChanged(nameof(Reviews));
        Save();
        Current = null;
        Stage = IqStage.Result;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_resultsPath)!);
            File.WriteAllText(_resultsPath, JsonSerializer.Serialize(History, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ActivityLog.Error("IQ", "The results could not be saved", ex);
        }
    }

    private List<IqResult> Load()
    {
        try
        {
            return File.Exists(_resultsPath)
                ? JsonSerializer.Deserialize<List<IqResult>>(File.ReadAllText(_resultsPath), JsonOptions) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            ActivityLog.Error("IQ", $"{_resultsPath} could not be read", ex);
            return [];
        }
    }
}
