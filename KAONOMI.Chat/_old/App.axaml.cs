using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.Themes;
using KAONOMI.Chat.ViewModels;
using KAONOMI.Chat.Views;

namespace KAONOMI.Chat;

/// <summary>Avalonia application that loads XAML, restores the last theme and hosts the main chat window.</summary>
public partial class App : Application
{
    /// <inheritdoc />
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // An exception on the UI thread is traced in ~/kaonomi/crash.log and the application goes on.
            CrashLog.InstallUi();
            // A UI thread blocked (every window frozen) is traced in ~/kaonomi/freeze.log.
            UiWatchdog.Install();

            // The paths of config.yml and of the calculator tape follow the standards of this system (Windows, macOS,
            // Linux) even when they were written on another one.
            var settings = ChatSettings.Load();
            var tapeLines = CalculatorTapeStore.AdaptPaths();

            // Restore the last theme before the first window is created; unknown names keep the default.
            if (AppTheme.TryParse(settings.Theme, out var theme))
                AppTheme.Apply(theme);

            var viewModel = new MainViewModel(settings);
            var mainWindow = new MainWindow { DataContext = viewModel };
            desktop.MainWindow = mainWindow;
            // Systray icon: show / hide the chat, and every "/" command in its menu.
            TrayMenu.Install(this, mainWindow, viewModel);
            ActivityLog.Info("App", $"Application started (theme {AppTheme.ToName(AppTheme.Current)}, server {settings.ServerUrl}, Ollama {settings.OllamaUrl})");
            if (tapeLines > 0)
                ActivityLog.Info("Settings", $"Paths of the calculator tape adapted to this system ({tapeLines} line(s))");

            desktop.Exit += (_, _) =>
            {
                ChatSettings.SaveTheme(AppTheme.ToName(AppTheme.Current));
                ActivityLog.Info("App", $"Application closed (theme {AppTheme.ToName(AppTheme.Current)} saved)");
                // Give the last entries a chance to reach the server, without holding the exit for long.
                ActivityLog.Flush(TimeSpan.FromSeconds(3));
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}