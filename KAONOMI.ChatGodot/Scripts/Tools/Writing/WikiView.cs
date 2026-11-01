using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using KAONOMI.ChatGodot.Tools.Knowledge;
using KAONOMI.ChatGodot.Tools.Work;

namespace KAONOMI.ChatGodot.Tools.Writing;

/// <summary>
/// /wiki: the wiki of Kaonomi. On the left, the search of the pages (title, content, tags; empty: all of them by
/// title) and their list, a click opens one; under it the pages that link to the one open. On the right, ◀ (back),
/// ⌂ (home), the title of the page, its tags and its version, then the page itself with the editor and the ribbon of
/// the journal and of /work: Home (Page: New, Save, Delete; font, paragraph, styles), Insert (pictures, copied into
/// Pictures/kaonomi/wiki; Wiki: a link to a page, or to a new page made from the selection; links; code), View
/// (zoom). A click on a link to a page (wiki:slug) opens it, or starts it when it does not exist; a web link opens in
/// the browser of the chat (⌥/Alt + click places the caret in a link to edit it). ⌘/Ctrl S saves, ⌘/Ctrl K makes a link. The title of the window is the title of the page.
/// </summary>
[ToolView(typeof(WikiWindowViewModel), "Wiki", "📚", 1320, 860)]
public sealed class WikiView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (WikiWindowViewModel)viewModel;
        vm.ConfirmAsync = WritingUi.Confirm;

        var root = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var overlay = new Overlay();
        var editor = new TapeEditor
        {
            KeepSource = true,
            // A click on a link follows it, as in any wiki (⌥/Alt + click places the caret in it to edit it).
            LinksOpenOnClick = true,
            Placeholder = "Write the page here… select text to format it (the ribbon above, or ⌘/Ctrl B, I, U); Insert › Wiki links it to another page."
        };
        var transient = Eve.Eve.Text_("", Eve.Eve.Green, 11);
        var ribbon = new TapeRibbon(editor, overlay, shell, root, transient, vm, "Wiki", "the wiki page", "wiki", _ => WikiWindowViewModel.WikiPicturesDirectory);

        // The page, both ways (Markdown).
        editor.TextCommitted += text => vm.Content = text;
        Bind.When(editor, vm, () => editor.Text = vm.Content, nameof(vm.Content));
        editor.LoadImage = async path => AvaloniaBridge.ToTexture(await vm.LoadImageAsync(path));
        editor.OpenImage = path => OpenImage(vm, shell, path);
        // The links: to a page of the wiki (it opens), to the web (the browser of the chat), to a file (the system).
        editor.OpenLink = link =>
        {
            if (link.StartsWith(WikiPageEntry.LinkScheme, StringComparison.OrdinalIgnoreCase))
            {
                _ = vm.OpenPageAsync(link);
                return;
            }
            if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                Links.Open(link);
                return;
            }
            ribbon.OpenLink(link);
        };

        // The title of the window: the title of the page.
        void UpdateTitle()
        {
            if (!GodotObject.IsInstanceValid(root) || shell.WindowOf(root) is not { } window)
                return;
            var title = vm.Title.Trim();
            window.Title = title.Length == 0 ? "Wiki" : $"Wiki · {title}";
        }
        Bind.When(root, vm, UpdateTitle, nameof(vm.Title));
        root.Ready += () => Callable.From(UpdateTitle).CallDeferred();

        // ── Left: the pages and the backlinks ──
        var search = Ui.Line(vm, nameof(vm.Search), "Search the pages (title, content, tags)… · Enter");
        search.TextSubmitted += _ => Bind.Run(vm.SearchCommand);
        var pages = new VBoxContainer();
        pages.AddThemeConstantOverride("separation", 2);
        var noPage = Ui.Placeholder("No page yet");
        Bind.Items(pages, vm.Pages, page => PageRow(vm, page, 54), noPage);
        var backlinks = new VBoxContainer();
        backlinks.AddThemeConstantOverride("separation", 2);
        var noBacklink = Ui.Placeholder("No page links here");
        Bind.Items(backlinks, vm.Backlinks, page => PageRow(vm, page, 36), noBacklink);
        var pagesScroll = Ui.Scroll(Ui.Column(2, pages, noPage));
        pagesScroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        pagesScroll.SizeFlagsStretchRatio = 3;
        var backScroll = Ui.Scroll(Ui.Column(2, backlinks, noBacklink));
        backScroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var left = Ui.Column(8,
            Ui.Row(6, search, Ui.Command("⟳", vm.SearchCommand, tooltip: "Search (Enter); empty: every page, by title")),
            pagesScroll,
            Eve.Eve.Caption("Linked from", Eve.Eve.Muted, 11),
            backScroll);
        left.CustomMinimumSize = new Vector2(280, 0);
        var leftHolder = Ui.Margin(left, 10, 10, 0, 10);

        // ── Right: the head of the page, the ribbon, the page ──
        var back = Ui.Command("◀", vm.BackCommand, tooltip: "Back to the page seen before");
        var home = Ui.Command("⌂", vm.HomeCommand, tooltip: $"The home page of the wiki ({WikiWindowViewModel.HomeSlug})");
        var title = Ui.Line(vm, nameof(vm.Title), "Title of the page");
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeFontOverride("font", Eve.Eve.Display);
        title.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var tags = Ui.Line(vm, nameof(vm.Tags), "#TOPIC !ALERT ?QUESTION &LINK");
        tags.CustomMinimumSize = new Vector2(240, 0);
        tags.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        var info = Ui.Label(vm, nameof(vm.Info), Eve.Eve.Muted, 11);
        var headRow = Ui.Row(8, back, home, title, tags);
        var head = Ui.Column(2, headRow, info);

        // The groups of the page and of the links between the pages.
        var newPage = TapeRibbon.BigTool("📄", "New", "A new page", () => Bind.Run(vm.NewPageCommand));
        var save = TapeRibbon.BigTool("💾", "Save", "Save the page (a new version each time) · ⌘/Ctrl S", () => Bind.Run(vm.SaveCommand));
        var delete = TapeRibbon.BigTool("🗑", "Delete", "Delete the page (it leaves the wiki, kept marked deleted in the search engine)", () => Bind.Run(vm.DeleteCommand));
        var pageGroup = TapeRibbon.Group("Page", false, newPage, save, delete);
        Button? linkPage = null;
        linkPage = TapeRibbon.BigTool("📚", "Page link", "Link the selection (or insert a link) to a page of the wiki", () => ShowPageMenu(vm, editor, linkPage!));
        var newLink = TapeRibbon.BigTool("✚", "New page link", "Link the selected words to a page of that name (it is started when the link is followed)", () =>
        {
            var words = editor.SelectedText.Trim();
            if (words.Length == 0 || words.Contains('\n'))
            {
                ribbon.ShowStatus("Select the words of the title of the new page first", Eve.Eve.Amber);
                return;
            }
            editor.SetLink(WikiPageEntry.LinkScheme + WikiPageEntry.Slug(words));
            ribbon.UpdateToolbar();
            ribbon.ShowStatus($"Linked to the page \"{words}\" (follow the link to write it)");
        });
        var wikiGroup = TapeRibbon.Group("Wiki", false, linkPage, newLink);
        var picturesTooltip = $"Insert pictures at the caret (copied into {WikiWindowViewModel.WikiPicturesDirectory}); resize them with the grip of their corner";
        var ribbonPanel = ribbon.Build(transient,
            ("Home", [pageGroup, ribbon.FontGroup(), ribbon.ParagraphGroup(), ribbon.StylesGroup(last: true)]),
            ("Insert", [ribbon.PicturesGroup(picturesTooltip, () => _ = InsertPicturesAsync(vm, editor),
                $"Generate an image with Fooocus from a prompt, then insert it at the caret (saved in {WikiWindowViewModel.WikiPicturesDirectory})"),
                wikiGroup, ribbon.LinksGroup(), ribbon.CodeGroup()]),
            ("View", [ribbon.ZoomGroup()]));
        var scroll = Ui.Scroll(editor);
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var page = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        page.AddThemeStyleboxOverride("panel", Eve.Eve.Box(WorkPalette.Page, Eve.Eve.Edge with { A = 0.45f }, 0));
        page.AddChild(Ui.Column(0, ribbonPanel, scroll));
        var right = Ui.Column(8, head, page);
        right.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var rightHolder = Ui.Margin(right, 10, 10, 10, 10);
        rightHolder.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var split = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        split.AddChild(leftHolder);
        split.AddChild(rightHolder);

        // Status: error dot, not saved, the text.
        var status = Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsBusy));
        if (status.GetChild(0) is HBoxContainer statusRow)
        {
            var dirty = Bind.Visible(Eve.Eve.Text_("● not saved", Eve.Eve.Amber, 12), vm, nameof(vm.IsDirty));
            statusRow.AddChild(dirty);
            statusRow.MoveChild(dirty, statusRow.GetChildCount() - 2);
        }

        var content = Ui.Column(0, split, status);
        content.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        root.AddChild(content);
        root.AddChild(overlay);

        // Shortcuts: ⌘/Ctrl S saves, ⌘/Ctrl K makes a link.
        WritingUi.Shortcuts(root, key =>
        {
            if (WritingUi.IsCommand(key, Key.S))
                return WritingUi.Run(vm.SaveCommand);
            if (WritingUi.IsCommand(key, Key.K) && editor.HasFocus())
            {
                ribbon.ShowLinkFlyout();
                return true;
            }
            return false;
        });

        // Pictures dropped from the Finder on the window of the wiki (in front): copied, then inserted where they fall.
        var window = shell.Root.GetTree().Root;
        void OnFilesDropped(string[] files)
        {
            if (!GodotObject.IsInstanceValid(root) || shell.WindowOf(root) is not { IsFront: true })
                return;
            var pictures = vm.CopyPictures(files.Where(PictureLibrary.IsPicture));
            if (pictures.Count == 0)
                return;
            var mouse = editor.GetLocalMousePosition();
            if (new Rect2(Vector2.Zero, editor.Size).HasPoint(mouse))
                editor.InsertImagesAt(pictures, mouse);
            else
                editor.InsertImages(pictures);
            vm.Status = $"{pictures.Count} picture(s) copied into the wiki and inserted";
        }
        window.FilesDropped += OnFilesDropped;
        Bind.OnGone(root, () => window.FilesDropped -= OnFilesDropped);
        return root;
    }

    /// <summary>A page of a list: its title, its date and the start of its text; the page open is outlined.</summary>
    private static Control PageRow(WikiWindowViewModel vm, WikiPageEntry page, int height)
    {
        var title = Eve.Eve.Text_(page.Title, Eve.Eve.TextBright, 13);
        title.ClipText = true;
        title.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        Control content = title;
        if (height > 40)
        {
            var date = Eve.Eve.Text_(page.DateLabel, Eve.Eve.Muted, 10);
            var summary = WritingUi.Clamp(Eve.Eve.Text_(page.Summary, Eve.Eve.Text with { A = 0.7f }, 11), 1);
            content = Ui.Column(1, Ui.Row(6, title, date), summary);
        }
        var button = WritingUi.ResultRow(vm, nameof(vm.PageId), () => vm.PageId is not null && vm.PageId == page.Id,
            () => Bind.Run(vm.OpenEntryCommand, page), content, height);
        button.TooltipText = page.Summary.Length > 0 ? page.Summary : page.Title;
        return button;
    }

    /// <summary>📚 Page link: the pages of the wiki in a menu; the one chosen links the selection, or is inserted as a link.</summary>
    private static void ShowPageMenu(WikiWindowViewModel vm, TapeEditor editor, Control anchor)
    {
        var menu = new ContextMenu();
        var selection = editor.HasSelectedContent && !editor.SelectedText.Contains('\n');
        var pages = vm.Pages.Where(p => p.Id is not null && p.Id != vm.PageId).OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).Take(60).ToList();
        if (pages.Count == 0)
            menu.Item("No other page (search the pages on the left first)", () => { }, enabled: false);
        foreach (var page in pages)
        {
            var target = page;
            menu.Item("📄 " + target.Title, () =>
            {
                var address = WikiPageEntry.LinkScheme + target.Id;
                if (selection)
                    editor.SetLink(address);
                else
                    editor.InsertLink(target.Title, address);
            });
        }
        menu.Show(anchor, new Vector2(0, anchor.Size.Y));
    }

    /// <summary>🖼: pictures picked, copied into the folder of the wiki, inserted at the caret.</summary>
    private static async Task InsertPicturesAsync(WikiWindowViewModel vm, TapeEditor editor)
    {
        var files = await Dialogs.OpenFiles("Insert pictures",
            ["*.jpg,*.jpeg,*.png,*.gif,*.webp,*.bmp,*.heic,*.heif,*.tif,*.tiff ; Pictures", "* ; All files"], vm.PicturesDirectory);
        if (files.Length == 0 || !GodotObject.IsInstanceValid(editor))
            return;
        var copies = vm.CopyPictures(files);
        if (copies.Count == 0)
            return;
        editor.InsertImages(copies);
        vm.Status = $"{copies.Count} picture(s) copied into the wiki and inserted; drag the corner of a picture to resize it";
    }

    /// <summary>A double-click on a picture: the image analysis window of /pictures (or the viewer of the system).</summary>
    private static void OpenImage(WikiWindowViewModel vm, Shell shell, string path)
    {
        if (vm.CreateAnalysis is not { } create || !System.IO.File.Exists(path) || !Shell.HasView(typeof(ImageAnalysisViewModel)))
        {
            PictureLibrary.OpenWithSystem(path);
            return;
        }
        shell.Open(create(path));
    }
}
