using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Writing;

/// <summary>
/// /collectionplan: the collection plans of the web scraper, the latest ones listed when it opens, a search in all their
/// fields (Enter or Search; and, or, parentheses; empty: the latest ones), New (⌘/Ctrl N), the plans on the left (date,
/// project, websites, badges active / new / modified, title, description), the selected one editable on the right (name,
/// active, project, description, instructions, the websites with their URLs (address, mode fixed / explore / random,
/// keywords), comment) with its id, date, status, 🕷 RUN (the web scraper reads the websites, the model of the chat carries
/// out the instructions, the result opens as a new document of /work; its log under the header, CANCEL), Delete (confirmed;
/// only marked deleted), Clone, Discard changes and Save (⌘/Ctrl S).
/// </summary>
[ToolView(typeof(CollectionPlanWindowViewModel), "Collection plans", "🕷", 1100, 750)]
public sealed class CollectionPlanView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (CollectionPlanWindowViewModel)viewModel;
        vm.ConfirmAsync = WritingUi.Confirm;

        var search = WritingUi.SearchBar(vm, nameof(vm.Term), vm.SearchCommand, "Search the collection plans in all their fields (and, or, parentheses) · empty: latest plans", out var searchBox,
            WritingUi.Command("NEW", vm.NewCommand, tooltip: "Create a new collection plan (Ctrl+N / ⌘N)"));

        var left = EntryParts.List(vm.Results, entry => ResultRow(vm, entry), "No results");
        var editor = WritingUi.Swap(vm, nameof(vm.SelectedResult), value => value is CollectionPlanEntryViewModel entry ? Editor(vm, entry) : null);

        var page = WritingUi.Page([WritingUi.Toolbar(search)], WritingUi.SplitH(left, editor, 340, minLeft: 260),
            WritingUi.StatusBar(vm, nameof(vm.Status), "IsError", "IsRunning", (System.ComponentModel.INotifyPropertyChanged)vm.SearchCommand));

        WritingUi.Shortcuts(page, key =>
        {
            if (WritingUi.IsCommand(key, Key.S))
                return WritingUi.Run(vm.SelectedResult?.SaveCommand);
            if (WritingUi.IsCommand(key, Key.N))
                return WritingUi.Run(vm.NewCommand);
            return false;
        });
        WritingUi.FocusWhenShown(searchBox);

        // RUN done: its document opens in /work.
        Action<DocumentEntry> created = document => Ui.OnMain(() => _ = OpenInWorkAsync(shell, vm, document));
        vm.DocumentCreated += created;
        WritingUi.OnGone(page, () => vm.DocumentCreated -= created);
        page.AttachedToVisualTree += (_, _) =>
        {
            vm.DocumentCreated -= created;
            vm.DocumentCreated += created;
        };
        return page;
    }

    /// <summary>
    /// Opens <paramref name="document"/> in /work. As NEW of /work does, the tape being worked on is put away first (saved as
    /// a document, or a new version of its document), so that nothing of it is lost.
    /// </summary>
    private static async Task OpenInWorkAsync(Shell shell, CollectionPlanWindowViewModel vm, DocumentEntry document)
    {
        shell.Chat.OpenTool("/work", false);
        CalculatorViewModel? work = null;
        for (var i = 0; i < 40 && work is null; i++)
        {
            await Task.Delay(25);
            work = shell.Deck.Find("/work")?.ViewModel as CalculatorViewModel;
        }
        if (work is null)
        {
            vm.Status = $"The document \"{document.Title}\" is saved: open it in /work (📂 Open)";
            return;
        }
        if (!string.IsNullOrWhiteSpace(work.Tape) && work.CurrentDocument?.Id != document.Id)
        {
            try
            {
                var done = await work.NewDocumentAsync();
                vm.RunLog += "\n📄 /work: " + done;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException)
            {
                vm.Status = $"The document \"{document.Title}\" is saved, but the tape of /work could not be put away first ({ex.Message}): open it with 📂 Open";
                return;
            }
        }
        work.Tape = work.OpenDocument(document);
        vm.RunLog += $"\n📄 Opened in /work: {document.Title}";
    }

    /// <summary>A collection plan of the list: its summary, badges active / new / modified, title and description.</summary>
    private static Control ResultRow(CollectionPlanWindowViewModel vm, CollectionPlanEntryViewModel entry)
    {
        var description = WritingUi.Clamp(Ui.Bound(entry, nameof(entry.Description), Ui.Muted, 11), 1).Visible(entry, nameof(entry.HasDescription));
        var content = Ui.Column(2,
            Ui.Row(6, Ui.Bound(entry, nameof(entry.Summary), Ui.Muted, 10),
                WritingUi.Badge("active", Ui.Accent, entry, nameof(entry.IsActive)),
                WritingUi.Badge("new", Ui.Green, entry, nameof(entry.IsNew)),
                WritingUi.Badge("modified", WritingUi.Amber, entry, nameof(entry.IsDirty))),
            WritingUi.Clamp(Ui.Bound(entry, nameof(entry.Title), Ui.TextBright, 13), 2),
            description);
        return WritingUi.ResultRow(vm, nameof(vm.SelectedResult), () => ReferenceEquals(vm.SelectedResult, entry), () => vm.SelectedResult = entry, content);
    }

    /// <summary>The editor of the selected collection plan.</summary>
    private static Control Editor(CollectionPlanWindowViewModel vm, CollectionPlanEntryViewModel entry)
    {
        var header = WritingUi.Flow(8,
            EntryParts.Identity(entry),
            EntryParts.Status(entry),
            WritingUi.Command("🕷 RUN", vm.RunCommand, tooltip: "Read the websites of this plan (by the mode and the keywords of each URL), let the model of the chat carry out the instructions on them, and open the result as a new document of /work"),
            WritingUi.Command("■ CANCEL", vm.CancelRunCommand, tooltip: "Stop the run").Visible(vm, nameof(vm.IsRunning)),
            WritingUi.Command("🤖 LAUNCH AGENT", vm.LaunchAgentCommand, tooltip: "Launch the web scraper agent (KAONOMI.Agent.WebScrapper) on this plan as it is saved: it visits its URLs, keeps a screenshot and the pictures of each page (Pictures/kaonomi/webscrapper), cuts the pages into paragraphs with their entities and saves them"),
            WritingUi.Command("■ STOP AGENT", vm.StopAgentCommand, tooltip: "Stop the web scraper agent (what it saved stays)").Visible(vm, nameof(vm.IsAgentRunning)),
            WritingUi.Command("DELETE", vm.DeleteCommand, tooltip: "Delete this collection plan (only marked deleted on the server: isDeleted = true)"),
            WritingUi.Command("CLONE", vm.CloneCommand, tooltip: "Create a copy of this collection plan (new id on save)"),
            WritingUi.Command("DISCARD CHANGES", entry.RevertCommand),
            WritingUi.Command("SAVE", entry.SaveCommand, primary: true, tooltip: "Save (Ctrl+S / ⌘S)"));

        var identity = WritingUi.Cells(2,
            WritingUi.Field("Name", WritingUi.Line(entry, nameof(entry.Name))),
            WritingUi.Field("Project", WritingUi.Line(entry, nameof(entry.Project))));

        var fields = Ui.Column(10,
            identity,
            Ui.Check("Active (run by the web scraper)", entry, nameof(entry.IsActive)),
            WritingUi.Field("Description", WritingUi.Lines(entry, nameof(entry.Description), 100)),
            WritingUi.Field("Instructions", WritingUi.Lines(entry, nameof(entry.Instructions), 160)),
            Websites(entry),
            WritingUi.Field("Comment", WritingUi.Lines(entry, nameof(entry.Comment), 44)));
        return EntryParts.Editor(header, fields, RunLog(vm));
    }

    /// <summary>What RUN does, one line per page read and per step, the last one in view (shown once a run started).</summary>
    private static Control RunLog(CollectionPlanWindowViewModel vm)
    {
        var log = WritingUi.ReadOnly(mono: true, size: 11);
        log.Height = 130;
        Ui.When(log, vm, () =>
        {
            log.Text = vm.RunLog;
            log.CaretIndex = log.Text?.Length ?? 0;
        }, nameof(vm.RunLog));
        var progress = new ProgressBar { IsIndeterminate = true, Height = 4, MinHeight = 4 }.Visible(vm, nameof(vm.IsRunning));
        var box = Ui.Column(4, Ui.Caption("RUN", WritingUi.Amber, 11), progress, log);
        box.Margin = new Thickness(0, 6, 0, 4);
        return box.Visible(vm, nameof(vm.HasRunLog));
    }

    /// <summary>The websites of the plan, each with its URLs, and ADD WEBSITE.</summary>
    private static Control Websites(CollectionPlanEntryViewModel entry)
    {
        var list = new StackPanel { Spacing = 8 };
        var empty = WritingUi.Placeholder("No website: ADD WEBSITE adds one");
        WritingUi.Items(list, entry.Websites, website => Website(entry, website), empty);
        var title = Ui.Label("Websites", Ui.TextBright, 13, bold: true);
        return Ui.Column(6,
            Ui.Dock((Ui.Button("ADD WEBSITE", entry.AddWebsite, "Add a website to collect"), Dock.Right), (title, Dock.Left)),
            list,
            empty);
    }

    /// <summary>A website: its name, REMOVE, its URLs (address, mode, keywords) and ADD URL.</summary>
    private static Control Website(CollectionPlanEntryViewModel entry, CollectionPlanWebsiteViewModel website)
    {
        var name = WritingUi.Line(website, nameof(website.Name), "Name of the website");
        var remove = Ui.Button("REMOVE", () => entry.RemoveWebsite(website), "Remove this website and its URLs");
        remove.Margin = new Thickness(6, 0, 0, 0);
        var urls = new StackPanel { Spacing = 4 };
        var noUrl = Ui.Label("No URL", Ui.Muted, 12);
        WritingUi.Items(urls, website.UrlProcesses, url => Url(website, url), noUrl);
        var inner = Ui.Column(4, urls, noUrl, Ui.Row(6, Ui.Button("ADD URL", website.AddUrl, "Add a URL to this website")));
        inner.Margin = new Thickness(16, 0, 0, 0);
        return Ui.Column(4,
            WritingUi.Separator(),
            Ui.Dock((remove, Dock.Right), (name, Dock.Left)),
            inner);
    }

    /// <summary>A URL of a website: the address, the mode (fixed, explore, random), the keywords and ✕.</summary>
    private static Control Url(CollectionPlanWebsiteViewModel website, CollectionPlanUrlViewModel url)
    {
        var address = WritingUi.Line(url, nameof(url.Url), "https://…");
        var mode = WritingUi.Choice(url, nameof(url.Mode), CollectionPlanUrlViewModel.Modes);
        mode.MinWidth = 100;
        ToolTip.SetTip(mode, "fixed: this page only · explore: the pages it links to · random: pages picked at random");
        var keywords = WritingUi.Line(url, nameof(url.Keywords), "Keywords, separated by commas");
        var remove = Ui.Button("✕", () => website.RemoveUrl(url), "Remove this URL");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,Auto,*,Auto"), ColumnSpacing = 6 };
        Grid.SetColumn(mode, 1);
        Grid.SetColumn(keywords, 2);
        Grid.SetColumn(remove, 3);
        grid.Children.Add(address);
        grid.Children.Add(mode);
        grid.Children.Add(keywords);
        grid.Children.Add(remove);
        return grid;
    }
}
