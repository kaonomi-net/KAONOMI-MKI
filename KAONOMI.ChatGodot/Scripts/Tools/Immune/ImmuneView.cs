using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using KAONOMI.ChatGodot.Tools.Dashboards;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;

namespace KAONOMI.ChatGodot.Tools.Immune;

/// <summary>
/// /immune: runs KAONOMI.ImmuneSystem and shows its report. Header: where to look (home folder, whole machine, one
/// folder), AI IMAGES, OFFICE CODE, BROWSING, ALL FOLDERS, RUN SCAN / CANCEL, the reports kept, JSON (opens the file), 📁, 🗑.
/// While the scan runs: the immune cell of <see cref="ImmuneScanCanvas"/>, the live counters and the findings as they
/// come. Then the report, in five tabs: OVERVIEW (key figures and charts), AI IMAGES (thumbnails, the clues and the
/// prompt of the one selected), OFFICE CODE (the files, their findings and the code), BROWSING (the AI services visited,
/// by kind and by month, the images and Office files downloaded, the browser profiles), SCAN (where, how long, errors).
/// </summary>
[ToolView(typeof(ImmuneWindowViewModel), "Immune system", "🧬", 1320, 900)]
public sealed class ImmuneView : IToolView
{
    private static readonly Dictionary<string, string> KindNames = new()
    {
        ["vba-macro"] = "VBA macros",
        ["xlm-macro"] = "Excel 4.0 macros",
        ["python-in-excel"] = "Python in Excel",
        ["power-query"] = "Power Query",
        ["activex"] = "ActiveX",
        ["ole-object"] = "OLE objects",
        ["embedded-file"] = "Embedded programs",
        ["dde"] = "DDE",
        ["remote-template"] = "Remote templates",
        ["external-object"] = "External objects",
        ["ribbon-callbacks"] = "Ribbon callbacks",
        ["office-addin"] = "Office add-ins",
        ["code-text"] = "Code as text",
        ["encrypted"] = "Encrypted",
    };

    private static string KindName(string kind) => KindNames.TryGetValue(kind, out var name) ? name : kind;

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (ImmuneWindowViewModel)viewModel;

        // ── Header ──
        var scope = Bind.Options(new OptionButton { TooltipText = "Where to look", CustomMinimumSize = new Vector2(180, 0) },
            ImmuneWindowViewModel.Scopes, vm, nameof(vm.Scope), ImmuneWindowViewModel.ScopeName);
        var folder = Ui.Line(vm, nameof(vm.Folder), "Folder to scan");
        folder.CustomMinimumSize = new Vector2(240, 0);
        var pick = Ui.Action("…", () => PickFolder(vm, folder), tooltip: "Choose the folder");
        Bind.Visible(folder, vm, nameof(vm.IsFolderScope));
        Bind.Visible(pick, vm, nameof(vm.IsFolderScope));
        var run = Ui.Command("🧬 RUN SCAN", vm.RunCommand, primary: true, tooltip: "Run KAONOMI.ImmuneSystem: it only reads the files, runs nothing and sends nothing");
        Bind.Visible(run, vm, nameof(vm.IsRunning), invert: true);
        var cancel = Ui.Command("■ CANCEL", vm.CancelCommand, tooltip: "Stop the scan: the report keeps what was found until then");
        Bind.Visible(cancel, vm, nameof(vm.IsRunning));
        var history = Bind.Options(new OptionButton { TooltipText = $"The reports kept in {ImmuneWindowViewModel.ReportsDirectory}", CustomMinimumSize = new Vector2(220, 0) },
            vm.History, vm, nameof(vm.SelectedHistory), ImmuneWindowViewModel.HistoryName);
        var json = Ui.Action("{ } JSON", () =>
        {
            if (vm.Report is { } r)
                OS.ShellOpen(r.File);
        }, tooltip: "Open the JSON report");
        var reveal = Ui.Action("📁", () => OS.ShellShowInFileManager(vm.Report?.File ?? ImmuneWindowViewModel.ReportsDirectory), tooltip: "Show the report in its folder");
        var delete = Ui.Command("🗑", vm.DeleteReportCommand, tooltip: "Delete this report");
        foreach (var c in new Control[] { json, reveal, delete })
            Bind.Visible(c, vm, nameof(vm.HasReport));

        var header = Ui.Toolbar(Ui.Row(8,
            EveStyle.Caption("Immune system", EveStyle.TextBright, 14),
            scope, folder, pick,
            Ui.Check("AI IMAGES", vm, nameof(vm.Images), "The images generated or edited by an AI"),
            Ui.Check("OFFICE CODE", vm, nameof(vm.Office), "Macros and code in Word, Excel, PowerPoint and Visio files"),
            Ui.Check("BROWSING", vm, nameof(vm.Browsing), "The browser histories: the AI services visited (counts per month, no address kept), the images and Office files downloaded"),
            Ui.Check("ALL FOLDERS", vm, nameof(vm.IncludeAll), "Also the hidden folders, Library, AppData, node_modules…"),
            Ui.Spring(),
            run, cancel, history, json, reveal, delete));

        var body = Ui.Swap(vm, nameof(vm.Stage), stage => stage switch
        {
            ImmuneStage.Running => Running(vm),
            ImmuneStage.Report => Ui.Swap(vm, nameof(vm.Report), report => report is ImmuneReport r ? Report(r) : Idle(vm)),
            _ => Idle(vm),
        });

