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
/// "Add a note" popup: the user types an anonymous note, sent with the window's context to <c>PUT /whitenote</c>.
/// Deliberately not written to the activity log: a log entry carries the user name and the exact time.
/// </summary>
public partial class NoteDialogViewModel : ViewModelBase
{
    private static readonly TimeSpan CloseDelay = TimeSpan.FromSeconds(2);

    private readonly KaonomiAssistantService? _assistant;

    /// <summary>Prepares a note for the window <paramref name="windowName"/> with its JSON <paramref name="context"/>.</summary>
    public NoteDialogViewModel(KaonomiAssistantService? assistant, string windowName, string context)
    {
        _assistant = assistant;
        WindowName = windowName;
        Context = context;
    }

    /// <summary>Design-time constructor.</summary>
    public NoteDialogViewModel() : this(null, "Kaonomi Chat", "{ \"window\": \"Kaonomi Chat\" }")
    {
    }

    /// <summary>Window the note is about.</summary>
    public string WindowName { get; }

    /// <summary>Context sent with the note (shown to the user before sending).</summary>
    public string Context { get; }

    /// <summary>The project of the window the note is about (the id of a project), saved with the note; null: none.</summary>
    public string? ProjectId { get; set; }

    /// <summary>Popup title.</summary>
    public string Title => $"Kaonomi · Note on {WindowName}";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _text = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isSent;

    /// <summary>Raised when the popup must close (2 seconds after the server's reply).</summary>
    public event Action? CloseRequested;

    private bool CanSave() => !IsSent && !string.IsNullOrWhiteSpace(Text);

    /// <summary>Sends the note, shows the server's reply, then closes after 2 seconds.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        IsError = false;
        Status = "Sending…";

        try
        {
            if (_assistant is null)
                throw new InvalidOperationException("No Kaonomi server configured.");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await _assistant.CreateWhiteNoteAsync(new WhiteNoteEntry { Text = Text.Trim(), Context = Context, ProjectId = ProjectId }, timeout.Token);

            IsError = !result.IsSuccess;
            Status = string.IsNullOrWhiteSpace(result.Message)
                ? (result.IsSuccess ? "Note saved" : "The server rejected the note.")
                : result.Message;
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

        // On failure the popup stays open so the typed note is not lost and can be sent again.
        if (IsError)
            return;

        IsSent = true;
        await Task.Delay(CloseDelay);
        CloseRequested?.Invoke();
    }
}
