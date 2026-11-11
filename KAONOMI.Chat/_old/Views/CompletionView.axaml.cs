using System;
using Avalonia.Controls;
using Avalonia.Input;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>Completions (<c>/completion</c>): search, results, word cloud of the results and the selected completion.</summary>
public partial class CompletionView : UserControl
{
    private CompletionWindowViewModel? _viewModel;

    /// <summary>Initializes the view: Enter in the search box searches.</summary>
    public CompletionView()
    {
        InitializeComponent();
        SearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _viewModel is { } viewModel)
            {
                viewModel.SearchCommand.Execute(null);
                e.Handled = true;
            }
        };
        AttachedToVisualTree += (_, _) => SearchBox.Focus();
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.ConfirmAsync = (_, _) => System.Threading.Tasks.Task.FromResult(false);
        _viewModel = DataContext as CompletionWindowViewModel;
        if (_viewModel is not null)
            _viewModel.ConfirmAsync = (message, label) => ConfirmDialog.AskAsync((Window)TopLevel.GetTopLevel(this)!, message, label);
    }
}
