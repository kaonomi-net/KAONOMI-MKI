using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.UI.Chat;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// The right-click menu of the text boxes of the chat, in place of the menu of the theme: undo, redo, cut, copy, paste,
/// select all, annotate the selection, then (in the multi-line boxes) the AI on the selection, or the whole text when
/// nothing is selected: translate into English, Français, Deutsch or Nederlands; analyse the text and the points to
/// improve (the images it names too); summarise; analyse its emotions. The selectable texts get copy, select all and
/// annotate the selection. The AI is the vision model of config.yml (<c>visionmodel</c>, mistral-small3.2 by default),
/// through Ollama. A box with a menu of its own (the console) is left alone.
/// </summary>
internal static class TextMenu
{
    /// <summary>The languages of the translation: as written in the language itself, and as named to the model.</summary>
    private static readonly (string Native, string English)[] Languages =
        [("English", "English"), ("Français", "French"), ("Deutsch", "German"), ("Nederlands", "Dutch")];

    private static readonly string[] DefaultFlyouts = ["DefaultTextBoxContextFlyout", "HorizontalTextBoxContextFlyout", "SelectableTextBlockContextFlyout"];

    private static bool _hooked;

    /// <summary>Takes the right click of every text box and selectable text (once).</summary>
    public static void Hook()
    {
        if (_hooked)
            return;
        _hooked = true;
        Control.ContextRequestedEvent.AddClassHandler<TextBox>(Requested, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        Control.ContextRequestedEvent.AddClassHandler<SelectableTextBlock>(Requested, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }

    /// <summary>True when <paramref name="control"/> has a menu of its own (not the one of the theme).</summary>
    private static bool HasOwnMenu(Control control)
    {
        if (control.ContextMenu is not null)
            return true;
        if (control.ContextFlyout is not { } flyout)
            return false;
        // A menu given by a view is a local value; the one of the theme comes from a style.
        var priority = Avalonia.Diagnostics.AvaloniaObjectExtensions.GetDiagnostic(control, Control.ContextFlyoutProperty).Priority;
        if (priority > Avalonia.Data.BindingPriority.LocalValue)
            return false;
        foreach (var key in DefaultFlyouts)
            if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) == true && ReferenceEquals(value, flyout))
                return false;
        return true;
    }

    private static void Requested(Control control, ContextRequestedEventArgs e)
    {
        if (e.Handled || HasOwnMenu(control))
            return;
        var items = control switch
        {
            TextBox box => BoxItems(box),
            SelectableTextBlock block => BlockItems(block),
            _ => null
        };
        if (items is null)
            return;
        e.Handled = true;
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(control);
    }

    private static MenuItem Item(string header, bool enabled, Action? click, string? gesture = null, int indent = 0)
    {
        var item = new MenuItem { Header = new string(' ', indent * 4) + header, IsEnabled = enabled };
        if (gesture is not null)
            item.InputGesture = KeyGesture.Parse(gesture);
        if (click is not null)
            item.Click += (_, _) => click();
        return item;
    }

    private static List<object> BlockItems(SelectableTextBlock block)
    {
        var selection = block.SelectedText ?? string.Empty;
        var has = selection.Trim().Length > 0;
        return
        [
            Item("Copy", has, () => block.Copy(), "Ctrl+C"),
            Item("Select all", (block.Text?.Length ?? 0) > 0 || block.Inlines?.Count > 0, block.SelectAll, "Ctrl+A"),
            new Separator(),
            Item("📝 Annotate selection…", has, () => Annotations.OpenText(block, selection))
        ];
    }

