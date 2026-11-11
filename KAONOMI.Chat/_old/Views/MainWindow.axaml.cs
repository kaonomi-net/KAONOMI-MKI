using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>Primary chat window: input handling, child tool windows, and auto-scroll.</summary>
public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    /// <summary>Ticker scroll speed in device-independent pixels per second.</summary>
    private const double TickerSpeed = 60;

    private readonly DispatcherTimer _tickerAnimation = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _tickerRefresh = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly Stopwatch _tickerClock = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    /// <summary>Checks that the Kaonomi REST server answers (Send is disabled while it does not).</summary>
    private readonly DispatcherTimer _serverCheck = new() { Interval = TimeSpan.FromSeconds(5) };
    private double _tickerOffset;
    private bool _tickerPaused;

    /// <summary>
    /// True for the full chat: a clone of the chat on a vertical screen, sharing its conversation, with the 3D band at
    /// the top (60 % of the height) and the chat below. The docked chat stays the main window (tools, systray, /bye)
    /// but is hidden while the full chat is open, and comes back when it closes.
    /// </summary>
    private readonly bool _isFullChat;

    /// <summary>3D animation shown in the band (animation in config.yml), driven by the voice and the streamed words.</summary>
    private IBandScene? _scene;

    /// <summary>Share of the full chat's height taken by the 3D band.</summary>
    private const double FullChatBandShare = 0.6;

    /// <summary>Full chat opened with the docked chat, on a vertical screen (null when there is none).</summary>
    private MainWindow? _fullChat;

    /// <summary>Initializes the docked chat.</summary>
    public MainWindow() : this(isFullChat: false)
    {
    }

    /// <summary>Initializes the view and wires Enter-to-send on the input box.</summary>
    private MainWindow(bool isFullChat)
    {
        _isFullChat = isFullChat;
        InitializeComponent();

        if (isFullChat)
        {
            // 3D band first, then the header and the ticker; the conversation, input and status bar keep their rows.
            Grid.SetRow(Band, 0);
            Grid.SetRow(HeaderBar, 1);
            Grid.SetRow(TickerBand, 2);
            Classes.Add("fullChat");
            Title = "Kaonomi Full Chat";
            Topmost = false;
            ShowInTaskbar = true;
            SizeChanged += (_, _) => Band.Height = Math.Round(ClientSize.Height * FullChatBandShare);
        }

        // Tunnel so that Enter is intercepted before the multi-line TextBox inserts a new line.
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        // Enter anywhere in the window while an answer is being revealed: show the rest of it at once.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        _clock.Tick += (_, _) => UpdateClock();
        _serverCheck.Tick += async (_, _) =>
        {
            if (_viewModel is { } viewModel)
                await viewModel.CheckServerAsync();
        };
        UpdateClock();
        _clock.Start();

        _tickerAnimation.Tick += OnTickerAnimationTick;
        _tickerRefresh.Tick += (_, _) => _viewModel?.RefreshTickerCommand.Execute(null);
        _layoutSave.Tick += (_, _) =>
        {
            _layoutSave.Stop();
            if (!_isFullChat && !_exiting)
                ScreenLayoutStore.Save(CaptureLayout());
        };

        // Pause on hover so a message can be read, like a stock ticker.
        TickerCanvas.PointerEntered += (_, _) => _tickerPaused = true;
        TickerCanvas.PointerExited += (_, _) => _tickerPaused = false;
        TickerCanvas.PointerReleased += OnTickerPointerReleased;
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.SpeakingChanged -= OnSpeakingChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.WordStreamed -= OnWordStreamed;
            _viewModel.Messages.CollectionChanged -= OnMessagesChanged;
            foreach (var message in _viewModel.Messages)
                message.PropertyChanged -= OnMessagePropertyChanged;
        }

        if (_viewModel is not null && !_isFullChat)
        {
            _viewModel.CodeWindowRequested -= OnCodeWindowRequested;
            _viewModel.CacheWindowRequested -= OnCacheWindowRequested;
            _viewModel.JsonClientWindowRequested -= OnJsonClientWindowRequested;
            _viewModel.SymbolicPatternWindowRequested -= OnSymbolicPatternWindowRequested;
            _viewModel.OntologyWindowRequested -= OnOntologyWindowRequested;
            _viewModel.IdeaWindowRequested -= OnIdeaWindowRequested;
            _viewModel.CalculatorWindowRequested -= OnCalculatorWindowRequested;
            _viewModel.PictureWindowRequested -= OnPictureWindowRequested;
            _viewModel.MusicWindowRequested -= OnMusicWindowRequested;
            _viewModel.YoutubeWindowRequested -= OnYoutubeWindowRequested;
            _viewModel.CompletionWindowRequested -= OnCompletionWindowRequested;
            _viewModel.JournalWindowRequested -= OnJournalWindowRequested;
            _viewModel.SystemWindowRequested -= OnSystemWindowRequested;
            _viewModel.ConfigWindowRequested -= OnConfigWindowRequested;
            _viewModel.RemoteWindowRequested -= OnRemoteWindowRequested;
            _viewModel.ProjectWindowRequested -= OnProjectWindowRequested;
            _viewModel.IntelWindowRequested -= OnIntelWindowRequested;
            _viewModel.LogDashboardWindowRequested -= OnLogDashboardWindowRequested;
            _viewModel.TokenDashboardWindowRequested -= OnTokenDashboardWindowRequested;
            _viewModel.FullChatRequested -= OnFullChatRequested;
            _viewModel.ExitRequested -= OnExitRequested;
        }

        _viewModel = DataContext as MainViewModel;

        if (_viewModel is not null)
        {
            _viewModel.SpeakingChanged += OnSpeakingChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            ShowAnimation(_viewModel);
            _viewModel.WordStreamed += OnWordStreamed;
            _viewModel.Messages.CollectionChanged += OnMessagesChanged;
            foreach (var message in _viewModel.Messages)
                message.PropertyChanged += OnMessagePropertyChanged;
        }

        // Tools, code views and /bye are handled by the docked chat only (the full chat shares its view model).
        if (_viewModel is not null && !_isFullChat)
        {
            _viewModel.CodeWindowRequested += OnCodeWindowRequested;
            _viewModel.CacheWindowRequested += OnCacheWindowRequested;
            _viewModel.JsonClientWindowRequested += OnJsonClientWindowRequested;
            _viewModel.SymbolicPatternWindowRequested += OnSymbolicPatternWindowRequested;
            _viewModel.OntologyWindowRequested += OnOntologyWindowRequested;
            _viewModel.IdeaWindowRequested += OnIdeaWindowRequested;
            _viewModel.CalculatorWindowRequested += OnCalculatorWindowRequested;
            _viewModel.PictureWindowRequested += OnPictureWindowRequested;
            _viewModel.MusicWindowRequested += OnMusicWindowRequested;
            _viewModel.YoutubeWindowRequested += OnYoutubeWindowRequested;
            _viewModel.CompletionWindowRequested += OnCompletionWindowRequested;
            _viewModel.JournalWindowRequested += OnJournalWindowRequested;
            _viewModel.SystemWindowRequested += OnSystemWindowRequested;
            _viewModel.ConfigWindowRequested += OnConfigWindowRequested;
            _viewModel.RemoteWindowRequested += OnRemoteWindowRequested;
            _viewModel.ProjectWindowRequested += OnProjectWindowRequested;
            _viewModel.IntelWindowRequested += OnIntelWindowRequested;
            _viewModel.LogDashboardWindowRequested += OnLogDashboardWindowRequested;
            _viewModel.TokenDashboardWindowRequested += OnTokenDashboardWindowRequested;
            _viewModel.FullChatRequested += OnFullChatRequested;
            _viewModel.ExitRequested += OnExitRequested;
        }
    }

    /// <summary>
    /// Shows the 3D animation of <paramref name="viewModel"/> (animation in config.yml, or the last preset applied):
    /// the 3D robot face (MARI), the photo portrait (JEROME), the red eye (HAL), the robot doll with a knife (MARIEKILLEUSE), the robot boy (MUSUKO), the masked robot (KAONAMI), the robot with a tablet (PEPPER), the holographic display (JARVIS) or
    /// nothing (NONE: the band keeps its background and the clock).
    /// </summary>
    private void ShowAnimation(MainViewModel viewModel)
    {
        if (_scene is not null)
            _scene.IsSpeaking = false;
        Scene.IsVisible = viewModel.Animation == SceneAnimation.Mari;
        JeromeScene.IsVisible = viewModel.Animation == SceneAnimation.Jerome;
        HalScene.IsVisible = viewModel.Animation == SceneAnimation.Hal;
        MarieKilleuseScene.IsVisible = viewModel.Animation == SceneAnimation.MarieKilleuse;
        MusukoScene.IsVisible = viewModel.Animation == SceneAnimation.Musuko;
        KaonamiScene.IsVisible = viewModel.Animation == SceneAnimation.Kaonami;
        PepperScene.IsVisible = viewModel.Animation == SceneAnimation.Pepper;
        GreenScene.IsVisible = viewModel.Animation == SceneAnimation.Green;
        JeromeLiveScene.IsVisible = viewModel.Animation == SceneAnimation.JeromeLive;
        MarvinScene.IsVisible = viewModel.Animation == SceneAnimation.Marvin;
        Jarvis.IsVisible = viewModel.Animation == SceneAnimation.Jarvis;
        _scene = viewModel.Animation switch
        {
            SceneAnimation.Jerome => JeromeScene,
            SceneAnimation.Hal => HalScene,
            SceneAnimation.MarieKilleuse => MarieKilleuseScene,
            SceneAnimation.Musuko => MusukoScene,
            SceneAnimation.Kaonami => KaonamiScene,
            SceneAnimation.Pepper => PepperScene,
            SceneAnimation.Green => GreenScene,
            SceneAnimation.JeromeLive => JeromeLiveScene,
            SceneAnimation.Marvin => MarvinScene,
            SceneAnimation.Jarvis => Jarvis,
            _ => Scene
        };
        _scene.VoiceLevel = () => viewModel.SpeechLevel;
    }

    /// <summary>A preset applied from the <c>/config</c> window switches the animation at once.</summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Animation) && sender is MainViewModel viewModel)
            ShowAnimation(viewModel);
    }

    /// <summary>Each streamed word makes the 3D scene of the Kaonomi band ripple.</summary>
    private void OnWordStreamed(string word) => _scene?.Pulse(word);

    /// <summary>While the answer is read aloud, the mouth of the 3D face follows the voice instead of the words.</summary>
    private void OnSpeakingChanged(bool speaking)
    {
        if (_scene is not null)
            _scene.IsSpeaking = speaking;
    }

    /// <summary>Opens a code viewer tab.</summary>
    private void OnCodeWindowRequested(CodeWindowViewModel codeViewModel)
    {
        // Every answer gets its own code tab.
        OpenTab($"code-{Guid.NewGuid()}", codeViewModel.Title, codeViewModel, () => new CodeView());
    }

    /// <summary>Opens the cache search/editor tab.</summary>
    private void OnCacheWindowRequested(CacheWindowViewModel cacheViewModel, bool detached)
    {
        OpenTool("cache", "Cache search", cacheViewModel, () => new CacheView(), detached, new Size(1110, 790));
    }

    /// <summary>Opens the JSON HTTP client tab.</summary>
    private void OnJsonClientWindowRequested(JsonClientWindowViewModel jsonViewModel, bool detached)
    {
        OpenTool("jsonClient", "JSON client", jsonViewModel, () => new JsonClientView(), detached, new Size(1110, 790));
    }

    /// <summary>Opens the symbolic pattern search/editor tab.</summary>
    private void OnSymbolicPatternWindowRequested(SymbolicPatternWindowViewModel symbolViewModel, bool detached)
    {
        OpenTool("symbolicPattern", "Symbolic patterns", symbolViewModel, () => new SymbolicPatternView(), detached, new Size(1110, 1250));
    }

    /// <summary>Opens the ontology management tab.</summary>
    private void OnOntologyWindowRequested(OntologyWindowViewModel ontologyViewModel, bool detached)
    {
        OpenTool("ontology", "Ontology", ontologyViewModel, () => new OntologyView(), detached, new Size(1920, 1080));
    }

    /// <summary>Opens the journal (a tab, or its own window with <c>/ndiary</c>).</summary>
    private void OnJournalWindowRequested(JournalWindowViewModel journalViewModel, bool detached)
    {
        OpenTool("journal", "Journal", journalViewModel, () => new JournalView(), detached, new Size(1400, 900));
    }

    /// <summary>Opens the completions and their word cloud (a tab, or its own window with <c>/ncompletion</c>).</summary>
    private void OnCompletionWindowRequested(CompletionWindowViewModel completionViewModel, bool detached)
    {
        OpenTool("completion", "Completions", completionViewModel, () => new CompletionView(), detached, new Size(1400, 900));
    }

    /// <summary>Opens the YouTube videos (a tab, or its own window with <c>/nyoutube</c>).</summary>
    private void OnYoutubeWindowRequested(YoutubeWindowViewModel youtubeViewModel, bool detached)
    {
        OpenTool("youtube", "YouTube", youtubeViewModel, () => new YoutubeView(), detached, new Size(1300, 860));
    }

    /// <summary>Opens the music player (a tab, or its own window with <c>/nmusic</c>).</summary>
    private void OnMusicWindowRequested(MusicPlayerViewModel musicViewModel, bool detached)
    {
        OpenTool("music", "Music", musicViewModel, () => new MusicPlayerView(), detached, new Size(1300, 850));
    }

    /// <summary>Opens the picture library (a tab, or its own window with <c>/npictures</c>).</summary>
    private void OnPictureWindowRequested(PictureLibraryViewModel pictureViewModel, bool detached)
    {
        OpenTool("pictures", "Pictures", pictureViewModel, () => new PictureLibraryView(), detached, new Size(1500, 950));
    }

    /// <summary>Opens the calculator (a tab, or its own window with <c>/ncalculator</c>).</summary>
    private void OnCalculatorWindowRequested(CalculatorViewModel calculatorViewModel, bool detached)
    {
        OpenTool("calculator", "Scientific calculator", calculatorViewModel, () => new CalculatorView(), detached, new Size(1180, 720));
    }

    /// <summary>Opens the idea list / editor (a tab, or its own window with <c>/nidea</c>).</summary>
    private void OnIdeaWindowRequested(IdeaWindowViewModel ideaViewModel, bool detached)
    {
        OpenTool("idea", "Ideas", ideaViewModel, () => new IdeaView(), detached, new Size(1200, 900));
    }

    /// <summary>Opens the tokens dashboard.</summary>
    private void OnTokenDashboardWindowRequested(TokenDashboardViewModel tokenViewModel, bool detached)
    {
        OpenTool("tokenDashboard", "Tokens dashboard", tokenViewModel, () => new TokenDashboardView(), detached, new Size(1400, 900));
    }

    /// <summary>Opens the logs dashboard.</summary>
    private void OnLogDashboardWindowRequested(LogDashboardViewModel logViewModel, bool detached)
    {
        OpenTool("logDashboard", "Logs dashboard", logViewModel, () => new LogDashboardView(), detached, new Size(1500, 950));
    }

    /// <summary>Opens the editor of config.yml.</summary>
    private void OnConfigWindowRequested(ConfigWindowViewModel configViewModel, bool detached)
    {
        OpenTool("config", "Configuration", configViewModel, () => new ConfigView(), detached, new Size(900, 800));
    }

    /// <summary>Opens the remote control of the chatbot (voice, effects, system prompt, animation, presets, voices).</summary>
    private void OnRemoteWindowRequested(RemoteWindowViewModel remoteViewModel, bool detached)
    {
        OpenTool("remote", "Remote", remoteViewModel, () => new RemoteView(), detached, new Size(1400, 860));
    }

    /// <summary>Opens the search and editor of the intels.</summary>
    private void OnIntelWindowRequested(IntelWindowViewModel intelViewModel, bool detached)
    {
        OpenTool("intel", "Intel", intelViewModel, () => new IntelView(), detached, new Size(1300, 900));
    }

    /// <summary>Opens the kanban of the tasks.</summary>
    private void OnProjectWindowRequested(ProjectWindowViewModel projectViewModel, bool detached)
    {
        OpenTool("project", "Project", projectViewModel, () => new ProjectView(), detached, new Size(1500, 860));
    }

    /// <summary>Opens the system activity tab.</summary>
    private void OnSystemWindowRequested(SystemWindowViewModel systemViewModel, bool detached)
    {
        OpenTool("system", "System", systemViewModel, () => new SystemView(), detached, new Size(540, 950));
    }

    /// <summary>Window of the tool tabs, created with its first tab (it closes with its last one).</summary>
    private WorkspaceWindow? _workspace;

    /// <summary>Opens (or selects) a tool view in a tab of the workspace window, next to the chat.</summary>
    private void OpenTab(string key, string title, object viewModel, Func<Control> createView)
    {
        if (_workspace is null)
        {
            _workspace = CreateWorkspace(WorkspaceGrid());
            _workspace.Show();
        }
        else if (_workspace.WindowState == WindowState.Minimized)
            _workspace.WindowState = WindowState.Normal;

        _workspace.Open(key, title, viewModel, createView,
            _pendingPanes is not null && _pendingPanes.TryGetValue(key, out var pane) ? pane : null);
        _workspace.Activate();
    }

    /// <summary>
    /// New workspace window of <paramref name="grid"/> panes. It is always docked next to the chat (the free space of its
    /// screen), not at its place of screens.yml.
    /// </summary>
    private WorkspaceWindow CreateWorkspace((int Columns, int Rows) grid)
    {
        var workspace = new WorkspaceWindow(grid.Columns, grid.Rows);
        workspace.LayoutChanged += ScheduleLayoutSave;
        workspace.Closed += (_, _) =>
        {
            // A workspace replaced by another one (screens changed) leaves the new one in place.
            if (ReferenceEquals(_workspace, workspace))
                _workspace = null;
            ScheduleLayoutSave();
        };
        return workspace;
    }

    /// <summary>
    /// After the screens changed: the open workspace gets the panes of its screen (a new window with the same tools and
    /// their state, each in the pane of the same number when there is one) and takes the free space next to the chat.
    /// </summary>
    private void RebuildWorkspace()
    {
        if (_workspace is not { } old)
            return;
        var grid = WorkspaceGrid();
        if (grid == old.PaneGrid)
        {
            RefillWorkspace();
            return;
        }

        var fresh = CreateWorkspace(grid);
        fresh.TakeTabsFrom(old);
        _workspace = fresh;
        old.Close();
        fresh.Show();
        ScheduleLayoutSave();
    }

    /// <summary>
    /// Panes of the workspace (columns × rows): the work zones of its screen (<see cref="WorkZones"/>), less the column
    /// of the full chat when it is docked in one of them.
    /// </summary>
    private (int Columns, int Rows) WorkspaceGrid()
    {
        var (columns, rows) = WorkZones();
        var grid = WindowGuard.ReservedZone is null ? (columns, rows) : (Math.Max(1, columns - 1), rows);
        ActivityLog.Info("Workspace", $"{grid.Item1} × {grid.Item2} pane(s)");
        return grid;
    }

    /// <summary>
    /// Work zones of the free space next to the docked chat (columns × rows), by its screen: on an ultrawide screen
    /// (3440 × 1440), 3 columns when the chat is in full size on a vertical screen (the docked chat is hidden, the
    /// workspace gets the whole width), else 2; on a super ultrawide screen (5120 × 1440), 3 columns; on a 4K screen,
    /// 2 × 2; otherwise a single zone.
    /// </summary>
    private (int Columns, int Rows) WorkZones()
    {
        var screen = Screens.ScreenFromWindow(this) ?? ScreenPlacement.LargestScreen(this);
        return screen is null ? (1, 1)
            : ScreenPlacement.IsSuperUltrawide(screen) ? (3, 1)
            : ScreenPlacement.IsUltrawide(screen) ? (FindVerticalScreen() is not null ? 3 : 2, 1)
            : ScreenPlacement.Is4K(screen) ? (2, 2)
            : (1, 1);
    }

    /// <summary>
    /// Opens a tool view: in a tab of the workspace window, or in its own window of <paramref name="windowSize"/>
    /// (clamped to the free space) when <paramref name="detached"/> (<c>/n</c> commands, a new window each time).
    /// </summary>
    private void OpenTool(string key, string title, object viewModel, Func<Control> createView, bool detached, Size windowSize)
    {
        if (!detached)
        {
            OpenTab(key, title, viewModel, createView);
            return;
        }

        var view = createView();
        view.DataContext = viewModel;
        new ToolWindow(key, view, title, windowSize, _pendingPlacement).Show();
    }

    /// <summary>Command opening each tool that screens.yml can reopen, by tab / window key (code views are not kept).</summary>
    private static readonly Dictionary<string, string> ToolCommands = new()
    {
        ["cache"] = "/cache",
        ["jsonClient"] = "/json",
        ["symbolicPattern"] = "/symbol",
        ["ontology"] = "/ontology",
        ["tokenDashboard"] = "/token",
        ["logDashboard"] = "/log",
        ["system"] = "/system",
        ["config"] = "/config",
        ["calculator"] = "/calculator",
        ["idea"] = "/idea",
        ["pictures"] = "/pictures",
        ["music"] = "/music",
        ["journal"] = "/journal",
        ["youtube"] = "/youtube",
        ["completion"] = "/completion"
    };

    /// <summary>Split layout: pane of each tool being reopened from screens.yml (read when its tab is created).</summary>
    private IReadOnlyDictionary<string, int>? _pendingPanes;

    /// <summary>
    /// screens.yml is also written shortly after the tabs change (opened, closed, moved to another pane, selected), so
    /// that the layout is kept even when the application is not closed with /bye.
    /// </summary>
    private readonly DispatcherTimer _layoutSave = new() { Interval = TimeSpan.FromSeconds(1.5) };

    /// <summary>Saves the layout of the windows a moment after the last change of the tabs.</summary>
    private void ScheduleLayoutSave()
    {
        if (_exiting || _restoringLayout)
            return;
        _layoutSave.Stop();
        _layoutSave.Start();
    }

    /// <summary>True while screens.yml is being restored (its own tab openings must not overwrite it).</summary>
    private bool _restoringLayout;

    /// <summary>Place of the window being reopened from screens.yml (read when it is created).</summary>
    private WindowPlacement? _pendingPlacement;

    /// <summary>Screen of the chat, tabs and tool windows, with their places (written to screens.yml by /bye).</summary>
    private ScreenLayout CaptureLayout()
    {
        var layout = new ScreenLayout
        {
            ChatScreen = Screens.ScreenFromWindow(this) is { } screen ? ScreenPlacement.Describe(screen) : null
        };

        if (_workspace is { } workspace && workspace.TabKeys.Where(ToolCommands.ContainsKey).ToList() is { Count: > 0 } tabs)
            layout.Workspace = new WorkspaceLayout
            {
                Placement = ScreenPlacement.Capture(workspace),
                Tabs = tabs,
                Selected = workspace.SelectedKey is { } selected && ToolCommands.ContainsKey(selected) ? selected : null,
                Panes = workspace.PaneOfTabs.Where(p => ToolCommands.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value),
                PaneSelections = workspace.PaneSelections.Select(k => k is not null && ToolCommands.ContainsKey(k) ? k : null).ToList(),
                ColumnShares = workspace.IsSplit ? workspace.ColumnShares.ToList() : null,
                RowShares = workspace.IsSplit ? workspace.RowShares.ToList() : null
            };

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            foreach (var window in desktop.Windows.OfType<ToolWindow>().Where(w => ToolCommands.ContainsKey(w.Key)))
                layout.Windows.Add(new WindowLayout { Tool = window.Key, Placement = ScreenPlacement.Capture(window) });

        return layout;
    }

    /// <summary>Reopens the tabs and tool windows saved in screens.yml, at their places.</summary>
    private void RestoreLayout(ScreenLayout layout)
    {
        if (_viewModel is null)
            return;

        try
        {
            _restoringLayout = true;
            if (layout.Workspace is { } workspace)
            {
                _pendingPlacement = workspace.Placement;
                // Split layout: each tool goes back to its pane.
                _pendingPanes = workspace.Panes;
                foreach (var tab in workspace.Tabs)
                    if (ToolCommands.TryGetValue(tab, out var command))
                        TryOpenTool(command, detached: false);
                _workspace?.SelectInPanes(workspace.PaneSelections);
                // Split layout: the sizes of the panes, as they were left (kept only when the grid is the same; the
                // splitX / splitY of older screens.yml files are the 2 × 2 shares).
                if (_workspace is { IsSplit: true } restored)
                {
                    if (workspace.ColumnShares is { } columnShares && workspace.RowShares is { } rowShares)
                    {
                        restored.ColumnShares = columnShares;
                        restored.RowShares = rowShares;
                    }
                    else if (workspace.SplitX is { } splitX && workspace.SplitY is { } splitY)
                    {
                        restored.ColumnShares = [splitX, 1 - splitX];
                        restored.RowShares = [splitY, 1 - splitY];
                    }
                }
                if (workspace.Selected is { } selected)
                    _workspace?.Select(selected);
            }

            foreach (var window in layout.Windows)
            {
                _pendingPlacement = window.Placement;
                if (ToolCommands.TryGetValue(window.Tool, out var command))
                    TryOpenTool(command, detached: true);
            }
        }
        finally
        {
            _pendingPlacement = null;
            _pendingPanes = null;
            _restoringLayout = false;
        }

        ActivityLog.Info("Screens", $"screens.yml restored ({layout.Workspace?.Tabs.Count ?? 0} tab(s), {layout.Windows.Count} window(s))");
    }

    /// <summary>Reopens one tool of screens.yml; a tool that cannot open (folder, registry…) is skipped, the others are restored.</summary>
    private void TryOpenTool(string command, bool detached)
    {
        try
        {
            _viewModel?.OpenTool(command, detached);
        }
        catch (Exception ex)
        {
            ActivityLog.Error("Screens", $"{command} not restored from screens.yml", ex);
        }
    }

    /// <summary>
    /// Shuts the whole application down (every window closes and the Exit handlers run, e.g. saving the theme).
    /// </summary>
    private void OnExitRequested()
    {
        _exiting = true;
        // The windows (screens, places, tabs) are reopened as they are now at the next start.
        ScreenLayoutStore.Save(CaptureLayout());
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Close();
    }

    /// <summary>
    /// True once the docked chat was set up: OnOpened is raised again by every Show() after a Hide() (systray, full chat
    /// closed), and the setup (guard, screens, timers, screens.yml, full chat) must run once only.
    /// </summary>
    private bool _opened;

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_isFullChat)
        {
            OnFullChatOpened();
            return;
        }
        if (_opened)
            return;
        _opened = true;

        // Always docked on the largest screen (widest ratio), whatever screen it was on at the last /bye.
        var layout = ScreenLayoutStore.Load();
        ScreenPlacement.DockRightOnLargestScreen(this);
        // Always on top, fixed at its docked place; the other windows are kept clear of it.
        WindowGuard.Install(this);
        WindowGuard.Pin(this);
        // The strip of the chat is removed from the screen of every application (Windows AppBar).
        AppBar.Dock(this);
        // A screen unplugged or plugged back: the chat display is reset on the screens connected then.
        _screenBounds = Screens.All.Select(s => s.Bounds).ToList();
        Screens.Changed += OnScreensChanged;
        _screensReset.Tick += (_, _) =>
        {
            _screensReset.Stop();
            ResetDisplay();
        };
        InputBox.Focus();
        _viewModel?.RefreshModelsCommand.Execute(null);
        // The Kaonomi server is checked now, then every 5 seconds.
        _ = _viewModel?.CheckServerAsync();
        _serverCheck.Start();

        _viewModel?.RefreshTickerCommand.Execute(null);
        _tickerClock.Start();
        _tickerAnimation.Start();
        _tickerRefresh.Start();

        // Once the chat is docked (so that the workspace takes the free space next to it): the windows of screens.yml
        // are reopened, or the system tab when there is none; the focus goes back to the input box.
        Dispatcher.UIThread.Post(() =>
        {
            // A broken screens.yml or a tool that cannot open must not end the application at every start.
            try
            {
                if (layout is not null)
                    RestoreLayout(layout);
                else
                    _viewModel?.OpenSystemWindow();
                OpenFullChat();
            }
            catch (Exception ex)
            {
                ActivityLog.Error("Screens", "Windows not restored at startup", ex);
            }
            // With a full chat, the docked chat is hidden: the full chat keeps the focus.
            if (_fullChat is null)
            {
                Activate();
                InputBox.Focus();
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Vertical screen (taller than wide) for the full chat: one other than the docked chat's screen when possible,
    /// the largest first; null when no screen is vertical.
    /// </summary>
    private Screen? FindVerticalScreen()
    {
        var chatScreen = Screens.ScreenFromWindow(this);
        return Screens.All
            .Where(s => s.Bounds.Height > s.Bounds.Width)
            .OrderBy(s => chatScreen is not null && s.Bounds == chatScreen.Bounds)
            .ThenByDescending(s => (long)s.Bounds.Width * s.Bounds.Height)
            .FirstOrDefault();
    }

    /// <summary>
    /// Opens the full chat. At startup (<paramref name="inZone"/> false), on a vertical screen when one is connected: the
    /// docked chat is hidden meanwhile (its strip of the screen is given back) and comes back when the full chat closes;
    /// nothing happens without a vertical screen. With <paramref name="inZone"/> (<c>/fullchat</c>), in a work zone next
    /// to the docked chat (the left column of the zones), the workspace is rebuilt in the other zones, and the docked
    /// chat stays open.
    /// </summary>
    private void OpenFullChat(bool inZone = false)
    {
        if (_fullChat is { } open)
        {
            open.Activate();
            open.InputBox.Focus();
            return;
        }

        PixelRect area;
        var vertical = inZone ? null : FindVerticalScreen();
        if (inZone && FullChatZone() is { } zone)
            area = zone;
        else if (vertical is not null)
            area = vertical.WorkingArea;
        else
            return;

        var docked = vertical is null;
        _fullChat = new MainWindow(isFullChat: true) { DataContext = DataContext, _fullChatArea = area };
        // The 3D band is shown in the full chat only (hidden, it is not rendered); it comes back when the full chat closes.
        Band.IsVisible = false;
        if (docked)
        {
            WindowGuard.ReservedZone = area;
            RebuildWorkspace();
        }
        _fullChat.Closed += (_, _) =>
        {
            _fullChat = null;
            Band.IsVisible = true;
            if (docked)
            {
                WindowGuard.ReservedZone = null;
                if (!_resettingDisplay)
                    RebuildWorkspace();
            }
            if (!_exiting && !_resettingDisplay)
                ShowChat();
        };
        _fullChat.Show();
        if (docked)
        {
            ActivityLog.Info("FullChat", $"Full chat docked in a work zone next to the chat ({area.Width}x{area.Height})");
            return;
        }
        HideChat();
        ActivityLog.Info("FullChat", $"Full chat opened on the vertical screen {vertical!.DisplayName} ({vertical.Bounds.Width}x{vertical.Bounds.Height})");
    }

    /// <summary>Bounds of the connected screens, to tell a screen unplugged or plugged from the other changes.</summary>
    private List<PixelRect> _screenBounds = [];

    /// <summary>
    /// Waits for the screens to settle (several changes are reported while a screen is unplugged) before the reset.
    /// </summary>
    private readonly DispatcherTimer _screensReset = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>
    /// Screens changed: when one of them is gone or a new one is there, the display of the chat is reset a moment later.
    /// </summary>
    private void OnScreensChanged(object? sender, EventArgs e)
    {
        var bounds = Screens.All.Select(s => s.Bounds).ToList();
        var changed = _screenBounds.Any(b => !bounds.Contains(b)) || bounds.Any(b => !_screenBounds.Contains(b));
        _screenBounds = bounds;
        if (!changed || _exiting)
            return;
        _screensReset.Stop();
        _screensReset.Start();
    }

    /// <summary>
    /// Resets the display of the chat after a screen was unplugged or plugged: the chat is docked again on the largest
    /// screen (widest ratio), the full chat is opened again (on a vertical screen if there is one, else in its zone when
    /// it was docked in one; closed otherwise) and the workspace takes the free space next to the chat again, with the
    /// panes of that screen (its tools keep their state).
    /// </summary>
    private void ResetDisplay()
    {
        var wasDockedInZone = _fullChat is not null && WindowGuard.ReservedZone is not null;
        if (_fullChat is { } fullChat)
        {
            _resettingDisplay = true;
            try
            {
                fullChat.Close();
            }
            finally
            {
                _resettingDisplay = false;
            }
        }

        WindowGuard.Unpin();
        AppBar.Undock();
        ScreenPlacement.DockRightOnLargestScreen(this);
        WindowGuard.Pin(this);
        if (IsVisible)
            AppBar.Dock(this);

        OpenFullChat(inZone: wasDockedInZone);
        RebuildWorkspace();
        if (_fullChat is null)
            ShowChat();
        var screen = Screens.ScreenFromWindow(this);
        ActivityLog.Info("Screens", $"Screens changed: chat docked again on {screen?.DisplayName} ({screen?.Bounds.Width}x{screen?.Bounds.Height}), {Screens.All.Count} screen(s)");
    }

    /// <summary>True while the display is reset (the full chat closed then must not bring the docked chat back).</summary>
    private bool _resettingDisplay;

    /// <summary><c>/fullchat</c>: opens the full chat in a work zone next to the docked chat.</summary>
    private void OnFullChatRequested() => OpenFullChat(inZone: true);

    /// <summary>
    /// Work zone of the full chat: the left column of the work zones of the free space next to the docked chat, at full
    /// height (half of the free space on a screen with a single zone); null before the chat is set up.
    /// </summary>
    private PixelRect? FullChatZone()
    {
        if (WindowGuard.FreeArea() is not { } free)
            return null;
        var columns = Math.Max(2, WorkZones().Columns);
        return new PixelRect(free.X, free.Y, free.Width / columns, free.Height);
    }

    /// <summary>The open workspace takes the free space again (the full chat's zone was taken or given back).</summary>
    private void RefillWorkspace()
    {
        if (_workspace is { } workspace && WindowGuard.FreeArea() is { } free)
            ScreenPlacement.Fill(workspace, free);
    }

    /// <summary>Area the full chat fills: the working area of its vertical screen, or its zone next to the docked chat.</summary>
    private PixelRect? _fullChatArea;

    /// <summary>The full chat fills its area (vertical screen or zone); the ticker strip scrolls there too.</summary>
    private void OnFullChatOpened()
    {
        if (_fullChatArea is { } area)
            ScreenPlacement.Fill(this, area);
        Band.Height = Math.Round(ClientSize.Height * FullChatBandShare);
        _tickerClock.Start();
        _tickerAnimation.Start();
    }

    /// <summary>Set when the application really ends: the window may then close instead of going to the systray.</summary>
    private bool _exiting;

    /// <summary>The close button hides the chat in the systray (the application keeps running there).</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_isFullChat && !_exiting && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            HideChat();
            return;
        }
        base.OnClosing(e);
    }

    /// <summary>
    /// Shows the chat (from the systray): docked again on the right of the screen, focus in the input box; while the
    /// full chat is open, the full chat is brought to the front instead.
    /// </summary>
    public void ShowChat()
    {
        if (_fullChat is { } fullChat)
        {
            if (fullChat.WindowState == WindowState.Minimized)
                fullChat.WindowState = WindowState.Normal;
            fullChat.Activate();
            fullChat.InputBox.Focus();
            return;
        }

        if (!IsVisible)
        {
            Show();
            AppBar.Dock(this);
        }
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        InputBox.Focus();
    }

    /// <summary>Hides the chat in the systray and gives its strip of the screen back to the other applications.</summary>
    public void HideChat()
    {
        AppBar.Undock();
        Hide();
    }

    /// <summary>Systray icon click: shows the chat when hidden, hides it otherwise (brings the full chat to the front when open).</summary>
    public void ToggleVisibility()
    {
        if (_fullChat is not null)
            ShowChat();
        else if (IsVisible)
            HideChat();
        else
            ShowChat();
    }

    /// <summary>Clock of the Kaonomi band: time and date in the current culture.</summary>
    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockTime.Text = now.ToString("HH:mm:ss");
        ClockDate.Text = now.ToString("dddd d MMMM yyyy");
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _serverCheck.Stop();
        _screensReset.Stop();
        Screens.Changed -= OnScreensChanged;
        // The full chat only closes its own view: the voice and the system prompt belong to the docked chat.
        if (!_isFullChat)
        {
            // Closed for good (shutdown, session end...): the full chat closing after it must not show it again.
            _exiting = true;
            _viewModel?.StopSpeaking();
            _viewModel?.SaveSystemPrompt();
        }
        DataContext = null;
        _clock.Stop();
        _tickerAnimation.Stop();
        _tickerRefresh.Stop();
        base.OnClosed(e);
    }

    /// <summary>Thumb up on an answer: opens the evaluation popup (isNegativeFeedback = false).</summary>
    private async void OnLikeClick(object? sender, RoutedEventArgs e) => await EvaluateAsync(sender, isNegative: false);

    /// <summary>Thumb down on an answer: opens the evaluation popup (isNegativeFeedback = true).</summary>
    private async void OnDislikeClick(object? sender, RoutedEventArgs e) => await EvaluateAsync(sender, isNegative: true);

    /// <summary>Shows the evaluation popup over the chat; when the server accepted it, highlights the chosen thumb.</summary>
    private async Task EvaluateAsync(object? sender, bool isNegative)
    {
        if (_viewModel is null || sender is not Control { DataContext: ChatMessageViewModel message })
            return;

        var dialogViewModel = _viewModel.CreateEvaluation(message, isNegative);
        await new EvaluationDialog { DataContext = dialogViewModel }.ShowDialog(this);

        if (dialogViewModel.Succeeded)
            message.Feedback = !isNegative;
    }

    /// <summary>Copies the bubble's text (raw Markdown for answers) to the clipboard, then shows a check mark briefly.</summary>
    private async void OnCopyMessageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChatMessageViewModel message } button || Clipboard is null)
            return;

        var icon = button.Content as PathIcon;
        try
        {
            await Clipboard.SetTextAsync(message.Content);
            ActivityLog.Info("Chat", $"{message.Author} message copied to the clipboard ({message.Content.Length} chars)");
            SetCopyIcon(icon, "Icon.Check", "Copied");
        }
        catch (Exception ex)
        {
            SetCopyIcon(icon, "Icon.Error", $"Copy failed: {ex.Message}");
            ActivityLog.Error("Chat", "Copy to the clipboard failed", ex);
        }

        await Task.Delay(TimeSpan.FromSeconds(1.5));
        SetCopyIcon(icon, "Icon.Copy", "Copy to clipboard");

        void SetCopyIcon(PathIcon? glyph, string iconKey, string tip)
        {
            if (glyph is not null && this.TryFindResource(iconKey, out var data) && data is Geometry geometry)
                glyph.Data = geometry;
            ToolTip.SetTip(button, tip);
        }
    }

    /// <summary>Exports the conversation as JSON.</summary>
    private async void OnExportJsonClick(object? sender, RoutedEventArgs e) =>
        await ExportAsync("json", "JSON", "*.json", (stream, export) => ChatExportService.WriteJsonAsync(stream, export));

    /// <summary>Exports the conversation as a Word document.</summary>
    private async void OnExportWordClick(object? sender, RoutedEventArgs e) =>
        await ExportAsync("docx", "Word document", "*.docx", (stream, export) => ChatExportService.WriteWordAsync(stream, export));

    /// <summary>Asks for a destination file and writes the conversation snapshot to it.</summary>
    private async Task ExportAsync(
        string extension,
        string typeName,
        string pattern,
        Func<Stream, ChatExport, Task> write)
    {
        if (_viewModel is null)
            return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Export conversation ({typeName})",
            SuggestedFileName = $"kaonomi-chat-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}",
            DefaultExtension = extension,
            ShowOverwritePrompt = true,
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = [pattern] }]
        });
        if (file is null)
            return;

        try
        {
            // Snapshot taken after the dialog so the export includes everything up to now.
            var export = _viewModel.BuildExport();
            await using (var stream = await file.OpenWriteAsync())
                await write(stream, export);

            ExportStatus.Text = $"· Exported to {file.Name}";
            ActivityLog.Info("Export", $"Conversation exported as {typeName} to {file.Name} ({export.Messages.Count} message(s))");
        }
        catch (Exception ex)
        {
            ExportStatus.Text = $"· Export failed: {ex.Message}";
            ActivityLog.Error("Export", $"{typeName} export to {file.Name} failed", ex);
        }
    }

    /// <summary>Saves the prompt being typed to a text file of the prompts folder (config.yml).</summary>
    private async void OnSavePromptClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;

        var prompt = _viewModel.Input;
        if (string.IsNullOrWhiteSpace(prompt))
        {
            ExportStatus.Text = "· Type a prompt before saving it";
            return;
        }

        try
        {
            Directory.CreateDirectory(_viewModel.PromptDirectory);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save prompt",
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(_viewModel.PromptDirectory),
                SuggestedFileName = PromptFileName(prompt),
                DefaultExtension = "json",
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("Prompt") { Patterns = [PromptCollection.Pattern] }]
            });
            if (file is null)
                return;

            // Same format as the prompt collections: {"prompt": "…"} in a .prompt.json file.
            var content = PromptCollection.Serialize(prompt);
            string savedName;
            if (file.TryGetLocalPath() is { } path)
            {
                var target = PromptCollection.WithExtension(path);
                await File.WriteAllTextAsync(target, content, new System.Text.UTF8Encoding(false));
                savedName = Path.GetFileName(target);
            }
            else
            {
                await using (var stream = await file.OpenWriteAsync())
                await using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
                    await writer.WriteAsync(content);
                savedName = file.Name;
            }

            ExportStatus.Text = $"· Prompt saved to {savedName}";
            ActivityLog.Info("Prompt", $"Prompt saved to {savedName} ({prompt.Length} characters)");
        }
        catch (Exception ex)
        {
            ExportStatus.Text = $"· Prompt not saved: {ex.Message}";
            ActivityLog.Error("Prompt", "Prompt save failed", ex);
        }
    }

    /// <summary>Loads a saved prompt into the input box (it replaces the text being typed; it is not sent).</summary>
    private async void OnLoadPromptClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;

        try
        {
            Directory.CreateDirectory(_viewModel.PromptDirectory);
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Load prompt",
                AllowMultiple = false,
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(_viewModel.PromptDirectory),
                FileTypeFilter =
                [
                    new FilePickerFileType("Prompt") { Patterns = [PromptCollection.Pattern] },
                    new FilePickerFileType("Former text prompt") { Patterns = ["*.txt", "*.md"] },
                    new FilePickerFileType("All files") { Patterns = ["*.*"] }
                ]
            });
            if (files is not [var file])
                return;

            string text;
            await using (var stream = await file.OpenReadAsync())
            using (var reader = new StreamReader(stream))
                text = await reader.ReadToEndAsync();

            // A .json file is a prompt file ({"prompt": "…"}); a former .txt / .md prompt is read as plain text.
            string prompt;
            if (file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                if (PromptCollection.Parse(text) is not { } parsed)
                {
                    ExportStatus.Text = $"· {file.Name} has no prompt ({{\"prompt\": \"…\"}} expected)";
                    return;
                }
                prompt = parsed;
            }
            else
            {
                prompt = text;
            }

            _viewModel.Input = prompt.TrimEnd();
            InputBox.Focus();
            InputBox.CaretIndex = _viewModel.Input.Length;
            ActivityLog.Info("Prompt", $"Prompt loaded from {file.Name} ({prompt.Length} characters)");

            // With prompts.directprompt (config.yml), sent right away as if Enter had been pressed. Otherwise, or when it
            // cannot be sent now (an answer is still coming, no model, empty file), it stays in the input box.
            if (!_viewModel.DirectPrompt)
            {
                ExportStatus.Text = $"· Prompt loaded from {file.Name}";
            }
            else if (_viewModel.SendCommand.CanExecute(null))
            {
                ExportStatus.Text = $"· Prompt loaded from {file.Name} and sent";
                _viewModel.SendCommand.Execute(null);
            }
            else
            {
                ExportStatus.Text = $"· Prompt loaded from {file.Name} (not sent: press Enter when ready)";
            }
        }
        catch (Exception ex)
        {
            ExportStatus.Text = $"· Prompt not loaded: {ex.Message}";
            ActivityLog.Error("Prompt", "Prompt load failed", ex);
        }
    }

    /// <summary>File name suggested for a prompt: its first words, without characters forbidden in file names.</summary>
    private static string PromptFileName(string prompt)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var words = prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(c => !invalid.Contains(c)).ToArray()))
            .Where(w => w.Length > 0)
            .Take(6);
        var name = string.Join('-', words);
        if (name.Length > 60)
            name = name[..60];
        return (name.Length == 0 ? $"prompt-{DateTime.Now:yyyyMMdd-HHmmss}" : name) + PromptCollection.Extension;
    }

    /// <summary>Opens the clicked ticker message's URL in the default browser.</summary>
    private async void OnTickerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left
            || (e.Source as Control)?.DataContext is not TickerItemViewModel { Url: { } url })
            return;

        e.Handled = true;
        var opened = await Launcher.LaunchUriAsync(url);
        if (opened)
            ActivityLog.Info("Ticker", $"Ticker link opened: {url}");
        else
            ActivityLog.Error("Ticker", $"Ticker link could not be opened: {url}");
    }

    /// <summary>Scrolls the ticker right-to-left; a second copy follows the first so the loop is seamless.</summary>
    private void OnTickerAnimationTick(object? sender, EventArgs e)
    {
        var elapsed = _tickerClock.Elapsed.TotalSeconds;
        _tickerClock.Restart();

        var stripWidth = TickerStrip.Bounds.Width;
        if (stripWidth <= 0 || !TickerCanvas.IsEffectivelyVisible)
            return;

        if (!_tickerPaused)
            _tickerOffset -= TickerSpeed * elapsed;

        // Wrap once the first copy has fully left; also clamps the offset when the list shrinks after a refresh.
        _tickerOffset %= stripWidth;

        Canvas.SetLeft(TickerStrip, _tickerOffset);
        Canvas.SetLeft(TickerStripCopy, _tickerOffset + stripWidth);
    }

    /// <summary>
    /// Sends the message on Enter (Shift+Enter keeps a newline). Up / Down browse the input history,
    /// only from the first / last line so they still move the caret inside a multi-line message.
    /// </summary>
    /// <summary>Enter during the simulated streaming shows the rest of the answer at once (nothing is sent).</summary>
    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && _viewModel?.SkipStreaming() == true)
            e.Handled = true;

        // Esc stops the voice reading the answer (or the spoken summary being prepared).
        if (e.Key == Key.Escape && _viewModel?.IsSpeakingOrPreparing == true)
        {
            _viewModel.StopSpeaking();
            ActivityLog.Info("Speech", "Reading aloud stopped (Esc)");
            e.Handled = true;
        }

        // Esc stops a running prompt collection, including the prompt being answered.
        if (e.Key == Key.Escape && _collectionRun is { IsCancellationRequested: false } run)
        {
            run.Cancel();
            if (_viewModel?.SendCancelCommand.CanExecute(null) == true)
                _viewModel.SendCancelCommand.Execute(null);
            ExportStatus.Text = "· Stopping the prompt collection…";
            e.Handled = true;
        }
    }

    /// <summary>Set while a prompt collection runs; cancelled by Esc.</summary>
    private CancellationTokenSource? _collectionRun;

    /// <summary>
    /// "Prompt collection": pick a folder, confirm the number of <c>*.prompt.json</c> prompts found, then run them in
    /// the chat one after the other (each prompt waits for the previous answer). Esc stops the run.
    /// </summary>
    private async void OnPromptCollectionClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
            return;
        if (_collectionRun is not null)
        {
            ExportStatus.Text = "· A prompt collection is already running (Esc to stop it)";
            return;
        }

        try
        {
            Directory.CreateDirectory(_viewModel.PromptDirectory);
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Prompt collection: choose the folder of the *.prompt.json prompts",
                AllowMultiple = false,
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(_viewModel.PromptDirectory)
            });
            if (folders is not [var folder] || folder.TryGetLocalPath() is not { } path)
                return;

            var files = PromptCollection.Find(path);
            if (files.Count == 0)
            {
                ExportStatus.Text = $"· No {PromptCollection.Pattern} file in {path}";
                return;
            }

            var message = $"{files.Count} prompt(s) ({PromptCollection.Pattern}) found in {path}.\n\n"
                          + "Run them in the chat, one after the other?\nPress Esc to stop.";
            if (!await ConfirmDialog.AskAsync(this, message, "Run"))
                return;

            await RunPromptCollectionAsync(files, path);
        }
        // async void: anything left would end the process (picker, dialog on a hidden chat, server answers).
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ExportStatus.Text = $"· Prompt collection not run: {ex.Message}";
            ActivityLog.Error("Prompt", "Prompt collection failed", ex);
        }
    }

    /// <summary>
    /// Sends each prompt of the collection and waits for its answer before the next one. Each completion is saved as
    /// JSON in a child folder named from the date and the model; then the topics of all the prompts are summarised by
    /// the model, the run is written to <c>name.rapport.json</c> (counts, execution time, machine, date, user, words,
    /// topics, every prompt with its completion) and the Word document is generated from that file.
    /// </summary>
    private async Task RunPromptCollectionAsync(IReadOnlyList<string> files, string collectionFolder)
    {
        using var run = new CancellationTokenSource();
        _collectionRun = run;
        // Batch: each answer is shown at once (no simulated streaming).
        _viewModel!.IsBatchRunning = true;

        var name = Path.GetFileName(collectionFolder.TrimEnd('\\', '/'));
        var prompts = files.Select(file => (File: file, Prompt: PromptCollection.Read(file))).ToList();
        var totalWords = prompts.Sum(p => CollectionReport.CountWords(p.Prompt));
        var model = _viewModel.CurrentModel ?? "unknown";
        var start = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();
        var runFolder = Path.Combine(collectionFolder, CollectionReport.RunFolderName(start, model));
        var items = new List<CollectionReportItem>();
        // "…-FR" / "…-EN" collection folders: the language goes into every completion file and into the report.
        var language = CollectionReport.LanguageOf(collectionFolder);
        int serverSaved = 0, serverFailed = 0;
        var skipped = 0;
        string? stoppedBecause = null;
        ActivityLog.Info("Prompt", $"Prompt collection {name}: {files.Count} prompt(s), {totalWords} word(s), model {model}, results in {runFolder}");

        try
        {
            Directory.CreateDirectory(runFolder);
            for (var i = 0; i < prompts.Count && !run.IsCancellationRequested; i++)
            {
                if (prompts[i].Prompt is not { } prompt)
                {
                    skipped++;
                    continue;
                }

                // An answer still coming (e.g. a prompt typed meanwhile) is waited for.
                while (_viewModel.SendCommand.IsRunning && !run.IsCancellationRequested)
                    await Task.Delay(200);
                if (run.IsCancellationRequested)
                    break;

                ExportStatus.Text = $"· Prompt collection {name}: {i + 1}/{files.Count} (Esc to stop)";
                _viewModel.Input = prompt;
                if (!_viewModel.SendCommand.CanExecute(null))
                {
                    stoppedBecause = "the prompt cannot be sent (no model selected?)";
                    break;
                }

                var sentAt = DateTimeOffset.Now;
                var timer = Stopwatch.StartNew();
                await _viewModel.SendCommand.ExecuteAsync(null);
                var answer = _viewModel.LastAnswer;
                var item = new CollectionItem
                {
                    Index = i + 1,
                    File = Path.GetFileName(prompts[i].File),
                    Prompt = prompt,
                    PromptWords = CollectionReport.CountWords(prompt),
                    Completion = answer?.Content ?? string.Empty,
                    Model = model,
                    Date = sentAt,
                    DurationSeconds = Math.Round(timer.Elapsed.TotalSeconds, 2),
                    Stats = answer?.Stats,
                    IsError = answer?.IsError ?? true,
                    Language = language
                };
                var completionPath = await CollectionReport.SaveCompletionAsync(runFolder, item);
                items.Add(new CollectionReportItem
                {
                    Index = item.Index,
                    File = item.File,
                    Prompt = item.Prompt,
                    PromptWords = item.PromptWords,
                    CompletionFile = Path.GetFileName(completionPath)
                });

                // With a language ("…-FR" / "…-EN" folder), the prompt and its completion are also saved on the
                // server (PUT /completion). Failed answers are not sent; a server error never stops the run.
                if (language is not null && !item.IsError)
                {
                    try
                    {
                        // Terms are counted on the completion without its formatting (no Markdown markers).
                        var rawCompletion = PlainText.FromMarkdown(item.Completion);
                        var saved = await _viewModel.SaveCompletionAsync(new CompletionEntry
                        {
                            Date = item.Date,
                            Comment = $"Prompt collection {name}",
                            Prompt = item.Prompt,
                            Completion = item.Completion,
                            RawCompletion = rawCompletion,
                            WordCounts = TermCounter.WordCounts(item.Prompt, rawCompletion),
                            Model = model,
                            Language = language,
                            Topic = PromptCollection.ReadTopic(prompts[i].File),
                            CollectionName = name,
                            Metadatas =
                            [
                                new CompletionMetaData { Key = "PromptFile", ValueString = item.File },
                                new CompletionMetaData { Key = "Index", ValueInt = item.Index },
                                new CompletionMetaData { Key = "PromptWords", ValueInt = item.PromptWords },
                                new CompletionMetaData { Key = "DurationSeconds", ValueDouble = item.DurationSeconds }
                            ]
                        });
                        if (saved.ReturnedId == "-1")
                            throw new InvalidOperationException(saved.Message ?? "The server could not save the completion.");
                        serverSaved++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || ex is TaskCanceledException)
                    {
                        if (serverFailed++ == 0)
                            ActivityLog.Error("Prompt", $"Prompt collection {name}: completion not saved with PUT /completion", ex);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stoppedBecause = ex.Message;
            ActivityLog.Error("Prompt", $"Prompt collection {name}: completions not saved", ex);
        }
        finally
        {
            _collectionRun = null;
            _viewModel.IsBatchRunning = false;
        }

        clock.Stop();
        var stopped = run.IsCancellationRequested;
        var outcome = stopped ? "stopped (Esc)" : stoppedBecause is null ? "done" : $"stopped: {stoppedBecause}";

        // Summary of the topics of all the prompts (skipped when the run was stopped with Esc).
        var summary = stopped ? "Not generated: the run was stopped with Esc." : string.Empty;
        var topics = new List<CollectionTopic>();
        var readable = prompts.Where(p => p.Prompt is not null).Select(p => p.Prompt!).ToList();
        if (!stopped && readable.Count > 0)
        {
            ExportStatus.Text = $"· Prompt collection {name}: summarising the topics of {readable.Count} prompt(s)…";
            try
            {
                (summary, topics) = await _viewModel.SummarizeTopicsAsync(readable, CancellationToken.None);
            }
            catch (Exception ex)
            {
                summary = $"Not generated: {ex.Message}";
                ActivityLog.Error("Prompt", $"Prompt collection {name}: topic summary failed", ex);
            }
        }

        var report = new CollectionRunReport
        {
            Collection = name,
            CollectionFolder = collectionFolder,
            Model = model,
            Language = language,
            Date = start,
            EndDate = DateTimeOffset.Now,
            ExecutionTimeSeconds = Math.Round(clock.Elapsed.TotalSeconds, 1),
            ExecutionTime = clock.Elapsed.ToString(@"hh\:mm\:ss"),
            PromptCount = files.Count,
            SentCount = items.Count,
            SkippedCount = skipped,
            Stopped = stopped || stoppedBecause is not null,
            TotalPromptWords = totalWords,
            TopicsSummary = summary,
            Topics = topics,
            Items = items
        };

        try
        {
            var reportPath = await CollectionReport.WriteReportAsync(runFolder, report);
            var wordPath = CollectionReport.WriteWordFromReport(reportPath);
            var serverNote = language is null
                ? string.Empty
                : $" · PUT /completion ({language}): {serverSaved} saved" + (serverFailed > 0 ? $", {serverFailed} failed" : string.Empty);
            ExportStatus.Text = $"· Prompt collection {name} {outcome}: {items.Count}/{files.Count} sent, {totalWords} word(s), "
                                + $"{report.ExecutionTime}{serverNote} · report: {Path.GetFileName(reportPath)} + {Path.GetFileName(wordPath)} in {runFolder}";
            ActivityLog.Info("Prompt", $"Prompt collection {name} {outcome}: {items.Count}/{files.Count} sent in {report.ExecutionTime}{serverNote}, report {reportPath}");
        }
        catch (Exception ex)
        {
            ExportStatus.Text = $"· Prompt collection {name} {outcome}, but the report was not written: {ex.Message}";
            ActivityLog.Error("Prompt", $"Prompt collection {name}: report not written", ex);
        }
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || e.KeyModifiers != KeyModifiers.None && e.Key != Key.Enter)
            return;

        switch (e.Key)
        {
            case Key.Enter when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                e.Handled = true;
                if (_viewModel.SendCommand.CanExecute(null))
                    _viewModel.SendCommand.Execute(null);
                break;

            case Key.Up when IsCaretOnFirstLine() && _viewModel.RecallPreviousInput():
            case Key.Down when IsCaretOnLastLine() && _viewModel.RecallNextInput():
                e.Handled = true;
                // The binding updates the text first; then put the caret at the end, like a shell.
                Dispatcher.UIThread.Post(() => InputBox.CaretIndex = InputBox.Text?.Length ?? 0, DispatcherPriority.Background);
                break;
        }
    }

    private bool IsCaretOnFirstLine()
    {
        var text = InputBox.Text ?? string.Empty;
        var caret = Math.Clamp(InputBox.CaretIndex, 0, text.Length);
        return caret == 0 || text.LastIndexOf('\n', caret - 1) < 0;
    }

    private bool IsCaretOnLastLine()
    {
        var text = InputBox.Text ?? string.Empty;
        var caret = Math.Clamp(InputBox.CaretIndex, 0, text.Length);
        return text.IndexOf('\n', caret) < 0;
    }

    /// <summary>Subscribes to new messages and scrolls the transcript.</summary>
    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ChatMessageViewModel message in e.OldItems)
                message.PropertyChanged -= OnMessagePropertyChanged;

        if (e.NewItems is not null)
            foreach (ChatMessageViewModel message in e.NewItems)
                message.PropertyChanged += OnMessagePropertyChanged;

        ScrollToEnd();
    }

    /// <summary>Scrolls when message content or stats change during streaming updates.</summary>
    private void OnMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatMessageViewModel.Content) or nameof(ChatMessageViewModel.Stats))
            ScrollToEnd();
    }

    private void ScrollToEnd() =>
        Dispatcher.UIThread.Post(() => MessagesScroller.ScrollToEnd(), DispatcherPriority.Background);
}
