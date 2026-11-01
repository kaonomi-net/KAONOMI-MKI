using System;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// /activity: the journal of the activities (index useractivities): the SAVE done in the windows, the newest first, a
/// search in their window, link and description; each one opens its window again.
/// </summary>
public partial class ActivityWindowViewModel : ViewModelBase
{
    private const int PageSize = 200;
    private readonly KaonomiAssistantService? _server;

    public ActivityWindowViewModel() : this(null)
    {
    }

    public ActivityWindowViewModel(KaonomiAssistantService? server)
    {
        _server = server;
        if (server is not null)
            _ = SearchAsync(CancellationToken.None);
    }

    /// <summary>The activities found, the newest first.</summary>
    public ObservableCollection<UserActivityEntry> Entries { get; } = [];

    [ObservableProperty]
    private string _term = string.Empty;

    [ObservableProperty]
    private string _status = "Reading the journal…";

    [ObservableProperty]
    private bool _isError;

    /// <summary>The activities matching <see cref="Term"/>, or the newest when it is empty.</summary>
    [RelayCommand]
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        if (_server is null)
        {
            Status = "No Kaonomi server: the journal cannot be read";
            IsError = true;
            return;
        }
        var term = Term.Trim();
        try
        {
            var entries = await _server.GetUserActivitiesAsync(term, PageSize, cancellationToken);
            Entries.Clear();
            foreach (var entry in entries)
                Entries.Add(entry);
            IsError = false;
            Status = entries.Count == 0
                ? term.Length == 0 ? "The journal is empty: each SAVE done in a window is written here" : $"No activity for \"{term}\""
                : term.Length == 0 ? $"{entries.Count} latest activit{(entries.Count > 1 ? "ies" : "y")}" : $"{entries.Count} activit{(entries.Count > 1 ? "ies" : "y")} for \"{term}\"";
        }
        catch (OperationCanceledException)
        {
            Status = "Search cancelled";
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            IsError = true;
            Status = "The journal could not be read: " + ex.Message;
        }
    }
}
