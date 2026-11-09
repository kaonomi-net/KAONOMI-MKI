using System;
using System.Linq;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot;

/// <summary>
/// The Kaonomi chat in Godot, in the style of EVE Online, with every function of the default chat (its view models and
/// services are shared as they are). On the left the bar of commands (one icon per tool, a command box at its top), on
/// the right the chat with its 3D animation; between them the desktop: every tool opens there as a window, flat in front
/// while it is in use, at the back in 3D in a perspective otherwise (a click on a card brings it back). There are no red
/// strings between the windows in this version.
/// Command line (after <c>--</c>): <c>--prompt="…"</c> sends a first prompt, <c>--command=/ontology</c> runs a command,
/// <c>--avatar=MARVIN</c> picks the 3D animation, <c>--back-after=2</c> sends the window in front to the back after 2 s, <c>--voice</c> starts in the voice mode, <c>--theme=kaonomi</c> starts in that theme, <c>--themes</c> opens the screen of the themes, <c>--no-intro</c> starts without the intro.
/// </summary>
public partial class Main : Control
{
    private MainViewModel _vm = null!;
    private Shell _shell = null!;
    private WindowDeck _deck = null!;
    private Neocom _neocom = null!;
    private ChatSettings _settings = null!;
    private ChatPanel _chat = null!;
    private ShaderMaterial _space = null!;

    /// <summary>The wallpaper, over the background of the theme and under the whole interface (the chat included).</summary>
    private TextureRect _wallpaper = null!;

    /// <summary>The background of the theme (its picture and its animated shader): not drawn while the wallpaper covers it.</summary>
    private readonly System.Collections.Generic.List<CanvasItem> _themeBackground = [];

    /// <summary>The last voice level given to the shader of the background (set only when it changes).</summary>
    private float _energy = -1;
    private Control _ui = null!;
    private double _serverCheck, _tickerRefresh = 60, _layoutSave = 20;

