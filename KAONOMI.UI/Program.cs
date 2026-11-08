using System;
using Avalonia;

namespace KAONOMI.UI;

/// <summary>
/// KAONOMI.UI: the Kaonomi chat in Avalonia, a flat version of KAONOMI.ChatGodot (the same view models, services and
/// commands; no 3D, no effects). Command line: <c>--prompt="…"</c> sends a first prompt, <c>--command=/alarm;/iq</c>
/// runs commands.
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
