using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;
using KAONOMI.UI.Tools.Work;

namespace KAONOMI.UI.Tools.Writing;

/// <summary>
/// /wiki: the wiki of Kaonomi. On the left, the search of the pages (title, content, tags; empty: all of them by title) and
/// their list, a click opens one; under it the pages that link to the one open. On the right, ◀ (back), ⌂ (home), the title
/// of the page, its tags and its version, then the page itself, edited as it reads with the editor and the ribbon of /work: Home (Page: New, Save,
/// Delete; font, paragraph, styles), Insert (pictures, copied into Pictures/kaonomi/wiki; Wiki: a link to a page, or to a new
/// page made from the selection; links; code), View (zoom). A click on a link to a page (wiki:slug:; ⌥/Alt + click edits it) opens it, or starts it when it does not exist; a web link opens in the browser
/// of the chat. ⌘/Ctrl S saves, ⌘/Ctrl K makes a link. The title of the window is the title of the page.
/// </summary>
[ToolView(typeof(WikiWindowViewModel), "Wiki", "📚", 1320, 860)]
public sealed class WikiView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (WikiWindowViewModel)viewModel;
        vm.ConfirmAsync = WritingUi.Confirm;

        // A click on a link follows it, as in any wiki (⌥/Alt + click places the caret in it to edit it).
        var tape = new TapePage(shell, vm, "Wiki", "the wiki page", "wiki", _ => WikiWindowViewModel.WikiPicturesDirectory,
            "Write the page here… select text to format it (the ribbon above, or ⌘/Ctrl B, I, U); Insert › Wiki links it to another page.",
            linksOpenOnClick: true);
        var editor = tape.Editor;
        var ribbon = tape.Ribbon;
        tape.Bind(vm, nameof(vm.Content));
        editor.LoadImage = vm.LoadImageAsync;
        editor.OpenImage = path => OpenImage(vm, shell, path);
        // The links: to a page of the wiki (it opens), to the web (the browser of the chat), to a file (the system, confirmed).
        editor.OpenLink = link =>
        {
            if (link.StartsWith(WikiPageEntry.LinkScheme, StringComparison.OrdinalIgnoreCase))
            {
                _ = vm.OpenPageAsync(link);
                return;
            }
            if (Uri.TryCreate(link, UriKind.Absolute, out var uri) && !uri.IsFile && uri.Scheme is "http" or "https")
            {
                PictureLibrary.OpenWeb(link);
                return;
            }
            ribbon.OpenLink(link);
        };

        // ── Left: the pages and the backlinks ──
        var search = WritingUi.Line(vm, nameof(vm.Search), "Search the pages (title, content, tags)… · Enter");
        search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Ui.Run(vm.SearchCommand);
                e.Handled = true;
            }
        };
        var searchButton = WritingUi.Command("⟳", vm.SearchCommand, tooltip: "Search (Enter); empty: every page, by title");
        searchButton.Margin = new Thickness(6, 0, 0, 0);
        var pages = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var noPage = WritingUi.Placeholder("No page yet");
        noPage.VerticalAlignment = VerticalAlignment.Top;
        WritingUi.Items(pages, vm.Pages, page => PageRow(vm, page, 54), noPage);
        var backlinks = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        var noBacklink = WritingUi.Placeholder("No page links here");
        noBacklink.VerticalAlignment = VerticalAlignment.Top;
        WritingUi.Items(backlinks, vm.Backlinks, page => PageRow(vm, page, 36), noBacklink);
        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,3*,Auto,*"), RowSpacing = 8, MinWidth = 260 };
        var searchRow = Ui.Dock((searchButton, Dock.Right), (search, Dock.Left));
        var pagesArea = new Panel { Children = { Ui.Scroll(pages), noPage } };
        var linkedFrom = Ui.Caption("Linked from", Ui.Muted, 11);
        var backArea = new Panel { Children = { Ui.Scroll(backlinks), noBacklink } };
        Grid.SetRow(pagesArea, 1);
        Grid.SetRow(linkedFrom, 2);
        Grid.SetRow(backArea, 3);
        left.Children.Add(searchRow);
        left.Children.Add(pagesArea);
        left.Children.Add(linkedFrom);
        left.Children.Add(backArea);
        var leftHolder = WritingUi.Margin(left, 10, 10, 6, 10);

        // ── Right: the head of the page, the ribbon, the page ──
        var back = WritingUi.Command("◀", vm.BackCommand, tooltip: "Back to the page seen before");
        var home = WritingUi.Command("⌂", vm.HomeCommand, tooltip: $"The home page of the wiki ({WikiWindowViewModel.HomeSlug})");
        var title = WritingUi.Line(vm, nameof(vm.Title), "Title of the page");
        title.FontSize = 20;
        // The tags, the events and the signals of the page, one line each (see MetaTags).
        TextBox Meta(string property, string hint)
        {
            var line = WritingUi.Line(vm, property, hint);
            line.Width = 170;
            line.Margin = new Thickness(8, 0, 0, 0);
            line.VerticalAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(line, hint + " · separated by spaces or commas, '-' inside one");
            return line;
        }
        var tags = Meta(nameof(vm.Tags), MetaTags.TagsHint);
        var events = Meta(nameof(vm.Events), MetaTags.EventsHint);
        var signals = Meta(nameof(vm.Signals), MetaTags.SignalsHint);
        var buttons = Ui.Row(8, back, home);
        buttons.Margin = new Thickness(0, 0, 8, 0);
        buttons.VerticalAlignment = VerticalAlignment.Center;
        var headRow = Ui.Dock((buttons, Dock.Left), (signals, Dock.Right), (events, Dock.Right), (tags, Dock.Right), (title, Dock.Left));
        var info = Ui.Bound(vm, nameof(vm.Info), Ui.Muted, 11);
        var head = Ui.Column(2, headRow, info);
        head.Margin = new Thickness(0, 0, 0, 8);

        // The groups of the page and of the links between the pages.
        var newPage = TapeRibbon.BigTool("📄", "New", "A new page", () => Ui.Run(vm.NewPageCommand));
        var save = TapeRibbon.BigTool("💾", "Save", "Save the page (a new version each time) · ⌘/Ctrl S", () => Ui.Run(vm.SaveCommand));
        var delete = TapeRibbon.BigTool("🗑", "Delete", "Delete the page (it leaves the wiki, kept marked deleted in the search engine)", () => Ui.Run(vm.DeleteCommand));
        var pageGroup = TapeRibbon.Group("Page", false, newPage, save, delete);
        Button? linkPage = null;
        linkPage = TapeRibbon.BigTool("📚", "Page link", "Link the selection (or insert a link) to a page of the wiki", () => ShowPageMenu(vm, editor, linkPage!));
        var newLink = TapeRibbon.BigTool("✚", "New page link", "Link the selected words to a page of that name (it is started when the link is followed)", () =>
        {
            var words = editor.SelectedText.Trim();
            if (words.Length == 0 || words.Contains('\n'))
            {
                ribbon.ShowStatus("Select the words of the title of the new page first", WritingUi.Amber);
                return;
            }
            editor.SetLink(WikiPageEntry.LinkScheme + WikiPageEntry.Slug(words));
            ribbon.UpdateToolbar();
            ribbon.ShowStatus($"Linked to the page \"{words}\" (follow the link to write it)");
        });
        var wikiGroup = TapeRibbon.Group("Wiki", false, linkPage, newLink);
        var picturesTooltip = $"Insert pictures at the caret (copied into {WikiWindowViewModel.WikiPicturesDirectory}); resize them with the grip of their corner";
        var page = tape.Build(
            ("Home", [pageGroup, ribbon.FontGroup(), ribbon.ParagraphGroup(), ribbon.StylesGroup(last: true)]),
            ("Insert", [ribbon.PicturesGroup(picturesTooltip, () => _ = InsertPicturesAsync(vm, editor),
                $"Generate an image with Fooocus from a prompt, then insert it at the caret (saved in {WikiWindowViewModel.WikiPicturesDirectory})"),
                wikiGroup, ribbon.LinksGroup(), ribbon.CodeGroup()]),
            ("View", [ribbon.ZoomGroup()]));
        var rightHolder = WritingUi.Margin(WritingUi.Page([head], page), 6, 10, 10, 10);

        var split = WritingUi.SplitH(leftHolder, rightHolder, 300, minLeft: 240, minRight: 400);

        // Status: error dot, busy, the text, not saved.
        var dirty = Ui.Label("● not saved", WritingUi.Amber, 12).Visible(vm, nameof(vm.IsDirty));
        var status = WritingUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsBusy), extra: dirty);
        var root = WritingUi.Page([], split, status);

        // The title of the window: the title of the page.
        void UpdateTitle()
        {
            var text = vm.Title.Trim();
            WritingUi.SetTitle(root, text.Length == 0 ? "Wiki" : $"Wiki · {text}");
        }
        Ui.When(root, vm, UpdateTitle, nameof(vm.Title));
        WritingUi.WhenInWindow(root, UpdateTitle);

        // ⌘/Ctrl S saves, ⌘/Ctrl K makes a link.
        WritingUi.Shortcuts(root, key =>
        {
            if (WritingUi.IsCommand(key, Key.S))
                return WritingUi.Run(vm.SaveCommand);
            if (WritingUi.IsCommand(key, Key.K) && editor.IsFocused)
            {
                ribbon.ShowLinkFlyout();
                return true;
            }
            return false;
        });

        // Pictures dropped from the file manager: copied into the wiki, then inserted where they fall (or at the caret).
        DragDrop.SetAllowDrop(root, true);
        root.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var pictures = vm.CopyPictures(WritingFiles.Dropped(e).Where(PictureLibrary.IsPicture));
            if (pictures.Count == 0)
                return;
            tape.InsertDropped(pictures, e);
            vm.Status = $"{pictures.Count} picture(s) copied into the wiki and inserted";
            e.Handled = true;
        });
        return root;
    }

    /// <summary>A page of a list: its title, its date and the start of its text; the page open is outlined.</summary>
    private static Control PageRow(WikiWindowViewModel vm, WikiPageEntry page, double height)
    {
        var title = Ui.Label(page.Title, Ui.TextBright, 13);
        Control content = title;
        if (height > 40)
        {
            var date = Ui.Label(page.DateLabel, Ui.Muted, 10);
            date.Margin = new Thickness(6, 0, 0, 0);
            var summary = WritingUi.Clamp(Ui.Label(page.Summary, Ui.Text, 11), 1);
            content = Ui.Column(1, Ui.Dock((date, Dock.Right), (title, Dock.Left)), summary);
        }
        var row = WritingUi.ResultRow(vm, nameof(vm.PageId), () => vm.PageId is not null && vm.PageId == page.Id,
            () => Ui.Run(vm.OpenEntryCommand, page), content, height);
        ToolTip.SetTip(row, page.Summary.Length > 0 ? page.Summary : page.Title);
        return row;
    }

    /// <summary>📚 Page link: the pages of the wiki in a menu; the one chosen links the selection, or is inserted as a link.</summary>
    private static void ShowPageMenu(WikiWindowViewModel vm, TapeEditor editor, Control anchor)
    {
        var linkSelection = editor.HasSelectedContent && !editor.SelectedText.Contains('\n');
        var menu = new MenuFlyout();
        var pages = vm.Pages.Where(p => p.Id is not null && p.Id != vm.PageId).OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase).Take(60).ToList();
        if (pages.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No other page (search the pages on the left first)", IsEnabled = false });
        foreach (var page in pages)
        {
            var target = page;
            var item = new MenuItem { Header = "📄 " + target.Title };
            item.Click += (_, _) =>
            {
                var address = WikiPageEntry.LinkScheme + target.Id;
                if (linkSelection)
                    editor.SetLink(address);
                else
                    editor.InsertLink(target.Title, address);
            };
            menu.Items.Add(item);
        }
        menu.ShowAt(anchor);
    }

    /// <summary>🖼: pictures picked, copied into the folder of the wiki, inserted at the caret.</summary>
    private static async Task InsertPicturesAsync(WikiWindowViewModel vm, TapeEditor editor)
    {
        var files = await Dialogs.OpenFiles("Insert pictures", WritingUi.PictureFilters, vm.PicturesDirectory);
        if (files.Length == 0)
            return;
        var copies = vm.CopyPictures(files);
        if (copies.Count == 0)
            return;
        editor.InsertImages(copies);
        vm.Status = $"{copies.Count} picture(s) copied into the wiki and inserted; right click a picture of the page to change its width";
    }

    /// <summary>A double-click on a picture: the image analysis window of /pictures (or the viewer of the system).</summary>
    private static void OpenImage(WikiWindowViewModel vm, Shell shell, string path)
    {
        if (vm.CreateAnalysis is not { } create || !System.IO.File.Exists(path))
        {
            PictureLibrary.OpenWithSystem(path);
            return;
        }
        shell.Open(create(path));
    }
}
