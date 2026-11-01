using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// A view of a view model of the Avalonia chat, in this Godot version: it builds the content of its window. Each view is
/// found by its <see cref="ToolViewAttribute"/> (no list to keep up to date): opening a view model opens the view made
/// for its type.
/// </summary>
public interface IToolView
{
    /// <summary>The content of the window showing <paramref name="viewModel"/>.</summary>
    Control Build(object viewModel, Shell shell);
}

/// <summary>Marks a view (<see cref="IToolView"/>) of the view models of <see cref="ViewModel"/>.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ToolViewAttribute(Type viewModel, string title, string icon, int width = 1000, int height = 700) : Attribute
{
    public Type ViewModel { get; } = viewModel;
    public string Title { get; } = title;
    public string Icon { get; } = icon;
    public int Width { get; } = width;
    public int Height { get; } = height;

    /// <summary>True: one window per object (an image, an entity…); false: one window for the tool (opening it again brings it back).</summary>
    public bool PerObject { get; init; }
}

/// <summary>
/// What the views can ask of the window of the chat: open a view model (its view in a window of the desktop), open any
/// content in a window, close a window, run a command as if typed in the chat. <see cref="Current"/> is the one of the
/// running chat.
/// </summary>
public sealed class Shell
{
    private static readonly Dictionary<Type, (IToolView View, ToolViewAttribute Info)> Views = Discover();

    public Shell(MainViewModel chat, WindowDeck deck, Node root)
    {
        Chat = chat;
        Deck = deck;
        Root = root;
        Current = this;
    }

    /// <summary>The shell of the running chat.</summary>
    public static Shell Current { get; private set; } = null!;

    /// <summary>The chat (its commands open the tools).</summary>
    public MainViewModel Chat { get; }

    /// <summary>The desktop of the windows.</summary>
    public WindowDeck Deck { get; }

    /// <summary>The root of the scene (dialogs are added there).</summary>
    public Node Root { get; }

    /// <summary>True when a view exists for the view models of <paramref name="type"/>.</summary>
    public static bool HasView(Type type) => Views.ContainsKey(type);

    /// <summary>
    /// Opens <paramref name="viewModel"/> in a window of the desktop, in front, with the view made for its type; a
    /// window of the same tool (or of the same object, for the views per object) is brought back instead. Null when no
    /// view exists for it.
    /// </summary>
    public DeckWindow? Open(object viewModel, string? key = null, string? title = null)
    {
        if (!Views.TryGetValue(viewModel.GetType(), out var view))
        {
            GD.PushWarning($"No view for {viewModel.GetType().Name}");
            return null;
        }
        var info = view.Info;
        key ??= info.PerObject ? $"{info.ViewModel.Name}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(viewModel)}" : info.ViewModel.Name;
        if (Deck.Find(key) is { } open)
        {
            Deck.BringToFront(open);
            (viewModel as IDisposable)?.Dispose();
            return open;
        }
        Control content;
        try
        {
            content = view.View.Build(viewModel, this);
        }
        catch (Exception ex)
        {
            GD.PushError($"{info.Title}: {ex}");
            content = Eve.Text_($"{info.Title} could not be shown: {ex.Message}", Eve.Red, 13, wrap: true);
        }
        var window = Deck.Open(key, title ?? info.Title, info.Icon, content, new Vector2(info.Width, info.Height), viewModel);
        // A view model shown by two windows (a clone of a window of an object) is disposed with the last of them.
        window.Closed += () =>
        {
            if (!Deck.Windows.Any(w => ReferenceEquals(w.ViewModel, viewModel)))
                (viewModel as IDisposable)?.Dispose();
        };
        return window;
    }

    // ── Clones ──

    /// <summary>The key the next window of a tool opens with (a clone: <c>/wiki#2</c>), taken by <see cref="KeyFor"/>.</summary>
    private string? _nextKey;

    /// <summary>The tool of <paramref name="key"/>: <c>/wiki</c> for <c>/wiki#2</c>; any other key as it is.</summary>
    public static string BaseKey(string key)
    {
        var hash = key.IndexOf('#');
        return key.StartsWith('/') && hash > 0 ? key[..hash] : key;
    }

    /// <summary>The key to open the tool <paramref name="key"/> with: the one of a clone being made, else the tool's own.</summary>
    public string KeyFor(string key)
    {
        if (_nextKey is { } next && BaseKey(next) == key)
        {
            _nextKey = null;
            return next;
        }
        return key;
    }

