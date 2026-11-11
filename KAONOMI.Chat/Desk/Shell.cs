using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.UI.Desk;

/// <summary>A view of a view model of the chat: it builds the content of its window (the same contract as in the Godot chat).</summary>
public interface IToolView
{
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

    /// <summary>True: one window per object; false: one window for the tool (opening it again brings it back).</summary>
    public bool PerObject { get; init; }
}

/// <summary>
/// What the views can ask of the chat: open a view model (its view in a window of the desktop), open any content in a
/// window, close a window, run a command as if typed. Each view is found by its <see cref="ToolViewAttribute"/>; a view
/// model without a view of its own is shown by <see cref="AutoView"/>, built from its properties and commands.
/// </summary>
public sealed class Shell
{
    private static readonly Dictionary<Type, (IToolView View, ToolViewAttribute Info)> Views = Discover();

    public Shell(MainViewModel chat, DeskArea deck)
    {
        Chat = chat;
        Deck = deck;
        Current = this;
    }

    public static Shell Current { get; private set; } = null!;

    public MainViewModel Chat { get; }

    public DeskArea Deck { get; }

    public static bool HasView(Type type) => Views.ContainsKey(type);

    /// <summary>The tool views written by hand (the others are generated).</summary>
    public static IEnumerable<Type> ViewModelsWithViews => Views.Keys;

    /// <summary>The key of the next window opened for a clone (<c>/wiki#2</c>).</summary>
    private string? _nextKey;

    public static string BaseKey(string key)
    {
        var hash = key.IndexOf('#');
        return key.StartsWith('/') && hash > 0 ? key[..hash] : key;
    }

    public string KeyFor(string key)
    {
        if (_nextKey is { } next && BaseKey(next) == key)
        {
            _nextKey = null;
            return next;
        }
        return key;
    }

    public string FreeKey(string baseKey)
    {
        for (var n = 2; ; n++)
            if (Deck.Find($"{baseKey}#{n}") is null)
                return $"{baseKey}#{n}";
    }

    /// <summary>Opens <paramref name="viewModel"/> in a window of the desktop, or brings back the window already showing its tool.</summary>
    public DeskWindow Open(object viewModel, string? key = null, string? title = null)
    {
        var type = viewModel.GetType();
        Views.TryGetValue(type, out var view);
        var info = view.Info ?? new ToolViewAttribute(type, AutoView.TitleOf(type), "▣", 820, 620);
        key ??= info.PerObject ? $"{type.Name}#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(viewModel)}" : type.Name;
        if (Deck.Find(key) is { } open)
        {
            Deck.BringToFront(open);
            if (!ReferenceEquals(open.ViewModel, viewModel))
                (viewModel as IDisposable)?.Dispose();
            return open;
        }
        Control content;
        try
        {
            content = view.View is { } v ? v.Build(viewModel, this) : AutoView.Build(viewModel, this);
        }
        catch (Exception ex)
        {
            content = new Border { Padding = new Thickness(16), Child = Ui.Label($"{info.Title} could not be shown: {ex.Message}", Ui.Red, 13, wrap: true) };
            KAONOMI.Chat.Services.ActivityLog.Error("UI", $"{info.Title} could not be shown", ex);
        }
        var window = Deck.Open(key, title ?? info.Title, info.Icon, content, new Size(info.Width, info.Height), viewModel);
        window.Closed += () =>
        {
            if (!Deck.Windows.Any(w => ReferenceEquals(w.ViewModel, viewModel)))
                (viewModel as IDisposable)?.Dispose();
        };
        return window;
    }

    /// <summary>Opens the tool <paramref name="key"/> (<c>/wiki#2</c>: a clone) with <paramref name="openTool"/>.</summary>
    public DeskWindow? OpenToolAs(string key, Action<string> openTool)
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

    /// <summary>The same tool in a second window (a window of an object: the same object), a little lower and to the right.</summary>
    public DeskWindow? Clone(DeskWindow window, Action<string> openTool)
    {
        var baseKey = BaseKey(window.Key);
        DeskWindow? clone = null;
        if (baseKey.StartsWith('/'))
            clone = OpenToolAs(FreeKey(baseKey), openTool);
        else if (window.ViewModel is { } viewModel)
            clone = Open(viewModel, FreeKey(window.Key), window.Title);
        if (clone is null || ReferenceEquals(clone, window))
            return null;
        Deck.Place(clone, new Rect(window.Rect.Position + new Point(36, 36), window.Rect.Size));
        Deck.BringToFront(clone);
        return clone;
    }

    public bool TryBringBack(string key)
    {
        if (Deck.Find(key) is not { } window)
            return false;
        Deck.BringToFront(window);
        return true;
    }

    public DeskWindow OpenControl(string key, string title, string icon, Control content, Size size) =>
        Deck.Open(key, title, icon, content, size);

    /// <summary>Closes the window showing <paramref name="viewModelOrContent"/>.</summary>
    public void Close(object viewModelOrContent)
    {
        if (Deck.Windows.FirstOrDefault(w => ReferenceEquals(w.ViewModel, viewModelOrContent) || ReferenceEquals(w.Content, viewModelOrContent)) is { } window)
            Deck.Close(window);
    }

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
