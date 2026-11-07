using System;
using System.Collections.Generic;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// Rings the alarms of /alarm (<see cref="AlarmClock.Shared"/>), whether its window is open or not: checked every
/// second; an alarm due puts the chat in full screen (the voice mode: the 3D animation alone on the screen), shows the
/// alarm over it (<see cref="AlarmScreen"/>) with its ringtone, louder and louder, and the avatar says "Bouge ton cul,
/// gros !" in the language of the chat (<see cref="SpeechLanguage.WakeUp"/>), then the label of the alarm when it has
/// to. The reminders of /calendar (<see cref="CalendarStore.Shared"/>) ring the same way, with the chime, the avatar
/// saying "Rappel : the title à 14:00" (<see cref="SpeechLanguage.Reminder"/>). STOP or SNOOZE bring the chat back as
/// it was. Several rings due at once come one after the other.
/// </summary>
public partial class AlarmRinger : Node
{
    /// <summary>A ring waiting: what the screen shows, what the avatar says, what SNOOZE does.</summary>
    private sealed record Ring(Alarm Shown, Func<string, string> Words, Action<DateTime> Snooze);

    private readonly Action<string> _say;
    private readonly Func<string> _language;
    private readonly Action _enterFullScreen;
    private readonly Action _leaveFullScreen;
    private readonly Queue<Ring> _waiting = new();
    private AlarmScreen? _screen;
    private bool _enteredFullScreen;
    private double _check;

    /// <summary>
    /// A ringer whose words go through <paramref name="say"/> (the voice of the avatar) in the language given by
    /// <paramref name="language"/> (FR, EN, DE…), and which puts the chat in full screen with
    /// <paramref name="enterFullScreen"/> and back with <paramref name="leaveFullScreen"/>.
    /// </summary>
    public AlarmRinger(Action<string> say, Func<string> language, Action enterFullScreen, Action leaveFullScreen)
    {
        _say = say;
        _language = language;
        _enterFullScreen = enterFullScreen;
        _leaveFullScreen = leaveFullScreen;
        Name = nameof(AlarmRinger);
    }

    public override void _Process(double delta)
    {
        _check -= delta;
        if (_check > 0)
            return;
        _check = 1;
        var now = DateTime.Now;
        foreach (var alarm in AlarmClock.Shared.Due(now))
            _waiting.Enqueue(new Ring(alarm,
                language => SpeechLanguage.WakeUp(language) + (alarm.Speak && !string.IsNullOrWhiteSpace(alarm.Label) ? " " + alarm.Label : string.Empty),
                at => AlarmClock.Shared.Snooze(alarm, at)));
        foreach (var (e, start) in CalendarStore.Shared.Due(now))
        {
            // The screen of an alarm, made for the reminder: its title and the time of the event.
            var shown = new Alarm { Label = e.Title, Hour = start.Hour, Minute = start.Minute, Ringtone = "Chime", SnoozeMinutes = 5 };
            var time = e.AllDay ? null : start.ToString("HH:mm");
            _waiting.Enqueue(new Ring(shown, language => SpeechLanguage.Reminder(language, e.Title, time), at => CalendarStore.Shared.Snooze(e, at, 5)));
        }
        if ((_screen is null || !IsInstanceValid(_screen)) && _waiting.Count > 0)
        {
            var keep = _enteredFullScreen;
            Show(_waiting.Dequeue());
            // The next ring of a row: the full screen entered by the first one is left by the last one.
            _enteredFullScreen |= keep;
        }
    }

    private void Show(Ring ring)
    {
        var alarm = ring.Shown;
        ActivityLog.Info("Alarm", $"Ringing: {alarm.Label} ({alarm.Hour:00}:{alarm.Minute:00})");
        _screen = new AlarmScreen(alarm,
            stop: () => Close(),
            snooze: () =>
            {
                ring.Snooze(DateTime.Now);
                Close();
            });
        // The chat in full screen (left as it is when it already was, and then not left at the end).
        _enteredFullScreen = !KioskLock.IsOn;
        if (_enteredFullScreen)
            _enterFullScreen();
        // Above everything: the desktop, the voice mode, the countdown of /bye.
        GetTree().Root.AddChild(_screen);
        _say(ring.Words(_language()));
    }

