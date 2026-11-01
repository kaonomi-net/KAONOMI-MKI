using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Media;

/// <summary>
/// /youtube: the videos saved in Kaonomi. Search on their title and description (Enter, Search or F5; empty: the latest
/// videos), New video (Ctrl+N). The videos found on the left (thumbnail, title, date; a click opens one in the editor, a
/// double-click plays it). The editor: the YouTube address (Paste puts the one of the clipboard; the title,
/// description and channel are read from YouTube), the thumbnail (a double-click plays the video), ▶ Play (the video
/// alone, in the browser page served by <see cref="KAONOMI.Chat.Services.YoutubePlayer"/>), Open on YouTube, Add to
/// Kaonomi / Save (Ctrl+S), Delete (after a confirmation), the title, the description and a note.
/// </summary>
[ToolView(typeof(YoutubeWindowViewModel), "YouTube", "📺", 1200, 760)]
public sealed class YoutubeView : IToolView
{
    private static readonly Color YoutubeRed = new(1f, 0f, 0.2f);

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (YoutubeWindowViewModel)viewModel;
        vm.ConfirmAsync = (message, label) => Dialogs.Confirm("YouTube", message, label);

        var newButton = Ui.Command("NEW", vm.NewCommand, tooltip: "Add another video (Ctrl+N)");
        var toolbar = Ui.Toolbar(Ui.SearchBar(vm, nameof(vm.Term), vm.SearchCommand, "Search the videos of Kaonomi (title and description) · empty: latest videos", newButton));

        // Saved videos.
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        var empty = Ui.Placeholder("No video in Kaonomi yet");
        Bind.Items(list, vm.Videos, video => VideoRow(vm, video), empty);
        var left = Ui.Column(0, Ui.FillHeight(Ui.Scroll(list)), empty);
        left.CustomMinimumSize = new Vector2(300, 0);

