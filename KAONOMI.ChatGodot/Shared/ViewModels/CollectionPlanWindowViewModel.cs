using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// Search, creation, cloning, edition and deletion of the collection plans of the web scraper (<c>/collectionplan</c>,
/// <c>/ncollectionplan</c> in its own window). The search looks in every text field; an empty search lists the newest
/// plans (shown when the window opens).
/// </summary>
public partial class CollectionPlanWindowViewModel : ViewModelBase, INoteContextProvider, IProjectDocument
{
    /// <summary>The project of the plan selected (shown and chosen by its name in the header of the window).</summary>
    public string? ProjectId
    {
        get => SelectedResult?.ProjectId;
        set
        {
            if (SelectedResult is { } selected && selected.ProjectId != value)
            {
                selected.ProjectId = value;
                OnPropertyChanged();
            }
        }
    }

    public bool HasProjectDocument => SelectedResult is not null;

    partial void OnSelectedResultChanged(CollectionPlanEntryViewModel? value)
    {
        OnPropertyChanged(nameof(ProjectId));
        OnPropertyChanged(nameof(HasProjectDocument));
    }

    private const int PageSize = 50;

    private readonly KaonomiAssistantService _assistant;
    private readonly OllamaModelService? _ollama;
    private readonly Func<string?>? _model;
    private readonly string _visionModel;
    private readonly string _language;
    private CancellationTokenSource? _run;

