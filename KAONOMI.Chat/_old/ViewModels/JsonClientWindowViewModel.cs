using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>Ad-hoc HTTP JSON client with save/load of requests and responses.</summary>
public partial class JsonClientWindowViewModel : ViewModelBase, INoteContextProvider
{
    private readonly JsonClientService _client = new();
    private readonly JsonFileStore _store;
    private JsonClientResponse? _lastResponse;
    private string _lastMethod = string.Empty;
    private string _lastUrl = string.Empty;

    /// <summary>Creates a client with default settings.</summary>
    public JsonClientWindowViewModel() : this(ChatSettings.Load())
    {
    }

    /// <summary>Initializes directories and default URL from settings.</summary>
    public JsonClientWindowViewModel(ChatSettings settings)
    {
        _store = new JsonFileStore(settings);
        _url = settings.ServerUrl.TrimEnd('/') + "/";
        UrlPlaceholder = _url + "…";
    }

    /// <summary>Hint of the URL box: the configured Kaonomi server URL (<c>serverUrl</c> in config.yml).</summary>
    public string UrlPlaceholder { get; }

    /// <summary>HTTP methods offered in the method picker.</summary>
    public IReadOnlyList<string> Methods { get; } = ["GET", "POST", "PUT"];

    /// <summary>Folder where saved request presets are stored.</summary>
    public string CollectionDirectory => _store.CollectionDirectory;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand), nameof(SaveQueryCommand))]
    private string _url;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBodyEnabled))]
    private string _method = "GET";

    [ObservableProperty]
    private string _body = string.Empty;

    [ObservableProperty]
    private string _response = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveResultCommand))]
    [NotifyPropertyChangedFor(nameof(IsResponseSuccess))]
    private bool _hasResponse;

    [ObservableProperty]
    private string _responseStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResponseSuccess))]
    private bool _isResponseError;

    /// <summary>True when a response is shown and the last status was successful.</summary>
    public bool IsResponseSuccess => HasResponse && !IsResponseError;

    [ObservableProperty]
    private string _status = "Enter a URL and send the request";

    [ObservableProperty]
    private bool _isError;

    /// <summary>True when the request body editor applies (non-GET).</summary>
    public bool IsBodyEnabled => Method != "GET";

    private bool IsUrlValid() =>
        Uri.TryCreate(Url?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    /// <summary>Executes the HTTP request and displays formatted response text.</summary>
    [RelayCommand(CanExecute = nameof(IsUrlValid), IncludeCancelCommand = true)]
    private async Task SendAsync(CancellationToken cancellationToken)
    {
        var method = Method;
        var url = Url.Trim();
        SetStatus($"{method} {url}…", log: false);

        try
        {
            var result = await _client.SendAsync(method, url, Body, cancellationToken);

            _lastResponse = result;
            _lastMethod = method;
            _lastUrl = url;

            Response = JsonFileStore.TryFormat(result.Content, out var formatted) ? formatted : result.Content;
            HasResponse = true;
            IsResponseError = !result.IsSuccess;
            ResponseStatus = $"{result.StatusCode} {result.ReasonPhrase} · {result.Elapsed.TotalMilliseconds:0} ms · " +
                             $"{FormatSize(result.Content.Length)}";
            SetStatus($"{method} {url} · {result.StatusCode}", log: false);
            var outcome = $"{method} {url} → {result.StatusCode} {result.ReasonPhrase} ({result.Elapsed.TotalMilliseconds:0} ms, {FormatSize(result.Content.Length)})";
            if (result.IsSuccess)
                ActivityLog.Info(LogArea, outcome);
            else
                ActivityLog.Error(LogArea, outcome);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Request cancelled");
        }
        catch (HttpRequestException ex)
        {
            SetStatus($"Request failed: {ex.Message}", isError: true);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, isError: true);
        }
    }

    /// <summary>Writes the last response body to the result directory.</summary>
    [RelayCommand(CanExecute = nameof(HasResponse))]
    private async Task SaveResultAsync()
    {
        if (_lastResponse is null)
            return;

        try
        {
            var path = await _store.SaveResultAsync(_lastMethod, _lastUrl, _lastResponse.Content);
            SetStatus($"Result saved: {DisplayPath(path)}");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save the result: {ex.Message}", isError: true);
        }
    }

    /// <summary>Saves the current method, URL, and body as a JSON preset.</summary>
    [RelayCommand(CanExecute = nameof(IsUrlValid))]
    private async Task SaveQueryAsync()
    {
        var query = new JsonQuery
        {
            Method = Method,
            Url = Url.Trim(),
            Body = Body,
            SavedAt = DateTimeOffset.Now
        };

        try
        {
            var path = await _store.SaveQueryAsync(query);
            SetStatus($"Request saved: {DisplayPath(path)}");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save the request: {ex.Message}", isError: true);
        }
    }

    /// <summary>Indents the request body when it contains valid JSON.</summary>
    [RelayCommand]
    private void FormatBody()
    {
        if (JsonFileStore.TryFormat(Body, out var formatted))
        {
            Body = formatted;
            ActivityLog.Info(LogArea, "Request body formatted");
        }
        else
            SetStatus("The request is not valid JSON", isError: true);
    }

    /// <summary>Loads a saved request file into the form fields.</summary>
    public async Task LoadQueryAsync(string path)
    {
        try
        {
            var query = await JsonFileStore.LoadQueryAsync(path);
            Method = Methods.FirstOrDefault(m => string.Equals(m, query.Method, StringComparison.OrdinalIgnoreCase))
                     ?? "GET";
            Url = query.Url;
            Body = query.Body;
            SetStatus($"Request loaded: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open the file: {ex.Message}", isError: true);
        }
    }

    private const string LogArea = "JSON client";

    /// <summary>Shows an outcome in the status bar and logs it (INFO or ERROR); progress messages pass <paramref name="log"/> false.</summary>
    private void SetStatus(string status, bool isError = false, bool log = true)
    {
        Status = status;
        IsError = isError;

        if (!log)
            return;
        if (isError)
            ActivityLog.Error(LogArea, status);
        else
            ActivityLog.Info(LogArea, status);
    }

    private static string DisplayPath(string path)
    {
        var relative = Path.GetRelativePath(AppContext.BaseDirectory, path);
        return relative.StartsWith("..", StringComparison.Ordinal) ? path : relative;
    }

    private static string FormatSize(int length) =>
        length < 1024 ? $"{length} B" : $"{length / 1024.0:0.0} KB";

    /// <inheritdoc />
    public string NoteWindowName => "JSON client";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        method = Method,
        url = Url,
        body = Body,
        responseStatus = ResponseStatus,
        response = Response
    };
}
