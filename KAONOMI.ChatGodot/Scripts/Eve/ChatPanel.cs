using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The chat, on the right of the screen, bound to the view model of the Avalonia chat (<see cref="MainViewModel"/>): the
/// 3D animation of the preset (its mouth follows the voice), the clock and the state of the server, the model, preset
/// and history, the system prompt, the conversation (Markdown answers revealed as they come, thumbs, copy, code, stats),
/// the input with the push-to-talk microphone and its language, Send / Stop, and a menu for the exports (JSON, Word),
/// the saved prompts and the prompt collections.
/// </summary>
public partial class ChatPanel : PanelContainer
{
    private readonly MainViewModel _vm;
    private Control _avatarStage = null!;
    /// <summary>The block of the animation (hidden while a full chat shows it, <see cref="FullChatWindow.OpenCount"/>).</summary>
    private Control _avatarBlock = null!;
    private IAvatar? _avatar;
    private SceneAnimation? _shownAnimation;
    private Label _clock = null!, _date = null!, _server = null!, _status = null!, _avatarState = null!;
    private ScrollContainer _scroll = null!;
    private VBoxContainer _messages = null!;
    private TextEdit _input = null!;
    private Button _mic = null!;
    private Label _ticker = null!;
    private double _tickerOffset;
    private bool _stickToBottom = true;

    public ChatPanel(MainViewModel viewModel)
    {
        _vm = viewModel;
        CustomMinimumSize = new Vector2(460, 0);
        var box = Eve.Box(Eve.Panel, Eve.Edge with { A = 0f }, 0, 0);
        box.BorderWidthLeft = 1;
        box.BorderColor = Eve.Edge with { A = 0.6f };
        AddThemeStyleboxOverride("panel", box);
    }

