using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The prompt collections of the chat (the same as the default chat): every <c>*.prompt.json</c> of a folder sent one
/// after the other, each completion saved as JSON (<c>NNN-name.completion.json</c>: index, file, prompt, promptWords,
/// completion, model, date, durationSeconds, stats, isError, language) in the <c>&lt;model&gt;-&lt;collection&gt;</c>
/// folder of the collection, the topics summarised, the run written to a report and a Word document. A folder ending
/// in "-FR", "-EN"… gives the language of the collection, and the name before it is the name of the collection; with
/// a language the completions are also saved on the server. Esc (<see cref="Stop"/>) stops it.
/// </summary>
public static class PromptCollectionRunner
{
    private static CancellationTokenSource? _run;

    /// <summary>True while a collection runs.</summary>
    public static bool IsRunning => _run is not null;

    /// <summary>Stops the collection running (Esc).</summary>
    public static bool Stop()
    {
        if (_run is not { IsCancellationRequested: false } run)
            return false;
        run.Cancel();
        return true;
    }

    /// <summary>Asks for the folder of the prompts, confirms, then runs them.</summary>
    public static async Task RunAsync(MainViewModel vm)
    {
        if (_run is not null)
        {
            vm.Status = "A prompt collection is already running (Esc to stop it)";
            return;
        }
        try
        {
            Directory.CreateDirectory(vm.PromptDirectory);
            if (await Dialogs.OpenFolder("Prompt collection: choose the folder of the *.prompt.json prompts", vm.PromptDirectory) is not { } path)
                return;
            var files = PromptCollection.Find(path);
            if (files.Count == 0)
            {
                vm.Status = $"No {PromptCollection.Pattern} file in {path}";
                return;
            }
            var language = CollectionReport.LanguageOf(path);
            var collection = CollectionReport.CollectionNameOf(path);
            var model = vm.CurrentModel ?? "unknown";
            var message = $"Collection {collection}{(language is null ? string.Empty : $" ({language})")}: {files.Count} prompt(s) ({PromptCollection.Pattern}) in {path}.\n\n"
                          + $"Run them in the chat with {model}, one after the other? Each prompt and its completion are saved in the folder {CollectionReport.RunFolderName(model, collection)}.\nPress Esc to stop.";
            if (!await Dialogs.Confirm("Prompt collection", message, "Run"))
                return;
            await RunCollectionAsync(vm, files, path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            vm.Status = $"Prompt collection not run: {ex.Message}";
            ActivityLog.Error("Prompt", "Prompt collection failed", ex);
        }
    }

    private static async Task RunCollectionAsync(MainViewModel vm, IReadOnlyList<string> files, string collectionFolder)
    {
        using var run = new CancellationTokenSource();
        _run = run;
        // Batch: each answer is shown at once (no simulated streaming).
        vm.IsBatchRunning = true;

        // "…-FR" / "…-EN" collection folders: the language goes into every completion file and into the report, and
        // the name of the collection is the folder name before it.
        var language = CollectionReport.LanguageOf(collectionFolder);
        var name = CollectionReport.CollectionNameOf(collectionFolder);
        var prompts = files.Select(file => (File: file, Prompt: PromptCollection.Read(file))).ToList();
        var totalWords = prompts.Sum(p => CollectionReport.CountWords(p.Prompt));
        var model = vm.CurrentModel ?? "unknown";
        var start = DateTimeOffset.Now;
        var clock = Stopwatch.StartNew();
        // The results: the <model>-<collection> folder of the collection (a run of the same model replaces its files).
        var runFolder = Path.Combine(collectionFolder, CollectionReport.RunFolderName(model, name));
        var items = new List<CollectionReportItem>();
        int serverSaved = 0, serverFailed = 0;
        var skipped = 0;
        string? stoppedBecause = null;
        ActivityLog.Info("Prompt", $"Prompt collection {name}: {files.Count} prompt(s), {totalWords} word(s), model {model}, results in {runFolder}");

        try
        {
            Directory.CreateDirectory(runFolder);
            for (var i = 0; i < prompts.Count && !run.IsCancellationRequested; i++)
            {
                if (prompts[i].Prompt is not { } prompt)
                {
                    skipped++;
                    continue;
                }

                // An answer still coming (e.g. a prompt typed meanwhile) is waited for.
                while (vm.SendCommand.IsRunning && !run.IsCancellationRequested)
                    await Task.Delay(200);
                if (run.IsCancellationRequested)
                    break;

                vm.Status = $"· Prompt collection {name}: {i + 1}/{files.Count} (Esc to stop)";
                vm.Input = prompt;
                if (!vm.SendCommand.CanExecute(null))
                {
                    stoppedBecause = "the prompt cannot be sent (no model selected?)";
                    break;
                }

                var sentAt = DateTimeOffset.Now;
                var timer = Stopwatch.StartNew();
                await vm.SendCommand.ExecuteAsync(null);
                var answer = vm.LastAnswer;
                var item = new CollectionItem
                {
                    Index = i + 1,
                    File = Path.GetFileName(prompts[i].File),
                    Prompt = prompt,
                    PromptWords = CollectionReport.CountWords(prompt),
                    Completion = answer?.Content ?? string.Empty,
                    Model = model,
                    Date = sentAt,
                    DurationSeconds = Math.Round(timer.Elapsed.TotalSeconds, 2),
                    Stats = answer?.Stats,
                    IsError = answer?.IsError ?? true,
                    Language = language
                };
                var completionPath = await CollectionReport.SaveCompletionAsync(runFolder, item);
                items.Add(new CollectionReportItem
                {
                    Index = item.Index,
                    File = item.File,
                    Prompt = item.Prompt,
                    PromptWords = item.PromptWords,
                    CompletionFile = Path.GetFileName(completionPath)
                });

                // With a language ("…-FR" / "…-EN" folder), the prompt and its completion are also saved on the
                // server (PUT /completion). Failed answers are not sent; a server error never stops the run.
                if (language is not null && !item.IsError)
                {
                    try
                    {
                        // Terms are counted on the completion without its formatting (no Markdown markers).
                        var rawCompletion = PlainText.FromMarkdown(item.Completion);
                        var saved = await vm.SaveCompletionAsync(new CompletionEntry
                        {
                            Date = item.Date,
                            Comment = $"Prompt collection {name}",
                            Prompt = item.Prompt,
                            Completion = item.Completion,
                            RawCompletion = rawCompletion,
                            WordCounts = TermCounter.WordCounts(item.Prompt, rawCompletion),
                            Model = model,
                            Language = language,
                            Topic = PromptCollection.ReadTopic(prompts[i].File),
                            CollectionName = name,
                            Metadatas =
                            [
                                new CompletionMetaData { Key = "PromptFile", ValueString = item.File },
                                new CompletionMetaData { Key = "Index", ValueInt = item.Index },
                                new CompletionMetaData { Key = "PromptWords", ValueInt = item.PromptWords },
                                new CompletionMetaData { Key = "DurationSeconds", ValueDouble = item.DurationSeconds }
                            ]
                        });
                        if (saved.ReturnedId == "-1")
                            throw new InvalidOperationException(saved.Message ?? "The server could not save the completion.");
                        serverSaved++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || ex is TaskCanceledException)
                    {
                        if (serverFailed++ == 0)
                            ActivityLog.Error("Prompt", $"Prompt collection {name}: completion not saved with PUT /completion", ex);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stoppedBecause = ex.Message;
            ActivityLog.Error("Prompt", $"Prompt collection {name}: completions not saved", ex);
        }
        finally
        {
            _run = null;
            vm.IsBatchRunning = false;
        }

        clock.Stop();
        var stopped = run.IsCancellationRequested;
        var outcome = stopped ? "stopped (Esc)" : stoppedBecause is null ? "done" : $"stopped: {stoppedBecause}";

        // Summary of the topics of all the prompts (skipped when the run was stopped with Esc).
        var summary = stopped ? "Not generated: the run was stopped with Esc." : string.Empty;
        var topics = new List<CollectionTopic>();
        var readable = prompts.Where(p => p.Prompt is not null).Select(p => p.Prompt!).ToList();
        if (!stopped && readable.Count > 0)
        {
            vm.Status = $"· Prompt collection {name}: summarising the topics of {readable.Count} prompt(s)…";
            try
            {
                (summary, topics) = await vm.SummarizeTopicsAsync(readable, CancellationToken.None);
            }
            catch (Exception ex)
            {
                summary = $"Not generated: {ex.Message}";
                ActivityLog.Error("Prompt", $"Prompt collection {name}: topic summary failed", ex);
            }
        }

        var report = new CollectionRunReport
        {
            Collection = name,
            CollectionFolder = collectionFolder,
            Model = model,
            Language = language,
            Date = start,
            EndDate = DateTimeOffset.Now,
            ExecutionTimeSeconds = Math.Round(clock.Elapsed.TotalSeconds, 1),
            ExecutionTime = clock.Elapsed.ToString(@"hh\:mm\:ss"),
            PromptCount = files.Count,
            SentCount = items.Count,
            SkippedCount = skipped,
            Stopped = stopped || stoppedBecause is not null,
            TotalPromptWords = totalWords,
            TopicsSummary = summary,
            Topics = topics,
            Items = items
        };

        try
        {
            var reportPath = await CollectionReport.WriteReportAsync(runFolder, report);
            var wordPath = CollectionReport.WriteWordFromReport(reportPath);
            var serverNote = language is null
                ? string.Empty
                : $" · PUT /completion ({language}): {serverSaved} saved" + (serverFailed > 0 ? $", {serverFailed} failed" : string.Empty);
            vm.Status = $"· Prompt collection {name} {outcome}: {items.Count}/{files.Count} sent, {totalWords} word(s), "
                                + $"{report.ExecutionTime}{serverNote} · report: {Path.GetFileName(reportPath)} + {Path.GetFileName(wordPath)} in {runFolder}";
            ActivityLog.Info("Prompt", $"Prompt collection {name} {outcome}: {items.Count}/{files.Count} sent in {report.ExecutionTime}{serverNote}, report {reportPath}");
        }
        catch (Exception ex)
        {
            vm.Status = $"· Prompt collection {name} {outcome}, but the report was not written: {ex.Message}";
            ActivityLog.Error("Prompt", $"Prompt collection {name}: report not written", ex);
        }
    }
}
