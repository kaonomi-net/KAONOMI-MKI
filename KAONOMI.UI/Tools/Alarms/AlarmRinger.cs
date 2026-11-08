using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Alarms;

/// <summary>
/// Rings the alarms of /alarm (<see cref="AlarmClock.Shared"/>) and the reminders of /calendar
/// (<see cref="CalendarStore.Shared"/>), whether their windows are open or not, checked every second, as in the Godot
/// chat: the chat goes full screen, the alarm is shown over it (<see cref="AlarmScreen"/>) with its ringtone, louder and
/// louder, and the voice says "Bouge ton cul, gros !" in the language of the chat (<see cref="SpeechLanguage.WakeUp"/>),
/// then the label when it has to. STOP or SNOOZE give the chat back as it was; several rings due come one after the other.
/// </summary>
public sealed class AlarmRinger
{
    private sealed record Ring(Alarm Shown, Func<string, string> Words, Action<DateTime> Snooze);

    private readonly Panel _layer;
    private readonly Action<string> _say;
    private readonly Func<string> _language;
    private readonly Action _enterFullScreen;
    private readonly Action _leaveFullScreen;
    private readonly Queue<Ring> _waiting = new();
    private AlarmScreen? _screen;
    private bool _enteredFullScreen;

    /// <summary>A ringer that shows its screen in <paramref name="layer"/> (over the whole window).</summary>
    public AlarmRinger(Panel layer, Action<string> say, Func<string> language, Action enterFullScreen, Action leaveFullScreen)
    {
        _layer = layer;
        _say = say;
        _language = language;
        _enterFullScreen = enterFullScreen;
        _leaveFullScreen = leaveFullScreen;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Check();
        timer.Start();
    }

    private void Check()
    {
        var now = DateTime.Now;
        foreach (var alarm in AlarmClock.Shared.Due(now))
            _waiting.Enqueue(new Ring(alarm,
                language => SpeechLanguage.WakeUp(language) + (alarm.Speak && !string.IsNullOrWhiteSpace(alarm.Label) ? " " + alarm.Label : string.Empty),
                at => AlarmClock.Shared.Snooze(alarm, at)));
        foreach (var (e, start) in CalendarStore.Shared.Due(now))
        {
            var shown = new Alarm { Label = e.Title, Hour = start.Hour, Minute = start.Minute, Ringtone = "Chime", SnoozeMinutes = 5 };
            var time = e.AllDay ? null : start.ToString("HH:mm");
            _waiting.Enqueue(new Ring(shown, language => SpeechLanguage.Reminder(language, e.Title, time), at => CalendarStore.Shared.Snooze(e, at, 5)));
        }
        if (_screen is null && _waiting.Count > 0)
            Show(_waiting.Dequeue());
    }

    private void Show(Ring ring)
    {
        var alarm = ring.Shown;
        ActivityLog.Info("Alarm", $"Ringing: {alarm.Label} ({alarm.Hour:00}:{alarm.Minute:00})");
        _screen = new AlarmScreen(alarm, stop: Close, snooze: () =>
        {
            ring.Snooze(DateTime.Now);
            Close();
        });
        if (!_enteredFullScreen)
        {
            _enteredFullScreen = true;
            _enterFullScreen();
        }
        _layer.Children.Add(_screen);
        _layer.IsHitTestVisible = true;
        _screen.Focus();
        _say(ring.Words(_language()));
    }

    private void Close()
    {
        if (_screen is not null)
            _layer.Children.Remove(_screen);
        _screen = null;
        _layer.IsHitTestVisible = false;
        // Another ring waiting keeps the full screen; the last one gives the chat back.
        if (_waiting.Count > 0)
        {
            Show(_waiting.Dequeue());
            return;
        }
        if (_enteredFullScreen)
        {
            _enteredFullScreen = false;
            _leaveFullScreen();
        }
    }
}

/// <summary>
/// The screen of an alarm ringing: a band at the top with the time in large digits and the label, SNOOZE and STOP under
/// it, over a veil. Enter, Escape or Space stop it, S snoozes; after 5 minutes without an answer it snoozes by itself.
/// </summary>
public sealed class AlarmScreen : Grid
{
    private static readonly TimeSpan GiveUp = TimeSpan.FromMinutes(5);

    private readonly CancellationTokenSource _ringing = new();
    private readonly Action _stop, _snooze;
    private readonly IDisposable _giveUp;
    private bool _done;

    public AlarmScreen(Alarm alarm, Action stop, Action snooze)
    {
        _stop = stop;
        _snooze = snooze;
        Focusable = true;
        Background = new SolidColorBrush(Color.FromArgb(150, 5, 8, 12));
        RowDefinitions = new RowDefinitions("Auto,Auto,*");

        var time = new TextBlock { Text = $"⏰ {alarm.Hour:00}:{alarm.Minute:00}", FontSize = 120, FontWeight = FontWeight.Light, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
        var label = new TextBlock { Text = alarm.Label.ToUpperInvariant(), FontSize = 36, Foreground = Ui.Warm, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        var band = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 5, 8, 12)),
            BorderBrush = Ui.Warm,
            BorderThickness = new Thickness(0, 0, 0, 4),
            Padding = new Thickness(20, 30),
            Child = Ui.Column(6, time, label)
        };
        Children.Add(band);

        var snoozeButton = Big($"💤  SNOOZE {alarm.SnoozeMinutes} MIN", new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), Brushes.White, "Ring again in a few minutes (S)", () => Finish(_snooze));
        var stopButton = Big("■  STOP", Ui.Warm, new SolidColorBrush(Color.FromRgb(13, 13, 18)), "Stop the alarm (Enter, Escape, Space)", () => Finish(_stop));
        var buttons = Ui.Row(28, snoozeButton, stopButton);
        buttons.HorizontalAlignment = HorizontalAlignment.Center;
        buttons.Margin = new Thickness(0, 30, 0, 10);
        SetRow(buttons, 1);
        Children.Add(buttons);
        var hint = new TextBlock { Text = DateTime.Now.ToString("dddd d MMMM").ToUpperInvariant() + "  ·  ENTER: STOP  ·  S: SNOOZE", Foreground = Brushes.White, Opacity = 0.85, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center };
        SetRow(hint, 2);
        Children.Add(hint);

        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Escape or Key.Enter or Key.Space)
            {
                e.Handled = true;
                Finish(_stop);
            }
            else if (e.Key == Key.S)
            {
                e.Handled = true;
                Finish(_snooze);
            }
        };
        _giveUp = DispatcherTimer.RunOnce(() => Finish(_snooze), GiveUp);
        _ = Ringtones.RingAsync(alarm.Ringtone, _ringing.Token);
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => stopButton.Focus());
    }

    private static Button Big(string text, IBrush fill, IBrush ink, string tooltip, Action click)
    {
        var button = new Button { Content = text, Background = fill, Foreground = ink, FontSize = 26, Padding = new Thickness(32, 14), CornerRadius = new CornerRadius(10), BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), BorderThickness = new Thickness(1) };
        ToolTip.SetTip(button, tooltip);
        button.Click += (_, _) => click();
        return button;
    }

    private void Finish(Action action)
    {
        if (_done)
            return;
        _done = true;
        _giveUp.Dispose();
        _ringing.Cancel();
        action();
    }
}
