using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Chat;

/// <summary>
/// The chat, on the right, bound to <see cref="MainViewModel"/> as in the Godot chat, without its 3D animation: the clock
/// and the state of the server (a double-click on the clock opens /alarm), the state of the assistant, the ticker, the
/// model, preset and history, the system prompt, the conversation (Markdown answers, thumbs, copy, code, stats), the
/// input with the push-to-talk microphone and its language, Send / Stop, and the menu of the exports and prompts.
/// </summary>
public sealed class ChatPanel : Border
{
    private readonly MainViewModel _vm;
    private readonly TextBlock _clock = Ui.Label("--:--:--", Ui.TextBright, 18);
    private readonly TextBlock _date = Ui.Caption("");
    private readonly TextBlock _state = Ui.Caption("Standby");
    private readonly ProgressBar _voice = new() { Minimum = 0, Maximum = 1, Height = 3, MinHeight = 3 };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    private readonly TextBox _input;
    private bool _stickToBottom = true;

    public ChatPanel(MainViewModel viewModel)
    {
        _vm = viewModel;
        Width = 470;
        Background = Ui.Panel;
        BorderBrush = Ui.Edge;
        BorderThickness = new Thickness(1, 0, 0, 0);
        _clock.FontFamily = Ui.Mono;
        _clock.HorizontalAlignment = HorizontalAlignment.Right;
        _date.HorizontalAlignment = HorizontalAlignment.Right;

        _input = new TextBox
        {
            PlaceholderText = "Message · Enter: send · Shift+Enter: new line · ↑ ↓: previous prompts",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 70,
            MaxHeight = 220
        };
        _input.Bind(TextBox.TextProperty, _vm, nameof(MainViewModel.Input), Avalonia.Data.BindingMode.TwoWay);
        _input.AddHandler(KeyDownEvent, OnInputKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,*,Auto,Auto") };
        void Add(Control c, int row)
        {
            Grid.SetRow(c, row);
            grid.Children.Add(c);
        }
        Add(BuildHeader(), 0);
        Add(BuildState(), 1);
        Add(BuildTicker(), 2);
        Add(BuildOptions(), 3);
        Add(BuildSystemPrompt(), 4);
        Add(BuildConversation(), 5);
        Add(BuildInput(), 6);
        Add(new Border { Padding = new Thickness(12, 4, 12, 6), Child = Ui.Bound(_vm, nameof(MainViewModel.Status), Ui.Muted, 11) }, 7);
        Child = grid;

        // The clock, the next alarm, the level of the voice: every quarter of a second.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        Tick();
    }

    private void Tick()
    {
        var now = DateTime.Now;
        _clock.Text = now.ToString("HH:mm:ss");
        var date = now.ToString("ddd d MMM yyyy").ToUpperInvariant();
        if (AlarmClock.Shared.Upcoming(now).FirstOrDefault() is { Alarm: not null } next && next.Ring - now < TimeSpan.FromDays(1))
            date += $"  ·  ⏰ {next.Ring:HH:mm}";
        _date.Text = date;
        _voice.Value = Math.Clamp(_vm.SpeechLevel, 0, 1);
    }

    // ── Header: title, server, clock ──

    private Control BuildHeader()
    {
        var server = Ui.Caption("Server · checking");
        Ui.When(server, _vm, () =>
        {
            server.Text = _vm.IsServerReachable ? "● SERVER ONLINE" : "● SERVER OFFLINE";
            server.Foreground = _vm.IsServerReachable ? Ui.Green : Ui.Red;
        }, nameof(MainViewModel.IsServerReachable));
        var titles = Ui.Column(0, Ui.Label("Kaonomi", Ui.TextBright, 15, bold: true), server);
        var time = Ui.Column(0, _clock, _date);
        time.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(time, "Double-click: the alarm clock (/alarm)");
        time.DoubleTapped += (_, _) => _vm.OpenTool("/alarm", false);
        var dock = new DockPanel();
        DockPanel.SetDock(time, Dock.Right);
        dock.Children.Add(time);
        dock.Children.Add(titles);
        return new Border { Child = dock, Background = Ui.Header, Padding = new Thickness(12, 6), BorderBrush = Ui.Edge, BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    // ── The 3D animation of the preset and the state of the assistant ──

    private readonly Panel _stage = new() { Height = 250, ClipToBounds = true, Background = Avalonia.Media.Brushes.Transparent };
    private Avatar.Avatar3D? _avatar;

    /// <summary>The avatar shown (in the chat, or in the voice mode while it is open).</summary>
    public Avatar.Avatar3D? CurrentAvatar => _avatar;

    /// <summary>The height of the 3D animation (250 in the chat on the right; most of the screen in the full chat).</summary>
    public double StageHeight
    {
        get => _stage.Height;
        set => _stage.Height = value;
    }

    /// <summary>Raised by a double-click on the animation: the voice mode.</summary>
    public event Action? VoiceModeRequested;

    private Control BuildState()
    {
        Ui.When(_state, _vm, UpdateState, nameof(MainViewModel.IsAwaitingAnswer), nameof(MainViewModel.IsListening),
            nameof(MainViewModel.IsTranscribing), nameof(MainViewModel.SelectedPreset));
        _vm.SpeakingChanged += speaking => Ui.OnMain(() =>
        {
            if (_avatar is not null)
                _avatar.Speaking = speaking;
            UpdateState();
        });
        // The mouth opens with the words of the answer as they come.
        _vm.WordStreamed += word => Ui.OnMain(() => _avatar?.Pulse(word.Length));
        Ui.When(_stage, _vm, ShowPresetAnimation, nameof(MainViewModel.SelectedPreset), nameof(MainViewModel.Animation));
        _voice.Foreground = Ui.Accent;
        _state.Margin = new Thickness(12, 0, 12, 8);
        _state.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
        var stage = new Panel { Children = { _stage, _state } };
        ToolTip.SetTip(stage, "Double-click: voice mode (the animation alone on the screen, a microphone)");
        stage.DoubleTapped += (_, _) => VoiceModeRequested?.Invoke();
        return Ui.Column(0, stage, _voice);
    }

    /// <summary>The animation of the preset in use (Kaonomi when it has none), made again when the preset changes.</summary>
    private void ShowPresetAnimation()
    {
        var animation = _vm.SelectedPreset?.Animation ?? _vm.Animation;
        if (animation == SceneAnimation.None)
            animation = SceneAnimation.Kaonomi;
        if (_avatar?.Animation == animation)
            return;
        _avatar = NewAvatar(animation);
        AvatarChanged?.Invoke(_avatar);
        if (!_lent)
        {
            _stage.Children.Clear();
            _stage.Children.Add(_avatar);
        }
    }

    /// <summary>Raised when the preset brings another animation (the voice mode shows it too).</summary>
    public event Action<Avatar.Avatar3D>? AvatarChanged;

    /// <summary>A new avatar of <paramref name="animation"/>, bound to the voice and the state of the chat.</summary>
    public Avatar.Avatar3D NewAvatar(SceneAnimation animation) => new(animation)
    {
        VoiceLevel = () => _vm.SpeechLevel,
        Speaking = _vm.IsSpeakingOrPreparing,
        Thinking = _vm.IsAwaitingAnswer
    };

    /// <summary>True while the voice mode shows the animation (the stage of the chat is empty then).</summary>
    private bool _lent;

    /// <summary>Takes the animation out of the chat (for the voice mode); <see cref="GiveBackAvatar"/> puts it back.</summary>
    public Avatar.Avatar3D? LendAvatar()
    {
        _lent = true;
        _stage.Children.Clear();
        return _avatar;
    }

    public void GiveBackAvatar()
    {
        _lent = false;
        _stage.Children.Clear();
        if (_avatar is not null)
            _stage.Children.Add(_avatar);
    }

    private void UpdateState()
    {
        var (text, color) = _vm.IsListening ? ("● Listening", Ui.Red)
            : _vm.IsTranscribing ? ("Recognising the speech", Ui.Warm)
            : _vm.IsAwaitingAnswer ? ("Processing", Ui.Warm)
            : _vm.IsSpeakingOrPreparing ? ("Speaking", Ui.Accent)
            : ("Standby", Ui.Muted);
        if (_avatar is not null)
            _avatar.Thinking = _vm.IsAwaitingAnswer;
        _state.Text = $"{(_vm.SelectedPreset?.Name ?? "Kaonomi").ToUpperInvariant()}  ·  {text.ToUpperInvariant()}";
        _state.Foreground = color;
    }

    // ── Ticker (still text, no scrolling) ──

    private Control BuildTicker()
    {
        var text = Ui.Label(string.Empty, Ui.Text, 12);
        void Fill() => text.Text = string.Join("   ·   ", _vm.TickerItems.Select(t => $"{(t.HasArrow ? t.Arrow + " " : string.Empty)}{t.Text}"));
        _vm.TickerItems.CollectionChanged += (_, _) => Ui.OnMain(Fill);
        Fill();
        var band = new Border { Child = text, Background = Ui.Field, Padding = new Thickness(12, 3) };
        ToolTip.SetTip(band, "The ticker (refreshed every minute)");
        return band.Visible(_vm, nameof(MainViewModel.HasTickerItems));
    }

    // ── Model, preset, history, actions ──

    private Control BuildOptions()
    {
        var toggle = new Avalonia.Controls.Primitives.ToggleButton { Content = "⚙", Padding = new Thickness(6, 2) };
        ToolTip.SetTip(toggle, "Show or hide the model, preset and history");
        toggle.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, _vm, nameof(MainViewModel.IsChatOptionsVisible), Avalonia.Data.BindingMode.TwoWay);
        var system = new Avalonia.Controls.Primitives.ToggleButton { Content = "📜", Padding = new Thickness(6, 2) };
        ToolTip.SetTip(system, "System prompt · show or hide the system prompt");
        system.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, _vm, nameof(MainViewModel.IsSystemPromptVisible), Avalonia.Data.BindingMode.TwoWay);
        var newChat = Ui.Command("💬", _vm.NewConversationCommand, tooltip: "New chat · start a new conversation");
        var save = Ui.Button("💾", () => _ = SavePromptAsync(), $"Save prompt · the prompt being typed as a JSON file ({PromptCollection.Pattern})");
        var load = Ui.Button("📂", () => _ = LoadPromptAsync(), "Load prompt · a prompt from a JSON or text file into the prompt box");
        var actions = Ui.Row(4, toggle, system, newChat, save, load);

        var menu = new Button { Content = "⋯" };
        ToolTip.SetTip(menu, "Export the conversation, saved prompts");
        menu.Flyout = new MenuFlyout
        {
            Items =
            {
                Item("Export the conversation as JSON…", () => _ = ExportAsync("json", "JSON", (s, e) => ChatExportService.WriteJsonAsync(s, e))),
                Item("Export the conversation as Word…", () => _ = ExportAsync("docx", "Word document", (s, e) => ChatExportService.WriteWordAsync(s, e))),
                new Separator(),
                Item("Save the prompt being typed…", () => _ = SavePromptAsync()),
                Item("Load a saved prompt…", () => _ = LoadPromptAsync())
            }
        };
        var top = new DockPanel();
        DockPanel.SetDock(menu, Dock.Right);
        top.Children.Add(menu);
        top.Children.Add(actions);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnSpacing = 10, RowSpacing = 4 };
        void Field(int row, string label, Control control)
        {
            var caption = Ui.Caption(label);
            Grid.SetRow(caption, row);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            grid.Children.Add(caption);
            grid.Children.Add(control);
        }
        Field(0, "Model", Ui.Combo(_vm.Models, _vm, nameof(MainViewModel.SelectedModel)));
        var presets = Ui.Combo(_vm.Presets, _vm, nameof(MainViewModel.SelectedPreset), p => (p as ChatPreset)?.Name ?? string.Empty);
        // The presets read again when the store changes (a preset saved or deleted in /preset or /remote).
        presets.Bind(ItemsControl.ItemsSourceProperty, _vm, nameof(MainViewModel.Presets));
        Field(1, "Preset", presets);
        var history = Ui.Combo(_vm.HistoryExchangeChoices, _vm, nameof(MainViewModel.HistoryExchanges));
        ToolTip.SetTip(history, "Previous exchanges written into each prompt");
        Field(2, "History", history);
        grid.Visible(_vm, nameof(MainViewModel.IsChatOptionsVisible));
        return new Border { Padding = new Thickness(12, 8, 12, 6), Child = Ui.Column(6, top, grid) };
    }

