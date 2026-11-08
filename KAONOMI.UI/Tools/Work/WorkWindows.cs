using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using KAONOMI.Chat.Services;
using KAONOMI.UI.Chat;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Work;

/// <summary>
/// The windows /work opens on the desktop besides its own: the result of an evaluation or of the AI review, the result
/// of a translation, a spelling correction or a rewriting, the code window (insert, edit, generate) and the image
/// generation window (Fooocus). Each is a window of the desktop of its own; the dialogs give their result through a
/// callback.
/// </summary>
public static class WorkWindows
{
    private static int _count;

    /// <summary>Opens <paramref name="content"/> in a new window of the desktop.</summary>
    public static DeskWindow Open(Shell shell, string title, string icon, Control content, Size size) =>
        shell.OpenControl($"work-window-{++_count}", title, icon, content, size);

    /// <summary>The text something was made from, quoted with a colored bar on its left (at most six lines).</summary>
    private static Control Quote(string text, IBrush bar, bool mono)
    {
        var label = Ui.Label(text, Ui.Text, 12, wrap: true);
        label.MaxLines = 6;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        if (mono)
            label.FontFamily = Ui.Mono;
        var line = new Border { Width = 3, Background = bar, Margin = new Thickness(0, 0, 10, 0) };
        return Ui.Dock((line, Avalonia.Controls.Dock.Left), (label, Avalonia.Controls.Dock.Left));
    }

    private static Border Page(Control content, double margin) => new() { Padding = new Thickness(margin), Child = content };

