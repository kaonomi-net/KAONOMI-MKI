using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// "Annotate…" (a right click on an image, a text selected, a video, an audio file, an entity): the user types the
/// annotation and its tags (<c>#SOFTWARE #IDEA</c>…, a click on a suggested tag adds it), saved with the object
/// annotated and the context of its window (as for a white note) by <c>PUT /annotation</c>.
/// </summary>
public partial class AnnotationDialogViewModel : ViewModelBase
{
    private static readonly TimeSpan CloseDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>Tags offered for any object.</summary>
    private static readonly string[] CommonTags = ["#IDEA", "#TODO", "#QUESTION", "#IMPORTANT", "#SOFTWARE", "#REVIEW"];

    private readonly KaonomiAssistantService? _assistant;

    /// <summary>Prepares an annotation of <paramref name="target"/>, written in the window <paramref name="windowName"/> with its JSON <paramref name="context"/>.</summary>
    public AnnotationDialogViewModel(KaonomiAssistantService? assistant, AnnotationTarget target, string windowName, string context)
    {
        _assistant = assistant;
        Target = target;
        WindowName = windowName;
        Context = context;
        var kind = KindTag(target.Type);
        SuggestedTags = new[] { kind, NormalizeTag(target.EntityType) }
            .Concat(CommonTags)
            .Where(t => t is not null)
            .Select(t => t!)
            .Distinct()
            .ToList();
        // The kind of the object is a tag from the start.
        _tagsText = kind ?? string.Empty;
    }

    /// <summary>Design-time constructor.</summary>
    public AnnotationDialogViewModel() : this(null, AnnotationTarget.Text("A sentence selected", "Work"), "Work", "{ \"window\": \"Work\" }")
    {
    }

    /// <summary>The object annotated.</summary>
    public AnnotationTarget Target { get; }

    /// <summary>The window the annotation is written in.</summary>
    public string WindowName { get; }

    /// <summary>The link of that window (its command, /intel…, or its key), kept in the journal of the activities.</summary>
    public string? SourceLink { get; init; }

    /// <summary>The context saved with the annotation (shown to the user before saving).</summary>
    public string Context { get; }

    /// <summary>Popup title.</summary>
    public string Title => $"Annotate · {TargetLabel}";

    /// <summary>What is annotated, in words: its kind and its title.</summary>
    public string TargetLabel => $"{KindName(Target.Type)}{(string.IsNullOrWhiteSpace(Target.Title) ? string.Empty : " · " + Target.Title)}";

    /// <summary>The part annotated (the text selected), shortened; empty when the whole object is.</summary>
    public string ExcerptPreview => Target.Excerpt is { Length: > 0 } excerpt
        ? (excerpt.Length > 600 ? excerpt[..600] + "…" : excerpt)
        : string.Empty;

    public bool HasExcerpt => !string.IsNullOrEmpty(Target.Excerpt);

