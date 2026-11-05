using System;
using System.Linq;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The voice mode of the chat (a double-click on its 3D animation): the screen holds the animation, a microphone in its
/// lower third, a line to type a command or a prompt above it, and ⏬ in the middle of the top (every window small at
/// the bottom). Hold the microphone (or the V key), speak, release: the speech is sent as the prompt and the answer is
/// read aloud. The disc turns red while it records and amber while the speech is recognised; an arc turns around it
/// while the answer is awaited; a line under it tells what the voice input is doing. The desktop comes along as a stage
/// (<see cref="WindowDeck.EnterStage"/>): a window opened is shown in the middle on 90% of the screen, one at a time,
/// ◀ ▶ going from one to the other; the line and the microphone then move into the margin under it. The full screen is
/// locked (<see cref="KioskLock"/>: no Cmd+Tab, no Ctrl+Tab): a double-click on the animation or Esc brings the chat back.
/// </summary>
public partial class VoiceMode : Control
{
    private readonly MainViewModel _vm;
    private Control _avatar;
    private readonly Action<Control> _leave;
    private Button _mic = null!;
    private Ring _ring = null!;
    private Label _status = null!;
    private LineEdit _input = null!;
    private Button _send = null!;
    private Button _shelfButton = null!;

    /// <summary>The desktop, taken from its place in the interface while the voice mode is shown (null: none).</summary>
    private WindowDeck? _deck;
    private Node? _deckHome;
    private int _deckIndex;

    /// <summary>True while a window is shown on the stage: the line and the microphone are in the margin under it.</summary>
    private bool _compact;
    private double _refresh;

    /// <summary>The size of ⏬ at the top.</summary>
    private const int ToolSize = 44;

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
        TakeDeck();

        _ring = new Ring { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_ring);
        _mic = new Button { Text = "🎤", FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = "Hold to speak (or hold the V key), release: the answer is read aloud (double-click the animation or Esc to leave)" };
        _mic.AddThemeFontSizeOverride("font_size", 42);
        _mic.ButtonDown += () => _vm.StartListening();
        _mic.ButtonUp += () => _ = _vm.StopListeningAsync();
        AddChild(_mic);
        _status = new Label { HorizontalAlignment = HorizontalAlignment.Center, AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = MouseFilterEnum.Ignore };
        _status.AddThemeFontSizeOverride("font_size", 15);
        _status.AddThemeColorOverride("font_color", Eve.TextBright with { A = 0.9f });
        AddChild(_status);
        Bind.Text(_status, _vm, nameof(MainViewModel.Status));
        BuildInput();
        BuildShelfButton();
        Bind.When(_mic, _vm, Restyle, nameof(MainViewModel.IsListening), nameof(MainViewModel.IsTranscribing), nameof(MainViewModel.IsAwaitingAnswer));
        Resized += Layout;
        Layout();

