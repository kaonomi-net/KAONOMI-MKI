using Avalonia;
using System;

namespace KAONOMI.Chat;

/// <summary>Application entry point for the Avalonia desktop host.</summary>
sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    /// <summary>Starts the chat application with a classic desktop lifetime.</summary>
    [STAThread]
    public static void Main(string[] args)
    {
        // Every crash leaves a trace in ~/kaonomi/crash.log (and in the activity log when the server answers).
        Services.CrashLog.Install();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    /// <summary>Configures the Avalonia app builder; also used by the visual designer.</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // macOS: OpenGL first (Metal is the default), so that the 3D scene of the Kaonomi band (OpenGlControlBase)
            // gets an OpenGL context; Metal, then software, when OpenGL is not available.
            .With(new AvaloniaNativePlatformOptions
            {
                RenderingMode = [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.Software]
            })
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
