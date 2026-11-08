using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Chat;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Media;

/// <summary>
/// /fractal: the fractal generator. Header: the family (escape time, L-system, IFS), NEW, SAVE, SAVE AS, DELETE, ZOOM +
/// and ZOOM − (×2 around the centre), ⬚ ZOOM BOX (a drag draws the square to zoom on), RESET VIEW, RANDOM JULIA, PNG.
/// Left: the classic examples and the user's own fractals (★), the description, the editor of the family (formula and
/// parameters; axiom, rules and angle; affine maps), the colours and the help of the syntax. Right: the picture
/// (<see cref="FractalCanvas"/>, computed on the CPU), the coordinates under the pointer and the zoom.
/// </summary>
[ToolView(typeof(FractalWindowViewModel), "Fractals", "❄", 1280, 860)]
public sealed class FractalView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (FractalWindowViewModel)viewModel;
        var canvas = new FractalCanvas(vm);

        // ── Header ──
        var family = Kit.Options(FractalWindowViewModel.Families, vm, nameof(vm.Family), FractalWindowViewModel.FamilyName, "The kind of fractal");
        family.Width = 270;
        var random = Ui.Command("🎲 RANDOM JULIA", vm.RandomJuliaCommand, tooltip: "A Julia set of z² + c, c taken at random on the border of the main cardioid of the Mandelbrot set")
            .Visible(vm, nameof(vm.IsEscapeTime));
        var box = new ToggleButton { Content = "⬚ ZOOM BOX" };
        ToolTip.SetTip(box, "Draw a square on the picture to zoom on it (⇧ + drag does it too) · Esc: cancel");
        box.IsCheckedChanged += (_, _) => canvas.BoxMode = box.IsChecked == true;
        var header = Kit.Toolbar(Kit.Flow(8,
            Ui.Label("Fractals", Ui.TextBright, 14, bold: true),
            family,
            Ui.Command("✚ NEW", vm.NewCommand, tooltip: "Start a fractal of your own in this family, from a simple one"),
            Ui.Button("💾 SAVE", () => _ = SaveAsync(vm, false), $"Save your fractal (an example: under a new name) in {FractalWindowViewModel.DefaultDirectory}", primary: true),
            Ui.Button("SAVE AS…", () => _ = SaveAsync(vm, true), "Save under a new name"),
            Ui.Button("🗑 DELETE", () => _ = DeleteAsync(vm), "Delete this fractal of yours (the examples stay)"),
            Ui.Command("ZOOM +", vm.ZoomInCommand, tooltip: "Zoom in ×2 around the centre of the picture (key +)"),
            Ui.Command("ZOOM −", vm.ZoomOutCommand, tooltip: "Zoom out ×2 around the centre of the picture (key −)"),
            box,
            Ui.Command("⟲ RESET VIEW", vm.ResetViewCommand, tooltip: "Back to the view the fractal was saved with (R)"),
            random,
            Ui.Button("🖼 PNG", () => _ = SavePngAsync(vm, canvas), $"Save the picture as a PNG, in {FractalWindowViewModel.PicturesDirectory}")));

        // ── Left: examples, editor, colours, help ──
        var description = Kit.Lines(vm, nameof(vm.Description), 56, "What it is, how it is made");
        description.FontSize = 12;
        var name = Ui.Edit(vm, nameof(vm.Name), "Name of the fractal");

        var editor = Kit.Swap(vm, nameof(vm.Family), value => value switch
        {
            FractalFamily.LSystem => LSystemEditor(vm),
            FractalFamily.Ifs => IfsEditor(vm),
            _ => EscapeEditor(vm)
        });

        var left = Ui.Column(10,
            Ui.Caption("EXAMPLES · ★ YOURS", Ui.Warm, 11),
            ExampleList(vm),
            Kit.Field("Name", name),
            Kit.Field("Description", description),
            new Separator(),
            editor,
            new Separator(),
            Colours(vm),
            new Separator(),
            Help(vm));
        var leftScroll = Ui.Scroll(Kit.Margin(left, 10, 8, 14, 10));

        // ── Right: the picture ──
        var frame = new Border { Child = canvas, BorderBrush = Ui.Edge, BorderThickness = new Thickness(1) };

        var pointer = Ui.Label(string.Empty, Ui.Text, 11);
        pointer.FontFamily = Ui.Mono;
        canvas.PointerMovedOnPlane += at => pointer.Text = at is { } p ? FractalWindowViewModel.Complex(p.X, p.Y) : string.Empty;
        var zoom = Ui.Label(string.Empty, Ui.Accent, 11);
        zoom.FontFamily = Ui.Mono;
        Ui.When(zoom, vm, () =>
        {
            var factor = 1.5 / vm.Scale;
            zoom.Text = string.Create(CultureInfo.InvariantCulture,
                $"centre {FractalWindowViewModel.Complex(vm.CenterX, vm.CenterY)} · zoom ×{(factor < 10000 ? factor.ToString("0.##", CultureInfo.InvariantCulture) : factor.ToString("0.##e+0", CultureInfo.InvariantCulture))}");
        }, nameof(vm.CenterX), nameof(vm.CenterY), nameof(vm.Scale));
        var gestures = Ui.Label("⇧ drag (or ⬚): zoom on a square · wheel or pinch: zoom · drag: move · double click: zoom in (⇧ out) · right click: Julia set of the point · R: reset", Ui.Muted, 11);
        ToolTip.SetTip(gestures, gestures.Text);
        var info = Kit.FillRow(12, [pointer], gestures, zoom);
        info.Margin = new Thickness(4, 4, 4, 0);

        var right = Kit.Rows("*,Auto", frame, info);
        var split = Kit.SplitH(leftScroll, Kit.Margin(right, 6, 8, 8, 6), "400", "*", 380, 320);
        var status = Kit.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError));
        var page = Kit.Rows("Auto,*,Auto", header, split, status);
        Kit.FocusWhenShown(canvas);
        return page;
    }

    /// <summary>
    /// The examples and the user's fractals (★, warm colour) of the family, filled again when the family changes or a
    /// fractal is saved or deleted; choosing one loads it.
    /// </summary>
    private static ListBox ExampleList(FractalWindowViewModel vm)
    {
        var list = new ListBox { Height = 210 };
        var shown = new List<FractalDefinition>();
        var updating = false;
        void Select()
        {
            updating = true;
            var index = vm.Selected is null ? -1 : shown.IndexOf(vm.Selected);
            list.SelectedIndex = index;
            if (index >= 0)
                list.ScrollIntoView(index);
            updating = false;
        }
        void Fill()
        {
            updating = true;
            shown = [.. vm.Items];
            var items = new List<Control>();
            foreach (var d in shown)
            {
                var label = Ui.Label(d.IsExample ? d.Name : "★ " + d.Name, d.IsExample ? Ui.Text : Ui.Warm, 13);
                if (!string.IsNullOrEmpty(d.Description))
                    ToolTip.SetTip(label, d.Description);
                items.Add(label);
            }
            list.ItemsSource = items;
            updating = false;
            Select();
        }
        Ui.When(list, vm, Fill, nameof(vm.Items), nameof(vm.Family));
        Ui.When(list, vm, Select, nameof(vm.Selected));
        list.SelectionChanged += (_, _) =>
        {
            var index = list.SelectedIndex;
            if (!updating && index >= 0 && index < shown.Count)
                vm.Selected = shown[index];
        };
        return list;
    }

    // ── The editors ──

    private static Control EscapeEditor(FractalWindowViewModel vm)
    {
        var formula = Ui.Edit(vm, nameof(vm.Formula), "z^2 + c");
        formula.FontFamily = Ui.Mono;
        ToolTip.SetTip(formula, "The next z, from z, c, zp (the z before), a, b, p (the pixel) and n (the iteration)");
        var error = Ui.Label(string.Empty, Ui.Red, 11, wrap: true);
        Ui.When(error, vm, () =>
        {
            error.Text = vm.FormulaError;
            error.IsVisible = vm.FormulaError.Length > 0;
        }, nameof(vm.FormulaError));

        var julia = Kit.Check("Julia: z starts at the pixel, c is fixed", vm, nameof(vm.Julia),
            "Ticked: a Julia set (z₀ = the pixel, c below) · unticked: a Mandelbrot-like set (c = the pixel, z starts at z₀)");
        var coloring = Kit.Options(FractalWindowViewModel.Colorings, vm, nameof(vm.Coloring), FractalWindowViewModel.ColoringName,
            "Escape: by how fast |z| passes the bailout · Convergence: by the root z settles on (Newton)");

        var numbers = Kit.Cells(2, 10, 6,
            Kit.Field("Iterations", Number(vm, nameof(vm.Iterations), 1, FractalCpu.MaxIterations, 10)),
            Kit.Field("Bailout |z|", Number(vm, nameof(vm.Bailout), 1.0001, 1e9, 1)),
            Kit.Field("Degree (smoothing)", Number(vm, nameof(vm.Degree), 1.0001, 64, 0.5)),
            Kit.Field("Colouring", coloring));

        return Ui.Column(8,
            Ui.Caption("FORMULA · next z =", Ui.Warm, 11),
            formula,
            error,
            julia,
            Pair("c (Julia)", vm, nameof(vm.Cx), nameof(vm.Cy)),
            Pair("z₀ (start of z, Mandelbrot)", vm, nameof(vm.Z0x), nameof(vm.Z0y)),
            Pair("a (parameter)", vm, nameof(vm.Ax), nameof(vm.Ay)),
            Pair("b (parameter)", vm, nameof(vm.Bx), nameof(vm.By)),
            numbers);
    }

    private static Control LSystemEditor(FractalWindowViewModel vm)
    {
        var axiom = Ui.Edit(vm, nameof(vm.Axiom), "F--F--F");
        axiom.FontFamily = Ui.Mono;
        var rules = Kit.Lines(vm, nameof(vm.Rules), 110, "F=F+F--F+F", mono: true);
        ToolTip.SetTip(rules, "One rule per line: a letter, = and what replaces it at each rewrite");
        var draw = Ui.Edit(vm, nameof(vm.DrawSymbols), "FGAB");
        draw.FontFamily = Ui.Mono;
        ToolTip.SetTip(draw, "The letters that draw a line forward (the others only rewrite)");
        var numbers = Kit.Cells(3, 10, 6,
            Kit.Field("Angle °", Number(vm, nameof(vm.Angle), -360, 360, 0.5)),
            Kit.Field("Depth", Number(vm, nameof(vm.Depth), 0, 20, 1)),
            Kit.Field("Heading °", Number(vm, nameof(vm.Heading), -360, 360, 1)));
        return Ui.Column(8,
            Ui.Caption("L-SYSTEM", Ui.Warm, 11),
            Kit.Field("Axiom", axiom),
            Kit.Field("Rules", rules),
            numbers,
            Kit.Field("Draw symbols", draw));
    }

    private static Control IfsEditor(FractalWindowViewModel vm)
    {
        var maps = Kit.Lines(vm, nameof(vm.Maps), 190, "a b c d e f [p]", mono: true);
        maps.TextWrapping = TextWrapping.NoWrap;
        ToolTip.SetTip(maps, "One affine map per line: x' = a x + b y + e, y' = c x + d y + f, chosen with the weight p");
        return Ui.Column(8,
            Ui.Caption("IFS · AFFINE MAPS", Ui.Warm, 11),
            Kit.Field("Maps: a b c d e f [p]", maps),
            Kit.Field("Points", Number(vm, nameof(vm.Points), 1000, FractalIfs.MaxPoints, 50_000)));
    }

    private static Control Colours(FractalWindowViewModel vm)
    {
        var palette = Kit.Options(FractalWindowViewModel.PaletteNames, vm, nameof(vm.Palette));
        var density = Kit.Slider(vm, nameof(vm.Density), 0.05, 8, 0.05, "How fast the colours go round the palette");
        var offset = Kit.Slider(vm, nameof(vm.Offset), 0, 1, 0.005, "Where the palette starts");
        var cycle = Kit.Check("Cycle the colours", vm, nameof(vm.Cycle), "The colours go round the palette over time (escape time)").Visible(vm, nameof(vm.IsEscapeTime));
        var quality = Kit.Options(new[] { 1, 2, 3 }, vm, nameof(vm.Quality), q => q switch { 1 => "1 sample", 2 => "4 samples", _ => "9 samples" },
            "Samples per pixel of the escape-time fractals: smoother edges, slower");
        var qualityField = Kit.Field("Smoothing", quality).Visible(vm, nameof(vm.IsEscapeTime));
        return Ui.Column(8,
            Ui.Caption("COLOURS", Ui.Warm, 11),
            Kit.Cells(2, 10, 6, Kit.Field("Palette", palette), qualityField),
            Kit.Field("Density", density),
            Kit.Field("Offset", offset),
            cycle);
    }

    private static Control Help(FractalWindowViewModel vm)
    {
        var help = Kit.Swap(vm, nameof(vm.Family), family => MarkdownView.Build(family switch
        {
            FractalFamily.LSystem => LSystemHelp,
            FractalFamily.Ifs => IfsHelp,
            _ => EscapeHelp
        }, 12));
        return Ui.Column(6, Ui.Caption("MAKE YOUR OWN", Ui.Warm, 11), help);
    }

    private static readonly string EscapeHelp =
        "Each pixel iterates **next z = formula**. Unticked Julia: c is the pixel and z starts at z₀ (a Mandelbrot-like map of every Julia set). "
        + "Ticked: z starts at the pixel and c is fixed. A pixel is coloured by how fast |z| passes the bailout, or (Convergence) by the root z settles on.\n\n"
        + FractalFormula.Help() + "\n"
        + "**Ideas** · `z^4 + c` · `fold(z)^3 + c` · `sin(z) + c` · `z^2 + c + a*zp` (a = -0.5: Phoenix) · `z - (z^4 - 1)/(4*z^3)` with Convergence · `cosh(z) + c` · `z^2 + c/z`\n\n"
        + "Right click on a Mandelbrot-like picture opens the Julia set of that point. The picture is computed on the processor in double precision: "
        + "a quick picture while the view moves, the full one when it stops; deep zooms (beyond about ×10¹³) turn into blocks.";

    private const string LSystemHelp =
        "The **axiom** is rewritten **depth** times: each letter that has a **rule** is replaced by its right side, all at once. "
        + "Then a turtle reads the string:\n\n"
        + "- a **draw symbol** (F, G, A, B by default): a line forward\n"
        + "- **f**, **g**: forward without drawing\n"
        + "- **+** / **-**: turn left / right by the angle · **|**: turn back\n"
        + "- **[** / **]**: keep / take back the place and the direction (branches)\n"
        + "- any other letter (X, Y…): nothing, it only rewrites\n\n"
        + "**Ideas** · `F` with `F=F-F++F-F` at 60° · `X` with `X=F[+X][-X]FX` and `F=FF` at 25.7° · `F+F+F+F` with `F=FF+F+F+F+FF` at 90°\n\n"
        + "Each rewrite multiplies the length: keep the depth low for long rules (the drawing stops at 1.5 million lines).";

    private const string IfsHelp =
        "An **iterated function system**: affine maps, each one **x' = a x + b y + e, y' = c x + d y + f**. The chaos game starts anywhere, picks a map at random "
        + "(with its weight **p**, by default its area |a d - b c|), moves the point, and again: the points settle on the only shape made of shrunken copies of itself.\n\n"
        + "- a = d = s, b = c = 0: shrink by s · then e, f: move\n"
        + "- a = s cos θ, b = -s sin θ, c = s sin θ, d = s cos θ: shrink by s and turn by θ\n"
        + "- every map must shrink, or the points run away\n\n"
        + "Each point takes the colour of the map that placed it; the brightness is the density. # starts a comment.";

    /// <summary>A number box bound to a number property: the arrows move by <paramref name="step"/>, what is typed is kept (4 decimals; whole numbers for the integer properties).</summary>
    private static NumericUpDown Number(FractalWindowViewModel vm, string property, double min, double max, double step)
    {
        var whole = typeof(FractalWindowViewModel).GetProperty(property)?.PropertyType == typeof(int);
        var number = Ui.Number(vm, property, min, max, step);
        number.FormatString = whole ? "0" : "0.####";
        number.HorizontalAlignment = HorizontalAlignment.Stretch;
        return number;
    }

    /// <summary>A complex number: its real and imaginary parts.</summary>
    private static Control Pair(string caption, FractalWindowViewModel vm, string re, string im)
    {
        NumericUpDown Part(string property) => Number(vm, property, -1e6, 1e6, 0.001);
        return Kit.Field(caption, Kit.Columns("*,*,Auto", 6, Part(re), Part(im), Ui.Label("i", Ui.Muted, 13)));
    }

    // ── Saving ──

    private static async Task SaveAsync(FractalWindowViewModel vm, bool asNew)
    {
        if (!asNew && vm.SaveOver())
            return;
        var suggested = vm.IsOwn || vm.Selected is null ? vm.Name : vm.Name + " (mine)";
        var name = await Dialogs.Ask("Save the fractal", $"Its name (a file in {FractalWindowViewModel.DefaultDirectory}):", suggested);
        if (!string.IsNullOrWhiteSpace(name))
            vm.SaveAs(name);
    }

    private static async Task DeleteAsync(FractalWindowViewModel vm)
    {
        if (!vm.IsOwn)
        {
            vm.Say("The examples cannot be deleted: only your fractals (★)", true);
            return;
        }
        if (await Dialogs.Confirm("Delete the fractal?", $"\"{vm.Selected!.Name}\" will be deleted ({vm.Selected.FilePath}).", "Delete", "Cancel"))
            vm.DeleteCommand.Execute(null);
    }

    private static async Task SavePngAsync(FractalWindowViewModel vm, FractalCanvas canvas)
    {
        var folder = FractalWindowViewModel.PicturesDirectory;
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            vm.Say($"The folder {folder} could not be made: {ex.Message}", true);
            return;
        }
        var suggested = $"{FractalWindowViewModel.Slug(vm.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.png";
        var path = await Dialogs.SaveFile("Save the fractal as a picture", suggested, ["*.png ; PNG picture"], folder);
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            path += ".png";
        var bitmap = await canvas.RenderFullAsync();
        if (bitmap is null)
        {
            vm.Say($"The picture could not be saved in {path}: it is not drawn yet", true);
            return;
        }
        try
        {
#pragma warning disable CS0618 // The PNG encoder by default.
            bitmap.Save(path);
#pragma warning restore CS0618
            vm.Say($"Picture saved: {path} ({bitmap.PixelSize.Width} × {bitmap.PixelSize.Height})", false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            vm.Say($"The picture could not be saved in {path}: {ex.Message}", true);
        }
    }
}