    private static MenuItem Item(string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        return item;
    }

    // ── System prompt ──

    private Control BuildSystemPrompt()
    {
        var edit = Ui.Edit(_vm, nameof(MainViewModel.SystemPrompt), "Instructions given to the model before the conversation (optional)", multiline: true, height: 90);
        ToolTip.SetTip(edit, "The system prompt of the chat, shared by every preset: added after the system prompt of the preset in use");
        var note = Ui.Bound(_vm, nameof(MainViewModel.PresetPromptNote), Ui.Muted, 11, wrap: true);
        return new Border { Padding = new Thickness(12, 0, 12, 6), Child = Ui.Column(4, edit, note) }.Visible(_vm, nameof(MainViewModel.IsSystemPromptVisible));
    }

    // ── Conversation ──

    private Control BuildConversation()
    {
        var empty = Ui.Label("Type a message, hold 🎤 to speak, or a / command (/help).", Ui.Muted, 13, wrap: true);
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.Margin = new Thickness(0, 20);
        empty.Visible(_vm, nameof(MainViewModel.HasNoMessages));
        var messages = new ItemsControl
        {
            ItemsSource = _vm.Messages,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ChatMessageViewModel>((message, _) => message is null ? new Panel() : new MessageView(message, _vm)),
            ItemsPanel = new Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new StackPanel { Spacing = 10 })
        };
        _scroll.Content = new StackPanel { Margin = new Thickness(10, 6), Children = { empty, messages } };
        // Follows the end of the conversation while it is at the end.
        _scroll.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta.Y != 0 && _stickToBottom)
                _scroll.ScrollToEnd();
            else if (e.OffsetDelta.Y != 0)
                _stickToBottom = _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 40;
        };
        return _scroll;
    }

    // ── Input ──

    private Control BuildInput()
    {
        var language = Ui.Combo(_vm.VoiceLanguages, _vm, nameof(MainViewModel.VoiceLanguage), l => (l as string ?? string.Empty).ToUpperInvariant());
        language.MinWidth = 70;
        language.HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip.SetTip(language, "Language of the voice input (and of the voice reading the answers)");
        var mic = new Button { Content = "🎤", Width = 48 };
        ToolTip.SetTip(mic, "Hold to speak, release: the speech is sent");
        mic.AddHandler(PointerPressedEvent, (_, _) => _vm.StartListening(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        mic.AddHandler(PointerReleasedEvent, (_, _) => _ = _vm.StopListeningAsync(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Ui.When(mic, _vm, () => mic.Background = _vm.IsListening ? Ui.Red : _vm.IsTranscribing ? Ui.Warm : null,
            nameof(MainViewModel.IsListening), nameof(MainViewModel.IsTranscribing));
        var stop = Ui.Button("■ STOP", () =>
        {
            Ui.Run(_vm.SendCancelCommand);
            _vm.StopSpeaking();
        }, "Stop the request (and the voice)");
        Ui.When(stop, _vm.SendCommand, () => stop.IsEnabled = _vm.SendCommand.IsRunning, nameof(_vm.SendCommand.IsRunning));
        var send = Ui.Command("SEND ▶", _vm.SendCommand, tooltip: "Send (Enter)", primary: true);
        send.MinWidth = 100;
        var right = Ui.Row(6, stop, send);
        var row = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(Ui.Row(6, language, mic));
        return new Border
        {
            Background = Ui.Header,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(10),
            Child = Ui.Column(8, _input, row)
        };
    }

    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                e.Handled = true;
                if (_vm.SkipStreaming())
                    return;
                _stickToBottom = true;
                Ui.Run(_vm.SendCommand);
                break;
            case Key.Up when !(_input.Text ?? string.Empty)[..Math.Min(_input.CaretIndex, (_input.Text ?? string.Empty).Length)].Contains('\n') && _vm.RecallPreviousInput():
                e.Handled = true;
                _input.CaretIndex = (_input.Text ?? string.Empty).Length;
                break;
            case Key.Down when !(_input.Text ?? string.Empty)[Math.Min(_input.CaretIndex, (_input.Text ?? string.Empty).Length)..].Contains('\n') && _vm.RecallNextInput():
                e.Handled = true;
                _input.CaretIndex = (_input.Text ?? string.Empty).Length;
                break;
            case Key.Escape when _vm.IsSpeakingOrPreparing:
                e.Handled = true;
                _vm.StopSpeaking();
                break;
        }
    }

    public void FocusInput() => _input.Focus();

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
        _input.Focus();
    }
}

