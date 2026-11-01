using System;
using Godot;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// A picture shown over the 3D animation of the chatbot (a line <c>IMAGE:&lt;file&gt;</c> of a script of
/// <c>/remote</c>): it takes the upper right two thirds of the place of the animation (in the chat or in full screen),
/// in front of it, while the animation comes forward and moves to the left; after <see cref="Hold"/> the picture fades
/// out and the animation goes back to its place in <see cref="Return"/>. A new picture replaces the one shown.
/// </summary>
public partial class ImageShow : Control
{
    /// <summary>How long the picture is shown, in seconds.</summary>
    private const double Hold = 20;

    /// <summary>How long the animation takes to go back to its place, in seconds.</summary>
    private const double Return = 3;

    private const double FadeIn = 0.6;
    private const double FadeOut = 1.0;

    /// <summary>The animation forward: its scale, and its shift (a share of its size) to the left and down.</summary>
    private const float Forward = 1.18f;
    private static readonly Vector2 Shift = new(-0.2f, 0.06f);

    private Control _avatar;
    private Tween? _tween;
    private bool _ending;
    private Action? _ended;

    private ImageShow(Control avatar, Texture2D picture)
    {
        _avatar = avatar;
        Name = nameof(ImageShow);
        MouseFilter = MouseFilterEnum.Ignore;
        Modulate = Colors.Transparent;
        // The upper right two thirds of the place of the animation, with a small margin.
        var frame = new TextureRect
        {
            Texture = picture,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
            AnchorLeft = 1f / 3, AnchorTop = 0, AnchorRight = 1, AnchorBottom = 2f / 3,
            OffsetLeft = 8, OffsetTop = 8, OffsetRight = -8, OffsetBottom = -4
        };
        AddChild(frame);
    }

    /// <summary>
    /// Shows the picture of <paramref name="path"/> over <paramref name="avatar"/> (the view of the animation, in the chat
    /// or in full screen); false when the file cannot be read as a picture. <paramref name="ended"/> runs once, when the
    /// animation is back at its place (or when a new picture replaces this one).
    /// </summary>
    public static bool Show(Control avatar, string path, Action? ended = null)
    {
        var image = Image.LoadFromFile(path);
        if (image is null || image.IsEmpty())
            return false;
        if (avatar.GetParent() is not Control host)
            return false;
        // The picture shown already gives its place at once.
        foreach (var child in host.GetChildren())
            if (child is ImageShow shown)
                shown.Stop();
        var show = new ImageShow(avatar, ImageTexture.CreateFromImage(image)) { _ended = ended };
        host.AddChild(show);
        show.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // In front of the animation, under what is above it (the microphone of the full screen).
        host.MoveChild(show, avatar.GetIndex() + 1);
        show.Start();
        return true;
    }

    /// <summary>
    /// The animation <paramref name="avatar"/> replaced the one under the picture shown in its place (another preset):
    /// the picture stays in front of it and it comes forward too.
    /// </summary>
    public static void Retarget(Control avatar)
    {
        if (avatar.GetParent() is not Control host)
            return;
        foreach (var child in host.GetChildren())
            if (child is ImageShow { _ending: false } shown)
            {
                if (IsInstanceValid(shown._avatar))
                    shown._avatar.TreeEntered -= shown.Followed;
                shown._avatar = avatar;
                avatar.TreeEntered += shown.Followed;
                host.MoveChild(shown, avatar.GetIndex() + 1);
                shown.Forwards(0.4);
            }
    }

    private void Start()
    {
        // The animation follows when it changes its place (the full screen, or back to the chat).
        _avatar.TreeEntered += Followed;
        _tween = CreateTween();
        _tween.SetParallel();
        _tween.TweenProperty(this, "modulate", Colors.White, FadeIn);
        Forwards(0.8);
        _tween.Chain().TweenInterval(Hold);
        _tween.Chain().TweenProperty(this, "modulate", Colors.Transparent, FadeOut);
        _tween.Chain().TweenCallback(Callable.From(() => End(Return)));
    }

    /// <summary>The animation comes forward and moves to the left, in <paramref name="seconds"/>.</summary>
    private void Forwards(double seconds)
    {
        if (!IsInstanceValid(_avatar))
            return;
        var size = _avatar.Size;
        _avatar.PivotOffset = size / 2;
        var tween = _avatar.CreateTween().SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut).SetParallel();
        tween.TweenProperty(_avatar, "scale", Vector2.One * Forward, seconds);
        tween.TweenProperty(_avatar, "position", size * Shift, seconds);
    }

    /// <summary>The picture gone, the animation goes back to its place in <paramref name="seconds"/>, then this is freed.</summary>
    private void End(double seconds)
    {
        if (_ending)
            return;
        _ending = true;
        if (IsInstanceValid(_avatar))
        {
            _avatar.TreeEntered -= Followed;
            var tween = _avatar.CreateTween().SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut).SetParallel();
            tween.TweenProperty(_avatar, "scale", Vector2.One, seconds);
            tween.TweenProperty(_avatar, "position", Vector2.Zero, seconds);
            var ended = TakeEnded();
            tween.Finished += () => ended?.Invoke();
        }
        else
            TakeEnded()?.Invoke();
        QueueFree();
    }

    /// <summary>Ends at once (a new picture replaces this one): the animation stays forward for the next one.</summary>
    private void Stop()
    {
        _tween?.Kill();
        if (IsInstanceValid(_avatar))
            _avatar.TreeEntered -= Followed;
        _ending = true;
        TakeEnded()?.Invoke();
        QueueFree();
    }

    /// <summary>The callback of the end, given once.</summary>
    private Action? TakeEnded()
    {
        var ended = _ended;
        _ended = null;
        return ended;
    }

    /// <summary>The animation moved (full screen or back): the picture goes with it, the animation forward again.</summary>
    private void Followed() => Callable.From(() =>
    {
        if (_ending || !IsInstanceValid(this) || !IsInstanceValid(_avatar) || _avatar.GetParent() is not Control host)
            return;
        if (GetParent() != host)
        {
            Reparent(host, false);
            SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            host.MoveChild(this, _avatar.GetIndex() + 1);
        }
        Forwards(0.4);
    }).CallDeferred();
}
