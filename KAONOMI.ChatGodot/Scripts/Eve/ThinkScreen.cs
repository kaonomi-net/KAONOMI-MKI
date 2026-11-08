using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// <c>/think</c>: the screen goes black over everything, "think …" at the top (its dots breathing), and what the
/// dialogs of the last hour brought out floats in 3D, a word cloud over the whole screen where each word wanders and
/// its opacity waves: the entities the NER detected (<c>GET /detectedentity/top</c>, coloured by their type), the
/// symbols of the symbolic patterns that matched (<c>GET /detectedsymbol/top</c>, coloured by their signal: !SAD blue,
/// !HAPPY gold, !FEAR red, !DISGUST green), the tags of the detections (<c>GET /detectedtag/top</c>, #TAG) and the most
/// frequent words of the text of the annotations of the hour (in lilac); the more often found the larger. The messages of the ticker (<c>POST /messageflow/last</c>) cross the screen on lanes of
/// their own, in their colour; the annotations of the hour are cards of whole sentences in a cloud that turns slowly;
/// the pictures of the image analyses of the hour float around it. No id is ever shown. A black fog gives the depth. A quiet music plays meanwhile, made by the code (no file): a pad of four calm chords that follow one
/// another slowly, a few bell notes of the pentatonic scale now and then, in a large reverb. A key or a click brings the
/// chat back (the music fades with the screen).
/// </summary>
public partial class ThinkScreen : Control
{
    private const int Minutes = 60;
    /// <summary>The cards of the cloud (the annotations of the hour), the newest first.</summary>
    private const int CloudItems = 36;
    /// <summary>The length of a card, in whole sentences.</summary>
    private const int MaxCharacters = 300;
    private const int Pictures = 16;
    /// <summary>The pictures grow to this size (times their first size), then shrink by half, again and again, in this many seconds each way.</summary>
    private const float PictureGrowth = 2f;
    private const double PictureBreath = 5.25;
    /// <summary>The words of the cloud, the most frequent of each kind: detected entities, symbols, tags.</summary>
    private const int EntityWords = 40;
    private const int SymbolWords = 40;
    private const int TagWords = 30;
    /// <summary>The most frequent words of the text of the annotations of the hour.</summary>
    private const int AnnotationWords = 25;
    /// <summary>The messages of the ticker that cross the screen.</summary>
    private const int Messages = 12;
    private const float CloudRadius = 9f;
    private const float Turn = 0.045f;

    private readonly ChatSettings _settings;
    private readonly KaonomiAssistantService _assistant;
    private readonly List<(Node3D Node, Vector3 Home, Vector3 Phase, float Range)> _floating = [];
    private readonly List<(Label3D Label, float Phase)> _entities = [];
    private static readonly Color Gold = new(1f, 0.84f, 0.45f);
    private Label _title = null!;
    private Label _footer = null!;
    private AudioStreamPlayer? _music;
    private SubViewport _scene = null!;
    private Camera3D _camera = null!;
    private Node3D _cloud = null!;
    private double _clock;
    private bool _leaving;

    public event Action? Finished;

