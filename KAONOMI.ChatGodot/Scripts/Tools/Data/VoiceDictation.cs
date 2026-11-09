using System;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.ChatGodot.Tools.Data;

/// <summary>
/// The voice dictation of a text area, opened from its right-click menu ("🎤 Voice dictation": <see cref="TextMenu"/>
/// for every <see cref="TextEdit"/>, the menu of the tape for /work and the chapters of /ghostwriter),
/// in the way of Google Docs: a small floating panel over the bottom-right corner of the text area (it can be dragged,
/// and follows the text area), with a round 🎤. A click turns the microphone on (the 🎤 turns red and pulses), another
/// one pauses it. While it listens, the words said so far show under the 🎤; at each pause in the speech (or every 25
/// seconds) they are turned into text by Whisper (on this machine, the model of <c>speech.model</c> in config.yml) and
/// written at the caret of the text area. The language of the speech is the one of the voice input of the chat
/// (<c>speech.language</c>), switched with its button (FR, EN, DE, JA). ✕ closes it, writing what was said before.
/// One dictation at a time: opened on another text area, it moves there.
/// </summary>
public partial class VoiceDictation : PanelContainer
{
    /// <summary>A pause in the speech this long ends a piece of dictation: it is written in the text.</summary>
    private static readonly TimeSpan EndPause = TimeSpan.FromMilliseconds(700);

    /// <summary>A piece of dictation longer than this is written even without a pause (Whisper works best on short pieces).</summary>
    private static readonly TimeSpan LongestPiece = TimeSpan.FromSeconds(25);

    /// <summary>Silence longer than this is forgotten (nothing to transcribe, and the recording would only grow).</summary>
    private static readonly TimeSpan LongestSilence = TimeSpan.FromSeconds(8);

    private static VoiceDictation? _current;
    private static SpeechRecognitionService? _speech;
    private static Task? _warmUp;

    /// <summary>The text area written into (a <see cref="TextEdit"/>, or the tape of /work).</summary>
    private readonly Control _edit;
    private readonly Action<string> _insert;
    private string _language;
    private Button _mic = null!;
    private Label _status = null!;
    private Label _words = null!;
    private Button _languageButton = null!;

    /// <summary>True while the microphone is on (the 🎤 red).</summary>
    private bool _listening;
    /// <summary>The stop of the microphone and the transcription of its last words, while they run.</summary>
    private Task? _finishing;
    private bool _closed;
    /// <summary>Where the panel is, from the top-left corner of the text area; null: its bottom-right corner.</summary>
    private Vector2? _offset;
    private Vector2? _dragFrom;
    private double _time;

    private VoiceDictation(Control edit, Action<string> insert, string language)
    {
        _edit = edit;
        _insert = insert;
        _language = language;
    }

    /// <summary>True when the dictation is open on <paramref name="edit"/>.</summary>
    public static bool IsOpenOn(Control edit) => _current is { } current && IsInstanceValid(current) && !current._closed && current._edit == edit;

    /// <summary>Opens the dictation on <paramref name="edit"/> (and starts listening), or closes it when it is open there.</summary>
    public static void Toggle(TextEdit edit) => Toggle(edit, text => InsertInto(edit, text));

    /// <summary>The same for the tape of /work (and of the chapters of /ghostwriter): the words typed at its caret.</summary>
    public static void Toggle(Work.TapeEditor editor) => Toggle(editor, text => editor.InsertPlainText(text + " "));

    private static void Toggle(Control edit, Action<string> insert)
    {
        if (IsOpenOn(edit))
        {
            _current!.Close();
            return;
        }
        _current?.Close();
        if (edit.GetViewport() is not { } viewport)
            return;
        var settings = ChatSettings.Load();
        _speech ??= new SpeechRecognitionService(settings.SpeechModel);
        if (!_speech.HasModel)
        {
            ActivityLog.Error("Dictation", $"Whisper model not found: {_speech.ModelPath}");
            OS.Alert($"Voice dictation: the Whisper model was not found ({_speech.ModelPath}). Set speech.model in config.yml.", "Voice dictation");
            return;
        }
        // The model loaded and run once on silence: the first words then come as fast as the next ones.
        _warmUp ??= _speech.WarmUpAsync(settings.SpeechLanguage);
        var dictation = new VoiceDictation(edit, insert, settings.SpeechLanguage);
        _current = dictation;
        viewport.AddChild(dictation);
        dictation.Listen();
    }

