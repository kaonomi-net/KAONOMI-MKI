using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>A calculation of the history: the expression and its result.</summary>
public sealed record CalculatorEntry(string Expression, string Result);

/// <summary>
/// Scientific calculator of the <c>/calculator</c> window (<c>/ncalculator</c> in its own window): the expression is
/// typed with the keys or the keyboard (functions, constants, <c>Ans</c>, see <see cref="CalculatorEngine"/>), its
/// result is shown as it is typed, <c>=</c> keeps it in the history and continues from it; angles in degrees or radians.
/// Every calculation is also written on the tape (<see cref="Tape"/>), kept in a file across the restarts.
/// </summary>
public partial class CalculatorViewModel : ViewModelBase, INoteContextProvider, IDisposable
{
    /// <summary>Most calculations kept in the history.</summary>
    private const int HistorySize = 50;

    /// <summary>Expression being typed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayExpression))]
    private string _expression = string.Empty;

    /// <summary>Result of <see cref="Expression"/> (live preview), or the error once <c>=</c> fails.</summary>
    [ObservableProperty]
    private string _result = "0";

    [ObservableProperty]
    private bool _isError;

    /// <summary>Angles of sin, cos, tan and their inverses in degrees (true) or radians (false).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AngleLabel))]
    private bool _isDegrees = true;

    /// <summary>DEG or RAD, on the key that switches them.</summary>
    public string AngleLabel => IsDegrees ? "DEG" : "RAD";

    /// <summary>True right after <c>=</c>: a number or a function starts a new expression, an operator continues from the result.</summary>
    private bool _justEvaluated;

    /// <summary>Last result (<c>Ans</c>).</summary>
    private double _ans;

    /// <summary>Keys that type several characters: ⌫ removes them at once.</summary>
    private static readonly string[] Tokens =
    [
        "asin(", "acos(", "atan(", "sinh(", "cosh(", "tanh(", "sin(", "cos(", "tan(",
        "log2(", "log(", "ln(", "exp(", "cbrt(", "abs(", "mod", "Ans"
    ];

    /// <summary>Keys that continue from the result after <c>=</c> (the others start a new expression).</summary>
    private static bool ContinuesResult(string key) =>
        key[0] is '+' or '−' or '-' or '×' or '÷' or '^' or '%' or '!' or ')' || key == "mod";

    /// <summary>Expression shown above the result ("0" when empty).</summary>
    public string DisplayExpression => Expression.Length == 0 ? "0" : Expression;

    /// <summary>
    /// The tape: every calculation done with <c>=</c> since the tape was last cleared, one per line, under a line with
    /// the date and time of each calculator session. Editable (notes can be added); saved to
    /// <c>calculator-tape.txt</c> at each change.
    /// </summary>
    [ObservableProperty]
    private string _tape = CalculatorTapeStore.Load();

    /// <summary>True once this window has written its session line on the tape.</summary>
    private bool _tapeSessionStarted;

    partial void OnTapeChanged(string value) => CalculatorTapeStore.Save(value);

    /// <summary>Empties the tape (and its file).</summary>
    [RelayCommand]
    private void ClearTape()
    {
        Tape = string.Empty;
        _tapeSessionStarted = false;
    }

    /// <summary>Adds a calculation at the end of the tape, after the session line of this window.</summary>
    private void WriteOnTape(string expression, string result) => AppendToTape($"{expression} = {result}");

    /// <summary>Adds lines at the end of the tape, after the session line of this window.</summary>
    private void AppendToTape(params string[] lines)
    {
        var text = new System.Text.StringBuilder(Tape);
        if (text.Length > 0 && text[^1] != '\n')
            text.Append(Environment.NewLine);
        if (!_tapeSessionStarted)
        {
            if (text.Length > 0)
                text.Append(Environment.NewLine);
            text.Append($"── {DateTime.Now:yyyy-MM-dd HH:mm} ({AngleLabel}) ──").Append(Environment.NewLine);
            _tapeSessionStarted = true;
        }
        foreach (var line in lines)
            text.Append(line).Append(Environment.NewLine);
        Tape = text.ToString();
    }

    /// <summary>
    /// The tape is shown rendered (its graph pictures included); <see cref="IsTapeEditing"/> shows its text to change it.
    /// </summary>
    [ObservableProperty]
    private bool _isTapeEditing;

    /// <summary>Shows the text of the tape to change it, or its rendering.</summary>
    [RelayCommand]
    private void ToggleTapeEditing() => IsTapeEditing = !IsTapeEditing;

    /// <summary>
    /// Folder of the pictures of the graphs added to the tape (<c>calculator.graphDirectory</c> of config.yml; by
    /// default the Pictures folder of the system, kaonomi/maths).
    /// </summary>
    public string GraphPicturesDirectory { get; }

