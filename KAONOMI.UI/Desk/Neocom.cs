using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace KAONOMI.UI.Desk;

/// <summary>
/// The bar of commands on the left, as in the Godot chat: the K (double-click: full screen), ›_ opens a box to type a
/// command or a prompt, one icon per tool (a line on its left while its window is open), /config at the bottom.
/// </summary>
public sealed class Neocom : Border
{
    /// <summary>The tools of the bar, in the order of the Godot chat.</summary>
    public static readonly (string Command, string Icon, string Name)[] Tools =
    [
        ("/work", "🧮", "Work (tape, calculator, graph)"),
        ("/pictures", "🖼", "Picture library"),
        ("/fooocus", "🎨", "Fooocus images"),
        ("/relation", "🕸", "Relations"),
        ("/ontology", "🌳", "Ontology"),
        ("/idea", "💡", "Ideas"),
        ("/journal", "📓", "Journal"),
        ("/wiki", "📚", "Wiki"),
        ("/interview", "🎙", "Interviews"),
        ("/bpmnai", "🔀", "AIMN (from BPMN) diagrams"),
        ("/infra", "🏗", "Infrastructure diagrams"),
        ("/mindmap", "🧠", "Mind maps"),
        ("/write", "✍", "Ghost writer"),
        ("/youtube", "📺", "YouTube videos"),
        ("/music", "🎵", "Music player"),
        ("/completion", "☁", "Completions and word cloud"),
        ("/project", "📋", "Projects"),
        ("/intel", "🕵", "Intel"),
        ("/collectionplan", "🕷", "Collection plans (web scraper)"),
        ("/game:solitaire", "✚", "Game 1 · Morpion solitaire"),
        ("/fractal", "❄", "Fractal generator"),
        ("/game:chess", "♞", "Chess against the LLM"),
        ("/game:go", "⚫", "Go against the LLM"),
        ("/immune", "🧬", "Immune system: AI images and Office code"),
        ("/alarm", "⏰", "Alarm clock"),
        ("/calendar", "📅", "Calendar"),
        ("/iq", "🧩", "IQ tests"),
        ("/activity", "🗒", "Activity journal (each SAVE)"),
        ("/folders", "📁", "Folders of windows"),
        ("/cache", "🗃", "Cache"),
        ("/search", "🔎", "Search every index (fuzzy)"),
        ("/json", "🌐", "HTTP JSON client"),
        ("/cmd", "⌨", "Console (cmd.exe)"),
        ("/symbol", "🔣", "Symbolic patterns"),
        ("/remote", "🎛", "Remote (voice and avatar)"),
        ("/system", "🖥", "System activity"),
        ("/log", "📜", "Logs dashboard"),
        ("/token", "🪙", "Tokens dashboard")
    ];

    public static readonly (string Command, string Icon, string Name) Settings = ("/config", "⚙", "Edit config.yml");

    /// <summary>The screen of the themes (not a command of the chat: the window opens it).</summary>
    public static readonly (string Command, string Icon, string Name) Themes = ("themes", "🎨", "Themes: choose the look of the chat");

    private readonly Dictionary<string, Button> _buttons = [];

    /// <summary>Raised with the command of the icon clicked, or with what was typed in the box.</summary>
    public event Action<string>? CommandRequested;

    /// <summary>Raised by a double-click on the K.</summary>
    public event Action? FullScreenRequested;

    public Neocom()
    {
        Width = 54;
        Background = Ui.Brush("Header");
        BorderBrush = Ui.Edge;
        BorderThickness = new Thickness(0, 0, 1, 0);

        var emblem = new TextBlock { Text = "K", FontSize = 22, FontWeight = Avalonia.Media.FontWeight.Bold, Foreground = Ui.Accent, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 6) };
        ToolTip.SetTip(emblem, "KAONOMI · double-click: full screen (again: back)");
        emblem.DoubleTapped += (_, _) => FullScreenRequested?.Invoke();

        var command = new Button { Content = "›_", FontFamily = Ui.Mono, FontSize = 15, Width = 42, HorizontalAlignment = HorizontalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(command, "Type a command (or a prompt) and run it");
        command.Flyout = CommandBox();

        var tools = new StackPanel { Spacing = 1 };
        foreach (var tool in Tools)
            tools.Children.Add(IconButton(tool));
        var scroll = new ScrollViewer { Content = tools, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden };

        var top = Ui.Column(4, emblem, command, new Border { Height = 1, Background = Ui.Edge, Margin = new Thickness(6, 4) });
        var bottom = Ui.Column(2, new Border { Height = 1, Background = Ui.Edge, Margin = new Thickness(6, 4) }, IconButton(Themes), IconButton(Settings));
        bottom.Margin = new Thickness(0, 0, 0, 6);
        Child = Ui.Dock((top, Dock.Top), (bottom, Dock.Bottom), (scroll, Dock.Top));
    }

    private Button IconButton((string Command, string Icon, string Name) tool)
    {
        var button = new Button { Content = tool.Icon, Classes = { "icon" }, HorizontalAlignment = HorizontalAlignment.Center, Focusable = false };
        ToolTip.SetTip(button, $"{tool.Name}  ·  {tool.Command}");
        ToolTip.SetPlacement(button, PlacementMode.Right);
        button.Click += (_, _) => CommandRequested?.Invoke(tool.Command);
        _buttons[tool.Command] = button;
        return button;
    }

    /// <summary>Marks the icon of <paramref name="command"/> while its window is open.</summary>
    public void SetOpen(string command, bool open)
    {
        if (_buttons.TryGetValue(command, out var button))
            button.Classes.Set("open", open);
    }

    private Flyout CommandBox()
    {
        var input = new TextBox
        {
            Width = 460,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            PlaceholderText = "A command (/work, /relation…) or a prompt · Enter: send · Shift+Enter: new line · Esc: close"
        };
        var flyout = new Flyout { Placement = PlacementMode.RightEdgeAlignedTop };
        void Send()
        {
            var text = input.Text?.Trim() ?? string.Empty;
            if (text.Length == 0)
                return;
            input.Text = string.Empty;
            flyout.Hide();
            CommandRequested?.Invoke(text);
        }
        input.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                e.Handled = true;
                Send();
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var send = Ui.Button("SEND", Send, primary: true);
        send.VerticalAlignment = VerticalAlignment.Top;
        flyout.Content = Ui.Row(8, input, send);
        flyout.Opened += (_, _) => input.Focus();
        return flyout;
    }
}
