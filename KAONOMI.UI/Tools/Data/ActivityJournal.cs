using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// The journal of the activities (port of the one of the Godot chat): each SAVE done in a window (a button "SAVE",
/// "💾…", or Ctrl/⌘+S in the window in front when it has such a button) is saved on the Kaonomi server
/// (<c>PUT /useractivity</c>, index useractivities) with the name of the window, its link (its key: its command,
/// /intel, /mindmap…; <c>/activity</c> lists them and opens them again) and a description (the button, the window and
/// the object it shows). The same window twice within 2 s counts once.
/// </summary>
public static class ActivityJournal
{
    /// <summary>The command of the window of the journal (not journaled itself).</summary>
    public const string Command = "/activity";

    private static readonly Regex SaveText = new(@"\bSAVE\b|💾", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Dictionary<string, long> LastRecorded = [];
    private static bool _hooked;

    /// <summary>Raised after an activity is saved on the server (the window of the journal reads them again).</summary>
    public static event Action? Recorded;

    /// <summary>Watches the SAVE buttons of every window, and Ctrl/⌘+S (once).</summary>
    public static void Hook()
    {
        if (_hooked)
            return;
        _hooked = true;
        Button.ClickEvent.AddClassHandler<Button>((button, _) =>
        {
            if (TextOf(button) is { } text && SaveText.IsMatch(text))
                Record(DataUi.WindowOf(button), Label(text));
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        InputElement.KeyDownEvent.AddClassHandler<TopLevel>((_, e) =>
        {
            if (e.Key != Key.S || !DataUi.IsCommand(e) || Shell.Current?.Deck.Front is not { } window)
                return;
            if (HasSaveButton(window.Content))
                Record(window, "SAVE (Ctrl+S)");
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private static string? TextOf(Button button) => button.Content switch
    {
        string s => s,
        TextBlock t => t.Text,
        _ => null
    };

    /// <summary>True when <paramref name="root"/> holds a SAVE button that can be pressed.</summary>
    private static bool HasSaveButton(Control root) =>
        root.GetVisualDescendants().OfType<Button>().Any(b => b.IsEffectivelyEnabled && b.IsEffectivelyVisible && TextOf(b) is { } t && SaveText.IsMatch(t));

    /// <summary>The text of a button without its icon, in capitals (💾 alone: SAVE).</summary>
    private static string Label(string text)
    {
        var label = new string(text.SkipWhile(c => !char.IsLetter(c)).ToArray()).Trim().ToUpperInvariant();
        return label.Length == 0 ? "SAVE" : label;
    }

    /// <summary>Saves the activity <paramref name="action"/> done in <paramref name="window"/> (null: the chat) on the server.</summary>
    public static void Record(DeskWindow? window, string action)
    {
        var link = window?.Key ?? "chat";
        // An annotation journals itself (the first 50 characters of its text): not a second time as a SAVE.
        if (link == Command || window?.ViewModel is KAONOMI.Chat.ViewModels.AnnotationDialogViewModel || KaonomiAssistantService.Shared is not { } server)
            return;
        var now = Environment.TickCount64;
        if (LastRecorded.TryGetValue(link, out var last) && now - last < 2000)
            return;
        LastRecorded[link] = now;
        var name = window?.Title ?? "Chat";
        var shown = window?.ViewModel is { } vm ? ShownObject(vm) : null;
        var entry = new UserActivityEntry
        {
            AppName = "KAONOMI.Chat",
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            Date = DateTimeOffset.Now,
            Action = "SAVE",
            WindowName = name,
            Link = link,
            Description = $"{action} in {name}" + (shown is not null && !name.Contains(shown, StringComparison.OrdinalIgnoreCase) ? $": {shown}" : string.Empty)
        };
        _ = SaveAsync(server, entry);
    }

    private static async Task SaveAsync(KaonomiAssistantService server, UserActivityEntry entry)
    {
        try
        {
            var result = await server.SaveUserActivityAsync(entry);
            if (result.IsSuccess)
                Ui.OnMain(() => Recorded?.Invoke());
            else
                ActivityLog.Error("Journal", $"Activity not saved ({entry.Description}): {result.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ActivityLog.Error("Journal", $"Activity not saved ({entry.Description})", ex);
        }
    }

    /// <summary>
    /// The object a window shows, read from its view model: the title or name of its selection (SelectedResult,
    /// Selected…), else its own title, name or file; null when there is none.
    /// </summary>
    private static string? ShownObject(object vm)
    {
        static string? Text(object? o, string[] names)
        {
            if (o is null)
                return null;
            foreach (var name in names)
                if (o.GetType().GetProperty(name)?.GetValue(o) is string s && !string.IsNullOrWhiteSpace(s))
                    return s.Trim();
            return null;
        }
        string[] titles = ["Title", "DisplayTitle", "Name", "IdeaTitle", "FileName"];
        foreach (var selection in new[] { "SelectedResult", "SelectedEntry", "Selected", "SelectedNode" })
            if (vm.GetType().GetProperty(selection)?.GetValue(vm) is { } selected && Text(selected, titles) is { } title)
                return title;
        return Text(vm, titles);
    }
}
