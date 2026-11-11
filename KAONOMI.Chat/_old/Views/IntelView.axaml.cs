using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>Searches, creates, clones, edits and deletes intels (<c>/intel</c>, <c>/nintel</c>).</summary>
public partial class IntelView : UserControl
{
    /// <summary>Initializes the view and binds Enter to search.</summary>
    public IntelView()
    {
        InitializeComponent();
        SearchBox.KeyDown += OnSearchKeyDown;
    }

    private IntelWindowViewModel? ViewModel => DataContext as IntelWindowViewModel;

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (ViewModel is { } viewModel)
            viewModel.ConfirmAsync = (message, label) =>
                TopLevel.GetTopLevel(this) is Window owner
                    ? ConfirmDialog.AskAsync(owner, message, label)
                    : System.Threading.Tasks.Task.FromResult(false);
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        SearchBox.Focus();
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