    public override void _Ready()
    {
        Theme = Eve.Eve.Shared;
        TopLevel = true;
        ZIndex = 100;
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.Move;
        var box = Eve.Eve.Box(Eve.Eve.PanelSolid, Eve.Eve.EdgeBright with { A = 0.6f }, 8);
        box.SetCornerRadiusAll(28);
        box.ShadowColor = new Color(0, 0, 0, 0.45f);
        box.ShadowSize = 12;
        AddThemeStyleboxOverride("panel", box);

        _mic = new Button
        {
            Text = "🎤",
            CustomMinimumSize = new Vector2(44, 44),
            FocusMode = FocusModeEnum.None,
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        _mic.AddThemeFontSizeOverride("font_size", 20);
        _mic.Pressed += () =>
        {
            if (_listening)
                Pause();
            else
                Listen();
        };

        _status = Eve.Eve.Caption("Voice dictation", Eve.Eve.Muted, 10);
        _words = Eve.Eve.Text_(string.Empty, Eve.Eve.Text, 12);
        _words.CustomMinimumSize = new Vector2(190, 0);
        _words.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _words.MaxLinesVisible = 2;
        _words.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _words.MouseFilter = MouseFilterEnum.Ignore;
        _status.MouseFilter = MouseFilterEnum.Ignore;

        _languageButton = Small(_language.ToUpperInvariant(), "Language of the speech (click to change)");
        _languageButton.Pressed += NextLanguage;
        var close = Small("✕", "Close the voice dictation");
        close.Pressed += Close;

        var head = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        head.AddThemeConstantOverride("separation", 4);
        _status.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        head.AddChild(_status);
        head.AddChild(_languageButton);
        head.AddChild(close);

        var text = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        text.AddThemeConstantOverride("separation", 2);
        text.AddChild(head);
        text.AddChild(_words);

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", 10);
        row.AddChild(_mic);
        row.AddChild(text);
        AddChild(row);

        // The text area goes (its window closed, its tab rebuilt): the dictation ends with it.
        _edit.TreeExiting += Close;
        Refresh();
        _ = RunAsync();
    }

    private static Button Small(string text, string tooltip)
    {
        var button = new Button
        {
            Text = text,
            TooltipText = tooltip,
            Flat = true,
            FocusMode = FocusModeEnum.None,
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        button.AddThemeFontSizeOverride("font_size", 10);
        return button;
    }

    // ── Place: over the bottom-right corner of the text area, or where it was dragged ──

    public override void _Process(double delta)
    {
        // Only what changed is set (not at every frame).
        if (!IsInstanceValid(_edit) || !_edit.IsVisibleInTree())
        {
            if (Visible)
                Visible = false;
            return;
        }
        if (!Visible)
            Visible = true;
        var rect = _edit.GetGlobalRect();
        var at = _offset is { } offset ? rect.Position + offset : rect.End - Size - new Vector2(12, 12);
        var room = GetViewportRect().Size - Size;
        var position = new Vector2(Mathf.Clamp(at.X, 0, Math.Max(0, room.X)), Mathf.Clamp(at.Y, 0, Math.Max(0, room.Y)));
        if (Position != position)
            Position = position;

        // The 🎤 pulses while it listens.
        _time += delta;
        var modulate = _listening ? new Color(1, 1, 1, 0.75f + 0.25f * (float)Math.Sin(_time * 5)) : Colors.White;
        if (_mic.Modulate != modulate)
            _mic.Modulate = modulate;
    }

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                _dragFrom = button.Pressed ? button.GlobalPosition - Position : null;
                AcceptEvent();
                break;
            case InputEventMouseMotion motion when _dragFrom is { } from && IsInstanceValid(_edit):
                _offset = motion.GlobalPosition - from - _edit.GetGlobalRect().Position;
                AcceptEvent();
                break;
        }
    }

    // ── Listen, pause, close ──

    private async void Listen()
    {
        if (_closed || _listening || _speech is not { } speech)
            return;
        // A pause still writing its last words: the microphone opens after.
        if (_finishing is { } finishing)
            await finishing;
        if (_closed)
            return;
        _listening = true;
        _words.Text = string.Empty;
        Refresh();
        try
        {
            await Task.Run(speech.Start).WaitAsync(TimeSpan.FromSeconds(6));
            ActivityLog.Info("Dictation", $"Listening ({_language})");
        }
        catch (Exception ex)
        {
            // Given up: the microphone, if it opens later, is closed at once (it never records unseen).
            speech.Cancel();
            _listening = false;
            var message = ex is TimeoutException ? "the microphone does not open" : ex.Message;
            ActivityLog.Error("Dictation", "Microphone: " + message);
            if (!_closed)
            {
                _status.Text = "MICROPHONE: " + message.ToUpperInvariant();
                _status.AddThemeColorOverride("font_color", Eve.Eve.Red);
            }
            return;
        }
        if (_closed)
            speech.Cancel();
        else
            Refresh();
    }

    /// <summary>Turns the microphone off; what was said before is written in the text.</summary>
    private void Pause()
    {
        if (!_listening || _speech is not { } speech)
            return;
        _listening = false;
        _finishing = WriteAsync(speech.StopAndTranscribeAsync(_language));
        Refresh();
    }

