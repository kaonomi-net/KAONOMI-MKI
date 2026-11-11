using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.ViewModels;

/// <summary>Identifies whether a chat line was sent by the user or the assistant.</summary>
public enum ChatRole
{
    User,
    Assistant
}

/// <summary>Single message in the main chat transcript, including optional stats and code blocks.</summary>
public partial class ChatMessageViewModel : ViewModelBase
{
    /// <summary>Creates a message with the given role and initial text.</summary>
    public ChatMessageViewModel(ChatRole role, string content = "")
    {
        Role = role;
        _content = content;
    }

    /// <summary>Local time the message was created, used by the exports.</summary>
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.Now;

    /// <summary>True for messages produced by the client itself (e.g. <c>/getthemes</c>); they are never sent to the model.</summary>
    public bool IsLocal { get; init; }

    /// <summary>Role of this message in the UI and API.</summary>
    public ChatRole Role { get; }

    /// <summary>True when this line was sent by the user.</summary>
    public bool IsUser => Role == ChatRole.User;

    /// <summary>True when this line is an assistant reply.</summary>
    public bool IsAssistant => Role == ChatRole.Assistant;

    /// <summary>Display name shown in the message header.</summary>
    public string Author => IsUser ? "You" : "Kaonomi";

    /// <summary>Role string sent to the Kaonomi assistant API.</summary>
    public string ApiRole => IsUser ? "user" : "assistant";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEvaluate))]
    private string _content;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStats))]
    private string? _stats;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEvaluate))]
    private bool _isPending;

    /// <summary>
    /// True while the answer is revealed word by word (<see cref="Content"/> grows): the view appends the words as
    /// plain text, cheaply; false once it is complete: the view formats the Markdown of the whole answer, once.
    /// </summary>
    [ObservableProperty]
    private bool _isRevealing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEvaluate))]
    private bool _isError;

    /// <summary>The model that gave this answer (the one the server names), null for the other messages.</summary>
    public string? Model { get; set; }

    /// <summary>The request this answer replied to, as it was sent (system prompt, prompt with its history), to ask it again.</summary>
    public IReadOnlyList<AssistantMessage>? Request { get; set; }

    /// <summary>True once this answer was disliked and asked again to another model: it is asked again only once.</summary>
    public bool AskedAgain { get; set; }

    /// <summary>True for a finished model answer, which can be liked or disliked (not for local or error bubbles).</summary>
    public bool CanEvaluate => IsAssistant && !IsLocal && !IsPending && !IsError && !string.IsNullOrWhiteSpace(Content);

    /// <summary>Evaluation sent for this answer: true = liked, false = disliked, null = not evaluated yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLiked), nameof(IsDisliked))]
    private bool? _feedback;

    /// <summary>True once a "like" has been sent (highlights the thumb up).</summary>
    public bool IsLiked => Feedback == true;

    /// <summary>True once a "dislike" has been sent (highlights the thumb down).</summary>
    public bool IsDisliked => Feedback == false;

    /// <summary>True when token or timing stats are available for this message.</summary>
    public bool HasStats => !string.IsNullOrEmpty(Stats);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCode), nameof(ShowCodeLabel))]
    private IReadOnlyList<CodeSnippet> _codeSnippets = [];

    /// <summary>True when the assistant answer contains at least one fenced code block.</summary>
    public bool HasCode => CodeSnippets.Count > 0;

    /// <summary>Label for the button that opens the code viewer.</summary>
    public string ShowCodeLabel => CodeSnippets.Count == 1
        ? "Show code"
        : $"Show code ({CodeSnippets.Count} blocks)";
}
