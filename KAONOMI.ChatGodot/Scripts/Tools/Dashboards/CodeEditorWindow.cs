using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using KAONOMI.ChatGodot.Eve;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;
using TextMate = KAONOMI.Chat.Services.CodeHighlighter;

namespace KAONOMI.ChatGodot.Tools.Dashboards;

/// <summary>
/// Port of the Avalonia <c>CodeWindow</c> (the code blocks of the calculator tape): a window of the desktop with the
/// language and the code, in the syntax colours of the language as it is typed (the editor is its own preview). Three
/// uses: insert code, edit a block (the action is then "Update"), and generate code: a prompt, ✨ GENERATE (also Ctrl
/// / ⌘ + Enter) asks the LLM through <c>generate</c>, and the code written can be changed before it is inserted.
/// <see cref="Show"/> gives the language ("" for plain text) and the code, or null when cancelled or closed.
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

        var languageBox = new OptionButton { CustomMinimumSize = new Vector2(180, 0) };
        void FillLanguages(string selected)
        {
            languageBox.Clear();
            foreach (var l in languages)
                languageBox.AddItem(l);
            languageBox.Selected = languages.IndexOf(selected);
        }
        FillLanguages(current);
        string Chosen() => languageBox.Selected >= 0 && languages[languageBox.Selected] is var l && l is not PlainText and not "auto" ? l : string.Empty;
        string Selected() => languageBox.Selected >= 0 ? languages[languageBox.Selected] : "auto";

        var highlighter = new CodeSyntaxHighlighter(Chosen());
        var codeBox = CodeEditors.Editor();
        codeBox.SyntaxHighlighter = highlighter;
        codeBox.Text = code;
        codeBox.IndentAutomatic = true;
        codeBox.PlaceholderText = generate is null ? "Type or paste the code" : "The generated code shows here; it can be changed before it is inserted";
        languageBox.ItemSelected += _ =>
        {
            highlighter.Language = Chosen();
            codeBox.SyntaxHighlighter = null;
            codeBox.SyntaxHighlighter = highlighter;
        };

        var status = EveStyle.Text_("", EveStyle.Muted, 11, wrap: true);
        status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var ok = Ui.Action(action.ToUpperInvariant(), () => { }, primary: true);
        ok.Disabled = code.Trim().Length == 0;
        codeBox.TextChanged += () => ok.Disabled = codeBox.Text.Trim().Length == 0;
        var cancel = Ui.Action("CANCEL", () => { });

        var column = Ui.Column(8);
        Control focus = codeBox;
        CancellationTokenSource? running = null;
        if (generate is not null)
        {
            var prompt = new TextEdit
            {
                WrapMode = TextEdit.LineWrappingMode.Boundary,
                CustomMinimumSize = new Vector2(0, 80),
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                PlaceholderText = "Describe the code to generate, e.g. \"a Python function that returns the area of a circle, with a test\""
            };
            var run = Ui.Action("✨ GENERATE", () => { }, primary: true, tooltip: "Ask the LLM to write the code (Ctrl / ⌘ + Enter)");
            run.SizeFlagsVertical = Control.SizeFlags.ShrinkEnd;
            async void Generate()
            {
                if (string.IsNullOrWhiteSpace(prompt.Text) || running is not null)
                    return;
                running = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                run.Disabled = true;
                status.Text = "✨ The LLM is writing the code…";
                try
                {
                    var (written, generated) = await generate(prompt.Text.Trim(), Selected(), running.Token);
                    var id = string.IsNullOrEmpty(written) ? PlainText : TextMate.Normalize(written);
                    if (!languages.Contains(id))
                        languages.Insert(1, id);
                    if (!GodotObject.IsInstanceValid(codeBox))
                        return;
                    FillLanguages(id);
                    highlighter.Language = Chosen();
                    codeBox.Text = generated;
                    status.Text = "✓ Code written: change it if needed, then " + action;
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
                        run.Disabled = false;
                }
            }
            run.Pressed += Generate;
            prompt.GuiInput += e =>
            {
                if (e is InputEventKey { Pressed: true, Keycode: Key.Enter or Key.KpEnter } key && (key.CtrlPressed || key.MetaPressed))
                {
                    prompt.AcceptEvent();
                    Generate();
                }
            };
            column.AddChild(Ui.Field("Prompt", Ui.Row(8, prompt, run)));
            focus = prompt;
        }
        column.AddChild(Ui.Row(8, EveStyle.Caption("Language"), languageBox));
        column.AddChild(Ui.FillHeight(codeBox));
        column.AddChild(Ui.Row(8, status, ok, cancel));
        var content = Ui.Margin(column, 12, 10, 12, 10);

        var window = shell.OpenControl($"code-editor#{Guid.NewGuid():N}", title, "⌨", content, new Vector2(820, generate is null ? 640 : 760));
        window.Closed += () =>
        {
            running?.Cancel();
            done.TrySetResult(null);
        };
        ok.Pressed += () =>
        {
            done.TrySetResult((Chosen(), codeBox.Text.Replace("\r\n", "\n").TrimEnd()));
            shell.Close(content);
        };
        cancel.Pressed += () => shell.Close(content);
        focus.CallDeferred(Control.MethodName.GrabFocus);
        return done.Task;
    }
}