        return Ui.Column(0, header, Ui.FillHeight(body), DashUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), vm.RunCommand));
    }

    private static void PickFolder(ImmuneWindowViewModel vm, Control owner)
    {
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            UseNativeDialog = true,
            Title = "Folder to scan",
            CurrentDir = Directory.Exists(vm.Folder) ? vm.Folder : System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
        };
        dialog.DirSelected += dir => vm.Folder = dir;
        dialog.VisibilityChanged += () =>
        {
            if (!dialog.Visible)
                dialog.QueueFree();
        };
        owner.AddChild(dialog);
        dialog.PopupCentered(new Vector2I(800, 560));
    }

    /// <summary>A panel standing on the dark cell: opaque, in the colours of the theme.</summary>
    private static PanelContainer Overlay(Control content, string? caption = null, int padding = 12)
    {
        var card = DashUi.Card(content, caption, padding);
        card.AddThemeStyleboxOverride("panel", EveStyle.Box(EveStyle.PanelSolid with { A = 0.94f }, EveStyle.Edge with { A = 0.6f }, padding));
        return card;
    }

    /// <summary>A control holding <paramref name="back"/> filling it and <paramref name="front"/> on top.</summary>
    private static Control Stack(Control back, params Control[] front)
    {
        var stack = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, ClipContents = true };
        stack.AddChild(back);
        back.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        foreach (var f in front)
            stack.AddChild(f);
        return stack;
    }

    // ── Before the first scan ──

    private static Control Idle(ImmuneWindowViewModel vm)
    {
        var canvas = new ImmuneScanCanvas(vm);
        var title = EveStyle.Caption("KAONOMI · IMMUNE SYSTEM", EveStyle.Cyan, 13);
        var text = EveStyle.Text_(
            "Looks on this machine for the images generated or edited by an AI (what they declare: Content Credentials, " +
            "IPTC, the parameters of Stable Diffusion, ComfyUI, Midjourney…) and for the code in Office files (VBA and " +
            "Excel 4.0 macros, DDE, OLE objects, Power Query, code written as text…). The files are only read.",
            EveStyle.Text, 13, wrap: true);
        text.HorizontalAlignment = HorizontalAlignment.Center;
        text.CustomMinimumSize = new Vector2(620, 0);
        var go = Ui.Command("🧬  RUN SCAN", vm.RunCommand, primary: true);
        go.CustomMinimumSize = new Vector2(220, 40);
        go.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var card = Overlay(Ui.Column(10, title, text, go), padding: 16);
        card.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        var bottom = new VBoxContainer();
        bottom.AddChild(card);
        bottom.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterBottom);
        bottom.GrowHorizontal = Control.GrowDirection.Both;
        bottom.GrowVertical = Control.GrowDirection.Begin;
        bottom.OffsetBottom = -24;
        return Stack(canvas, bottom);
    }

    // ── While it scans ──

    private static Control Running(ImmuneWindowViewModel vm)
    {
        var canvas = new ImmuneScanCanvas(vm);

        var counters = Ui.Column(10,
            Counter(vm, "FOLDERS", nameof(vm.Directories), EveStyle.Text),
            Counter(vm, "FILES", nameof(vm.Files), EveStyle.TextBright),
            Counter(vm, "IMAGES READ", nameof(vm.ImagesInspected), EveStyle.Text),
            Counter(vm, "OFFICE FILES READ", nameof(vm.OfficeInspected), EveStyle.Text),
            Counter(vm, "AI IMAGES", nameof(vm.AiImagesFound), ImmuneScanCanvas.Violet),
            Counter(vm, "OFFICE FILES WITH CODE", nameof(vm.OfficeWithCode), ImmuneScanCanvas.Crimson),
            Counter(vm, "AI SERVICES VISITED", nameof(vm.AiServicesFound), ImmuneCharts.Green));
        var left = Overlay(counters, padding: 14);
        left.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        left.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopLeft);
        left.Position = new Vector2(18, 18);

        var feed = new VBoxContainer();
        feed.AddThemeConstantOverride("separation", 4);
        var none = EveStyle.Text_("Nothing found yet", EveStyle.Muted, 12);
        feed.AddChild(none);
        var rows = Bind.Items(new VBoxContainer(), vm.Feed, FeedRow, none);
        rows.AddThemeConstantOverride("separation", 4);
        feed.AddChild(rows);
        var right = Overlay(feed, "FOUND", 12);
        right.CustomMinimumSize = new Vector2(340, 0);
        right.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopRight);
        right.GrowHorizontal = Control.GrowDirection.Begin;
        right.OffsetRight = -18;
        right.OffsetTop = 18;

        var where = Ui.Label(vm, nameof(vm.CurrentFolder), EveStyle.Muted, 12);
        where.ClipText = true;
        where.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        where.HorizontalAlignment = HorizontalAlignment.Center;
        var phase = Ui.Label(vm, nameof(vm.Phase), EveStyle.Cyan, 11, format: v => (v?.ToString() ?? "").ToUpperInvariant());
        phase.AddThemeFontOverride("font", EveStyle.Display);
        phase.HorizontalAlignment = HorizontalAlignment.Center;
        var bar = new ProgressBar { Indeterminate = true, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 3) };
        var bottom = Overlay(Ui.Column(4, phase, where, bar), padding: 8);
        bottom.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
        bottom.GrowVertical = Control.GrowDirection.Begin;
        bottom.OffsetLeft = 40;
        bottom.OffsetRight = -40;
        bottom.OffsetBottom = -14;
        return Stack(canvas, left, right, bottom);
    }

    private static Control Counter(ImmuneWindowViewModel vm, string caption, string property, Color color)
    {
        var value = Ui.Label(vm, property, color, 22, format: v => v is long n ? n.ToString("N0", CultureInfo.CurrentCulture) : v?.ToString() ?? "0");
        value.AddThemeFontOverride("font", EveStyle.Display);
        return Ui.Column(0, EveStyle.Caption(caption, EveStyle.Muted, 9), value);
    }

    private static Control FeedRow(ImmuneHit hit)
    {
        var image = hit.Type == "image";
        var web = hit.Type == "history";
        var color = image ? ImmuneScanCanvas.Violet : web ? ImmuneCharts.Category(hit.Level) : ImmuneCharts.Level(hit.Level);
        var icon = EveStyle.Text_(image ? "⬡" : web ? "◈" : "✷", color, 15);
        var name = EveStyle.Text_(web ? hit.Path : hit.Name, EveStyle.TextBright, 12);
        name.ClipText = true;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        name.TooltipText = hit.Path;
        name.MouseFilter = Control.MouseFilterEnum.Pass;
        var label = EveStyle.Text_(web ? $"AI service · {ImmuneWindowViewModel.CategoryName(hit.Level)} · {hit.Label.Split(" · ").Last()}" : $"{(image ? "AI image" : "Office")} · {hit.Label} · {hit.Level}", color, 10);
        label.ClipText = true;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var text = Ui.Column(0, name, label);
        text.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return Ui.Row(8, icon, text);
    }

    // ── The report ──

    private static Control Report(ImmuneReport report)
    {
        var tabs = new TabContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var overview = Overview(report);
        overview.Name = "OVERVIEW";
        var images = Images(report);
        images.Name = $"AI IMAGES ({report.AiImages.Count})";
        var office = Office(report);
        office.Name = $"OFFICE CODE ({report.OfficeFiles.Count})";
        var scan = Scan(report);
        scan.Name = "SCAN";
        tabs.AddChild(overview);
        tabs.AddChild(images);
        tabs.AddChild(office);
        if (report.Browsers is { } browsers)
        {
            var browsing = Browsing(browsers);
            browsing.Name = $"BROWSING ({browsers.AiServices.Count})";
            tabs.AddChild(browsing);
        }
        tabs.AddChild(scan);
        return tabs;
    }

    private static Control Overview(ImmuneReport report)
    {
        var s = report.Stats;
        var title = EveStyle.Caption($"SCAN OF {report.StartedAt:yyyy-MM-dd HH:mm} · {report.Machine} · {string.Join(", ", report.Roots)}", EveStyle.Cyan, 12);
        title.ClipText = true;
        title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var top = Ui.Row(10, title);
        if (report.Interrupted)
            top.AddChild(Ui.Badge("INTERRUPTED", ImmuneScanCanvas.Orange));

        var kpis = new GridContainer { Columns = report.Browsers is null ? 7 : 8, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        kpis.AddThemeConstantOverride("h_separation", 10);
        kpis.AddChild(Kpi("FILES", s.Files.ToString("N0"), $"{s.Directories:N0} folders", EveStyle.TextBright));
        kpis.AddChild(Kpi("IMAGES READ", s.ImagesInspected.ToString("N0"), "metadata only", EveStyle.TextBright));
        kpis.AddChild(Kpi("AI IMAGES", report.AiImages.Count.ToString("N0"), $"{report.AiImages.Count(i => i.Confidence == "high")} sure · {Percent(report.AiImages.Count, s.ImagesInspected)} of the images", ImmuneScanCanvas.Violet));
        kpis.AddChild(Kpi("OFFICE FILES READ", s.OfficeFilesInspected.ToString("N0"), s.SkippedTooLarge > 0 ? $"{s.SkippedTooLarge} too large" : "Word, Excel, PowerPoint…", EveStyle.TextBright));
        kpis.AddChild(Kpi("WITH CODE", report.OfficeFiles.Count.ToString("N0"), $"{report.OfficeFiles.Count(o => o.Severity == "high")} high · {report.OfficeFiles.Sum(o => o.Findings.Count)} findings", ImmuneScanCanvas.Crimson));
        kpis.AddChild(Kpi("MACRO LINES", report.OfficeFiles.Sum(o => o.Findings.Where(f => f.Kind == "vba-macro").Sum(f => f.Lines ?? 0)).ToString("N0"), "of VBA", ImmuneScanCanvas.Orange));
        if (report.Browsers is { } web)
            kpis.AddChild(Kpi("AI SERVICES", web.AiServices.Count.ToString("N0"), $"{web.AiVisits:N0} visits · {Percent(web.AiVisits, web.Visits)} of the browsing", ImmuneCharts.Green));
        kpis.AddChild(Kpi("DURATION", Duration(report.DurationSeconds), s.Errors > 0 ? $"{s.Errors} unreadable" : "no error", EveStyle.TextBright));
        foreach (var child in kpis.GetChildren().OfType<Control>())
            child.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var generators = ImmuneWindowViewModel.ByGenerator(report).Select((g, i) => new Slice(g.Label, g.Count, ImmuneCharts.Palette[i % ImmuneCharts.Palette.Length])).ToList();
        var confidence = ImmuneWindowViewModel.ByConfidence(report).Select(c => new Slice(c.Label, c.Count, ImmuneCharts.Level(c.Label))).ToList();
        var months = ImmuneWindowViewModel.ByMonth(report).Select(m => new Slice(m.Label, m.Count, ImmuneScanCanvas.Violet)).ToList();
        var severity = ImmuneWindowViewModel.BySeverity(report).Select(c => new Slice(c.Label, c.Count, ImmuneCharts.Level(c.Label))).ToList();
        var kinds = ImmuneWindowViewModel.ByKind(report).Select((k, i) => new Slice(KindName(k.Label), k.Count, ImmuneCharts.Palette[(i + 1) % ImmuneCharts.Palette.Length])).ToList();
        var folders = ImmuneWindowViewModel.ByFolder(report).Select(f => new Slice(f.Label, f.Count, ImmuneScanCanvas.Violet)).ToList();

        var row1 = Ui.Row(10,
            Chart("AI IMAGES BY GENERATOR", report.AiImages.Count == 0 ? Nothing("No AI image found") : ImmuneCharts.DonutWithLegend(generators, "AI images")),
            Chart("CONFIDENCE", report.AiImages.Count == 0 ? Nothing("No AI image found") : new ImmuneCharts.Bars(confidence, 70)),
            Chart("AI IMAGES PER MONTH", new ImmuneCharts.Columns(months)));
        var row2 = Ui.Row(10,
            Chart("OFFICE FILES BY SEVERITY", report.OfficeFiles.Count == 0 ? Nothing("No code in the Office files") : ImmuneCharts.DonutWithLegend(severity, "files")),
            Chart("CODE BY KIND", kinds.Count == 0 ? Nothing("No code in the Office files") : new ImmuneCharts.Bars(kinds, 140)),
            Chart("FOLDERS WITH THE MOST AI IMAGES", folders.Count == 0 ? Nothing("No AI image found") : new ImmuneCharts.Bars(folders, 230)));

        var column = Ui.Column(12, top, kpis, row1, row2, Highlights(report));
        return Ui.Scroll(Ui.Margin(column, 14, 12, 14, 14));
    }

    private static string Percent(long part, long whole) => whole == 0 ? "0%" : $"{100.0 * part / whole:0.#}%";

    private static string Duration(double seconds) =>
        seconds < 60 ? $"{seconds:0.0} s" : TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    private static PanelContainer Kpi(string caption, string value, string details, Color color)
    {
        var big = EveStyle.Text_(value, color, 26);
        big.AddThemeFontOverride("font", EveStyle.Display);
        var line = EveStyle.Text_(details, EveStyle.Muted, 11);
        line.ClipText = true;
        line.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        line.TooltipText = details;
        line.MouseFilter = Control.MouseFilterEnum.Pass;
        return DashUi.Card(Ui.Column(2, EveStyle.Caption(caption), big, line));
    }

    private static PanelContainer Chart(string caption, Control chart)
    {
        var card = DashUi.Card(chart, caption, 12);
        card.CustomMinimumSize = new Vector2(0, 230);
        card.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        card.SizeFlagsStretchRatio = 1;
        return card;
    }

    private static Label Nothing(string text)
    {
        var label = EveStyle.Text_(text, EveStyle.Muted, 12);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        label.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        return label;
    }

    /// <summary>What deserves a look first: the Office files of high severity and the prompts found.</summary>
    private static Control Highlights(ImmuneReport report)
    {
        var watch = report.OfficeFiles.Where(o => o.Severity == "high").Take(8).ToList();
        var prompts = report.AiImages.Where(i => !string.IsNullOrWhiteSpace(i.Prompt)).Take(6).ToList();
        var left = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 6);
        if (watch.Count == 0)
            left.AddChild(EveStyle.Text_("No Office file of high severity", EveStyle.Muted, 12));
        foreach (var o in watch)
        {
            var reasons = o.Findings.Where(f => f.Severity == "high").Select(f => KindName(f.Kind))
                .Concat(o.Findings.SelectMany(f => f.AutoExec ?? []).Take(2)).Distinct().Take(4);
            left.AddChild(Line("✷", ImmuneScanCanvas.Crimson, o.Name, string.Join(" · ", reasons), o.Path));
        }
        var right = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        right.AddThemeConstantOverride("separation", 6);
        if (prompts.Count == 0)
            right.AddChild(EveStyle.Text_("No prompt left in the images", EveStyle.Muted, 12));
        foreach (var i in prompts)
            right.AddChild(Line("⬡", ImmuneScanCanvas.Violet, i.Generator ?? i.Name, $"“{Cut(i.Prompt!, 140)}”", i.Path));
        return Ui.Row(10, DashUi.Card(left, "TO LOOK AT FIRST · OFFICE FILES OF HIGH SEVERITY", 12), DashUi.Card(right, "PROMPTS FOUND IN THE IMAGES", 12));
    }

    private static Control Line(string icon, Color color, string title, string detail, string tooltip)
    {
        var name = EveStyle.Text_(title, EveStyle.TextBright, 12);
        name.ClipText = true;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var text = EveStyle.Text_(detail, EveStyle.Muted, 11);
        text.ClipText = true;
        text.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var column = Ui.Column(0, name, text);
        column.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        column.TooltipText = tooltip;
        return Ui.Row(8, EveStyle.Text_(icon, color, 15), column);
    }

    private static string Cut(string s, int max)
    {
        s = s.ReplaceLineEndings(" ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    // ── AI images: thumbnails and the details of the one selected ──

    private const int MaxThumbnails = 240;

    private static Control Images(ImmuneReport report)
    {
        if (report.AiImages.Count == 0)
            return Ui.Placeholder("No image generated by AI was found");

        var details = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var flow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        flow.AddThemeConstantOverride("h_separation", 10);
        flow.AddThemeConstantOverride("v_separation", 10);
        var loader = new ThumbnailLoader();
        Bind.OnGone(flow, loader.Dispose);
        PanelContainer? selected = null;
        foreach (var image in report.AiImages.Take(MaxThumbnails))
        {
            var card = Thumbnail(image, loader);
            card.GuiInput += e =>
            {
                if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click)
                {
                    if (selected is not null && GodotObject.IsInstanceValid(selected))
                        Highlight(selected, false);
                    selected = card;
                    Highlight(card, true);
                    ShowImage(details, image);
                    if (click.DoubleClick)
                        OS.ShellOpen(image.Path);
                }
            };
            flow.AddChild(card);
        }
        if (report.AiImages.Count > MaxThumbnails)
            flow.AddChild(EveStyle.Text_($"+ {report.AiImages.Count - MaxThumbnails} more in the JSON report", EveStyle.Muted, 12));
        ShowImage(details, report.AiImages[0]);
        if (flow.GetChild(0) is PanelContainer first)
        {
            selected = first;
            Highlight(first, true);
        }

        var right = Ui.Scroll(Ui.Margin(details, 12));
        right.CustomMinimumSize = new Vector2(420, 0);
        return Ui.SplitH(Ui.Scroll(Ui.Margin(flow, 12)), right, 820);
    }

    private static PanelContainer Thumbnail(ImmuneImage image, ThumbnailLoader loader)
    {
        var picture = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            CustomMinimumSize = new Vector2(176, 132),
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        var format = EveStyle.Text_(image.Format.ToUpperInvariant(), EveStyle.Muted, 11);
        format.HorizontalAlignment = HorizontalAlignment.Center;
        format.VerticalAlignment = VerticalAlignment.Center;
        format.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        picture.AddChild(format);
        loader.Load(image.Path, 260, texture =>
        {
            picture.Texture = texture;
            format.Visible = false;
        });

        var level = ImmuneCharts.Level(image.Confidence);
        var name = EveStyle.Text_(image.Name, EveStyle.TextBright, 11);
        name.ClipText = true;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        name.CustomMinimumSize = new Vector2(176, 0);
        var generator = EveStyle.Text_($"● {image.Generator ?? "AI"}", level, 10);
        generator.ClipText = true;
        generator.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        generator.CustomMinimumSize = new Vector2(176, 0);
        var column = Ui.Column(4, picture, name, generator);
        column.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (var child in column.GetChildren().OfType<Control>())
            child.MouseFilter = Control.MouseFilterEnum.Ignore;
        var card = new PanelContainer { TooltipText = $"{image.Path}\n{image.Kind} · confidence {image.Confidence}", MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        card.AddChild(column);
        Highlight(card, false);
        return card;
    }

    private static void Highlight(PanelContainer card, bool on) =>
        card.AddThemeStyleboxOverride("panel", EveStyle.Box(EveStyle.Raised(on ? 0.07f : 0.025f), on ? ImmuneScanCanvas.Violet : EveStyle.Edge with { A = 0.3f }, 6, on ? 2 : 1));

    private static void ShowImage(MarginContainer holder, ImmuneImage image)
    {
        foreach (var child in holder.GetChildren())
            child.QueueFree();
        var preview = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(0, 300),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        var none = EveStyle.Text_($"No preview ({image.Format.ToUpperInvariant()})", EveStyle.Muted, 12);
        none.HorizontalAlignment = HorizontalAlignment.Center;
        none.VerticalAlignment = VerticalAlignment.Center;
        none.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        preview.AddChild(none);
        var loader = new ThumbnailLoader();
        Bind.OnGone(preview, loader.Dispose);
        loader.Load(image.Path, 900, t =>
        {
            preview.Texture = t;
            none.Visible = false;
        });

        var level = ImmuneCharts.Level(image.Confidence);
        var title = EveStyle.Text_(image.Name, EveStyle.TextBright, 15, wrap: true);
        var badges = Ui.Row(6,
            Ui.Badge(image.Kind.ToUpperInvariant(), ImmuneScanCanvas.Violet),
            Ui.Badge($"CONFIDENCE {image.Confidence.ToUpperInvariant()}", level));
        if (image.ContentCredentials)
            badges.AddChild(Ui.Badge("CONTENT CREDENTIALS", EveStyle.Cyan));

        var md = new System.Text.StringBuilder();
        md.AppendLine($"**Generator** {image.Generator ?? "unknown"}  ");
        md.AppendLine($"**Format** {image.Format.ToUpperInvariant()} · {image.Size / 1024.0:N0} KB · modified {image.Modified:yyyy-MM-dd HH:mm}  ");
        md.AppendLine($"**Path** {image.Path}  ");
        md.AppendLine($"**Downloaded from** {image.Origin ?? "—"}");
        md.AppendLine();
        md.AppendLine("**Clues**");
        foreach (var e in image.Evidence)
            md.AppendLine($"- [{e.Confidence}] *{e.Source}* · {e.Detail}");
        var info = Ui.Markdown(md.ToString());
        info.FitContent = true;
        info.SelectionEnabled = true;

        var column = Ui.Column(10, preview, title, badges, info);
        if (!string.IsNullOrWhiteSpace(image.Prompt))
        {
            var prompt = EveStyle.Text_($"“{image.Prompt.Trim()}”", EveStyle.Text, 13, wrap: true);
            column.AddChild(DashUi.Card(prompt, "PROMPT", 10));
        }
        column.AddChild(Ui.Row(8,
            Ui.Action("🖼 OPEN", () => OS.ShellOpen(image.Path), primary: true, tooltip: "Open the image"),
            Ui.Action("📁 SHOW IN FOLDER", () => OS.ShellShowInFileManager(image.Path))));
        holder.AddChild(column);
    }

    // ── Office files: the files and their findings, the code of the one selected ──

    private static Control Office(ImmuneReport report)
    {
        if (report.OfficeFiles.Count == 0)
            return Ui.Placeholder("No code was found in the Office files");

        var tree = new Tree { HideRoot = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, SelectMode = Tree.SelectModeEnum.Row };
        var root = tree.CreateItem();
        var items = new Dictionary<TreeItem, (ImmuneOfficeFile File, ImmuneFinding? Finding)>();
        var index = 0;
        foreach (var file in report.OfficeFiles)
        {
            var item = tree.CreateItem(root);
            item.SetText(0, $"●  {file.Name}   ·   {file.Findings.Count} finding(s)");
            item.SetCustomColor(0, ImmuneCharts.Level(file.Severity));
            item.SetTooltipText(0, file.Path);
            items[item] = (file, null);
            item.Collapsed = index++ >= 12 || file.Findings.Count > 8;
            foreach (var finding in file.Findings)
            {
                var child = tree.CreateItem(item);
                var where = finding.Location.Split('/').Last();
                child.SetText(0, $"{KindName(finding.Kind)} · {finding.Severity} · {(where.Length > 0 ? where : finding.Location)}");
                child.SetCustomColor(0, ImmuneCharts.Level(finding.Severity).Lerp(EveStyle.Text, 0.35f));
                child.SetTooltipText(0, finding.Description);
                items[child] = (file, finding);
            }
        }

        var details = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        tree.ItemSelected += () =>
        {
            if (tree.GetSelected() is { } item && items.TryGetValue(item, out var entry))
                ShowFinding(details, entry.File, entry.Finding);
        };
        var firstFile = root.GetFirstChild();
        firstFile.Select(0);
        ShowFinding(details, report.OfficeFiles[0], null);

        var left = DashUi.Card(tree, "FILES · FINDINGS", 8);
        left.CustomMinimumSize = new Vector2(420, 0);
        return Ui.SplitH(Ui.Margin(left, 12, 12, 0, 12), Ui.Margin(details, 12), 520);
    }

    private static void ShowFinding(MarginContainer holder, ImmuneOfficeFile file, ImmuneFinding? finding)
    {
        foreach (var child in holder.GetChildren())
            child.QueueFree();

        var title = EveStyle.Text_(file.Name, EveStyle.TextBright, 15, wrap: true);
        var path = EveStyle.Text_(file.Path, EveStyle.Muted, 11, wrap: true);
        var badges = Ui.Row(6,
            Ui.Badge($"SEVERITY {file.Severity.ToUpperInvariant()}", ImmuneCharts.Level(file.Severity)),
            Ui.Badge($"{file.Format.ToUpperInvariant()} · {file.Container.ToUpperInvariant()}", EveStyle.Cyan));
        if (file.Origin is { } origin)
            path.Text += $"\nDownloaded from {origin}";
        var head = Ui.Column(4, title, path, Ui.Row(8, badges, Ui.Spring(),
            Ui.Action("📁 SHOW IN FOLDER", () => OS.ShellShowInFileManager(file.Path), tooltip: "The file is not opened: its macros could run")));

        Control body;
        if (finding is null)
        {
            // The file: its findings, one line each.
            var list = new VBoxContainer();
            list.AddThemeConstantOverride("separation", 6);
            foreach (var f in file.Findings)
                list.AddChild(Line(f.Kind == "vba-macro" || f.Kind == "xlm-macro" ? "✷" : "◆", ImmuneCharts.Level(f.Severity),
                    $"{KindName(f.Kind)} · {f.Severity}", f.Description, f.Location));
            var kinds = file.Findings.GroupBy(f => KindName(f.Kind)).Select((g, i) => new Slice(g.Key, g.Count(), ImmuneCharts.Palette[(i + 1) % ImmuneCharts.Palette.Length])).ToList();
            body = Ui.Column(10, Chart("FINDINGS OF THIS FILE", new ImmuneCharts.Bars(kinds, 150)), DashUi.Card(list, "ALL THE FINDINGS · SELECT ONE TO SEE ITS CODE", 12));
        }
        else
        {
            var md = new System.Text.StringBuilder();
            md.AppendLine($"**{KindName(finding.Kind)}** · severity **{finding.Severity}**{(finding.Lines is { } n ? $" · {n:N0} line(s)" : "")}  ");
            md.AppendLine($"{finding.Description}  ");
            md.AppendLine($"*{finding.Location}*");
            if (finding.AutoExec is { Count: > 0 } auto)
                md.AppendLine($"\n**Runs by itself** {string.Join(", ", auto.Select(a => $"`{a}`"))}");
            if (finding.Suspicious is { Count: > 0 } bad)
            {
                md.AppendLine("\n**What it can do**");
                foreach (var b in bad)
                    md.AppendLine($"- {b}");
            }
            if (finding.Iocs is { Count: > 0 } iocs)
            {
                md.AppendLine("\n**Addresses**");
                foreach (var i in iocs)
                    md.AppendLine($"- `{i}`");
            }
            var info = Ui.Markdown(md.ToString());
            info.FitContent = true;
            info.SelectionEnabled = true;
            var column = Ui.Column(10, DashUi.Card(info, null, 12));
            if (!string.IsNullOrEmpty(finding.Code))
            {
                var code = CodeEditors.Editor(editable: false);
                code.Text = finding.Code;
                code.CustomMinimumSize = new Vector2(0, 360);
                column.AddChild(DashUi.Card(code, "CODE", 8));
            }
            body = column;
        }
        holder.AddChild(Ui.Scroll(Ui.Column(12, head, body)));
    }

    // ── The browsing history ──

    private static Control Browsing(ImmuneBrowsers web)
    {
        var readable = web.Profiles.Where(p => p.Readable).ToList();
        var blocked = web.Profiles.Where(p => !p.Readable).ToList();
        var kpis = new GridContainer { Columns = 5, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        kpis.AddThemeConstantOverride("h_separation", 10);
        kpis.AddChild(Kpi("PROFILES READ", readable.Count.ToString("N0"), blocked.Count > 0 ? $"{blocked.Count} protected or unreadable" : string.Join(", ", readable.Select(p => p.Browser).Distinct()), EveStyle.TextBright));
        kpis.AddChild(Kpi("VISITS", web.Visits.ToString("N0"), web.From is { } from && web.To is { } to ? $"{from:yyyy-MM-dd} → {to:yyyy-MM-dd}" : "empty histories", EveStyle.TextBright));
        kpis.AddChild(Kpi("AI SERVICES", web.AiServices.Count.ToString("N0"), $"{web.AiServices.Select(a => a.Category).Distinct().Count()} kinds", ImmuneCharts.Green));
        kpis.AddChild(Kpi("AI VISITS", web.AiVisits.ToString("N0"), $"{Percent(web.AiVisits, web.Visits)} of the browsing", ImmuneCharts.Green));
        kpis.AddChild(Kpi("DOWNLOADS", web.Downloads.Count.ToString("N0"), $"{web.Downloads.Count(d => d.Kind == "image")} images · {web.Downloads.Count(d => d.Kind == "office")} Office · {web.Downloads.Count(d => d.Finding is not null)} flagged", EveStyle.TextBright));
        foreach (var child in kpis.GetChildren().OfType<Control>())
            child.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var categories = ImmuneWindowViewModel.VisitsByCategory(web).Select(c => new Slice(ImmuneWindowViewModel.CategoryName(c.Label), (int)c.Count, ImmuneCharts.Category(c.Label))).ToList();
        var top = web.AiServices.Take(10).Select(a => new Slice(a.Name, (int)a.Visits, ImmuneCharts.Category(a.Category))).ToList();
        var months = ImmuneWindowViewModel.VisitsByMonth(web)
            .Select(m => (m.Month, (IReadOnlyList<Slice>)m.Parts.Select(p => new Slice(p.Category, (int)p.Count, ImmuneCharts.Category(p.Category))).ToList())).ToList();
        var none = web.AiServices.Count == 0 ? (readable.Count == 0 ? "No browser history could be read" : "No AI service in the histories") : null;
        var row = Ui.Row(10,
            Chart("AI VISITS BY KIND", none is not null ? Nothing(none) : ImmuneCharts.DonutWithLegend(categories, "AI visits")),
            Chart("THE MOST VISITED", none is not null ? Nothing(none) : new ImmuneCharts.Bars(top, 150)),
            Chart("AI VISITS PER MONTH", new ImmuneCharts.StackedColumns(months)));

        // The services, one line each.
        var services = new VBoxContainer();
        services.AddThemeConstantOverride("separation", 6);
        if (web.AiServices.Count == 0)
            services.AddChild(EveStyle.Text_(none ?? "", EveStyle.Muted, 12));
        foreach (var a in web.AiServices)
            services.AddChild(Line("◈", ImmuneCharts.Category(a.Category), $"{a.Name} · {a.Visits:N0} visits",
                $"{ImmuneWindowViewModel.CategoryName(a.Category)} · {a.FirstVisit:yyyy-MM-dd} → {a.LastVisit:yyyy-MM-dd} · {string.Join(", ", a.Browsers)}", a.Name));

        // The downloads: flagged first.
        var downloads = new VBoxContainer();
        downloads.AddThemeConstantOverride("separation", 6);
        if (web.Downloads.Count == 0)
            downloads.AddChild(EveStyle.Text_("No image or Office file in the downloads of the browsers", EveStyle.Muted, 12));
        foreach (var d in web.Downloads.OrderBy(d => d.Finding is null).ThenBy(d => d.Service is null).Take(60))
        {
            var color = d.Finding == "office-code" ? ImmuneScanCanvas.Crimson : d.Finding == "ai-image" || d.Service is not null ? ImmuneScanCanvas.Violet : EveStyle.Muted;
            var what = d.Finding switch { "ai-image" => "AI image", "office-code" => "Office file with code", _ => d.Kind == "image" ? "image" : "Office file" };
            var where = d.Service ?? (Uri.TryCreate(d.Source ?? d.Referrer, UriKind.Absolute, out var u) ? u.Host : "unknown source");
            var line = Line(d.Kind == "image" ? "⬡" : "✷", color, d.Name,
                $"{what} · from {where} · {d.Browser} · {d.Time:yyyy-MM-dd}{(d.Exists ? "" : " · deleted")}", $"{d.File}\n{d.Source}");
            if (d.Exists)
                line.AddChild(Ui.Action("📁", () => OS.ShellShowInFileManager(d.File), tooltip: "Show the file in its folder"));
            downloads.AddChild(line);
        }

        // The profiles, and why some could not be read.
        var profiles = new VBoxContainer();
        profiles.AddThemeConstantOverride("separation", 6);
        if (web.Profiles.Count == 0)
            profiles.AddChild(EveStyle.Text_("No browser found", EveStyle.Muted, 12));
        foreach (var p in web.Profiles)
            profiles.AddChild(Line(p.Readable ? "●" : "○", p.Readable ? ImmuneCharts.Green : ImmuneScanCanvas.Orange, $"{p.Browser} · {p.Profile}",
                p.Readable ? $"{p.Visits:N0} visits, {p.AiVisits:N0} to AI services · {p.From:yyyy-MM-dd} → {p.To:yyyy-MM-dd}" : p.Error ?? "unreadable", p.Path));

        var privacy = EveStyle.Text_("Only the visits to generative AI services are counted, per service and per month: no address and no page title is kept in the report.", EveStyle.Muted, 11, wrap: true);
        var lists = Ui.Row(10, DashUi.Card(services, "AI SERVICES VISITED", 12), DashUi.Card(downloads, "IMAGES AND OFFICE FILES DOWNLOADED", 12));
        var column = Ui.Column(12, kpis, row, lists, DashUi.Card(Ui.Column(8, profiles, privacy), "BROWSER PROFILES", 12));
        return Ui.Scroll(Ui.Margin(column, 14, 12, 14, 14));
    }

    // ── The scan itself ──

    private static Control Scan(ImmuneReport report)
    {
        var s = report.Stats;
        var md = new System.Text.StringBuilder();
        md.AppendLine($"**Machine** {report.Machine} · {report.User} · {report.Os}  ");
        md.AppendLine($"**Folders scanned** {string.Join(", ", report.Roots)}  ");
        md.AppendLine($"**Started** {report.StartedAt:yyyy-MM-dd HH:mm:ss} · **finished** {report.FinishedAt:HH:mm:ss} · {Duration(report.DurationSeconds)}{(report.Interrupted ? " · **interrupted**" : "")}  ");
        md.AppendLine($"**Read** {s.Directories:N0} folders, {s.Files:N0} files, {s.ImagesInspected:N0} images, {s.OfficeFilesInspected:N0} Office files ({s.SkippedTooLarge} too large)  ");
        md.AppendLine($"**Scanner** {report.Tool} {report.Version}  ");
        md.AppendLine($"**Report** {report.File}");
        var info = Ui.Markdown(md.ToString());
        info.FitContent = true;
        info.SelectionEnabled = true;

        var errors = new ItemList { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 240) };
        foreach (var e in report.Errors)
        {
            var i = errors.AddItem($"{e.Path}  ·  {e.Error}");
            errors.SetItemTooltip(i, e.Error);
        }
        if (report.Errors.Count == 0)
            errors.AddItem("Every file could be read");

        var how = EveStyle.Text_(
            "AI images: only what the images declare is read (IPTC digital source type, C2PA Content Credentials, the parameters of the generators, their file names). " +
            "An image without metadata (a screenshot, a copy from a messaging app) is not recognised, and invisible watermarks such as SynthID cannot be read. " +
            "Office code: the severity says how much the code can do (run by itself, call the system…), not whether it is malicious.",
            EveStyle.Muted, 12, wrap: true);
        var column = Ui.Column(12, DashUi.Card(info, "SCAN", 12), DashUi.Card(how, "HOW TO READ IT", 12), DashUi.Card(errors, $"UNREADABLE FILES ({s.Errors})", 8));
        return Ui.Scroll(Ui.Margin(column, 14, 12, 14, 14));
    }

    /// <summary>Loads pictures off the main thread, two at a time, as textures at most <c>size</c> pixels wide.</summary>
    private sealed class ThumbnailLoader : IDisposable
    {
        private static readonly SemaphoreSlim Gate = new(2);
        private readonly CancellationTokenSource _cancel = new();

        public void Load(string path, int size, Action<Texture2D> done)
        {
            var token = _cancel.Token;
            _ = Task.Run(async () =>
            {
                await Gate.WaitAsync(token);
                try
                {
                    if (token.IsCancellationRequested || !File.Exists(path))
                        return;
                    var image = new Image();
                    if (image.Load(path) != Error.Ok || image.IsEmpty())
                        return;
                    var scale = Math.Min(1.0, (double)size / Math.Max(image.GetWidth(), image.GetHeight()));
                    if (scale < 1)
                        image.Resize(Math.Max(1, (int)(image.GetWidth() * scale)), Math.Max(1, (int)(image.GetHeight() * scale)), Image.Interpolation.Bilinear);
                    Bind.OnMain(() =>
                    {
                        if (!token.IsCancellationRequested)
                            done(ImageTexture.CreateFromImage(image));
                    });
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    // No thumbnail: the format is shown instead.
                }
                finally
                {
                    Gate.Release();
                }
            }, token);
        }

        public void Dispose() => _cancel.Cancel();
    }
}
