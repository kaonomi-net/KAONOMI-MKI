using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Media;

/// <summary>
/// /youtube: the videos saved in Kaonomi. Search on their title and description (Enter, Search or F5; empty: the latest
/// videos), New video (Ctrl+N). The videos found on the left (thumbnail, title, date; a click opens one in the editor, a
/// double-click plays it, a right click annotates it). The editor: the YouTube address (Paste puts the one of the
/// clipboard; the title, description and channel are read from YouTube), the thumbnail (a double-click plays the
/// video), ▶ Play (the video alone, in a browser window opened by <see cref="KAONOMI.Chat.Services.YoutubePlayer"/>),
/// Open on YouTube (the system browser), Add to Kaonomi / Save (Ctrl+S), Delete (after a confirmation), the title, the
/// description and a note.
/// </summary>
[ToolView(typeof(YoutubeWindowViewModel), "YouTube", "📺", 1200, 760)]
public sealed class YoutubeView : IToolView
{
    private static readonly IBrush YoutubeRed = Kit.Brush("#FF0033");

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (YoutubeWindowViewModel)viewModel;
        vm.ConfirmAsync = (message, label) => Dialogs.Confirm("YouTube", message, label);

        var term = Ui.Edit(vm, nameof(vm.Term), "Search the videos of Kaonomi (title and description) · empty: latest videos");
        term.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Ui.Run(vm.SearchCommand);
                e.Handled = true;
            }
        };
        var toolbar = Kit.Toolbar(Kit.FillRow(6, [], term,
            Ui.Command("SEARCH", vm.SearchCommand, primary: true),
            Ui.Command("NEW", vm.NewCommand, tooltip: "Add another video (Ctrl+N)")));

        // Saved videos.
        var list = Kit.Items(vm.Videos, video => VideoRow(vm, video));
        var empty = Kit.EmptyWhen(Kit.Placeholder("No video in Kaonomi yet"), vm.Videos);
        var left = new Grid { Children = { Ui.Scroll(Kit.Margin(list, 4)), empty } };

        var (editor, urlBox) = Editor(vm);
        var page = Kit.Rows("Auto,*,Auto", toolbar, Kit.SplitH(left, editor, "380", "*", 300, 400), Kit.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError)));
        Kit.FocusWhenShown(urlBox);
        page.DetachedFromVisualTree += (_, _) => vm.ConfirmAsync = (_, _) => Task.FromResult(false);
        page.AttachedToVisualTree += (_, _) => vm.ConfirmAsync = (message, label) => Dialogs.Confirm("YouTube", message, label);
        Kit.Keys(page, e =>
        {
            if (Kit.IsCommand(e, Key.S))
                Ui.Run(vm.SaveCommand);
            else if (Kit.IsCommand(e, Key.N))
                Ui.Run(vm.NewCommand);
            else if (e.Key == Key.F5)
                Ui.Run(vm.SearchCommand);
            else
                return false;
            return true;
        });
        return page;
    }

    /// <summary>A saved video: its thumbnail, title and date; a click selects it, a double-click plays it, a right click annotates it.</summary>
    private static Control VideoRow(YoutubeWindowViewModel vm, YoutubeVideoItemViewModel video)
    {
        var thumb = new Image { Stretch = Stretch.UniformToFill };
        thumb.Bind(Image.SourceProperty, video, nameof(video.Thumbnail));
        var title = Ui.Label(video.DisplayTitle, Ui.TextBright, 13, wrap: true);
        title.MaxLines = 2;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        var text = Ui.Column(3, title, Ui.Label(video.Summary, Ui.Muted, 11));
        text.VerticalAlignment = VerticalAlignment.Center;
        var row = new Border
        {
            Child = Kit.FillRow(10, [Frame(thumb, 128, 72)], text),
            Padding = new Thickness(6),
            Background = Ui.Field,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            MinHeight = 84,
            Cursor = new Cursor(StandardCursorType.Hand)
        };
        if (!string.IsNullOrEmpty(video.OpenUrl))
            ToolTip.SetTip(row, video.OpenUrl);
        row.PointerPressed += (_, e) =>
        {
            if (Kit.IsRight(e, row))
            {
                vm.SelectedVideo = video;
                Kit.AnnotateMenu(row, new AnnotationTarget(AnnotationTargetTypes.Video, video.VideoId ?? video.OpenUrl, video.DisplayTitle));
            }
            else if (Kit.IsLeft(e, row))
            {
                vm.SelectedVideo = video;
                if (e.ClickCount >= 2)
                    Ui.Run(vm.PlayCommand, video);
            }
            e.Handled = true;
        };
        Ui.When(row, vm, () => row.BorderBrush = ReferenceEquals(vm.SelectedVideo, video) ? Ui.Accent : Ui.Edge, nameof(vm.SelectedVideo));
        return row;
    }

    /// <summary>A thumbnail in a dark frame.</summary>
    private static Border Frame(Control content, double width, double height) => new()
    {
        Child = content,
        Width = width,
        Height = height,
        Background = Ui.Field,
        BorderBrush = Ui.Edge,
        BorderThickness = new Thickness(1),
        ClipToBounds = true,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <summary>The editor of the video: address, preview and actions, title, description, note.</summary>
    private static (Control Editor, TextBox Url) Editor(YoutubeWindowViewModel vm)
    {
        var url = Ui.Edit(vm, nameof(vm.Url), "https://www.youtube.com/watch?v=…  ·  youtu.be/…  ·  shorts/…");
        var paste = Ui.Button("PASTE", null, "Paste the address copied from the browser");
        paste.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(paste)?.Clipboard is not { } clipboard)
                return;
            var text = (await clipboard.TryGetTextAsync())?.Trim();
            if (!string.IsNullOrEmpty(text))
                vm.Url = text;
        };

        // The thumbnail: a double-click plays the video.
        var preview = new Image { Stretch = Stretch.UniformToFill };
        preview.Bind(Image.SourceProperty, vm, nameof(vm.Thumbnail));
        var placeholder = Ui.Label("►", Ui.Edge, 48);
        placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        Ui.When(placeholder, vm, () => placeholder.IsVisible = vm.Thumbnail is null, nameof(vm.Thumbnail));
        var frame = Frame(new Grid { Children = { placeholder, preview } }, 256, 144);
        frame.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(frame, "Double-click to play the video (only the video, in its own window)");
        frame.DoubleTapped += (_, _) => Ui.Run(vm.PlayCommand);

        // The view model does not tell Play that it can run when the address changes (only Save and Open): asked here.
        var play = new Button
        {
            Content = Ui.Label("►  PLAY", Brushes.White, 13, bold: true),
            Background = YoutubeRed,
            BorderBrush = YoutubeRed,
            MinWidth = 170,
            MinHeight = 40,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(play, "Play the video in a window that shows only the video (or double-click it)");
        play.Click += (_, _) => Ui.Run(vm.PlayCommand);
        void Refresh() => play.IsEnabled = vm.PlayCommand.CanExecute(null);
        Ui.When(play, vm, Refresh, nameof(vm.Url));
        vm.PlayCommand.CanExecuteChanged += (_, _) => Ui.OnMain(Refresh);

        var save = Ui.Command(string.Empty, vm.SaveCommand, tooltip: "Save the video in Kaonomi (Ctrl+S)", primary: true);
        save.Bind(ContentControl.ContentProperty, vm, nameof(vm.SaveLabel), Avalonia.Data.BindingMode.OneWay, v => v?.ToString()?.ToUpperInvariant() ?? string.Empty);
        save.MinWidth = 170;
        save.MinHeight = 36;
        var open = Ui.Command("OPEN ON YOUTUBE", vm.OpenCommand, tooltip: "Open the page of the video on YouTube in the browser");
        var delete = Ui.Command("DELETE", vm.DeleteCommand, tooltip: "Delete the video from Kaonomi");
        foreach (var button in new[] { save, open, delete })
            button.HorizontalContentAlignment = HorizontalAlignment.Center;
        var fetching = Ui.Row(8,
            new ProgressBar { IsIndeterminate = true, Width = 80, Height = 4, MinHeight = 4, VerticalAlignment = VerticalAlignment.Center },
            Ui.Label("Reading YouTube…", Ui.Muted, 11)).Visible(vm, nameof(vm.IsFetching));
        var channel = Kit.Text(vm, nameof(vm.Author), v => $"Channel: {v}", Ui.Muted, 11);
        Ui.When(channel, vm, () => channel.IsVisible = !string.IsNullOrEmpty(vm.Author), nameof(vm.Author));
        var actions = Ui.Column(10, play, open, save, delete, fetching, channel);
        actions.VerticalAlignment = VerticalAlignment.Center;

        var description = Kit.Lines(vm, nameof(vm.Description), 220);
        var note = Kit.Lines(vm, nameof(vm.Comment), 60, "Why this video (optional, saved in comment)");
        var title = Ui.Edit(vm, nameof(vm.Title));

        var fields = Ui.Column(12,
            Kit.Field("YouTube address", Kit.FillRow(8, [], url, paste)),
            Ui.Row(16, frame, actions),
            Kit.Field("Title", title),
            Kit.Field("Description", description),
            Kit.Field("Note", note));
        return (Ui.Scroll(Kit.Margin(fields, 18, 8, 18, 18)), url);
    }
}