    /// <summary>Adds a picture of the graph to the tape: the functions, the range, then the picture (a Markdown image line).</summary>
    public void AddGraphToTape(string picturePath, string range)
    {
        var functions = string.Join("; ", GraphFunctions.Select(f => f.Label));
        AppendToTape($"📈 y = {functions}   ({range})", Views.JournalMarkdown.ImageMarkdown(picturePath, 560));
        ActivityLog.Info("Calculator", $"Graph added to the tape: {picturePath}");
    }

    /// <summary>Width written back after a picture of the tape was resized.</summary>
    public void SetTapeImageWidth(int index, int width) => Tape = Views.JournalMarkdown.SetImageWidth(Tape, index, width);

    /// <summary>Pictures of the tape, decoded once.</summary>
    private readonly Dictionary<string, Task<Avalonia.Media.Imaging.Bitmap?>> _tapeImages = new();

    /// <summary>A picture of the tape (decoded once, at most 1600 pixels wide).</summary>
    public Task<Avalonia.Media.Imaging.Bitmap?> LoadTapeImageAsync(string path)
    {
        if (!_tapeImages.TryGetValue(path, out var image))
            _tapeImages[path] = image = PictureLibrary.DecodeAsync(path, 1600, System.Threading.CancellationToken.None);
        return image;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var image in _tapeImages.Values)
            if (image.IsCompletedSuccessfully)
                image.Result?.Dispose();
        _tapeImages.Clear();
    }

    // Graph.

    /// <summary>True when the right panel shows the graph (false: the tape).</summary>
    [ObservableProperty]
    private bool _isGraphMode;

    /// <summary>
    /// Functions of x to draw, separated by ";" or new lines (<c>y = sin(x); x^2/4 - 2</c>); "y =", "f(x) =" are
    /// optional. Angles in radians.
    /// </summary>
    [ObservableProperty]
    private string _graphInput = "sin(x); x^2/4 - 2";

    /// <summary>The curves of <see cref="GraphInput"/> (the valid ones).</summary>
    [ObservableProperty]
    private IReadOnlyList<Views.GraphFunction> _graphFunctions = [];

    /// <summary>The syntax errors of the functions, or empty.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGraphError))]
    private string _graphError = string.Empty;

    /// <summary>True when a function cannot be read.</summary>
    public bool HasGraphError => GraphError.Length > 0;

    /// <summary>Colors of the curves, in order.</summary>
    private static readonly string[] GraphColors = ["#2563EB", "#E11D48", "#059669", "#D97706", "#7C3AED", "#0891B2", "#DB2777", "#65A30D"];

    /// <summary>"y =", "f(x) =", "y1 =", "g(x)=" in front of a function.</summary>
    private static readonly System.Text.RegularExpressions.Regex FunctionName =
        new(@"^\s*[a-zA-Z]\w*\s*(\(\s*x\s*\))?\s*=\s*");

    partial void OnGraphInputChanged(string value) => BuildGraph();

    /// <summary>Shows the graph or the tape.</summary>
    [RelayCommand]
    private void ShowGraph(string? graph) => IsGraphMode = graph == "graph";

    /// <summary>Reads the functions of <see cref="GraphInput"/> into curves; the invalid ones are reported.</summary>
    private void BuildGraph()
    {
        var functions = new List<Views.GraphFunction>();
        var errors = new List<string>();
        foreach (var part in GraphInput.Split([';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var expression = FunctionName.Replace(part, string.Empty).Trim();
            if (expression.Length == 0)
                continue;
            if (CalculatorEngine.CheckFunction(expression) is { } error)
            {
                errors.Add($"{part}: {error}");
                continue;
            }
            var color = Avalonia.Media.Color.Parse(GraphColors[functions.Count % GraphColors.Length]);
            functions.Add(new Views.GraphFunction(expression, x => CalculatorEngine.EvaluateAt(expression, x), color));
        }
        GraphFunctions = functions;
        GraphError = string.Join("   ·   ", errors);
    }

    /// <summary>Calculations done with <c>=</c>, the newest first.</summary>
    public ObservableCollection<CalculatorEntry> History { get; } = [];

    /// <summary>Builds the curves of the default functions; graph pictures in the default folder.</summary>
    public CalculatorViewModel() : this(Models.ChatSettings.DefaultCalculatorGraphDirectory)
    {
    }

    /// <summary>Builds the curves of the default functions; the pictures of the graphs go to <paramref name="graphPicturesDirectory"/>.</summary>
    public CalculatorViewModel(string graphPicturesDirectory)
    {
        GraphPicturesDirectory = graphPicturesDirectory;
        BuildGraph();
    }

    /// <summary>True when at least one calculation is in the history.</summary>
    public bool HasHistory => History.Count > 0;

    /// <summary>
    /// A key of the calculator: a digit, <c>.</c>, an operator (<c>+ − × ÷ ^ %</c>), a parenthesis, <c>√</c>,
    /// <c>±</c> (sign of the last number), <c>C</c> (clear), <c>⌫</c> (last character) or <c>=</c>.
    /// </summary>
    [RelayCommand]
    private void Press(string? key)
    {
        switch (key)
        {
            case null or "":
                return;
            case "C":
                Expression = string.Empty;
                _justEvaluated = false;
                break;
            case "⌫":
                if (_justEvaluated)
                    Expression = string.Empty;
                else if (Tokens.FirstOrDefault(t => Expression.EndsWith(t, StringComparison.Ordinal)) is { } token)
                    Expression = Expression[..^token.Length];
                else if (Expression.Length > 0)
                    Expression = Expression[..^1];
                _justEvaluated = false;
                break;
            case "Rand":
                Type(CalculatorEngine.Format(Math.Round(Random.Shared.NextDouble(), 6)));
                break;
            case "=":
                Evaluate();
                return;
            case "±":
                ToggleSign();
                break;
            default:
                Type(key == "," ? "." : key);
                break;
        }
        Preview();
    }

    /// <summary>Adds <paramref name="text"/> to the expression; after <c>=</c>, only an operator continues from the result.</summary>
    private void Type(string text)
    {
        if (_justEvaluated && !ContinuesResult(text))
            Expression = string.Empty;
        _justEvaluated = false;
        Expression += text;
    }

    /// <summary>Switches the angles between degrees and radians.</summary>
    [RelayCommand]
    private void ToggleAngle()
    {
        IsDegrees = !IsDegrees;
        Preview();
    }

    /// <summary>Puts a calculation of the history back in the expression.</summary>
    [RelayCommand]
    private void Reuse(CalculatorEntry? entry)
    {
        if (entry is null)
            return;
        Expression = entry.Expression;
        _justEvaluated = false;
        Preview();
    }

    /// <summary>Empties the history.</summary>
    [RelayCommand]
    private void ClearHistory()
    {
        History.Clear();
        OnPropertyChanged(nameof(HasHistory));
    }

    /// <summary>Shows the result of the expression as it is typed (nothing when it is still incomplete).</summary>
    private void Preview()
    {
        IsError = false;
        if (Expression.Length == 0)
        {
            Result = "0";
            return;
        }
        try
        {
            Result = CalculatorEngine.Format(CalculatorEngine.Evaluate(Expression, IsDegrees, _ans));
        }
        catch (Exception ex) when (ex is FormatException or ArithmeticException)
        {
            // Incomplete while typing ("12 +"): the last result stays.
        }
    }

    /// <summary><c>=</c>: shows the result or the error; a result goes to the history and becomes the expression.</summary>
    private void Evaluate()
    {
        if (Expression.Length == 0)
            return;
        try
        {
            var value = CalculatorEngine.Evaluate(Expression, IsDegrees, _ans);
            var result = CalculatorEngine.Format(value);
            _ans = value;
            WriteOnTape(Expression, result);
            History.Insert(0, new CalculatorEntry(Expression, result));
            while (History.Count > HistorySize)
                History.RemoveAt(History.Count - 1);
            OnPropertyChanged(nameof(HasHistory));
            Expression = result;
            Result = result;
            IsError = false;
            _justEvaluated = true;
        }
        catch (Exception ex) when (ex is FormatException or ArithmeticException)
        {
            Result = ex.Message;
            IsError = true;
        }
    }

    /// <summary>Changes the sign of the last number of the expression (or of the result after <c>=</c>).</summary>
    private void ToggleSign()
    {
        var end = Expression.Length;
        var start = end;
        while (start > 0 && (char.IsDigit(Expression[start - 1]) || Expression[start - 1] == '.'))
            start--;
        if (start == end)
        {
            Expression += "-";
            return;
        }

        // The sign is a "-" (the subtraction key types "−"), so it is never taken for a subtraction.
        var hasSign = start > 0 && Expression[start - 1] == '-';
        Expression = hasSign
            ? Expression[..(start - 1)] + Expression[start..]
            : Expression[..start] + "-" + Expression[start..];
        _justEvaluated = false;
    }

    /// <inheritdoc />
    public string NoteWindowName => "Calculator";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        expression = Expression,
        result = Result,
        angles = AngleLabel,
        history = History.Take(10).ToList()
    };
}