    private void Close()
    {
        if (_screen is not null && IsInstanceValid(_screen))
            _screen.QueueFree();
        _screen = null;
        _check = 0;
        // Another alarm waiting keeps the full screen; the last one gives the chat back.
        if (_enteredFullScreen && _waiting.Count == 0)
        {
            _enteredFullScreen = false;
            _leaveFullScreen();
        }
    }
}

/// <summary>
/// The screen of an alarm ringing, over the voice mode: the time in large digits with a bell shaking and the label in a
/// band at the top, STOP and SNOOZE (its minutes) at the bottom, the avatar in view between them. Enter, Escape or Space stop it, S snoozes. The ringtone loops and grows from soft to loud over
/// 30 s; after 5 minutes without an answer the alarm is snoozed by itself.
/// </summary>
public partial class AlarmScreen : Control
{
    private static readonly TimeSpan GiveUp = TimeSpan.FromMinutes(5);

    private readonly Alarm _alarm;
    private readonly Action _stop;
    private readonly Action _snooze;
    private readonly AudioStreamPlayer _player = new();
    private HBoxContainer _buttons = null!;
    private double _since;

    /// <summary>The height of the band at the top (the time, the label), the buttons just under it.</summary>
    private float Band => Size.Y * 0.26f;
    private bool _done;

    public AlarmScreen(Alarm alarm, Action stop, Action snooze)
    {
        _alarm = alarm;
        _stop = stop;
        _snooze = snooze;
        Name = nameof(AlarmScreen);
        MouseFilter = MouseFilterEnum.Stop;
        Modulate = Colors.Transparent;
        ZIndex = 4000;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _player.Stream = Ringtones.Get(_alarm.Ringtone);
        _player.VolumeDb = -24;
        AddChild(_player);
        _player.Play();
        CreateTween().TweenProperty(this, "modulate:a", 1f, 0.3);

        // Styled here: this screen sits on the root of the window, outside the theme of the chat.
        var stop = Big("■  STOP", Eve.Amber, new Color(0.05f, 0.05f, 0.07f), "Stop the alarm (Enter, Escape, Space)");
        stop.CustomMinimumSize = new Vector2(220, 64);
        stop.Pressed += () => Finish(_stop);
        var snooze = Big($"💤  SNOOZE {_alarm.SnoozeMinutes} MIN", new Color(1, 1, 1, 0.12f), Colors.White, "Ring again in a few minutes (S)");
        snooze.CustomMinimumSize = new Vector2(280, 64);
        snooze.Pressed += () => Finish(_snooze);
        // At the scale of the screen (a 4K screen: larger buttons).
        var m = Math.Min(GetViewportRect().Size.X, GetViewportRect().Size.Y);
        var fontSize = Math.Max(22, (int)(m * 0.022f));
        foreach (var button in new[] { stop, snooze })
            button.AddThemeFontSizeOverride("font_size", fontSize);
        stop.CustomMinimumSize = new Vector2(Math.Max(220, m * 0.11f), Math.Max(64, m * 0.055f));
        snooze.CustomMinimumSize = new Vector2(Math.Max(280, m * 0.15f), Math.Max(64, m * 0.055f));
        _buttons = Ui.Row((int)Math.Max(24, m * 0.015f), snooze, stop);
        AddChild(_buttons);
        Callable.From(() => stop.GrabFocus()).CallDeferred();
    }