    public override void _Ready()
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 0);
        AddChild(column);
        column.AddChild(BuildHeader());
        column.AddChild(BuildAvatar());
        column.AddChild(BuildTicker());
        column.AddChild(BuildOptions());
        column.AddChild(BuildSystemPrompt());
        column.AddChild(BuildConversation());
        column.AddChild(BuildInput());
        column.AddChild(BuildStatus());

        // Unsubscribed when the panel is freed (a new theme builds the interface again).
        Action<string> pulse = word => Bind.OnMain(() => _avatar?.Pulse(word.Length));
        Action<bool> speakingChanged = speaking => Bind.OnMain(() =>
        {
            if (!IsInstanceValid(this))
                return;
            if (_avatar is not null)
                _avatar.Speaking = speaking;
            UpdateAvatarState();
        });
        // FULLSCREEN:ON / OFF of a script of /remote: the voice mode (the chat in full screen) or back.
        Action<bool> fullScreen = on => Bind.OnMain(() =>
        {
            if (!IsInstanceValid(this))
                return;
            if (on)
                EnterVoiceMode();
            else
                LeaveVoiceMode();
        });
        _vm.WordStreamed += pulse;
        _vm.SpeakingChanged += speakingChanged;
        // IMAGE:<file> of a script of /remote: the picture over the animation, for 20 s.
        Action<string> image = path => Bind.OnMain(() =>
        {
            if (!IsInstanceValid(this) || _avatarView is not { } view || !IsInstanceValid(view))
                return;
            // The chat knows while a picture is shown (a script waits for it before leaving the full screen).
            _imagesShown++;
            _vm.IsShowingImage = true;
            void Ended()
            {
                _imagesShown = Math.Max(0, _imagesShown - 1);
                _vm.IsShowingImage = _imagesShown > 0;
            }
            if (!ImageShow.Show(view, path, Ended))
            {
                Ended();
                _vm.Status = $"The picture {Path.GetFileName(path)} cannot be read.";
            }
        });
        _vm.FullScreenRequested += fullScreen;
        _vm.ImageShowRequested += image;
        Bind.OnGone(this, () =>
        {
            _vm.WordStreamed -= pulse;
            _vm.SpeakingChanged -= speakingChanged;
            _vm.FullScreenRequested -= fullScreen;
            _vm.ImageShowRequested -= image;
        });
        Bind.When(this, _vm, () =>
        {
            if (_avatar is not null)
                _avatar.Thinking = _vm.IsAwaitingAnswer;
            UpdateAvatarState();
        }, nameof(MainViewModel.IsAwaitingAnswer), nameof(MainViewModel.IsListening), nameof(MainViewModel.IsTranscribing));
        Bind.When(this, _vm, ShowPresetAnimation, nameof(MainViewModel.SelectedPreset), nameof(MainViewModel.Animation));
        // A full chat open: no animation here (also for a chat built again by a new theme while it is open).
        Action fullChat = () => Bind.OnMain(FullChatChanged);
        FullChatWindow.OpenChanged += fullChat;
        Bind.OnGone(this, () => FullChatWindow.OpenChanged -= fullChat);
        FullChatChanged();
    }

    public override void _Process(double delta)
    {
        var now = DateTime.Now;
        _clock.Text = now.ToString("HH:mm:ss");
        _date.Text = now.ToString("ddd d MMM yyyy").ToUpperInvariant();
        // The ticker scrolls from right to left.
        if (_ticker.Visible && _ticker.Text.Length > 0)
        {
            _tickerOffset -= delta * 60;
            var width = _ticker.GetMinimumSize().X;
            if (_tickerOffset < -width)
                _tickerOffset = _ticker.GetParentControl().Size.X;
            _ticker.Position = new Vector2((float)_tickerOffset, 3);
        }
    }

    // ── Header: title, clock, server ──

    private Control BuildHeader()
    {
        var panel = new PanelContainer();
        var box = Eve.Box(Eve.Header, Eve.Edge with { A = 0f }, 0, 0);
        box.BorderWidthBottom = 1;
        box.BorderColor = Eve.Edge with { A = 0.6f };
        box.ContentMarginLeft = box.ContentMarginRight = 12;
        box.ContentMarginTop = box.ContentMarginBottom = 6;
        panel.AddThemeStyleboxOverride("panel", box);
        var row = new HBoxContainer();
        panel.AddChild(row);
        var titles = new VBoxContainer();
        titles.AddThemeConstantOverride("separation", 0);
        titles.AddChild(Eve.Caption("Kaonomi", Eve.TextBright, 15));
        _server = Eve.Caption("Server · checking", Eve.Muted, 10);
        titles.AddChild(_server);
        titles.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(titles);
        var time = new VBoxContainer();
        time.AddThemeConstantOverride("separation", 0);
        _clock = Eve.Text_("--:--:--", Eve.TextBright, 18);
        _clock.AddThemeFontOverride("font", Eve.Mono);
        _clock.HorizontalAlignment = HorizontalAlignment.Right;
        _date = Eve.Caption("", Eve.Muted, 10);
        _date.HorizontalAlignment = HorizontalAlignment.Right;
        time.AddChild(_clock);
        time.AddChild(_date);
        row.AddChild(time);
        Bind.When(_server, _vm, () =>
        {
            var ok = _vm.IsServerReachable;
            _server.Text = ok ? "● SERVER ONLINE" : "● SERVER OFFLINE";
            _server.AddThemeColorOverride("font_color", ok ? Eve.Green : Eve.Red);
        }, nameof(MainViewModel.IsServerReachable));
        return panel;
    }

    // ── The 3D animation ──

    private Control BuildAvatar()
    {
        var stage = new Control { CustomMinimumSize = new Vector2(0, 250), ClipContents = true, MouseFilter = MouseFilterEnum.Stop, TooltipText = "Double-click: voice mode (the animation alone on the screen, a microphone)" };
        stage.GuiInput += e =>
        {
            if (e is InputEventMouseButton { DoubleClick: true, ButtonIndex: MouseButton.Left })
                EnterVoiceMode();
        };
        _avatarStage = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _avatarStage.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        stage.AddChild(_avatarStage);
        _avatarState = Eve.Caption("Standby", Eve.Muted, 10);
        _avatarState.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomLeft);
        _avatarState.Position = new Vector2(12, 226);
        stage.AddChild(_avatarState);
        _avatarBlock = stage;
        return stage;
    }

    /// <summary>The voice mode is shown now (it holds the animation of the chat).</summary>
    private bool InVoiceMode => _voiceMode is { } voice && IsInstanceValid(voice) && !voice.IsQueuedForDeletion();

    /// <summary>A full chat open (/fullchat) shows the animation: the chat has none then (one 3D animation drawn, not two); it comes back when the last full chat closes.</summary>
    private bool FullChatShown => FullChatWindow.OpenCount > 0 && !InVoiceMode;

    private void FullChatChanged()
    {
        if (!IsInstanceValid(this))
            return;
        _avatarBlock.Visible = !FullChatShown;
        if (FullChatShown)
        {
            foreach (var child in _avatarStage.GetChildren())
                child.QueueFree();
            _avatar = null;
            _avatarView = null;
            _shownAnimation = null;
        }
        else if (_shownAnimation is null)
            ShowPresetAnimation();
    }

    /// <summary>The pictures of IMAGE: being shown (until the animation is back at its place).</summary>
    private int _imagesShown;

    /// <summary>The view of the 3D animation (in the chat, or in the voice mode while it is shown).</summary>
    private Control? _avatarView;

    /// <summary>The voice mode shown now (null: the usual chat).</summary>
    private VoiceMode? _voiceMode;

    /// <summary>The voice mode: the animation on the whole screen with a microphone; it comes back here when it ends.</summary>
    public void EnterVoiceMode()
    {
        if (_voiceMode is { } shown && GodotObject.IsInstanceValid(shown) && !shown.IsQueuedForDeletion())
            return;
        if (_avatarStage.GetChildCount() == 0 || Shell.Current.Root is not Control root)
            return;
        var view = (Control)_avatarStage.GetChild(0);
        VoiceMode? mode = null;
        // Back in the chat: the animation shown at the end (a preset changed meanwhile has replaced the first one).
        mode = new VoiceMode(_vm, view, shown =>
        {
            if (GodotObject.IsInstanceValid(shown))
            {
                shown.Reparent(_avatarStage, false);
                shown.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            }
            if (ReferenceEquals(_voiceMode, mode))
                _voiceMode = null;
            // A full chat opened meanwhile: it shows the animation, not the chat.
            FullChatChanged();
        });
        _voiceMode = mode;
        root.AddChild(mode);
    }

    /// <summary>Leaves the voice mode when it is shown.</summary>
    public void LeaveVoiceMode()
    {
        if (_voiceMode is { } shown && GodotObject.IsInstanceValid(shown))
            shown.Leave();
        _voiceMode = null;
    }

    private void ShowPresetAnimation()
    {
        var animation = _vm.SelectedPreset?.Animation ?? _vm.Animation;
        if (animation == SceneAnimation.None)
            animation = SceneAnimation.Kaonomi;
        // The full chat shows it: made again here when it closes.
        if (FullChatShown || _shownAnimation == animation)
            return;
        _shownAnimation = animation;
        // In the voice mode the animation is shown there: the new one replaces it there.
        var inVoiceMode = _voiceMode is { } voice && IsInstanceValid(voice) && !voice.IsQueuedForDeletion();
        if (!inVoiceMode)
            foreach (var child in _avatarStage.GetChildren())
                child.QueueFree();
        Control view;
        if (animation == SceneAnimation.Jarvis)
        {
            var jarvis = new JarvisAvatar();
            view = jarvis;
            _avatar = jarvis;
        }
        else
        {
            FaceAvatar face;
            try
            {
                face = new FaceAvatar(animation);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Collections.Generic.KeyNotFoundException)
            {
                face = new FaceAvatar(SceneAnimation.Kaonomi);
            }
            view = face;
            _avatar = face;
        }
        view.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        if (inVoiceMode)
            _voiceMode!.SwapAvatar(view);
        else
            _avatarStage.AddChild(view);
        _avatarView = view;
        // A picture of IMAGE: shown stays in front of the new animation.
        Callable.From(() => ImageShow.Retarget(view)).CallDeferred();
        _avatar.VoiceLevel = () => _vm.SpeechLevel;
        _avatar.Thinking = _vm.IsAwaitingAnswer;
        UpdateAvatarState();
    }

    private void UpdateAvatarState()
    {
        var (text, color) = _vm.IsListening ? ("● Listening", Eve.Red)
            : _vm.IsTranscribing ? ("Recognising the speech", Eve.Amber)
            : _vm.IsAwaitingAnswer ? ("Processing", Eve.Amber)
            : _vm.IsSpeakingOrPreparing ? ("Speaking", Eve.Cyan)
            : ("Standby", Eve.Muted);
        _avatarState.Text = $"{(_vm.SelectedPreset?.Name ?? "Kaonomi").ToUpperInvariant()}  ·  {text.ToUpperInvariant()}";
        _avatarState.AddThemeColorOverride("font_color", color);
    }

    // ── Ticker ──

    private Control BuildTicker()
    {
        var band = new Control { CustomMinimumSize = new Vector2(0, 22), ClipContents = true, MouseFilter = MouseFilterEnum.Ignore };
        var back = new ColorRect { Color = Eve.Sunken(0.4f), MouseFilter = MouseFilterEnum.Ignore };
        back.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        band.AddChild(back);
        _ticker = new Label { MouseFilter = MouseFilterEnum.Ignore };
        _ticker.AddThemeFontSizeOverride("font_size", 12);
        _ticker.AddThemeColorOverride("font_color", Eve.Text);
        band.AddChild(_ticker);
        Bind.Visible(band, _vm, nameof(MainViewModel.HasTickerItems));
        void Fill() => _ticker.Text = string.Join("      ", _vm.TickerItems.Select(t => $"{(t.HasArrow ? t.Arrow + " " : string.Empty)}{t.Text}"));
        System.Collections.Specialized.NotifyCollectionChangedEventHandler changed = (_, _) => Bind.OnMain(() =>
        {
            if (IsInstanceValid(_ticker))
                Fill();
        });
        _vm.TickerItems.CollectionChanged += changed;
        Bind.OnGone(band, () => _vm.TickerItems.CollectionChanged -= changed);
        Fill();
        return band;
    }

    // ── Model, preset, history ──

    private Control BuildOptions()
    {
        var margin = Margins(12, 8, 12, 6);
        var column = new VBoxContainer();
        margin.AddChild(column);

        var actions = new HBoxContainer();
        var toggle = Eve.Button("⚙", tooltip: "Show or hide the model, preset and history");
        Bind.Check(toggle, _vm, nameof(MainViewModel.IsChatOptionsVisible));
        actions.AddChild(toggle);
        // The actions as icons, each named in its tooltip.
        var system = Eve.Button("📜", tooltip: "System prompt · show or hide the system prompt");
        Bind.Check(system, _vm, nameof(MainViewModel.IsSystemPromptVisible));
        actions.AddChild(system);
        var newChat = Eve.Button("💬", tooltip: "New chat · start a new conversation");
        Bind.Command(newChat, _vm.NewConversationCommand);
        actions.AddChild(newChat);
        // The prompts: the one being typed saved as JSON, one loaded from a JSON file, a whole collection run.
        var savePrompt = Eve.Button("💾", tooltip: $"Save prompt · save the prompt being typed as a JSON file ({PromptCollection.Pattern}: {{\"prompt\": …}})");
        savePrompt.Pressed += () => _ = SavePromptAsync();
        actions.AddChild(savePrompt);
        var loadPrompt = Eve.Button("📂", tooltip: $"Load prompt · load a prompt from a JSON file ({PromptCollection.Pattern}, or a text file) into the prompt box");
        loadPrompt.Pressed += () => _ = LoadPromptAsync();
        actions.AddChild(loadPrompt);
        var loadCollection = Eve.Button("📚", tooltip: "Load collection · read a folder of prompts (*.prompt.json; a folder ending in -FR, -EN… gives the language, the name before it the collection), count them, ask, then run them in the chat one after the other; each prompt and its completion are saved as JSON in the <model>-<collection> folder of the collection");
        loadCollection.Pressed += () => _ = PromptCollectionRunner.RunAsync(_vm);
        actions.AddChild(loadCollection);
        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        actions.AddChild(spacer);
        var note = Eve.Button("📝", tooltip: "Note · add a note about the chat (sent to the server with the conversation data)");
        note.Pressed += () => Tools.Data.NoteView.Open(Shell.Current, "Kaonomi Chat", _vm);
        actions.AddChild(note);
        actions.AddChild(BuildMenu());
        column.AddChild(actions);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 4);
        Bind.Visible(grid, _vm, nameof(MainViewModel.IsChatOptionsVisible));
        grid.AddChild(Eve.Caption("Model"));
        var models = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        Bind.Options(models, _vm.Models, _vm, nameof(MainViewModel.SelectedModel));
        grid.AddChild(models);
        grid.AddChild(Eve.Caption("Preset"));
        var presets = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true };
        // The presets read again when the store changes (a preset saved or deleted in /preset or /remote).
        Bind.Options(presets, Bind.Live(() => _vm.Presets), _vm, nameof(MainViewModel.SelectedPreset), p => p.Name, itemsProperty: nameof(MainViewModel.Presets));
        grid.AddChild(presets);
        grid.AddChild(Eve.Caption("History"));
        var history = new SpinBox { MinValue = 0, MaxValue = 20, Step = 1, SizeFlagsHorizontal = SizeFlags.ExpandFill, TooltipText = "Previous exchanges written into each prompt" };
        Bind.Number(history, _vm, nameof(MainViewModel.HistoryExchanges));
        grid.AddChild(history);
        column.AddChild(grid);
        return margin;
    }

    private MenuButton BuildMenu()
    {
        var menu = new MenuButton { Text = "⋯", TooltipText = "Export the conversation, saved prompts, prompt collection", Flat = false };
        var popup = menu.GetPopup();
        popup.AddItem("Export the conversation as JSON…", 0);
        popup.AddItem("Export the conversation as Word…", 1);
        popup.AddSeparator();
        popup.AddItem("Save the prompt being typed…", 2);
        popup.AddItem("Load a saved prompt…", 3);
        popup.AddSeparator();
        popup.AddItem("Run a prompt collection…", 4);
        popup.IdPressed += id =>
        {
            switch (id)
            {
                case 0: _ = ExportAsync("json", "JSON", (stream, export) => ChatExportService.WriteJsonAsync(stream, export)); break;
                case 1: _ = ExportAsync("docx", "Word document", (stream, export) => ChatExportService.WriteWordAsync(stream, export)); break;
                case 2: _ = SavePromptAsync(); break;
                case 3: _ = LoadPromptAsync(); break;
                case 4: _ = PromptCollectionRunner.RunAsync(_vm); break;
            }
        };
        return menu;
    }

    // ── System prompt ──

    private Control BuildSystemPrompt()
    {
        var margin = Margins(12, 0, 12, 6);
        var edit = new TextEdit
        {
            PlaceholderText = "Instructions given to the model before the conversation (optional)",
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            CustomMinimumSize = new Vector2(0, 90)
        };
        Bind.Edit(edit, _vm, nameof(MainViewModel.SystemPrompt));
        edit.TooltipText = "The system prompt of the chat, shared by every preset: it is added after the system prompt of the preset in use";
        // What the preset in use adds (its own system prompt, set in /preset).
        var preset = Ui.Label(_vm, nameof(MainViewModel.PresetPromptNote), Eve.Muted, 11, wrap: true);
        margin.AddChild(Ui.Column(4, edit, preset));
        Bind.Visible(margin, _vm, nameof(MainViewModel.IsSystemPromptVisible));
        return margin;
    }

    // ── Conversation ──

    private Control BuildConversation()
    {
        _scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var margin = Margins(10, 6, 10, 6);
        margin.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _scroll.AddChild(margin);
        _messages = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _messages.AddThemeConstantOverride("separation", 10);
        margin.AddChild(_messages);
        var empty = Eve.Text_("Type a message, hold 🎤 to speak, or a / command (/help).", Eve.Muted, 13, wrap: true);
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        Bind.Items(_messages, _vm.Messages, message => new MessageView(message, _vm), empty);
        _messages.AddChild(empty);
        // Follows the end of the conversation while it is at the end.
        var bar = _scroll.GetVScrollBar();
        bar.ValueChanged += _ => _stickToBottom = bar.Value >= bar.MaxValue - bar.Page - 40;
        bar.Changed += () =>
        {
            if (_stickToBottom)
                _scroll.ScrollVertical = (int)bar.MaxValue;
        };
        return _scroll;
    }

    // ── Input ──

    private Control BuildInput()
    {
        var panel = new PanelContainer();
        var box = Eve.Box(Eve.Header with { A = 0.7f }, Eve.Edge with { A = 0f }, 10, 0);
        box.BorderWidthTop = 1;
        box.BorderColor = Eve.Edge with { A = 0.6f };
        panel.AddThemeStyleboxOverride("panel", box);
        var column = new VBoxContainer();
        panel.AddChild(column);

        _input = new TextEdit
        {
            PlaceholderText = "Message · Enter: send · Shift+Enter: new line · ↑ ↓: previous prompts",
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            CustomMinimumSize = new Vector2(0, 70),
            ScrollFitContentHeight = false
        };
        Bind.Edit(_input, _vm, nameof(MainViewModel.Input));
        _input.GuiInput += OnInputKey;
        column.AddChild(_input);

        var row = new HBoxContainer();
        var language = new OptionButton { TooltipText = "Language of the voice input (and of the voice reading the answers)", CustomMinimumSize = new Vector2(64, 0) };
        Bind.Options(language, _vm.VoiceLanguages, _vm, nameof(MainViewModel.VoiceLanguage), l => l.ToUpperInvariant());
        row.AddChild(language);
        _mic = Eve.Button("🎤", tooltip: "Hold to speak, release: the speech is sent");
        _mic.CustomMinimumSize = new Vector2(48, 34);
        _mic.ButtonDown += () => _vm.StartListening();
        _mic.ButtonUp += () => _ = _vm.StopListeningAsync();
        Bind.When(_mic, _vm, () =>
        {
            var box2 = _vm.IsListening ? Eve.Box(Eve.Red with { A = 0.55f }, Eve.Red, 6)
                : _vm.IsTranscribing ? Eve.Box(Eve.Amber with { A = 0.4f }, Eve.Amber, 6)
                : null;
            if (box2 is null)
                _mic.RemoveThemeStyleboxOverride("normal");
            else
                _mic.AddThemeStyleboxOverride("normal", box2);
            if (box2 is not null)
                _mic.AddThemeStyleboxOverride("pressed", box2);
            else
                _mic.RemoveThemeStyleboxOverride("pressed");
        }, nameof(MainViewModel.IsListening), nameof(MainViewModel.IsTranscribing));
        row.AddChild(_mic);
        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        var stop = Eve.Button("■ STOP", tooltip: "Stop the request (and the voice)");
        stop.Pressed += () =>
        {
            Bind.Run(_vm.SendCancelCommand);
            _vm.StopSpeaking();
        };
        Bind.When(stop, _vm.SendCommand, () => stop.Disabled = !_vm.SendCommand.IsRunning, nameof(_vm.SendCommand.IsRunning));
        row.AddChild(stop);
        var send = Eve.Button("SEND ▶", primary: true, tooltip: "Send (Enter)");
        send.CustomMinimumSize = new Vector2(100, 34);
        Bind.Command(send, _vm.SendCommand);
        row.AddChild(send);
        column.AddChild(row);
        return panel;
    }

    private void OnInputKey(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } key)
            return;
        switch (key.Keycode)
        {
            case Key.Enter or Key.KpEnter when !key.ShiftPressed:
                _input.AcceptEvent();
                if (_vm.SkipStreaming())
                    return;
                Bind.Run(_vm.SendCommand);
                break;
            case Key.Up when _input.GetCaretLine() == 0 && _vm.RecallPreviousInput():
                _input.AcceptEvent();
                _input.CallDeferred(TextEdit.MethodName.SetCaretLine, _input.GetLineCount() - 1);
                break;
            case Key.Down when _input.GetCaretLine() == _input.GetLineCount() - 1 && _vm.RecallNextInput():
                _input.AcceptEvent();
                break;
            case Key.Escape when _vm.IsSpeakingOrPreparing:
                _input.AcceptEvent();
                _vm.StopSpeaking();
                break;
        }
    }

    /// <summary>Puts the cursor in the input.</summary>
    public void FocusInput()
    {
        if (IsInstanceValid(_input) && _input.IsInsideTree())
            _input.GrabFocus();
    }

    // ── Status ──

    private Control BuildStatus()
    {
        var margin = Margins(12, 4, 12, 6);
        _status = Eve.Text_("", Eve.Muted, 11);
        _status.ClipText = true;
        Bind.Text(_status, _vm, nameof(MainViewModel.Status));
        margin.AddChild(_status);
        return margin;
    }

    // ── Exports and prompts ──

    private async Task ExportAsync(string extension, string typeName, Func<Stream, ChatExport, Task> write)
    {
        if (_vm.HasNoMessages)
        {
            _vm.Status = "Nothing to export: the conversation is empty";
            return;
        }
        var path = await Dialogs.SaveFile($"Export the conversation ({typeName})", $"kaonomi-chat-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}", [$"*.{extension} ; {typeName}"]);
        if (path is null)
            return;
        try
        {
            var export = _vm.BuildExport();
            await using (var stream = File.Create(path))
                await write(stream, export);
            _vm.Status = $"Exported to {Path.GetFileName(path)}";
            ActivityLog.Info("Export", $"Conversation exported as {typeName} to {path} ({export.Messages.Count} message(s))");
        }
        catch (Exception ex)
        {
            _vm.Status = "Export failed: " + ex.Message;
            ActivityLog.Error("Export", $"{typeName} export to {path} failed", ex);
        }
    }

    private async Task SavePromptAsync()
    {
        var text = _vm.Input.Trim();
        if (text.Length == 0)
        {
            _vm.Status = "Type a prompt first, then save it";
            return;
        }
        Directory.CreateDirectory(_vm.PromptDirectory);
        var name = string.Concat(text.Split('\n')[0].Take(40).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim();
        var path = await Dialogs.SaveFile("Save the prompt", (name.Length == 0 ? $"prompt-{DateTime.Now:yyyyMMdd-HHmmss}" : name) + PromptCollection.Extension,
            [$"{PromptCollection.Pattern} ; Prompt"], _vm.PromptDirectory);
        if (path is null)
            return;
        var target = PromptCollection.WithExtension(path);
        await File.WriteAllTextAsync(target, PromptCollection.Serialize(text), new System.Text.UTF8Encoding(false));
        _vm.Status = $"Prompt saved: {Path.GetFileName(target)}";
    }

    private async Task LoadPromptAsync()
    {
        var path = await Dialogs.OpenFile("Load a prompt", [$"{PromptCollection.Pattern} ; Prompt", "*.txt, *.md ; Text"], _vm.PromptDirectory);
        if (path is null)
            return;
        var text = await File.ReadAllTextAsync(path);
        _vm.Input = PromptCollection.Parse(text) ?? text;
        _vm.Status = $"Prompt loaded: {Path.GetFileName(path)}";
        _input.GrabFocus();
    }

    private static MarginContainer Margins(int left, int top, int right, int bottom)
    {
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", left);
        margin.AddThemeConstantOverride("margin_top", top);
        margin.AddThemeConstantOverride("margin_right", right);
        margin.AddThemeConstantOverride("margin_bottom", bottom);
        return margin;
    }
}

