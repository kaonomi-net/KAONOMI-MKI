using System;
using System.IO;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Data.Converters;
using Avalonia.Media;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>Dashboard of the most recent logs (<c>/log</c>, <c>/logs</c>).</summary>
public partial class LogDashboardView : UserControl
{
    /// <summary>Badge of the log type: red for ERROR, blue for INFO.</summary>
    public static readonly IValueConverter TypeBackground =
        new FuncValueConverter<bool, IBrush>(error => new SolidColorBrush(Color.Parse(error ? "#66E81123" : "#442B9BF4")));

    /// <summary>Initializes the view.</summary>
    public LogDashboardView()
    {
        InitializeComponent();
    }

    /// <summary>File pickers of the window hosting this view.</summary>
    private IStorageProvider Storage => TopLevel.GetTopLevel(this)!.StorageProvider;

    private bool _refreshed;

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // Loaded again each time its tab is selected: the data is only fetched the first time.
        if (!_refreshed && DataContext is LogDashboardViewModel viewModel)
        {
            _refreshed = true;
            viewModel.RefreshCommand.Execute(null);
        }
    }

    /// <summary>SAVE: writes the content of the window to a JSON file chosen by the user.</summary>
    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogDashboardViewModel viewModel)
            return;

        try
        {
            var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the logs dashboard",
                SuggestedFileName = $"logs-dashboard-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                DefaultExtension = "json",
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }]
            });
            if (file is null)
                return;

            var export = viewModel.BuildExport();
            await using (var stream = await file.OpenWriteAsync())
            {
                stream.SetLength(0);
                await JsonSerializer.SerializeAsync(stream, export, new JsonSerializerOptions { WriteIndented = true });
            }
            viewModel.Status = $"Saved to {file.Name}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            viewModel.Status = $"Save failed: {ex.Message}";
        }
    }
}
