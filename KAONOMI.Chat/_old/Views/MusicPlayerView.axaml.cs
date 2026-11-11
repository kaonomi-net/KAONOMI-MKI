using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>Music player (<c>/music</c>): folders, tracks and the track being played.</summary>
public partial class MusicPlayerView : UserControl
{
    /// <summary>The track being played is shown in bold in the list.</summary>
    public static readonly IValueConverter CurrentWeight =
        new FuncValueConverter<bool, FontWeight>(current => current ? FontWeight.Bold : FontWeight.Normal);

    private MusicPlayerViewModel? ViewModel => DataContext as MusicPlayerViewModel;

    /// <summary>
    /// Initializes the view: double-click or Enter plays a track, Space plays / pauses (outside the search box), F5
    /// refreshes; the position slider seeks when it is released.
    /// </summary>
    public MusicPlayerView()
    {
        InitializeComponent();
        TrackList.DoubleTapped += (_, _) => ViewModel?.PlayTrackCommand.Execute(ViewModel.SelectedTrack);
        TrackList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && ViewModel is { } viewModel)
            {
                viewModel.PlayTrackCommand.Execute(viewModel.SelectedTrack);
                e.Handled = true;
            }
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (ViewModel is not { } viewModel)
                return;
            if (e.Key == Key.Space && e.Source is not TextBox)
            {
                viewModel.PlayPauseCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                viewModel.RefreshCommand.Execute(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        // Seek: while the slider is held its value is not moved by the playback, the track jumps when it is released.
        PositionSlider.AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (ViewModel is { } viewModel)
                viewModel.IsSeeking = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        PositionSlider.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            if (ViewModel is not { } viewModel)
                return;
            viewModel.SeekTo(PositionSlider.Value);
            viewModel.IsSeeking = false;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }
}
