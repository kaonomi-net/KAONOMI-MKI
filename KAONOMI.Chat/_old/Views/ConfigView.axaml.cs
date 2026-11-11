using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;

namespace KAONOMI.Chat.Views;

/// <summary>Colored editor of config.yml, with SAVE and RELOAD (<c>/config</c>, <c>/nconfig</c>).</summary>
public partial class ConfigView : UserControl
{
    /// <summary>Recoloring is done once the typing pauses.</summary>
    private readonly DispatcherTimer _recolor = new() { Interval = TimeSpan.FromMilliseconds(120) };

    /// <summary>Initializes the editor and recolors it whenever its text or the theme changes.</summary>
    public ConfigView()
    {
        InitializeComponent();
        _recolor.Tick += (_, _) =>
        {
            _recolor.Stop();
            Recolor();
        };
        Editor.TextChanged += (_, _) =>
        {
            _recolor.Stop();
            _recolor.Start();
        };
        ActualThemeVariantChanged += (_, _) => Recolor();
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Recolor();
        Editor.Focus();
    }

    /// <summary>Colored copy of the text, drawn under the transparent text of the TextBox.</summary>
    private void Recolor()
    {
        var inlines = Highlight.Inlines ??= new Avalonia.Controls.Documents.InlineCollection();
        YamlHighlighter.Highlight(inlines, Editor.Text ?? string.Empty, ActualThemeVariant == ThemeVariant.Dark);
    }
}
