using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.ChatGodot.Tools.Knowledge;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The URLs shown anywhere in the chat open in the browser of the chat (<see cref="WebWindow"/>; <see cref="Hook"/>, once): the links of the rich texts (the
/// answers, the Markdown views), a click on a text holding a URL (a menu when it holds several), a click on a URL of a
/// read-only text area, Cmd+click (Ctrl+click) on a URL of a field or of a text area being edited.
/// </summary>
public static partial class Links
{
    // http(s)://…, ftp://…, file://…, www.…: up to a space, a quote or a bracket.
    [GeneratedRegex(@"\b(?:(?:https?|ftp|file)://|www\.)[^\s<>""'`\[\]{}]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    /// <summary>The URLs of <paramref name="text"/> (with where they are), the punctuation ending a sentence left out.</summary>
    public static IEnumerable<(int Start, string Url)> Find(string? text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;
        foreach (Match match in UrlPattern().Matches(text))
        {
            var url = match.Value.TrimEnd('.', ',', ';', ':', '!', '?');
            // A closing parenthesis ends the URL unless the URL opens one (the pages of Wikipedia).
            while (url.EndsWith(')') && url.Count(c => c == ')') > url.Count(c => c == '('))
                url = url[..^1];
            if (url.Length > 8)
                yield return (match.Index, url);
        }
    }

    /// <summary>Opens <paramref name="url"/> in the browser of the chat (www.… as https).</summary>
    public static void Open(string url)
    {
        if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        // The web opens at once; a file or another scheme (the text may come from the model or the server) is opened
        // by the system only once the user has said so.
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            WebWindow.Open(url);
            return;
        }
        _ = ConfirmAndOpenAsync(url);
    }

    private static async System.Threading.Tasks.Task ConfirmAndOpenAsync(string url)
    {
        // Never a program, a script, a shortcut, a share of the network or a handler of the system, even when asked.
        if (!KAONOMI.Chat.Services.PictureLibrary.IsSafeToOpen(url, out var reason))
        {
            ActivityLog.Error("Links", $"Link refused ({reason}): {url}");
            await Dialogs.Message("Link not opened", $"This link is not opened: {reason}.\n\n{(url.Length > 160 ? url[..160] + "…" : url)}");
            return;
        }
        var shown = url.Length > 160 ? url[..160] + "…" : url;
        if (!await Dialogs.Confirm("Open outside the chat", $"This link is not a web page: it would be opened by the system.\n\n{shown}", "Open"))
            return;
        ActivityLog.Info("Links", $"Link opened by the system after confirmation: {url}");
        OS.ShellOpen(url);
    }

    /// <summary>The BBCode of the plain <paramref name="text"/>: escaped, its URLs as links.</summary>
    public static string Bbcode(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;
        var bbcode = new StringBuilder();
        var at = 0;
        foreach (var (start, url) in Find(text))
        {
            bbcode.Append(MarkdownBbcode.Escape(text[at..start]));
            bbcode.Append($"[url={MarkdownBbcode.Escape(url)}][color=#{Eve.Cyan.ToHtml(false)}][u]{MarkdownBbcode.Escape(url)}[/u][/color][/url]");
            at = start + url.Length;
        }
        bbcode.Append(MarkdownBbcode.Escape(text[at..]));
        return bbcode.ToString();
    }

    /// <summary>Makes the URLs of every text of the tree open (the controls added from now on).</summary>
    public static void Hook(SceneTree tree) =>
        tree.NodeAdded += node =>
        {
            if (node is not Control control || control.HasMeta("links_hooked"))
                return;
            switch (control)
            {
                case RichTextLabel { BbcodeEnabled: true } rich:
                    control.SetMeta("links_hooked", true);
                    rich.MetaClicked += meta => Open(meta.AsString());
                    break;
                case Label or RichTextLabel:
                    control.SetMeta("links_hooked", true);
                    HookPlain(control);
                    break;
                case TextEdit edit:
                    control.SetMeta("links_hooked", true);
                    HookArea(edit);
                    break;
                case LineEdit line:
                    control.SetMeta("links_hooked", true);
                    HookLine(line);
                    break;
            }
        };

    private static string PlainText(Control control) => control switch
    {
        Label label => label.Text,
        RichTextLabel rich => rich.Text,
        _ => string.Empty
    };

    /// <summary>
    /// A label (or a selectable text without BBCode): while its text holds a URL, the hand over it and a click opens
    /// it (a menu of them when there are several); a text selected by dragging stays selected.
    /// </summary>
    private static void HookPlain(Control control)
    {
        var filter = control.MouseFilter;
        var cursor = control.MouseDefaultCursorShape;
        var tooltip = control.TooltipText;
        var linked = false;
        // A label has no signal of a change of its text: it is drawn again with each one.
        control.Draw += () =>
        {
            var has = Find(PlainText(control)).Any();
            if (has == linked)
                return;
            linked = has;
            control.MouseFilter = has && filter == Control.MouseFilterEnum.Ignore ? Control.MouseFilterEnum.Pass : filter;
            control.MouseDefaultCursorShape = has ? Control.CursorShape.PointingHand : cursor;
            control.TooltipText = has && tooltip.Length == 0 ? "Click to open the link" : tooltip;
        };
        var down = Vector2.Zero;
        control.GuiInput += e =>
        {
            if (!linked || e is not InputEventMouseButton { ButtonIndex: MouseButton.Left } button)
                return;
            if (button.Pressed)
            {
                down = button.Position;
                return;
            }
            if (button.Position.DistanceTo(down) > 4 || control is RichTextLabel rich && rich.GetSelectedText().Length > 0)
                return;
            var urls = Find(PlainText(control)).Select(found => found.Url).Distinct().ToList();
            if (urls.Count == 0)
                return;
            control.AcceptEvent();
            if (urls.Count == 1)
                Open(urls[0]);
            else
            {
                var menu = new ContextMenu();
                foreach (var url in urls)
                    menu.Item("🔗 " + url, () => Open(url));
                menu.Show(control, button.Position);
            }
        };
    }

    /// <summary>The URL of <paramref name="text"/> at <paramref name="column"/>, or null.</summary>
    private static string? At(string text, int column) =>
        Find(text).Where(found => column >= found.Start && column <= found.Start + found.Url.Length).Select(found => found.Url).FirstOrDefault();

    private static bool Command(InputEventWithModifiers e) => OperatingSystem.IsMacOS() ? e.MetaPressed : e.CtrlPressed;

    /// <summary>
    /// A text area: a click on a URL opens it when the area is read-only (a drag still selects), Cmd+click when it is
    /// edited; the hand over a URL that a click opens.
    /// </summary>
    private static void HookArea(TextEdit edit)
    {
        var cursor = edit.MouseDefaultCursorShape;
        string? UrlUnder(Vector2 position)
        {
            var at = edit.GetLineColumnAtPos((Vector2I)position, false);
            return at.Y < 0 || at.Y >= edit.GetLineCount() ? null : At(edit.GetLine(at.Y), at.X);
        }
        var down = Vector2.Zero;
        edit.GuiInput += e =>
        {
            switch (e)
            {
                case InputEventMouseMotion motion:
                    var opens = (!edit.Editable || Command(motion)) && UrlUnder(motion.Position) is not null;
                    edit.MouseDefaultCursorShape = opens ? Control.CursorShape.PointingHand : cursor;
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } press:
                    down = press.Position;
                    // Cmd+click on a URL of a text edited: the caret stays where it was.
                    if (edit.Editable && Command(press) && UrlUnder(press.Position) is { } url)
                    {
                        edit.AcceptEvent();
                        Open(url);
                    }
                    break;
                case InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release
                    when !edit.Editable && release.Position.DistanceTo(down) <= 4 && !edit.HasSelection():
                    if (UrlUnder(release.Position) is { } link)
                        Open(link);
                    break;
            }
        };
    }

    /// <summary>A field: Cmd+click on a URL of its text opens it.</summary>
    private static void HookLine(LineEdit line)
    {
        line.GuiInput += e =>
        {
            // The click has put the caret on the character clicked when it is released.
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } release && Command(release)
                && At(line.Text, line.CaretColumn) is { } url)
                Open(url);
        };
    }
}
