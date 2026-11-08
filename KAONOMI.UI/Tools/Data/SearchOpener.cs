using System;
using System.Linq;
using System.Threading.Tasks;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// Opens a hit of <c>/search</c> in the window of its kind: the tool is opened (or brought back) with its usual command,
/// then taken to the document: the cache, the completions, the ideas and the intel search its title and select it by
/// its id; the ontology selects its node; the relations search it and open its node; Work loads the document; the
/// journal opens the entry; the projects open the card of the task; an image analysis opens by the path of its image;
/// the YouTube videos search it and select it; the wiki, BPMN, infrastructure, mind map and writings open it from the
/// server. A dialog has no window of its own: its JSON stays on the right of the search.
/// </summary>
public static class SearchOpener
{
    public static async Task OpenAsync(Shell shell, SearchWindowViewModel search, SearchHit hit)
    {
        var title = Title(hit);
        try
        {
            switch (hit.Index)
            {
                case "cache":
                {
                    var vm = await OpenToolAsync<CacheWindowViewModel>(shell, "/cache");
                    if (vm is null) break;
                    vm.Term = title;
                    await vm.SearchCommand.ExecuteAsync(null);
                    vm.SelectedResult = vm.Results.FirstOrDefault(r => r.Id == hit.Id) ?? vm.SelectedResult;
                    break;
                }
                case "completion":
                {
                    var vm = await OpenToolAsync<CompletionWindowViewModel>(shell, "/completion");
                    if (vm is null) break;
                    vm.Term = title;
                    await vm.SearchCommand.ExecuteAsync(null);
                    vm.SelectedResult = vm.Results.FirstOrDefault(r => r.Entry.Id == hit.Id) ?? vm.SelectedResult;
                    break;
                }
                case "idea":
                {
                    var vm = await OpenToolAsync<IdeaWindowViewModel>(shell, "/idea");
                    if (vm is null) break;
                    vm.Term = title;
                    await vm.SearchCommand.ExecuteAsync(null);
                    vm.SelectedResult = vm.Results.FirstOrDefault(r => r.Id == hit.Id) ?? vm.SelectedResult;
                    break;
                }
                case "intel":
                {
                    var vm = await OpenToolAsync<IntelWindowViewModel>(shell, "/intel");
                    if (vm is null) break;
                    vm.Term = title;
                    await vm.SearchCommand.ExecuteAsync(null);
                    vm.SelectedResult = vm.Results.FirstOrDefault(r => r.Id == hit.Id) ?? vm.SelectedResult;
                    break;
                }
                case "ontology":
                {
                    var vm = await OpenToolAsync<OntologyWindowViewModel>(shell, "/ontology");
                    if (vm is null) break;
                    var (nodes, _) = await vm.FindNodesAsync(title, default);
                    var node = nodes.FirstOrDefault(n => n.Id == hit.Id) ?? nodes.FirstOrDefault();
                    if (node is not null)
                    {
                        vm.SelectedNode = node;
                        vm.EditNode(node);
                    }
                    break;
                }
                case "relation":
                {
                    var vm = await OpenToolAsync<RelationGraphViewModel>(shell, "/relation");
                    if (vm is null) break;
                    vm.Term = title;
                    await vm.SearchCommand.ExecuteAsync(null);
                    var node = vm.Nodes.FirstOrDefault(n => n.ObjectId == hit.Id || n.Key == hit.Id);
                    if (node is not null)
                        await vm.OpenNodeAsync(node);
                    break;
                }
                case "taskitem":
                {
                    var vm = await OpenToolAsync<ProjectWindowViewModel>(shell, "/project");
                    if (vm is null) break;
                    await vm.ReloadAfterChangeAsync();
                    var card = vm.Columns.SelectMany(c => c.Cards).FirstOrDefault(c => c.Task.Id == hit.Id);
                    if (card is not null && vm.Edit(card) is { } editor)
                        shell.Open(editor);
                    break;
                }
                case "document":
                {
                    var vm = await OpenToolAsync<CalculatorViewModel>(shell, "/work");
                    if (vm is null) break;
                    var documents = await vm.SearchDocumentsAsync(title);
                    var document = documents.FirstOrDefault(d => d.Id == hit.Id) ?? documents.FirstOrDefault();
                    if (document is not null)
                        vm.Tape = vm.OpenDocument(document);
                    break;
                }
                case "diary":
                {
                    var vm = await OpenToolAsync<JournalWindowViewModel>(shell, "/journal");
                    if (vm is null) break;
                    vm.ShowHistory = true;
                    vm.HistorySearch = title;
                    await vm.SearchHistoryCommand.ExecuteAsync(null);
                    var item = vm.History.FirstOrDefault(h => h.Entry.Id == hit.Id) ?? vm.History.FirstOrDefault();
                    if (item is not null)
                        await vm.OpenEntryCommand.ExecuteAsync(item);
                    break;
                }
                case "imageanalysis":
                {
                    var entry = await shell.Chat.Assistant.GetImageAnalysisAsync(hit.Id);
                    if (entry is null || string.IsNullOrWhiteSpace(entry.ImagePath))
                    {
                        search.Status = "The image analysis was not found on the server";
                        return;
                    }
                    var analysis = shell.Chat.CreateImageAnalysis(entry.ImagePath);
                    shell.Open(analysis, title: analysis.Title);
                    break;
                }
                case "youtubevideo":
                {
                    var vm = await OpenToolAsync<YoutubeWindowViewModel>(shell, "/youtube");
                    if (vm is null) break;
                    vm.Term = title;
                    await vm.SearchCommand.ExecuteAsync(null);
                    vm.SelectedVideo = vm.Videos.FirstOrDefault(v => v.Entry.Id == hit.Id) ?? vm.SelectedVideo;
                    break;
                }
                case "wiki":
                {
                    var vm = await OpenToolAsync<WikiWindowViewModel>(shell, "/wiki");
                    if (vm is null) break;
                    await vm.OpenPageAsync(hit.Id);
                    break;
                }
                case "bpmn":
                {
                    var vm = await OpenToolAsync<BpmnWindowViewModel>(shell, "/bpmnai");
                    if (vm is null) break;
                    await vm.OpenFromServerAsync(hit.Id);
                    break;
                }
                case "infra":
                {
                    var vm = await OpenToolAsync<InfraWindowViewModel>(shell, "/infra");
                    if (vm is null) break;
                    await vm.OpenFromServerAsync(hit.Id);
                    break;
                }
                case "mindmap":
                {
                    var vm = await OpenToolAsync<MindmapWindowViewModel>(shell, "/mindmap");
                    if (vm is null) break;
                    await vm.OpenFromServerAsync(hit.Id);
                    break;
                }
                case "ghostwriter":
                {
                    var vm = await OpenToolAsync<GhostWriterWindowViewModel>(shell, "/write");
                    if (vm is null) break;
                    // The writing open in /write has changes not saved: they are lost only once the user said so.
                    if (vm.IsDirty && vm.WritingId != hit.Id
                        && !await vm.ConfirmAsync("The writing open in /write has changes that are not saved. Open this one instead (they are kept in its draft)?", "Open"))
                        return;
                    await vm.OpenFromServerAsync(hit.Id);
                    break;
                }
                case "dialog":
                    search.Status = "A dialog has no window of its own: its JSON is on the right";
                    return;
                case "webpage":
                    // A page of the web scraper agent: its screenshot (or the page itself).
                    search.Status = SearchWindowViewModel.OpenWebPage(hit);
                    return;
                default:
                    search.Status = $"No window for the index {hit.Index}";
                    return;
            }
            search.Status = $"Opened in {hit.Index}: {title}";
        }
        catch (Exception ex)
        {
            search.IsError = true;
            search.Status = $"{hit.Index}: {ex.Message}";
            KAONOMI.Chat.Services.ActivityLog.Error("Search", $"The hit {hit.Index}/{hit.Id} could not be opened", ex);
        }
    }

    /// <summary>The title of the hit as a search term (its first line, 80 characters at most), or its id.</summary>
    private static string Title(SearchHit hit)
    {
        var text = (hit.Title ?? string.Empty).Split('\n')[0].Trim();
        if (text.Length == 0)
            return hit.Id;
        return text.Length > 80 ? text[..80] : text;
    }

    /// <summary>
    /// Opens the tool of <paramref name="command"/> (or brings its window back) and gives its view model, read from the
    /// window of the desktop once it exists.
    /// </summary>
    private static async Task<T?> OpenToolAsync<T>(Shell shell, string command) where T : class
    {
        if (!shell.TryBringBack(command))
            shell.Chat.OpenTool(command, false);
        for (var i = 0; i < 30; i++)
        {
            if (shell.Deck.Find(command)?.ViewModel is T vm)
                return vm;
            await Task.Delay(33);
        }
        return null;
    }
}
