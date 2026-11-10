using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Chat;
using KAONOMI.UI.Desk;
using KAONOMI.UI.Themes;
using KAONOMI.UI.Tools.Alarms;

namespace KAONOMI.UI;

/// <summary>
/// The window of the chat, laid out as the Godot chat: the bar of commands on the left, the desktop of the tool windows
/// in the middle, the chat on the right. The same view model (<see cref="MainViewModel"/>) runs everything: a command
/// typed in the chat (/alarm, /iq…) or clicked in the bar opens its tool in a window of the desktop.
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly string LayoutFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "kaonomi", "ui-windows.txt");

    private readonly string[] _args;
    private readonly MainViewModel _vm;
    private readonly DeskArea _deck = new();
    private readonly Neocom _neocom = new();
    private readonly ChatPanel _chat;
    private readonly Desk.Shell _shell;
    private readonly Panel _overlay = new() { IsHitTestVisible = false };
    private readonly TextBlock _countdown = new() { FontSize = 90, Foreground = Avalonia.Media.Brushes.White, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, IsVisible = false };
    private WindowState _beforeFullScreen = WindowState.Normal;
    private bool _quitting;

    public MainWindow(string[] args)
    {
        _args = args;
        Title = "KAONOMI";
        Width = 1600;
        Height = 960;
        MinWidth = 1100;
        MinHeight = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Confirmations.Ask = (message, ok) => Dialogs.Confirm("Confirm", message, ok);
        IqWindowViewModel.BankPath = Path.Combine(AppContext.BaseDirectory, "Data", "iq-questions.json");
        PictureLibrary.WebOpener = OpenUrl;

        // The theme chosen last (--theme=Matrix on the command line for a test, not kept), as in the Godot chat.
        DeskTheme.Apply(Argument("--theme=") is { } theme ? DeskTheme.Find(theme) : DeskTheme.Saved(), save: false);
        this.Bind(FontFamilyProperty, this.GetResourceObservable("BodyFont", v => v as Avalonia.Media.FontFamily ?? Avalonia.Media.FontFamily.Default));

        var settings = ChatSettings.Load();
        _vm = new MainViewModel(settings);
        if (settings.KeepAwake)
            SleepGuard.Start();

        _chat = new ChatPanel(_vm);
        _shell = new Desk.Shell(_vm, _deck);
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        Grid.SetColumn(_deck, 1);
        Grid.SetColumn(_chat, 2);
        row.Children.Add(_neocom);
        row.Children.Add(_deck);
        row.Children.Add(_chat);
        _overlay.Children.Add(_countdown);
        Content = new Panel { Children = { _backgroundHost, row, _voiceLayer, _overlay } };
        ShowBackground(DeskTheme.Current);
        // A theme applied (🎨, /theme:name): its background replaces the one shown; /getthemes knows it.
        DeskTheme.Changed += palette => Ui.OnMain(() =>
        {
            ShowBackground(palette);
            _vm.Status = $"Theme: {palette.Name}";
        });
        KAONOMI.Chat.Themes.AppTheme.Applied += kind => Ui.OnMain(() =>
        {
            if (DeskTheme.Find(kind.ToString()) is var palette && palette != DeskTheme.Current)
                DeskTheme.Apply(palette);
        });
        // A double-click on the 3D animation: the voice mode.
        _chat.VoiceModeRequested += EnterVoiceMode;

        _neocom.CommandRequested += command =>
        {
            if (command == Neocom.Themes.Command)
                OpenThemes();
            else
                _vm.RunCommand(command);
        };
        _neocom.FullScreenRequested += ToggleFullScreen;
        _deck.Changed += RefreshNeocom;
        _deck.CloneRequested += window =>
        {
            var clone = _shell.Clone(window, tool => _vm.OpenTool(tool, false));
            _vm.Status = clone is null ? $"{window.Title} cannot be cloned" : $"{window.Title} cloned";
        };
        WireTools();

        // The alarms of /alarm and the reminders of /calendar ring even when their windows are closed.
        _ = new AlarmRinger(_overlay, _vm.Say, () => _vm.VoiceLanguage,
            // As in the Godot chat: full screen in the voice mode (the 3D animation in view behind the alarm).
            enterFullScreen: () =>
            {
                _beforeFullScreen = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState;
                WindowState = WindowState.FullScreen;
                _voiceFromAlarm = _voiceMode is null;
                EnterVoiceMode();
            },
            leaveFullScreen: () =>
            {
                if (_voiceFromAlarm)
                    _voiceMode?.Leave();
                _voiceFromAlarm = false;
                WindowState = _beforeFullScreen;
            });

        // The server every 5 s, the ticker every minute, the windows saved every 20 s.
        var server = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        server.Tick += (_, _) => _ = _vm.CheckServerAsync();
        server.Start();
        var ticker = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        ticker.Tick += (_, _) => _vm.RefreshTickerCommand.Execute(null);
        ticker.Start();
        var save = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        save.Tick += (_, _) => SaveLayout();
        save.Start();

        Opened += (_, _) => Started();
        Closing += OnClosing;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _deck.Cycle();
                e.Handled = true;
            }
        };
    }

    // ── The themes ──

    private readonly Panel _backgroundHost = new() { IsHitTestVisible = false };

    /// <summary>The animated background of <paramref name="palette"/> behind everything (brighter while the answer is read aloud).</summary>
    private void ShowBackground(Palette palette)
    {
        if (_backgroundHost.Children.FirstOrDefault() is ThemeBackground { } shown && shown.Palette == palette)
            return;
        _backgroundHost.Children.Clear();
        _backgroundHost.Children.Add(new ThemeBackground(palette) { Energy = () => _vm.IsSpeakingOrPreparing ? 1f : 0f });
        KAONOMI.Chat.Themes.AppTheme.SetCurrent(KAONOMI.Chat.Themes.AppTheme.TryParse(palette.Name, out var kind) ? kind : KAONOMI.Chat.Themes.AppThemeKind.Eve);
    }

    /// <summary>The screen of the themes, in a window of the desktop.</summary>
    private void OpenThemes()
    {
        if (!_shell.TryBringBack(ThemeScreen.Key))
            _shell.OpenControl(ThemeScreen.Key, "Themes", ThemeScreen.Icon, ThemeScreen.Build(), new Size(720, 520));
    }

    // ── The voice mode ──

    private readonly Panel _voiceLayer = new();
    private VoiceMode? _voiceMode;
    private bool _voiceFromAlarm;

    /// <summary>The voice mode over the window: the 3D animation alone, the microphone (once at a time).</summary>
    private void EnterVoiceMode()
    {
        if (_voiceMode is not null)
            return;
        _voiceMode = new VoiceMode(_chat, _vm, () =>
        {
            _voiceLayer.Children.Clear();
            _voiceMode = null;
            _chat.FocusInput();
        });
        _voiceLayer.Children.Add(_voiceMode);
    }

    private void Started()
    {
        // --themes: the screen of the themes.
        if (_args.Contains("--themes"))
            DispatcherTimer.RunOnce(OpenThemes, TimeSpan.FromMilliseconds(600));
        // --avatar=HAL: the preset of that 3D animation.
        if (Argument("--avatar=") is { } avatar && Enum.TryParse<SceneAnimation>(avatar, true, out var animation)
            && _vm.Presets.FirstOrDefault(p => p.Animation == animation) is { } preset)
            _vm.SelectedPreset = preset;
        // --voice: starts in the voice mode.
        if (_args.Contains("--voice"))
            DispatcherTimer.RunOnce(EnterVoiceMode, TimeSpan.FromMilliseconds(500));
        _vm.RefreshModelsCommand.Execute(null);
        _ = _vm.CheckServerAsync();
        _vm.RefreshTickerCommand.Execute(null);
        ActivityLog.Info("App", "Avalonia chat (KAONOMI.UI) started");
        // The windows open at the last close come back once the desktop has its size.
        DispatcherTimer.RunOnce(RestoreLayout, TimeSpan.FromMilliseconds(300));
        if (Argument("--command=") is { } commands)
            foreach (var command in commands.Trim('"').Split(';', StringSplitOptions.RemoveEmptyEntries))
                _vm.RunCommand(command.Trim());
        if (Argument("--prompt=") is { } prompt)
            _ = RunWhenReadyAsync(prompt.Trim('"'));
        _chat.FocusInput();
    }

    /// <summary>A prompt given on the command line, sent once the server and the models answer (20 s at most).</summary>
    private async Task RunWhenReadyAsync(string text)
    {
        for (var i = 0; i < 40; i++)
        {
            _vm.Input = text;
            if (_vm.SendCommand.CanExecute(null))
            {
                _vm.RunCommand(text);
                return;
            }
            await Task.Delay(500);
        }
    }

    private string? Argument(string name) => _args.FirstOrDefault(a => a.StartsWith(name, StringComparison.Ordinal))?[name.Length..];

    /// <summary>Every request of the chat to open a tool opens its window on the desktop.</summary>
    private void WireTools()
    {
        // A window asked for (a command typed in the voice mode, say) is shown: the voice mode is left, but not the
        // full screen of an alarm ringing.
        void Show(Action open)
        {
            if (!_voiceFromAlarm)
                _voiceMode?.Leave();
            open();
        }
        void Open(object viewModel, string key) => Ui.OnMain(() => Show(() => _shell.Open(viewModel, _shell.KeyFor(key))));
        _vm.CodeWindowRequested += vm => Ui.OnMain(() => Show(() => _shell.Open(vm)));
        _vm.CacheWindowRequested += (vm, _) => Open(vm, "/cache");
        _vm.SearchWindowRequested += (vm, _) => Open(vm, "/search");
        _vm.JsonClientWindowRequested += (vm, _) => Open(vm, "/json");
        _vm.SymbolicPatternWindowRequested += (vm, _) => Open(vm, "/symbol");
        _vm.OntologyWindowRequested += (vm, _) => Open(vm, "/ontology");
        _vm.JournalWindowRequested += (vm, _) => Open(vm, "/journal");
        _vm.CompletionWindowRequested += (vm, _) => Open(vm, "/completion");
        _vm.FooocusWindowRequested += (vm, _) => Open(vm, "/fooocus");
        _vm.YoutubeWindowRequested += (vm, _) => Open(vm, "/youtube");
        _vm.MusicWindowRequested += (vm, _) => Open(vm, "/music");
        _vm.PictureWindowRequested += (vm, _) => Open(vm, "/pictures");
        _vm.CalculatorWindowRequested += (vm, _) => Open(vm, "/work");
        _vm.IdeaWindowRequested += (vm, _) => Open(vm, "/idea");
        _vm.TokenDashboardWindowRequested += (vm, _) => Open(vm, "/token");
        _vm.ConfigWindowRequested += (vm, _) => Open(vm, "/config");
        _vm.IntelWindowRequested += (vm, _) => Open(vm, "/intel");
        _vm.CollectionPlanWindowRequested += (vm, _) => Open(vm, "/collectionplan");
        _vm.Game1WindowRequested += (vm, _) => Open(vm, "/game:solitaire");
        _vm.FractalWindowRequested += (vm, _) => Open(vm, "/fractal");
        _vm.ChessWindowRequested += (vm, _) => Open(vm, "/game:chess");
        _vm.GoWindowRequested += (vm, _) => Open(vm, "/game:go");
        _vm.ImmuneWindowRequested += (vm, _) => Open(vm, "/immune");
        _vm.AlarmWindowRequested += (vm, _) => Open(vm, "/alarm");
        _vm.CalendarWindowRequested += (vm, _) => Open(vm, "/calendar");
        _vm.IqWindowRequested += (vm, _) => Open(vm, "/iq");
        _vm.ActivityWindowRequested += (vm, _) => Open(vm, "/activity");
        _vm.FoldersWindowRequested += (vm, _) => Open(vm, "/folders");
        _vm.ProjectWindowRequested += (vm, _) => Open(vm, "/project");
        _vm.RemoteWindowRequested += (vm, _) => Open(vm, "/remote");
        _vm.PresetWindowRequested += (vm, _) => Open(vm, "/preset");
        _vm.ProcessWindowRequested += (vm, _) => Open(vm, "/process");
        _vm.GatewayWindowRequested += (vm, _) => Open(vm, "/gateway");
        _vm.WikiWindowRequested += (vm, _) => Open(vm, "/wiki");
        _vm.InterviewWindowRequested += (vm, _) => Open(vm, "/interview");
        _vm.BpmnWindowRequested += (vm, _) => Open(vm, "/bpmnai");
        _vm.InfraWindowRequested += (vm, _) => Open(vm, "/infra");
        _vm.MindmapWindowRequested += (vm, _) => Open(vm, "/mindmap");
        _vm.GhostWriterWindowRequested += (vm, _) => Open(vm, "/write");
        _vm.ConsoleWindowRequested += (vm, _) => Open(vm, "/cmd");
        _vm.LogDashboardWindowRequested += (vm, _) => Open(vm, "/log");
        _vm.SystemWindowRequested += (vm, _) => Open(vm, "/system");
        _vm.RelationGraphRequested += (vm, _) => Open(vm, "/relation");
        // What needs the 3D or the screens of the Godot chat: said in the status.
        // /fullchat: the chat as in its full screen, the 3D animation and the microphone (the voice mode).
        _vm.FullChatRequested += _ => Ui.OnMain(EnterVoiceMode);
        // FULLSCREEN:ON / OFF of a script of /remote.
        _vm.FullScreenRequested += on => Ui.OnMain(() =>
        {
            if (on)
                EnterVoiceMode();
            else
                _voiceMode?.Leave();
        });
        _vm.ThinkRequested += () => Ui.OnMain(() => _vm.Status = "/think (the animated screen) is not part of KAONOMI.UI");
        _vm.SetupRequested += () => Ui.OnMain(() => _vm.Status = "/setup: run it in KAONOMI.ChatGodot (it installs the dependencies of both chats)");
        _vm.AudioSettingsRequested += () => Ui.OnMain(() => _vm.Status = "/audiosettings: set the devices in KAONOMI.ChatGodot (the choice is shared)");
        _vm.CloseAllRequested += () => Ui.OnMain(() => _ = CloseAllAsync());
        _vm.ExitCountdown += remaining => Ui.OnMain(() =>
        {
            _countdown.IsVisible = remaining is not null;
            _countdown.Text = remaining is { } s ? $"BYE · {s}" : string.Empty;
        });
        _vm.ExitRequested += () => Ui.OnMain(Close);
    }

    private async Task CloseAllAsync()
    {
        foreach (var window in _deck.Windows.ToList())
            if (!await _deck.CloseAsync(window))
                return;
    }

    private void RefreshNeocom()
    {
        var open = _deck.Windows.Select(w => Desk.Shell.BaseKey(w.Key)).ToHashSet();
        foreach (var (command, _, _) in Neocom.Tools.Append(Neocom.Settings))
            _neocom.SetOpen(command, open.Contains(command));
    }

    private void ToggleFullScreen()
    {
        if (WindowState == WindowState.FullScreen)
            WindowState = _beforeFullScreen;
        else
        {
            _beforeFullScreen = WindowState;
            WindowState = WindowState.FullScreen;
        }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ActivityLog.Error("UI", $"The link {url} could not be opened: {ex.Message}");
        }
    }

    // ── The windows of the desktop, kept from one start to the next ──

    private void SaveLayout()
    {
        if (_quitting && _deck.Windows.Count == 0)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutFile)!);
            var lines = _deck.Windows.Where(w => w.Key.StartsWith('/'))
                .Select(w => FormattableString.Invariant($"{w.Key}|{w.Rect.X:0}|{w.Rect.Y:0}|{w.Rect.Width:0}|{w.Rect.Height:0}|{(w.IsMinimized ? "min" : w.IsMaximized ? "max" : "")}"));
            File.WriteAllLines(LayoutFile, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void RestoreLayout()
    {
        if (!File.Exists(LayoutFile))
            return;
        foreach (var line in File.ReadAllLines(LayoutFile))
        {
            var parts = line.Split('|');
            if (parts.Length < 5 || !parts[0].StartsWith('/'))
                continue;
            var window = _shell.OpenToolAs(parts[0], tool => _vm.OpenTool(tool, false));
            if (window is null)
                continue;
            var n = parts.Skip(1).Take(4).Select(p => double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();
            if (n[2] > 100 && n[3] > 80)
                _deck.Place(window, new Rect(n[0], n[1], n[2], n[3]));
            var state = parts.Length > 5 ? parts[5] : string.Empty;
            if (state == "max")
                _deck.ToggleMaximize(window);
            else if (state == "min")
                _deck.Minimize(window);
        }
    }

    private bool _askedToQuit;

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_quitting)
            return;
        e.Cancel = true;
        if (_askedToQuit)
            return;
        if (_deck.UnsavedWindows() is { Count: > 0 } unsaved)
        {
            _askedToQuit = true;
            var quit = await Dialogs.Confirm("Quit KAONOMI",
                $"{unsaved.Count} window(s) have changes that are not saved:\n{string.Join("\n", unsaved.Select(w => "· " + w.Title))}\n\nQuitting loses them.",
                "Quit without saving", "Cancel");
            _askedToQuit = false;
            if (!quit)
                return;
        }
        SaveLayout();
        _quitting = true;
        _vm.StopSpeaking();
        _vm.SaveSystemPrompt();
        _deck.CloseAll();
        _vm.DisposeSpeech();
        SleepGuard.Stop();
        ActivityLog.Info("App", "Avalonia chat (KAONOMI.UI) closed");
        ActivityLog.Flush(TimeSpan.FromSeconds(2));
        Close();
    }
}
