using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using CodeHighlighter = KAONOMI.Chat.Services.CodeHighlighter;
using Timer = Godot.Timer;
using KAONOMI.Chat.Services;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Work;

/// <summary>
/// The windows /work opens on the desktop besides its own: the result of an evaluation or of the AI review, the result
/// of a translation, a spelling correction or a rewriting, the code window (insert, edit, generate) and the image
/// generation window (Fooocus). Each is a window of the desktop of its own (the Avalonia chat opened them as windows
/// owned by Work); the dialogs give their result through a callback instead of ShowDialog.
/// </summary>
internal static class WorkWindows
{
    private static int _count;

    /// <summary>Opens <paramref name="content"/> in a new window of the desktop.</summary>
    public static DeckWindow Open(Shell shell, string title, string icon, Control content, Vector2 size) =>
        shell.OpenControl($"work-window-{++_count}", title, icon, content, size);

    /// <summary>The text something was made from, quoted with a colored bar on its left (at most six lines).</summary>
    private static Control Quote(string text, Color bar, bool mono)
    {
        var label = Eve.Eve.Text_(text, Eve.Eve.Text with { A = 0.75f }, 12, wrap: true);
        label.MaxLinesVisible = 6;
        label.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        if (mono)
            label.AddThemeFontOverride("font", Eve.Eve.Mono);
        var line = new ColorRect { Color = bar, CustomMinimumSize = new Vector2(3, 0) };
        return Ui.Row(10, line, Ui.Fill(label));
    }

