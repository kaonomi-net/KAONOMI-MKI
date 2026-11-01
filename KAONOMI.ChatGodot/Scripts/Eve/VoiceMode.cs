using System;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The voice mode of the chat (a double-click on its 3D animation): the screen holds nothing but the animation and a
/// microphone in its lower third. Hold the microphone (or the V key), speak, release: the speech is sent as the prompt and the answer is
/// read aloud. The disc turns red while it records and amber while the speech is recognised; an arc turns around it
/// while the answer is awaited; a line under it tells what the voice input is doing. The full screen is locked
/// (<see cref="KioskLock"/>: no Cmd+Tab, no Ctrl+Tab): a double-click on the animation or Esc brings the chat back.
/// </summary>
public partial class VoiceMode : Control
{
    private readonly MainViewModel _vm;
    private Control _avatar;
    private readonly Action<Control> _leave;
    private Button _mic = null!;
    private Ring _ring = null!;
    private Label _status = null!;

    /// <summary>
    /// The voice mode showing <paramref name="avatar"/>; <paramref name="leave"/> gets the view of the animation shown
    /// when it ends (a preset changed meanwhile has replaced it), to put it back in the chat.
    /// </summary>
    public VoiceMode(MainViewModel viewModel, Control avatar, Action<Control> leave)
    {
        _vm = viewModel;
        _avatar = avatar;
        _leave = leave;
        MouseFilter = MouseFilterEnum.Stop;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var back = new ColorRect { Color = Eve.Space, MouseFilter = MouseFilterEnum.Ignore };
        back.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(back);
        _avatar.Reparent(this, false);
        _avatar.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _ring = new Ring { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_ring);
        _mic = new Button { Text = "🎤", CustomMinimumSize = new Vector2(104, 104), FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = "Hold to speak (or hold the V key), release: the answer is read aloud (double-click the animation or Esc to leave)" };
        _mic.AddThemeFontSizeOverride("font_size", 42);
        _mic.ButtonDown += () => _vm.StartListening();
        _mic.ButtonUp += () => _ = _vm.StopListeningAsync();
        AddChild(_mic);
        _status = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = MouseFilterEnum.Ignore };
        _status.AddThemeFontSizeOverride("font_size", 15);
        _status.AddThemeColorOverride("font_color", Eve.TextBright with { A = 0.9f });
        AddChild(_status);
        Bind.Text(_status, _vm, nameof(MainViewModel.Status));
        Bind.When(_mic, _vm, Restyle, nameof(MainViewModel.IsListening), nameof(MainViewModel.IsTranscribing), nameof(MainViewModel.IsAwaitingAnswer));
        Resized += Layout;
        Layout();

        // The keyboard leaves the input of the chat (V is for the microphone here).
        GetViewport().GuiReleaseFocus();
        // The window over the whole screen, locked.
        KioskLock.Engage();
    }

    public override void _ExitTree() => KioskLock.Release();

    private void Layout()
    {
        // The microphone in the middle of the lower third, the ring around it, the status under it.
        var center = new Vector2(Size.X / 2, Size.Y * 5 / 6);
        _mic.Position = center - _mic.CustomMinimumSize / 2;
        _mic.Size = _mic.CustomMinimumSize;
        _ring.Position = center - new Vector2(90, 90);
        _ring.Size = new Vector2(180, 180);
        _status.Position = new Vector2(24, center.Y + 100);
        _status.Size = new Vector2(Size.X - 48, 60);
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

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { DoubleClick: true, ButtonIndex: MouseButton.Left })
        {
            AcceptEvent();
            Leave();
        }
    }

    /// <summary>True while the V key is held (the microphone records).</summary>
    private bool _holdingV;

    public override void _Input(InputEvent e)
    {
        // Hold V to speak (like holding the microphone), release it to send: before the controls, so that the V never
        // reaches the input of the chat behind.
        if (e is InputEventKey { Pressed: true, Keycode: Key.Tab, CtrlPressed: true })
        {
            // Ctrl+Tab brings no window in front while the full screen is locked.
            GetViewport().SetInputAsHandled();
            return;
        }
        if (e is not InputEventKey { Keycode: Key.V } key || key.CtrlPressed || key.MetaPressed || key.AltPressed)
            return;
        GetViewport().SetInputAsHandled();
        if (key.Pressed && !_holdingV)
        {
            _holdingV = true;
            _vm.StartListening();
        }
        else if (!key.Pressed && _holdingV)
        {
            _holdingV = false;
            _ = _vm.StopListeningAsync();
        }
    }

    public override void _UnhandledKeyInput(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape })
        {
            if (_vm.IsSpeakingOrPreparing)
                _vm.StopSpeaking();
            else
                Leave();
            GetViewport().SetInputAsHandled();
        }
    }

    /// <summary>
    /// Shows <paramref name="avatar"/> (the animation of another preset) instead of the one shown, at its place (under
    /// the microphone and what is above the animation); the one shown is freed.
    /// </summary>
    public void SwapAvatar(Control avatar)
    {
        var old = _avatar;
        _avatar = avatar;
        if (!IsInsideTree())
            return;
        var index = IsInstanceValid(old) && old.GetParent() == this ? old.GetIndex() : 1;
        if (avatar.GetParent() is not null)
            avatar.Reparent(this, false);
        else
            AddChild(avatar);
        avatar.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MoveChild(avatar, index);
        if (IsInstanceValid(old) && !ReferenceEquals(old, avatar))
            old.QueueFree();
    }

    /// <summary>
    /// Leaves the voice mode (a double-click on the animation, Esc, and FULLSCREEN:OFF of a script of /remote call it):
    /// the lock off, the window of the chat back to how it was, the animation back in the chat.
    /// </summary>
    public void Leave()
    {
        if (IsQueuedForDeletion())
            return;
        KioskLock.Release();
        _leave(_avatar);
        QueueFree();
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
