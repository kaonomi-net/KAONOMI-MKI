using Avalonia.Controls;
using Avalonia.Interactivity;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>Tokens and models consumed (<c>/token</c>, <c>/tokens</c>).</summary>
public partial class TokenDashboardView : UserControl
{
    /// <summary>Initializes the view.</summary>
    public TokenDashboardView()
    {
        InitializeComponent();
    }

    private bool _refreshed;

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        // Loaded again each time its tab is selected: the data is only fetched the first time.
        if (!_refreshed && DataContext is TokenDashboardViewModel viewModel)
        {
            _refreshed = true;
            viewModel.RefreshCommand.Execute(null);
        }
    }
}