    private static List<object> BoxItems(TextBox box)
    {
        var selection = box.SelectedText ?? string.Empty;
        var hasSelection = selection.Trim().Length > 0;
        var target = new Target(box, hasSelection);
        var hasText = target.Text.Trim().Length > 0;
        var editable = !box.IsReadOnly && box.IsEnabled;
        var items = new List<object>
        {
            Item("Undo", editable && box.CanUndo, box.Undo, "Ctrl+Z"),
            Item("Redo", editable && box.CanRedo, box.Redo, "Ctrl+Shift+Z"),
            new Separator(),
            Item("Cut", editable && hasSelection, box.Cut, "Ctrl+X"),
            Item("Copy", hasSelection, box.Copy, "Ctrl+C"),
            Item("Paste", editable, box.Paste, "Ctrl+V"),
            Item("Select all", (box.Text?.Length ?? 0) > 0, box.SelectAll, "Ctrl+A"),
            new Separator(),
            Item("📝 Annotate selection…", hasSelection, () => Annotations.OpenText(box, selection))
        };
        if (!box.AcceptsReturn)
            return items;
        items.Add(new Separator());
        items.Add(Item(hasSelection ? "🌐 Translate the selection" : "🌐 Translate the text", false, null));
        foreach (var (native, english) in Languages)
            items.Add(Item(native, hasText, () => _ = TranslateAsync(box, target, native, english), indent: 1));
        items.Add(new Separator());
        items.Add(Item("🧐 Analyse · points to improve", hasText, () => _ = AnalyseAsync(box, target, Kind.Analysis)));
        items.Add(Item("📄 Summary", hasText, () => _ = AnalyseAsync(box, target, Kind.Summary)));
        items.Add(Item("💗 Emotional analysis", hasText, () => _ = AnalyseAsync(box, target, Kind.Emotions)));
        return items;
    }

    /// <summary>
    /// What the AI works on: the selection when there is one, else the whole text, with where it was, to put a result
    /// back only while the text has not changed.
    /// </summary>
    private sealed class Target
    {
        private readonly string _whole;
        private readonly (int Start, int End)? _range;

        public Target(TextBox box, bool selection)
        {
            _whole = box.Text ?? string.Empty;
            if (selection)
            {
                var start = Math.Clamp(Math.Min(box.SelectionStart, box.SelectionEnd), 0, _whole.Length);
                var end = Math.Clamp(Math.Max(box.SelectionStart, box.SelectionEnd), 0, _whole.Length);
                _range = (start, end);
            }
            Text = _range is { } r ? _whole[r.Start..r.End] : _whole;
        }

        public string Text { get; }
        public bool IsSelection => _range is not null;

        /// <summary>Puts <paramref name="text"/> in place of the target (or under it); false when the text box changed meanwhile.</summary>
        public bool Put(TextBox box, string text, bool replace)
        {
            if (box.IsReadOnly || !box.IsEnabled || (box.Text ?? string.Empty) != _whole)
                return false;
            string result;
            int caret;
            if (_range is { } r)
            {
                result = replace ? _whole[..r.Start] + text + _whole[r.End..] : _whole[..r.End] + "\n" + text + _whole[r.End..];
                caret = replace ? r.Start + text.Length : r.End + 1 + text.Length;
            }
            else
            {
                result = replace ? text : _whole + "\n" + text;
                caret = result.Length;
            }
            box.Text = result;
            box.CaretIndex = Math.Min(caret, result.Length);
            box.Focus();
            return true;
        }
    }

    private enum Kind { Analysis, Summary, Emotions }

    // ── The model ──

    private static OllamaModelService? _ollama;
    private static string _visionModel = ChatSettings.DefaultVisionModel;

