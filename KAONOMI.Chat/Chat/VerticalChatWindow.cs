using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Chat;

/// <summary>
/// The full chat of a vertical screen (a screen taller than wide): the chat alone, in full screen on that screen, the
/// 3D animation of the preset over its upper part and, under it, the conversation and the input. It is a second view
/// of the same chat (the same <see cref="MainViewModel"/>): the conversation, the model, the preset and the voice are
/// those of the chat of the main window, and a prompt typed in one shows in the other. Opened at the start when a
/// vertical screen is connected (<c>--no-vertical</c> on the command line leaves it closed) and by <c>/fullchat</c>;
/// Escape closes it, <c>/fullchat</c> brings it back. The tools still open in the desktop of the main window.
/// </summary>
public sealed class VerticalChatWindow : Window
{
    /// <summary>The share of the height the 3D animation takes, as long as the conversation keeps <see cref="MinConversation"/>.</summary>
    private const double BandShare = 0.6;
    private const double MinConversation = 520;

    private readonly ChatPanel _panel;

    public VerticalChatWindow(MainViewModel viewModel, Screen screen)
    {
        Title = "Kaonomi · full chat";
        SystemDecorations = WindowDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Ui.Panel;
        this.Bind(FontFamilyProperty, this.GetResourceObservable("BodyFont", v => v as Avalonia.Media.FontFamily ?? Avalonia.Media.FontFamily.Default));

        // The whole of the vertical screen.
        Position = screen.Bounds.Position;
        Width = screen.Bounds.Width / screen.Scaling;
        Height = screen.Bounds.Height / screen.Scaling;

        _panel = new ChatPanel(viewModel)
        {
            Width = double.NaN,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            BorderThickness = new Thickness(0)
        };
        Content = _panel;

        SizeChanged += (_, _) => Fit();
        Opened += (_, _) =>
        {
            // Full screen on the screen the window was put on.
            Position = screen.Bounds.Position;
            WindowState = WindowState.FullScreen;
            Fit();
            _panel.FocusInput();
            ActivityLog.Info("FullChat", $"Full chat opened on the vertical screen ({screen.Bounds.Width}x{screen.Bounds.Height})");
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    /// <summary>The animation over the upper part of the screen, the conversation under it.</summary>
    private void Fit()
    {
        var height = ClientSize.Height;
        if (height > 0)
            _panel.StageHeight = Math.Max(200, Math.Min(Math.Round(height * BandShare), height - MinConversation));
    }

    /// <summary>
    /// The vertical screen of the full chat: a screen taller than wide, another one than the screen of
    /// <paramref name="owner"/> when there is one, the largest first; null when no screen is vertical.
    /// </summary>
    public static Screen? FindVerticalScreen(Window owner)
    {
        var own = owner.Screens.ScreenFromWindow(owner);
        return owner.Screens.All
            .Where(s => s.Bounds.Height > s.Bounds.Width)
            .OrderBy(s => own is not null && s.Bounds == own.Bounds ? 1 : 0)
            .ThenByDescending(s => (long)s.Bounds.Width * s.Bounds.Height)
            .FirstOrDefault();
    }
}
