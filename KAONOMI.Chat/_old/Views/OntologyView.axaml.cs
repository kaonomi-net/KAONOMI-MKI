using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>Ontology management: node tree with a context menu and a node editor.</summary>
public partial class OntologyView : UserControl
{
    private OntologyWindowViewModel? _viewModel;

    /// <summary>Initializes the view, Enter-to-search, and right-click selection in the tree.</summary>
    public OntologyView()
    {
        InitializeComponent();

        // macOS: ⌘S saves too (Ctrl+S is the Windows / Linux shortcut).
        if (OperatingSystem.IsMacOS())
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.S, KeyModifiers.Meta),
                Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() =>
                {
                    if ((DataContext as OntologyWindowViewModel)?.SelectedNode?.SaveCommand is { } save && save.CanExecute(null))
                        save.Execute(null);
                })
            });
        // Tunnel so Enter / Escape are seen before the TextBox can handle them.
        SearchBox.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);

        // Select the node under the pointer on right-click, so the context menu and the editor agree.
        NodeTree.AddHandler(PointerPressedEvent, OnTreePointerPressed, RoutingStrategies.Tunnel);

        // Escape leaves "attach to another node" mode.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && _viewModel is { IsAttaching: true } viewModel)
            {
                e.Handled = true;
                viewModel.CancelAttachCommand.Execute(null);
            }
        }, RoutingStrategies.Tunnel);
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
            _viewModel.EditRequested -= OnEditRequested;

        _viewModel = DataContext as OntologyWindowViewModel;

        if (_viewModel is not null)
        {
            _viewModel.EditRequested += OnEditRequested;
            _viewModel.ConfirmAsync = (message, confirmLabel) => ConfirmDialog.AskAsync((Window)TopLevel.GetTopLevel(this)!, message, confirmLabel);
            _viewModel.SaveTextFileAsync = SaveTextFileAsync;
            _viewModel.OpenTextFileAsync = OpenTextFileAsync;
        }
    }

    /// <summary>File pickers of the window hosting this view.</summary>
    private IStorageProvider Storage => TopLevel.GetTopLevel(this)!.StorageProvider;

    private static readonly FilePickerFileType JsonFileType = new("JSON") { Patterns = ["*.json"] };

    /// <summary>Asks where to save an export and writes it (UTF-8, no BOM); returns the file name, or null when cancelled.</summary>
    private async Task<string?> SaveTextFileAsync(string suggestedName, string content)
    {
        var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export ontology nodes",
            SuggestedFileName = suggestedName,
            DefaultExtension = "json",
            ShowOverwritePrompt = true,
            FileTypeChoices = [JsonFileType]
        });
        if (file is null)
            return null;

        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        await writer.WriteAsync(content);
        return file.Name;
    }

    /// <summary>Asks for a JSON file to import and returns its text, or null when cancelled.</summary>
    private async Task<string?> OpenTextFileAsync()
    {
        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import ontology nodes",
            AllowMultiple = false,
            FileTypeFilter = [JsonFileType]
        });
        if (files.Count == 0)
            return null;

        await using var stream = await files[0].OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private bool _refreshed;

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // Loaded again each time its tab is selected: the tree is only fetched the first time.
        if (!_refreshed && _viewModel is not null)
        {
            _refreshed = true;
            _viewModel.RefreshCommand.Execute(null);
        }
    }

    /// <summary>Focuses the name field once the editor shows the requested node.</summary>
    private void OnEditRequested(OntologyNodeViewModel node) =>
        Dispatcher.UIThread.Post(() =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        }, DispatcherPriority.Background);

    private void OnTreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_viewModel is null || (e.Source as Control)?.DataContext is not OntologyTreeItem item)
            return;

        var point = e.GetCurrentPoint(NodeTree).Properties;

        // Attach mode: the clicked node becomes the virtual parent (the click does not change the selection).
        if (_viewModel.IsAttaching && point.IsLeftButtonPressed)
        {
            e.Handled = true;
            _ = _viewModel.CompleteAttachAsync(item.Node);
            return;
        }

        if (point.IsRightButtonPressed)
            _viewModel.SelectedTreeItem = item;
    }

    /// <summary>Enter runs the search now and selects the first match; Escape clears it.</summary>
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
            return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _viewModel.SearchCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.ClearSearchCommand.Execute(null);
        }
    }
}
