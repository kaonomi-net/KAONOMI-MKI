using System;
using System.ComponentModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;
using KAONOMI.UI.Tools.Work;

namespace KAONOMI.UI.Tools.Writing;

/// <summary>
/// The page of /journal, /wiki and the chapters of /write, as in the Godot chat: the rich editor of /work
/// (<see cref="TapeEditor"/>, the text edited as it is shown) with its ribbon (<see cref="TapeRibbon"/>), the ribbon fixed
/// above and the page scrolling below. The text stays Markdown (<see cref="TapeEditor.KeepSource"/>: the blocks left
/// unchanged are written back exactly as they were read).
/// </summary>
public sealed class TapePage
{
    public TapePage(Shell shell, ITapeAssistant? assistant, string category, string subject, string zoomKey,
        Func<string, string> cropDirectory, string placeholder, bool linksOpenOnClick = false)
    {
        Editor = new TapeEditor { KeepSource = true, LinksOpenOnClick = linksOpenOnClick, Placeholder = placeholder };
        Status = Ui.Label("", Ui.Green, 11);
        Status.MaxWidth = 520;
        Host = new Grid();
        Ribbon = new TapeRibbon(Editor, new Overlay(), shell, Host, Status, assistant, category, subject, zoomKey, cropDirectory);
    }

    /// <summary>The editor (the page).</summary>
    public TapeEditor Editor { get; }

    /// <summary>The ribbon of the editor (its groups: font, paragraph, styles, pictures, links, code, zoom).</summary>
    public TapeRibbon Ribbon { get; }

    /// <summary>The short messages of the ribbon, at the right of its tabs.</summary>
    public TextBlock Status { get; }

    /// <summary>The page with its ribbon (built by <see cref="Build"/>); the waiting popups of the ribbon go over it.</summary>
    public Grid Host { get; }

    /// <summary>The text of the editor, both ways, with the property <paramref name="path"/> (a string) of <paramref name="source"/>.</summary>
    public void Bind(INotifyPropertyChanged source, string path)
    {
        var property = source.GetType().GetProperty(path, BindingFlags.Public | BindingFlags.Instance)
                       ?? throw new ArgumentException($"{source.GetType().Name} has no property {path}");
        Editor.TextCommitted += text => property.SetValue(source, text);
        Ui.When(Editor, source, () => Editor.Text = property.GetValue(source) as string ?? string.Empty, path);
    }

    /// <summary>The page: the ribbon of <paramref name="tabs"/> fixed above, the editor scrolling below.</summary>
    public Grid Build(params (string Name, Control[] Groups)[] tabs)
    {
        var ribbon = Ribbon.Build(Status, tabs);
        DockPanel.SetDock(ribbon, Avalonia.Controls.Dock.Top);
        var scroll = new ScrollViewer
        {
            Content = Editor,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Host.Children.Add(new Border
        {
            Background = WorkPalette.PageBrush,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1),
            Child = new DockPanel { LastChildFill = true, Children = { ribbon, scroll } }
        });
        return Host;
    }

    /// <summary>A short message at the right of the tabs of the ribbon.</summary>
    public void ShowStatus(string message, Avalonia.Media.IBrush? color = null) => Ribbon.ShowStatus(message, color);

    /// <summary>Pictures dropped on the window: inserted where they fall on the page, else at the caret.</summary>
    public void InsertDropped(System.Collections.Generic.IReadOnlyList<string> pictures, Avalonia.Input.DragEventArgs e)
    {
        var at = e.GetPosition(Editor);
        if (new Rect(Editor.Bounds.Size).Contains(at))
            Editor.InsertImagesAt(pictures, at);
        else
            Editor.InsertImages(pictures);
    }
}
