using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Writing;

/// <summary>
/// The results of the web scraper agent for a collection plan (🗂 RESULTS of /collectionplan): the pages it saved on
/// the left, the newest first (date, website, pictures, paragraphs, title, address); the selected one on the right: its
/// title, its address (opened in the browser), what the visit found, its screenshot, its pictures as thumbnails and its
/// text. A click on the screenshot or a picture opens it in the image analysis at its real size, scrolled in the window.
/// REFRESH reads the pages again (the agent may have saved new ones).
/// </summary>
[ToolView(typeof(WebScrapeResultsWindowViewModel), "Scraper results", "🗂", 1250, 820)]
public sealed class WebScrapeResultsView : IToolView
{
    private const int ThumbnailWidth = 180, ThumbnailHeight = 130;

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (WebScrapeResultsWindowViewModel)viewModel;
        var loader = new ThumbnailLoader();

        var planName = Eve.Eve.Text_($"🕷 {vm.PlanName}", Eve.Eve.TextBright, 14);
        planName.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        planName.ClipText = true;
        var toolbar = Ui.Row(8, planName,
            Ui.Command("REFRESH", vm.RefreshCommand, tooltip: "Read the pages of the plan again (the agent may have saved new ones)"));

        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 2);
        var empty = Ui.Placeholder("No page saved for this plan");
        Bind.Items(list, vm.Pages, page => PageRow(vm, page), empty);
        var left = Ui.Column(0, Ui.FillHeight(Ui.Scroll(list)), empty);
        left.CustomMinimumSize = new Vector2(320, 0);

        var detail = Ui.Swap(vm, nameof(vm.SelectedPage), value =>
            value is WebPageResultViewModel page ? Detail(vm, page, shell, loader) : Ui.Placeholder("Select a page"));

        var root = Ui.Column(6, Ui.Toolbar(toolbar), Ui.SplitH(left, detail, 380), WritingUi.StatusBar(vm, nameof(vm.Status), vm));
        Bind.OnGone(root, loader.Dispose);
        return root;
    }

    /// <summary>A page of the list: date, website, counts; its title; its address.</summary>
    private static Control PageRow(WebScrapeResultsWindowViewModel vm, WebPageResultViewModel page)
    {
        var url = WritingUi.Clamp(Eve.Eve.Text_(page.Url, Eve.Eve.Muted, 10), 1);
        var content = Ui.Column(2,
            Ui.Row(6, Eve.Eve.Text_(page.Summary, Eve.Eve.Muted, 10), Bind.Visible(Ui.Badge("⚠", Eve.Eve.Amber), page, nameof(page.HasError))),
            WritingUi.Clamp(Eve.Eve.Text_(page.Title, Eve.Eve.TextBright, 13), 2),
            url);
        return WritingUi.ResultRow(vm, nameof(vm.SelectedPage), () => ReferenceEquals(vm.SelectedPage, page), () => vm.SelectedPage = page, content);
    }

    /// <summary>The page shown: title, address, details, warnings, screenshot, pictures and text.</summary>
    private static Control Detail(WebScrapeResultsWindowViewModel vm, WebPageResultViewModel page, Shell shell, ThumbnailLoader loader)
    {
        void Open(string path)
        {
            if (vm.OpenImage(path) is { } analysis)
                shell.Open(analysis, title: analysis.Title);
        }

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);

        column.AddChild(Eve.Eve.Text_(page.Title, Eve.Eve.TextBright, 16, wrap: true));
        if (page.Url.Length > 0)
        {
            var link = new LinkButton { Text = page.Url, Uri = page.Url, TooltipText = "Open the page in the browser", Underline = LinkButton.UnderlineMode.OnHover };
            link.AddThemeFontSizeOverride("font_size", 12);
            link.AddThemeColorOverride("font_color", Eve.Eve.Cyan);
            column.AddChild(link);
        }
        column.AddChild(Eve.Eve.Text_(page.Details, Eve.Eve.Muted, 11, wrap: true));
        if (page.HasError)
            column.AddChild(Eve.Eve.Text_("⚠ " + page.Error, Eve.Eve.Amber, 11, wrap: true));

        // The screenshot of the whole page.
        var folderButtons = Ui.Row(6);
        if (page.ImagesFolder.Length > 0 && Directory.Exists(page.ImagesFolder))
            folderButtons.AddChild(Ui.Action("📁 PICTURES FOLDER", () => PictureLibrary.OpenWithSystem(page.ImagesFolder), tooltip: page.ImagesFolder));
        if (page.HasScreenshot)
        {
            column.AddChild(Section("Screenshot", folderButtons));
            column.AddChild(Thumbnail(page.ScreenshotPath, $"Screenshot of the whole page\n{page.ScreenshotPath}\nClick: open it at its real size in the image analysis",
                loader, () => Open(page.ScreenshotPath), 2));
        }
        else
            column.AddChild(Section("Screenshot", folderButtons, Eve.Eve.Text_(page.ScreenshotPath.Length > 0 ? "Not on this machine" : "None", Eve.Eve.Muted, 11)));

        // The pictures of the page.
        column.AddChild(Section($"Pictures ({page.Images.Count})"));
        if (page.Images.Count == 0)
            column.AddChild(Eve.Eve.Text_("No picture kept for this page", Eve.Eve.Muted, 11));
        else
        {
            var flow = new HFlowContainer();
            flow.AddThemeConstantOverride("h_separation", 8);
            flow.AddThemeConstantOverride("v_separation", 8);
            foreach (var image in page.Images)
            {
                var item = image;
                flow.AddChild(item.Exists
                    ? Thumbnail(item.Path, item.Tooltip, loader, () => Open(item.Path), 1)
                    : Missing(item));
            }
            column.AddChild(flow);
        }

        // The text of the page (its paragraphs).
        column.AddChild(Section("Text"));
        var text = new TextEdit
        {
            Text = page.Text,
            Editable = false,
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            CustomMinimumSize = new Vector2(0, 320),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            ScrollFitContentHeight = true
        };
        text.AddThemeFontSizeOverride("font_size", 12);
        column.AddChild(text);

        return Ui.Scroll(Ui.Margin(column, 12, 8, 16, 12));
    }

    /// <summary>The caption of a part of the page, with its buttons on the right.</summary>
    private static Control Section(string caption, params Control[] right)
    {
        var label = Eve.Eve.Caption(caption, Eve.Eve.Amber, 11);
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var row = Ui.Row(6, label);
        foreach (var control in right)
            row.AddChild(control);
        return Ui.Column(4, Eve.Eve.Separator(), row);
    }

    /// <summary>
    /// The thumbnail of the picture at <paramref name="path"/> (<paramref name="scale"/> times the usual size), opened
    /// by a click.
    /// </summary>
    private static Button Thumbnail(string path, string tooltip, ThumbnailLoader loader, Action open, int scale)
    {
        var size = new Vector2(ThumbnailWidth * scale, ThumbnailHeight * scale);
        var button = new Button
        {
            CustomMinimumSize = size,
            TooltipText = tooltip,
            Flat = true,
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            ClipContents = true,
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin
        };
        button.AddThemeStyleboxOverride("normal", Eve.Eve.Box(Eve.Eve.Sunken(0.25f), Eve.Eve.Edge with { A = 0.35f }, 2));
        button.AddThemeStyleboxOverride("hover", Eve.Eve.Box(Eve.Eve.Sunken(0.25f), Eve.Eve.Cyan, 2));
        button.AddThemeStyleboxOverride("pressed", Eve.Eve.Box(Eve.Eve.Sunken(0.35f), Eve.Eve.Cyan, 2));
        var picture = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = scale > 1 ? TextureRect.StretchModeEnum.KeepAspect : TextureRect.StretchModeEnum.KeepAspectCovered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        picture.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        picture.OffsetLeft = picture.OffsetTop = 2;
        picture.OffsetRight = picture.OffsetBottom = -2;
        var name = Eve.Eve.Text_(Path.GetExtension(path).TrimStart('.').ToUpperInvariant(), Eve.Eve.Muted, 11);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.VerticalAlignment = VerticalAlignment.Center;
        name.MouseFilter = Control.MouseFilterEnum.Ignore;
        name.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        button.AddChild(name);
        button.AddChild(picture);
        loader.Load(path, (int)Math.Max(size.X, size.Y) * 2, texture =>
        {
            if (!GodotObject.IsInstanceValid(picture))
                return;
            picture.Texture = texture;
            name.Visible = false;
        });
        button.Pressed += open;
        return button;
    }

    /// <summary>A picture of the page that was not downloaded (or is not on this machine): its address and why.</summary>
    private static Control Missing(WebPageImageItem image)
    {
        var label = Eve.Eve.Text_(image.Error.Length > 0 ? "⚠ not downloaded" : "Not on this machine", Eve.Eve.Muted, 10, wrap: true);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(ThumbnailWidth, ThumbnailHeight), TooltipText = image.Tooltip.Replace("Click: open it at its real size in the image analysis", "").TrimEnd() };
        panel.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.15f), Eve.Eve.Edge with { A = 0.25f }, 6));
        panel.AddChild(label);
        return panel;
    }

    /// <summary>Loads the thumbnails off the main thread, two at a time; stopped with the window.</summary>
    private sealed class ThumbnailLoader : IDisposable
    {
        private static readonly SemaphoreSlim Gate = new(2);
        private readonly CancellationTokenSource _cancel = new();

        public void Load(string path, int size, Action<Texture2D> done)
        {
            var token = _cancel.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Gate.WaitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                try
                {
                    if (token.IsCancellationRequested || !File.Exists(path))
                        return;
                    var image = new Image();
                    if (image.Load(path) != Error.Ok || image.IsEmpty())
                        return;
                    var scale = Math.Min(1.0, (double)size / Math.Max(image.GetWidth(), image.GetHeight()));
                    if (scale < 1)
                        image.Resize(Math.Max(1, (int)(image.GetWidth() * scale)), Math.Max(1, (int)(image.GetHeight() * scale)), Image.Interpolation.Bilinear);
                    Bind.OnMain(() =>
                    {
                        if (!token.IsCancellationRequested)
                            done(ImageTexture.CreateFromImage(image));
                    });
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException)
                {
                    // No thumbnail: the format is shown instead.
                }
                finally
                {
                    Gate.Release();
                }
            }, token);
        }

        public void Dispose() => _cancel.Cancel();
    }
}
