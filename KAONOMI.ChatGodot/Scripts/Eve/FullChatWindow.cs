using System;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The chat as in its full screen, but in a window of the desktop (<c>/fullchat</c>): nothing but the 3D animation
/// of the preset in use (its own copy, following the voice, the words shown and the server at work like the one of
/// the chat) and, in its lower third, the microphone of the voice input. Hold the microphone, speak, release: the
/// speech is sent as the prompt and the answer is read aloud; the disc turns red while it records and amber while the
/// speech is recognised, an arc turns around it while the answer is awaited, and a line under it tells what the voice
/// input is doing. No lock of the screen: the window moves, resizes and closes like the others; ⧉ in its header moves it
/// into a window of the system, to put on another screen (closing that window brings it back to the desktop).
/// </summary>
public partial class FullChatWindow : Control
{
    public const string Key = "fullchat";

    /// <summary>The full chats open (on the desktop or in a window of the system): the chat hides its own animation while one is.</summary>
    public static int OpenCount { get; private set; }

    /// <summary>Raised when a full chat opens or closes.</summary>
    public static event Action? OpenChanged;

    private readonly MainViewModel _vm;
    private Control? _avatarView;
    private IAvatar? _avatar;
    private SceneAnimation? _shownAnimation;
    private Button _mic = null!;
    private Ring _ring = null!;
    private Label _status = null!;

    public FullChatWindow(MainViewModel viewModel)
    {
        _vm = viewModel;
        MouseFilter = MouseFilterEnum.Stop;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        SizeFlagsVertical = SizeFlags.ExpandFill;
    }

    public override void _Ready()
    {
        var back = new ColorRect { Color = Eve.Space, MouseFilter = MouseFilterEnum.Ignore };
        back.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(back);
        _ring = new Ring { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_ring);
        _mic = new Button { Text = "🎤", CustomMinimumSize = new Vector2(104, 104), FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = "Hold to speak, release: the speech is sent and the answer is read aloud" };
        _mic.AddThemeFontSizeOverride("font_size", 42);
        _mic.ButtonDown += () => _vm.StartListening();
        _mic.ButtonUp += () => _ = _vm.StopListeningAsync();
        AddChild(_mic);
        _status = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = MouseFilterEnum.Ignore };
        _status.AddThemeFontSizeOverride("font_size", 14);
        _status.AddThemeColorOverride("font_color", Eve.TextBright with { A = 0.9f });
        AddChild(_status);
        Bind.Text(_status, _vm, nameof(MainViewModel.Status));
        Bind.When(_mic, _vm, Restyle, nameof(MainViewModel.IsListening), nameof(MainViewModel.IsTranscribing), nameof(MainViewModel.IsAwaitingAnswer));

        // The animation follows the chat: the words shown, the voice, the server at work, the preset chosen.
        Action<string> pulse = word => Bind.OnMain(() => { if (IsInstanceValid(this)) _avatar?.Pulse(word.Length); });
        Action<bool> speakingChanged = speaking => Bind.OnMain(() =>
        {
            if (IsInstanceValid(this) && _avatar is not null)
                _avatar.Speaking = speaking;
        });
        _vm.WordStreamed += pulse;
        _vm.SpeakingChanged += speakingChanged;
        // Open until closed (moved into a window of the system and back, it stays open).
        OpenCount++;
        OpenChanged?.Invoke();
        Bind.OnGone(this, () =>
        {
            _vm.WordStreamed -= pulse;
            _vm.SpeakingChanged -= speakingChanged;
            OpenCount = Math.Max(0, OpenCount - 1);
            OpenChanged?.Invoke();
        });
        Bind.When(this, _vm, () =>
        {
            if (_avatar is not null)
                _avatar.Thinking = _vm.IsAwaitingAnswer;
        }, nameof(MainViewModel.IsAwaitingAnswer));
        Bind.When(this, _vm, ShowPresetAnimation, nameof(MainViewModel.SelectedPreset), nameof(MainViewModel.Animation));
        Resized += Layout;
        Layout();
    }

    /// <summary>The animation of the preset in use (its own copy), under the microphone.</summary>
    private void ShowPresetAnimation()
    {
        var animation = _vm.SelectedPreset?.Animation ?? _vm.Animation;
        if (animation == SceneAnimation.None)
            animation = SceneAnimation.Kaonomi;
        if (_shownAnimation == animation)
            return;
        _shownAnimation = animation;
        if (_avatarView is { } old && IsInstanceValid(old))
            old.QueueFree();
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
        view.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(view);
        // Behind the ring, the microphone and the status (after the background).
        MoveChild(view, 1);
        _avatarView = view;
        _avatar.VoiceLevel = () => _vm.SpeechLevel;
        _avatar.Thinking = _vm.IsAwaitingAnswer;
        _avatar.Speaking = _vm.IsSpeakingOrPreparing;
    }

    private void Layout()
    {
        // The microphone in the middle of the lower third, the ring around it, the status under it.
        var center = new Vector2(Size.X / 2, Size.Y * 5 / 6);
        _mic.Position = center - _mic.CustomMinimumSize / 2;
        _mic.Size = _mic.CustomMinimumSize;
        _ring.Position = center - new Vector2(90, 90);
        _ring.Size = new Vector2(180, 180);
        _status.Position = new Vector2(24, Math.Min(center.Y + 60, Size.Y - 50));
        _status.Size = new Vector2(Math.Max(0, Size.X - 48), 44);
    }

    private void Restyle()
    {
        var (fill, edge) = _vm.IsListening ? (Eve.Red, Eve.Red)
            : _vm.IsTranscribing ? (Eve.Amber, Eve.Amber)
            : (new Color(1, 1, 1, 0.9f), Eve.EdgeBright);
        StyleBoxFlat Disc(float alpha)
        {
            var box = Eve.Box(fill with { A = fill.A * alpha }, edge, 0, 2);
            box.SetCornerRadiusAll(52);
            box.ShadowColor = new Color(0, 0, 0, 0.5f);
            box.ShadowSize = 16;
            return box;
        }
        _mic.AddThemeStyleboxOverride("normal", Disc(0.85f));
        _mic.AddThemeStyleboxOverride("hover", Disc(1f));
        _mic.AddThemeStyleboxOverride("pressed", Disc(1f));
        _ring.Visible = _vm.IsAwaitingAnswer;
    }

    /// <summary>The waiting animation: an arc turning around the microphone, a halo breathing behind it.</summary>
    private partial class Ring : Control
    {
        private double _time;

        public override void _Process(double delta)
        {
            if (!Visible)
                return;
            _time += delta;
            QueueRedraw();
        }

        public override void _Draw()
        {
            var center = Size / 2;
            var breath = (float)(0.5 + 0.5 * Math.Sin(_time * 4));
            DrawCircle(center, 70 + breath * 14, Eve.Cyan with { A = 0.1f + 0.1f * (1 - breath) });
            DrawArc(center, 68, 0, Mathf.Tau, 64, Eve.Cyan with { A = 0.25f }, 4, true);
            var start = (float)(_time * Mathf.Tau / 1.1);
            DrawArc(center, 68, start, start + 1.9f, 32, Eve.Cyan, 4, true);
        }
    }
}
