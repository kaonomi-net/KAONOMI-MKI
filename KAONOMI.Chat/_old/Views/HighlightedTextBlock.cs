using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>Text block showing <see cref="Segments"/>, the detected parts highlighted in yellow (black text).</summary>
public sealed class HighlightedTextBlock : TextBlock
{
    private static readonly IBrush HighlightBackground = new SolidColorBrush(Color.Parse("#FFE81A"));

    /// <summary>Parts of the text; the ones with <see cref="TextSegment.IsMatch"/> are highlighted.</summary>
    public static readonly StyledProperty<IReadOnlyList<TextSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, IReadOnlyList<TextSegment>?>(nameof(Segments));

    /// <inheritdoc cref="SegmentsProperty" />
    public IReadOnlyList<TextSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SegmentsProperty)
            return;

        var inlines = new InlineCollection();
        foreach (var segment in Segments ?? [])
        {
            inlines.Add(segment.IsMatch
                ? new Run(segment.Text) { Background = HighlightBackground, Foreground = Brushes.Black, FontWeight = FontWeight.SemiBold }
                : new Run(segment.Text));
        }
        Inlines = inlines;
    }
}