/// <summary>A message of the conversation: its author and time, its text (Markdown for the answers), its actions.</summary>
public partial class MessageView : PanelContainer
{
    public MessageView(ChatMessageViewModel message, MainViewModel chat)
    {
        var color = message.IsUser ? Eve.Cyan : Eve.Amber;
        var box = Eve.Box(message.IsUser ? Eve.Cyan with { A = 0.07f } : Eve.Sunken(0.3f), Eve.Edge with { A = 0f }, 10, 0);
        box.BorderWidthLeft = 2;
        box.BorderColor = color with { A = 0.8f };
        AddThemeStyleboxOverride("panel", box);
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 4);
        AddChild(column);

        var header = new HBoxContainer();
        var author = Eve.Caption($"{message.Author}  ·  {message.Timestamp:HH:mm}", color, 10);
        author.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(author);
        var like = Small("👍", "Like this answer (evaluation)");
        var dislike = Small("👎", "Dislike this answer (evaluation)");
        like.Pressed += () => Evaluate(message, chat, false);
        dislike.Pressed += () => Evaluate(message, chat, true);
        Bind.When(like, message, () =>
        {
            like.Visible = dislike.Visible = message.CanEvaluate;
            like.Modulate = message.IsLiked ? Eve.Green : Colors.White;
            dislike.Modulate = message.IsDisliked ? Eve.Red : Colors.White;
        });
        header.AddChild(like);
        header.AddChild(dislike);
        var copy = Small("⧉", "Copy to the clipboard");
        copy.Pressed += () =>
        {
            Dialogs.Copy(message.Content);
            copy.Text = "✓";
            GetTree().CreateTimer(1.2).Timeout += () => copy.Text = "⧉";
        };
        Bind.Visible(copy, message, nameof(ChatMessageViewModel.IsPending), invert: true);
        header.AddChild(copy);
        column.AddChild(header);

