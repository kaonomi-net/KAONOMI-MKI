using System;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.ViewModels;

/// <summary>One message of the scrolling ticker band, colored by trend.</summary>
public sealed class TickerItemViewModel : ViewModelBase
{
    /// <summary>Wraps a message flow entry for display.</summary>
    public TickerItemViewModel(MessageFlowEntry entry)
    {
        Text = entry.Text ?? string.Empty;
        Url = Uri.TryCreate(entry.Url?.Trim(), UriKind.Absolute, out var url)
              && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps)
            ? url
            : null;
        IsRed = string.Equals(entry.Color?.Trim(), "red", StringComparison.OrdinalIgnoreCase);
        IsGreen = string.Equals(entry.Color?.Trim(), "green", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Message text.</summary>
    public string Text { get; }

    /// <summary>Link opened in the browser on click; null when the entry has no valid http(s) URL.</summary>
    public Uri? Url { get; }

    /// <summary>True when clicking the message opens a link.</summary>
    public bool HasUrl => Url is not null;

    /// <summary>True for a falling (red) message.</summary>
    public bool IsRed { get; }

    /// <summary>True for a rising (green) message.</summary>
    public bool IsGreen { get; }

    /// <summary>Down arrow for red, up arrow for green, nothing otherwise.</summary>
    public string Arrow => IsRed ? "▼" : IsGreen ? "▲" : string.Empty;

    /// <summary>True when an arrow is shown before the text.</summary>
    public bool HasArrow => IsRed || IsGreen;
}