    private static Label Heading(string text)
    {
        var label = Eve.Eve.Text_(text, Eve.Eve.TextBright, 18);
        label.AddThemeFontOverride("font", Eve.Eve.Display);
        return label;
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
            ? Ui.Markdown(markdown ?? string.Empty)
            : Eve.Eve.Text_($"{plain} failed: {error}", Eve.Eve.Red, 13, wrap: true);
        var status = Eve.Eve.Text_("", Eve.Eve.Muted, 11);
        var copy = Ui.Action("COPY", () =>
        {
            Dialogs.Copy(markdown ?? string.Empty);
            status.Text = "✓ Copied";
        });
        copy.Disabled = error is not null;
        Control? page = null;
        var close = Ui.Action("CLOSE", () => shell.Close(page!), primary: true);
        var header = Ui.Column(6,
            Heading(heading),
            Eve.Eve.Text_(subtitle ?? $"Mathematical and formal criticism, then ideas to improve the text, without the solution · {model}", Eve.Eve.Muted, 11, wrap: true),
            Quote(text, Eve.Eve.Amber, mono: true));
        var buttons = Ui.Row(8, Ui.Spring(), status, copy, close);
        // An action offered on the result (the rewriting of an AI-Check, for one): it closes the window, then runs.
        if (actionLabel is not null && action is not null && error is null)
        {
            var act = Ui.Action(actionLabel, () =>
            {
                shell.Close(page!);
                action();
            }, primary: true);
            buttons.AddChild(act);
            buttons.MoveChild(act, buttons.GetChildCount() - 2);
        }
        page = Ui.Margin(Ui.Column(12, header, Eve.Eve.Separator(), Ui.FillHeight(Ui.Scroll(Ui.Margin(result, 0, 0, 10, 0))), buttons), 18);
        Open(shell, plain, heading.Split(' ')[0], page, new Vector2(680, 620));
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
        var result = new TextEdit
        {
            Text = translation ?? $"{title ?? "The translation"} failed: {error}",
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            Editable = error is null,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 160)
        };
        result.AddThemeFontSizeOverride("font_size", 14);
        var status = Eve.Eve.Text_("", Eve.Eve.Muted, 11, wrap: true);
        status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        Control? page = null;
        void Put(Func<string, bool> action)
        {
            if (action(result.Text))
                shell.Close(page!);
            else
                status.Text = "The tape changed since the selection: copy the text instead.";
        }
        var replaceButton = Ui.Action(replaceLabel, () => Put(replace), primary: true,
            tooltip: "The text takes the place of the selected text in the tape (Ctrl / ⌘ + Z undoes it)");
        var insertButton = Ui.Action(insertLabel, () => Put(insert), tooltip: "The text is added under the selected text, which is kept");
        var copy = Ui.Action("COPY", () =>
        {
            Dialogs.Copy(result.Text);
            status.Text = "✓ Copied";
        });
        replaceButton.Disabled = insertButton.Disabled = copy.Disabled = error is not null;
        var close = Ui.Action("CLOSE", () => shell.Close(page!));
        var header = Ui.Column(6,
            Heading(heading ?? $"🌐 Translation into {language}"),
            Eve.Eve.Text_(model, Eve.Eve.Muted, 11),
            Quote(text, Eve.Eve.Muted, mono: false));
        page = Ui.Margin(Ui.Column(12, header, result, Ui.Row(8, status, replaceButton, insertButton, copy, close)), 18);
        page.Ready += () => Callable.From(() => replaceButton.GrabFocus()).CallDeferred();
        Open(shell, title ?? $"Translation · {language}", heading?.Split(' ')[0] ?? "🌐", page, new Vector2(640, 520));
    }

    /// <summary>"Plain text" entry of the language list (no coloring).</summary>
    private const string PlainText = "text";

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
        var languageBox = new Dropdown { CustomMinimumSize = new Vector2(180, 0) };
        void FillLanguages(string selected) => languageBox.SetItems(languages, Math.Max(0, languages.IndexOf(selected)));
        FillLanguages(current);
        string Selected() => languageBox.Selected >= 0 && languageBox.Selected < languages.Count ? languages[languageBox.Selected] : PlainText;
        string LanguageChosen() => Selected() is var l && l is not PlainText and not "auto" ? l : string.Empty;

        var codeBox = new CodeEdit
        {
            Text = code,
            PlaceholderText = generate is null ? "Type or paste the code" : "The generated code shows here; it can be changed before it is inserted",
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 160),
            GuttersDrawLineNumbers = true
        };
        codeBox.AddThemeFontOverride("font", Eve.Eve.Mono);
        codeBox.AddThemeFontSizeOverride("font_size", 13);
        var preview = new RichTextLabel { BbcodeEnabled = true, FitContent = true, ScrollActive = false, SelectionEnabled = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        preview.AddThemeFontOverride("mono_font", Eve.Eve.Mono);
        preview.AddThemeFontOverride("normal_font", Eve.Eve.Mono);
        void Refresh() => preview.Text = WorkPalette.CodeBbcode(LanguageChosen(), codeBox.Text);
        Refresh();
        var status = Eve.Eve.Text_("", Eve.Eve.Muted, 11, wrap: true);
        status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        Control? page = null;
        var ok = Ui.Action(action.ToUpperInvariant(), () =>
        {
            done(LanguageChosen(), codeBox.Text.Replace("\r\n", "\n").TrimEnd());
            shell.Close(page!);
        }, primary: generate is null);
        ok.Disabled = code.Trim().Length == 0;
        var cancel = Ui.Action("CANCEL", () => shell.Close(page!));

        // The preview follows the code a moment after the last key, and the language at once.
        var timer = new Timer { WaitTime = 0.25, OneShot = true };
        timer.Timeout += Refresh;
        codeBox.TextChanged += () =>
        {
            ok.Disabled = codeBox.Text.Trim().Length == 0;
            timer.Start();
        };
        languageBox.ItemSelected += _ => Refresh();

        var top = Ui.Column(8);
        top.AddChild(timer);
        CancellationTokenSource? running = null;
        if (generate is not null)
        {
            var prompt = new TextEdit
            {
                WrapMode = TextEdit.LineWrappingMode.Boundary,
                CustomMinimumSize = new Vector2(0, 80),
                PlaceholderText = "Describe the code to generate, e.g. \"a Python function that returns the area of a circle, with a test\"",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
            };
            Button? run = null;
            async void Generate()
            {
                if (string.IsNullOrWhiteSpace(prompt.Text) || running is not null)
                    return;
                running = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                run!.Disabled = true;
                page!.MouseDefaultCursorShape = Control.CursorShape.Busy;
                status.Text = "✨ The LLM is writing the code…";
                try
                {
                    var (written, generated) = await generate(prompt.Text.Trim(), Selected(), running.Token);
                    if (!GodotObject.IsInstanceValid(codeBox))
                        return;
                    var id = string.IsNullOrEmpty(written) ? PlainText : CodeHighlighter.Normalize(written);
                    if (!languages.Contains(id))
                        languages.Insert(1, id);
                    FillLanguages(id);
                    codeBox.Text = generated;
                    Refresh();
                    status.Text = "✓ Code written: change it if needed, then Insert";
                    Eve.Eve.StylePrimary(ok);
                }
                catch (Exception ex) when (ex is OperationCanceledException or System.Net.Http.HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    if (GodotObject.IsInstanceValid(status))
                        status.Text = ex is OperationCanceledException ? "No answer after 10 minutes." : $"Not generated: {ex.Message}";
                }
                finally
                {
                    running?.Dispose();
                    running = null;
                    if (GodotObject.IsInstanceValid(run))
                    {
                        run.Disabled = false;
                        page.MouseDefaultCursorShape = Control.CursorShape.Arrow;
                    }
                }
            }
            run = Ui.Action("✨ GENERATE", Generate, primary: true, tooltip: "Ask the LLM to write the code (Ctrl / ⌘ + Enter)");
            run.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
            prompt.GuiInput += e =>
            {
                if (e is InputEventKey { Pressed: true, Keycode: Key.Enter or Key.KpEnter } key && (OS.GetName() == "macOS" ? key.MetaPressed : key.CtrlPressed))
                {
                    prompt.AcceptEvent();
                    Generate();
                }
            };
            top.AddChild(Eve.Eve.Caption("Prompt"));
            top.AddChild(Ui.Row(8, prompt, run));
            prompt.Ready += () => Callable.From(() => prompt.GrabFocus()).CallDeferred();
        }
        else
            codeBox.Ready += () => Callable.From(() => codeBox.GrabFocus()).CallDeferred();
        top.AddChild(Ui.Row(8, Eve.Eve.Caption("Language"), languageBox));

        var previewScroll = Ui.Scroll(preview);
        var panes = Ui.Column(4, codeBox, Eve.Eve.Caption("Preview"), previewScroll);
        panes.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        page = Ui.Margin(Ui.Column(10, top, panes, Ui.Row(8, status, ok, cancel)), 16);
        var window = Open(shell, title, "</>", page, new Vector2(820, generate is null ? 640 : 760));
        window.Closed += () => running?.Cancel();
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
        var prompt = new TextEdit
        {
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            CustomMinimumSize = new Vector2(0, 84),
            PlaceholderText = "Describe the image, in English, e.g. \"a clean diagram of a right triangle with its angles, white background, flat style\"",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        var size = new Dropdown { CustomMinimumSize = new Vector2(140, 0) };
        size.SetItems(ImageSizes, 0);
        var performance = new Dropdown { CustomMinimumSize = new Vector2(150, 0), TooltipText = "Speed: about a minute; Quality: twice as long; Extreme Speed, Lightning, Hyper-SD: a few seconds" };
        performance.SetItems(Performances, 0);
        var picture = Ui.Picture(null, 200);
        picture.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var placeholder = Ui.Placeholder("The image shows here; generate it again until it fits, then Insert");
        var frame = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        frame.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Field, Eve.Eve.Edge with { A = 0.4f }, 6));
        frame.AddChild(picture);
        frame.AddChild(placeholder);
        var status = Eve.Eve.Text_("", Eve.Eve.Muted, 11, wrap: true);
        status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var progress = new ProgressBar { Indeterminate = true, ShowPercentage = false, Visible = false, CustomMinimumSize = new Vector2(0, 4) };

        string? path = null;
        string? inserted = null;
        var generated = new List<string>();
        CancellationTokenSource? running = null;
        Control? page = null;
        var insert = Ui.Action("INSERT", () =>
        {
            if (path is null)
                return;
            inserted = path;
            done(path);
            shell.Close(page!);
        });
        insert.Disabled = true;
        var cancel = Ui.Action("CANCEL", () => shell.Close(page!));
        Button? run = null;
        var started = 0UL;
        var clock = new Timer { WaitTime = 1 };
        clock.Timeout += () => status.Text = $"✨ Fooocus is drawing the image… {TimeSpan.FromMilliseconds(Time.GetTicksMsec() - started):m\\:ss}";

        async void Generate()
        {
            if (string.IsNullOrWhiteSpace(prompt.Text) || running is not null)
                return;
            running = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            run!.Disabled = true;
            progress.Visible = true;
            started = Time.GetTicksMsec();
            clock.Start();
            status.Text = "✨ Fooocus is drawing the image… 0:00";
            try
            {
                path = await generate(prompt.Text.Trim(), ImageSizes[Math.Max(0, size.Selected)], Performances[Math.Max(0, performance.Selected)], running.Token);
                generated.Add(path);
                if (!GodotObject.IsInstanceValid(picture))
                    return;
                picture.Texture = AvaloniaBridge.LoadTexture(path, 1400);
                placeholder.Visible = picture.Texture is null;
                insert.Disabled = false;
                Eve.Eve.StylePrimary(insert);
                status.Text = $"✓ Image ready in {(Time.GetTicksMsec() - started) / 1000.0:0} s, saved as {System.IO.Path.GetFileName(path)}: Insert it, or generate again";
            }
            catch (Exception ex) when (ex is OperationCanceledException or System.Net.Http.HttpRequestException or InvalidOperationException
                                           or System.Text.Json.JsonException or System.IO.IOException or FormatException)
            {
                if (GodotObject.IsInstanceValid(status))
                    status.Text = ex is OperationCanceledException ? "Stopped." : $"Not generated: {ex.Message}";
            }
            finally
            {
                running?.Dispose();
                running = null;
                if (GodotObject.IsInstanceValid(run))
                {
                    clock.Stop();
                    run.Disabled = false;
                    progress.Visible = false;
                }
            }
        }
        run = Ui.Action("✨ GENERATE", Generate, primary: true, tooltip: "Ask Fooocus for the image (Ctrl / ⌘ + Enter)");
        run.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
        prompt.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Enter or Key.KpEnter } key && (OS.GetName() == "macOS" ? key.MetaPressed : key.CtrlPressed))
            {
                prompt.AcceptEvent();
                Generate();
            }
        };
        prompt.Ready += () => Callable.From(() => prompt.GrabFocus()).CallDeferred();

        var top = Ui.Column(8,
            Eve.Eve.Caption("Prompt"),
            Ui.Row(8, prompt, run),
            Ui.Row(8, Eve.Eve.Caption("Size"), size, Eve.Eve.Caption("Performance"), performance),
            progress);
        top.AddChild(clock);
        page = Ui.Margin(Ui.Column(10, top, frame, Ui.Row(8, status, insert, cancel)), 16);
        var window = Open(shell, title, "🖼", page, new Vector2(860, 820));
        window.Closed += () =>
        {
            running?.Cancel();
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
