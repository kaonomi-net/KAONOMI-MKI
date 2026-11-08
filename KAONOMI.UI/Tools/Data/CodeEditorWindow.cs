using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using KAONOMI.UI.Desk;
using TextMate = KAONOMI.Chat.Services.CodeHighlighter;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// The code window (the code blocks of the calculator tape): a window of the desktop with the language and the code,
/// and the code in the syntax colours of the language below it, coloured again as it is typed. Three uses: insert
/// code, edit a block (the action is then "Update"), and generate code: a prompt, ✨ GENERATE (also Ctrl / ⌘ + Enter)
/// asks the LLM through <c>generate</c>, and the code written can be changed before it is inserted. <see cref="Show"/>
/// gives the language ("" for plain text) and the code, or null when cancelled or closed.
/// </summary>
public static class CodeEditorWindow
{
    private const string PlainText = "text";

    public static Task<(string Language, string Code)?> Show(Shell shell, string title, string action, string language, string code,
        Func<string, string, CancellationToken, Task<(string Language, string Code)>>? generate = null)
    {
        var done = new TaskCompletionSource<(string Language, string Code)?>();
        var languages = new[] { PlainText }.Concat(TextMate.Languages).ToList();
        if (generate is not null)
            languages.Insert(0, "auto");
        var current = string.IsNullOrEmpty(language) ? (generate is null ? PlainText : "auto") : TextMate.Normalize(language);
        if (!languages.Contains(current))
            languages.Insert(1, current);

        var languageBox = new ComboBox { MinWidth = 180 };
        void FillLanguages(string selected)
        {
            languageBox.ItemsSource = languages.ToList();
            languageBox.SelectedIndex = languages.IndexOf(selected);
        }
        FillLanguages(current);
        string Selected() => languageBox.SelectedItem as string ?? "auto";
        string Chosen() => Selected() is var l && l is not PlainText and not "auto" ? l : string.Empty;

        var codeBox = CodeSyntax.Editor(generate is null ? "Type or paste the code" : "The generated code shows here; it can be changed before it is inserted");
        codeBox.Text = code;
        // The colours of the code, read again a moment after it is typed.
        var preview = CodeSyntax.View();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        void Colour()
        {
            timer.Stop();
            preview.ShowCode(Chosen(), codeBox.Text ?? string.Empty);
        }
        timer.Tick += (_, _) => Colour();
        codeBox.TextChanged += (_, _) =>
        {
            timer.Stop();
            timer.Start();
        };
        languageBox.SelectionChanged += (_, _) => Colour();
        Colour();

        var status = Ui.Label(string.Empty, Ui.Muted, 11, wrap: true);
        var ok = DataUi.Action(action.ToUpperInvariant(), () => { }, primary: true);
        ok.IsEnabled = code.Trim().Length > 0;
        codeBox.TextChanged += (_, _) => ok.IsEnabled = (codeBox.Text ?? string.Empty).Trim().Length > 0;
        var cancel = DataUi.Action("CANCEL", () => { });

        var top = Ui.Column(8);
        Control focus = codeBox;
        CancellationTokenSource? running = null;
        if (generate is not null)
        {
            var prompt = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                MinHeight = 80,
                VerticalContentAlignment = VerticalAlignment.Top,
                PlaceholderText = "Describe the code to generate, e.g. \"a Python function that returns the area of a circle, with a test\""
            };
            var run = DataUi.Action("✨ GENERATE", () => { }, "Ask the LLM to write the code (Ctrl / ⌘ + Enter)", primary: true);
            run.VerticalAlignment = VerticalAlignment.Bottom;
            async void Generate()
            {
                if (string.IsNullOrWhiteSpace(prompt.Text) || running is not null)
                    return;
                running = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                run.IsEnabled = false;
                status.Text = "✨ The LLM is writing the code…";
                try
                {
                    var (written, generated) = await generate(prompt.Text.Trim(), Selected(), running.Token);
                    var id = string.IsNullOrEmpty(written) ? PlainText : TextMate.Normalize(written);
                    if (!languages.Contains(id))
                        languages.Insert(1, id);
                    FillLanguages(id);
                    codeBox.Text = generated;
                    Colour();
                    status.Text = "✓ Code written: change it if needed, then " + action;
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
                }
            }
            run.Click += (_, _) => Generate();
            DataUi.CtrlEnter(prompt, Generate);
            run.Margin = new Thickness(8, 0, 0, 0);
            top.Children.Add(DataUi.Field("Prompt", Ui.Dock((run, Dock.Right), (prompt, Dock.Left))));
            focus = prompt;
        }
        top.Children.Add(Ui.Row(8, DataUi.Caption("Language"), languageBox));

        var buttons = DataUi.Bar([], status, ok, cancel);
        buttons.Margin = new Thickness(0, 8, 0, 0);
        // The code typed above, its colours below (the bar between them moves).
        var codes = new Grid { RowDefinitions = new RowDefinitions("2*,Auto,*") };
        var splitter = new GridSplitter { Height = 5, Background = Avalonia.Media.Brushes.Transparent, ResizeDirection = GridResizeDirection.Rows };
        Grid.SetRow(splitter, 1);
        Grid.SetRow(preview, 2);
        codes.Children.Add(codeBox);
        codes.Children.Add(splitter);
        codes.Children.Add(preview);
        top.Margin = new Thickness(0, 0, 0, 8);
        var content = Ui.Dock((top, Dock.Top), (buttons, Dock.Bottom), (codes, Dock.Top));
        content.Margin = new Thickness(12, 10);
        DataUi.FocusWhenShown(focus);

        var window = shell.OpenControl($"code-editor#{Guid.NewGuid():N}", title, "⌨", content, new Size(820, generate is null ? 640 : 760));
        window.Closed += () =>
        {
            timer.Stop();
            running?.Cancel();
            done.TrySetResult(null);
        };
        ok.Click += (_, _) =>
        {
            done.TrySetResult((Chosen(), (codeBox.Text ?? string.Empty).Replace("\r\n", "\n").TrimEnd()));
            shell.Close(content);
        };
        cancel.Click += (_, _) => shell.Close(content);
        return done.Task;
    }
}