/// <summary>A message of the conversation: its author and time, its text (Markdown for the answers), its actions.</summary>
public sealed class MessageView : Border
{
    public MessageView(ChatMessageViewModel message, MainViewModel chat)
    {
        var color = message.IsUser ? Ui.Accent : Ui.Warm;
        Background = message.IsUser ? new SolidColorBrush(Color.FromArgb(18, 0x5C, 0xC2, 0xE0)) : Ui.Field;
        BorderBrush = color;
        BorderThickness = new Thickness(2, 0, 0, 0);
        Padding = new Thickness(10, 8);

        var author = Ui.Caption($"{message.Author}  ·  {message.Timestamp:HH:mm}", color);
        var like = Small("👍", "Like this answer (evaluation)", () => Evaluate(message, chat, false));
        var dislike = Small("👎", "Dislike this answer (evaluation)", () => Evaluate(message, chat, true));
        Ui.When(like, message, () =>
        {
            like.IsVisible = dislike.IsVisible = message.CanEvaluate;
            like.Foreground = message.IsLiked ? Ui.Green : Ui.Text;
            dislike.Foreground = message.IsDisliked ? Ui.Red : Ui.Text;
        });
        Button copy = null!;
        copy = Small("⧉", "Copy to the clipboard", () =>
        {
            Dialogs.Copy(message.Content);
            copy.Content = "✓";
            DispatcherTimer.RunOnce(() => copy.Content = "⧉", TimeSpan.FromSeconds(1.2));
        });
        copy.Visible(message, nameof(ChatMessageViewModel.IsPending), invert: true);
        var header = new DockPanel();
        var buttons = Ui.Row(0, like, dislike, copy);
        DockPanel.SetDock(buttons, Dock.Right);
        header.Children.Add(buttons);
        header.Children.Add(author);

        var body = new ContentControl();
        // The answer revealed word by word is plain text; the Markdown is laid out once complete.
        Ui.When(body, message, () =>
        {
            var text = message.Content;
            if (message.IsError)
                body.Content = MarkdownView.Plain(text, 13, Ui.Red);
            else if (message.IsUser || message.IsRevealing || message.IsPending)
            {
                if (body.Content is SelectableTextBlock { Tag: "plain" } plain)
                    plain.Text = text;
                else
                    body.Content = new SelectableTextBlock { Text = text, Tag = "plain", FontSize = 13, Foreground = Ui.Text, TextWrapping = TextWrapping.Wrap };
            }
            else
                body.Content = MarkdownView.Build(LatexText.Convert(text));
        }, nameof(ChatMessageViewModel.Content), nameof(ChatMessageViewModel.IsError), nameof(ChatMessageViewModel.IsRevealing), nameof(ChatMessageViewModel.IsPending));

        var pending = new ProgressBar { IsIndeterminate = true, Height = 3, MinHeight = 3 }.Visible(message, nameof(ChatMessageViewModel.IsPending));
        var code = new Button { HorizontalAlignment = HorizontalAlignment.Left, Command = chat.ShowCodeCommand, CommandParameter = message }
            .Bind(ContentControl.ContentProperty, message, nameof(ChatMessageViewModel.ShowCodeLabel))
            .Visible(message, nameof(ChatMessageViewModel.HasCode));
        var stats = Ui.Bound(message, nameof(ChatMessageViewModel.Stats), Ui.Muted, 10, wrap: true).Visible(message, nameof(ChatMessageViewModel.HasStats));
        Child = Ui.Column(4, header, body, pending, code, stats);
    }

    private static Button Small(string text, string tooltip, Action click)
    {
        var button = new Button { Content = text, Classes = { "small" }, Focusable = false };
        ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => click();
        return button;
    }

    private static void Evaluate(ChatMessageViewModel message, MainViewModel chat, bool negative)
    {
        var evaluation = chat.CreateEvaluation(message, negative);
        // A thumb down on an answer of the default model: out of the cache of the gateway, and asked again to the
        // escalation model (while the popup asks what was wrong), as in the Godot chat.
        if (negative)
            _ = chat.RetryDislikedAsync(message);
        var window = Desk.Shell.Current.Open(evaluation);
        window.Closed += () =>
        {
            if (evaluation.Succeeded)
                message.Feedback = !negative;
        };
    }
}
