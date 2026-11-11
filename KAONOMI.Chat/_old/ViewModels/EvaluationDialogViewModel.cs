using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// Popup shown after a thumb up / down on an answer: the user explains the evaluation, which is sent to the
/// server; the server's reply is shown and the popup closes 2 seconds later.
/// </summary>
public partial class EvaluationDialogViewModel : ViewModelBase
{
    /// <summary>Time the server's reply stays visible before the popup closes.</summary>
    private static readonly TimeSpan CloseDelay = TimeSpan.FromSeconds(2);

    private readonly KaonomiAssistantService _assistant;
    private readonly EvaluationEntry _entry;

    /// <summary>Prepares the evaluation of an answer; <see cref="EvaluationEntry.Explanation"/> comes from the text box.</summary>
    public EvaluationDialogViewModel(KaonomiAssistantService assistant, EvaluationEntry entry)
    {
        _assistant = assistant;
        _entry = entry;
    }

    /// <summary>Design-time constructor.</summary>
    public EvaluationDialogViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl),
        new EvaluationEntry { Prompt = "Question", Completion = "Answer" })
    {
    }

    /// <summary>True for a thumb down.</summary>
    public bool IsNegative => _entry.IsNegativeFeedback;

    /// <summary>Window title.</summary>
    public string Title => IsNegative ? "Kaonomi · Dislike" : "Kaonomi · Like";

    /// <summary>Question shown above the text box.</summary>
    public string Question => IsNegative
        ? "What was wrong with this answer?"
        : "What did you like about this answer?";

    /// <summary>Beginning of the evaluated answer, as a reminder.</summary>
    public string AnswerPreview => _entry.Completion.Length <= 240
        ? _entry.Completion
        : _entry.Completion[..240] + "…";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _explanation = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool _isSent;

    /// <summary>True once the server accepted the evaluation (the bubble then highlights the thumb).</summary>
    public bool Succeeded { get; private set; }

    /// <summary>Raised when the popup must close (2 seconds after the server's reply).</summary>
    public event Action? CloseRequested;

    private bool CanSend() => !IsSent;

    /// <summary>Sends <c>PUT /evaluation</c>, shows the server's reply, then closes after 2 seconds.</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync(CancellationToken cancellationToken)
    {
        _entry.Explanation = Explanation.Trim();
        _entry.Date = DateTimeOffset.Now;
        IsError = false;
        Status = "Sending…";

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await _assistant.CreateEvaluationAsync(_entry, timeout.Token);

            Succeeded = result.IsSuccess;
            IsError = !result.IsSuccess;
            Status = string.IsNullOrWhiteSpace(result.Message)
                ? (result.IsSuccess ? $"Saved (id {result.ReturnedId})" : "The server rejected the evaluation.")
                : result.Message;

            var what = $"{(IsNegative ? "Dislike" : "Like")} sent for \"{ActivityLog.Excerpt(_entry.Prompt, 80)}\" ({Status})";
            if (result.IsSuccess)
                ActivityLog.Info("Evaluation", what);
            else
                ActivityLog.Error("Evaluation", what);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            IsError = true;
            Status = $"Kaonomi server unreachable at {_assistant.BaseAddress} ({ex.Message})";
            ActivityLog.Error("Evaluation", $"{(IsNegative ? "Dislike" : "Like")} not sent: server unreachable", ex);
        }
        catch (Exception ex)
        {
            IsError = true;
            Status = ex is OperationCanceledException ? "No answer from the server (timeout)." : ex.Message;
            ActivityLog.Error("Evaluation", $"{(IsNegative ? "Dislike" : "Like")} not sent", ex);
        }

        // Reply shown, then the popup closes, whatever the outcome.
        IsSent = true;
        await Task.Delay(CloseDelay);
        CloseRequested?.Invoke();
    }
}
