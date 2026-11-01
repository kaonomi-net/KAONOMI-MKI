using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.ChatGodot.Eve;
using KAONOMI.ChatGodot.Tools.Work;

namespace KAONOMI.ChatGodot.Tools.Data;

/// <summary>
/// The right-click menu of every text area of the chat (<see cref="TextEdit"/>), in place of the menu of Godot and in
/// the look of the menus of /bpmnai: undo, redo, cut, copy, paste, select all, annotate the selection, then the AI on
/// the selection (or the whole text when nothing is selected): translate into English, Français, Deutsch or
/// Nederlands; analyse the text and the points to improve (the images it names too); summarise; analyse its emotions.
/// The AI is the vision model of config.yml (<c>visionmodel</c>, mistral-small3.2 by default), through Ollama.
/// A text area whose own menu is turned off (it handles its right click itself, as the console) is left alone.
/// </summary>
internal static class TextMenu
{
    /// <summary>The languages of the translation: as written in the language itself, and as named to the model.</summary>
    private static readonly (string Native, string English)[] Languages =
        [("English", "English"), ("Français", "French"), ("Deutsch", "German"), ("Nederlands", "Dutch")];

    private const string Watched = "text_menu";

    /// <summary>
    /// Takes the right click of <paramref name="edit"/> (called for each right click, before the text area handles it):
    /// true when the menu of the chat was shown instead of the one of Godot.
    /// </summary>
    public static bool RightClicked(TextEdit edit, InputEventMouseButton press)
    {
        // A text area that turned its menu off handles the right click itself.
        if (!edit.HasMeta(Watched))
        {
            if (!edit.ContextMenuEnabled)
                return false;
            edit.SetMeta(Watched, true);
            edit.ContextMenuEnabled = false;
        }
        Show(edit, press.GlobalPosition);
        return true;
    }

    private static void Show(TextEdit edit, Vector2 at)
    {
        if (edit.GetViewport() is not { } viewport)
            return;
        edit.GrabFocus();
        var overlay = new Overlay();
        viewport.AddChild(overlay);
        overlay.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var selection = edit.HasSelection() ? edit.GetSelectedText() : string.Empty;
        var hasSelection = selection.Trim().Length > 0;
        var target = new Target(edit, hasSelection);
        var hasText = target.Text.Trim().Length > 0;
        var editable = edit.Editable;
        void Run(TextEdit.MenuItems item) => edit.MenuOption((int)item);

        var items = new List<Overlay.MenuItem?>
        {
            new("Undo", editable && edit.HasUndo(), () => Run(TextEdit.MenuItems.Undo), "⌘Z"),
            new("Redo", editable && edit.HasRedo(), () => Run(TextEdit.MenuItems.Redo), "⇧⌘Z"),
            null,
            new("Cut", editable && hasSelection, () => Run(TextEdit.MenuItems.Cut), "⌘X"),
            new("Copy", hasSelection, () => Run(TextEdit.MenuItems.Copy), "⌘C"),
            new("Paste", editable, () => Run(TextEdit.MenuItems.Paste), "⌘V"),
            new("Select all", edit.Text.Length > 0, () => Run(TextEdit.MenuItems.SelectAll), "⌘A"),
            null,
            new("📝 Annotate selection…", hasSelection, () =>
                Annotations.Open(edit, AnnotationTarget.Text(selection.Trim(), Shell.Current?.WindowOf(edit)?.Title ?? "Chat"))),
            null,
            new(hasSelection ? "🌐 Translate the selection" : "🌐 Translate the text", false, null)
        };
        foreach (var (native, english) in Languages)
            items.Add(new Overlay.MenuItem(native, hasText, () => _ = TranslateAsync(edit, target, native, english), Indent: 1));
        items.Add(null);
        items.Add(new Overlay.MenuItem("🧐 Analyse · points to improve", hasText, () => _ = AnalyseAsync(edit, target, Kind.Analysis)));
        items.Add(new Overlay.MenuItem("📄 Summary", hasText, () => _ = AnalyseAsync(edit, target, Kind.Summary)));
        items.Add(new Overlay.MenuItem("💗 Emotional analysis", hasText, () => _ = AnalyseAsync(edit, target, Kind.Emotions)));
        overlay.Menu(items, at, closed: () => overlay.QueueFree());
    }

    /// <summary>
    /// What the AI works on: the selection when there is one, else the whole text, with where it was, to put a result
    /// back only while the text has not changed.
    /// </summary>
    private sealed class Target
    {
        private readonly string _whole;
        private readonly (int FromLine, int FromColumn, int ToLine, int ToColumn)? _range;

        public Target(TextEdit edit, bool selection)
        {
            _whole = edit.Text;
            if (selection)
                _range = (edit.GetSelectionFromLine(), edit.GetSelectionFromColumn(), edit.GetSelectionToLine(), edit.GetSelectionToColumn());
            Text = selection ? edit.GetSelectedText() : edit.Text;
        }