        var body = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            SelectionEnabled = true,
            ContextMenuEnabled = true,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        body.AddThemeFontSizeOverride("normal_font_size", 13);
        body.AddThemeFontSizeOverride("mono_font_size", 12);
        body.AddThemeFontSizeOverride("bold_font_size", 13);
        // The text shown as plain text while the answer is revealed (the words appended, nothing parsed again), and
        // the version of the last formatting asked (the Markdown of a long answer is converted off the main thread).
        var shownPlain = (string?)null;
        var formatVersion = 0;
        string Format(string text) => message.IsError ? $"[color=#{Eve.Red.ToHtml(false)}]{Links.Bbcode(text)}[/color]"
            : message.IsUser ? Links.Bbcode(text)
            : MarkdownBbcode.Convert(LatexText.Convert(text));
        Bind.When(body, message, () =>
        {
            var text = message.Content;
            if (message.IsRevealing && !message.IsError && !message.IsUser)
            {
                // Revealed word by word: the new words are appended as they are (the Markdown comes at the end), the
                // label laying out the new piece only.
                if (shownPlain is not null && text.StartsWith(shownPlain, StringComparison.Ordinal))
                    body.AppendText(MarkdownBbcode.Escape(text[shownPlain.Length..]));
                else
                    body.Text = MarkdownBbcode.Escape(text);
                shownPlain = text;
                return;
            }
            shownPlain = null;
            var version = ++formatVersion;
            // A short text is formatted at once; a long one on a thread of the pool, the newest asked winning.
            if (text.Length < 3000)
            {
                body.Text = Format(text);
                return;
            }
            _ = System.Threading.Tasks.Task.Run(() => Format(text)).ContinueWith(done => Bind.OnMain(() =>
            {
                if (version == formatVersion && IsInstanceValid(body) && done.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
                    body.Text = done.Result;
            }));
        }, nameof(ChatMessageViewModel.Content), nameof(ChatMessageViewModel.IsError), nameof(ChatMessageViewModel.IsRevealing));
        column.AddChild(body);

        var pending = new ProgressBar { Indeterminate = true, CustomMinimumSize = new Vector2(0, 3), ShowPercentage = false };
        Bind.Visible(pending, message, nameof(ChatMessageViewModel.IsPending));
        column.AddChild(pending);

        var code = Eve.Button("", tooltip: "Open the code of the answer");
        code.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        Bind.Text(code, message, nameof(ChatMessageViewModel.ShowCodeLabel));
        Bind.Visible(code, message, nameof(ChatMessageViewModel.HasCode));
        code.Pressed += () => Bind.Run(chat.ShowCodeCommand, message);
        column.AddChild(code);

        var stats = Eve.Text_("", Eve.Muted, 10);
        Bind.Text(stats, message, nameof(ChatMessageViewModel.Stats));
        Bind.Visible(stats, message, nameof(ChatMessageViewModel.HasStats));
        column.AddChild(stats);
    }

    private static Button Small(string text, string tooltip)
    {
        var button = new Button { Text = text, Flat = true, TooltipText = tooltip, FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand };
        button.AddThemeFontSizeOverride("font_size", 11);
        return button;
    }

    private static void Evaluate(ChatMessageViewModel message, MainViewModel chat, bool negative)
    {
        var evaluation = chat.CreateEvaluation(message, negative);
        if (Shell.Current.Open(evaluation) is { } window)
            window.Closed += () =>
            {
                if (evaluation.Succeeded)
                    message.Feedback = !negative;
            };
    }
}
