using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>Searches and edits symbolic pattern documents.</summary>
public partial class SymbolicPatternView : UserControl
{
    /// <summary>Initializes the view and binds Enter to search.</summary>
    public SymbolicPatternView()
    {
        InitializeComponent();
        SearchBox.KeyDown += OnSearchKeyDown;
    }

    /// <summary>Red border for a regular expression that does not compile (default border otherwise).</summary>
    public static readonly IValueConverter InvalidBorder =
        new FuncValueConverter<bool, IBrush?>(invalid => invalid ? new SolidColorBrush(Color.Parse("#E81123")) : null);

    /// <summary>Green ✓ / red ✗ of the regex assistant test results.</summary>
    public static readonly IValueConverter MatchBrush =
        new FuncValueConverter<bool, IBrush>(match => new SolidColorBrush(Color.Parse(match ? "#16C60C" : "#E81123")));

    private SymbolicPatternWindowViewModel? ViewModel => DataContext as SymbolicPatternWindowViewModel;

    /// <summary>File pickers of the window hosting this view.</summary>
    private IStorageProvider Storage => TopLevel.GetTopLevel(this)!.StorageProvider;

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        SearchBox.Focus();
    }

    /// <summary>Saves the regex of the assistant with its goal and examples to a JSON file of the regex folder.</summary>
    private async void OnSaveRegexClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.RegexAssistant is not { } assistant)
            return;

        try
        {
            Directory.CreateDirectory(assistant.Directory);
            var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save regex",
                SuggestedStartLocation = await Storage.TryGetFolderFromPathAsync(assistant.Directory),
                SuggestedFileName = assistant.SuggestedFileName,
                DefaultExtension = "json",
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("Regex (JSON)") { Patterns = ["*.json"] }]
            });
            if (file is null)
                return;

            await using var stream = await file.OpenWriteAsync();
            await assistant.SaveAsync(stream, file.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            assistant.IsError = true;
            assistant.Status = $"Not saved: {ex.Message}";
        }
    }

    /// <summary>Loads a saved regex (goal, examples, regex) into the assistant.</summary>
    private async void OnLoadRegexClick(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.RegexAssistant is not { } assistant)
            return;

        try
        {
            Directory.CreateDirectory(assistant.Directory);
            var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Load regex",
                AllowMultiple = false,
                SuggestedStartLocation = await Storage.TryGetFolderFromPathAsync(assistant.Directory),
                FileTypeFilter =
                [
                    new FilePickerFileType("Regex (JSON)") { Patterns = ["*.json"] },
                    new FilePickerFileType("All files") { Patterns = ["*.*"] }
                ]
            });
            if (files is not [var file])
                return;

            await using var stream = await file.OpenReadAsync();
            await assistant.LoadAsync(stream, file.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            assistant.IsError = true;
            assistant.Status = $"Not loaded: {ex.Message}";
        }
    }

    /// <summary>Runs search when Enter is pressed in the search box.</summary>
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ViewModel is not { } viewModel)
            return;

        e.Handled = true;
        if (viewModel.SearchCommand.CanExecute(null))
            viewModel.SearchCommand.Execute(null);
    }
}