    /// <summary>A key of <paramref name="baseKey"/> no window has: <c>#2</c>, <c>#3</c>…</summary>
    public string FreeKey(string baseKey)
    {
        for (var n = 2; ; n++)
            if (Deck.Find($"{baseKey}#{n}") is null)
                return $"{baseKey}#{n}";
    }

    /// <summary>
    /// Opens the tool <paramref name="key"/> (<c>/wiki#2</c>: a clone) with <paramref name="openTool"/>, which runs
    /// the command of the tool; the window opened is returned (null when the tool did not open).
    /// </summary>
    public DeckWindow? OpenToolAs(string key, Action<string> openTool)
    {
        var baseKey = BaseKey(key);
        _nextKey = key == baseKey ? null : key;
        try
        {
            openTool(baseKey);
        }
        finally
        {
            _nextKey = null;
        }
        return Deck.Find(key);
    }

    /// <summary>
    /// Clones <paramref name="window"/>: a tool opens again in a second window of its own (its own data, as when it is
    /// opened by its command), a window of an object shows the same object in a second window; the clone is put a
    /// little lower and to the right of the window, with its size. Null when the window cannot be cloned.
    /// </summary>
    public DeckWindow? Clone(DeckWindow window, Action<string> openTool)
    {
        var baseKey = BaseKey(window.Key);
        DeckWindow? clone = null;
        if (baseKey.StartsWith('/'))
            clone = OpenToolAs(FreeKey(baseKey), openTool);
        else if (window.ViewModel is { } viewModel && Views.ContainsKey(viewModel.GetType()))
            clone = Open(viewModel, FreeKey(window.Key), window.Title);
        if (clone is null)
            return null;
        Deck.MoveTo(clone, new Rect2(window.Rect.Position + new Vector2(36, 36), window.Rect.Size));
        Deck.BringToFront(clone);
        return clone;
    }

    /// <summary>Brings back the window of <paramref name="key"/> when it is open (true), else does nothing (false).</summary>
    public bool TryBringBack(string key)
    {
        if (Deck.Find(key) is not { } window)
            return false;
        Deck.BringToFront(window);
        return true;
    }

    /// <summary>Opens <paramref name="content"/> in a window of the desktop (or brings back the one of <paramref name="key"/>).</summary>
    public DeckWindow OpenControl(string key, string title, string icon, Control content, Vector2 size) =>
        Deck.Open(key, title, icon, content, size);

    /// <summary>Closes the window showing <paramref name="viewModel"/> (or the content <paramref name="content"/>).</summary>
    public void Close(object viewModelOrContent)
    {
        if (Deck.Windows.FirstOrDefault(w => ReferenceEquals(w.ViewModel, viewModelOrContent) || ReferenceEquals(w.Content, viewModelOrContent)) is { } window)
            Deck.Close(window);
    }

    /// <summary>The window holding <paramref name="control"/>, or null.</summary>
    public DeckWindow? WindowOf(Control control) =>
        Deck.Windows.FirstOrDefault(w => w.Content == control || w.Content.IsAncestorOf(control));

    /// <summary>
    /// True when the keyboard shortcuts of <paramref name="control"/> may act: it is shown, its window is the active
    /// one of the deck (the one in front of all; every window of the deck receives every key, so the others must
    /// stand aside), or it is not in a window of the deck; and no text field of the desktop itself (the prompt of the
    /// chat) is being typed in.
    /// </summary>
    public bool HasKeyboard(Control control)
    {
        if (!control.IsVisibleInTree())
            return false;
        if (WindowOf(control) is { } window && !ReferenceEquals(Deck.Front, window))
            return false;
        return Root.GetViewport()?.GuiGetFocusOwner() is not (LineEdit or TextEdit or SpinBox);
    }

    /// <summary>Runs <paramref name="text"/> as if it were typed in the chat (a command or a prompt).</summary>
    public void RunCommand(string text) => Chat.RunCommand(text);

    private static Dictionary<Type, (IToolView, ToolViewAttribute)> Discover()
    {
        var views = new Dictionary<Type, (IToolView, ToolViewAttribute)>();
        foreach (var type in Assembly.GetExecutingAssembly().GetTypes())
        {
            if (type.IsAbstract || !typeof(IToolView).IsAssignableFrom(type) || type.GetCustomAttribute<ToolViewAttribute>() is not { } info)
                continue;
            if (Activator.CreateInstance(type) is IToolView view)
                views[info.ViewModel] = (view, info);
        }
        return views;
    }
}
