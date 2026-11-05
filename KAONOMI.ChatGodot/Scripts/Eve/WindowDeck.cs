using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.ChatGodot.Tools.Knowledge;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// A window of the desktop: its frame and content live in a viewport of their own, shown flat in front while it is in
/// use, or as the live picture of a card in 3D at the back of the desktop otherwise.
/// </summary>
public sealed class DeckWindow
{
    internal DeckWindow(string key, string title, string icon, Control content, object? viewModel)
    {
        Key = key;
        Icon = icon;
        Content = content;
        ViewModel = viewModel;
        Viewport = new SubViewport
        {
            TransparentBg = true,
            HandleInputLocally = true,
            // The drop-downs, menus and dialogs of the window are drawn in it (else they are not drawn at all).
            GuiEmbedSubwindows = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Msaa2D = Godot.Viewport.Msaa.Disabled
        };
        Frame = new EveFrame(title, icon, content);
        Viewport.AddChild(Frame);
        Container = new SubViewportContainer { Stretch = true, MouseFilter = Control.MouseFilterEnum.Stop };
    }

    /// <summary>What identifies the window (the command of its tool, or its object): opening it again brings it back.</summary>
    public string Key { get; }

    public string Icon { get; }

    public Control Content { get; }

    /// <summary>The view model shown (null for a window without one).</summary>
    public object? ViewModel { get; }

    public string Title
    {
        get => Frame.Title;
        set => Frame.Title = value;
    }

    /// <summary>A PNG picture of the window as it shows now, or null when it cannot be read.</summary>
    public byte[]? Picture()
    {
        try
        {
            return Viewport.GetTexture()?.GetImage() is { } image && !image.IsEmpty() ? image.SavePngToBuffer() : null;
        }
        catch (System.Exception)
        {
            return null;
        }
    }

    internal SubViewport Viewport { get; }
    internal EveFrame Frame { get; }
    internal SubViewportContainer Container { get; }

    /// <summary>The shadow of the window on the desktop, drawn behind it (null until it is placed).</summary>
    internal ColorRect? Shadow { get; set; }

    /// <summary>Where the window is in front, in the coordinates of the desktop.</summary>
    internal Rect2 Rect { get; set; }

    internal MeshInstance3D? Card { get; set; }
    internal Label3D? CardLabel { get; set; }
    internal ShaderMaterial? CardMaterial { get; set; }
    internal Tween? Motion { get; set; }

    /// <summary>The place of the card at the back (before it grows under the pointer).</summary>
    internal Transform3D Pose { get; set; }

    /// <summary>The size of the card at the back: 1, up to 1.3 under the pointer.</summary>
    internal float Grow { get; set; } = 1;

    /// <summary>The card growing or shrinking back.</summary>
    internal Tween? GrowMotion { get; set; }

    /// <summary>The place before the window took the whole desktop (null: it does not).</summary>
    internal Rect2? Restore { get; set; }

    /// <summary>True while the window takes the whole desktop.</summary>
    public bool IsMaximized => Restore is not null;

    /// <summary>The smallest size the window can be given by hand (its content may ask for more: the ribbon of the tape).</summary>
    public Vector2 MinSize { get; internal set; } = new(360, 240);

    /// <summary>True while the window is behind the others, faded and smaller (flicked up; a click brings it back).</summary>
    public bool IsFaded { get; internal set; }

    /// <summary>The window fading or coming back.</summary>
    internal Tween? FadeMotion { get; set; }

    /// <summary>When the window was last used (the cards at the back are ordered by it).</summary>
    internal ulong LastUsed { get; set; }

    /// <summary>True while the window is flat in front.</summary>
    public bool IsFront { get; internal set; }

    /// <summary>Raised when the window is closed.</summary>
    public event Action? Closed;

    internal void RaiseClosed() => Closed?.Invoke();
}

/// <summary>
/// The desktop between the bar of commands and the chat: the window in use is flat in front; the others go to the back,
/// on cards in 3D in a perspective (a row turned towards the middle, deeper for the windows unused for longer), still
/// showing their live content. A click on a card brings its window to the front (it flies forward), a click on the
/// empty desktop sends the window in front to the back; right click on a card: close it. A window is moved by its
/// header, resized by its corner, maximised by a double-click on its header; ◐ in its header puts it behind the
/// others (faded, smaller; a click brings it back).
/// </summary>
public partial class WindowDeck : Control
{
    private const float FieldOfView = 50;
    private const float FrontDistance = 10;
    private const float Duration = 0.38f;

    private readonly List<DeckWindow> _windows = [];
    private readonly Control _layer = new() { MouseFilter = MouseFilterEnum.Ignore };
    private readonly Node _holder = new() { Name = "Hidden" };
    private readonly TextureRect _wallpaper = new()
    {
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
        MouseFilter = MouseFilterEnum.Ignore
    };
    private SubViewportContainer _sceneContainer = null!;
    private SubViewport _scene = null!;
    private Camera3D _camera = null!;
    private Node3D _cards = null!;
    private DeckWindow? _front;
    private DeckWindow? _hovered;
    private ulong _clock;
    private double _refresh;

    /// <summary>The windows, in front or at the back.</summary>
    public IReadOnlyList<DeckWindow> Windows => _windows;

    /// <summary>The window in front, or null.</summary>
    public DeckWindow? Front => _front;

    /// <summary>Raised when the window in front changes.</summary>
    public event Action<DeckWindow?>? FrontChanged;

    /// <summary>Raised when "add a note" is clicked in the header of a window.</summary>
    public event Action<DeckWindow>? NoteRequested;

    /// <summary>📁+ in the header of a window: the window goes into a folder.</summary>
    public event Action<DeckWindow>? FolderRequested;

    /// <summary>❐ in the header of a window: clone it.</summary>
    public event Action<DeckWindow>? CloneRequested;

    /// <summary>A command asked by the menu of the empty desktop (/pictures, /think, a tool to open again).</summary>
    public event Action<string>? CommandRequested;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;

