using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Chat;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Media;

/// <summary>
/// /immune: runs KAONOMI.ImmuneSystem and shows its report. Header: where to look (home folder, whole machine, one
/// folder), AI IMAGES, OFFICE CODE, BROWSING, ALL FOLDERS, RUN SCAN / CANCEL, the reports kept, JSON (opens the file), 📁, 🗑.
/// While the scan runs: the immune cell of <see cref="ImmuneScanCanvas"/>, the live counters and the findings as they
/// come. Then the report, in tabs: OVERVIEW (key figures and charts), AI IMAGES (thumbnails, the clues and the prompt of
/// the one selected), OFFICE CODE (the files, their findings and the code), BROWSING (the AI services visited, by kind
/// and by month, the images and Office files downloaded, the browser profiles), SCAN (where, how long, errors).
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

    private static IBrush B(Color color) => ImmuneCharts.B(color);

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (ImmuneWindowViewModel)viewModel;

        // ── Header ──
        var scope = Kit.Options(ImmuneWindowViewModel.Scopes, vm, nameof(vm.Scope), ImmuneWindowViewModel.ScopeName, "Where to look");
        scope.Width = 180;
        var folder = Ui.Edit(vm, nameof(vm.Folder), "Folder to scan").Visible(vm, nameof(vm.IsFolderScope));
        folder.Width = 240;
        var pick = Ui.Button("…", () => _ = PickFolder(vm), "Choose the folder").Visible(vm, nameof(vm.IsFolderScope));
        var run = Ui.Command("🧬 RUN SCAN", vm.RunCommand, tooltip: "Run KAONOMI.ImmuneSystem: it only reads the files, runs nothing and sends nothing", primary: true)
            .Visible(vm, nameof(vm.IsRunning), invert: true);
        var cancel = Ui.Command("■ CANCEL", vm.CancelCommand, tooltip: "Stop the scan: the report keeps what was found until then").Visible(vm, nameof(vm.IsRunning));
        var history = Kit.Options(vm.History, vm, nameof(vm.SelectedHistory), ImmuneWindowViewModel.HistoryName, $"The reports kept in {ImmuneWindowViewModel.ReportsDirectory}");
        history.Width = 220;
        var json = Ui.Button("{ } JSON", () => Kit.OpenWithSystem(vm.Report?.File), "Open the JSON report").Visible(vm, nameof(vm.HasReport));
        var reveal = Ui.Button("📁", () => Kit.ShowInFolder(vm.Report?.File ?? ImmuneWindowViewModel.ReportsDirectory), "Show the report in its folder").Visible(vm, nameof(vm.HasReport));
        var delete = Ui.Command("🗑", vm.DeleteReportCommand, tooltip: "Delete this report").Visible(vm, nameof(vm.HasReport));

        var header = Kit.Toolbar(Kit.FillRow(8,
            [
                Ui.Label("Immune system", Ui.TextBright, 14, bold: true),
                scope, folder, pick,
                Kit.Check("AI IMAGES", vm, nameof(vm.Images), "The images generated or edited by an AI"),
                Kit.Check("OFFICE CODE", vm, nameof(vm.Office), "Macros and code in Word, Excel, PowerPoint and Visio files"),
                Kit.Check("BROWSING", vm, nameof(vm.Browsing), "The browser histories: the AI services visited (counts per month, no address kept), the images and Office files downloaded"),
                Kit.Check("ALL FOLDERS", vm, nameof(vm.IncludeAll), "Also the hidden folders, Library, AppData, node_modules…")
            ],
            new Panel(),
            run, cancel, history, json, reveal, delete));

        var body = Kit.Swap(vm, nameof(vm.Stage), stage => stage switch
        {
            ImmuneStage.Running => Running(vm),
            ImmuneStage.Report => Kit.Swap(vm, nameof(vm.Report), report => report is ImmuneReport r ? Report(r) : Idle(vm)),
            _ => Idle(vm),
        });

        return Kit.Rows("Auto,*,Auto", header, body, Kit.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), busySources: vm.RunCommand));
    }

    private static async Task PickFolder(ImmuneWindowViewModel vm)
    {
        var start = Directory.Exists(vm.Folder) ? vm.Folder : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (await Dialogs.OpenFolder("Folder to scan", start) is { } dir)
            vm.Folder = dir;
    }

    /// <summary>A panel standing on the dark cell: opaque, in the colours of the theme.</summary>
    private static Border Overlay(Control content, string? caption = null, double padding = 12) =>
        Kit.DashCard(content, caption, padding);

    /// <summary>A grid holding <paramref name="back"/> filling it and <paramref name="front"/> on top (placed by their alignment).</summary>
    private static Grid Stack(Control back, params Control[] front)
    {
        var stack = new Grid { ClipToBounds = true };
        stack.Children.Add(back);
        stack.Children.AddRange(front);
        return stack;
    }

    // ── Before the first scan ──

    private static Control Idle(ImmuneWindowViewModel vm)
    {
        var canvas = new ImmuneScanCanvas(vm);
        var title = Ui.Caption("KAONOMI · IMMUNE SYSTEM", Ui.Accent, 13);
        title.HorizontalAlignment = HorizontalAlignment.Center;
        var text = Ui.Label(
            "Looks on this machine for the images generated or edited by an AI (what they declare: Content Credentials, " +
            "IPTC, the parameters of Stable Diffusion, ComfyUI, Midjourney…) and for the code in Office files (VBA and " +
            "Excel 4.0 macros, DDE, OLE objects, Power Query, code written as text…). The files are only read.",
            Ui.Text, 13, wrap: true);
        text.TextAlignment = TextAlignment.Center;
        text.Width = 620;
        var go = Ui.Command("🧬  RUN SCAN", vm.RunCommand, primary: true);
        go.MinWidth = 220;
        go.MinHeight = 40;
        go.HorizontalAlignment = HorizontalAlignment.Center;
        go.HorizontalContentAlignment = HorizontalAlignment.Center;
        go.VerticalContentAlignment = VerticalAlignment.Center;
        var card = Overlay(Ui.Column(10, title, text, go), padding: 16);
        card.HorizontalAlignment = HorizontalAlignment.Center;
        card.VerticalAlignment = VerticalAlignment.Bottom;
        card.Margin = new Thickness(0, 0, 0, 24);
        return Stack(canvas, card);
    }

    // ── While it scans ──

    private static Control Running(ImmuneWindowViewModel vm)
    {
        var canvas = new ImmuneScanCanvas(vm);

        var counters = Ui.Column(10,
            Counter(vm, "FOLDERS", nameof(vm.Directories), Ui.Text),
            Counter(vm, "FILES", nameof(vm.Files), Ui.TextBright),
            Counter(vm, "IMAGES READ", nameof(vm.ImagesInspected), Ui.Text),
            Counter(vm, "OFFICE FILES READ", nameof(vm.OfficeInspected), Ui.Text),
            Counter(vm, "AI IMAGES", nameof(vm.AiImagesFound), B(ImmuneCharts.Violet)),
            Counter(vm, "OFFICE FILES WITH CODE", nameof(vm.OfficeWithCode), B(ImmuneCharts.Crimson)),
            Counter(vm, "AI SERVICES VISITED", nameof(vm.AiServicesFound), B(ImmuneCharts.Green)));
        var left = Overlay(counters, padding: 14);
        left.HorizontalAlignment = HorizontalAlignment.Left;
        left.VerticalAlignment = VerticalAlignment.Top;
        left.Margin = new Thickness(18);

        var none = Ui.Label("Nothing found yet", Ui.Muted, 12);
        Kit.EmptyWhen(none, vm.Feed);
        var rows = Kit.Items(vm.Feed, FeedRow, () => new StackPanel { Spacing = 4 });
        var right = Overlay(new ScrollViewer { Content = Ui.Column(4, none, rows), MaxHeight = 420 }, "FOUND", 12);
        right.Width = 340;
        right.HorizontalAlignment = HorizontalAlignment.Right;
        right.VerticalAlignment = VerticalAlignment.Top;
        right.Margin = new Thickness(18);

        var where = Ui.Bound(vm, nameof(vm.CurrentFolder), Ui.Muted, 12);
        where.HorizontalAlignment = HorizontalAlignment.Center;
        var phase = Kit.Text(vm, nameof(vm.Phase), v => (v?.ToString() ?? string.Empty).ToUpperInvariant(), Ui.Accent, 11);
        phase.HorizontalAlignment = HorizontalAlignment.Center;
        var bar = new ProgressBar { IsIndeterminate = true, Height = 3, MinHeight = 3 };
        var bottom = Overlay(Ui.Column(4, phase, where, bar), padding: 8);
        bottom.VerticalAlignment = VerticalAlignment.Bottom;
        bottom.Margin = new Thickness(40, 0, 40, 14);
        return Stack(canvas, left, right, bottom);
    }

    private static Control Counter(ImmuneWindowViewModel vm, string caption, string property, IBrush color)
    {
        var value = Kit.Text(vm, property, v => v switch
        {
            long n => n.ToString("N0", CultureInfo.CurrentCulture),
            int n => n.ToString("N0", CultureInfo.CurrentCulture),
            _ => v?.ToString() ?? "0"
        }, color, 22);
        return Ui.Column(0, Ui.Caption(caption, Ui.Muted, 9), value);
    }

    private static Control FeedRow(ImmuneHit hit)
    {
        var image = hit.Type == "image";
        var web = hit.Type == "history";
        var color = B(image ? ImmuneCharts.Violet : web ? ImmuneCharts.Category(hit.Level) : ImmuneCharts.Level(hit.Level));
        var icon = Ui.Label(image ? "⬡" : web ? "◈" : "✷", color, 15);
        var name = Ui.Label(web ? hit.Path : hit.Name, Ui.TextBright, 12);
        ToolTip.SetTip(name, hit.Path);
        var label = Ui.Label(web
            ? $"AI service · {ImmuneWindowViewModel.CategoryName(hit.Level)} · {hit.Label.Split(" · ").Last()}"
            : $"{(image ? "AI image" : "Office")} · {hit.Label} · {hit.Level}", color, 10);
        return Kit.FillRow(8, [icon], Ui.Column(0, name, label));
    }

    // ── The report ──

    private static Control Report(ImmuneReport report)
    {
        var tabs = new TabControl();
        void Tab(string header, Control content) => tabs.Items.Add(new TabItem { Header = header, Content = content, FontSize = 13 });
        Tab("OVERVIEW", Overview(report));
        Tab($"AI IMAGES ({report.AiImages.Count})", Images(report));
        Tab($"OFFICE CODE ({report.OfficeFiles.Count})", Office(report));
        if (report.Browsers is { } browsers)
            Tab($"BROWSING ({browsers.AiServices.Count})", Browsing(browsers));
        Tab("SCAN", Scan(report));
        return tabs;
    }

    private static Control Overview(ImmuneReport report)
    {
        var s = report.Stats;
        var title = Ui.Caption($"SCAN OF {report.StartedAt:yyyy-MM-dd HH:mm} · {report.Machine} · {string.Join(", ", report.Roots)}", Ui.Accent, 12);
        var top = report.Interrupted ? Kit.FillRow(10, [], title, Kit.Badge("INTERRUPTED", B(ImmuneCharts.Orange))) : (Control)title;

        var kpis = new List<Control>
        {
            Kpi("FILES", s.Files.ToString("N0"), $"{s.Directories:N0} folders", Ui.TextBright),
            Kpi("IMAGES READ", s.ImagesInspected.ToString("N0"), "metadata only", Ui.TextBright),
            Kpi("AI IMAGES", report.AiImages.Count.ToString("N0"), $"{report.AiImages.Count(i => i.Confidence == "high")} sure · {Percent(report.AiImages.Count, s.ImagesInspected)} of the images", B(ImmuneCharts.Violet)),
            Kpi("OFFICE FILES READ", s.OfficeFilesInspected.ToString("N0"), s.SkippedTooLarge > 0 ? $"{s.SkippedTooLarge} too large" : "Word, Excel, PowerPoint…", Ui.TextBright),
            Kpi("WITH CODE", report.OfficeFiles.Count.ToString("N0"), $"{report.OfficeFiles.Count(o => o.Severity == "high")} high · {report.OfficeFiles.Sum(o => o.Findings.Count)} findings", B(ImmuneCharts.Crimson)),
            Kpi("MACRO LINES", report.OfficeFiles.Sum(o => o.Findings.Where(f => f.Kind == "vba-macro").Sum(f => f.Lines ?? 0)).ToString("N0"), "of VBA", B(ImmuneCharts.Orange))
        };
        if (report.Browsers is { } web)
            kpis.Add(Kpi("AI SERVICES", web.AiServices.Count.ToString("N0"), $"{web.AiVisits:N0} visits · {Percent(web.AiVisits, web.Visits)} of the browsing", B(ImmuneCharts.Green)));
        kpis.Add(Kpi("DURATION", Duration(report.DurationSeconds), s.Errors > 0 ? $"{s.Errors} unreadable" : "no error", Ui.TextBright));

        var generators = ImmuneWindowViewModel.ByGenerator(report).Select((g, i) => new Slice(g.Label, g.Count, ImmuneCharts.Palette[i % ImmuneCharts.Palette.Length])).ToList();
        var confidence = ImmuneWindowViewModel.ByConfidence(report).Select(c => new Slice(c.Label, c.Count, ImmuneCharts.Level(c.Label))).ToList();
        var months = ImmuneWindowViewModel.ByMonth(report).Select(m => new Slice(m.Label, m.Count, ImmuneCharts.Violet)).ToList();
        var severity = ImmuneWindowViewModel.BySeverity(report).Select(c => new Slice(c.Label, c.Count, ImmuneCharts.Level(c.Label))).ToList();
        var kinds = ImmuneWindowViewModel.ByKind(report).Select((k, i) => new Slice(KindName(k.Label), k.Count, ImmuneCharts.Palette[(i + 1) % ImmuneCharts.Palette.Length])).ToList();
        var folders = ImmuneWindowViewModel.ByFolder(report).Select(f => new Slice(f.Label, f.Count, ImmuneCharts.Violet)).ToList();

        var row1 = Kit.Cells(3, 10, 10,
            Chart("AI IMAGES BY GENERATOR", report.AiImages.Count == 0 ? Nothing("No AI image found") : ImmuneCharts.DonutWithLegend(generators, "AI images")),
            Chart("CONFIDENCE", report.AiImages.Count == 0 ? Nothing("No AI image found") : new ImmuneCharts.Bars(confidence, 70)),
            Chart("AI IMAGES PER MONTH", new ImmuneCharts.Columns(months)));
        var row2 = Kit.Cells(3, 10, 10,
            Chart("OFFICE FILES BY SEVERITY", report.OfficeFiles.Count == 0 ? Nothing("No code in the Office files") : ImmuneCharts.DonutWithLegend(severity, "files")),
            Chart("CODE BY KIND", kinds.Count == 0 ? Nothing("No code in the Office files") : new ImmuneCharts.Bars(kinds, 140)),
            Chart("FOLDERS WITH THE MOST AI IMAGES", folders.Count == 0 ? Nothing("No AI image found") : new ImmuneCharts.Bars(folders, 230)));

        var column = Ui.Column(12, top, Kit.Cells(kpis.Count, 10, 10, [.. kpis]), row1, row2, Highlights(report));
        return Ui.Scroll(Kit.Margin(column, 14, 12, 14, 14));
    }

    private static string Percent(long part, long whole) => whole == 0 ? "0%" : $"{100.0 * part / whole:0.#}%";

    private static string Duration(double seconds) =>
        seconds < 60 ? $"{seconds:0.0} s" : TimeSpan.FromSeconds(seconds).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    private static Border Kpi(string caption, string value, string details, IBrush color)
    {
        var big = Ui.Label(value, color, 26);
        var line = Ui.Label(details, Ui.Muted, 11);
        ToolTip.SetTip(line, details);
        return Kit.DashCard(Ui.Column(2, Ui.Caption(caption), big, line));
    }

    private static Border Chart(string caption, Control chart)
    {
        var card = Kit.DashCard(chart, caption, 12);
        card.MinHeight = 230;
        return card;
    }

    private static TextBlock Nothing(string text)
    {
        var label = Ui.Label(text, Ui.Muted, 12);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        return label;
    }

    /// <summary>What deserves a look first: the Office files of high severity and the prompts found.</summary>
    private static Control Highlights(ImmuneReport report)
    {
        var watch = report.OfficeFiles.Where(o => o.Severity == "high").Take(8).ToList();
        var prompts = report.AiImages.Where(i => !string.IsNullOrWhiteSpace(i.Prompt)).Take(6).ToList();
        var left = Ui.Column(6);
        if (watch.Count == 0)
            left.Children.Add(Ui.Label("No Office file of high severity", Ui.Muted, 12));
        foreach (var o in watch)
        {
            var reasons = o.Findings.Where(f => f.Severity == "high").Select(f => KindName(f.Kind))
                .Concat(o.Findings.SelectMany(f => f.AutoExec ?? []).Take(2)).Distinct().Take(4);
            left.Children.Add(Line("✷", B(ImmuneCharts.Crimson), o.Name, string.Join(" · ", reasons), o.Path));
        }
        var right = Ui.Column(6);
        if (prompts.Count == 0)
            right.Children.Add(Ui.Label("No prompt left in the images", Ui.Muted, 12));
        foreach (var i in prompts)
            right.Children.Add(Line("⬡", B(ImmuneCharts.Violet), i.Generator ?? i.Name, $"“{Cut(i.Prompt!, 140)}”", i.Path));
        return Kit.Cells(2, 10, 10, Kit.DashCard(left, "TO LOOK AT FIRST · OFFICE FILES OF HIGH SEVERITY", 12), Kit.DashCard(right, "PROMPTS FOUND IN THE IMAGES", 12));
    }

    private static DockPanel Line(string icon, IBrush color, string title, string detail, string tooltip, params Control[] after)
    {
        var column = Ui.Column(0, Ui.Label(title, Ui.TextBright, 12), Ui.Label(detail, Ui.Muted, 11));
        ToolTip.SetTip(column, tooltip);
        return Kit.FillRow(8, [Ui.Label(icon, color, 15)], column, after);
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
            return Kit.Placeholder("No image generated by AI was found");

        var details = new ContentControl();
        var flow = new WrapPanel { ItemSpacing = 10, LineSpacing = 10 };
        var loader = new ThumbnailLoader();
        flow.DetachedFromVisualTree += (_, _) => loader.Dispose();
        Border? selected = null;
        foreach (var image in report.AiImages.Take(MaxThumbnails))
        {
            var card = Thumbnail(image, loader);
            card.PointerPressed += (_, e) =>
            {
                if (!Kit.IsLeft(e, card))
                    return;
                if (selected is not null)
                    Highlight(selected, false);
                selected = card;
                Highlight(card, true);
                details.Content = ShowImage(image);
                if (e.ClickCount >= 2)
                    Kit.OpenWithSystem(image.Path);
            };
            flow.Children.Add(card);
        }
        if (report.AiImages.Count > MaxThumbnails)
            flow.Children.Add(Ui.Label($"+ {report.AiImages.Count - MaxThumbnails} more in the JSON report", Ui.Muted, 12));
        details.Content = ShowImage(report.AiImages[0]);
        if (flow.Children[0] is Border first)
        {
            selected = first;
            Highlight(first, true);
        }
        return Kit.SplitH(Ui.Scroll(Kit.Margin(flow, 12)), Ui.Scroll(Kit.Margin(details, 12)), "*", "460", 300, 420);
    }

    private static Border Thumbnail(ImmuneImage image, ThumbnailLoader loader)
    {
        var picture = new Image { Stretch = Stretch.UniformToFill };
        var format = Ui.Label(image.Format.ToUpperInvariant(), Ui.Muted, 11);
        format.HorizontalAlignment = HorizontalAlignment.Center;
        loader.Load(image.Path, 260, bitmap =>
        {
            picture.Source = bitmap;
            format.IsVisible = false;
        });
        var frame = new Border { Width = 176, Height = 132, ClipToBounds = true, Background = Ui.Field, Child = new Grid { Children = { format, picture } } };

        var level = B(ImmuneCharts.Level(image.Confidence));
        var name = Ui.Label(image.Name, Ui.TextBright, 11);
        name.Width = 176;
        var generator = Ui.Label($"● {image.Generator ?? "AI"}", level, 10);
        generator.Width = 176;
        var card = new Border
        {
            Child = Ui.Column(4, frame, name, generator),
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(2),
            Background = Ui.Panel,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        ToolTip.SetTip(card, $"{image.Path}\n{image.Kind} · confidence {image.Confidence}");
        Highlight(card, false);
        return card;
    }

    private static void Highlight(Border card, bool on)
    {
        card.BorderBrush = on ? B(ImmuneCharts.Violet) : Ui.Edge;
        card.BorderThickness = new Thickness(on ? 2 : 1);
    }

    private static Control ShowImage(ImmuneImage image)
    {
        var preview = new Image { Stretch = Stretch.Uniform, MaxHeight = 420 };
        var none = Ui.Label($"No preview ({image.Format.ToUpperInvariant()})", Ui.Muted, 12);
        none.HorizontalAlignment = HorizontalAlignment.Center;
        var previewArea = new Grid { MinHeight = 300, Children = { none, preview } };
        var loader = new ThumbnailLoader();
        previewArea.DetachedFromVisualTree += (_, _) => loader.Dispose();
        loader.Load(image.Path, 900, bitmap =>
        {
            preview.Source = bitmap;
            none.IsVisible = false;
        });

        var level = B(ImmuneCharts.Level(image.Confidence));
        var title = Ui.Label(image.Name, Ui.TextBright, 15, wrap: true);
        var badges = Ui.Row(6,
            Kit.Badge(image.Kind, B(ImmuneCharts.Violet)),
            Kit.Badge($"CONFIDENCE {image.Confidence}", level));
        if (image.ContentCredentials)
            badges.Children.Add(Kit.Badge("CONTENT CREDENTIALS", Ui.Accent));

        var md = new System.Text.StringBuilder();
        md.AppendLine($"**Generator** {image.Generator ?? "unknown"}  ");
        md.AppendLine($"**Format** {image.Format.ToUpperInvariant()} · {image.Size / 1024.0:N0} KB · modified {image.Modified:yyyy-MM-dd HH:mm}  ");
        md.AppendLine($"**Path** {image.Path}  ");
        md.AppendLine($"**Downloaded from** {image.Origin ?? "—"}");
        md.AppendLine();
        md.AppendLine("**Clues**");
        foreach (var e in image.Evidence)
            md.AppendLine($"- [{e.Confidence}] *{e.Source}* · {e.Detail}");

        var column = Ui.Column(10, previewArea, title, badges, MarkdownView.Build(md.ToString()));
        if (!string.IsNullOrWhiteSpace(image.Prompt))
            column.Children.Add(Kit.DashCard(new SelectableTextBlock { Text = $"“{image.Prompt.Trim()}”", Foreground = Ui.Text, FontSize = 13, TextWrapping = TextWrapping.Wrap }, "PROMPT", 10));
        column.Children.Add(Ui.Row(8,
            Ui.Button("🖼 OPEN", () => Kit.OpenWithSystem(image.Path), "Open the image", primary: true),
            Ui.Button("📁 SHOW IN FOLDER", () => Kit.ShowInFolder(image.Path))));
        return column;
    }

    // ── Office files: the files and their findings, the code of the one selected ──

    private static Control Office(ImmuneReport report)
    {
        if (report.OfficeFiles.Count == 0)
            return Kit.Placeholder("No code was found in the Office files");

        var text = Kit.ColorOf(Ui.Text);
        var tree = new TreeView();
        var items = new Dictionary<TreeViewItem, (ImmuneOfficeFile File, ImmuneFinding? Finding)>();
        var index = 0;
        TreeViewItem? firstItem = null;
        foreach (var file in report.OfficeFiles)
        {
            var header = Ui.Label($"●  {file.Name}   ·   {file.Findings.Count} finding(s)", B(ImmuneCharts.Level(file.Severity)), 12);
            ToolTip.SetTip(header, file.Path);
            var item = new TreeViewItem { Header = header, IsExpanded = !(index++ >= 12 || file.Findings.Count > 8) };
            items[item] = (file, null);
            firstItem ??= item;
            foreach (var finding in file.Findings)
            {
                var where = finding.Location.Split('/').Last();
                var label = Ui.Label($"{KindName(finding.Kind)} · {finding.Severity} · {(where.Length > 0 ? where : finding.Location)}",
                    new SolidColorBrush(Kit.Lerp(ImmuneCharts.Level(finding.Severity), text, 0.35)), 12);
                ToolTip.SetTip(label, finding.Description);
                var child = new TreeViewItem { Header = label };
                items[child] = (file, finding);
                item.Items.Add(child);
            }
            tree.Items.Add(item);
        }

        var details = new ContentControl();
        tree.SelectionChanged += (_, _) =>
        {
            if (tree.SelectedItem is TreeViewItem item && items.TryGetValue(item, out var entry))
                details.Content = ShowFinding(entry.File, entry.Finding);
        };
        tree.SelectedItem = firstItem;
        details.Content = ShowFinding(report.OfficeFiles[0], null);

        var left = Kit.DashCard(tree, "FILES · FINDINGS", 8);
        return Kit.SplitH(Kit.Margin(left, 12, 12, 0, 12), Kit.Margin(details, 12), "520", "*", 420, 300);
    }

    private static Control ShowFinding(ImmuneOfficeFile file, ImmuneFinding? finding)
    {
        var title = Ui.Label(file.Name, Ui.TextBright, 15, wrap: true);
        var pathText = file.Path + (file.Origin is { } origin ? $"\nDownloaded from {origin}" : string.Empty);
        var path = new SelectableTextBlock { Text = pathText, Foreground = Ui.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        var badges = Ui.Row(6,
            Kit.Badge($"SEVERITY {file.Severity}", B(ImmuneCharts.Level(file.Severity))),
            Kit.Badge($"{file.Format} · {file.Container}", Ui.Accent));
        var head = Ui.Column(4, title, path, Kit.FillRow(8, [badges], new Panel(),
            Ui.Button("📁 SHOW IN FOLDER", () => Kit.ShowInFolder(file.Path), "The file is not opened: its macros could run")));

        Control body;
        if (finding is null)
        {
            // The file: its findings, one line each.
            var list = Ui.Column(6);
            foreach (var f in file.Findings)
                list.Children.Add(Line(f.Kind == "vba-macro" || f.Kind == "xlm-macro" ? "✷" : "◆", B(ImmuneCharts.Level(f.Severity)),
                    $"{KindName(f.Kind)} · {f.Severity}", f.Description, f.Location));
            var kinds = file.Findings.GroupBy(f => KindName(f.Kind)).Select((g, i) => new Slice(g.Key, g.Count(), ImmuneCharts.Palette[(i + 1) % ImmuneCharts.Palette.Length])).ToList();
            body = Ui.Column(10, Chart("FINDINGS OF THIS FILE", new ImmuneCharts.Bars(kinds, 150)), Kit.DashCard(list, "ALL THE FINDINGS · SELECT ONE TO SEE ITS CODE", 12));
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
            var column = Ui.Column(10, Kit.DashCard(MarkdownView.Build(md.ToString()), null, 12));
            if (!string.IsNullOrEmpty(finding.Code))
            {
                var code = new TextBox
                {
                    Text = finding.Code,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.NoWrap,
                    FontFamily = Ui.Mono,
                    FontSize = 12,
                    MinHeight = 360,
                    MaxHeight = 600
                };
                ScrollViewer.SetHorizontalScrollBarVisibility(code, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
                column.Children.Add(Kit.DashCard(code, "CODE", 8));
            }
            body = column;
        }
        return Ui.Scroll(Ui.Column(12, head, body));
    }

    // ── The browsing history ──

    private static Control Browsing(ImmuneBrowsers web)
    {
        var readable = web.Profiles.Where(p => p.Readable).ToList();
        var blocked = web.Profiles.Where(p => !p.Readable).ToList();
        var kpis = Kit.Cells(5, 10, 10,
            Kpi("PROFILES READ", readable.Count.ToString("N0"), blocked.Count > 0 ? $"{blocked.Count} protected or unreadable" : string.Join(", ", readable.Select(p => p.Browser).Distinct()), Ui.TextBright),
            Kpi("VISITS", web.Visits.ToString("N0"), web.From is { } from && web.To is { } to ? $"{from:yyyy-MM-dd} → {to:yyyy-MM-dd}" : "empty histories", Ui.TextBright),
            Kpi("AI SERVICES", web.AiServices.Count.ToString("N0"), $"{web.AiServices.Select(a => a.Category).Distinct().Count()} kinds", B(ImmuneCharts.Green)),
            Kpi("AI VISITS", web.AiVisits.ToString("N0"), $"{Percent(web.AiVisits, web.Visits)} of the browsing", B(ImmuneCharts.Green)),
            Kpi("DOWNLOADS", web.Downloads.Count.ToString("N0"), $"{web.Downloads.Count(d => d.Kind == "image")} images · {web.Downloads.Count(d => d.Kind == "office")} Office · {web.Downloads.Count(d => d.Finding is not null)} flagged", Ui.TextBright));

        var categories = ImmuneWindowViewModel.VisitsByCategory(web).Select(c => new Slice(ImmuneWindowViewModel.CategoryName(c.Label), (int)c.Count, ImmuneCharts.Category(c.Label))).ToList();
        var top = web.AiServices.Take(10).Select(a => new Slice(a.Name, (int)a.Visits, ImmuneCharts.Category(a.Category))).ToList();
        var months = ImmuneWindowViewModel.VisitsByMonth(web)
            .Select(m => (m.Month, (IReadOnlyList<Slice>)m.Parts.Select(p => new Slice(p.Category, (int)p.Count, ImmuneCharts.Category(p.Category))).ToList())).ToList();
        var none = web.AiServices.Count == 0 ? (readable.Count == 0 ? "No browser history could be read" : "No AI service in the histories") : null;
        var row = Kit.Cells(3, 10, 10,
            Chart("AI VISITS BY KIND", none is not null ? Nothing(none) : ImmuneCharts.DonutWithLegend(categories, "AI visits")),
            Chart("THE MOST VISITED", none is not null ? Nothing(none) : new ImmuneCharts.Bars(top, 150)),
            Chart("AI VISITS PER MONTH", new ImmuneCharts.StackedColumns(months)));

        // The services, one line each.
        var services = Ui.Column(6);
        if (web.AiServices.Count == 0)
            services.Children.Add(Ui.Label(none ?? string.Empty, Ui.Muted, 12));
        foreach (var a in web.AiServices)
            services.Children.Add(Line("◈", B(ImmuneCharts.Category(a.Category)), $"{a.Name} · {a.Visits:N0} visits",
                $"{ImmuneWindowViewModel.CategoryName(a.Category)} · {a.FirstVisit:yyyy-MM-dd} → {a.LastVisit:yyyy-MM-dd} · {string.Join(", ", a.Browsers)}", a.Name));

        // The downloads: flagged first.
        var downloads = Ui.Column(6);
        if (web.Downloads.Count == 0)
            downloads.Children.Add(Ui.Label("No image or Office file in the downloads of the browsers", Ui.Muted, 12));
        foreach (var d in web.Downloads.OrderBy(d => d.Finding is null).ThenBy(d => d.Service is null).Take(60))
        {
            var color = d.Finding == "office-code" ? B(ImmuneCharts.Crimson) : d.Finding == "ai-image" || d.Service is not null ? B(ImmuneCharts.Violet) : Ui.Muted;
            var what = d.Finding switch { "ai-image" => "AI image", "office-code" => "Office file with code", _ => d.Kind == "image" ? "image" : "Office file" };
            var where = d.Service ?? (Uri.TryCreate(d.Source ?? d.Referrer, UriKind.Absolute, out var u) ? u.Host : "unknown source");
            var file = d.File;
            Control[] after = d.Exists ? [Ui.Button("📁", () => Kit.ShowInFolder(file), "Show the file in its folder")] : [];
            downloads.Children.Add(Line(d.Kind == "image" ? "⬡" : "✷", color, d.Name,
                $"{what} · from {where} · {d.Browser} · {d.Time:yyyy-MM-dd}{(d.Exists ? "" : " · deleted")}", $"{d.File}\n{d.Source}", after));
        }

        // The profiles, and why some could not be read.
        var profiles = Ui.Column(6);
        if (web.Profiles.Count == 0)
            profiles.Children.Add(Ui.Label("No browser found", Ui.Muted, 12));
        foreach (var p in web.Profiles)
            profiles.Children.Add(Line(p.Readable ? "●" : "○", B(p.Readable ? ImmuneCharts.Green : ImmuneCharts.Orange), $"{p.Browser} · {p.Profile}",
                p.Readable ? $"{p.Visits:N0} visits, {p.AiVisits:N0} to AI services · {p.From:yyyy-MM-dd} → {p.To:yyyy-MM-dd}" : p.Error ?? "unreadable", p.Path));

        var privacy = Ui.Label("Only the visits to generative AI services are counted, per service and per month: no address and no page title is kept in the report.", Ui.Muted, 11, wrap: true);
        var lists = Kit.Cells(2, 10, 10, Kit.DashCard(services, "AI SERVICES VISITED", 12), Kit.DashCard(downloads, "IMAGES AND OFFICE FILES DOWNLOADED", 12));
        var column = Ui.Column(12, kpis, row, lists, Kit.DashCard(Ui.Column(8, profiles, privacy), "BROWSER PROFILES", 12));
        return Ui.Scroll(Kit.Margin(column, 14, 12, 14, 14));
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

        var errors = new ListBox { MinHeight = 240, MaxHeight = 480 };
        foreach (var e in report.Errors)
        {
            var label = Ui.Label($"{e.Path}  ·  {e.Error}", Ui.Text, 12);
            ToolTip.SetTip(label, e.Error);
            errors.Items.Add(label);
        }
        if (report.Errors.Count == 0)
            errors.Items.Add(Ui.Label("Every file could be read", Ui.Muted, 12));

        var how = Ui.Label(
            "AI images: only what the images declare is read (IPTC digital source type, C2PA Content Credentials, the parameters of the generators, their file names). " +
            "An image without metadata (a screenshot, a copy from a messaging app) is not recognised, and invisible watermarks such as SynthID cannot be read. " +
            "Office code: the severity says how much the code can do (run by itself, call the system…), not whether it is malicious.",
            Ui.Muted, 12, wrap: true);
        var column = Ui.Column(12, Kit.DashCard(MarkdownView.Build(md.ToString()), "SCAN", 12), Kit.DashCard(how, "HOW TO READ IT", 12), Kit.DashCard(errors, $"UNREADABLE FILES ({s.Errors})", 8));
        return Ui.Scroll(Kit.Margin(column, 14, 12, 14, 14));
    }

    /// <summary>Loads pictures off the interface thread, two at a time, at most <c>size</c> pixels wide.</summary>
    private sealed class ThumbnailLoader : IDisposable
    {
        private static readonly SemaphoreSlim Gate = new(2);
        private readonly CancellationTokenSource _cancel = new();

        public void Load(string path, int size, Action<Bitmap> done)
        {
            var token = _cancel.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Gate.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                try
                {
                    if (token.IsCancellationRequested || ImageKit.Load(path) is not { } full)
                        return;
                    var bitmap = full;
                    if (full.PixelSize.Width > size)
                    {
                        var scaled = full.CreateScaledBitmap(new PixelSize(size, Math.Max(1, (int)((double)full.PixelSize.Height * size / full.PixelSize.Width))));
                        full.Dispose();
                        bitmap = scaled;
                    }
                    Ui.OnMain(() =>
                    {
                        if (!token.IsCancellationRequested)
                            done(bitmap);
                    });
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or ArgumentException)
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