        var (editor, urlBox) = Editor(vm);
        var page = Ui.Column(6, toolbar, Ui.SplitH(left, editor, 380), Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError)));
        page.Ready += () => urlBox.CallDeferred(Control.MethodName.GrabFocus);
        Bind.OnGone(page, () => vm.ConfirmAsync = (_, _) => Task.FromResult(false));
        return MediaKeys.Attach(page, key =>
        {
            if (MediaKeys.Command(key, Key.S))
                Bind.Run(vm.SaveCommand);
            else if (MediaKeys.Command(key, Key.N))
                Bind.Run(vm.NewCommand);
            else if (key.Keycode == Key.F5)
                Bind.Run(vm.SearchCommand);
            else
                return false;
            return true;
        });
    }

    /// <summary>A saved video: its thumbnail, title and date; a click selects it, a double-click plays it.</summary>
    private static Control VideoRow(YoutubeWindowViewModel vm, YoutubeVideoItemViewModel video)
    {
        var button = new Button { ToggleMode = true, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 84), TooltipText = video.OpenUrl ?? string.Empty };
        button.AddThemeStyleboxOverride("normal", Eve.Eve.Box(Eve.Eve.Sunken(0.15f), Eve.Eve.Edge with { A = 0.15f }, 6));
        var thumb = Thumbnail(128, 72);
        Bind.When(thumb, video, () => thumb.Texture = AvaloniaBridge.ToTexture(video.Thumbnail), nameof(video.Thumbnail));
        var title = Eve.Eve.Text_(video.DisplayTitle, Eve.Eve.TextBright, 13, wrap: true);
        title.MaxLinesVisible = 2;
        title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var text = Ui.Fill(Ui.Column(3, title, Eve.Eve.Text_(video.Summary, Eve.Eve.Muted, 11)));
        text.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        var content = Ui.Row(10, Frame(thumb), text);
        Ignore(content);
        var margin = Ui.Margin(content, 6, 6, 8, 6);
        margin.MouseFilter = Control.MouseFilterEnum.Ignore;
        margin.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        button.AddChild(margin);
        button.Pressed += () => vm.SelectedVideo = video;
        button.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, DoubleClick: true, Pressed: true })
                Bind.Run(vm.PlayCommand, video);
            // A right click offers to annotate the video.
            else if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } right)
            {
                vm.SelectedVideo = video;
                Data.Annotations.ShowMenu(button, right.Position,
                    new AnnotationTarget(AnnotationTargetTypes.Video, video.VideoId ?? video.OpenUrl, video.DisplayTitle));
            }
        };
        Bind.When(button, vm, () => button.SetPressedNoSignal(ReferenceEquals(vm.SelectedVideo, video)), nameof(vm.SelectedVideo));
        return button;
    }

    private static void Ignore(Control control)
    {
        control.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (var child in control.GetChildren())
            if (child is Control c)
                Ignore(c);
    }

    private static TextureRect Thumbnail(int width, int height) => new()
    {
        ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
        CustomMinimumSize = new Vector2(width, height),
        ClipContents = true
    };

    /// <summary>A thumbnail in a dark frame.</summary>
    private static PanelContainer Frame(params Control[] content)
    {
        var frame = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ShrinkCenter, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
        frame.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.35f), Eve.Eve.Edge with { A = 0.35f }, 0));
        foreach (var child in content)
            frame.AddChild(child);
        return frame;
    }

    /// <summary>The editor of the video: address, preview and actions, title, description, note.</summary>
    private static (Control Editor, LineEdit Url) Editor(YoutubeWindowViewModel vm)
    {
        var url = Ui.Line(vm, nameof(vm.Url), "https://www.youtube.com/watch?v=…  ·  youtu.be/…  ·  shorts/…");
        var paste = Ui.Action("PASTE", () =>
        {
            var text = DisplayServer.ClipboardGet().Trim();
            if (text.Length > 0)
                vm.Url = text;
        }, tooltip: "Paste the address copied from the browser");

        // The thumbnail: a double-click plays the video.
        var preview = Thumbnail(256, 144);
        var placeholder = Eve.Eve.Text_("►", Eve.Eve.Text with { A = 0.25f }, 48);
        placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        var frame = Frame(placeholder, preview);
        frame.TooltipText = "Double-click to play the video (only the video, in its own window)";
        frame.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        preview.MouseFilter = Control.MouseFilterEnum.Ignore;
        placeholder.MouseFilter = Control.MouseFilterEnum.Ignore;
        frame.GuiInput += e =>
        {
            if (e is InputEventMouseButton { ButtonIndex: MouseButton.Left, DoubleClick: true, Pressed: true })
                Bind.Run(vm.PlayCommand);
        };
        Bind.When(preview, vm, () =>
        {
            preview.Texture = AvaloniaBridge.ToTexture(vm.Thumbnail);
            placeholder.Visible = preview.Texture is null;
        }, nameof(vm.Thumbnail));

        var play = Ui.Command("►  PLAY", vm.PlayCommand, tooltip: "Play the video in a window that shows only the video (or double-click it)");
        play.AddThemeStyleboxOverride("normal", Eve.Eve.Box(YoutubeRed with { A = 0.55f }, YoutubeRed, 6));
        play.AddThemeStyleboxOverride("hover", Eve.Eve.Box(YoutubeRed with { A = 0.75f }, YoutubeRed.Lightened(0.3f), 6));
        play.AddThemeStyleboxOverride("pressed", Eve.Eve.Box(YoutubeRed with { A = 0.9f }, YoutubeRed.Lightened(0.3f), 6));
        play.AddThemeColorOverride("font_color", Colors.White);
        play.CustomMinimumSize = new Vector2(170, 40);
        // The view model does not tell Play that it can run when the address changes (only Save and Open): asked here.
        Bind.When(play, vm, () => play.Disabled = !vm.PlayCommand.CanExecute(null), nameof(vm.Url));
        var save = Ui.Command("", vm.SaveCommand, primary: true, tooltip: "Save the video in Kaonomi (Ctrl+S)");
        Bind.Text(save, vm, nameof(vm.SaveLabel), v => v?.ToString()?.ToUpperInvariant() ?? string.Empty);
        save.CustomMinimumSize = new Vector2(170, 36);
        var open = Ui.Command("OPEN ON YOUTUBE", vm.OpenCommand, tooltip: "Open the page of the video on YouTube in the browser");
        var delete = Ui.Command("DELETE", vm.DeleteCommand, tooltip: "Delete the video from Kaonomi");
        var fetching = Bind.Visible(Ui.Row(8,
            new ProgressBar { Indeterminate = true, ShowPercentage = false, CustomMinimumSize = new Vector2(80, 4), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter },
            Eve.Eve.Text_("Reading YouTube…", Eve.Eve.Muted, 11)), vm, nameof(vm.IsFetching));
        var channel = Ui.Label(vm, nameof(vm.Author), Eve.Eve.Muted, 11, format: v => $"Channel: {v}");
        Bind.When(channel, vm, () => channel.Visible = !string.IsNullOrEmpty(vm.Author), nameof(vm.Author));
        var actions = Ui.Column(10, play, open, save, delete, fetching, channel);
        actions.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;

        var description = Ui.Lines(vm, nameof(vm.Description), 220);
        var note = Ui.Lines(vm, nameof(vm.Comment), 60, "Why this video (optional, saved in comment)");
        var title = Ui.Line(vm, nameof(vm.Title));

        var fields = Ui.Column(12,
            Ui.Field("YouTube address", Ui.Row(8, url, paste)),
            Ui.Row(16, frame, actions),
            Ui.Field("Title", title),
            Ui.Field("Description", description),
            Ui.Field("Note", note));
        return (Ui.Scroll(Ui.Margin(fields, 18, 8, 18, 18)), url);
    }
}
