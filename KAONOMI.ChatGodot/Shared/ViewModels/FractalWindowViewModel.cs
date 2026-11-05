using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// /fractal: the fractal generator. Three families (<see cref="FractalFamily"/>): the escape-time fractals (a formula
/// iterated on the complex plane, drawn by the GPU: Mandelbrot, Julia, Burning Ship, Newton…), the L-systems (Koch,
/// dragon, Hilbert, plants…) and the IFS (Barnsley fern, Sierpinski…). The classic ones are given as examples
/// (<see cref="FractalExamples"/>); any of them is edited (formula, parameters, rules, maps, colours) and saved as the
/// user's own, a JSON file in ~/kaonomi/fractals. The view is zoomed and moved with the pointer; PNG saves a picture
/// in Pictures/kaonomi/fractal.
/// </summary>
public partial class FractalWindowViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The fractals of the user, a JSON file each.</summary>
    public static string DefaultDirectory => Path.Combine(ChatSettings.KaonomiHome, "fractals");

    /// <summary>Where PNG saves the pictures.</summary>
    public static string PicturesDirectory => Path.Combine(ChatSettings.SystemPicturesDirectory, "kaonomi", "fractal");

    /// <summary>The single precision of the GPU: below this half-height the escape-time pictures turn into blocks.</summary>
    public const double PrecisionLimit = 2e-5;

    private readonly string _directory;
    private bool _loading;

    /// <summary>True while the selection follows the editor (a fractal just saved): it is not loaded again.</summary>
    private bool _keepEditor;

    public FractalWindowViewModel() : this(null)
    {
    }

    /// <param name="directory">Where the fractals of the user are kept (~/kaonomi/fractals by default; another one for the tests).</param>
    public FractalWindowViewModel(string? directory)
    {
        _directory = directory ?? DefaultDirectory;
        LoadSaved();
        Selected = FractalExamples.All[0];
    }

    /// <summary>Raised when a whole fractal was loaded (an example, a saved one, a new one): the canvas draws it again.</summary>
    public event Action? Loaded;

    /// <summary>The families, for the drop-down.</summary>
    public static IReadOnlyList<FractalFamily> Families { get; } = [FractalFamily.EscapeTime, FractalFamily.LSystem, FractalFamily.Ifs];

    public static string FamilyName(FractalFamily family) => family switch
    {
        FractalFamily.EscapeTime => "Escape time (Mandelbrot, Julia…)",
        FractalFamily.LSystem => "L-system (Koch, dragon, plants…)",
        _ => "IFS (fern, Sierpinski…)"
    };

    public static IReadOnlyList<FractalColoring> Colorings { get; } = [FractalColoring.Escape, FractalColoring.Convergence];

    public static string ColoringName(FractalColoring coloring) =>
        coloring == FractalColoring.Escape ? "Escape (|z| > bailout)" : "Convergence (roots)";

    public static IReadOnlyList<string> PaletteNames { get; } = FractalPalette.All.Select(p => p.Name).ToList();

    /// <summary>The fractals saved by the user (every family).</summary>
    public ObservableCollection<FractalDefinition> Saved { get; } = [];

    /// <summary>The examples and the saved fractals of the current family, the examples first.</summary>
    public IReadOnlyList<FractalDefinition> Items =>
        FractalExamples.All.Where(d => d.Family == Family).Concat(Saved.Where(d => d.Family == Family)).ToList();

    /// <summary>The fractal chosen in the list (its settings are copied into the editor).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOwn))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private FractalDefinition? _selected;

    /// <summary>True when the selected fractal is one of the user's (it can be saved over and deleted).</summary>
    public bool IsOwn => Selected is { IsExample: false, FilePath: not null };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Items), nameof(IsEscapeTime))]
    private FractalFamily _family;

    public bool IsEscapeTime => Family == FractalFamily.EscapeTime;

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;

    // ── View and colours ──
    [ObservableProperty] private double _centerX;
    [ObservableProperty] private double _centerY;
    [ObservableProperty] private double _scale = 1.5;
    [ObservableProperty] private string _palette = "Rainbow";
    [ObservableProperty] private double _density = 1;
    [ObservableProperty] private double _offset;

    /// <summary>The colours go round the palette over time.</summary>
    [ObservableProperty] private bool _cycle;

    /// <summary>The samples per side of a pixel (1: none, 2: 4 samples, 3: 9) for the escape-time fractals.</summary>
    [ObservableProperty] private int _quality = 1;

    // ── Escape time ──
    [ObservableProperty] private string _formula = "z^2 + c";
    [ObservableProperty] private bool _julia;
    [ObservableProperty] private double _cx;
    [ObservableProperty] private double _cy;
    [ObservableProperty] private double _z0x;
    [ObservableProperty] private double _z0y;
    [ObservableProperty] private double _ax;
    [ObservableProperty] private double _ay;
    [ObservableProperty] private double _bx;
    [ObservableProperty] private double _by;
    [ObservableProperty] private int _iterations = 300;
    [ObservableProperty] private double _bailout = 64;
    [ObservableProperty] private double _degree = 2;
    [ObservableProperty] private FractalColoring _coloring;

    /// <summary>The formula translated for the shader (null while it has an error).</summary>
    [ObservableProperty] private string? _glsl = FractalFormula.Compile("z^2 + c").Glsl;

    /// <summary>What is wrong in the formula (empty: nothing).</summary>
    [ObservableProperty] private string _formulaError = string.Empty;

    // ── L-system ──
    [ObservableProperty] private string _axiom = "F";
    [ObservableProperty] private string _rules = string.Empty;
    [ObservableProperty] private double _angle = 90;
    [ObservableProperty] private int _depth = 4;
    [ObservableProperty] private double _heading;
    [ObservableProperty] private string _drawSymbols = "FGAB";

    // ── IFS ──
    [ObservableProperty] private string _maps = string.Empty;
    [ObservableProperty] private int _points = 400_000;

    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private bool _isError;

    partial void OnSelectedChanged(FractalDefinition? value)
    {
        if (value is not null && !_keepEditor)
            Load(value);
    }

    partial void OnFamilyChanged(FractalFamily value)
    {
        // Another family chosen in the drop-down: its first fractal.
        if (!_loading && Selected?.Family != value)
            Selected = Items.FirstOrDefault();
    }

    partial void OnFormulaChanged(string value) => CompileFormula();

    partial void OnScaleChanged(double value)
    {
        if (!_loading && IsEscapeTime && value < PrecisionLimit)
            Say("Zoomed to the limit of the GPU (single precision): the picture turns into blocks below this", false);
    }

    private void CompileFormula()
    {
        var result = FractalFormula.Compile(Formula);
        if (result.Ok)
        {
            FormulaError = string.Empty;
            Glsl = result.Glsl;
        }
        else
        {
            FormulaError = result.Position >= 0 ? $"{result.Error} (at {result.Position + 1})" : result.Error ?? "Error";
        }
    }

    /// <summary>Copies <paramref name="definition"/> into the editor.</summary>
    public void Load(FractalDefinition definition)
    {
        _loading = true;
        try
        {
            Family = definition.Family;
            Name = definition.Name;
            Description = definition.Description;
            CenterX = definition.CenterX;
            CenterY = definition.CenterY;
            Scale = definition.Scale > 0 ? definition.Scale : 1.5;
            Palette = FractalPalette.Find(definition.Palette).Name;
            Density = definition.Density;
            Offset = definition.Offset;
            Formula = definition.Formula;
            Julia = definition.Julia;
            Cx = definition.Cx;
            Cy = definition.Cy;
            Z0x = definition.Z0x;
            Z0y = definition.Z0y;
            Ax = definition.Ax;
            Ay = definition.Ay;
            Bx = definition.Bx;
            By = definition.By;
            Iterations = Math.Clamp(definition.Iterations, 1, 20000);
            Bailout = definition.Bailout;
            Degree = definition.Degree;
            Coloring = definition.Coloring;
            Axiom = definition.Axiom;
            Rules = definition.Rules;
            Angle = definition.Angle;
            Depth = definition.Depth;
            Heading = definition.Heading;
            DrawSymbols = definition.DrawSymbols;
            Maps = definition.Maps;
            Points = definition.Points;
            CompileFormula();
        }
        finally
        {
            _loading = false;
        }
        Say(definition.IsExample ? $"Example: {definition.Name}" : $"Yours: {definition.Name}", false);
        Loaded?.Invoke();
    }

    /// <summary>The fractal as it is now in the editor.</summary>
    public FractalDefinition Current() => new()
    {
        Name = Name,
        Description = Description,
        Family = Family,
        CenterX = CenterX,
        CenterY = CenterY,
        Scale = Scale,
        Palette = Palette,
        Density = Density,
        Offset = Offset,
        Formula = Formula,
        Julia = Julia,
        Cx = Cx,
        Cy = Cy,
        Z0x = Z0x,
        Z0y = Z0y,
        Ax = Ax,
        Ay = Ay,
        Bx = Bx,
        By = By,
        Iterations = Iterations,
        Bailout = Bailout,
        Degree = Degree,
        Coloring = Coloring,
        Axiom = Axiom,
        Rules = Rules,
        Angle = Angle,
        Depth = Depth,
        Heading = Heading,
        DrawSymbols = DrawSymbols,
        Maps = Maps,
        Points = Points
    };

    // ── The view ──

    /// <summary>Zooms by <paramref name="factor"/> (above 1: in) keeping the point (<paramref name="x"/>, <paramref name="y"/>) of the plane under the pointer.</summary>
    public void ZoomAt(double x, double y, double factor)
    {
        if (factor <= 0)
            return;
        var scale = Math.Clamp(Scale / factor, 1e-12, 1e6);
        var applied = Scale / scale;
        CenterX = x + (CenterX - x) / applied;
        CenterY = y + (CenterY - y) / applied;
        Scale = scale;
    }

    /// <summary>The step of ZOOM + and ZOOM − (and of the keys + and −).</summary>
    public const double ZoomStep = 2;

    /// <summary>ZOOM +: twice as close, around the centre of the view.</summary>
    [RelayCommand]
    private void ZoomIn() => ZoomAt(CenterX, CenterY, ZoomStep);

    /// <summary>ZOOM −: twice as far, around the centre of the view.</summary>
    [RelayCommand]
    private void ZoomOut() => ZoomAt(CenterX, CenterY, 1 / ZoomStep);

    /// <summary>
    /// Zooms on the square of the plane centred on (<paramref name="x"/>, <paramref name="y"/>) and
    /// <paramref name="half"/> wide on each side: it fills the height of the view.
    /// </summary>
    public void ZoomToSquare(double x, double y, double half)
    {
        if (!(half > 0) || double.IsNaN(x) || double.IsNaN(y))
            return;
        CenterX = x;
        CenterY = y;
        Scale = Math.Clamp(half, 1e-12, 1e6);
    }

    /// <summary>Moves the view by (<paramref name="dx"/>, <paramref name="dy"/>) in the units of the plane.</summary>
    public void Pan(double dx, double dy)
    {
        CenterX += dx;
        CenterY += dy;
    }

    [RelayCommand]
    private void ResetView()
    {
        // The view of the selected fractal, unless the picture is no longer it (a Julia set opened by a right click).
        var origin = Selected is { } s && s.Family == Family && (s.Family != FractalFamily.EscapeTime || s.Julia == Julia) ? s : null;
        CenterX = origin?.CenterX ?? 0;
        CenterY = origin?.CenterY ?? 0;
        Scale = origin?.Scale ?? (Family == FractalFamily.EscapeTime ? 1.5 : 1.08);
        Say("View reset", false);
    }

    /// <summary>The Julia set of the point (<paramref name="x"/>, <paramref name="y"/>): c fixed there, z starting at the pixel.</summary>
    public void JuliaAt(double x, double y)
    {
        if (!IsEscapeTime)
            return;
        Julia = true;
        Cx = Math.Round(x, 6);
        Cy = Math.Round(y, 6);
        CenterX = 0;
        CenterY = 0;
        Scale = 1.5;
        Say($"Julia set of c = {Complex(Cx, Cy)} (Mandelbrot: untick Julia)", false);
    }

    /// <summary>A Julia set of z² + c with c on the border of the main cardioid of the Mandelbrot set (always connected, often spiralled).</summary>
    [RelayCommand]
    private void RandomJulia()
    {
        var random = Random.Shared;
        var t = random.NextDouble() * 2 * Math.PI;
        var r = 0.98 + random.NextDouble() * 0.04;
        // c = e^{it}/2 - e^{2it}/4: the border of the main cardioid.
        var x = r * (Math.Cos(t) / 2 - Math.Cos(2 * t) / 4);
        var y = r * (Math.Sin(t) / 2 - Math.Sin(2 * t) / 4);
        Selected = null;
        _loading = true;
        Family = FractalFamily.EscapeTime;
        _loading = false;
        Formula = "z^2 + c";
        Coloring = FractalColoring.Escape;
        Degree = 2;
        Bailout = 64;
        Iterations = Math.Max(Iterations, 400);
        Name = "Random Julia";
        Description = "A Julia set of z² + c, c near the border of the main cardioid of the Mandelbrot set.";
        JuliaAt(x, y);
        Loaded?.Invoke();
    }

    /// <summary>A blank fractal of the current family to start one's own: a simple formula, rule or set of maps.</summary>
    [RelayCommand]
    private void New()
    {
        var blank = Family switch
        {
            FractalFamily.EscapeTime => new FractalDefinition
            {
                Family = FractalFamily.EscapeTime, Name = "My fractal", Formula = "z^2 + c", CenterX = -0.5, Scale = 1.4,
                Description = "Write the next z in the formula (z^3 + c, sin(z) + c, fold(z)^2 + c…)."
            },
            FractalFamily.LSystem => new FractalDefinition
            {
                Family = FractalFamily.LSystem, Name = "My L-system", Axiom = "F", Rules = "F=F+F-F-F+F", Angle = 90, Depth = 3, Scale = 1.08, Palette = "EVE",
                Description = "Rewrite the axiom with the rules; F draws, + and - turn, [ and ] keep and take back the place."
            },
            _ => new FractalDefinition
            {
                Family = FractalFamily.Ifs, Name = "My IFS", Scale = 1.08, Palette = "Lagoon",
                Maps = "# a b c d e f [p]: x' = a x + b y + e, y' = c x + d y + f\n0.5 0 0 0.5 0 0\n0.5 0 0 0.5 0.5 0\n0.5 0 0 0.5 0.25 0.5",
                Description = "One affine map per line; each must shrink. The chaos game draws what they keep."
            }
        };
        _loading = true;
        Selected = null;
        _loading = false;
        Load(blank);
        Say("New fractal: change it, then SAVE AS", false);
    }

    // ── Saving ──

    /// <summary>Saves the editor as <paramref name="name"/> (a file of the same name is replaced); null when it failed.</summary>
    public FractalDefinition? SaveAs(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
        {
            Say("A name is needed", true);
            return null;
        }
        var definition = Current();
        definition.Name = name;
        var path = Path.Combine(_directory, Slug(name) + ".json");
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(path, JsonSerializer.Serialize(definition, JsonOptions), Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say($"Not saved: {ex.Message}", true);
            return null;
        }
        definition.FilePath = path;
        if (Saved.FirstOrDefault(d => string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase)) is { } old)
            Saved.Remove(old);
        Saved.Add(definition);
        OnPropertyChanged(nameof(Items));
        _keepEditor = true;
        Selected = definition;
        _keepEditor = false;
        Name = name;
        OnPropertyChanged(nameof(IsOwn));
        DeleteCommand.NotifyCanExecuteChanged();
        Say($"Saved: {path}", false);
        return definition;
    }

    /// <summary>Saves over the selected fractal of the user; false when it is an example (SAVE AS then).</summary>
    public bool SaveOver() => IsOwn && SaveAs(Selected!.Name) is not null;

    [RelayCommand(CanExecute = nameof(IsOwn))]
    private void Delete()
    {
        if (Selected is not { FilePath: { } path } definition)
            return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say($"Not deleted: {ex.Message}", true);
            return;
        }
        Saved.Remove(definition);
        OnPropertyChanged(nameof(Items));
        Selected = Items.FirstOrDefault();
        Say($"Deleted: {definition.Name}", false);
    }

    private void LoadSaved()
    {
        if (!Directory.Exists(_directory))
            return;
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (JsonSerializer.Deserialize<FractalDefinition>(File.ReadAllText(file), JsonOptions) is { } definition)
                {
                    definition.FilePath = file;
                    definition.IsExample = false;
                    Saved.Add(definition);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                ActivityLog.Error("Fractal", $"{file} could not be read", ex);
            }
        }
    }

    /// <summary>A file name made of the letters and digits of <paramref name="name"/>.</summary>
    public static string Slug(string name)
    {
        var slug = new StringBuilder();
        foreach (var ch in name.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(ch) && ch < 128)
                slug.Append(char.ToLowerInvariant(ch));
            else if ((ch == ' ' || ch == '-' || ch == '_') && slug.Length > 0 && slug[^1] != '-')
                slug.Append('-');
        }
        var text = slug.ToString().Trim('-');
        return text.Length == 0 ? "fractal" : text;
    }

    /// <summary>A complex number as text: -0.123 + 0.745i.</summary>
    public static string Complex(double x, double y) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{x:0.######} {(y < 0 ? "-" : "+")} {Math.Abs(y):0.######}i");

    public void Say(string message, bool error)
    {
        Status = message;
        IsError = error;
    }
}