        // The wallpaper, behind the cards and the windows.
        _wallpaper.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_wallpaper);
        ShowWallpaper();

        // The 3D scene of the cards, behind the window in front, its background transparent (space shows through).
        var container = new SubViewportContainer { Stretch = true, MouseFilter = MouseFilterEnum.Ignore };
        container.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(container);
        _sceneContainer = container;
        _scene = new SubViewport { OwnWorld3D = true, TransparentBg = true, Msaa3D = Viewport.Msaa.Msaa4X, HandleInputLocally = false };
        container.AddChild(_scene);
        _camera = new Camera3D { Fov = FieldOfView, Current = true };
        _scene.AddChild(_camera);
        _cards = new Node3D();
        _scene.AddChild(_cards);

        _layer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_layer);
        AddChild(_holder);
        BuildStageArrows();
        // The pointer leaves the desktop: the card it was on comes back to its size.
        MouseExited += () =>
        {
            if (_hovered is not { } left)
                return;
            left.CardMaterial?.SetShaderParameter("hover", 0f);
            GrowTo(left, 1);
            _hovered = null;
        };
        Resized += () =>
        {
            if (_stage)
            {
                if (_shelf)
                    BuildShelf(animate: false);
                else if (_front is { } shown)
                    Place(shown, StageRect);
                PlaceStageArrows();
                return;
            }
            foreach (var window in _windows.Where(w => w.IsFront))
                Place(window, window.IsMaximized ? FullRect : Clamp(window.Rect));
            Arrange(animate: false);
        };
    }

    // Subscribed on entering the tree, not in _Ready: the voice mode takes the desktop and gives it back (out of the
    // tree and in again, _Ready not called again).
    public override void _EnterTree() => Wallpaper.Changed += ShowWallpaper;

    public override void _ExitTree() => Wallpaper.Changed -= ShowWallpaper;

    private void ShowWallpaper() => _wallpaper.Texture = Wallpaper.Load();

    public override void _Process(double delta)
    {
        // The cards at the back show their window as it is now, a few times a second.
        _refresh += delta;
        if (_refresh < 0.4)
            return;
        _refresh = 0;
        foreach (var window in _windows.Where(w => !w.IsFront && w.Motion is null))
            window.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
    }

    // ── Opening and closing ──

    /// <summary>The window of <paramref name="key"/>, or null.</summary>
    public DeckWindow? Find(string key) => _windows.FirstOrDefault(w => w.Key == key);

    /// <summary>
    /// Opens <paramref name="content"/> in a window in front (the window in front goes to the back); a window of the
    /// same <paramref name="key"/> already open is brought to the front instead (and <paramref name="content"/> freed).
    /// </summary>
    public DeckWindow Open(string key, string title, string icon, Control content, Vector2 size, object? viewModel = null)
    {
        if (Find(key) is { } existing)
        {
            if (!content.IsInsideTree())
                content.QueueFree();
            BringToFront(existing);
            return existing;
        }
        var window = new DeckWindow(key, title, icon, content, viewModel);
        // Moved or resized by hand, a window in full size is no longer in full size.
        window.Frame.Moved += delta => { Unmaximize(window); Place(window, Clamp(new Rect2(window.Rect.Position + delta, window.Rect.Size))); };
        window.Frame.Resized += delta => { Unmaximize(window); Place(window, Clamp(new Rect2(window.Rect.Position, (window.Rect.Size + delta).Max(window.MinSize)))); };
        window.Frame.MaximizeClicked += () => ToggleMaximized(window);
        window.Frame.CloseClicked += () => _ = RequestCloseAsync(window);
        window.Frame.BackClicked += () => SendToBack(window);
        window.Frame.HeaderDoubleClicked += () => ToggleMaximized(window);
        window.Frame.FadeClicked += () => Fade(window);
        window.Frame.Edged += (move, grow) => { Unmaximize(window); Place(window, Clamp(new Rect2(window.Rect.Position + move, (window.Rect.Size + grow).Max(window.MinSize)))); };
        window.Frame.NoteClicked += () => NoteRequested?.Invoke(window);
        window.Frame.FolderClicked += () => FolderRequested?.Invoke(window);
        window.Frame.CloneClicked += () => CloneRequested?.Invoke(window);
        // A tool (its key is its command) or a window of an object can be cloned; a window made of a control alone cannot.
        window.Frame.CanClone = key.StartsWith('/') || viewModel is not null;
        _windows.Add(window);
        _layer.AddChild(window.Container);
        window.Container.AddChild(window.Viewport);
        // A tool (its key is its command): remembered for "Last 3 windows" of the menu of the desktop.
        if (key.StartsWith('/'))
            Preferences.AddRecentWindow(key);

        // A little lower and to the right of the last one opened, centred the first time.
        var desktop = Size;
        var wanted = size.Min(desktop - new Vector2(40, 40)).Max(new Vector2(360, 240));
        var offset = (_windows.Count - 1) % 6 * 28;
        var rect = new Rect2((desktop - wanted) / 2 + new Vector2(offset - 70, offset - 70), wanted);
        Round(window);
        Place(window, Clamp(rect));
        window.IsFront = true;
        window.LastUsed = ++_clock;
        // The windows already in front stay there: only the double-click on a header (or ▁) sends one to the back.
        SetFront(window);
        if (_stage)
        {
            // On the stage it is the one shown; its place on the desktop is the one it gets back after.
            _beforeStage[window] = (window.Rect, true);
            ShowOnStage(window);
        }
        return window;
    }

    /// <summary>Closes <paramref name="window"/> (its content is freed).</summary>
    public void Close(DeckWindow window)
    {
        if (!_windows.Remove(window))
            return;
        window.Motion?.Kill();
        window.Motion = null;
        // The tweens of the card (its growth under the pointer, its fade) would run on a freed card.
        window.GrowMotion?.Kill();
        window.GrowMotion = null;
        window.FadeMotion?.Kill();
        window.FadeMotion = null;
        if (ReferenceEquals(_hovered, window))
            _hovered = null;
        window.Card?.QueueFree();
        window.CardLabel?.QueueFree();
        window.Card = null;
        window.CardLabel = null;
        window.CardMaterial = null;
        if (_stage && !_shelf && window.Container.Visible)
        {
            // Shown on the stage: it stays a moment under the one that replaces it, so that the animation behind does
            // not flash before that one is drawn.
            var container = window.Container;
            container.MouseFilter = Control.MouseFilterEnum.Ignore;
            GetTree().CreateTimer(0.15).Timeout += () =>
            {
                if (IsInstanceValid(container))
                    container.QueueFree();
            };
            if (window.Viewport.GetParent() != container)
                window.Viewport.QueueFree();
        }
        else
        {
            window.Container.QueueFree();
            window.Viewport.QueueFree();
        }
        if (_front == window)
            SetFront(_windows.Where(w => w.IsFront).OrderByDescending(w => w.LastUsed).FirstOrDefault());
        _beforeStage.Remove(window);
        if (_stage && _shelf && _windows.Count > 0)
            RemoveFromShelf(window);
        else if (_stage)
            ShowOnStage(_front);
        window.RaiseClosed();
        Arrange(animate: true);
    }

    /// <summary>Closes every window.</summary>
    public void CloseAll()
    {
        foreach (var window in _windows.ToList())
            Close(window);
    }

    // ── Changes not saved ──

    /// <summary>
    /// The windows whose view model has changes not saved (<see cref="KAONOMI.Chat.ViewModels.IUnsavedChanges"/>);
    /// a view model shown by several windows (a clone) counts once, under its first window.
    /// </summary>
    public List<DeckWindow> UnsavedWindows() =>
        _windows.Where(w => w.ViewModel is KAONOMI.Chat.ViewModels.IUnsavedChanges { IsDirty: true })
            .GroupBy(w => w.ViewModel, ReferenceEqualityComparer.Instance)
            .Select(g => g.First())
            .ToList();

    /// <summary>The titles of <paramref name="windows"/>, one per line, for a question.</summary>
    public static string Titles(IEnumerable<DeckWindow> windows) =>
        string.Join("\n", windows.Select(w => "• " + w.Title.TrimEnd(' ', '•')));

    /// <summary>
    /// The ✕ of a window: closed at once, unless its view model has changes not saved and no other window shows it
    /// (a clone keeps them): then the user is asked first. True when the window was closed.
    /// </summary>
    public async Task<bool> RequestCloseAsync(DeckWindow window)
    {
        if (window.ViewModel is KAONOMI.Chat.ViewModels.IUnsavedChanges { IsDirty: true } viewModel
            && _windows.Count(w => ReferenceEquals(w.ViewModel, viewModel)) == 1)
        {
            BringToFront(window);
            if (!await Dialogs.Confirm("Changes not saved",
                    $"« {window.Title.TrimEnd(' ', '•')} » has changes that are not saved: closing the window loses them.",
                    "Close without saving", "Cancel"))
                return false;
            ActivityLog.Info("Desktop", $"Window {window.Key} closed without saving its changes");
        }
        if (_windows.Contains(window))
            Close(window);
        return true;
    }

    /// <summary>
    /// <c>/closeall</c>: every window closed; the ones with changes not saved are named first, and kept open unless
    /// the user says to close them too.
    /// </summary>
    public async Task RequestCloseAllAsync()
    {
        var unsaved = UnsavedWindows();
        var closeUnsaved = unsaved.Count == 0 || await Dialogs.Confirm("Changes not saved",
            $"{unsaved.Count} window(s) have changes that are not saved:\n{Titles(unsaved)}\n\nClose them too (the changes are lost), or keep them open?",
            "Close them too", "Keep them open");
        var keep = closeUnsaved ? [] : unsaved.Select(w => w.ViewModel).ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var window in _windows.ToList())
            if (!keep.Contains(window.ViewModel))
                Close(window);
    }

    // ── Front and back ──

    /// <summary>Brings <paramref name="window"/> to the front: its card flies forward, the window in front goes back.</summary>
    public void BringToFront(DeckWindow window)
    {
        if (_stage)
        {
            ShowOnStage(window);
            return;
        }
        window.LastUsed = ++_clock;
        if (window.IsFront)
        {
            if (_front != window)
                SetFront(window);
            return;
        }
        window.IsFront = true;
        SetFront(window);
        var card = window.Card;
        if (card is null)
        {
            ShowFlat(window);
            return;
        }
        // From the card at the back (as it is shown, grown or not) to its place in front, then the window itself.
        window.Motion?.Kill();
        window.GrowMotion?.Kill();
        window.Grow = 1;
        window.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        var from = card.Transform;
        var to = FrontPose(window.Rect);
        var tween = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tween.TweenMethod(Callable.From<float>(t => card.Transform = Blend(from, to, t)), 0f, 1f, Duration);
        tween.TweenMethod(Callable.From<float>(t => window.CardMaterial?.SetShaderParameter("light", t)), 0.55f, 1f, Duration);
        window.CardLabel?.QueueFree();
        window.CardLabel = null;
        tween.Chain().TweenCallback(Callable.From(() =>
        {
            window.Motion = null;
            if (window.IsFront)
                ShowFlat(window);
        }));
        window.Motion = tween;
        Arrange(animate: true);
    }

    /// <summary>Sends <paramref name="window"/> to the back: its card leaves its place in front for the row of cards.</summary>
    public void SendToBack(DeckWindow window, bool raise = true)
    {
        // On the stage there is no back: ◀ ▶ go from window to window.
        if (!window.IsFront || _stage)
            return;
        window.IsFront = false;
        window.Motion?.Kill();
        window.Motion = null;
        Unfade(window, animate: false);
        // The window leaves the front: its viewport keeps rendering for its card.
        window.Viewport.Reparent(_holder, false);
        window.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        window.Container.Visible = false;
        if (window.Card is null)
            CreateCard(window);
        // The card has the size of the window now (it may have been resized since).
        ((QuadMesh)window.Card!.Mesh).Size = window.Rect.Size * Scale;
        window.CardMaterial?.SetShaderParameter("size", window.Rect.Size);
        window.GrowMotion?.Kill();
        window.Grow = 1;
        SetPose(window, FrontPose(window.Rect));
        window.CardMaterial!.SetShaderParameter("light", 1f);
        window.Card.Visible = true;
        // The window in front used last becomes the active one.
        if (_front == window)
            SetFront(raise ? _windows.Where(w => w.IsFront).OrderByDescending(w => w.LastUsed).FirstOrDefault() : null);
        Arrange(animate: true);
    }

    /// <summary>Sends the window in front to the back.</summary>
    public void SendFrontToBack()
    {
        if (_front is { } front)
            SendToBack(front);
    }

    /// <summary>Makes <paramref name="window"/> (in front) the active one: above the others, its edge lit.</summary>
    public void Activate(DeckWindow window)
    {
        if (!window.IsFront || _front == window)
            return;
        window.LastUsed = ++_clock;
        SetFront(window);
    }

    /// <summary>The window in front under <paramref name="globalPoint"/> (the topmost), or null.</summary>
    public DeckWindow? FrontAt(Vector2 globalPoint) =>
        _windows.Where(w => w.IsFront && w.Container.Visible && w.Container.GetGlobalRect().HasPoint(globalPoint))
            .OrderByDescending(w => w.Container.GetIndex()).FirstOrDefault();

    /// <summary>Sends every window in front to the back.</summary>
    public void SendAllToBack()
    {
        // One after the other, a moment apart: they fly to the shelf in turn.
        var delay = 0.0;
        foreach (var window in _windows.Where(w => w.IsFront).ToList())
        {
            GetTree().CreateTimer(delay).Timeout += () =>
            {
                if (_windows.Contains(window))
                    SendToBack(window);
            };
            delay += 0.07;
        }
    }

    /// <summary>Brings every window at the back to the front, in the order they were opened (the last one above).</summary>
    public void BringAllToFront()
    {
        var delay = 0.0;
        foreach (var window in _windows.Where(w => !w.IsFront).ToList())
        {
            GetTree().CreateTimer(delay).Timeout += () =>
            {
                if (_windows.Contains(window))
                    BringToFront(window);
            };
            delay += 0.07;
        }
    }

    /// <summary>
    /// Makes the next window the active one (Ctrl+Tab), in the order they were opened, in front or at the back: after the
    /// last one, the first one again.
    /// </summary>
    public void Cycle()
    {
        if (_windows.Count == 0)
            return;
        var index = _front is null ? -1 : _windows.IndexOf(_front);
        BringToFront(_windows[(index + 1) % _windows.Count]);
    }

    private void SetFront(DeckWindow? window)
    {
        if (window is not null)
            Unfade(window, animate: true);
        _front = window;
        foreach (var w in _windows)
            w.Frame.AddThemeStyleboxOverride("panel", FrameBox(w == window));
        if (window is not null)
            _layer.MoveChild(window.Container, -1);
        FrontChanged?.Invoke(window);
    }

    /// <summary>The window flat in front, in its viewport container, its card hidden.</summary>
    private void ShowFlat(DeckWindow window)
    {
        if (window.Viewport.GetParent() != window.Container)
            window.Viewport.Reparent(window.Container, false);
        window.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        window.Container.Visible = true;
        Place(window, window.IsMaximized ? FullRect : Clamp(window.Rect));
        if (window.Card is not null)
            window.Card.Visible = false;
        _layer.MoveChild(window.Container, -1);
    }

    // ── Faded behind the others ──

    private const float FadedScale = 0.7f;
    private const float FadedAlpha = 0.75f;

    /// <summary>
    /// Puts <paramref name="window"/> (in front) behind the other windows in front, 25% transparent and 30% smaller; the
    /// window in front used last becomes the active one. A click on it brings it back.
    /// </summary>
    public void Fade(DeckWindow window)
    {
        if (!window.IsFront || window.IsFaded || _stage)
            return;
        window.IsFaded = true;
        _layer.MoveChild(window.Container, 0);
        FadeTo(window, FadedScale, FadedAlpha, true);
        if (_front == window)
            SetFront(_windows.Where(w => w.IsFront && !w.IsFaded).OrderByDescending(w => w.LastUsed).FirstOrDefault());
        if (_front is null)
        {
            // No other window: none is active (the faded one stays behind, not lit).
            window.Frame.AddThemeStyleboxOverride("panel", FrameBox(false));
            FrontChanged?.Invoke(null);
        }
    }

    /// <summary><paramref name="window"/> back to its size, opaque (when it was faded).</summary>
    private void Unfade(DeckWindow window, bool animate)
    {
        if (!window.IsFaded)
            return;
        window.IsFaded = false;
        FadeTo(window, 1, 1, animate);
    }

    private void FadeTo(DeckWindow window, float scale, float alpha, bool animate)
    {
        var container = window.Container;
        window.FadeMotion?.Kill();
        window.FadeMotion = null;
        // Smaller around its middle.
        container.PivotOffset = container.Size / 2;
        if (!animate)
        {
            container.Scale = new Vector2(scale, scale);
            container.Modulate = Colors.White with { A = alpha };
            return;
        }
        var tween = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(container, "scale", new Vector2(scale, scale), Duration);
        tween.TweenProperty(container, "modulate:a", alpha, Duration);
        window.FadeMotion = tween;
    }

    // ── The stage (the voice mode) ──

    /// <summary>True while the desktop is the stage of the voice mode.</summary>
    private bool _stage;

    /// <summary>Where each window was on the desktop, and whether it was in front, when the stage began.</summary>
    private readonly Dictionary<DeckWindow, (Rect2 Rect, bool Front)> _beforeStage = [];

    private Button _previous = null!;
    private Button _next = null!;

    /// <summary>True while every window of the stage is small at the bottom (the animation seen above them).</summary>
    private bool _shelf;

    /// <summary>The small windows at the bottom of the stage, one button each.</summary>
    private readonly Control _shelfRow = new() { MouseFilter = MouseFilterEnum.Ignore, Visible = false };

    /// <summary>The small window of each window in the row.</summary>
    private readonly Dictionary<DeckWindow, Button> _shelfTiles = [];

    /// <summary>True while the stage shows windows: one in the middle, or all small at the bottom.</summary>
    public bool ShowsOnStage => _stage && (_shelf ? _windows.Count > 0 : _front is not null);

    /// <summary>True while every window of the stage is small at the bottom.</summary>
    public bool IsShelved => _stage && _shelf;

    /// <summary>
    /// The top of the stage, given by the voice mode: under its ⏬, when the camera of the screen has pushed it down
    /// (0: the usual margin).
    /// </summary>
    public float StageTop { get; set; }

    /// <summary>
    /// The place of the window on the stage: in the middle, 90% of the width and of the height (from
    /// <see cref="StageTop"/> when it is lower than the margin of the top).
    /// </summary>
    private Rect2 StageRect
    {
        get
        {
            var top = Math.Max(Size.Y * 0.05f, StageTop);
            return new Rect2(Size.X * 0.05f, top, Size.X * 0.9f, Size.Y * 0.95f - top);
        }
    }

    /// <summary>
    /// The desktop becomes the stage of the voice mode: no wallpaper, no cards, the animation seen through it; one
    /// window at a time, in the middle on 90% of the screen, ◀ ▶ on its sides to go to the others.
    /// </summary>
    public void EnterStage()
    {
        if (_stage)
            return;
        _stage = true;
        _beforeStage.Clear();
        foreach (var window in _windows)
            _beforeStage[window] = (window.Rect, window.IsFront);
        _wallpaper.Visible = false;
        _sceneContainer.Visible = false;
        // The empty stage lets the clicks through to the animation (a double-click on it leaves the voice mode).
        MouseFilter = MouseFilterEnum.Ignore;
        if (_hovered is not null)
            GrowTo(_hovered, 1);
        _hovered = null;
        foreach (var window in _windows)
            ToStage(window);
        ShowOnStage(_front ?? _windows.OrderByDescending(w => w.LastUsed).FirstOrDefault());
    }

    /// <summary>The desktop as it was: each window back at its place, in front or at the back.</summary>
    public void LeaveStage()
    {
        if (!_stage)
            return;
        _stage = false;
        _stageMotion?.Kill();
        _stageMotion = null;
        ClearShelf();
        _wallpaper.Visible = true;
        _sceneContainer.Visible = true;
        MouseFilter = MouseFilterEnum.Stop;
        PlaceStageArrows();
        var shown = _front;
        foreach (var window in _windows.ToList())
        {
            var (rect, front) = _beforeStage.TryGetValue(window, out var before) ? before : (window.Rect, true);
            window.Motion?.Kill();
            window.Motion = null;
            window.Container.Modulate = Colors.White;
            // The corners of the desktop again.
            Round(window);
            window.Frame.AddThemeStyleboxOverride("panel", FrameBox(window == shown));
            // Small at the bottom, its viewport rendered apart for its picture: back in its window.
            if (window.Viewport.GetParent() != window.Container)
                window.Viewport.Reparent(window.Container, false);
            window.Container.Visible = true;
            Place(window, window.IsMaximized ? FullRect : Clamp(rect));
            if (!front && window != shown)
                SendToBack(window, raise: false);
        }
        _beforeStage.Clear();
        SetFront(shown is { IsFront: true } ? shown : _windows.Where(w => w.IsFront).OrderByDescending(w => w.LastUsed).FirstOrDefault());
        Arrange(animate: false);
    }

    /// <summary><paramref name="window"/> flat, its card hidden (the stage shows no card).</summary>
    private void ToStage(DeckWindow window)
    {
        window.Motion?.Kill();
        window.Motion = null;
        window.GrowMotion?.Kill();
        window.GrowMotion = null;
        window.Grow = 1;
        Unfade(window, animate: false);
        if (window.Card is not null)
            window.Card.Visible = false;
        window.CardLabel?.QueueFree();
        window.CardLabel = null;
        if (window.Viewport.GetParent() != window.Container)
            window.Viewport.Reparent(window.Container, false);
        window.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        window.IsFront = true;
        Round(window);
        window.Frame.AddThemeStyleboxOverride("panel", FrameBox(window == _front));
    }

    /// <summary>The radius of the corners of the windows on the desktop, and on the stage (larger, as on a tablet).</summary>
    private const int DeskRadius = 12, StageRadius = 18;

    /// <summary>The radius of the corners of the windows now.</summary>
    private int Radius => _stage ? StageRadius : DeskRadius;

    private static Shader? _roundedWindow;
    private static Shader RoundedWindow => _roundedWindow ??= GD.Load<Shader>("res://Shaders/rounded_window.gdshader");

    /// <summary>
    /// Rounded corners: the picture of the window cut by a shader (outside the corners, what is behind shows through),
    /// at the radius of the desktop or of the stage; its panel draws its edge along the same curve (<see cref="FrameBox"/>).
    /// </summary>
    private void Round(DeckWindow window)
    {
        if (window.Container.Material is not ShaderMaterial material)
            window.Container.Material = material = new ShaderMaterial { Shader = RoundedWindow };
        material.SetShaderParameter("radius", (float)Radius);
        material.SetShaderParameter("size", window.Container.Size);
        CastShadow(window);
    }

    /// <summary>How far the shadow of a window goes past it, in pixels.</summary>
    private const int ShadowMargin = 28;

    private static Shader? _windowShadow;
    private static Shader WindowShadow => _windowShadow ??= GD.Load<Shader>("res://Shaders/window_shadow.gdshader");

    /// <summary>
    /// The shadow under a window of the desktop: a child of its picture drawn behind it, larger by
    /// <see cref="ShadowMargin"/>, so that it moves, grows, fades and comes in front with the window; none on the stage.
    /// </summary>
    private void CastShadow(DeckWindow window)
    {
        if (window.Shadow is not { } shadow)
        {
            var material = new ShaderMaterial { Shader = WindowShadow };
            material.SetShaderParameter("margin", (float)ShadowMargin);
            material.SetShaderParameter("offset", new Vector2(0, 7));
            material.SetShaderParameter("shadow_color", new Color(0, 0, 0, Eve.IsLight ? 0.24f : 0.5f));
            shadow = new ColorRect { Color = Colors.White, Material = material, MouseFilter = MouseFilterEnum.Ignore, ShowBehindParent = true };
            window.Container.AddChild(shadow);
            window.Shadow = shadow;
        }
        shadow.Visible = !_stage;
        FitShadow(window);
    }

    /// <summary>The shadow at the size of its window.</summary>
    private static void FitShadow(DeckWindow window)
    {
        if (window.Shadow is not { } shadow)
            return;
        shadow.Position = -new Vector2(ShadowMargin, ShadowMargin);
        shadow.Size = window.Container.Size + new Vector2(ShadowMargin * 2, ShadowMargin * 2);
        if (shadow.Material is ShaderMaterial material)
        {
            material.SetShaderParameter("size", shadow.Size);
            material.SetShaderParameter("radius", (float)DeskRadius);
        }
    }

    /// <summary>
    /// The panel of a window, its corners rounded as the picture is cut: glass on the desktop, opaque on the stage (the
    /// 3D animation behind does not show through the window).
    /// </summary>
    private StyleBoxFlat FrameBox(bool active)
    {
        var box = Eve.WindowBox(active);
        if (_stage)
            box.BgColor = Eve.PanelSolid with { A = 1 };
        box.SetCornerRadiusAll(Radius);
        box.AntiAliasing = true;
        return box;
    }

    /// <summary>
    /// Shows <paramref name="window"/> alone on the stage (null: none), sliding in from the side of <paramref name="from"/>
    /// (-1: from the left, 1: from the right, 0: in place).
    /// </summary>
    private void ShowOnStage(DeckWindow? window, int from = 0)
    {
        ClearShelf();
        // The window shown until now stays under the new one, opaque and in its place, until the new one covers it: the
        // animation behind never shows between the two (it would flash for the time of the fade).
        var previous = window is not null && _front is { } shown && shown != window && shown.Container.Visible ? shown : null;
        _stageMotion?.Kill();
        _stageMotion = null;
        if (window is not null)
            ToStage(window);
        foreach (var w in _windows)
            w.Container.Visible = w == window || w == previous;
        if (window is not null)
        {
            window.LastUsed = ++_clock;
            var rect = StageRect;
            if (previous is not null)
            {
                // Stopped half-way (a quick ◀ ▶): whole again, under the new one.
                Place(previous, rect);
                previous.Container.Modulate = Colors.White;
            }
            Place(window, rect);
            if (_front != window)
                SetFront(window);
            _layer.MoveChild(window.Container, -1);
            var tween = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            if (from != 0)
            {
                var container = window.Container;
                container.Position = rect.Position.Round() + new Vector2(from * 90, 0);
                container.Modulate = Colors.White with { A = 0 };
                tween.TweenProperty(container, "position", rect.Position.Round(), 0.24);
                tween.TweenProperty(container, "modulate:a", 1f, 0.2);
            }
            else
            {
                window.Container.Modulate = Colors.White;
                // A moment for the picture of the window shown to be drawn (its viewport was hidden) before the other goes.
                tween.TweenInterval(0.1);
            }
            // The new one covers the stage: the others are hidden behind it.
            tween.Chain().TweenCallback(Callable.From(() =>
            {
                if (_stage && _front == window)
                    foreach (var w in _windows)
                        if (w != window)
                            w.Container.Visible = false;
                _stageMotion = null;
            }));
            _stageMotion = tween;
        }
        else if (_front is not null)
            SetFront(null);
        PlaceStageArrows();
    }

    /// <summary>The window coming on the stage, the one before still under it until the end.</summary>
    private Tween? _stageMotion;

    /// <summary>The next window on the stage (<paramref name="by"/> = 1) or the one before (-1), round the list.</summary>
    private void StepStage(int by)
    {
        if (!_stage || _windows.Count < 2)
            return;
        var index = _front is null ? 0 : _windows.IndexOf(_front);
        ShowOnStage(_windows[((index + by) % _windows.Count + _windows.Count) % _windows.Count], by);
    }

    /// <summary>
    /// ⏬ of the voice mode: every window of the stage small in a row at the bottom of the screen, the animation seen
    /// above them; a click on one shows it in the middle again; again: the window shown before comes back.
    /// </summary>
    public void ToggleShelf()
    {
        if (!_stage)
            return;
        if (_shelf)
        {
            ShowOnStage(_front ?? _windows.OrderByDescending(w => w.LastUsed).FirstOrDefault());
            return;
        }
        if (_windows.Count == 0)
            return;
        _stageMotion?.Kill();
        _stageMotion = null;
        _shelf = true;
        foreach (var window in _windows)
        {
            window.Motion?.Kill();
            window.Motion = null;
            window.Container.Visible = false;
            // Out of its hidden container its viewport keeps rendering: the small picture stays alive.
            if (window.Viewport.GetParent() != _holder)
                window.Viewport.Reparent(_holder, false);
            window.Viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        }
        BuildShelf();
        PlaceStageArrows();
    }

    /// <summary>The row of the small windows, in the middle of the bottom, above the margin of the line and the microphone.</summary>
    private void BuildShelf(bool animate = true)
    {
        foreach (var child in _shelfRow.GetChildren())
        {
            _shelfRow.RemoveChild(child);
            child.QueueFree();
        }
        _shelfTiles.Clear();
        _shelfRow.Visible = _shelf;
        if (!_shelf || _windows.Count == 0)
            return;
        if (_shelfRow.GetParent() is null)
            AddChild(_shelfRow);
        _shelfRow.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MoveChild(_shelfRow, -1);
        const float gap = 16, caption = 26;
        var count = _windows.Count;
        var aspect = StageRect.Size.X / Math.Max(1, StageRect.Size.Y);
        var width = Math.Min(Size.X * 0.15f, (Size.X * 0.9f - gap * (count - 1)) / count);
        var height = width / aspect;
        var total = count * width + (count - 1) * gap;
        var y = Size.Y * 0.95f - height - caption - 10;
        for (var i = 0; i < count; i++)
        {
            var window = _windows[i];
            var tile = new Button
            {
                FocusMode = FocusModeEnum.None,
                MouseDefaultCursorShape = CursorShape.PointingHand,
                TooltipText = $"{window.Title} · click: in the middle again",
                Position = new Vector2((Size.X - total) / 2 + i * (width + gap), y),
                Size = new Vector2(width, height + caption)
            };
            StyleTile(tile, window == _front);
            tile.AddThemeStyleboxOverride("hover", TileBox(Eve.Cyan, 2));
            tile.AddThemeStyleboxOverride("pressed", TileBox(Eve.Cyan, 2));
            var picture = new TextureRect
            {
                Texture = window.Viewport.GetTexture(),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                MouseFilter = MouseFilterEnum.Ignore,
                Position = new Vector2(6, 6),
                Size = new Vector2(width - 12, height - 6)
            };
            tile.AddChild(picture);
            var title = new Label
            {
                Text = $"{window.Icon}  {window.Title}",
                ClipText = true,
                HorizontalAlignment = HorizontalAlignment.Center,
                MouseFilter = MouseFilterEnum.Ignore,
                Position = new Vector2(6, height + 2),
                Size = new Vector2(width - 12, caption - 4)
            };
            title.AddThemeFontSizeOverride("font_size", 12);
            title.AddThemeColorOverride("font_color", VoiceMode.Ink);
            tile.AddChild(title);
            tile.AddChild(ShelfClose(window, width));
            tile.Pressed += () => ShowOnStage(window);
            _shelfRow.AddChild(tile);
            _shelfTiles[window] = tile;
            if (!animate)
                continue;
            // Each one comes down into its place, a moment after the one before.
            var place = tile.Position;
            tile.Position = place - new Vector2(0, 60);
            tile.Modulate = Colors.White with { A = 0 };
            var tween = tile.CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
            tween.TweenProperty(tile, "position", place, 0.28).SetDelay(i * 0.04);
            tween.TweenProperty(tile, "modulate:a", 1f, 0.22).SetDelay(i * 0.04);
        }
    }

    /// <summary>
    /// ✕ in a disc on the upper right corner of a small window of the bottom: closes it (asked first when it has
    /// changes not saved, as its own ✕); the row is made again without it.
    /// </summary>
    private Button ShelfClose(DeckWindow window, float tileWidth)
    {
        const int size = 28;
        var close = new Button
        {
            Text = "✕",
            FocusMode = FocusModeEnum.None,
            MouseDefaultCursorShape = CursorShape.PointingHand,
            TooltipText = $"Close {window.Title}",
            Position = new Vector2(tileWidth - size + 8, -8),
            Size = new Vector2(size, size),
            CustomMinimumSize = new Vector2(size, size)
        };
        close.AddThemeStyleboxOverride("normal", VoiceMode.Glass(size / 2, VoiceMode.SurfaceHover, VoiceMode.Hairline, 1, 0.8f));
        close.AddThemeStyleboxOverride("hover", VoiceMode.Glass(size / 2, Eve.Red, Eve.Red, 0, 1.2f));
        close.AddThemeStyleboxOverride("pressed", VoiceMode.Glass(size / 2, Eve.Red.Darkened(0.15f), Eve.Red, 0, 0.5f));
        close.AddThemeFontSizeOverride("font_size", 12);
        close.AddThemeColorOverride("font_color", VoiceMode.Ink);
        close.AddThemeColorOverride("font_hover_color", Colors.White);
        close.AddThemeColorOverride("font_pressed_color", Colors.White);
        close.Pressed += () => _ = RequestCloseAsync(window);
        return close;
    }

    /// <summary>
    /// The small window of <paramref name="window"/> (closed) gone from the row: the others stay where they are, at
    /// their size (the row is laid out again only at the next ⏬).
    /// </summary>
    private void RemoveFromShelf(DeckWindow window)
    {
        if (_shelfTiles.Remove(window, out var tile) && IsInstanceValid(tile))
        {
            tile.Disabled = true;
            var tween = tile.CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
            tile.PivotOffset = tile.Size / 2;
            tween.TweenProperty(tile, "scale", new Vector2(0.6f, 0.6f), 0.18);
            tween.TweenProperty(tile, "modulate:a", 0f, 0.18);
            tween.Chain().TweenCallback(Callable.From(tile.QueueFree));
        }
        // The window shown before may have been the one closed: the edge goes to the one that replaces it.
        foreach (var (shown, other) in _shelfTiles)
            StyleTile(other, shown == _front);
    }

    private static StyleBoxFlat TileBox(Color edge, int border)
    {
        var box = VoiceMode.Glass(14, VoiceMode.Surface, edge, border, border > 1 ? 1.4f : 1f);
        box.ContentMarginLeft = box.ContentMarginRight = box.ContentMarginTop = box.ContentMarginBottom = 0;
        return box;
    }

    /// <summary>A small window at rest: its edge cyan when it is the window shown before.</summary>
    private static void StyleTile(Button tile, bool front) =>
        tile.AddThemeStyleboxOverride("normal", front ? TileBox(Eve.Cyan, 2) : TileBox(Eve.Edge, 1));

    /// <summary>The small windows of the bottom gone (a window shown in the middle, the stage left).</summary>
    private void ClearShelf()
    {
        _shelf = false;
        foreach (var child in _shelfRow.GetChildren())
        {
            _shelfRow.RemoveChild(child);
            child.QueueFree();
        }
        _shelfTiles.Clear();
        _shelfRow.Visible = false;
    }

    private void BuildStageArrows()
    {
        _previous = StageArrow("◀", -1);
        _next = StageArrow("▶", 1);
        PlaceStageArrows();
    }

    private Button StageArrow(string text, int by)
    {
        var button = new Button { Text = text, Visible = false, FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand };
        VoiceMode.MakeRound(button, 56, 22);
        button.Pressed += () => StepStage(by);
        AddChild(button);
        return button;
    }

    /// <summary>◀ ▶ in the middle of the margins left and right of the window on the stage, when there are several.</summary>
    private void PlaceStageArrows()
    {
        if (_previous is null || _next is null)
            return;
        var shown = _stage && !_shelf && _windows.Count > 1;
        _previous.Visible = _next.Visible = shown;
        if (!shown)
            return;
        var size = _previous.CustomMinimumSize;
        var x = Math.Max(4, Size.X * 0.025f - size.X / 2);
        var y = (Size.Y - size.Y) / 2;
        _previous.Position = new Vector2(x, y);
        _next.Position = new Vector2(Size.X - x - size.X, y);
        _previous.Size = _next.Size = size;
        var index = _front is null ? 0 : _windows.IndexOf(_front);
        _previous.TooltipText = $"◀ {_windows[(index - 1 + _windows.Count) % _windows.Count].Title}  ·  {index + 1} / {_windows.Count}";
        _next.TooltipText = $"▶ {_windows[(index + 1) % _windows.Count].Title}  ·  {index + 1} / {_windows.Count}";
        MoveChild(_previous, -1);
        MoveChild(_next, -1);
    }

    // ── Layout ──

    /// <summary>The whole desktop: between the bar of commands and the chat.</summary>
    private Rect2 FullRect => new(Vector2.Zero, Size);

    /// <summary>
    /// □ in the header: the window takes the whole desktop (between the bar of commands and the chat); ❐ gives it back
    /// the place and size it had.
    /// </summary>
    public void ToggleMaximized(DeckWindow window)
    {
        // On the stage the window keeps its place in the middle.
        if (_stage)
            return;
        if (window.IsMaximized)
        {
            var restore = window.Restore!.Value;
            window.Restore = null;
            window.Frame.SetMaximized(false);
            Place(window, Clamp(restore));
            return;
        }
        Maximize(window);
    }

    /// <summary>Gives <paramref name="window"/> the whole desktop (its place before is kept for ❐).</summary>
    public void Maximize(DeckWindow window, Rect2? restore = null)
    {
        window.Restore = restore ?? window.Rect;
        window.Frame.SetMaximized(true);
        if (window.IsFront)
            Activate(window);
        Place(window, FullRect);
    }

    private static void Unmaximize(DeckWindow window)
    {
        if (!window.IsMaximized)
            return;
        window.Restore = null;
        window.Frame.SetMaximized(false);
    }

    /// <summary>The place and size of <paramref name="window"/> in front (also kept while it is at the back).</summary>
    public Rect2 RectOf(DeckWindow window) => window.Rect;

    /// <summary>Puts <paramref name="window"/> at <paramref name="rect"/> (kept inside the desktop).</summary>
    public void MoveTo(DeckWindow window, Rect2 rect) => Place(window, Clamp(rect));

    /// <summary>
    /// The content of <paramref name="window"/> needs at least <paramref name="size"/> (the ribbon of the tape shown
    /// whole): the window cannot be made smaller by hand, and it grows now when it is smaller (inside the desktop).
    /// </summary>
    public void RequireMinSize(DeckWindow window, Vector2 size)
    {
        window.MinSize = size.Max(new Vector2(360, 240));
        if (window.IsMaximized || (window.Rect.Size.X >= window.MinSize.X && window.Rect.Size.Y >= window.MinSize.Y))
            return;
        var grown = new Rect2(window.Rect.Position, window.Rect.Size.Max(window.MinSize));
        // Grown to the right and down: moved back left or up when that goes out of the desktop.
        Place(window, Clamp(grown));
    }

    /// <summary>Puts <paramref name="window"/> at <paramref name="rect"/> in front.</summary>
    private void Place(DeckWindow window, Rect2 rect)
    {
        window.Rect = rect;
        window.Container.Position = rect.Position.Round();
        window.Container.Size = rect.Size.Round();
        window.Container.PivotOffset = window.Container.Size / 2;
        // The corners cut and the shadow follow the size.
        if (window.Container.Material is ShaderMaterial rounded)
            rounded.SetShaderParameter("size", window.Container.Size);
        FitShadow(window);
    }

    /// <summary><paramref name="rect"/> kept inside the desktop (its header always reachable).</summary>
    private Rect2 Clamp(Rect2 rect)
    {
        var size = rect.Size.Min(Size - new Vector2(8, 8)).Max(new Vector2(320, 200));
        var position = new Vector2(Math.Clamp(rect.Position.X, 4, Math.Max(4, Size.X - size.X - 4)), Math.Clamp(rect.Position.Y, 4, Math.Max(4, Size.Y - size.Y - 4)));
        return new Rect2(position, size);
    }

    /// <summary>World units per pixel at the distance of the front.</summary>
    private float Scale => 2 * FrontDistance * MathF.Tan(Mathf.DegToRad(FieldOfView / 2)) / Math.Max(1, Size.Y);

    /// <summary>The pose of a card covering exactly <paramref name="rect"/> of the desktop (seen by the camera).</summary>
    private Transform3D FrontPose(Rect2 rect)
    {
        var s = Scale;
        var center = rect.GetCenter() - Size / 2;
        return new Transform3D(Basis.Identity, new Vector3(center.X * s, -center.Y * s, -FrontDistance));
    }

    /// <summary>
    /// The pose of the card number <paramref name="index"/> of <paramref name="count"/> at the back: a row along the
    /// bottom of the desktop, the cards lying back in a perspective, turned a little towards the middle, closer together
    /// (overlapping) when there are many.
    /// </summary>
    private Transform3D BackPose(int index, int count, Rect2 rect)
    {
        var s = Scale;
        const float depthRatio = 3.8f;
        var depth = FrontDistance * depthRatio;
        var visibleWidth = Size.X * s * depthRatio;
        var visibleHalfHeight = Size.Y * s * depthRatio / 2;
        var card = rect.Size * s;
        var slot = Math.Min(card.X * 1.08f, visibleWidth * 0.94f / Math.Max(1, count));
        var x = (index - (count - 1) / 2f) * slot;
        const float lean = 0.85f;
        // The bottom edge of the card a little above the bottom of the desktop.
        var y = -visibleHalfHeight + card.Y * MathF.Cos(lean) / 2 + visibleHalfHeight * 0.13f;
        var basis = Basis.FromEuler(new Vector3(-lean, -MathF.Atan2(x, depth) * 0.8f, 0));
        return new Transform3D(basis, new Vector3(x, y, -depth - Math.Abs(x) * 0.08f));
    }

    /// <summary>Places the cards at the back, the most recently used in the middle of the front row.</summary>
    private void Arrange(bool animate)
    {
        // In the order the windows were opened (the cards keep their place in the row).
        var back = _windows.Where(w => !w.IsFront && w.Card is not null).ToList();
        for (var i = 0; i < back.Count; i++)
        {
            var window = back[i];
            var pose = BackPose(i, back.Count, window.Rect);
            var card = window.Card!;
            if (window.CardLabel is null)
                CreateLabel(window);
            window.CardLabel!.Position = new Vector3(0, card.Mesh is QuadMesh q ? q.Size.Y / 2 + 0.35f : 1, 0.01f);
            if (!animate || window.Motion is not null && window.IsFront)
            {
                if (!window.IsFront)
                    SetPose(window, pose);
                continue;
            }
            window.Motion?.Kill();
            var from = window.Pose;
            var startLight = (float)(window.CardMaterial?.GetShaderParameter("light").AsDouble() ?? 1);
            var tween = CreateTween().SetParallel().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.InOut);
            tween.TweenMethod(Callable.From<float>(t => SetPose(window, Blend(from, pose, t))), 0f, 1f, Duration);
            tween.TweenMethod(Callable.From<float>(t => window.CardMaterial?.SetShaderParameter("light", t)), startLight, 0.55f, Duration);
            tween.Chain().TweenCallback(Callable.From(() => window.Motion = null));
            window.Motion = tween;
        }
    }

    private void CreateCard(DeckWindow window)
    {
        var s = Scale;
        var material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/deck_card.gdshader") };
        material.SetShaderParameter("picture", window.Viewport.GetTexture());
        material.SetShaderParameter("edge_color", Eve.Cyan);
        material.SetShaderParameter("light_theme", Eve.IsLight);
        // The card has the rounded corners of the window.
        material.SetShaderParameter("size", window.Rect.Size);
        material.SetShaderParameter("radius", (float)DeskRadius);
        var card = new MeshInstance3D { Mesh = new QuadMesh { Size = window.Rect.Size * s }, MaterialOverride = material };
        _cards.AddChild(card);
        window.Card = card;
        window.CardMaterial = material;
    }

    private void CreateLabel(DeckWindow window)
    {
        var label = new Label3D
        {
            Text = $"{window.Icon}  {window.Title}",
            FontSize = 40,
            PixelSize = Scale * 0.5f,
            Modulate = Eve.Text with { A = 0.85f },
            OutlineModulate = Eve.IsLight ? new Color(1, 1, 1, 0.9f) : new Color(0, 0, 0, 0.8f),
            OutlineSize = 8,
            Font = Eve.Display,
            Shaded = false,
            DoubleSided = true,
            NoDepthTest = false
        };
        window.Card!.AddChild(label);
        window.CardLabel = label;
    }

    /// <summary>The pose between <paramref name="a"/> (t = 0) and <paramref name="b"/> (t = 1), the rotation slerped.</summary>
    private static Transform3D Blend(Transform3D a, Transform3D b, float t) =>
        new(new Basis(a.Basis.GetRotationQuaternion().Slerp(b.Basis.GetRotationQuaternion(), t)).Scaled(a.Basis.Scale.Lerp(b.Basis.Scale, t)),
            a.Origin.Lerp(b.Origin, t));

    /// <summary>How much a card at the back grows under the pointer.</summary>
    private const float HoverGrowth = 1.56f;  // +30 %, then +20 % more

    /// <summary>Puts the card of <paramref name="window"/> at <paramref name="pose"/>, grown as it is now.</summary>
    private static void SetPose(DeckWindow window, Transform3D pose)
    {
        window.Pose = pose;
        ApplyPose(window);
    }

    /// <summary>
    /// The card at its pose, grown by <see cref="DeckWindow.Grow"/>: larger, its bottom edge kept where it was (it grows
    /// upwards, never below the screen) and a little closer, in front of its neighbours.
    /// </summary>
    private static void ApplyPose(DeckWindow window)
    {
        if (window.Card is not { } card)
            return;
        var grow = window.Grow;
        if (Math.Abs(grow - 1) < 0.001f)
        {
            card.Transform = window.Pose;
            return;
        }
        var height = card.Mesh is QuadMesh quad ? quad.Size.Y : 1;
        var pose = window.Pose;
        var lift = pose.Basis * new Vector3(0, height * (grow - 1) / 2, 0);
        var closer = new Vector3(0, 0, (grow - 1) / (HoverGrowth - 1) * 3f);
        card.Transform = new Transform3D(pose.Basis.Scaled(Vector3.One * grow), pose.Origin + lift + closer);
    }

    /// <summary>Grows the card of <paramref name="window"/> to <paramref name="target"/> (1: back to its size).</summary>
    private void GrowTo(DeckWindow window, float target)
    {
        if (window.IsFront || window.Card is null)
            return;
        window.GrowMotion?.Kill();
        var tween = CreateTween().SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        // Under the pointer the card also gets its normal contrast back and is drawn above the others.
        if (window.CardMaterial is { } material)
            material.RenderPriority = target > 1 ? 10 : 0;
        tween.TweenMethod(Callable.From<float>(g =>
        {
            window.Grow = g;
            if (window.IsFront)
                return;
            ApplyPose(window);
            var share = Math.Clamp((g - 1) / (HoverGrowth - 1), 0, 1);
            window.CardMaterial?.SetShaderParameter("light", Mathf.Lerp(0.55f, 1f, share));
        }), window.Grow, target, 0.18);
        window.GrowMotion = tween;
    }

    // ── The pointer on the desktop ──

    /// <summary>The window whose card is under <paramref name="point"/> (desktop coordinates), the nearest first.</summary>
    private DeckWindow? CardAt(Vector2 point)
    {
        foreach (var window in _windows.Where(w => !w.IsFront && w.Card is { Visible: true }).OrderBy(w => -w.Card!.GlobalPosition.Z))
        {
            var card = window.Card!;
            var half = ((QuadMesh)card.Mesh).Size / 2;
            var corners = new[] { new Vector3(-half.X, half.Y, 0), new Vector3(half.X, half.Y, 0), new Vector3(half.X, -half.Y, 0), new Vector3(-half.X, -half.Y, 0) }
                .Select(c => _camera.UnprojectPosition(card.GlobalTransform * c)).ToArray();
            if (Geometry2D.IsPointInPolygon(point, corners))
                return window;
        }
        return null;
    }

    public override void _GuiInput(InputEvent e)
    {
        switch (e)
        {
            case InputEventMouseMotion motion:
                var hovered = CardAt(motion.Position);
                if (hovered != _hovered)
                {
                    _hovered?.CardMaterial?.SetShaderParameter("hover", 0f);
                    // (no extra light: the card under the pointer shows its window as it is)
                    // The card under the pointer grows by 30%, the one it leaves comes back to its size.
                    if (_hovered is not null)
                        GrowTo(_hovered, 1);
                    if (hovered is not null)
                        GrowTo(hovered, HoverGrowth);
                    _hovered = hovered;
                    MouseDefaultCursorShape = hovered is null ? CursorShape.Arrow : CursorShape.PointingHand;
                    TooltipText = hovered is null ? string.Empty : $"{hovered.Title} · click: bring to the front · right click: close";
                }
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click:
                // One click on a card brings its window back to the front.
                if (CardAt(click.Position) is { } window)
                    BringToFront(window);
                AcceptEvent();
                break;
            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } click:
                if (CardAt(click.Position) is { } closing)
                    Close(closing);
                else
                    DesktopMenu(click.Position);
                AcceptEvent();
                break;
        }
    }

    /// <summary>
    /// The menu of a right click on the empty desktop: Set wallpaper (the picture library, where a right click on a
    /// picture sets it), Down all (every window to the back), Think, Last 3 windows (the three tools opened last, again
    /// or brought to the front), and Remove the wallpaper when there is one.
    /// </summary>
    private void DesktopMenu(Vector2 at)
    {
        var recent = Preferences.RecentWindows.Take(3).ToList();
        var menu = new ContextMenu()
            .Item("🖼 Set wallpaper…", () => CommandRequested?.Invoke("/pictures"), tooltip: "Opens the picture library: a right click on a picture → Set as wallpaper")
            .Item("⏬ Down all", SendAllToBack, enabled: _windows.Count > 0, tooltip: "Sends every window to the back")
            .Item("🧠 Think", () => CommandRequested?.Invoke("/think"), tooltip: "The black screen of the last hour (/think)")
            .Item("🗔 Last 3 windows", () => OpenAgain(recent), enabled: recent.Count > 0,
                tooltip: recent.Count > 0 ? "Opens again: " + string.Join(", ", recent) : "No window opened yet");
        if (Wallpaper.Current is not null)
            menu.Separator().Item("🖼 Remove the wallpaper", Wallpaper.Clear);
        menu.Show(this, at);
    }

    /// <summary>The windows of <paramref name="commands"/>: brought to the front when open, opened again when closed.</summary>
    private void OpenAgain(IReadOnlyList<string> commands)
    {
        // The oldest first, so that the newest ends in front.
        foreach (var command in commands.Reverse())
        {
            if (Find(command) is { } open)
                BringToFront(open);
            else
                CommandRequested?.Invoke(command);
        }
    }

    /// <summary>True when <paramref name="globalPoint"/> is over the window in front.</summary>
    public bool IsOverFront(Vector2 globalPoint) =>
        _front is { IsFront: true } front && front.Container.Visible && front.Container.GetGlobalRect().HasPoint(globalPoint);
}