    /// <summary>The Ollama of config.yml and its vision model (installed), or an error saying what is missing.</summary>
    private static async Task<(OllamaModelService Ollama, string Model)> ModelAsync(CancellationToken cancellationToken)
    {
        if (_ollama is null)
        {
            var settings = ChatSettings.Load();
            _ollama = new OllamaModelService(settings.OllamaUrl);
            if (!string.IsNullOrWhiteSpace(settings.VisionModel))
                _visionModel = settings.VisionModel.Trim();
        }
        var models = await _ollama.ListModelsAsync(cancellationToken);
        var model = models.FirstOrDefault(m => m.StartsWith(_visionModel, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"The model {_visionModel} is not installed in Ollama (ollama pull {_visionModel}).");
        return (_ollama, model);
    }

    /// <summary>
    /// Runs <paramref name="work"/> with a waiting window (Cancel, 10 minutes at most); the error message when it
    /// fails, cancelled when the user cancelled.
    /// </summary>
    private static async Task<(T? Result, string? Error, bool Cancelled)> BusyAsync<T>(Shell shell, string heading, string message,
        Func<CancellationToken, Task<T>> work)
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var byUser = false;
        var content = new Border { Padding = new Thickness(16) };
        var cancelButton = DataUi.Action("CANCEL", () =>
        {
            byUser = true;
            cancel.Cancel();
        });
        cancelButton.HorizontalAlignment = HorizontalAlignment.Right;
        content.Child = Ui.Column(12,
            Ui.Label(message, Ui.Text, 13, wrap: true),
            new ProgressBar { IsIndeterminate = true, Height = 4, MinHeight = 4 },
            cancelButton);
        var key = $"text-menu-busy#{Guid.NewGuid():N}";
        var done = false;
        var window = shell.OpenControl(key, heading, "⏳", content, new Size(440, 170));
        // Closing the waiting window cancels too.
        window.Closed += () =>
        {
            if (done || cancel.IsCancellationRequested)
                return;
            byUser = true;
            cancel.Cancel();
        };
        try
        {
            return (await work(cancel.Token), null, false);
        }
        catch (OperationCanceledException) when (byUser)
        {
            return (default, null, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException or IOException
                                       or System.Text.Json.JsonException)
        {
            return (default, ex is OperationCanceledException ? "no answer within 10 minutes" : ex.Message, false);
        }
        finally
        {
            // Closed without cancelling what has ended.
            done = true;
            if (shell.Deck.Find(key) is { } open)
                shell.Deck.Close(open);
        }
    }

    // ── Translate ──

    private static async Task TranslateAsync(TextBox box, Target target, string native, string english)
    {
        if (Shell.Current is not { } shell)
            return;
        var model = _visionModel;
        var (translation, error, cancelled) = await BusyAsync(shell, $"🌐 {native}", $"Translation into {english} ({target.Text.Length} characters)…", async ct =>
        {
            var (ollama, name) = await ModelAsync(ct);
            model = name;
            ActivityLog.Info("TextMenu", $"Translation of {target.Text.Length} chars into {english} asked to {name}");
            var answer = await ollama.ChatAsync(name,
                $"You are a professional translator. Translate the text given by the user into {english}. Keep the formulas, "
                + "numbers, units, symbols, names and line breaks exactly as they are. Answer with the translation only: no "
                + "comment, no quotes, no Markdown.",
                target.Text, ct);
            return Unwrap(answer);
        });
        if (cancelled)
            return;
        var what = target.IsSelection ? "THE SELECTION" : "THE TEXT";
        TranslationWindow(shell, native, model, translation, error,
            replace: text => target.Put(box, text, replace: true),
            insert: text => target.Put(box, text, replace: false),
            replaceLabel: !box.IsReadOnly ? $"REPLACE {what}" : "REPLACE (READ ONLY)",
            insertLabel: target.IsSelection ? "INSERT BELOW" : "ADD AT THE END");
    }

    /// <summary>The translation: the text (selectable), REPLACE, INSERT, COPY, CLOSE; the error instead when it failed.</summary>
    private static void TranslationWindow(Shell shell, string native, string model, string? translation, string? error,
        Func<string, bool> replace, Func<string, bool> insert, string replaceLabel, string insertLabel)
    {
        var content = new Border { Padding = new Thickness(12) };
        var subtitle = Ui.Label($"Translation into {native} · {model}", Ui.Muted, 11);
        subtitle.Margin = new Thickness(0, 0, 0, 8);
        if (error is not null || translation is null)
        {
            content.Child = Ui.Column(8, subtitle, Ui.Label($"Not translated: {error}", Ui.Red, 13, wrap: true));
        }
        else
        {
            var text = new TextBox { Text = translation, AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top };
            var status = Ui.Label(string.Empty, Ui.Muted, 11, wrap: true);
            void Put(Func<string, bool> put)
            {
                if (put(text.Text ?? string.Empty))
                    shell.Close(content);
                else
                {
                    status.Text = "The text changed meanwhile, or it is read only: the translation was not put back (COPY it).";
                    status.Foreground = Ui.Red;
                }
            }
            var buttons = DataUi.Bar([], status,
                DataUi.Action(replaceLabel, () => Put(replace), primary: true),
                DataUi.Action(insertLabel, () => Put(insert)),
                DataUi.Action("COPY", () => Dialogs.Copy(text.Text ?? string.Empty)),
                DataUi.Action("CLOSE", () => shell.Close(content)));
            buttons.Margin = new Thickness(0, 8, 0, 0);
            content.Child = Ui.Dock((subtitle, Dock.Top), (buttons, Dock.Bottom), (text, Dock.Top));
        }
        shell.OpenControl($"translation#{Guid.NewGuid():N}", $"Translation · {native}", "🌐", content, new Size(720, 520));
    }

    // ── Analyse, summarise, emotions ──

    private static async Task AnalyseAsync(TextBox box, Target target, Kind kind)
    {
        if (Shell.Current is not { } shell)
            return;
        var (heading, title, subtitle) = kind switch
        {
            Kind.Analysis => ("🧐 Analysis", "Analysis", "What works, then the points to improve and how · the images named in the text too"),
            Kind.Summary => ("📄 Summary", "Summary", "The essentials of the text and its key points"),
            _ => ("💗 Emotional analysis", "Emotional analysis", "The tone, the emotions and their intensity, where they show")
        };
        var system = kind switch
        {
            Kind.Analysis =>
                "You are a demanding and benevolent editor. Analyse the text given by the user: first what works, then the "
                + "points to improve (clarity, structure, logic of the argument, precision, style, spelling and grammar), each "
                + "with the passage concerned and a concrete suggestion. When images come with the text, describe what they "
                + "show, how they serve the text and what could be improved in them or in their use. Answer in Markdown "
                + "(headings, lists), in the language of the text.",
            Kind.Summary =>
                "Summarise the text given by the user: a short paragraph with the essentials, then its key points as a "
                + "list. Keep the facts, figures and names; add nothing. Answer in Markdown, in the language of the text.",
            _ =>
                "Analyse the emotions of the text given by the user: its overall tone; the emotions it carries (joy, sadness, "
                + "anger, fear, surprise, disgust, trust, anticipation) with an intensity from 0 to 10 in a table; how they "
                + "change along the text; the words and passages that carry them; the effect on a reader. Answer in Markdown, "
                + "in the language of the text."
        };
        var model = _visionModel;
        var images = kind == Kind.Analysis ? ImagesOf(target.Text) : [];
        var message = images.Count > 0 ? $"{target.Text.Length} characters and {images.Count} image(s) sent to the model…" : $"{target.Text.Length} characters sent to the model…";
        var (markdown, error, cancelled) = await BusyAsync(shell, heading, message, async ct =>
        {
            var (ollama, name) = await ModelAsync(ct);
            model = name;
            var pictures = new List<byte[]>();
            foreach (var image in images)
                if (await LoadImageAsync(image, ct) is { } png)
                    pictures.Add(png);
            ActivityLog.Info("TextMenu", $"{title} of {target.Text.Length} chars ({pictures.Count} image(s)) asked to {name}");
            var prompt = $"Texte :\n\"\"\"\n{target.Text}\n\"\"\"";
            var answer = pictures.Count > 0
                ? await ollama.ChatWithImagesAsync(name, system, prompt + $"\n\n{pictures.Count} image(s) named in the text are attached, in their order.", pictures, ct)
                : await ollama.ChatAsync(name, system, prompt, ct);
            return LatexText.Convert(answer).Trim();
        });
        if (cancelled)
            return;

        var content = new Border { Padding = new Thickness(12) };
        var caption = Ui.Label($"{subtitle} · {model}", Ui.Muted, 11, wrap: true);
        caption.Margin = new Thickness(0, 0, 0, 8);
        Control body = error is not null || markdown is null
            ? Ui.Label($"No answer: {error}", Ui.Red, 13, wrap: true)
            : Ui.Scroll(MarkdownView.Build(markdown));
        var buttons = DataUi.Bar([], null,
            DataUi.Action("COPY", () => Dialogs.Copy(markdown ?? string.Empty)),
            DataUi.Action("CLOSE", () => shell.Close(content)));
        buttons.Margin = new Thickness(0, 8, 0, 0);
        content.Child = Ui.Dock((caption, Dock.Top), (buttons, Dock.Bottom), (body, Dock.Top));
        shell.OpenControl($"text-analysis#{Guid.NewGuid():N}", heading, heading.Split(' ')[0], content, new Size(760, 600));
    }

    // ── The images named in the text ──

    private static readonly Regex ImageReference = new(
        @"(?:!\[[^\]]*\]\((?<link>[^)\s]+)\))|(?<link>(?:https?://|file:///)[^\s""'<>()]+\.(?:png|jpe?g|webp|gif|bmp)(?:\?[^\s""'<>()]*)?)|(?<link>(?:[A-Za-z]:[\\/]|/|~[\\/])[^\s""'<>|*?]+\.(?:png|jpe?g|webp|gif|bmp))",
        RegexOptions.IgnoreCase);

    /// <summary>The images the text names: Markdown images, links and paths of picture files (8 at most).</summary>
    private static List<string> ImagesOf(string text) =>
        ImageReference.Matches(text).Select(m => m.Groups["link"].Value.Trim()).Where(l => l.Length > 0).Distinct().Take(8).ToList();

    /// <summary>The picture of <paramref name="link"/> (a URL or a file) as a PNG of 1568 px at most, null when it cannot be read.</summary>
    private static async Task<byte[]?> LoadImageAsync(string link, CancellationToken cancellationToken)
    {
        try
        {
            byte[] bytes;
            if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                bytes = await http.GetByteArrayAsync(link, cancellationToken);
            }
            else
            {
                var path = link.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) ? Uri.UnescapeDataString(new Uri(link).LocalPath) : link;
                if (path.StartsWith('~'))
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\'));
                if (!File.Exists(path))
                    return null;
                bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            }
            using var input = new MemoryStream(bytes);
            using var bitmap = new Bitmap(input);
            var (width, height) = (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            var longest = Math.Max(width, height);
            using var output = new MemoryStream();
            if (longest > 1568)
            {
                using var scaled = bitmap.CreateScaledBitmap(new PixelSize(Math.Max(1, width * 1568 / longest), Math.Max(1, height * 1568 / longest)), BitmapInterpolationMode.HighQuality);
                scaled.Save(output);
            }
            else
                bitmap.Save(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException or UnauthorizedAccessException or TaskCanceledException
                                       or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            ActivityLog.Error("TextMenu", $"Image not read: {link}", ex);
            return null;
        }
    }

    /// <summary>An answer taken out of the code block or the quotes it may be wrapped in.</summary>
    private static string Unwrap(string answer)
    {
        var result = answer.Trim();
        if (result.StartsWith("```", StringComparison.Ordinal))
            result = Regex.Replace(result, @"^```[^\n]*\n|\n?```$", string.Empty).Trim();
        if (result.Length > 1 && result[0] is '"' or '«' or '“' && result[^1] is '"' or '»' or '”')
            result = result[1..^1].Trim();
        return result;
    }
}