        public string Text { get; }
        public bool IsSelection => _range is not null;

        /// <summary>Puts <paramref name="text"/> in place of the target (or under it); false when the text area changed meanwhile.</summary>
        public bool Put(TextEdit edit, string text, bool replace)
        {
            if (!GodotObject.IsInstanceValid(edit) || !edit.Editable || edit.Text != _whole)
                return false;
            if (_range is { } r)
            {
                if (replace)
                    edit.Select(r.FromLine, r.FromColumn, r.ToLine, r.ToColumn);
                else
                {
                    edit.Deselect();
                    edit.SetCaretLine(r.ToLine);
                    edit.SetCaretColumn(r.ToColumn);
                }
            }
            else if (replace)
                edit.SelectAll();
            else
            {
                edit.Deselect();
                edit.SetCaretLine(edit.GetLineCount() - 1);
                edit.SetCaretColumn(edit.GetLine(edit.GetLineCount() - 1).Length);
            }
            edit.InsertTextAtCaret(replace ? text : "\n" + text);
            edit.GrabFocus();
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
    /// Runs <paramref name="work"/> under the waiting popup of /work over the window of <paramref name="edit"/> (Cancel,
    /// 10 minutes at most); the error message when it fails, null when it was cancelled.
    /// </summary>
    private static async Task<(T? Result, string? Error, bool Cancelled)> BusyAsync<T>(TextEdit edit, string heading, string message,
        Func<CancellationToken, Task<T>> work)
    {
        var layer = new Control { MouseFilter = Control.MouseFilterEnum.Ignore, Theme = Eve.Eve.Shared };
        edit.GetViewport().AddChild(layer);
        layer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var busy = BusyPopup.Show(layer, heading, message, cancel);
        try
        {
            return (await work(cancel.Token), null, false);
        }
        catch (OperationCanceledException) when (busy.IsCancelledByUser)
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
            busy.Finish();
            if (GodotObject.IsInstanceValid(layer))
                layer.QueueFree();
        }
    }

    // ── Translate ──

    private static async Task TranslateAsync(TextEdit edit, Target target, string native, string english)
    {
        if (Shell.Current is not { } shell)
            return;
        var model = _visionModel;
        var (translation, error, cancelled) = await BusyAsync(edit, $"🌐 {native}", $"Translation into {english} ({target.Text.Length} characters)…", async ct =>
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
        if (cancelled || !GodotObject.IsInstanceValid(edit))
            return;
        var what = target.IsSelection ? "THE SELECTION" : "THE TEXT";
        WorkWindows.Translation(shell, target.Text, native, model, translation, error,
            replace: text => target.Put(edit, text, replace: true),
            insert: text => target.Put(edit, text, replace: false),
            heading: $"🌐 {native}", title: $"Translation · {native}",
            replaceLabel: edit.Editable ? $"REPLACE {what}" : "REPLACE (READ ONLY)",
            insertLabel: target.IsSelection ? "INSERT BELOW" : "ADD AT THE END");
    }

    // ── Analyse, summarise, emotions ──

    private static async Task AnalyseAsync(TextEdit edit, Target target, Kind kind)
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
        var (markdown, error, cancelled) = await BusyAsync(edit, heading, message, async ct =>
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
        WorkWindows.Evaluation(shell, target.Text, model, markdown, error, heading, $"{subtitle} · {model}");
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
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                bytes = await http.GetByteArrayAsync(link, cancellationToken);
            }
            else
            {
                var path = link.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) ? Uri.UnescapeDataString(new Uri(link).LocalPath) : link;
                if (path.StartsWith('~'))
                    path = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), path[1..].TrimStart('/', '\\'));
                if (!File.Exists(path))
                    return null;
                bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            }
            // The format read from the first bytes of the file (GIF is not read by Godot: left out).
            var image = new Image();
            static bool Starts(byte[] b, params byte[] magic) => b.Length >= magic.Length && magic.Select((m, i) => b[i] == m).All(x => x);
            var loaded = Starts(bytes, 0x89, 0x50, 0x4E, 0x47) ? image.LoadPngFromBuffer(bytes)
                : Starts(bytes, 0xFF, 0xD8) ? image.LoadJpgFromBuffer(bytes)
                : Starts(bytes, 0x42, 0x4D) ? image.LoadBmpFromBuffer(bytes)
                : bytes.Length > 12 && Starts(bytes, 0x52, 0x49, 0x46, 0x46) && bytes[8] == 0x57 && bytes[9] == 0x45 ? image.LoadWebpFromBuffer(bytes)
                : Error.FileUnrecognized;
            if (loaded != Error.Ok || image.IsEmpty())
                return null;
            var longest = Math.Max(image.GetWidth(), image.GetHeight());
            if (longest > 1568)
                image.Resize(image.GetWidth() * 1568 / longest, image.GetHeight() * 1568 / longest, Image.Interpolation.Lanczos);
            return image.SavePngToBuffer();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UriFormatException or UnauthorizedAccessException or TaskCanceledException)
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