    public override void _Process(double delta)
    {
        _since += delta;
        // From soft to loud in 30 s.
        _player.VolumeDb = (float)Mathf.Lerp(-24, 0, Math.Min(1, _since / 30));
        if (!_done && _since > GiveUp.TotalSeconds)
            Finish(_snooze);
        // Under the band: the bottom of the screen holds the windows of the voice mode, small in a row.
        var buttonsSize = _buttons.GetCombinedMinimumSize();
        _buttons.Position = new Vector2((Size.X - buttonsSize.X) / 2, Band + Size.Y * 0.03f);
        QueueRedraw();
    }

    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        if (key.Keycode is Key.Escape or Key.Enter or Key.KpEnter or Key.Space)
        {
            GetViewport().SetInputAsHandled();
            Finish(_stop);
        }
        else if (key.Keycode == Key.S)
        {
            GetViewport().SetInputAsHandled();
            Finish(_snooze);
        }
    }

    /// <summary>A large button of <paramref name="fill"/> with its text in <paramref name="ink"/>, lighter under the pointer.</summary>
    private static Button Big(string text, Color fill, Color ink, string tooltip)
    {
        var button = new Button { Text = text, TooltipText = tooltip, FocusMode = FocusModeEnum.All };
        button.AddThemeFontOverride("font", Eve.Display);
        button.AddThemeFontSizeOverride("font_size", 22);
        foreach (var (state, colour) in new[] { ("normal", fill), ("focus", fill), ("hover", fill.Lightened(0.15f)), ("pressed", fill.Darkened(0.15f)) })
        {
            var box = Eve.Box(colour, Colors.White with { A = 0.35f }, 10);
            box.SetBorderWidthAll(state == "focus" ? 2 : 1);
            box.ContentMarginLeft = box.ContentMarginRight = 24;
            button.AddThemeStyleboxOverride(state, box);
        }
        foreach (var name in new[] { "font_color", "font_hover_color", "font_pressed_color", "font_focus_color" })
            button.AddThemeColorOverride(name, ink);
        return button;
    }

    private void Finish(Action action)
    {
        if (_done)
            return;
        _done = true;
        _player.Stop();
        action();
    }

    public override void _Draw()
    {
        // The avatar of the voice mode stays in view in the middle: the alarm takes a band at the top, its buttons just
        // under it, over a light veil (the windows are small in a row at the bottom).
        var size = Size;
        var m = Math.Min(size.X, size.Y);
        var t = (float)_since;
        var pulse = 0.5f + 0.5f * MathF.Sin(t * 9);
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.02f, 0.03f, 0.05f, 0.2f));
        var band = Band;
        DrawRect(new Rect2(0, 0, size.X, band), new Color(0.02f, 0.03f, 0.05f, 0.7f));
        // The edge of the band beats with the ringtone, a light running along it.
        var edge = Math.Max(3, m * 0.006f);
        DrawLine(new Vector2(0, band), new Vector2(size.X, band), Eve.Amber with { A = 0.35f + 0.5f * pulse }, edge);
        var run = (t * 0.6f % 1) * size.X;
        DrawLine(new Vector2(run - size.X * 0.08f, band), new Vector2(run + size.X * 0.08f, band), Colors.White with { A = 0.8f }, edge);

        var font = Eve.Display;
        var timeText = $"{_alarm.Hour:00}:{_alarm.Minute:00}";
        var timeSize = (int)(m * 0.12f);
        var timeWidth = font.GetStringSize(timeText, HorizontalAlignment.Left, -1, timeSize).X;
        var bellSize = (int)(timeSize * 0.6f);
        var timeAt = new Vector2((size.X - timeWidth + bellSize * 1.3f) / 2, band * 0.58f);
        DrawString(font, timeAt, timeText, HorizontalAlignment.Left, -1, timeSize, Colors.White);
        // The bell, shaking, on the left of the time.
        DrawSetTransform(timeAt + new Vector2(-bellSize * 0.8f, -timeSize * 0.32f), MathF.Sin(t * 40) * 0.15f, Vector2.One);
        DrawString(font, new Vector2(-bellSize * 0.5f, bellSize * 0.35f), "⏰", HorizontalAlignment.Left, -1, bellSize, Colors.White);
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
        DrawString(font, new Vector2(0, band * 0.86f), _alarm.Label.ToUpperInvariant(), HorizontalAlignment.Center, size.X, (int)(m * 0.035f), Eve.Amber);

        var caption = (int)(m * 0.016f);
        var below = _buttons.Position.Y + _buttons.Size.Y + caption * 2f;
        DrawString(font, new Vector2(0, below), DateTime.Now.ToString("dddd d MMMM").ToUpperInvariant() + "  ·  ENTER: STOP  ·  S: SNOOZE",
            HorizontalAlignment.Center, size.X, caption, Colors.White with { A = 0.85f });
    }
}