    /// <summary>Tags offered (a click adds one): the kind of the object, its entity type, common ones.</summary>
    public IReadOnlyList<string> SuggestedTags { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _text = string.Empty;

    /// <summary>The tags typed, separated by spaces or commas (<c>#SOFTWARE #IDEA</c>; the # is optional).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _tagsText;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isSent;

    /// <summary>Raised when the popup must close (shortly after the server saved the annotation).</summary>
    public event Action? CloseRequested;

    /// <summary>Raised once the annotation is saved, with its id.</summary>
    public event Action<string>? Saved;

    /// <summary>The tags typed, normalized (upper case, #, no duplicates).</summary>
    public List<string> Tags => TagsText
        .Split([' ', ',', ';', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
        .Select(NormalizeTag)
        .Where(t => t is not null)
        .Select(t => t!)
        .Distinct()
        .ToList();

    /// <summary>Adds <paramref name="tag"/> to the tags typed (removes it when it is there already).</summary>
    [RelayCommand]
    private void ToggleTag(string? tag)
    {
        if (NormalizeTag(tag) is not { } normalized)
            return;
        var tags = Tags;
        if (!tags.Remove(normalized))
            tags.Add(normalized);
        TagsText = string.Join(' ', tags);
    }

    private bool CanSave() => !IsSent && (!string.IsNullOrWhiteSpace(Text) || Tags.Count > 0);

    /// <summary>Saves the annotation, shows the server's reply, then closes.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        IsError = false;
        Status = "Saving…";
        string? id = null;
        try
        {
            if (_assistant is null)
                throw new InvalidOperationException("No Kaonomi server configured.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await _assistant.SaveAnnotationAsync(new AnnotationEntry
            {
                MachineName = Environment.MachineName,
                UserName = Environment.UserName,
                Text = Text.Trim(),
                Tags = Tags,
                TargetType = Target.Type,
                TargetId = Target.Id,
                TargetTitle = Target.Title,
                Excerpt = Target.Excerpt,
                Context = Context
            }, timeout.Token);
            IsError = !result.IsSuccess;
            id = result.ReturnedId;
            Status = result.IsSuccess ? "Annotation saved" : "Not saved: " + result.Message;
            if (result.IsSuccess)
            {
                ActivityLog.Info("Annotation", $"Annotation saved ({id}): {Target.Type} {Target.Title} · {string.Join(' ', Tags)}");
                _ = JournalAsync(_assistant);
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant?.BaseAddress} ({ex.Message})";
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = ex is OperationCanceledException ? "No answer from the server (timeout)." : ex.Message;
        }

        // On failure the popup stays open so the annotation typed is not lost and can be saved again.
        if (IsError)
            return;
        IsSent = true;
        if (id is not null)
            Saved?.Invoke(id);
        await Task.Delay(CloseDelay);
        CloseRequested?.Invoke();
    }

    /// <summary>A tag as the server stores it: <c>#</c> and the word in upper case, spaces made underscores; null when empty.</summary>
    public static string? NormalizeTag(string? tag)
    {
        var word = tag?.Trim().TrimStart('#').Trim();
        return string.IsNullOrEmpty(word)
            ? null
            : "#" + string.Join('_', word.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }

    /// <summary>Raised once the annotation is in the journal of the activities (the window of the journal reads it again).</summary>
    public static event Action? Journaled;

    /// <summary>
    /// The annotation in the journal of the activities (index useractivities): the window it was written in, its link,
    /// and the first 50 characters of its text as the description (its tags when it has no text).
    /// </summary>
    private async Task JournalAsync(KaonomiAssistantService server)
    {
        var text = string.Join(' ', Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0)
            text = string.Join(' ', Tags);
        try
        {
            var result = await server.SaveUserActivityAsync(new UserActivityEntry
            {
                AppName = "KAONOMI.Chat",
                MachineName = Environment.MachineName,
                UserName = Environment.UserName,
                Date = DateTimeOffset.Now,
                Action = "ANNOTATION",
                WindowName = WindowName,
                Link = SourceLink ?? "chat",
                Description = text.Length > 50 ? text[..50] : text
            });
            if (result.IsSuccess)
                Journaled?.Invoke();
            else
                ActivityLog.Error("Journal", $"Annotation not journaled: {result.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ActivityLog.Error("Journal", "Annotation not journaled", ex);
        }
    }

    private static string? KindTag(string type) => type switch
    {
        AnnotationTargetTypes.TextBlock => "#TEXT",
        AnnotationTargetTypes.Image => "#IMAGE",
        AnnotationTargetTypes.Video => "#VIDEO",
        AnnotationTargetTypes.Audio => "#AUDIO",
        AnnotationTargetTypes.Entity => "#ENTITY",
        AnnotationTargetTypes.Idea => "#IDEA",
        AnnotationTargetTypes.ProjectItem => "#PROJECT",
        AnnotationTargetTypes.DiagramElement => "#DIAGRAM",
        AnnotationTargetTypes.MindmapTopic => "#MINDMAP",
        _ => NormalizeTag(type)
    };

    private static string KindName(string type) => type switch
    {
        AnnotationTargetTypes.TextBlock => "Text selected",
        AnnotationTargetTypes.Image => "Image",
        AnnotationTargetTypes.Video => "Video",
        AnnotationTargetTypes.Audio => "Audio file",
        AnnotationTargetTypes.Entity => "Entity",
        AnnotationTargetTypes.Idea => "Idea",
        AnnotationTargetTypes.ProjectItem => "Project element",
        AnnotationTargetTypes.DiagramElement => "Diagram element",
        AnnotationTargetTypes.MindmapTopic => "Mind map topic",
        _ => type
    };
}
