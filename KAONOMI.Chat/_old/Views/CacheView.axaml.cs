using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>Searches and edits Kaonomi cache entries.</summary>
public partial class CacheView : UserControl
{
    /// <summary>Initializes the view and binds Enter to search.</summary>
    public CacheView()
    {
        InitializeComponent();
        SearchBox.KeyDown += OnSearchKeyDown;
    }

    private CacheWindowViewModel? ViewModel => DataContext as CacheWindowViewModel;

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