    public override void _Ready()
    {
        DisplayServer.WindowSetTitle("KAONOMI");
        // Retina / high-DPI screens: the interface at the scale of the screen.
        var window = GetWindow();
        var scale = Math.Max(1f, DisplayServer.ScreenGetScale(window.CurrentScreen));
        window.ContentScaleFactor = scale;
        window.MinSize = (Vector2I)(new Vector2(1200, 760) * scale);
        if (scale > 1)
        {
            window.Size = (Vector2I)(new Vector2(1600, 960) * scale);
            window.MoveToCenter();
        }
        GetTree().AutoAcceptQuit = false;
        // Every view model asks with a dialog before a change it cannot undo, even one shown without a window of its own.
        Confirmations.Ask = KAONOMI.ChatGodot.Tools.Writing.WritingUi.Confirm;
        // The bank of the 1000 questions of /iq, shipped with the chat.
        IqWindowViewModel.BankPath = ProjectSettings.GlobalizePath("res://Data/iq-questions.json");
        // The web pages of the tools (Show on map…) open in the browser of the chat.
        PictureLibrary.WebOpener = url => Bind.OnMain(() => WebWindow.Open(url));
        // A right click on a text selected anywhere offers to annotate it.
        Tools.Data.Annotations.HookTextMenus(GetTree());
        // The URLs shown anywhere open in the browser.
        Eve.Links.Hook(GetTree());
        // Each SAVE of a window goes to the journal of the activities (index useractivities).
        ActivityJournal.Hook(GetTree());
        // The window of the chat where it was at the last close (screen, position, size, maximised…), once the scene is
        // set up: switching to full screen during _Ready makes Godot add a child to the busy root ("Parent node is busy").
        Callable.From(() => DeskLayout.RestoreMainWindow(window)).CallDeferred();

        AvaloniaBridge.Initialize();
        var settings = ChatSettings.Load();
        if (Argument("--avatar=") is { } avatar && Enum.TryParse<SceneAnimation>(avatar, true, out var animation))
            settings.Animation = animation;
        _settings = settings;
        _vm = new MainViewModel(settings);
        if (settings.KeepAwake)
            SleepGuard.Start();
        // The output and the microphone chosen with /audiosettings.
        AudioSettings.Restore();

        // The theme chosen last (--theme=kaonomi on the command line for a test).
        Eve.Eve.Use(Eve.Eve.Find(Argument("--theme=") ?? Preferences.Theme));
        BuildUi();
        WireTools();
        // The alarms of /alarm ring even when its window is closed (built once: a new theme keeps it).
        AddChild(new AlarmRinger(_vm.Say, () => _vm.VoiceLanguage,
            enterFullScreen: () =>
            {
                _chat.EnterVoiceMode();
                // The windows small at the bottom: the 3D animation in view, not a window in the middle.
                GetTree().CreateTimer(0.4).Timeout += () =>
                {
                    if (_deck.ShowsOnStage && !_deck.IsShelved)
                        _deck.ToggleShelf();
                };
            },
            leaveFullScreen: () => _chat.LeaveVoiceMode()));
        // /theme:kaonomi or /theme:eve typed in the chat changes the theme too.
        KAONOMI.Chat.Themes.AppTheme.Applied += kind => Bind.OnMain(() => ApplyTheme(Eve.Eve.Find(kind.ToString())));

        _vm.RefreshModelsCommand.Execute(null);
        _ = _vm.CheckServerAsync();
        _vm.RefreshTickerCommand.Execute(null);
        ActivityLog.Info("App", "Godot chat (EVE) started");
        // The windows open at the last close (in front or at the back) come back, once the desktop has its size.
        GetTree().CreateTimer(0.3).Timeout += RestoreDesktop;

        // --command=/cache;/json runs those commands one after the other.
        if (Argument("--command=") is { } commands)
            foreach (var command in commands.Trim('"').Split(';', StringSplitOptions.RemoveEmptyEntries))
                CallDeferred(MethodName.RunStartCommand, command.Trim());
        // --back-after=2: sends the window in front to the back after so many seconds (to see the desktop in 3D).
        if (Argument("--back-after=") is { } back && double.TryParse(back, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            GetTree().CreateTimer(seconds).Timeout += () => _deck.SendFrontToBack();
        // --open-url=https://…: opens that link in the browser of the chat.
        if (Argument("--open-url=") is { } url)
            GetTree().CreateTimer(1).Timeout += () => Eve.Links.Open(url.Trim('"'));
        if (Argument("--prompt=") is { } prompt)
            CallDeferred(MethodName.RunStartCommand, prompt.Trim('"'));
        _chat.CallDeferred(ChatPanel.MethodName.FocusInput);
        // --themes: opens the screen of the themes.
        if (OS.GetCmdlineUserArgs().Contains("--themes"))
            GetTree().CreateTimer(1).Timeout += OpenThemes;
        // The first start (until DONE on that screen), or --setup: the dependencies of the chat checked, with their installation.
        if (!Preferences.SetupDone || OS.GetCmdlineUserArgs().Contains("--setup"))
            GetTree().CreateTimer(1).Timeout += OpenSetup;
        // --voice: starts in the voice mode (the animation alone, the microphone).
        if (OS.GetCmdlineUserArgs().Contains("--voice"))
            GetTree().CreateTimer(0.5).Timeout += _chat.EnterVoiceMode;
        // The intro (the 3D animation that comes close and says "Let's go to think") over everything; --no-intro skips it.
        if (!OS.GetCmdlineUserArgs().Contains("--no-intro"))
        {
            var intro = new StartIntro(settings);
            intro.Finished += _chat.FocusInput;
            AddChild(intro);
        }
    }

    /// <summary>
    /// Runs a command or sends a prompt given on the command line; a prompt waits (20 s at most) for the server and the
    /// models, checked just after the start.
    /// </summary>
    private async void RunStartCommand(string text)
    {
        for (var i = 0; i < 40; i++)
        {
            _vm.Input = text;
            if (_vm.SendCommand.CanExecute(null))
            {
                _vm.RunCommand(text);
                return;
            }
            await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
        }
    }

    /// <summary>The value of <paramref name="name"/> on the command line (after <c>--</c>), or null.</summary>
    private static string? Argument(string name) =>
        OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith(name, StringComparison.Ordinal))?[name.Length..];

    /// <summary>The whole interface, in the theme in use: background, bar of commands, desktop, chat.</summary>
    private void BuildUi()
    {
        Theme = Eve.Eve.Shared;
        _ui = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _ui.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_ui);
        BuildBackground();
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 0);
        row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _ui.AddChild(row);
        _neocom = new Neocom();
        row.AddChild(_neocom);
        _deck = new WindowDeck
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            WallpaperRect = _wallpaper,
            // The windows not used for windowIdleCloseSeconds of config.yml close by themselves.
            IdleCloseSeconds = ChatSettings.Load().WindowIdleCloseSeconds
        };
        row.AddChild(_deck);
        _chat = new ChatPanel(_vm);
        row.AddChild(_chat);

        _shell = new Shell(_vm, _deck, this);
        _neocom.CommandRequested += command =>
        {
            if (command == Neocom.Themes.Command)
                OpenThemes();
            else if (command == Neocom.AllBack.Command)
                _deck.SendAllToBack();
            else if (command == Neocom.AllFront.Command)
                _deck.BringAllToFront();
            else
                _vm.RunCommand(command);
        };
        _deck.FrontChanged += _ => RefreshNeocom();
        // The menu of the empty desktop: the picture library, /think, the tools opened last.
        _deck.CommandRequested += command => _vm.RunCommand(command);
        // ❐ in the header of a window: the same tool in a second window.
        _deck.CloneRequested += window =>
        {
            var clone = _shell.Clone(window, tool => _vm.OpenTool(tool, false));
            _vm.Status = clone is null ? $"{window.Title} cannot be cloned" : $"{window.Title} cloned";
        };
        // 📝 in the header of a window: a note about it, with its data.
        _deck.NoteRequested += window => Tools.Data.NoteView.Open(_shell, window.Title, window.ViewModel);
        // 📁+ in the header of a window: the window, its data and a picture of it, into a folder.
        _deck.FolderRequested += window => Tools.Data.AddToFolderView.Open(_shell, window.Title, window.ViewModel, window.Picture());
    }

    /// <summary>/think: the black screen of the last hour over everything (once at a time).</summary>
    private void OpenThink()
    {
        if (GetChildren().OfType<ThinkScreen>().Any())
            return;
        var think = new ThinkScreen(_settings, _vm.Assistant);
        think.Finished += _chat.FocusInput;
        AddChild(think);
    }

    /// <summary>The screen of the dependencies (the first start, --setup, /setup), in a window of the desktop.</summary>
    private void OpenSetup()
    {
        if (_shell.TryBringBack(SetupScreen.Key))
            return;
        Control content = null!;
        content = SetupScreen.Build(_settings, () => _shell.Close(content));
        _shell.OpenControl(SetupScreen.Key, "Setup", SetupScreen.Icon, content, new Vector2(900, 640));
    }

    /// <summary>The screen of the themes, in a window of the desktop.</summary>
    private void OpenThemes()
    {
        if (_shell.TryBringBack(ThemeScreen.Key))
            return;
        _shell.OpenControl(ThemeScreen.Key, "Themes", Neocom.Themes.Icon, ThemeScreen.Build(ApplyTheme), new Vector2(720, 460));
    }

    /// <summary>
    /// Builds the interface again in <paramref name="palette"/> (kept for the next start): the windows open come back
    /// at their place, in front or at the back; the conversation stays.
    /// </summary>
    private async void ApplyTheme(Palette palette)
    {
        if (palette == Eve.Eve.Current)
            return;
        // The tool windows are opened again: what was typed in them and not saved would be lost.
        if (_deck.Windows.Any(w => w.Key != ThemeScreen.Key)
            && !await Dialogs.Confirm("Theme", $"The interface is built again in the {palette.Name} theme: the windows open come back at their place, but what is typed in them and not saved is lost."
                                               + (Wallpaper.Current is null ? string.Empty : " The wallpaper is removed: the theme shows its own background."), "Apply"))
            return;
        // The click that asked is still being handled by the interface: it is built again at the next frame.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!IsInstanceValid(_ui) || palette == Eve.Eve.Current)
            return;
        Preferences.Theme = palette.Name;
        // A wallpaper set by hand would hide the background of the new theme: it goes with the old theme.
        if (Wallpaper.Current is not null)
            Wallpaper.Clear();
        var themesOpen = _deck.Find(ThemeScreen.Key) is not null;
        DeskLayout.Save(GetWindow(), _deck);
        _deck.CloseAll();
        _ui.QueueFree();
        RemoveChild(_ui);
        Eve.Eve.Use(palette);
        BuildUi();
        ActivityLog.Info("App", $"Theme of the Godot chat: {palette.Name}");
        GetTree().CreateTimer(0.2).Timeout += () =>
        {
            RestoreDesktop();
            if (themesOpen)
                OpenThemes();
        };
    }

    private void BuildBackground()
    {
        // The picture of the theme, when it has one (the shader of the theme is then an overlay: its rain, its lights).
        if (Eve.Eve.Current.PicturePath is { } path && System.IO.File.Exists(path))
        {
            var image = Image.LoadFromFile(path);
            if (image is not null && !image.IsEmpty())
            {
                var picture = new TextureRect
                {
                    Texture = ImageTexture.CreateFromImage(image),
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
                    MouseFilter = MouseFilterEnum.Ignore
                };
                picture.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
                _ui.AddChild(picture);
                _themeBackground.Add(picture);
            }
            else
                GD.PushWarning($"The picture of the theme {path} could not be read");
        }
        _space = new ShaderMaterial { Shader = GD.Load<Shader>(Eve.Eve.Current.Background) };
        var background = new ColorRect { Material = _space, MouseFilter = MouseFilterEnum.Ignore };
        background.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _ui.AddChild(background);
        _themeBackground.Add(background);

        // The wallpaper on the whole surface of the application, under the bar of commands, the desktop and the chat
        // (the desktop puts the picture chosen in it, and hides it on the stage).
        _wallpaper = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _wallpaper.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _ui.AddChild(_wallpaper);
    }

    /// <summary>Every request of the chat to open a tool opens its window on the desktop.</summary>
    private void WireTools()
    {
        // The key of the window: the tool's own, or the one of a clone being made (/wiki#2).
        void Open(object viewModel, string key) => Bind.OnMain(() => _shell.Open(viewModel, _shell.KeyFor(key)));
        _vm.CodeWindowRequested += vm => Bind.OnMain(() => _shell.Open(vm));
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
        _vm.ActivityWindowRequested += (vm, _) => Open(vm, ActivityJournal.Command);
        _vm.FoldersWindowRequested += (vm, _) => Open(vm, "/folders");
        _vm.ProjectWindowRequested += (vm, _) => Open(vm, "/project");
        _vm.RemoteWindowRequested += (vm, _) => Open(vm, "/remote");
        _vm.PresetWindowRequested += (vm, _) => Open(vm, "/preset");
        _vm.ProcessWindowRequested += (vm, _) => Open(vm, "/process");
        _vm.GatewayWindowRequested += (vm, _) => Open(vm, "/gateway");
        _vm.WikiWindowRequested += (vm, _) => Open(vm, "/wiki");
        _vm.BpmnWindowRequested += (vm, _) => Open(vm, "/bpmnai");
        _vm.InfraWindowRequested += (vm, _) => Open(vm, "/infra");
        _vm.MindmapWindowRequested += (vm, _) => Open(vm, "/mindmap");
        _vm.GhostWriterWindowRequested += (vm, _) => Open(vm, "/write");
        _vm.ConsoleWindowRequested += (vm, _) => Open(vm, "/cmd");
        _vm.LogDashboardWindowRequested += (vm, _) => Open(vm, "/log");
        _vm.SystemWindowRequested += (vm, _) => Open(vm, "/system");
        _vm.RelationGraphRequested += (vm, _) => Open(vm, "/relation");
        // /fullchat: the chat as in its full screen (the 3D animation and the microphone), in a window of the desktop.
        _vm.FullChatRequested += detached => Bind.OnMain(() =>
        {
            if (_shell.Deck.Find(FullChatWindow.Key) is { } open)
            {
                if (detached)
                    DetachFullChat(open);
                else
                    _shell.Deck.BringToFront(open);
                return;
            }
            var window = OpenFullChat(new FullChatWindow(_vm));
            if (detached && window is not null)
                Callable.From(() => DetachFullChat(window)).CallDeferred();
        });
        _vm.SetupRequested += () => Bind.OnMain(OpenSetup);
        _vm.ThinkRequested += () => Bind.OnMain(OpenThink);
        _vm.MosaicRequested += () => Bind.OnMain(() =>
        {
            if (!_deck.Mosaic())
                _vm.Status = _deck.Windows.Count == 0 ? "/mosaic: no window open" : "/mosaic: not while the windows are on the stage (voice mode)";
        });
        _vm.WindowsRequested += () => Bind.OnMain(() =>
        {
            if (!_deck.ToggleCarousel())
                _vm.Status = _deck.Windows.Count == 0 ? "/windows: no window open" : "/windows: not while the windows are on the stage (voice mode)";
        });
        _vm.AudioSettingsRequested += () => Bind.OnMain(() => AudioSettings.Open(this));
        _vm.CloseAllRequested += () => Bind.OnMain(() => _ = _shell.Deck.RequestCloseAllAsync());
        _vm.ExitCountdown += remaining => Bind.OnMain(() => ShowCountdown(remaining));
        _vm.ExitRequested += () => Bind.OnMain(Quit);
    }

    /// <summary>The countdown of /bye shown over everything (null: cancelled, it goes away).</summary>
    private ExitCountdown? _countdown;

    private void ShowCountdown(int? remaining)
    {
        if (remaining is not { } seconds)
        {
            if (_countdown is not null && IsInstanceValid(_countdown))
                _countdown.QueueFree();
            _countdown = null;
            return;
        }
        if (_countdown is null || !IsInstanceValid(_countdown))
        {
            _countdown = new ExitCountdown(() => Bind.Run(_vm.SendCancelCommand));
            AddChild(_countdown);
        }
        _countdown.SetRemaining(seconds);
    }

    /// <summary>Lights the icons of the tools whose window is open.</summary>
    private void RefreshNeocom()
    {
        var open = _deck.Windows.Select(w => w.Key).ToHashSet();
        foreach (var (command, _, _) in Neocom.Tools.Append(Neocom.Settings).Append(Neocom.Themes))
            _neocom.SetOpen(command, open.Contains(command));
    }

    public override void _Process(double delta)
    {
        // Restoring the main window in _Ready can draw a frame before the interface is built.
        if (_space is null)
            return;
        AvaloniaBridge.Pump();
        // The background of the theme is not drawn under a wallpaper (its full-screen shader cost every frame for nothing).
        var covered = IsInstanceValid(_wallpaper) && _wallpaper.Visible && _wallpaper.Texture is not null;
        foreach (var item in _themeBackground)
            if (IsInstanceValid(item) && item.Visible == covered)
                item.Visible = !covered;
        var energy = (float)Math.Round(Math.Clamp(_vm.SpeechLevel, 0, 1), 2);
        if (Math.Abs(energy - _energy) > 0.001f)
        {
            _energy = energy;
            _space.SetShaderParameter("energy", energy);
        }
        _serverCheck += delta;
        if (_serverCheck > 5)
        {
            _serverCheck = 0;
            _ = _vm.CheckServerAsync();
        }
        // The windows saved every 20 s too: a chat stopped without closing (a signal, a crash) keeps them.
        _layoutSave -= delta;
        // Not while the voice mode covers the screen: the window then is not the one of the usual desktop.
        if (_layoutSave < 0 && !_quitting && !KioskLock.IsOn)
        {
            _layoutSave = 20;
            DeskLayout.Save(GetWindow(), _deck);
        }
        _tickerRefresh -= delta;
        if (_tickerRefresh < 0)
        {
            _tickerRefresh = 60;
            _vm.RefreshTickerCommand.Execute(null);
        }
    }

    public override void _Input(InputEvent e)
    {
        if (_deck is null)
            return;
        // A click outside the window in front takes the keyboard away from it, and the other way round: the chat and
        // the window in front never both receive the keys.
        if (e is InputEventMouseButton { Pressed: true } click)
        {
            if (_deck.FrontAt(click.GlobalPosition) is { } clicked)
            {
                // A click in a window in front makes it the active one (above the others) and gives it the keyboard.
                _deck.Activate(clicked);
                GetViewport().GuiReleaseFocus();
                foreach (var other in _deck.Windows.Where(w => w.IsFront && w != clicked))
                    other.Content.GetViewport()?.GuiReleaseFocus();
            }
            else
                foreach (var window in _deck.Windows.Where(w => w.IsFront))
                    window.Content.GetViewport()?.GuiReleaseFocus();
        }
        if (e is InputEventKey { Pressed: true } key)
        {
            if (key.Keycode == Key.Escape && PromptCollectionRunner.Stop())
                GetViewport().SetInputAsHandled();
            else if (key.Keycode == Key.Tab && key.CtrlPressed && !KioskLock.IsOn)
            {
                _deck.Cycle();
                GetViewport().SetInputAsHandled();
            }
            else if (key.Keycode == Key.M && key.CtrlPressed && !key.Echo)
            {
                ShowPointerTarget();
                GetViewport().SetInputAsHandled();
            }
        }
    }

    /// <summary>The layer of the target of Ctrl+M, above everything (the windows of the desktop live in layers of their own).</summary>
    private CanvasLayer? _targetLayer;
    private PointerTarget? _target;

    /// <summary>Ctrl+M: a target drawn around the pointer for two seconds (again while shown: the two seconds start over).</summary>
    private void ShowPointerTarget()
    {
        if (_target is not null && IsInstanceValid(_target))
        {
            _target.Restart();
            return;
        }
        if (_targetLayer is null || !IsInstanceValid(_targetLayer))
        {
            _targetLayer = new CanvasLayer { Name = "PointerTargetLayer", Layer = 120 };
            AddChild(_targetLayer);
        }
        _target = new PointerTarget();
        _targetLayer.AddChild(_target);
    }

    public override void _Notification(int what)
    {
        // The chat in full screen locked (the voice mode) does not close (Cmd+Q): the voice mode is left first.
        if (what == NotificationWMCloseRequest && !KioskLock.IsOn)
            Quit();
    }

    // The tools are opened directly (not as a typed command, which waits while an answer or /bye is running).
    // The windows saved come back, the clones too (/wiki#2 opens the tool /wiki under that key).
    private void RestoreDesktop() => DeskLayout.Restore(_deck, command => _shell.OpenToolAs(command, tool => _vm.OpenTool(tool, false)));

    /// <summary>
    /// The full chat (/fullchat) on the desktop, with ⧉ in its header: the window moves into a window of the system
    /// (to put on another screen); closing that window brings it back here.
    /// </summary>
    private DeckWindow? OpenFullChat(Control content)
    {
        var window = _shell.OpenControl(FullChatWindow.Key, "Chat", "🎙", content, new Vector2(720, 640));
        if (!ReferenceEquals(window.Content, content))
            return null;
        window.Frame.AddHeaderButton("⧉", "Open in a window of its own (of the system: it goes on another screen); closing it brings the chat back here", () =>
            // Deferred: the tooltip of the button (an embedded pop-up) must be gone before the embedding is turned off.
            Callable.From(() => DetachFullChat(window)).CallDeferred());
        return window;
    }

    /// <summary>The full chat of <paramref name="window"/> moved into a window of the system (back here when it is closed).</summary>
    private void DetachFullChat(DeckWindow window)
    {
        if (!IsInstanceValid(window.Content) || _shell.Deck.Find(FullChatWindow.Key) is null)
            return;
        var size = (Vector2I)window.Rect.Size.Max(new Vector2(480, 420));
        _vm.Status = NativeWindows.Detach(_shell, window, "KAONOMI · Chat", size, back => OpenFullChat(back));
    }

    private bool _quitting;

    public override void _ExitTree()
    {
        // Leaving without the close of the window (Ctrl+C in the terminal, the end of the process): the windows are kept.
        if (!_quitting && !KioskLock.IsOn && IsInstanceValid(_deck))
            DeskLayout.Save(GetWindow(), _deck);
    }

    /// <summary>True while the question about the changes not saved is shown (a second close waits for it).</summary>
    private bool _askingToQuit;

    private async void Quit()
    {
        // Once: /bye then the close of the window must not save an empty desktop over the windows saved.
        if (_quitting || _askingToQuit)
            return;
        // Windows with changes not saved (a writing, a page, a diagram…): the user says first whether to lose them.
        if (_deck is not null && IsInstanceValid(_deck) && _deck.UnsavedWindows() is { Count: > 0 } unsaved)
        {
            _askingToQuit = true;
            var quit = await Dialogs.Confirm("Quit KAONOMI",
                $"{unsaved.Count} window(s) have changes that are not saved:\n{WindowDeck.Titles(unsaved)}\n\nQuitting loses them.",
                "Quit without saving", "Cancel");
            _askingToQuit = false;
            if (!quit)
                return;
            ActivityLog.Info("App", $"Quit without saving {unsaved.Count} window(s)");
        }
        _quitting = true;
        // A start that failed before the desktop was built: nothing to save, the window just closes.
        if (_deck is null || _vm is null)
        {
            ActivityLog.Flush(TimeSpan.FromSeconds(2));
            GetTree().Quit();
            return;
        }
        // The windows open now (in front or at the back) come back at the next start.
        DeskLayout.Save(GetWindow(), _deck);
        _vm.StopSpeaking();
        _vm.SaveSystemPrompt();
        _deck.CloseAll();
        _vm.DisposeSpeech();
        SleepGuard.Stop();
        ActivityLog.Info("App", "Godot chat (EVE) closed");
        ActivityLog.Flush(TimeSpan.FromSeconds(2));
        GetTree().Quit();
    }
}