    private void Close()
    {
        if (_closed)
            return;
        Pause();
        _closed = true;
        if (_current == this)
            _current = null;
        if (IsInstanceValid(_edit))
            _edit.TreeExiting -= Close;
        if (_finishing is { } finishing)
            _ = finishing.ContinueWith(_ => Callable.From(() => { if (IsInstanceValid(this)) QueueFree(); }).CallDeferred());
        else
            QueueFree();
    }

    private void NextLanguage()
    {
        var languages = ChatSettings.SpeechLanguages;
        var index = Array.IndexOf(languages, _language);
        _language = languages[(index + 1) % languages.Length];
        _languageButton.Text = _language.ToUpperInvariant();
    }

    private void Refresh()
    {
        if (_closed || !IsInstanceValid(this))
            return;
        _status.RemoveThemeColorOverride("font_color");
        _status.AddThemeColorOverride("font_color", _listening ? Eve.Eve.Red : Eve.Eve.Muted);
        _status.Text = (_listening ? "● Listening" : _finishing is { IsCompleted: false } ? "Writing…" : "Paused").ToUpperInvariant();
        _mic.TooltipText = _listening ? "Click to pause the dictation" : "Click to dictate (the words are written at the caret)";
        var back = _listening ? Eve.Eve.Red : Eve.Eve.Field;
        var edge = _listening ? Eve.Eve.Red : Eve.Eve.EdgeBright;
        foreach (var state in new[] { "normal", "hover", "pressed" })
        {
            var box = Eve.Eve.Box(back with { A = _listening ? (state == "normal" ? 0.55f : 0.75f) : (state == "normal" ? 0.6f : 0.9f) }, edge, 4);
            box.SetCornerRadiusAll(22);
            _mic.AddThemeStyleboxOverride(state, box);
        }
        if (!_listening && _finishing is not { IsCompleted: false } && _words.Text.Length == 0)
            _words.Text = "Click 🎤 to dictate at the caret";
    }

    // ── The speech, cut at its pauses ──

    /// <summary>
    /// While the dictation is open: the words said so far shown under the 🎤, and at each pause in the speech (or a piece
    /// too long) the piece turned into text and written at the caret.
    /// </summary>
    private async Task RunAsync()
    {
        if (_warmUp is { } warmUp)
            await warmUp;
        var shown = string.Empty;
        while (!_closed)
        {
            await Task.Delay(200);
            if (_closed || !_listening || _speech is not { } speech || !speech.IsRecording)
                continue;
            try
            {
                var recorded = speech.Recorded;
                var (whole, recent) = speech.Peaks(EndPause);
                var spoke = whole >= SpeechRecognitionService.Silence;
                if (!spoke)
                {
                    if (recorded > LongestSilence)
                        speech.Discard();
                    continue;
                }
                var paused = recorded > EndPause * 2 && recent < Math.Max(SpeechRecognitionService.Silence, whole * 0.12f);
                if (paused || recorded > LongestPiece)
                {
                    await WriteAsync(speech.TakeAndTranscribeAsync(_language));
                    shown = string.Empty;
                    continue;
                }
                var text = await speech.TranscribeSoFarAsync(_language);
                if (!_closed && _listening && text.Length > 0 && text != shown)
                {
                    shown = text;
                    _words.Text = text;
                }
            }
            catch (Exception ex) when (ex is not ObjectDisposedException)
            {
                ActivityLog.Error("Dictation", "Transcription: " + ex.Message);
                if (!_closed)
                    _words.Text = "Transcription failed: " + ex.Message;
            }
        }
    }

    /// <summary>Writes the text of <paramref name="transcription"/> at the caret of the text area (nothing when it is empty).</summary>
    private async Task WriteAsync(Task<string> transcription)
    {
        string text;
        try
        {
            text = await transcription;
        }
        catch (Exception ex)
        {
            ActivityLog.Error("Dictation", "Transcription: " + ex.Message);
            text = string.Empty;
        }
        Insert(text);
        if (!_closed && IsInstanceValid(this))
        {
            if (!_listening)
                _words.Text = string.Empty;
            Refresh();
        }
    }

    private void Insert(string text)
    {
        if (text.Length > 0 && IsInstanceValid(_edit))
            _insert(text);
    }

    /// <summary><paramref name="text"/> at the caret of <paramref name="edit"/> (in place of the selection), a space before it when it follows a word.</summary>
    private static void InsertInto(TextEdit edit, string text)
    {
        if (!IsInstanceValid(edit) || !edit.Editable)
            return;
        var line = edit.GetLine(edit.GetCaretLine());
        var column = Math.Min(edit.GetCaretColumn(), line.Length);
        var space = !edit.HasSelection() && column > 0 && !char.IsWhiteSpace(line[column - 1]) ? " " : string.Empty;
        edit.InsertTextAtCaret(space + text);
    }
}
