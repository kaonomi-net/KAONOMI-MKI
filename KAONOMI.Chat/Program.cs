using System;
using Avalonia;

namespace KAONOMI.UI;

/// <summary>
/// KAONOMI.Chat: the Kaonomi chat in Avalonia, aligned on KAONOMI.ChatGodot (the same view models, services, commands
/// and tool windows, from its Shared/ folder), with the full chat on a vertical screen when one is connected.
/// Command line: <c>--prompt="…"</c> sends a first prompt, <c>--command=/alarm;/iq</c> runs commands,
/// <c>--no-vertical</c> leaves the full chat of the vertical screen closed.
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