        // The keyboard leaves the input of the chat (V is for the microphone here).
        GetViewport().GuiReleaseFocus();
        // The window over the whole screen, locked.
        KioskLock.Engage();
    }

    public override void _ExitTree() => KioskLock.Release();

    /// <summary>Where the line, the microphone and the status are (the ring and ➤ follow the microphone and the line).</summary>
    private readonly record struct Placement(Rect2 Input, Rect2 Mic, Rect2 Status, float MicFont);

    /// <summary>The placement shown now, and the move from one placement to the other (1 s).</summary>
    private Placement _placement;
    private Tween? _move;

    /// <summary>How long the line and the microphone take to go down into the margin, or to come back up.</summary>
    private const double MoveDuration = 1.0;

    /// <summary>The placement of the line, the microphone and the status for <see cref="_compact"/>.</summary>
    private Placement Target()
    {
        var band = Size.Y * 0.05f;
        if (_compact)
        {
            // A window on the stage: the line and a smaller microphone side by side in the margin under it, the status
            // on their right.
            const float micSize = 48;
            var input = new Vector2(520, 46);
            var center = new Vector2(Size.X / 2 + 280, Size.Y - band / 2);
            var status = new Vector2(center.X + micSize / 2 + 16, center.Y - 11);
            return new Placement(
                new Rect2(new Vector2(center.X - micSize / 2 - 14 - input.X, center.Y - input.Y / 2), input),
                new Rect2(center - new Vector2(micSize, micSize) / 2, new Vector2(micSize, micSize)),
                new Rect2(status, new Vector2(Math.Max(40, Size.X - status.X - 24), 22)),
                20);
        }
        else
        {
            // The microphone in the middle of the lower third, the ring around it, the line above it, the status under it.
            const float micSize = 96;
            var input = new Vector2(640, 56);
            var center = new Vector2(Size.X / 2, Size.Y * 5 / 6);
            return new Placement(
                new Rect2(new Vector2(center.X - input.X / 2, center.Y - 90 - 18 - input.Y), input),
                new Rect2(center - new Vector2(micSize, micSize) / 2, new Vector2(micSize, micSize)),
                new Rect2(new Vector2(24, center.Y + 100), new Vector2(Size.X - 48, 60)),
                38);
        }
    }

    /// <summary>
    /// Puts the line, the microphone and the status at their place; <paramref name="animate"/>: they glide there in
    /// <see cref="MoveDuration"/> (a window opened on the stage, or the last one gone), with the sound of the move.
    /// </summary>
    private void Layout(bool animate)
    {
        PlaceShelfButton();

        var target = Target();
        _move?.Kill();
        _move = null;
        if (!animate || _placement.Input.Size == Vector2.Zero)
        {
            Place(target, target, 1);
            return;
        }
        var from = _placement;
        PlayMoveSound(down: _compact);
        _move = CreateTween().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
        _move.TweenMethod(Callable.From<float>(t => Place(from, target, t)), 0f, 1f, MoveDuration);
        _move.TweenCallback(Callable.From(() => _move = null));
    }

    private void Layout() => Layout(animate: false);

    /// <summary>The line, the microphone and the status at <paramref name="t"/> of the way from <paramref name="a"/> to <paramref name="b"/>.</summary>
    private void Place(Placement a, Placement b, float t)
    {
        static Rect2 Between(Rect2 x, Rect2 y, float t) => new(x.Position.Lerp(y.Position, t), x.Size.Lerp(y.Size, t));
        var now = new Placement(Between(a.Input, b.Input, t), Between(a.Mic, b.Mic, t), Between(a.Status, b.Status, t), Mathf.Lerp(a.MicFont, b.MicFont, t));
        _placement = now;

        _input.Position = now.Input.Position;
        _input.Size = now.Input.Size;
        // ➤ inside the right end of the line.
        var send = now.Input.Size.Y - 12;
        _send.Size = new Vector2(send, send);
        _send.Position = now.Input.Position + new Vector2(now.Input.Size.X - send - 6, 6);
        StyleInput();

        _mic.Position = now.Mic.Position;
        _mic.Size = now.Mic.Size;
        _mic.AddThemeFontSizeOverride("font_size", (int)Math.Round(now.MicFont));
        var ring = now.Mic.Size.X * 1.73f;
        _ring.Position = now.Mic.GetCenter() - new Vector2(ring, ring) / 2;
        _ring.Size = new Vector2(ring, ring);
        Restyle();

        // The status changes its wrapping halfway, where it is the least seen.
        _status.Position = now.Status.Position;
        _status.Size = now.Status.Size;
        var compact = (t < 0.5f ? a : b).Mic.Size.X < 72;
        _status.AutowrapMode = compact ? TextServer.AutowrapMode.Off : TextServer.AutowrapMode.WordSmart;
        _status.ClipText = compact;
        _status.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Center;
    }

    // ── The sound of the move ──

    private const int SoundRate = 44100;
    private static AudioStreamWav? _downSound, _upSound;
    private AudioStreamPlayer? _movePlayer;

    /// <summary>The whoosh of the move: falling as the line and the microphone go down, rising as they come back up.</summary>
    private void PlayMoveSound(bool down)
    {
        if (_movePlayer is null)
        {
            _movePlayer = new AudioStreamPlayer { VolumeDb = -9 };
            AddChild(_movePlayer);
        }
        _movePlayer.Stream = down ? _downSound ??= MakeWhoosh(true) : _upSound ??= MakeWhoosh(false);
        _movePlayer.Play();
    }

    /// <summary>
    /// A soft whoosh of <see cref="MoveDuration"/>: breath (noise through a resonant band-pass whose pitch sweeps down
    /// or up) and a quiet glide of a tone under it, swelling then fading. Made by the code, no file.
    /// </summary>
    private static AudioStreamWav MakeWhoosh(bool down)
    {
        var random = new Random(down ? 11 : 12);
        var frames = (int)(MoveDuration * SoundRate);
        var samples = new float[frames];
        // A state-variable band-pass filter on the noise.
        double low = 0, band = 0, phase = 0;
        for (var n = 0; n < frames; n++)
        {
            var x = n / (double)frames;
            var sweep = down ? 1 - x : x;
            var cutoff = 350 * Math.Pow(1800 / 350.0, sweep);
            var f = 2 * Math.Sin(Math.PI * cutoff / SoundRate);
            const double damping = 0.35;
            var noise = random.NextDouble() * 2 - 1;
            var high = noise - low - damping * band;
            band += f * high;
            low += f * band;
            // A swell: up quickly, down slowly.
            var envelope = Math.Pow(Math.Sin(Math.PI * Math.Pow(x, 0.7)), 2);
            var tone = 520 * Math.Pow(0.5, down ? x : 1 - x);
            phase += Math.Tau * tone / SoundRate;
            samples[n] = (float)((band * 0.8 + Math.Sin(phase) * 0.12) * envelope);
        }
        var peak = 0f;
        foreach (var v in samples)
            peak = Math.Max(peak, Math.Abs(v));
        var gain = peak > 0 ? 0.8f / peak : 1;
        var data = new byte[frames * 2];
        for (var n = 0; n < frames; n++)
        {
            var v = (short)(samples[n] * gain * 32000);
            data[n * 2] = (byte)v;
            data[n * 2 + 1] = (byte)(v >> 8);
        }
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = SoundRate, Data = data };
    }

    private void Restyle()
    {
        // A surface disc with a ring of the accent at rest; filled red while it records, amber while it recognises.
        var radius = (int)_mic.Size.X / 2;
        var ring = _compact ? 2 : 3;
        if (_vm.IsListening || _vm.IsTranscribing)
        {
            var fill = _vm.IsListening ? Eve.Red : Eve.Amber;
            var lit = Glass(radius, fill, fill.Lightened(0.25f), ring, 0.9f);
            _mic.AddThemeStyleboxOverride("normal", lit);
            _mic.AddThemeStyleboxOverride("hover", lit);
            _mic.AddThemeStyleboxOverride("pressed", lit);
        }
        else
        {
            _mic.AddThemeStyleboxOverride("normal", Glass(radius, Surface, Eve.Cyan with { A = 0.85f }, ring, 1f));
            _mic.AddThemeStyleboxOverride("hover", Glass(radius, SurfaceHover, Eve.Cyan, ring, 1.2f));
            _mic.AddThemeStyleboxOverride("pressed", Glass(radius, Eve.Cyan with { A = 0.25f }, Eve.Cyan, ring, 0.6f));
        }
        _ring.Visible = _vm.IsAwaitingAnswer;
    }

    public override void _Process(double delta)
    {
        // At once: a window shown on the stage (or none any more) sends the line and the microphone down (or up).
        var deck = _deck is not null && IsInstanceValid(_deck) ? _deck : null;
        var compact = deck?.ShowsOnStage ?? false;
        if (compact != _compact)
        {
            _compact = compact;
            Layout(animate: true);
        }
        // A few times a second: ⏬ (lit while the windows are small at the bottom, off without windows).
        _refresh -= delta;
        if (_refresh > 0)
            return;
        _refresh = 0.4;
        StyleShelfButton(deck?.IsShelved ?? false);
        _shelfButton.Disabled = deck is null || deck.Windows.Count == 0;
    }

    // ── The desktop as a stage ──

    /// <summary>The desktop of the interface into the voice mode, over the animation, as a stage.</summary>
    private void TakeDeck()
    {
        if (Shell.Current?.Deck is not { } deck || !IsInstanceValid(deck) || deck.GetParent() is not { } home)
            return;
        _deck = deck;
        _deckHome = home;
        // The window on the stage under ⏬ (itself under the camera of the screen, when it has one).
        deck.StageTop = ShelfButtonTop().Stage;
        _deckIndex = deck.GetIndex();
        deck.Reparent(this, false);
        MoveChild(deck, _avatar.GetIndex() + 1);
        deck.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        deck.EnterStage();
    }

    /// <summary>The desktop back at its place in the interface, as it was.</summary>
    private void GiveDeckBack()
    {
        if (_deck is not { } deck || !IsInstanceValid(deck))
            return;
        _deck = null;
        deck.StageTop = 0;
        deck.LeaveStage();
        if (_deckHome is not { } home || !IsInstanceValid(home))
            return;
        deck.SetAnchorsPreset(LayoutPreset.TopLeft);
        deck.Reparent(home, false);
        home.MoveChild(deck, Math.Min(_deckIndex, home.GetChildCount() - 1));
    }

    /// <summary>True while the keyboard is in the line or in the window on the stage (the V key is then a letter).</summary>
    private bool Typing() =>
        GetViewport().GuiGetFocusOwner() is not null
        || _deck is not null && IsInstanceValid(_deck) && _deck.Windows.Any(w => w.IsFront && w.Container.Visible && w.Content.GetViewport()?.GuiGetFocusOwner() is not null);

    // ── The line and the tools ──

    private void BuildInput()
    {
        _input = new LineEdit
        {
            PlaceholderText = "Ask, or type a command (/work, /relation…)",
            ClearButtonEnabled = false,
            TooltipText = "Typed here as in the chat: a command or a prompt (Enter or ➤ sends it, Esc leaves the line)"
        };
        _input.AddThemeFontSizeOverride("font_size", 17);
        _input.TextSubmitted += Submit;
        AddChild(_input);

        _send = new Button { Text = "➤", FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand, TooltipText = "Send" };
        _send.AddThemeFontSizeOverride("font_size", 17);
        foreach (var state in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_disabled_color" })
            _send.AddThemeColorOverride(state, Colors.White);
        _send.Pressed += () => Submit(_input.Text);
        AddChild(_send);
        // ➤ lit only while there is something to send.
        _input.TextChanged += _ => StyleSend();
        StyleSend();
    }

    private void Submit(string text)
    {
        var typed = text.Trim();
        if (typed.Length == 0)
            return;
        _input.Clear();
        StyleSend();
        _vm.RunCommand(typed);
    }

    /// <summary>The line as a pill: a surface with a soft shadow, a ring of the accent while it has the keyboard.</summary>
    private void StyleInput()
    {
        var radius = (int)_input.Size.Y / 2;
        StyleBoxFlat Pill(Color edge, int border, float shadow)
        {
            var box = Glass(radius, Surface, edge, border, shadow);
            box.ContentMarginLeft = 24;
            box.ContentMarginRight = _input.Size.Y + 4;
            return box;
        }
        _input.AddThemeStyleboxOverride("normal", Pill(Hairline, 1, 1f));
        _input.AddThemeStyleboxOverride("focus", Pill(Eve.Cyan, 2, 1.3f));
        _input.AddThemeColorOverride("font_color", Ink);
        _input.AddThemeColorOverride("font_placeholder_color", Ink with { A = 0.42f });
        _input.AddThemeColorOverride("caret_color", Eve.Cyan);
        _input.AddThemeColorOverride("selection_color", Eve.Cyan with { A = 0.3f });
        StyleSend();
    }

    private void StyleSend()
    {
        if (_send is null || _input is null)
            return;
        var radius = (int)Math.Max(1, _send.Size.X / 2);
        var ready = _input.Text.Trim().Length > 0;
        _send.Disabled = !ready;
        _send.AddThemeStyleboxOverride("normal", Glass(radius, Eve.Cyan, Eve.Cyan, 0, 0));
        _send.AddThemeStyleboxOverride("hover", Glass(radius, Eve.Cyan.Lightened(0.15f), Eve.Cyan, 0, 0));
        _send.AddThemeStyleboxOverride("pressed", Glass(radius, Eve.Cyan.Darkened(0.15f), Eve.Cyan, 0, 0));
        _send.AddThemeStyleboxOverride("disabled", Glass(radius, Ink with { A = 0.12f }, Ink, 0, 0));
    }

    /// <summary>
    /// ⏬ alone in the middle of the top: every window small at the bottom, the animation seen (again: the window shown
    /// before comes back). The tools are opened from the line (/work…).
    /// </summary>
    private void BuildShelfButton()
    {
        _shelfButton = new Button { Text = "⏬", FocusMode = FocusModeEnum.None, TooltipText = "Every window small at the bottom of the screen, the 3D animation seen (again: back)", MouseDefaultCursorShape = CursorShape.PointingHand };
        MakeRound(_shelfButton, ToolSize, 17);
        _shelfButton.Pressed += () =>
        {
            if (_deck is not null && IsInstanceValid(_deck))
                _deck.ToggleShelf();
            _refresh = 0;
        };
        AddChild(_shelfButton);
    }

    /// <summary>
    /// ⏬ alone in the middle of the top, under the camera of the screen when it has one (the notch of a MacBook: the
    /// locked full screen goes under it); the window on the stage then starts under ⏬.
    /// </summary>
    private void PlaceShelfButton()
    {
        var (y, stageTop) = ShelfButtonTop();
        _shelfButton.Position = new Vector2((Size.X - ToolSize) / 2, y);
        _shelfButton.Size = new Vector2(ToolSize, ToolSize);
        if (_deck is not null && IsInstanceValid(_deck))
            _deck.StageTop = stageTop;
    }

    /// <summary>The top of ⏬, and the top of the stage under it (0 without a camera in the screen).</summary>
    private (float Button, float Stage) ShelfButtonTop()
    {
        var window = GetWindow();
        var camera = (float)(KioskLock.CameraInset() * DisplayServer.ScreenGetScale(window.CurrentScreen) / Math.Max(1f, window.ContentScaleFactor));
        var y = Math.Max(4, (Size.Y * 0.05f - ToolSize) / 2);
        if (camera <= 0)
            return (y, 0);
        y = Math.Max(y, camera + 8);
        return (y, y + ToolSize + 10);
    }

    /// <summary>⏬ ringed with the accent and tinted while the windows are small at the bottom.</summary>
    private void StyleShelfButton(bool shelved)
    {
        var radius = (int)_shelfButton.CustomMinimumSize.X / 2;
        var back = shelved ? Eve.Cyan.Lerp(Surface, 0.55f) with { A = 0.95f } : Surface;
        _shelfButton.AddThemeStyleboxOverride("normal", Glass(radius, back, shelved ? Eve.Cyan : Hairline, shelved ? 2 : 1, 1f));
    }

    // ── The look: light surfaces, large radii, soft shadows (as on a tablet) ──

    /// <summary>The surface of the controls: frosted white on a light theme, a dark slate on a dark one.</summary>
    internal static Color Surface => Eve.IsLight ? new Color(1, 1, 1, 0.9f) : new Color(0.12f, 0.14f, 0.17f, 0.86f);

    internal static Color SurfaceHover => Eve.IsLight ? Colors.White : new Color(0.17f, 0.2f, 0.24f, 0.94f);

    /// <summary>The fine edge of a surface.</summary>
    internal static Color Hairline => Eve.IsLight ? new Color(0, 0, 0, 0.08f) : new Color(1, 1, 1, 0.12f);

    /// <summary>The text on a surface.</summary>
    internal static Color Ink => Eve.IsLight ? new Color(0.11f, 0.13f, 0.17f) : new Color(0.94f, 0.96f, 0.98f);

    /// <summary>
    /// A rounded surface of <paramref name="radius"/>, its edge <paramref name="border"/> px of <paramref name="edge"/>,
    /// with a soft shadow falling a little below it (<paramref name="shadow"/>: 0 none, 1 the usual one, more: higher).
    /// </summary>
    public static StyleBoxFlat Glass(int radius, Color back, Color edge, int border, float shadow)
    {
        var box = new StyleBoxFlat { BgColor = back, BorderColor = edge, AntiAliasing = true, CornerDetail = 12 };
        box.SetBorderWidthAll(border);
        box.SetCornerRadiusAll(radius);
        if (shadow > 0)
        {
            box.ShadowColor = new Color(0.05f, 0.1f, 0.2f, (Eve.IsLight ? 0.13f : 0.45f) * Math.Min(1.5f, shadow));
            box.ShadowSize = (int)(14 * shadow);
            box.ShadowOffset = new Vector2(0, 4 * shadow);
        }
        return box;
    }

    /// <summary>
    /// <paramref name="button"/> as a disc of <paramref name="size"/> pixels: a surface with a soft shadow, rising and
    /// ringed with the accent under the pointer (the tools of the top, ◀ ▶ and the small windows of the stage).
    /// </summary>
    public static void MakeRound(Button button, int size, int fontSize)
    {
        var radius = size / 2;
        button.CustomMinimumSize = new Vector2(size, size);
        button.AddThemeFontSizeOverride("font_size", fontSize);
        button.AddThemeStyleboxOverride("normal", Glass(radius, Surface, Hairline, 1, 1f));
        button.AddThemeStyleboxOverride("hover", Glass(radius, SurfaceHover, Eve.Cyan, 2, 1.4f));
        button.AddThemeStyleboxOverride("pressed", Glass(radius, Eve.Cyan.Lerp(Surface, 0.6f), Eve.Cyan, 2, 0.5f));
        button.AddThemeStyleboxOverride("disabled", Glass(radius, Surface with { A = Surface.A * 0.45f }, Hairline, 1, 0));
        button.AddThemeColorOverride("font_color", Ink);
        button.AddThemeColorOverride("font_hover_color", Ink);
        button.AddThemeColorOverride("font_pressed_color", Ink);
        button.AddThemeColorOverride("font_disabled_color", Ink with { A = 0.35f });
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseButton { DoubleClick: true, ButtonIndex: MouseButton.Left })
        {
            AcceptEvent();
            Leave();
        }
        // A click on the animation takes the keyboard from the line (V speaks again).
        else if (e is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
            GetViewport().GuiReleaseFocus();
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
        // Typed in the line or in the window on the stage, V is a letter (unless the microphone already records).
        if (!_holdingV && Typing())
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
            // Esc in the line or in the window on the stage only takes the keyboard from it.
            if (Typing())
            {
                GetViewport().GuiReleaseFocus();
                if (_deck is not null && IsInstanceValid(_deck))
                    foreach (var window in _deck.Windows)
                        window.Content.GetViewport()?.GuiReleaseFocus();
            }
            else if (_vm.IsSpeakingOrPreparing)
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
    /// the lock off, the desktop back at its place, the window of the chat back to how it was, the animation back in
    /// the chat.
    /// </summary>
    public void Leave()
    {
        if (IsQueuedForDeletion())
            return;
        KioskLock.Release();
        GiveDeckBack();
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
            // Sized on the ring (180 px around the large microphone, smaller around the one of the margin).
            var center = Size / 2;
            var unit = Math.Min(Size.X, Size.Y) / 180;
            var breath = (float)(0.5 + 0.5 * Math.Sin(_time * 4));
            DrawCircle(center, (70 + breath * 14) * unit, Eve.Cyan with { A = 0.1f + 0.1f * (1 - breath) });
            DrawArc(center, 68 * unit, 0, Mathf.Tau, 64, Eve.Cyan with { A = 0.25f }, Math.Max(2, 4 * unit), true);
            var start = (float)(_time * Mathf.Tau / 1.1);
            DrawArc(center, 68 * unit, start, start + 1.9f, 32, Eve.Cyan, Math.Max(2, 4 * unit), true);
        }
    }
}
