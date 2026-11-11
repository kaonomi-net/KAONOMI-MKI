using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>Lists, searches, creates and edits ideas.</summary>
public partial class IdeaView : UserControl
{
    private IdeaWindowViewModel? _viewModel;
    private bool _loaded;

    /// <summary>Initializes the view, Enter-to-search and the macOS ⌘S / ⌘N shortcuts.</summary>
    public IdeaView()
    {
        InitializeComponent();
        SearchBox.KeyDown += OnSearchKeyDown;

        // macOS: ⌘S / ⌘N too (Ctrl+S / Ctrl+N are the Windows / Linux shortcuts).
        if (OperatingSystem.IsMacOS())
        {
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.S, KeyModifiers.Meta),
                Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() =>
                {
                    if (_viewModel?.SelectedResult?.SaveCommand is { } save && save.CanExecute(null))
                        save.Execute(null);
                })
            });
            KeyBindings.Add(new KeyBinding
            {
                Gesture = new KeyGesture(Key.N, KeyModifiers.Meta),
                Command = new CommunityToolkit.Mvvm.Input.RelayCommand(NewIdea)
            });
        }
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
            _viewModel.ConfirmAsync = (_, _) => System.Threading.Tasks.Task.FromResult(true);

        _viewModel = DataContext as IdeaWindowViewModel;

        if (_viewModel is not null)
            _viewModel.ConfirmAsync = (message, confirmLabel) =>
                ConfirmDialog.AskAsync((Window)TopLevel.GetTopLevel(this)!, message, confirmLabel);
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        SearchBox.Focus();

        // The latest ideas are listed when the view opens (once: a tab can be loaded again when it is re-docked).
        if (!_loaded && _viewModel?.SearchCommand is { } search && search.CanExecute(null))
        {
            _loaded = true;
            search.Execute(null);
        }
    }

    /// <summary>Starts a new idea and puts the cursor in its title.</summary>
    private void NewIdea()
    {
        if (_viewModel is null)
            return;
        _viewModel.NewCommand.Execute(null);
        Dispatcher.UIThread.Post(() => TitleBox.Focus(), DispatcherPriority.Background);
    }

    /// <summary>Runs search when Enter is pressed in the search box.</summary>
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewModel is not { } viewModel)
            return;

        e.Handled = true;
        if (viewModel.SearchCommand.CanExecute(null))
            viewModel.SearchCommand.Execute(null);
    }
}
