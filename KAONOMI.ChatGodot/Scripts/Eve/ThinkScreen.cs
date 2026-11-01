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
/// <c>/think</c>: the screen goes black over everything, "think …" at the top (its dots breathing), and the last hour
/// of KAONOMI floats in 3D: a cloud of whole texts, never cut into words: the documents written in the hour (cache,
/// completions, dialogs, diary, documents, ideas, intel, ontology, relations, tasks, YouTube videos…: <c>POST /recent</c>),
/// each with its kind, its title and its whole sentences; the annotations of the hour (their text, what they annotate);
/// the activities of the journal (what was saved, and where); the newest the largest and the nearest, in a cloud that
/// turns slowly while each card drifts on its own, and the pictures of the image analyses of the hour, floating around
/// it. No id is ever shown. A text that names an entity of the ontology (its name or a synonym) stands out: in gold,
/// nearer the front, with a halo and a breath of its own. A black fog
/// gives the depth. A quiet music plays meanwhile, made by the code (no file): a pad of four calm chords that follow one
/// another slowly, a few bell notes of the pentatonic scale now and then, in a large reverb. A key or a click brings the
/// chat back (the music fades with the screen).
/// </summary>
public partial class ThinkScreen : Control
{
    private const int Minutes = 60;
    /// <summary>The cards of the cloud (texts, annotations, activities), the newest first.</summary>
    private const int CloudItems = 36;
    /// <summary>The length of a card, in whole sentences.</summary>
    private const int MaxCharacters = 300;
    private const int Pictures = 16;
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
        _camera.Position = new Vector3(0, 0, 22);
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
        _camera.Position = new Vector3(MathF.Sin(t * 0.11f) * 1.2f, MathF.Sin(t * 0.09f) * 0.8f, 22 + MathF.Sin(t * 0.05f) * 1.5f);
        _camera.LookAt(Vector3.Zero, Vector3.Up);
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
        IReadOnlyList<SearchHit> hits = [];
        try
        {
            hits = await _assistant.GetRecentAsync(Minutes, SearchWindowViewModel.AllIndices, 40);
        }
        catch (Exception ex)
        {
            ActivityLog.Error("Think", "The entities of the last hour could not be read", ex);
        }
        var since = DateTime.UtcNow.AddMinutes(-Minutes);
        static DateTime Utc(DateTime date) => date.Kind == DateTimeKind.Local ? date.ToUniversalTime() : date;
        // The documents of the hour: their kind, their title (never when it is only their id), their text.
        var items = hits.Where(h => h.Index != "imageanalysis" || h.Text.Length > 0)
            .Select(h => new ThinkItem(h.Index.ToUpperInvariant(), h.Title == h.Id ? null : h.Title, h.Text, h.Date is { } d ? Utc(d) : null, Kind.Document))
            .ToList();
        // The annotations of the hour: their text, and what they annotate.
        var annotations = 0;
        try
        {
            foreach (var annotation in await _assistant.SearchAnnotationsAsync("*", 200))
                if (annotation.Date is { } date && Utc(date) >= since)
                {
                    annotations++;
                    var text = annotation.Text.Trim().Length > 0 ? annotation.Text : string.Join(' ', annotation.Tags);
                    items.Add(new ThinkItem("ANNOTATION", annotation.TargetTitle, text, Utc(date), Kind.Annotation));
                }
        }
        catch (Exception ex)
        {
            ActivityLog.Error("Think", "The annotations of the last hour could not be read", ex);
        }
        // The activities of the journal of the hour: what was saved, and where.
        var activities = 0;
        try
        {
            foreach (var activity in await _assistant.GetUserActivitiesAsync("*", 200))
                if (activity.Date is { } date && date.UtcDateTime >= since)
                {
                    activities++;
                    items.Add(new ThinkItem(activity.Action, activity.WindowName, activity.Description, date.UtcDateTime, Kind.Activity));
                }
        }
        catch (Exception ex)
        {
            ActivityLog.Error("Think", "The activities of the last hour could not be read", ex);
        }
        // Whole sentences, never an id (the ids written in the texts are taken away), each text once, the newest first.
        var cards = items
            .Select(i => i with { Heading = Clean(i.Heading), Body = Sentences(Clean(i.Body) ?? string.Empty) })
            .Where(i => i.Body.Length > 0 || i.Heading is { Length: > 0 })
            .DistinctBy(i => (i.Heading, i.Body))
            .OrderByDescending(i => i.Date ?? DateTime.MinValue)
            .Take(CloudItems)
            .ToList();
        // The entities of the ontology (names and synonyms): a text that names one stands out in gold.
        var entities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var node in await _assistant.GetLastOntologiesAsync("*", 1000))
            {
                if (node.IsOntologyClass)
                    continue;
                foreach (var name in new[] { node.Name }.Concat(node.Synonyms ?? []))
                    if (!string.IsNullOrWhiteSpace(name) && name.Trim().Length >= 3)
                        entities.Add(name.Trim());
            }
        }
        catch (Exception ex)
        {
            ActivityLog.Error("Think", "The entities of the ontology could not be read", ex);
        }
        if (entities.Count > 0)
            cards = cards.Select(c => c with { Entity = entities.Any(e => Names(c.Heading + "\n" + c.Body, e)) }).ToList();
        var pictures = hits.Where(h => h.Index == "imageanalysis").Select(ImagePathOf).Where(p => p is not null && File.Exists(p)).Select(p => p!).Distinct().Take(Pictures).ToList();
        if (!IsInsideTree() || _leaving)
            return;
        Bind.OnMain(() =>
        {
            if (!IsInstanceValid(this) || _leaving)
                return;
            ShowCloud(cards);
            ShowPictures(pictures);
            var documents = hits.Count(h => h.Index != "imageanalysis");
            var known = cards.Count(c => c.Entity);
            _footer.Text = $"the last {Minutes} minutes  ·  {documents} text(s)  ·  {annotations} annotation(s)  ·  {activities} activit{(activities == 1 ? "y" : "ies")}  ·  {pictures.Count} picture(s)  ·  {known} naming an entity of the ontology, in gold  ·  a key or a click brings the chat back";
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

    /// <summary>True when <paramref name="text"/> names <paramref name="name"/> as a whole word.</summary>
    private static bool Names(string text, string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, $@"(?<!\w){System.Text.RegularExpressions.Regex.Escape(name)}(?!\w)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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
        {
            Place(new Label3D { Text = "nothing in the last hour", Font = Eve.Display, FontSize = 48, PixelSize = 0.02f, Modulate = new Color(1, 1, 1, 0.5f), Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Shaded = false }, Vector3.Zero, 0.2f, 0);
            return;
        }
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
            order++;
        }
    }
}