    public ThinkScreen(ChatSettings settings, KaonomiAssistantService assistant)
    {
        _settings = settings;
        _assistant = assistant;
        MouseFilter = MouseFilterEnum.Stop;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public override void _Ready()
    {
        var black = new ColorRect { Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore };
        black.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(black);

        // The 3D scene: a camera in front of the cloud, a black fog behind, no light (the words and pictures glow by themselves).
        var container = new SubViewportContainer { Stretch = true, MouseFilter = MouseFilterEnum.Ignore };
        container.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(container);
        _scene = new SubViewport { OwnWorld3D = true, TransparentBg = true, Msaa3D = Viewport.Msaa.Msaa4X, HandleInputLocally = false };
        container.AddChild(_scene);
        _scene.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0, 0, 0, 0),
                FogEnabled = true,
                FogLightColor = Colors.Black,
                FogDensity = 0.035f,
                FogSkyAffect = 0
            }
        });
        _camera = new Camera3D { Fov = 55, Near = 0.1f, Far = 200, Current = true };
        _camera.Position = new Vector3(0, 0, CameraDistance);
        _scene.AddChild(_camera);
        _cloud = new Node3D();
        _scene.AddChild(_cloud);

        _title = new Label { Text = "think …", MouseFilter = MouseFilterEnum.Ignore };
        _title.AddThemeFontOverride("font", Eve.Display);
        _title.AddThemeFontSizeOverride("font_size", 24);
        _title.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.75f));
        _title.Position = new Vector2(36, 28);
        AddChild(_title);

        _footer = new Label { Text = string.Empty, MouseFilter = MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Right };
        _footer.AddThemeFontOverride("font", Eve.Display);
        _footer.AddThemeFontSizeOverride("font_size", 10);
        _footer.AddThemeColorOverride("font_color", new Color(1, 1, 1, 0.35f));
        _footer.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomRight);
        _footer.OffsetLeft = -800;
        _footer.OffsetTop = -30;
        _footer.OffsetRight = -36;
        _footer.OffsetBottom = -12;
        AddChild(_footer);

        // The black comes over the chat in half a second.
        Modulate = new Color(1, 1, 1, 0);
        CreateTween().TweenProperty(this, "modulate:a", 1f, 0.5);
        ActivityLog.Info("Think", "Thinking over the last hour");
        _ = LoadAsync();
        _ = PlayMusicAsync();
    }

    // ── The music ──

    private const int Rate = 44100;
    private const double ChordSeconds = 7;
    private const string MusicBus = "Think";

    /// <summary>The pad is made on another thread (half a second), then played, rising from silence over 4 s.</summary>
    private async Task PlayMusicAsync()
    {
        AudioStreamWav stream;
        try
        {
            stream = await Task.Run(MakeMusic);
        }
        catch (Exception ex)
        {
            ActivityLog.Error("Think", "The music could not be made", ex);
            return;
        }
        if (!IsInsideTree() || _leaving)
            return;
        Bind.OnMain(() =>
        {
            if (!IsInstanceValid(this) || _leaving)
                return;
            EnsureMusicBus();
            // Quiet, and two semitones lower than written (the pitch scale slows the loop a little too, which suits a pad).
            _music = new AudioStreamPlayer { Stream = stream, VolumeDb = -60, Bus = MusicBus, PitchScale = MathF.Pow(2f, -2f / 12f) };
            AddChild(_music);
            _music.Play();
            CreateTween().TweenProperty(_music, "volume_db", -24f, 4.0).SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
        });
    }

    /// <summary>The bus of the music, with a large reverb, made once.</summary>
    private static void EnsureMusicBus()
    {
        if (AudioServer.GetBusIndex(MusicBus) >= 0)
            return;
        var index = AudioServer.BusCount;
        AudioServer.AddBus(index);
        AudioServer.SetBusName(index, MusicBus);
        AudioServer.AddBusEffect(index, new AudioEffectReverb { RoomSize = 0.92f, Damping = 0.45f, Wet = 0.4f, Dry = 0.75f, Spread = 1f, Hipass = 0.08f, PredelayMsec = 60 });
    }

    /// <summary>
    /// The pad, as a loop of 28 s: four chords (Am9, Fmaj7, Cmaj7 add 9, G add 9) of five soft voices each (a sine, a
    /// little of its second and third harmonics, detuned a touch between the two ears), 7 s each, crossfading over
    /// 2 s, each one swelling then settling to a softer level (no long sustain), a breath over 16 s, and bell notes of
    /// the A minor pentatonic scale every few seconds, ringing for under 2 s.
    /// </summary>
    private static AudioStreamWav MakeMusic()
    {
        double[][] chords =
        [
            [110.00, 164.81, 261.63, 392.00, 493.88], // Am9
            [87.31, 130.81, 329.63, 440.00, 523.25], // Fmaj7
            [130.81, 196.00, 329.63, 493.88, 587.33], // Cmaj7 add 9
            [98.00, 146.83, 246.94, 329.63, 440.00] // G add 9
        ];
        var loop = ChordSeconds * chords.Length;
        var frames = (int)(loop * Rate);
        var left = new float[frames];
        var right = new float[frames];
        const double fade = 2.0;

        // The envelope of a chord at the time t of the loop: a raised cosine of 2 s in, then the chord settles from 1
        // to 0.55 over its length (a swell, not a sustain), a raised cosine of 2 s out (the crossfade wraps around the
        // end of the loop, so the loop is seamless).
        static double Envelope(double t, int chord, double loop)
        {
            var start = chord * ChordSeconds;
            var local = ((t - start) % loop + loop) % loop; // 0 at the start of the chord
            if (local < ChordSeconds)
            {
                var settle = 1 - 0.45 * Math.Clamp((local - fade) / (ChordSeconds - fade), 0, 1);
                return (local < fade ? 0.5 - 0.5 * Math.Cos(Math.PI * local / fade) : 1) * settle;
            }
            var after = local - ChordSeconds;
            return after < fade ? (0.5 + 0.5 * Math.Cos(Math.PI * after / fade)) * 0.55 : 0;
        }

        var phases = new double[chords.Length][];
        var random = new Random(11);
        for (var c = 0; c < chords.Length; c++)
            phases[c] = chords[c].Select(_ => random.NextDouble() * Math.Tau).ToArray();
        for (var n = 0; n < frames; n++)
        {
            var t = (double)n / Rate;
            var breath = 0.82 + 0.18 * Math.Sin(Math.Tau * t / 16);
            double l = 0, r = 0;
            for (var c = 0; c < chords.Length; c++)
            {
                var env = Envelope(t, c, loop);
                if (env <= 0)
                    continue;
                for (var v = 0; v < chords[c].Length; v++)
                {
                    var f = chords[c][v];
                    var gain = env * (v == 0 ? 0.11 : 0.075) * breath;
                    var phase = phases[c][v];
                    // The voice: a sine with a touch of its harmonics, detuned a little on each side for the width.
                    double Voice(double detune) => Math.Sin(Math.Tau * f * (1 + detune) * t + phase)
                                                   + 0.18 * Math.Sin(Math.Tau * 2 * f * (1 + detune) * t + phase * 1.3)
                                                   + 0.05 * Math.Sin(Math.Tau * 3 * f * t);
                    l += Voice(-0.0012) * gain;
                    r += Voice(0.0012) * gain;
                }
            }
            left[n] = (float)l;
            right[n] = (float)r;
        }
        // The bells: a note of the pentatonic scale every 2.5 to 5 s, soft, ringing for under 2 s, placed left or
        // right; none starts in the last 2 s of the loop, so that the loop stays seamless.
        double[] scale = [440.00, 523.25, 587.33, 659.25, 783.99, 880.00];
        var bells = new Random(5);
        for (var at = 1.5; at < loop - 2.2; at += 2.5 + bells.NextDouble() * 2.5)
        {
            var f = scale[bells.Next(scale.Length)];
            var pan = bells.NextDouble() * 1.4 - 0.7;
            var start = (int)(at * Rate);
            var length = (int)(1.8 * Rate);
            for (var k = 0; k < length && start + k < frames; k++)
            {
                var tt = (double)k / Rate;
                var ring = Math.Exp(-tt * 3.2) * (1 - Math.Exp(-tt * 60));
                var tone = (Math.Sin(Math.Tau * f * tt) + 0.3 * Math.Sin(Math.Tau * f * 2.76 * tt) * Math.Exp(-tt * 4)) * 0.07 * ring;
                left[start + k] += (float)(tone * (1 - Math.Max(0, pan)));
                right[start + k] += (float)(tone * (1 + Math.Min(0, pan)));
            }
        }
        // 16-bit stereo, a soft limiter against the rare peaks.
        var data = new byte[frames * 4];
        for (var n = 0; n < frames; n++)
        {
            var sl = (short)(Math.Tanh(left[n] * 1.4) * 0.9 * short.MaxValue);
            var sr = (short)(Math.Tanh(right[n] * 1.4) * 0.9 * short.MaxValue);
            data[n * 4] = (byte)(sl & 0xFF);
            data[n * 4 + 1] = (byte)((sl >> 8) & 0xFF);
            data[n * 4 + 2] = (byte)(sr & 0xFF);
            data[n * 4 + 3] = (byte)((sr >> 8) & 0xFF);
        }
        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = true, Data = data,
            LoopMode = AudioStreamWav.LoopModeEnum.Forward, LoopBegin = 0, LoopEnd = frames
        };
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        // The dots of "think …" breathe: none, one, two, three, every 400 ms.
        var dots = (int)(_clock / 0.4) % 4;
        _title.Text = "think " + new string('.', dots) + new string(' ', 3 - dots);
        // The cloud turns slowly; each word and picture drifts around its place; the camera sways a little.
        var t = (float)_clock;
        _cloud.Rotation = new Vector3(MathF.Sin(t * 0.07f) * 0.12f, t * Turn, 0);
        foreach (var (node, home, phase, range) in _floating)
            node.Position = home + new Vector3(
                MathF.Sin(t * 0.31f + phase.X) * range,
                MathF.Sin(t * 0.23f + phase.Y) * range * 0.7f,
                MathF.Sin(t * 0.19f + phase.Z) * range * 0.5f);
        _camera.Position = new Vector3(MathF.Sin(t * 0.11f) * 1.2f, MathF.Sin(t * 0.09f) * 0.8f, CameraDistance + MathF.Sin(t * 0.05f) * 1.5f);
        _camera.LookAt(Vector3.Zero, Vector3.Up);
        // The words of the detected entities wander over the whole screen.
        Wander((float)delta);
        // The entities of the ontology breathe: their halo brightens and they grow a little, each at its own pace.
        foreach (var (label, phase) in _entities)
        {
            var breath = 0.5f + 0.5f * MathF.Sin(t * 1.4f + phase);
            label.OutlineModulate = Gold with { A = 0.18f + 0.4f * breath };
            label.Scale = Vector3.One * (1f + 0.1f * breath);
        }
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true } or InputEventMouseButton { Pressed: true })
        {
            GetViewport().SetInputAsHandled();
            Leave();
        }
    }

    private void Leave()
    {
        if (_leaving)
            return;
        _leaving = true;
        var tween = CreateTween().SetParallel();
        tween.TweenProperty(this, "modulate:a", 0f, 0.4);
        if (_music is not null)
            tween.TweenProperty(_music, "volume_db", -60f, 0.6);
        tween.Chain().TweenCallback(Callable.From(() =>
        {
            Finished?.Invoke();
            QueueFree();
        }));
    }

    // ── The last hour ──

    private async Task LoadAsync()
    {
        var since = DateTime.UtcNow.AddMinutes(-Minutes);
        static DateTime Utc(DateTime date) => date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : date;
        async Task<T> Read<T>(Func<Task<T>> read, T empty, string what)
        {
            try
            {
                return await read();
            }
            catch (Exception ex)
            {
                ActivityLog.Error("Think", $"The {what} could not be read", ex);
                return empty;
            }
        }

        // What the dialogs of the hour brought out: the entities, the symbols and the tags detected, counted.
        var entities = await Read(() => _assistant.GetDetectedEntitiesTopAsync(Minutes, EntityWords), (IReadOnlyList<DetectedEntityCount>)[], "detected entities");
        var symbols = await Read(() => _assistant.GetDetectedSymbolsTopAsync(Minutes, SymbolWords), (IReadOnlyList<DetectedSymbolCount>)[], "detected symbols");
        var tags = await Read(() => _assistant.GetDetectedTagsTopAsync(Minutes, TagWords), (IReadOnlyList<DetectedTagCount>)[], "detected tags");
        // The messages of the ticker (the ones it shows now, whatever their date), the most important first.
        var messages = (await Read(() => _assistant.GetLastMessageFlowsAsync("*", 50), (IReadOnlyList<MessageFlowEntry>)[], "messages of the ticker"))
            .Where(m => !string.IsNullOrWhiteSpace(m.Text))
            .OrderByDescending(m => m.Priority).ThenByDescending(m => m.Date ?? DateTime.MinValue)
            .Take(Messages)
            .ToList();
        // The annotations of the hour: their text, and what they annotate.
        var items = new List<ThinkItem>();
        var annotationTexts = new List<string>();
        foreach (var annotation in await Read(() => _assistant.SearchAnnotationsAsync("*", 200), new List<AnnotationEntry>(), "annotations"))
            if (annotation.Date is { } date && Utc(date) >= since)
            {
                var text = annotation.Text.Trim().Length > 0 ? annotation.Text : string.Join(' ', annotation.Tags);
                items.Add(new ThinkItem("ANNOTATION", annotation.TargetTitle, text, Utc(date), Kind.Annotation));
                annotationTexts.Add(annotation.Text);
            }
        // The most frequent words of their text join the cloud (the stop words, numbers and short words left out).
        var annotationTerms = WordCloud.TopTerms(annotationTexts.Select(t => WithoutIds(t)), AnnotationWords);
        // Whole sentences, never an id (the ids written in the texts are taken away), each text once, the newest first.
        var cards = items
            .Select(i => i with { Heading = Clean(i.Heading), Body = Sentences(Clean(i.Body) ?? string.Empty) })
            .Where(i => i.Body.Length > 0 || i.Heading is { Length: > 0 })
            .DistinctBy(i => (i.Heading, i.Body))
            .OrderByDescending(i => i.Date ?? DateTime.MinValue)
            .Take(CloudItems)
            .ToList();
        // The pictures of the image analyses of the hour.
        var hits = await Read(() => _assistant.GetRecentAsync(Minutes, ["imageanalysis"], 40), (IReadOnlyList<SearchHit>)[], "image analyses");
        var pictures = hits.Select(ImagePathOf).Where(p => p is not null && File.Exists(p)).Select(p => p!).Distinct().Take(Pictures).ToList();

        var words = MergeWords(entities, symbols, tags, annotationTerms, messages);
        if (!IsInsideTree() || _leaving)
            return;
        Bind.OnMain(() =>
        {
            if (!IsInstanceValid(this) || _leaving)
                return;
            if (words.Count == 0 && cards.Count == 0 && pictures.Count == 0)
                Place(new Label3D { Text = "nothing in the last hour", Font = Eve.Display, FontSize = 48, PixelSize = 0.02f, Modulate = new Color(1, 1, 1, 0.5f), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Shaded = false }, Vector3.Zero, 0.2f, 0);
            ShowCloud(cards);
            ShowWords(words);
            ShowPictures(pictures);
            _footer.Text = $"the last {Minutes} minutes  ·  {entities.Count} detected entit{(entities.Count == 1 ? "y" : "ies")}  ·  {symbols.Count} symbol(s): sad in blue, happy in gold, fear in red, disgust in green  ·  {tags.Count} tag(s)  ·  {messages.Count} message(s) of the ticker  ·  {cards.Count} annotation(s), their words in lilac  ·  {pictures.Count} picture(s)  ·  a key or a click brings the chat back";
        });
    }

    private enum Kind { Document, Annotation, Activity }

    /// <summary>A card of the cloud: its kind (IDEA, ANNOTATION, SAVE…), its heading, its text in whole sentences.</summary>
    private sealed record ThinkItem(string Label, string? Heading, string Body, DateTime? Date, Kind Kind, bool Entity = false);

    /// <summary>A text on one line, its ids taken away, its spaces single; null when nothing is left.</summary>
    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var clean = System.Text.RegularExpressions.Regex.Replace(WithoutIds(text)!, @"\s+", " ").Trim();
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>
    /// The whole sentences of <paramref name="text"/> that fit in about 300 characters (at least the first, whole):
    /// a sentence is never cut; "…" says that more sentences follow.
    /// </summary>
    private static string Sentences(string text)
    {
        if (text.Length <= MaxCharacters)
            return text;
        var sentences = System.Text.RegularExpressions.Regex.Split(text, @"(?<=[.!?…])\s+");
        var kept = new System.Text.StringBuilder();
        foreach (var sentence in sentences)
        {
            if (kept.Length > 0 && kept.Length + 1 + sentence.Length > MaxCharacters)
                return kept + " …";
            if (kept.Length > 0)
                kept.Append(' ');
            kept.Append(sentence);
        }
        return kept.ToString();
    }

    /// <summary>
    /// The GUIDs (and their pieces of hexadecimal digits) and the words mixing letters and digits (ids such as
    /// task_12, a3f9…) of a text, taken away.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex Ids = new(
        @"\b[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\b|\b(?=[\w-]*\d)(?=[\w-]*[A-Za-z])[\w-]+\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string? WithoutIds(string? text) => text is null ? null : Ids.Replace(text, " ");

    /// <summary>The path of the image of an image analysis (its source JSON), or null.</summary>
    private static string? ImagePathOf(SearchHit hit)
    {
        try
        {
            using var document = JsonDocument.Parse(hit.Source);
            return document.RootElement.TryGetProperty("imagePath", out var path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── The cloud ──

    /// <summary>
    /// The cards in a cloud, each a whole text: its kind and heading on the first line, then its sentences. The newest
    /// the largest and the nearest to the centre, the older further out, each at a random place of its shell; the texts
    /// in white, the annotations in amber, the activities in the accent colour, a text naming an entity of the ontology in
    /// gold with a halo that breathes. They settle one after the other.
    /// </summary>
    private void ShowCloud(IReadOnlyList<ThinkItem> cards)
    {
        if (cards.Count == 0)
            return;
        var random = new Random(cards.Count * 7919);
        for (var order = 0; order < cards.Count; order++)
        {
            var card = cards[order];
            // The newest weighs 1, the oldest shown about 0.
            var weight = cards.Count == 1 ? 1 : 1 - order / (double)(cards.Count - 1);
            var heading = card.Heading is { Length: > 0 } h ? $"{card.Label} · {h}" : card.Label;
            var label = new Label3D
            {
                Text = card.Body.Length > 0 ? $"{heading}\n{card.Body}" : heading,
                Font = Eve.Body,
                FontSize = (int)Math.Round(26 + 18 * weight),
                PixelSize = 0.0065f,
                Width = 1100,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                HorizontalAlignment = HorizontalAlignment.Left,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Shaded = false,
                NoDepthTest = false,
                AlphaCut = Label3D.AlphaCutMode.Disabled,
                Modulate = new Color(1, 1, 1, 0)
            };
            var tint = card.Entity ? Gold : card.Kind switch
            {
                Kind.Annotation => Eve.Amber,
                Kind.Activity => Eve.Cyan,
                _ => Colors.White
            };
            var alpha = card.Entity ? 1f : 0.45f + 0.55f * (float)weight;
            if (card.Entity)
            {
                // A text naming an entity of the ontology: a halo of gold that breathes (see _Process), nearer the front.
                label.OutlineSize = 10;
                label.OutlineModulate = Gold with { A = 0.3f };
                _entities.Add((label, (float)random.NextDouble() * 6));
            }
            // The place: a random direction, at a distance that grows with the age of the card (the newest inside).
            var direction = new Vector3((float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 1.4f - 0.7f, (float)random.NextDouble() * 2 - 1).Normalized();
            var distance = (float)(CloudRadius * (0.2 + 0.8 * (1 - weight))) * (0.85f + (float)random.NextDouble() * 0.3f) * (card.Entity ? 0.8f : 1f);
            Place(label, direction * distance, 0.25f + (float)random.NextDouble() * 0.3f, order);
            CreateTween().TweenProperty(label, "modulate", new Color(tint.R, tint.G, tint.B, alpha), 0.8).SetDelay(0.06 * order);
        }
    }

    // ── The detected entities ──

    /// <summary>
    /// The entities detected by the NER in the dialogs of the hour and the terms of the white notes (in lilac), as a word
    /// cloud filling the whole screen in front of the cards: the weight of a word grows with the number of times it was found (on a log scale, the most frequent
    /// weighs 1), and with it its size and its first place near the centre; its opacity waves (see PairOpacities). The colour tells the type: gold for an entity of the ontology,
    /// cyan for a person, amber for an organization, green for a place, a soft white for the rest (dates, URLs, amounts…).
    /// </summary>
    private void ShowWords(IReadOnlyList<CloudWord> entities)
    {
        if (entities.Count == 0)
            return;
        // The words lie on a plane facing the camera that does not turn with the cards: a word cloud laid out flat (the
        // most frequent first, at the centre, each next one at the first free place along a spiral of the shape of the
        // screen), then scaled as a whole to fill all the view of the camera, its margins aside (the title, the footer).
        var words = new Node3D { Position = new Vector3(0, 0, WordsDepth) };
        _scene.AddChild(words);
        var aspect = Size.Y > 0 ? Size.X / Size.Y : 16f / 9f;
        var max = Math.Log(1 + entities.Max(e => e.Count));
        var placed = new List<(Label3D Label, Rect2 Box, Color Tint, bool Long)>();
        foreach (var entity in entities)
        {
            var weight = max <= 0 ? 1 : Math.Log(1 + entity.Count) / max;
            var label = new Label3D
            {
                Text = entity.Text,
                Font = Eve.Display,
                FontSize = (int)Math.Round(36 + 84 * weight),
                PixelSize = WordPixel,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Shaded = false,
                NoDepthTest = false,
                AlphaCut = Label3D.AlphaCutMode.Disabled,
                OutlineSize = weight > 0.6 ? 8 : 0,
                Modulate = new Color(1, 1, 1, 0)
            };
            var tint = entity.Tint;
            if (label.OutlineSize > 0)
                label.OutlineModulate = tint with { A = 0.25f };
            // The size of the word in the units of the plane, with a little room around it.
            var pixels = Eve.Display.GetStringSize(entity.Text, HorizontalAlignment.Left, -1, label.FontSize);
            var size = new Vector2(pixels.X, Math.Max(pixels.Y, label.FontSize)) * WordPixel * 1.12f;
            // A message of the ticker, or a long text (more than LongWord characters), is not laid out in the cloud: it
            // crosses the screen on a lane of its own.
            var isLong = entity.Lane || entity.Text.Length > LongWord;
            var box = isLong ? new Rect2(-size / 2, size) : FreePlace(size, aspect, placed.Where(p => !p.Long).Select(p => p.Box));
            placed.Add((label, box, tint, isLong));
        }

        // The cloud of the short words scaled (sizes and places) to the visible rectangle of the plane: the half height the
        // camera sees at that distance, the width of the screen, 8 % kept for the title, the footer and the sway of the camera.
        var halfHeight = (float)((CameraDistance - WordsDepth) * Math.Tan(Mathf.DegToRad(_camera.Fov) / 2));
        var visible = new Vector2(halfHeight * 2 * aspect * 0.94f, halfHeight * 2 * 0.86f);
        var shorts = placed.Where(p => !p.Long).Select(p => p.Box).ToList();
        var bounds = shorts.Count > 0 ? shorts.Aggregate((a, b) => a.Merge(b)) : new Rect2(-visible / 2, visible);
        var scale = Math.Min(MaxWordScale, Math.Min(visible.X / bounds.Size.X, visible.Y / bounds.Size.Y));
        var centre = bounds.GetCenter();
        var random = new Random(entities.Count * 4243 + 11);
        _wordsArea = visible / 2;
        var smallestFont = placed.Min(p => p.Label.FontSize);
        // The lanes of the long texts: the height of the screen shared between them, one each, so that they never cover
        // one another; from top to bottom, the most frequent first.
        var longs = placed.Count(p => p.Long);
        var laneHeight = longs > 0 ? visible.Y / longs : 0;
        var lane = 0;
        for (var order = 0; order < placed.Count; order++)
        {
            var (label, box, tint, isLong) = placed[order];
            // The speed in inverse proportion to the size of the font: a word twice as large goes half as fast.
            var speed = Math.Max(WordSpeedMin, WordSpeedMax * smallestFont / label.FontSize);
            // A long text no higher than 85 % of its lane.
            var wordScale = isLong ? Math.Min(scale, laneHeight * 0.85f / box.Size.Y) : scale;
            // The font scaled rather than its pixels: a large word stays sharp.
            label.FontSize = Math.Clamp((int)Math.Round(label.FontSize * wordScale), 8, 512);
            label.OutlineSize = (int)Math.Round(label.OutlineSize * wordScale);
            var half = box.Size * wordScale / 2;
            var wanderer = new Wanderer(label, half, speed, new Random(order * 7717 + 5))
            {
                Tint = tint,
                Born = _clock + 0.3 + 0.05 * order
            };
            // A small depth of its own for each word, so that they do not z-fight.
            var depth = (float)random.NextDouble() * 0.6f - 0.3f;
            if (isLong)
            {
                // Its lane, crossing the screen from outside: the even lanes from the left, the odd ones from the right,
                // each starting at its own distance from the edge so that they do not come in together.
                wanderer.LaneY = _wordsArea.Y - laneHeight * (lane + 0.5f);
                wanderer.Direction = lane % 2 == 0 ? 1 : -1;
                var start = _wordsArea.X + half.X + (float)random.NextDouble() * _wordsArea.X;
                label.Position = new Vector3(-wanderer.Direction * start, wanderer.LaneY.Value, depth);
                wanderer.Born = _clock;
                lane++;
            }
            else
            {
                // Godot's y goes up, the layout's down.
                var home = (box.GetCenter() - centre) * scale;
                label.Position = new Vector3(home.X, -home.Y, depth);
                // From its place in the cloud, the word then wanders over the whole screen (see Wander).
                wanderer.Target = wanderer.NextTarget(_wordsArea);
            }
            words.AddChild(label);
            _wanderers.Add(wanderer);
        }
        PairOpacities(random);
    }

    /// <summary>A text longer than this (in characters) crosses the screen on a lane of its own, from outside.</summary>
    private const int LongWord = 50;

    /// <summary>
    /// The opacity of each word goes from 0 to 100 % and back, but always by pairs in opposition: the two words of a pair
    /// share a pace and a phase, one at 50 % + x when the other is at 50 % − x. So at any time half of the words are at
    /// 50 % or more and half at 50 % or less. The words are paired at random; an odd one out stays at 50 %.
    /// </summary>
    private void PairOpacities(Random random)
    {
        var order = _wanderers.OrderBy(_ => random.Next()).ToList();
        for (var i = 0; i + 1 < order.Count; i += 2)
        {
            var phase = (float)(random.NextDouble() * Math.Tau);
            // A full wave (0 → 100 % → 0) in 8 to 16 seconds.
            var pace = (float)(Math.Tau / (8 + random.NextDouble() * 8));
            foreach (var (w, sign) in new[] { (order[i], 1f), (order[i + 1], -1f) })
            {
                w.Phase = phase;
                w.Pace = pace;
                w.Sign = sign;
            }
        }
    }

    /// <summary>The slowest a word goes, in units of the plane per second (the screen is about 35 wide).</summary>
    private const float WordSpeedMin = 0.3f;
    /// <summary>The speed of the smallest word; the others in inverse proportion to their font.</summary>
    private const float WordSpeedMax = 1.4f;

    /// <summary>The words that wander, and the half size of the visible rectangle of their plane.</summary>
    private readonly List<Wanderer> _wanderers = [];
    private Vector2 _wordsArea;

    /// <summary>
    /// A word that wanders over the screen: it heads for a point of the visible rectangle, turning smoothly, and once
    /// there picks another, the farthest of a few drawn at random, so that little by little it goes everywhere.
    /// </summary>
    private sealed class Wanderer(Label3D label, Vector2 half, float speed, Random random)
    {
        public Label3D Label { get; } = label;
        public Vector2 Half { get; } = half;
        public float Speed { get; } = speed;
        public Vector2 Target { get; set; }
        public Vector2 Velocity { get; set; }
        /// <summary>The colour of the word (its opacity set at each frame).</summary>
        public Color Tint { get; set; } = Colors.White;
        /// <summary>When it appears (clock of the screen), fading in for 0.8 s.</summary>
        public double Born { get; set; }
        /// <summary>The wave of its opacity: phase, pace (radians per second), and +1 / −1 in its pair (0: always 50 %).</summary>
        public float Phase { get; set; }
        public float Pace { get; set; }
        public float Sign { get; set; }
        /// <summary>A long text: the height of its lane (null for a word that wanders), and its way (+1 rightward, −1 leftward).</summary>
        public float? LaneY { get; set; }
        public int Direction { get; set; } = 1;

        /// <summary>The opacity at <paramref name="clock"/>: 50 % ± 50 % along the wave, times the fade-in.</summary>
        public float Opacity(double clock)
        {
            var wave = 0.5f + 0.5f * Sign * MathF.Sin(Pace * (float)clock + Phase);
            return wave * (float)Math.Clamp((clock - Born) / 0.8, 0, 1);
        }

        /// <summary>The farthest of three points drawn in the rectangle where the word fits whole.</summary>
        public Vector2 NextTarget(Vector2 area)
        {
            var room = new Vector2(Math.Max(0, area.X - Half.X), Math.Max(0, area.Y - Half.Y));
            var here = new Vector2(Label.Position.X, Label.Position.Y);
            Vector2 Draw() => new((float)(random.NextDouble() * 2 - 1) * room.X, (float)(random.NextDouble() * 2 - 1) * room.Y);
            return Enumerable.Range(0, 3).Select(_ => Draw()).OrderByDescending(p => p.DistanceSquaredTo(here)).First();
        }
    }

    /// <summary>
    /// Moves each word toward its target, turning smoothly, kept inside the screen; a long text along its lane, from one
    /// side to the other, coming back from outside once it has left the screen whole.
    /// </summary>
    private void Wander(float delta)
    {
        foreach (var w in _wanderers)
        {
            if (w.LaneY is { } laneY)
            {
                var x = w.Label.Position.X + w.Direction * w.Speed * delta;
                var limit = _wordsArea.X + w.Half.X;
                if (w.Direction * x > limit)
                    x = -w.Direction * limit;
                w.Label.Position = new Vector3(x, laneY, w.Label.Position.Z);
                w.Label.Modulate = w.Tint with { A = w.Opacity(_clock) };
                continue;
            }
            var position = new Vector2(w.Label.Position.X, w.Label.Position.Y);
            var toTarget = w.Target - position;
            if (toTarget.Length() < 0.6f)
            {
                w.Target = w.NextTarget(_wordsArea);
                toTarget = w.Target - position;
            }
            w.Velocity = w.Velocity.Lerp(toTarget.Normalized() * w.Speed, Math.Min(1f, delta * 0.9f));
            position += w.Velocity * delta;
            var room = new Vector2(Math.Max(0, _wordsArea.X - w.Half.X), Math.Max(0, _wordsArea.Y - w.Half.Y));
            position = position.Clamp(-room, room);
            w.Label.Position = new Vector3(position.X, position.Y, w.Label.Position.Z);
            w.Label.Modulate = w.Tint with { A = w.Opacity(_clock) };
        }
    }

    /// <summary>The distance of the camera to the centre of the scene (it sways around it, see _Process).</summary>
    private const float CameraDistance = 22f;
    /// <summary>The plane of the words, a little in front of the centre of the cards.</summary>
    private const float WordsDepth = 2f;
    /// <summary>The size of a pixel of a word before the cloud is scaled to the screen.</summary>
    private const float WordPixel = 0.008f;
    /// <summary>A few words are not blown up beyond this.</summary>
    private const float MaxWordScale = 6f;

    /// <summary>
    /// The first place, along a spiral from the centre stretched to <paramref name="aspect"/>, where a box of
    /// <paramref name="size"/> overlaps none of <paramref name="taken"/> (y going down, as on the screen).
    /// </summary>
    private static Rect2 FreePlace(Vector2 size, float aspect, IEnumerable<Rect2> taken)
    {
        var boxes = taken.ToList();
        var step = Math.Max(0.02f, Math.Min(size.X, size.Y) * 0.25f);
        for (var i = 0; i < 20000; i++)
        {
            var angle = i * 0.35f;
            var radius = step * angle / MathF.Tau;
            var centre = new Vector2(MathF.Cos(angle) * radius * aspect, MathF.Sin(angle) * radius);
            var box = new Rect2(centre - size / 2, size);
            if (!boxes.Any(b => b.Intersects(box)))
                return box;
        }
        // Never reached in practice: beside everything.
        var right = boxes.Count == 0 ? 0 : boxes.Max(b => b.End.X);
        return new Rect2(new Vector2(right, -size.Y / 2), size);
    }

    /// <summary>A word of the cloud: its text, how many times it was found, its colour; on a lane of its own (a message of the ticker).</summary>
    private sealed record CloudWord(string Text, int Count, Color Tint, bool Lane = false);

    /// <summary>The colours of the signals of the symbols, and of the tags.</summary>
    private static readonly Color SadTint = new(0.45f, 0.62f, 1f);
    private static readonly Color HappyTint = new(1f, 0.82f, 0.3f);
    private static readonly Color FearTint = new(1f, 0.38f, 0.42f);
    private static readonly Color DisgustTint = new(0.6f, 0.85f, 0.3f);
    private static readonly Color TagTint = new(0.72f, 0.85f, 0.92f);
    /// <summary>The colour of the words of the annotations: a pale lilac.</summary>
    private static readonly Color AnnotationTint = new(0.82f, 0.7f, 1f);

    /// <summary>
    /// The words of the cloud: the detected entities, the symbols, the tags and the most frequent words of the text of
    /// the annotations; a word found in several of them (the same text) adds up its counts and keeps the colour of the
    /// first. Then the messages of the ticker, on lanes. The most frequent first.
    /// </summary>
    private static List<CloudWord> MergeWords(IReadOnlyList<DetectedEntityCount> entities, IReadOnlyList<DetectedSymbolCount> symbols,
        IReadOnlyList<DetectedTagCount> tags, IReadOnlyList<(string Term, int Count)> annotationTerms, IReadOnlyList<MessageFlowEntry> messages)
    {
        var words = new Dictionary<string, CloudWord>(StringComparer.OrdinalIgnoreCase);
        void Add(string text, int count, Color tint)
        {
            text = text.Trim();
            if (text.Length == 0)
                return;
            words[text] = words.TryGetValue(text, out var known) ? known with { Count = known.Count + count } : new CloudWord(text, count, tint);
        }
        foreach (var entity in entities)
            Add(entity.Text, entity.Count, EntityTint(entity));
        foreach (var symbol in symbols)
            Add(symbol.Symbol, symbol.Count, SignalTint(symbol.Signals));
        foreach (var tag in tags)
            Add(tag.Tag, tag.Count, TagTint);
        foreach (var (term, count) in annotationTerms)
            Add(term, count, AnnotationTint);
        var cloud = words.Values.OrderByDescending(w => w.Count).ThenBy(w => w.Text, StringComparer.OrdinalIgnoreCase).ToList();
        // The messages weigh as the most frequent word, so that they are well read.
        var weight = cloud.Count > 0 ? cloud.Max(w => w.Count) : 1;
        cloud.AddRange(messages.Select(m => new CloudWord(m.Text!.Trim(), weight, MessageTint(m.Color), Lane: true)));
        return cloud;
    }

    /// <summary>The colour of a symbol by its first known signal.</summary>
    private static Color SignalTint(IEnumerable<string> signals)
    {
        foreach (var signal in signals)
            switch (signal.ToUpperInvariant())
            {
                case "!SAD": return SadTint;
                case "!HAPPY": return HappyTint;
                case "!FEAR": return FearTint;
                case "!DISGUST": return DisgustTint;
            }
        return Colors.White;
    }

    /// <summary>The colour of a message of the ticker: red, green, or amber.</summary>
    private static Color MessageTint(string? color) => color?.Trim().ToLowerInvariant() switch
    {
        "red" => new Color(1f, 0.42f, 0.42f),
        "green" => new Color(0.5f, 0.95f, 0.6f),
        _ => Eve.Amber
    };

    /// <summary>The colour of an entity by its type (gold when it is an entity of the ontology).</summary>
    private static Color EntityTint(DetectedEntityCount entity)
    {
        if (entity.OntologyId is { Length: > 0 })
            return Gold;
        return entity.Type.ToUpperInvariant() switch
        {
            "PERSON" or "PER" => Eve.Cyan,
            "ORG" or "ORGANIZATION" => Eve.Amber,
            "LOC" or "LOCATION" or "CITY" or "COUNTRY" or "GPE" => new Color(0.55f, 0.9f, 0.6f),
            _ => new Color(0.85f, 0.88f, 0.95f)
        };
    }

    /// <summary>Adds <paramref name="node"/> to the cloud at <paramref name="home"/>, drifting around it by <paramref name="range"/>.</summary>
    private void Place(Node3D node, Vector3 home, float range, int order)
    {
        node.Position = home;
        _cloud.AddChild(node);
        var random = new Random(order * 31 + 17);
        _floating.Add((node, home, new Vector3((float)random.NextDouble() * 6, (float)random.NextDouble() * 6, (float)random.NextDouble() * 6), range));
    }

    // ── The pictures ──

    /// <summary>The pictures around the cloud, on a wide ring a little behind it, each one facing the camera.</summary>
    private void ShowPictures(List<string> pictures)
    {
        var order = 0;
        var random = new Random(pictures.Count * 977 + 3);
        foreach (var path in pictures)
        {
            var image = new Image();
            if (image.Load(path) != Error.Ok)
                continue;
            var scale = Math.Min(1f, 320f / Math.Max(1, Math.Max(image.GetWidth(), image.GetHeight())));
            if (scale < 1)
                image.Resize((int)(image.GetWidth() * scale), (int)(image.GetHeight() * scale), Image.Interpolation.Lanczos);
            var sprite = new Sprite3D
            {
                Texture = ImageTexture.CreateFromImage(image),
                PixelSize = 0.011f,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Shaded = false,
                Transparent = true,
                Modulate = new Color(1, 1, 1, 0)
            };
            // On a ring around the cloud, a little above or below, the newest nearer the front.
            var angle = (float)(order * Math.Tau / Math.Max(1, pictures.Count)) + (float)random.NextDouble() * 0.3f;
            var ring = CloudRadius * 1.25f;
            var home = new Vector3(MathF.Cos(angle) * ring, (float)random.NextDouble() * 6 - 3, MathF.Sin(angle) * ring * 0.8f);
            Place(sprite, home, 0.5f, 100 + order);
            CreateTween().TweenProperty(sprite, "modulate", new Color(1, 1, 1, 0.92f), 0.8).SetDelay(0.6 + 0.1 * order);
            // Its size breathes: it grows twice as large, then shrinks by half, then grows back to twice, and
            // so on; each picture a little after the one before, so that they do not breathe together.
            // (The loop starts after its delay: a delay inside a looping tween would come back at each breath.)
            var picture = sprite;
            sprite.CreateTween().TweenCallback(Callable.From(() =>
            {
                if (!IsInstanceValid(picture))
                    return;
                var breath = picture.CreateTween().SetLoops().SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
                breath.TweenProperty(picture, "scale", Vector3.One * PictureGrowth, PictureBreath);
                breath.TweenProperty(picture, "scale", Vector3.One * PictureGrowth / 2, PictureBreath);
            })).SetDelay(0.6 + 0.4 * order);
            order++;
        }
    }
}
