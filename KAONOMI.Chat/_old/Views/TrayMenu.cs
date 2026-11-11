using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using KAONOMI.Chat.Themes;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>
/// Notification area (systray) icon of the chat: a click shows or hides the chat, and its menu gives access to every
/// <c>/</c> command, run in the chat as if typed (the chat is shown first).
/// </summary>
internal static class TrayMenu
{
    /// <summary>Commands of the menu, with their description; themes are in a sub-menu.</summary>
    private static readonly (string Command, string Description)[] Commands =
    [
        ("/fullchat", "Full chat"),
        ("/system", "System activity"),
        ("/log", "Logs dashboard"),
        ("/token", "Tokens dashboard"),
        ("/cache", "Cache search / edit"),
        ("/json", "HTTP JSON client"),
        ("/symbol", "Symbolic patterns"),
        ("/ontology", "Ontology management"),
        ("/idea", "Ideas"),
        ("/calculator", "Calculator"),
        ("/pictures", "Picture library"),
        ("/music", "Music player"),
        ("/journal", "Journal"),
        ("/youtube", "YouTube videos"),
        ("/completion", "Completions and word cloud"),
        ("/config", "Edit config.yml"),
        ("/getthemes", "List the themes"),
        ("/help", "List the commands"),
    ];

    /// <summary>Creates the systray icon for <paramref name="window"/> and its view model.</summary>
    public static void Install(Application application, MainWindow window, MainViewModel viewModel)
    {
        var menu = new NativeMenu();

        var toggle = new NativeMenuItem("Show / hide the chat");
        toggle.Click += (_, _) => window.ToggleVisibility();
        menu.Add(toggle);
        menu.Add(new NativeMenuItemSeparator());

        foreach (var (command, description) in Commands)
            menu.Add(CommandItem($"{command}    {description}", command, window, viewModel));

        var themes = new NativeMenu();
        foreach (var (kind, description) in AppTheme.All)
        {
            var name = AppTheme.ToName(kind);
            themes.Add(CommandItem($"/theme:{name}    {description}", $"/theme:{name}", window, viewModel));
        }
        menu.Add(new NativeMenuItem("/theme:<name>    Themes") { Menu = themes });

        menu.Add(new NativeMenuItemSeparator());
        menu.Add(CommandItem("/quit    Close the application (10 s, Cancel to stay)", "/quit", window, viewModel));

        var icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://KAONOMI.Chat/Assets/kaonomi-logo.ico"))),
            ToolTipText = "Kaonomi Chat",
            Menu = menu,
            IsVisible = true,
        };
        icon.Clicked += (_, _) => window.ToggleVisibility();
        TrayIcon.SetIcons(application, [icon]);
    }

    private static NativeMenuItem CommandItem(string header, string command, MainWindow window, MainViewModel viewModel)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) =>
        {
            window.ShowChat();
            viewModel.RunCommand(command);
        };
        return item;
    }
}
