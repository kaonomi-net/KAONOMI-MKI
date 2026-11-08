using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Chat;

/// <summary>
/// The voice mode, as in the Godot chat: the 3D animation of the chat alone over the whole window, its state under it,
/// the push-to-talk 🎤 (hold, release: the speech is sent; or the space bar), STOP (the request and the voice) and LEAVE
/// (Escape); the animation goes back to the chat when it ends. The alarms of /alarm ring over it.
/// </summary>
public sealed class VoiceMode : Grid
{
    private readonly ChatPanel _chat;
    private readonly MainViewModel _vm;
    private readonly Panel _stage = new() { ClipToBounds = true };
    private readonly Action _closed;
    private bool _spaceDown;

    public VoiceMode(ChatPanel chat, MainViewModel vm, Action closed)
    {
        _chat = chat;
        _vm = vm;
        _closed = closed;
        Focusable = true;
        Background = Ui.Brush("Space");
        RowDefinitions = new RowDefinitions("*,Auto,Auto,Auto");

        if (_chat.LendAvatar() is { } avatar)
            Show(avatar);
        _chat.AvatarChanged += Show;
        Children.Add(_stage);

        var state = Ui.Bound(vm, nameof(MainViewModel.Status), Ui.Muted, 14);
        state.HorizontalAlignment = HorizontalAlignment.Center;
        var listening = Ui.Label("", Ui.Accent, 18, bold: true);
        listening.HorizontalAlignment = HorizontalAlignment.Center;
        Ui.When(listening, vm, () =>
        {
            (listening.Text, listening.Foreground) = vm.IsListening ? ("● LISTENING", Ui.Red)
                : vm.IsTranscribing ? ("RECOGNISING THE SPEECH", Ui.Warm)
                : vm.IsAwaitingAnswer ? ("PROCESSING", Ui.Warm)
                : vm.IsSpeakingOrPreparing ? ("SPEAKING", Ui.Accent)
                : ("HOLD 🎤 OR THE SPACE BAR TO SPEAK", Ui.Muted);
        }, nameof(MainViewModel.IsListening), nameof(MainViewModel.IsTranscribing), nameof(MainViewModel.IsAwaitingAnswer));
        vm.SpeakingChanged += _ => Ui.OnMain(() => listening.Text = vm.IsSpeakingOrPreparing ? "SPEAKING" : listening.Text);
        var info = Ui.Column(4, listening, state);
        info.Margin = new Thickness(0, 0, 0, 16);
        SetRow(info, 1);
        Children.Add(info);

        var mic = new Button { Content = "🎤", FontSize = 30, Width = 96, Height = 72, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(mic, "Hold to speak, release: the speech is sent (or the space bar)");
        mic.AddHandler(PointerPressedEvent, (_, _) => vm.StartListening(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        mic.AddHandler(PointerReleasedEvent, (_, _) => _ = vm.StopListeningAsync(), Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Ui.When(mic, vm, () => mic.Background = vm.IsListening ? Ui.Red : vm.IsTranscribing ? Ui.Warm : null,
            nameof(MainViewModel.IsListening), nameof(MainViewModel.IsTranscribing));
        var stop = Ui.Button("■ STOP", () =>
        {
            Ui.Run(vm.SendCancelCommand);
            vm.StopSpeaking();
        }, "Stop the request and the voice");
        var leave = Ui.Button("✕ LEAVE", Leave, "Back to the chat (Escape)");
        foreach (var button in new[] { stop, leave })
        {
            button.FontSize = 15;
            button.Padding = new Thickness(18, 10);
            button.VerticalAlignment = VerticalAlignment.Center;
        }
        var buttons = Ui.Row(18, stop, mic, leave);
        buttons.HorizontalAlignment = HorizontalAlignment.Center;
        buttons.Margin = new Thickness(0, 0, 0, 28);
        SetRow(buttons, 3);
        Children.Add(buttons);

        // A prompt or a command typed (the input of the chat: what is typed here is there too).
        var input = new TextBox
        {
            PlaceholderText = "Prompt or /command · Enter: send · ↑ ↓: previous prompts · Escape: clear, then leave",
            AcceptsReturn = false,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 16,
            MaxWidth = 760,
            MaxHeight = 140,
            Margin = new Thickness(24, 0, 24, 16)
        };
        input.Bind(TextBox.TextProperty, vm, nameof(MainViewModel.Input), Avalonia.Data.BindingMode.TwoWay);
        input.AddHandler(KeyDownEvent, (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Enter:
                    e.Handled = true;
                    if (!vm.SkipStreaming() && !string.IsNullOrWhiteSpace(input.Text))
                        Ui.Run(vm.SendCommand);
                    break;
                case Key.Up when vm.RecallPreviousInput():
                case Key.Down when vm.RecallNextInput():
                    e.Handled = true;
                    input.CaretIndex = (input.Text ?? string.Empty).Length;
                    break;
                case Key.Escape when !string.IsNullOrEmpty(input.Text):
                    e.Handled = true;
                    input.Text = string.Empty;
                    break;
                case Key.Escape:
                    e.Handled = true;
                    Leave();
                    break;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        SetRow(input, 2);
        Children.Add(input);

        KeyDown += (_, e) =>
        {
            if (e.Source is TextBox)
                return;
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Leave();
            }
            else if (e.Key == Key.Space && !_spaceDown)
            {
                e.Handled = true;
                _spaceDown = true;
                vm.StartListening();
            }
        };
        KeyUp += (_, e) =>
        {
            if (e.Key == Key.Space && _spaceDown && e.Source is not TextBox)
            {
                e.Handled = true;
                _spaceDown = false;
                _ = vm.StopListeningAsync();
            }
        };
        AttachedToVisualTree += (_, _) => Focus();
    }

    /// <summary>The animation shown, larger and closer than in the chat.</summary>
    private void Show(Avatar.Avatar3D avatar)
    {
        if (avatar.Parent is Panel parent && !ReferenceEquals(parent, _stage))
            parent.Children.Remove(avatar);
        _stage.Children.Clear();
        _stage.Children.Add(avatar);
    }

    /// <summary>Back to the chat: the animation goes back to its place.</summary>
    public void Leave()
    {
        _chat.AvatarChanged -= Show;
        _stage.Children.Clear();
        _chat.GiveBackAvatar();
        _closed();
    }
}