    /// <summary>Design-time constructor using default server URL from settings.</summary>
    public CollectionPlanWindowViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl), load: false)
    {
    }

    /// <summary>
    /// Uses the given assistant client for the collection plan API calls; lists the newest plans. With
    /// <paramref name="ollama"/> and <paramref name="model"/> (the model of the chat), RUN carries out a plan: the web
    /// scraper reads its websites and the model writes the analysis in <paramref name="language"/>.
    /// </summary>
    public CollectionPlanWindowViewModel(KaonomiAssistantService assistant, bool load = true, OllamaModelService? ollama = null,
        Func<string?>? model = null, string language = "FR", string visionModel = ChatSettings.DefaultVisionModel)
    {
        _assistant = assistant;
        _ollama = ollama;
        _model = model;
        _visionModel = visionModel;
        _language = SpeechLanguage.Name(language);
        if (load)
            _ = SearchAsync(CancellationToken.None);
    }

    /// <summary>Search hits and locally created rows bound to the results list.</summary>
    public ObservableCollection<CollectionPlanEntryViewModel> Results { get; } = [];

    /// <summary>True when at least one result row is present.</summary>
    public bool HasResults => Results.Count > 0;

    /// <summary>Asks the user to confirm (message, confirm label); set by the view.</summary>
    public Func<string, string, Task<bool>> ConfirmAsync { get; set; } = (message, label) => Confirmations.Ask(message, label);

    [ObservableProperty]
    private string _term = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneCommand), nameof(DeleteCommand), nameof(RunCommand), nameof(LaunchAgentCommand))]
    private CollectionPlanEntryViewModel? _selectedResult;

    /// <summary>True while RUN reads the websites and the model writes the analysis.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(CancelRunCommand))]
    private bool _isRunning;

    /// <summary>What RUN did, one line per step (the pages read, skipped, failed; the analysis; the document).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRunLog))]
    private string _runLog = string.Empty;

    public bool HasRunLog => RunLog.Length > 0;

    /// <summary>Raised when RUN has saved the document of its analysis: the view opens it in /work.</summary>
    public event Action<DocumentEntry>? DocumentCreated;

    private bool CanRun() => SelectedResult is not null && !IsRunning;

    /// <summary>
    /// RUN: the web scraper reads the websites of the selected plan (as it is in the editor, saved or not), by the mode
    /// and the keywords of each URL; each page read is pictured as a browser draws it, and the vision model reads the
    /// pictures: all their texts, then each piece of information, summarised; the model of the chat carries out the
    /// instructions of the plan on what was read; the result is saved as a new document of /work (category webpage)
    /// and opened there.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        var entry = SelectedResult!;
        var plan = entry.ToEntry();
        var urls = (plan.Websites ?? []).SelectMany(w => w.UrlProcesses ?? []).Count(u => !string.IsNullOrWhiteSpace(u.Url));
        if (urls == 0)
        {
            Fail("No URL to read: ADD WEBSITE, then ADD URL");
            return;
        }
        var model = _model?.Invoke();
        if (_ollama is null || string.IsNullOrWhiteSpace(model))
        {
            Fail("No model of Ollama to write the analysis: choose one in the chat");
            return;
        }

        _run?.Cancel();
        var cancel = new CancellationTokenSource();
        _run = cancel;
        IsRunning = true;
        IsError = false;
        RunLog = string.Empty;
        var started = DateTime.Now;
        Log($"▶ {plan.Name}: {urls} URL(s) to read");
        Status = $"Reading the websites of \"{entry.Title}\"…";
        try
        {
            var scraper = new WebScraper(Log);
            var pages = await scraper.CollectAsync(plan, cancel.Token);
            var read = pages.Count(p => p.Ok);
            Log($"— {read} page(s) read of {pages.Count}, {pages.Where(p => p.Ok).Sum(p => p.KeywordHits)} keyword hit(s)");
            if (read == 0)
            {
                Fail("No page could be read: see the log");
                return;
            }

            // The pictures of the pages, screen by screen, as a browser draws them.
            var folder = System.IO.Path.Combine(ChatSettings.SystemPicturesDirectory, "kaonomi", "scraper", $"{Slug(plan.Name)}-{started:yyyyMMdd-HHmmss}");
            Status = $"Pictures of {read} page(s)…";
            Log($"📷 Pictures of the pages in {folder}");
            var pictures = await CapturePagesAsync(pages, folder, cancel.Token);
            Log($"📷 {pictures.Values.Sum(p => p.Count)} screen(s) of {pictures.Count} page(s)");

            // Each page read from its pictures by the vision model: its texts, then each piece of information.
            var vision = string.IsNullOrWhiteSpace(_visionModel) ? model! : _visionModel;
            var readings = new Dictionary<string, PageReading>(StringComparer.Ordinal);
            var number = 0;
            foreach (var page in pages.Where(p => pictures.ContainsKey(p.Url)))
            {
                number++;
                Status = $"{vision} reads page {number} of {pictures.Count}…";
                Log($"👁 {vision} reads {page.Url} ({pictures[page.Url].Count} screen(s))…");
                try
                {
                    var reading = await PageReader.ReadAsync(_ollama, vision, _language, page, pictures[page.Url], cancel.Token, Log);
                    readings[page.Url] = reading;
                    Log($"👁 {reading.Informations.Count} piece(s) of information, {reading.Text.Length:N0} characters of text ({reading.Took.TotalSeconds:0} s)");
                }
                catch (Exception ex) when (!cancel.IsCancellationRequested && ex is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException or System.IO.IOException)
                {
                    var why = ex is OperationCanceledException ? $"no answer within {PageReader.Timeout.TotalMinutes:0} minutes" : ex.Message;
                    Log($"👁 ✕ {page.Url}: {why} (its HTML text is used)");
                    ActivityLog.Error("CollectionPlan", $"{vision} could not read {page.Url}", ex);
                }
            }

            Status = $"{model} carries out the instructions on {read} page(s)…";
            Log($"✎ {model} carries out the instructions…");
            var analysis = await CollectionAnalyst.AnalyseAsync(_ollama, model!, _language, plan, pages, readings, cancel.Token);
            Log($"✓ Analysis written ({analysis.Length:N0} characters)");

            var when = DateTime.Now;
            var document = new DocumentEntry
            {
                Title = CollectionAnalyst.DocumentTitle(plan, when),
                Category = "webpage",
                Description = CollectionAnalyst.Description(plan, pages),
                Comment = plan.Id is null ? "Collection plan not saved" : $"Collection plan {plan.Id}",
                Contents = [new DocumentContent { Content = CollectionAnalyst.Tape(plan, pages, analysis, model!, vision, when, pictures, readings), Version = 1 }],
                Date = DateTimeOffset.Now,
                AppName = "KAONOMI.Chat",
                ProjectId = plan.ProjectId,
                MachineName = Environment.MachineName,
                UserName = Environment.UserName
            };
            var result = await _assistant.SaveDocumentAsync(document, cancel.Token);
            if (!result.Success || result.ReturnedId is null or "-1")
            {
                Fail($"The analysis was not saved as a document: {result.Message ?? "refused by the server"}");
                return;
            }
            document.Id = result.ReturnedId;
            Log($"💾 Document \"{document.Title}\" saved ({document.Id}) in {(DateTime.Now - started).TotalSeconds:0} s");
            Status = $"Done: the document \"{document.Title}\" opens in /work";
            ActivityLog.Info("CollectionPlan", $"Plan \"{plan.Name}\" run: {read} page(s), document {document.Id}");
            DocumentCreated?.Invoke(document);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Log("■ Cancelled");
            Status = "Run cancelled";
        }
        catch (OperationCanceledException)
        {
            Fail($"The model gave no analysis within {CollectionAnalyst.AnalysisTimeout.TotalMinutes:0} minutes");
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Fail($"Run failed: {ex.Message}");
            ActivityLog.Error("CollectionPlan", $"Run of the plan \"{plan.Name}\" failed", ex);
        }
        finally
        {
            if (ReferenceEquals(_run, cancel))
                _run = null;
            cancel.Dispose();
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void CancelRun() => _run?.Cancel();

    /// <summary>The pictures of the pages read (as a browser draws them, screen by screen), by the address of the page.</summary>
    private async Task<Dictionary<string, IReadOnlyList<string>>> CapturePagesAsync(IReadOnlyList<ScrapedPage> pages, string folder, CancellationToken cancellationToken)
    {
        var pictures = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var shots = pages.Where(p => p.Ok).Select(async (page, index) =>
        {
            var host = Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", string.Empty) : "page";
            var (paths, error) = await PageSnapshot.CaptureAsync(page.Url, folder, $"{index + 1:00}-{Slug(host)}", cancellationToken);
            if (paths.Count > 0)
            {
                lock (pictures)
                    pictures[page.Url] = paths;
                Log($"📷 {page.Url} · {paths.Count} screen(s)");
            }
            else
                Log($"📷 ✕ {page.Url}: {error}");
        });
        await Task.WhenAll(shots);
        return pictures;
    }

    /// <summary>A name of file made of the letters and digits of <paramref name="text"/>.</summary>
    private static string Slug(string? text)
    {
        var slug = new System.Text.StringBuilder();
        foreach (var ch in (text ?? string.Empty).Normalize(System.Text.NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(ch) && ch < 128)
                slug.Append(char.ToLowerInvariant(ch));
            else if (slug.Length > 0 && slug[^1] != '-' && (char.IsWhiteSpace(ch) || ch is '-' or '_' or '.' or '·'))
                slug.Append('-');
        }
        var result = slug.ToString().Trim('-');
        return result.Length == 0 ? "plan" : result.Length > 60 ? result[..60] : result;
    }

    // ── RESULTS: what the web scraper agent saved for the selected plan ──

    /// <summary>
    /// The window of the results of the web scraper agent for <paramref name="entry"/> (its pages, screenshots, pictures
    /// and text, read in the search engine); its pictures open in the image analysis at their real size.
    /// </summary>
    public WebScrapeResultsWindowViewModel CreateResults(CollectionPlanEntryViewModel entry) =>
        new(ChatSettings.Load().SearchEngineUrl, entry.Id ?? string.Empty, entry.Title,
            path => new ImageAnalysisViewModel(path, _assistant, _ollama, _visionModel));

    // ── LAUNCH AGENT: the web scraper agent (KAONOMI.Agent.WebScrapper) on the selected plan ──

    /// <summary>The name of the process of the web scraper agent (its apphost).</summary>
    private const string AgentProcessName = "KAONOMI.Agent.WebScrapper";

    /// <summary>The web scraper agent started by LAUNCH AGENT, or null.</summary>
    private Process? _agent;

    /// <summary>True while the web scraper agent started from here runs.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchAgentCommand), nameof(StopAgentCommand))]
    private bool _isAgentRunning;

    private bool CanLaunchAgent() => SelectedResult is { IsNew: false } && !IsAgentRunning;

    /// <summary>A line the agent writes on its console: « [19:23:15] ▸ … », its marker x for an error, ! for a warning.</summary>
    private static readonly Regex AgentLine = new(@"^\[\d\d:\d\d:\d\d\]\s*(?<mark>\S)\s+(?<text>.+)$", RegexOptions.Compiled);

    /// <summary>The colors of the console (ANSI escapes), taken off the lines of the agent.</summary>
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[A-Za-z]|\x1B\][^\u0007]*\u0007", RegexOptions.Compiled);

    /// <summary>
    /// LAUNCH AGENT: starts the web scraper agent (KAONOMI.Agent.WebScrapper, <c>dotnet run -- --plan &lt;id&gt;</c>: one run, then it stops)
    /// on the selected plan as it is saved on the server, active or not: it visits the URLs of its websites, keeps a
    /// screenshot and the pictures of each page (Pictures/kaonomi/webscrapper), cuts the pages into paragraphs with
    /// their entities and saves them (indices webpage and webparagraph). The steps of the agent are shown in the log of
    /// the window, its whole output goes to ~/kaonomi/logs/WebScrapper.log.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLaunchAgent))]
    private async Task LaunchAgentAsync()
    {
        var entry = SelectedResult!;
        IsError = false;
        if (entry.IsDirty && !await ConfirmAsync($"\"{entry.Title}\" has unsaved changes: the agent reads the plan as it is saved on the server. Launch anyway?", "Launch"))
            return;

        var folder = Path.Combine(ProcessWindowViewModel.FindProjects(), AgentProcessName);
        if (!File.Exists(Path.Combine(folder, AgentProcessName + ".csproj")))
        {
            Fail($"The web scraper agent was not found ({folder})");
            return;
        }
        if (Process.GetProcessesByName(AgentProcessName).Length > 0)
        {
            Fail("The web scraper agent is already running (started from /process or a terminal): stop it first");
            return;
        }
        var info = ProcessWindowViewModel.Dotnet(folder, "run", "--", "--plan", entry.Id!);
        if (info is null)
        {
            Fail("dotnet was not found: install the .NET 8 SDK");
            return;
        }
        // The agent writes its console in UTF-8 (the markers ▸ ✓ ·).
        info.StandardOutputEncoding = System.Text.Encoding.UTF8;
        info.StandardErrorEncoding = System.Text.Encoding.UTF8;

        RunLog = string.Empty;
        var lines = new ConcurrentQueue<(bool Error, string Text)>();
        var errors = new List<string>();
        StreamWriter? log = null;
        var logPath = Path.Combine(ProcessWindowViewModel.LogsDirectory, "WebScrapper.log");
        try
        {
            Directory.CreateDirectory(ProcessWindowViewModel.LogsDirectory);
            log = new StreamWriter(logPath, append: true) { AutoFlush = true };
            log.WriteLine($"──── {DateTime.Now:yyyy-MM-dd HH:mm:ss} · started from the collection plan window: --plan {entry.Id} ({entry.Title}) ────");
            var writer = log;
            void Received(string? data)
            {
                if (data is null)
                    return;
                lock (writer)
                    writer.WriteLine(data);
                var clean = Ansi.Replace(data, string.Empty).Trim();
                if (AgentLine.Match(clean) is { Success: true } m)
                    lines.Enqueue((m.Groups["mark"].Value == "x", m.Groups["text"].Value.Trim()));
            }

            _agent = new Process { StartInfo = info, EnableRaisingEvents = true };
            _agent.OutputDataReceived += (_, e) => Received(e.Data);
            _agent.ErrorDataReceived += (_, e) => Received(e.Data);
            if (!_agent.Start())
                throw new InvalidOperationException("the process did not start");
            _agent.BeginOutputReadLine();
            _agent.BeginErrorReadLine();
            IsAgentRunning = true;
            Status = $"Web scraper agent started on \"{entry.Title}\" (building it the first time takes a moment)…";
            Log($"▶ Web scraper agent on \"{entry.Title}\" (output in {logPath})");
            ActivityLog.Info("Collection", $"Web scraper agent launched on {entry.Id} ({entry.Title})");

            // The steps of the agent shown as they come; the window stays responsive.
            string? summary = null;
            while (!_agent.HasExited)
            {
                await Task.Delay(400);
                summary = Drain(lines) ?? summary;
            }
            await _agent.WaitForExitAsync();
            summary = Drain(lines) ?? summary;

            var code = _agent.ExitCode;
            var nothingSaved = summary is null || summary.StartsWith("Saved 0 page", StringComparison.Ordinal);
            IsError = (code != 0 && summary is null) || (errors.Count > 0 && nothingSaved);
            Status = (summary is not null
                    ? $"Web scraper agent done: {summary}"
                    : code == 0 ? "Web scraper agent stopped" : $"Web scraper agent ended with code {code} (see {logPath})")
                + (errors.Count > 0 ? $" · {errors.Count} error(s): {errors[0]}" : string.Empty);
            Log((IsError ? "✕ " : "■ ") + Status);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            Fail($"The web scraper agent could not be started: {ex.Message}");
        }
        finally
        {
            log?.Dispose();
            _agent?.Dispose();
            _agent = null;
            IsAgentRunning = false;
        }

        // The new lines of the agent in the log (its errors kept); the summary of its run when it came ("Run done: Saved …").
        string? Drain(ConcurrentQueue<(bool Error, string Text)> queue)
        {
            string? done = null;
            while (queue.TryDequeue(out var line))
            {
                if (line.Text.StartsWith("Run done:", StringComparison.Ordinal))
                    done = line.Text["Run done:".Length..].Trim();
                if (line.Error)
                    errors.Add(line.Text);
                Log((line.Error ? "✕ " : "· ") + line.Text);
                Status = line.Text;
            }
            return done;
        }
    }

    /// <summary>Stops the web scraper agent started by LAUNCH AGENT (what it saved stays).</summary>
    [RelayCommand(CanExecute = nameof(IsAgentRunning))]
    private void StopAgent()
    {
        try
        {
            if (_agent is { HasExited: false } agent)
            {
                agent.Kill(entireProcessTree: true);
                Status = "Stopping the web scraper agent…";
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Fail($"The web scraper agent could not be stopped: {ex.Message}");
        }
    }

    private void Log(string line) => RunLog = RunLog.Length == 0 ? line : RunLog + "\n" + line;

    private void Fail(string message)
    {
        IsError = true;
        Status = message;
        Log("✕ " + message);
    }

    [ObservableProperty]
    private string _status = "Loading the latest collection plans…";

    [ObservableProperty]
    private bool _isError;

    /// <summary>Inserts a new unsaved collection plan at the top of the list.</summary>
    [RelayCommand]
    private void New()
    {
        var entry = CollectionPlanEntryViewModel.CreateNew(_assistant);
        Results.Insert(0, entry);
        SelectedResult = entry;
        OnPropertyChanged(nameof(HasResults));
        ActivityLog.Info("CollectionPlan", "New collection plan started (unsaved)");
    }

    private bool CanClone() => SelectedResult is not null;

    /// <summary>Duplicates the selected collection plan as a new unsaved one.</summary>
    [RelayCommand(CanExecute = nameof(CanClone))]
    private void Clone()
    {
        var source = SelectedResult!;
        var clone = source.Clone();
        Results.Insert(Results.IndexOf(source) + 1, clone);
        SelectedResult = clone;
        clone.Status = "Unsaved clone";
        OnPropertyChanged(nameof(HasResults));
        ActivityLog.Info("CollectionPlan", $"Collection plan cloned from {source.Id ?? "(new)"} (unsaved)");
    }

    private bool CanDelete() => SelectedResult is not null;

    /// <summary>
    /// Deletes the selected collection plan after a confirmation: on the server it is only marked deleted
    /// (<c>isDeleted</c>), and it leaves the list; a new plan not saved yet is just removed from the list.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync(CancellationToken cancellationToken)
    {
        var entry = SelectedResult!;
        if (!await ConfirmAsync($"Delete the collection plan \"{entry.Title}\"?{(entry.IsNew ? "" : " It is only marked deleted on the server.")}", "Delete"))
            return;

        if (!entry.IsNew)
        {
            try
            {
                var result = await _assistant.DeleteCollectionPlanAsync(entry.Id!, cancellationToken);
                if (!result.IsSuccess)
                {
                    IsError = true;
                    Status = result.Message ?? "The Kaonomi server rejected the deletion.";
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                IsError = true;
                Status = $"Collection plan not deleted: {ex.Message}";
                ActivityLog.Error("CollectionPlan", $"Collection plan {entry.Id} not deleted", ex);
                return;
            }
        }

        var index = Results.IndexOf(entry);
        Results.Remove(entry);
        SelectedResult = Results.Count == 0 ? null : Results[Math.Min(index, Results.Count - 1)];
        OnPropertyChanged(nameof(HasResults));
        IsError = false;
        Status = $"Collection plan \"{entry.Title}\" deleted";
        ActivityLog.Info("CollectionPlan", $"Collection plan deleted (isDeleted = true): {entry.Id ?? "(unsaved)"} ({entry.Title})");
    }

    /// <summary>
    /// Searches the collection plans matching <see cref="Term"/> in all their text fields; an empty term (or <c>*</c>)
    /// lists the newest plans.
    /// </summary>
    [RelayCommand]
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        var term = Term.Trim();
        var isLast = term.Length == 0 || term == "*";
        IsError = false;
        Status = isLast ? "Loading the latest collection plans…" : $"Searching for \"{term}\"…";

        try
        {
            var entries = isLast
                ? await _assistant.GetLastCollectionPlansAsync(PageSize, cancellationToken)
                : await _assistant.SearchCollectionPlansAsync(term, PageSize, cancellationToken);

            Results.Clear();
            foreach (var entry in entries)
                Results.Add(new CollectionPlanEntryViewModel(entry, _assistant));

            SelectedResult = Results.FirstOrDefault();
            Status = isLast
                ? Results.Count == 0 ? "No collection plan yet: New creates one" : $"{Results.Count} latest collection plan(s)"
                : Results.Count == 0 ? $"No results for \"{term}\"" : $"{Results.Count} result(s) for \"{term}\"";
            ActivityLog.Info("CollectionPlan", isLast ? $"Latest collection plans: {Results.Count}" : $"Search \"{term}\": {Results.Count} result(s)");
        }
        catch (OperationCanceledException)
        {
            Status = "Search cancelled";
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
            ActivityLog.Error("CollectionPlan", $"Search \"{term}\" failed: server unreachable", ex);
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = ex.Message;
            ActivityLog.Error("CollectionPlan", $"Search \"{term}\" failed", ex);
        }
        finally
        {
            OnPropertyChanged(nameof(HasResults));
        }
    }

    /// <inheritdoc />
    public string NoteWindowName => "Collection plan";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        searchTerm = Term,
        resultCount = Results.Count,
        selected = SelectedResult?.ToEntry()
    };
}