    /// <summary>A grid of rows: the ones marked * take the free height.</summary>
    private static Grid Rows(string definition, params Control[] rows)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions(definition), RowSpacing = 12 };
        for (var i = 0; i < rows.Length; i++)
        {
            Grid.SetRow(rows[i], i);
            grid.Children.Add(rows[i]);
        }
        return grid;
    }

    /// <summary>
    /// The result of 🧐 Evaluate or ✨ AI review: the text evaluated, then the analysis of the LLM (Markdown) or the error,
    /// Copy and Close.
    /// </summary>
    public static void Evaluation(Shell shell, string text, string model, string? markdown, string? error,
        string heading = "🧐 Evaluation", string? subtitle = null, string? actionLabel = null, Action? action = null)
    {
        var plain = new string(heading.SkipWhile(c => !char.IsLetter(c)).ToArray());
        Control result = error is null
            ? MarkdownView.Build(markdown ?? string.Empty)
            : Ui.Label($"{plain} failed: {error}", Ui.Red, 13, wrap: true);
        var status = Ui.Label("", Ui.Muted, 11);
        Control? page = null;
        var copy = Ui.Button("COPY", () =>
        {
            Dialogs.Copy(markdown ?? string.Empty);
            status.Text = "✓ Copied";
        });
        copy.IsEnabled = error is null;
        var close = Ui.Button("CLOSE", () => shell.Close(page!), primary: true);
        var header = Ui.Column(6,
            WorkUi.Heading(heading),
            Ui.Label(subtitle ?? $"Mathematical and formal criticism, then ideas to improve the text, without the solution · {model}", Ui.Muted, 11, wrap: true),
            Quote(text, Ui.Warm, mono: true));
        var buttons = Ui.Row(8, status, copy);
        // An action offered on the result (the rewriting of an AI-Check, for one): it closes the window, then runs.
        if (actionLabel is not null && action is not null && error is null)
            buttons.Children.Add(Ui.Button(actionLabel, () =>
            {
                shell.Close(page!);
                action();
            }, primary: true));
        buttons.Children.Add(close);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var scroll = Ui.Scroll(new Border { Padding = new Thickness(0, 0, 10, 0), Child = result });
        page = Page(Rows("Auto,Auto,*,Auto", header, WorkUi.Separator(), scroll, buttons), 18);
        Open(shell, plain, heading.Split(' ')[0], page, new Size(680, 620));
    }

    /// <summary>
    /// The result of 🌐 Translate, ✏️ Correct the spelling or ✨ Rewrite for readability: the text, the result (which can
    /// be changed), Replace the selection, Insert below, Copy, Close. <paramref name="replace"/> and
    /// <paramref name="insert"/> return false when the tape changed meanwhile.
    /// </summary>
    public static void Translation(Shell shell, string text, string language, string model, string? translation, string? error,
        Func<string, bool> replace, Func<string, bool> insert, string? heading = null, string? title = null,
        string replaceLabel = "REPLACE THE SELECTION", string insertLabel = "INSERT BELOW")
    {
        var result = new TextBox
        {
            Text = translation ?? $"{title ?? "The translation"} failed: {error}",
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            IsReadOnly = error is not null,
            MinHeight = 160,
            FontSize = 14,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        var status = Ui.Label("", Ui.Muted, 11, wrap: true);
        Control? page = null;
        void Put(Func<string, bool> action)
        {
            if (action(result.Text ?? string.Empty))
                shell.Close(page!);
            else
                status.Text = "The tape changed since the selection: copy the text instead.";
        }
        var replaceButton = Ui.Button(replaceLabel, () => Put(replace), "The text takes the place of the selected text in the tape (Ctrl / ⌘ + Z undoes it)", primary: true);
        var insertButton = Ui.Button(insertLabel, () => Put(insert), "The text is added under the selected text, which is kept");
        var copy = Ui.Button("COPY", () =>
        {
            Dialogs.Copy(result.Text ?? string.Empty);
            status.Text = "✓ Copied";
        });
        replaceButton.IsEnabled = insertButton.IsEnabled = copy.IsEnabled = error is null;
        var close = Ui.Button("CLOSE", () => shell.Close(page!));
        var header = Ui.Column(6,
            WorkUi.Heading(heading ?? $"🌐 Translation into {language}"),
            Ui.Label(model, Ui.Muted, 11),
            Quote(text, Ui.Muted, mono: false));
        page = Page(Rows("Auto,*,Auto", header, result, WorkUi.FillRow(8, status, replaceButton, insertButton, copy, close)), 18);
        page.AttachedToVisualTree += (_, _) => WorkUi.Later(() => replaceButton.Focus());
        Open(shell, title ?? $"Translation · {language}", heading?.Split(' ')[0] ?? "🌐", page, new Size(640, 520));
    }

    /// <summary>"Plain text" entry of the language list (no coloring).</summary>
    private const string PlainText = "text";

    /// <summary>A code editor: a text in the monospace font with the numbers of its lines on its left.</summary>
    private static (Control View, TextBox Box) CodeBox(string code, string placeholder)
    {
        var box = new TextBox
        {
            Text = code,
            PlaceholderText = placeholder,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = Ui.Mono,
            FontSize = 13,
            Padding = new Thickness(6, 4),
            VerticalContentAlignment = VerticalAlignment.Top,
            BorderThickness = new Thickness(0)
        };
        var numbers = new TextBlock
        {
            FontFamily = Ui.Mono,
            FontSize = 13,
            Foreground = Ui.Muted,
            TextAlignment = TextAlignment.Right,
            Padding = new Thickness(6, 4, 6, 4),
            MinWidth = 36
        };
        void Number() => numbers.Text = string.Join("\n", Enumerable.Range(1, Math.Max(1, (box.Text ?? string.Empty).Split('\n').Length)));
        Number();
        box.TextChanged += (_, _) => Number();
        var gutter = new Border { Child = numbers, Background = Ui.Header, BorderBrush = Ui.Edge, BorderThickness = new Thickness(0, 0, 1, 0) };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(gutter);
        Grid.SetColumn(box, 1);
        grid.Children.Add(box);
        var scroll = new ScrollViewer
        {
            Content = grid,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
        };
        return (new Border { Child = scroll, Background = Ui.Field, BorderBrush = Ui.Edge, BorderThickness = new Thickness(1), MinHeight = 160 }, box);
    }

    /// <summary>
    /// The code window: its language and code, a live preview in the syntax coloring of the language; <paramref name="done"/>
    /// gets the language and the code on <paramref name="action"/>. With <paramref name="generate"/>, a prompt and
    /// ✨ Generate (Ctrl / ⌘ + Enter) ask the LLM, the code written can be changed before it is inserted.
    /// </summary>
    public static void Code(Shell shell, string title, string action, string language, string code, Action<string, string> done,
        Func<string, string, CancellationToken, Task<(string Language, string Code)>>? generate = null)
    {
        var languages = new List<string> { PlainText };
        languages.AddRange(CodeHighlighter.Languages);
        if (generate is not null)
            languages.Insert(0, "auto");
        var current = string.IsNullOrEmpty(language) ? (generate is null ? PlainText : "auto") : CodeHighlighter.Normalize(language);
        if (!languages.Contains(current))
            languages.Insert(1, current);
        var languageBox = new Dropdown { Width = 180 };
        void FillLanguages(string selected) => languageBox.SetItems(languages, Math.Max(0, languages.IndexOf(selected)));
        FillLanguages(current);
        string Selected() => languageBox.Selected >= 0 && languageBox.Selected < languages.Count ? languages[languageBox.Selected] : PlainText;
        string LanguageChosen() => Selected() is var l && l is not PlainText and not "auto" ? l : string.Empty;

        var (codeView, codeBox) = CodeBox(code, generate is null ? "Type or paste the code" : "The generated code shows here; it can be changed before it is inserted");
        var preview = new SelectableTextBlock { FontFamily = Ui.Mono, FontSize = 13, TextWrapping = TextWrapping.NoWrap };
        void Refresh() => WorkPalette.CodeInlines(preview.Inlines!, LanguageChosen(), codeBox.Text ?? string.Empty);
        Refresh();
        var status = Ui.Label("", Ui.Muted, 11, wrap: true);
        Control? page = null;
        var ok = Ui.Button(action.ToUpperInvariant(), () =>
        {
            done(LanguageChosen(), (codeBox.Text ?? string.Empty).Replace("\r\n", "\n").TrimEnd());
            shell.Close(page!);
        }, primary: generate is null);
        ok.IsEnabled = code.Trim().Length > 0;
        var cancel = Ui.Button("CANCEL", () => shell.Close(page!));

        // The preview follows the code a moment after the last key, and the language at once.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Refresh();
        };
        codeBox.TextChanged += (_, _) =>
        {
            ok.IsEnabled = (codeBox.Text ?? string.Empty).Trim().Length > 0;
            timer.Stop();
            timer.Start();
        };
        languageBox.ItemSelected += _ => Refresh();

        var top = Ui.Column(8);
        CancellationTokenSource? running = null;
        Control focus = codeBox;
        if (generate is not null)
        {
            var prompt = new TextBox
            {
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                MinHeight = 80,
                PlaceholderText = "Describe the code to generate, e.g. \"a Python function that returns the area of a circle, with a test\"",
                VerticalContentAlignment = VerticalAlignment.Top
            };
            Button? run = null;
            async void Generate()
            {
                if (string.IsNullOrWhiteSpace(prompt.Text) || running is not null)
                    return;
                running = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                run!.IsEnabled = false;
                page!.Cursor = new Cursor(StandardCursorType.Wait);
                status.Text = "✨ The LLM is writing the code…";
                try
                {
                    var (written, generated) = await generate(prompt.Text.Trim(), Selected(), running.Token);
                    var id = string.IsNullOrEmpty(written) ? PlainText : CodeHighlighter.Normalize(written);
                    if (!languages.Contains(id))
                        languages.Insert(1, id);
                    FillLanguages(id);
                    codeBox.Text = generated;
                    Refresh();
                    status.Text = "✓ Code written: change it if needed, then Insert";
                    ok.Classes.Add("primary");
                }
                catch (Exception ex) when (ex is OperationCanceledException or System.Net.Http.HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    status.Text = ex is OperationCanceledException ? "No answer after 10 minutes." : $"Not generated: {ex.Message}";
                }
                finally
                {
                    running?.Dispose();
                    running = null;
                    run.IsEnabled = true;
                    page.Cursor = Cursor.Default;
                }
            }
            run = Ui.Button("✨ GENERATE", Generate, "Ask the LLM to write the code (Ctrl / ⌘ + Enter)", primary: true);
            run.VerticalAlignment = VerticalAlignment.Bottom;
            prompt.AddHandler(InputElement.KeyDownEvent, (_, e) =>
            {
                if (e.Key == Key.Enter && WorkUi.IsCommand(e.KeyModifiers))
                {
                    e.Handled = true;
                    Generate();
                }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            top.Children.Add(Ui.Caption("Prompt"));
            top.Children.Add(WorkUi.FillRow(8, prompt, run));
            focus = prompt;
        }
        top.Children.Add(Ui.Row(8, Ui.Caption("Language"), languageBox));

        var previewBox = new Border
        {
            Child = new ScrollViewer
            {
                Content = preview,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            },
            Padding = new Thickness(8),
            Background = Ui.Field,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1)
        };
        var panes = new Grid { RowDefinitions = new RowDefinitions("2*,Auto,*"), RowSpacing = 4 };
        panes.Children.Add(codeView);
        var previewCaption = Ui.Caption("Preview");
        Grid.SetRow(previewCaption, 1);
        panes.Children.Add(previewCaption);
        Grid.SetRow(previewBox, 2);
        panes.Children.Add(previewBox);
        page = Page(Rows("Auto,*,Auto", top, panes, WorkUi.FillRow(8, status, ok, cancel)), 16);
        page.AttachedToVisualTree += (_, _) => WorkUi.Later(() => focus.Focus());
        var window = Open(shell, title, "</>", page, new Size(820, generate is null ? 640 : 760));
        window.Closed += () =>
        {
            timer.Stop();
            running?.Cancel();
        };
    }

    /// <summary>Sizes offered by the image window, width*height (the ones of SDXL).</summary>
    private static readonly string[] ImageSizes = ["1152*896", "1024*1024", "896*1152", "1344*768", "768*1344", "1216*832", "832*1216"];

    /// <summary>Performances of Fooocus; the fast ones download a LoRA the first time.</summary>
    private static readonly string[] Performances = ["Speed", "Quality", "Extreme Speed", "Lightning", "Hyper-SD"];

    /// <summary>
    /// The ✨🖼 window: a prompt, the size, the performance; ✨ Generate asks Fooocus and shows the image (generated again
    /// until it fits); Insert gives its path to <paramref name="done"/>. The images generated but not inserted are deleted
    /// when the window closes.
    /// </summary>
    public static void ImageGeneration(Shell shell, string title, Func<string, string, string, CancellationToken, Task<string>> generate, Action<string> done)
    {
        var prompt = new TextBox
        {
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            MinHeight = 84,
            PlaceholderText = "Describe the image, in English, e.g. \"a clean diagram of a right triangle with its angles, white background, flat style\"",
            VerticalContentAlignment = VerticalAlignment.Top
        };
        var size = new Dropdown { Width = 140 };
        size.SetItems(ImageSizes, 0);
        var performance = new Dropdown { Width = 150 };
        ToolTip.SetTip(performance, "Speed: about a minute; Quality: twice as long; Extreme Speed, Lightning, Hyper-SD: a few seconds");
        performance.SetItems(Performances, 0);
        var picture = new Image { Stretch = Stretch.Uniform, MinHeight = 200 };
        var placeholder = WorkUi.Placeholder("The image shows here; generate it again until it fits, then Insert");
        var frame = new Border
        {
            Background = Ui.Field,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            Child = new Panel { Children = { picture, placeholder } }
        };
        var status = Ui.Label("", Ui.Muted, 11, wrap: true);

        string? path = null;
        string? inserted = null;
        var generated = new List<string>();
        CancellationTokenSource? running = null;
        Control? page = null;
        var insert = Ui.Button("INSERT", () =>
        {
            if (path is null)
                return;
            inserted = path;
            done(path);
            shell.Close(page!);
        });
        insert.IsEnabled = false;
        var cancel = Ui.Button("CANCEL", () => shell.Close(page!));
        Button? run = null;
        var started = DateTime.UtcNow;
        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => status.Text = $"✨ Fooocus is drawing the image… {DateTime.UtcNow - started:m\\:ss}";

        async void Generate()
        {
            if (string.IsNullOrWhiteSpace(prompt.Text) || running is not null)
                return;
            running = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            run!.IsEnabled = false;
            started = DateTime.UtcNow;
            clock.Start();
            status.Text = "✨ Fooocus is drawing the image… 0:00";
            try
            {
                path = await generate(prompt.Text.Trim(), ImageSizes[Math.Max(0, size.Selected)], Performances[Math.Max(0, performance.Selected)], running.Token);
                generated.Add(path);
                (picture.Source as IDisposable)?.Dispose();
                picture.Source = await PictureLibrary.DecodeAsync(path, 1400, CancellationToken.None);
                placeholder.IsVisible = picture.Source is null;
                insert.IsEnabled = true;
                insert.Classes.Add("primary");
                status.Text = $"✓ Image ready in {(DateTime.UtcNow - started).TotalSeconds:0} s, saved as {System.IO.Path.GetFileName(path)}: Insert it, or generate again";
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.Net.Http.HttpRequestException or InvalidOperationException
                                           or System.Text.Json.JsonException or System.IO.IOException or FormatException)
            {
                status.Text = ex is OperationCanceledException ? "Stopped." : $"Not generated: {ex.Message}";
            }
            finally
            {
                running?.Dispose();
                running = null;
                clock.Stop();
                run.IsEnabled = true;
            }
        }
        run = Ui.Button("✨ GENERATE", Generate, "Ask Fooocus for the image (Ctrl / ⌘ + Enter)", primary: true);
        run.VerticalAlignment = VerticalAlignment.Bottom;
        prompt.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && WorkUi.IsCommand(e.KeyModifiers))
            {
                e.Handled = true;
                Generate();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var top = Ui.Column(8,
            Ui.Caption("Prompt"),
            WorkUi.FillRow(8, prompt, run),
            Ui.Row(8, Ui.Caption("Size"), size, Ui.Caption("Performance"), performance));
        page = Page(Rows("Auto,*,Auto", top, frame, WorkUi.FillRow(8, status, insert, cancel)), 16);
        page.AttachedToVisualTree += (_, _) => WorkUi.Later(() => prompt.Focus());
        var window = Open(shell, title, "🖼", page, new Size(860, 820));
        window.Closed += () =>
        {
            running?.Cancel();
            clock.Stop();
            (picture.Source as IDisposable)?.Dispose();
            foreach (var unused in generated)
                if (unused != inserted)
                    try
                    {
                        System.IO.File.Delete(unused);
                    }
                    catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                    {
                        ActivityLog.Error("Calculator", $"Unused generated image not deleted: {unused}", ex);
                    }
        };
    }
}
