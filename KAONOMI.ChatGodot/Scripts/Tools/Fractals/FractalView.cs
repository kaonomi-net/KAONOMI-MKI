using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Fractals;

/// <summary>
/// /fractal: the fractal generator. Header: the family (escape time, L-system, IFS), NEW, SAVE, SAVE AS, DELETE,
/// ZOOM + and ZOOM − (×2 around the centre), ⬚ ZOOM BOX (a drag draws the square to zoom on), RESET VIEW, RANDOM JULIA, PNG. Left: the classic examples and the user's own fractals (★), the description, the
/// editor of the family (formula and parameters; axiom, rules and angle; affine maps), the colours and the help of the
/// syntax. Right: the picture (<see cref="FractalCanvas"/>), the coordinates under the pointer and the zoom.
/// </summary>
[ToolView(typeof(FractalWindowViewModel), "Fractals", "❄", 1280, 860)]
public sealed class FractalView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (FractalWindowViewModel)viewModel;
        var canvas = new FractalCanvas(vm);

        // ── Header ──
        var family = Bind.Options(new OptionButton { TooltipText = "The kind of fractal", CustomMinimumSize = new Vector2(270, 0) },
            FractalWindowViewModel.Families, vm, nameof(vm.Family), FractalWindowViewModel.FamilyName);
        var random = Ui.Command("🎲 RANDOM JULIA", vm.RandomJuliaCommand, tooltip: "A Julia set of z² + c, c taken at random on the border of the main cardioid of the Mandelbrot set");
        Bind.Visible(random, vm, nameof(vm.IsEscapeTime));
        var header = Ui.Toolbar(Ui.Row(8,
            Eve.Eve.Caption("Fractals", Eve.Eve.TextBright, 14),
            family,
            Ui.Spring(),
            Ui.Command("✚ NEW", vm.NewCommand, tooltip: "Start a fractal of your own in this family, from a simple one"),
            Ui.Action("💾 SAVE", () => _ = SaveAsync(vm, false), primary: true, tooltip: $"Save your fractal (an example: under a new name) in {FractalWindowViewModel.DefaultDirectory}"),
            Ui.Action("SAVE AS…", () => _ = SaveAsync(vm, true), tooltip: "Save under a new name"),
            Ui.Action("🗑 DELETE", () => _ = DeleteAsync(vm), tooltip: "Delete this fractal of yours (the examples stay)"),
            Ui.Command("ZOOM +", vm.ZoomInCommand, tooltip: "Zoom in ×2 around the centre of the picture (key +)"),
            Ui.Command("ZOOM −", vm.ZoomOutCommand, tooltip: "Zoom out ×2 around the centre of the picture (key −)"),
            BoxToggle(canvas),
            Ui.Command("⟲ RESET VIEW", vm.ResetViewCommand, tooltip: "Back to the view the fractal was saved with (R)"),
            random,
            Ui.Action("🖼 PNG", () => _ = SavePngAsync(vm, canvas), tooltip: $"Save the picture as a PNG, in {FractalWindowViewModel.PicturesDirectory}")));

        // ── Left: examples, editor, colours, help ──
        var list = ExampleList(vm);

        var description = Ui.Lines(vm, nameof(vm.Description), 56, "What it is, how it is made");
        description.AddThemeFontSizeOverride("font_size", 12);
        var name = Ui.Line(vm, nameof(vm.Name), "Name of the fractal");

        var editor = Ui.Swap(vm, nameof(vm.Family), value => value switch
        {
            FractalFamily.LSystem => LSystemEditor(vm),
            FractalFamily.Ifs => IfsEditor(vm),
            _ => EscapeEditor(vm)
        });

        var left = Ui.Column(10,
            Eve.Eve.Caption("EXAMPLES · ★ YOURS", Eve.Eve.Amber, 11),
            list,
            Ui.Field("Name", name),
            Ui.Field("Description", description),
            Eve.Eve.Separator(),
            editor,
            Eve.Eve.Separator(),
            Colours(vm),
            Eve.Eve.Separator(),
            Help(vm));
        var leftScroll = Ui.Scroll(Ui.Margin(left, 10, 8, 14, 10));
        leftScroll.CustomMinimumSize = new Vector2(380, 0);
        // Only the picture takes the room left: the split starts with the editor at its width.
        leftScroll.SizeFlagsHorizontal = Control.SizeFlags.Fill;

        // ── Right: the picture ──
        var frame = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        frame.AddThemeStyleboxOverride("panel", Eve.Eve.Box(new Color(0.02f, 0.03f, 0.045f), Eve.Eve.Edge with { A = 0.45f }, 0));
        frame.AddChild(canvas);

        var pointer = Eve.Eve.Text_("", Eve.Eve.Text, 11);
        pointer.AddThemeFontOverride("font", Eve.Eve.Mono);
        canvas.PointerMoved += at => pointer.Text = at is { } p ? FractalWindowViewModel.Complex(p.X, p.Y) : string.Empty;
        var zoom = Eve.Eve.Text_("", Eve.Eve.Cyan, 11);
        zoom.AddThemeFontOverride("font", Eve.Eve.Mono);
        Bind.When(zoom, vm, () => zoom.Text = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"centre {FractalWindowViewModel.Complex(vm.CenterX, vm.CenterY)} · zoom ×{(1.5 / vm.Scale < 10000 ? (1.5 / vm.Scale).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : (1.5 / vm.Scale).ToString("0.##e+0", System.Globalization.CultureInfo.InvariantCulture))}"), nameof(vm.CenterX), nameof(vm.CenterY), nameof(vm.Scale));
        var gestures = Eve.Eve.Text_("⇧ drag (or ⬚): zoom on a square · wheel or pinch: zoom · drag: move · double click: zoom in (⇧ out) · right click: Julia set of the point · R: reset", Eve.Eve.Muted, 11);
        gestures.ClipText = true;
        gestures.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var info = Ui.Row(12, pointer, Ui.Fill(gestures), zoom);

        var right = Ui.Column(4, Ui.FillHeight(frame), Ui.Margin(info, 4, 2, 4, 0));
        right.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var split = Ui.SplitH(leftScroll, Ui.Margin(right, 6, 8, 8, 6), 0);
        var status = Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError));
        var page = Ui.Column(0, header, Ui.FillHeight(split), status);
        page.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        page.Ready += () => Callable.From(() => canvas.GrabFocus()).CallDeferred();
        return page;
    }

    /// <summary>
    /// The examples and the user's fractals (★, amber) of the family, filled again when the family changes or a
    /// fractal is saved or deleted; choosing one loads it.
    /// </summary>
    private static ItemList ExampleList(FractalWindowViewModel vm)
    {
        var list = new ItemList { CustomMinimumSize = new Vector2(0, 210), SelectMode = ItemList.SelectModeEnum.Single };
        var shown = new System.Collections.Generic.List<FractalDefinition>();
        var updating = false;
        void Select()
        {
            updating = true;
            var index = shown.IndexOf(vm.Selected!);
            if (index >= 0)
            {
                list.Select(index);
                list.EnsureCurrentIsVisible();
            }
            else
                list.DeselectAll();
            updating = false;
        }
        void Fill()
        {
            if (!GodotObject.IsInstanceValid(list))
                return;
            updating = true;
            list.Clear();
            shown = [.. vm.Items];
            for (var index = 0; index < shown.Count; index++)
            {
                var d = shown[index];
                list.AddItem(d.IsExample ? d.Name : "★ " + d.Name);
                list.SetItemTooltip(index, d.Description);
                if (!d.IsExample)
                    list.SetItemCustomFgColor(index, Eve.Eve.Amber);
            }
            updating = false;
            Select();
        }
        Bind.When(list, vm, Fill, nameof(vm.Items), nameof(vm.Family));
        Bind.When(list, vm, Select, nameof(vm.Selected));
        list.ItemSelected += index =>
        {
            if (!updating && index >= 0 && index < shown.Count)
                vm.Selected = shown[(int)index];
        };
        return list;
    }



    /// <summary>⬚ ZOOM BOX: while pressed, a drag on the picture draws a square to zoom on (else ⇧ + drag does).</summary>
    private static Button BoxToggle(FractalCanvas canvas)
    {
        var toggle = Eve.Eve.Button("⬚ ZOOM BOX", false, "Draw a square on the picture to zoom on it (⇧ + drag does it too) · Esc: cancel");
        toggle.ToggleMode = true;
        toggle.Toggled += on => canvas.BoxMode = on;
        return toggle;
    }

    // ── The editors ──

    private static Control EscapeEditor(FractalWindowViewModel vm)
    {
        var formula = Ui.Line(vm, nameof(vm.Formula), "z^2 + c");
        formula.AddThemeFontOverride("font", Eve.Eve.Mono);
        formula.TooltipText = "The next z, from z, c, zp (the z before), a, b, p (the pixel) and n (the iteration)";
        var error = Eve.Eve.Text_("", Eve.Eve.Red, 11, wrap: true);
        Bind.When(error, vm, () =>
        {
            error.Text = vm.FormulaError;
            error.Visible = vm.FormulaError.Length > 0;
        }, nameof(vm.FormulaError));

        var julia = Ui.Check("Julia: z starts at the pixel, c is fixed", vm, nameof(vm.Julia),
            "Ticked: a Julia set (z₀ = the pixel, c below) · unticked: a Mandelbrot-like set (c = the pixel, z starts at z₀)");
        var coloring = Bind.Options(new OptionButton(), FractalWindowViewModel.Colorings, vm, nameof(vm.Coloring), FractalWindowViewModel.ColoringName);
        coloring.TooltipText = "Escape: by how fast |z| passes the bailout · Convergence: by the root z settles on (Newton)";

        var numbers = Ui.Grid(2);
        numbers.AddChild(Ui.Field("Iterations", Number(vm, nameof(vm.Iterations), 1, FractalShader.MaxIterations, 10)));
        numbers.AddChild(Ui.Field("Bailout |z|", Number(vm, nameof(vm.Bailout), 1.0001, 1e9, 1)));
        numbers.AddChild(Ui.Field("Degree (smoothing)", Number(vm, nameof(vm.Degree), 1.0001, 64, 0.5)));
        numbers.AddChild(Ui.Field("Colouring", coloring));

        return Ui.Column(8,
            Eve.Eve.Caption("FORMULA · next z =", Eve.Eve.Amber, 11),
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
        var axiom = Ui.Line(vm, nameof(vm.Axiom), "F--F--F");
        axiom.AddThemeFontOverride("font", Eve.Eve.Mono);
        var rules = Ui.Lines(vm, nameof(vm.Rules), 110, "F=F+F--F+F", mono: true);
        rules.TooltipText = "One rule per line: a letter, = and what replaces it at each rewrite";
        var draw = Ui.Line(vm, nameof(vm.DrawSymbols), "FGAB");
        draw.AddThemeFontOverride("font", Eve.Eve.Mono);
        draw.TooltipText = "The letters that draw a line forward (the others only rewrite)";
        var numbers = Ui.Grid(3);
        numbers.AddChild(Ui.Field("Angle °", Number(vm, nameof(vm.Angle), -360, 360, 0.5)));
        numbers.AddChild(Ui.Field("Depth", Number(vm, nameof(vm.Depth), 0, 20, 1)));
        numbers.AddChild(Ui.Field("Heading °", Number(vm, nameof(vm.Heading), -360, 360, 1)));
        return Ui.Column(8,
            Eve.Eve.Caption("L-SYSTEM", Eve.Eve.Amber, 11),
            Ui.Field("Axiom", axiom),
            Ui.Field("Rules", rules),
            numbers,
            Ui.Field("Draw symbols", draw));
    }

    private static Control IfsEditor(FractalWindowViewModel vm)
    {
        var maps = Ui.Lines(vm, nameof(vm.Maps), 190, "a b c d e f [p]", mono: true);
        maps.WrapMode = TextEdit.LineWrappingMode.None;
        maps.TooltipText = "One affine map per line: x' = a x + b y + e, y' = c x + d y + f, chosen with the weight p";
        return Ui.Column(8,
            Eve.Eve.Caption("IFS · AFFINE MAPS", Eve.Eve.Amber, 11),
            Ui.Field("Maps: a b c d e f [p]", maps),
            Ui.Field("Points", Number(vm, nameof(vm.Points), 1000, FractalIfs.MaxPoints, 50_000)));
    }

    private static Control Colours(FractalWindowViewModel vm)
    {
        var palette = Bind.Options(new OptionButton(), FractalWindowViewModel.PaletteNames, vm, nameof(vm.Palette));
        var density = Bind.Range(new HSlider { MinValue = 0.05, MaxValue = 8, Step = 0.05, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "How fast the colours go round the palette" }, vm, nameof(vm.Density));
        var offset = Bind.Range(new HSlider { MinValue = 0, MaxValue = 1, Step = 0.005, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "Where the palette starts" }, vm, nameof(vm.Offset));
        var cycle = Ui.Check("Cycle the colours", vm, nameof(vm.Cycle), "The colours go round the palette over time (escape time)");
        var quality = Bind.Options(new OptionButton { TooltipText = "Samples per pixel of the escape-time fractals: smoother edges, slower" },
            new[] { 1, 2, 3 }, vm, nameof(vm.Quality), q => q switch { 1 => "1 sample", 2 => "4 samples", _ => "9 samples" });
        var qualityField = Ui.Field("Smoothing", quality);
        Bind.Visible(qualityField, vm, nameof(vm.IsEscapeTime));
        Bind.Visible(cycle, vm, nameof(vm.IsEscapeTime));
        var row = Ui.Grid(2);
        row.AddChild(Ui.Field("Palette", palette));
        row.AddChild(qualityField);
        return Ui.Column(8,
            Eve.Eve.Caption("COLOURS", Eve.Eve.Amber, 11),
            row,
            Ui.Field("Density", density),
            Ui.Field("Offset", offset),
            cycle);
    }

    private static Control Help(FractalWindowViewModel vm)
    {
        var help = Ui.Markdown();
        Bind.When(help, vm, () => Ui.SetMarkdown(help, vm.Family switch
        {
            FractalFamily.LSystem => LSystemHelp,
            FractalFamily.Ifs => IfsHelp,
            _ => EscapeHelp
        }), nameof(vm.Family));
        return Ui.Column(6, Eve.Eve.Caption("MAKE YOUR OWN", Eve.Eve.Amber, 11), help);
    }

    private static readonly string EscapeHelp =
        "Each pixel iterates **next z = formula**. Unticked Julia: c is the pixel and z starts at z₀ (a Mandelbrot-like map of every Julia set). "
        + "Ticked: z starts at the pixel and c is fixed. A pixel is coloured by how fast |z| passes the bailout, or (Convergence) by the root z settles on.\n\n"
        + FractalFormula.Help() + "\n"
        + "**Ideas** · `z^4 + c` · `fold(z)^3 + c` · `sin(z) + c` · `z^2 + c + a*zp` (a = -0.5: Phoenix) · `z - (z^4 - 1)/(4*z^3)` with Convergence · `cosh(z) + c` · `z^2 + c/z`\n\n"
        + "Right click on a Mandelbrot-like picture opens the Julia set of that point. The GPU computes in single precision: below a zoom of about ×100 000 the picture turns into blocks.";

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

    /// <summary>A spin box bound to a number property.</summary>
    private static SpinBox Number(FractalWindowViewModel vm, string property, double min, double max, double step)
    {
        // The arrows move by step; what is typed is kept as it is (4 decimals), whole numbers for the integer properties.
        var whole = typeof(FractalWindowViewModel).GetProperty(property)?.PropertyType == typeof(int);
        var spin = new SpinBox
        {
            MinValue = min,
            MaxValue = max,
            Step = whole ? 1 : 0.0001,
            CustomArrowStep = step,
            AllowGreater = false,
            AllowLesser = false,
            SelectAllOnFocus = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        return Bind.Number(spin, vm, property);
    }

    /// <summary>A complex number: its real and imaginary parts.</summary>
    private static Control Pair(string caption, FractalWindowViewModel vm, string re, string im)
    {
        SpinBox Part(string property, string suffix)
        {
            var spin = Number(vm, property, -1e6, 1e6, 0.001);
            spin.CustomArrowStep = 0.001;
            spin.Suffix = suffix;
            return spin;
        }
        return Ui.Field(caption, Ui.Row(6, Part(re, ""), Part(im, "i")));
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
        if (string.IsNullOrWhiteSpace(path) || !GodotObject.IsInstanceValid(canvas))
            return;
        if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            path += ".png";
        canvas.RenderNow();
        await canvas.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await canvas.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = GodotObject.IsInstanceValid(canvas) ? canvas.Picture.GetTexture().GetImage() : null;
        if (image is null || image.SavePng(path) != Error.Ok)
        {
            vm.Say($"The picture could not be saved in {path}", true);
            return;
        }
        vm.Say($"Picture saved: {path} ({image.GetWidth()} × {image.GetHeight()})", false);
    }
}
