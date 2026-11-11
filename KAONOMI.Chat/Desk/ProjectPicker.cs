using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.UI.Desk;

/// <summary>
/// The field of the project of a document, at the top of its window (a bar under the header: PROJECT and a list), as
/// in the Godot chat: the projects of the server by name (their id is never shown), "No project" first. What is chosen is written in the document shown by
/// the window (<see cref="IProjectDocument.ProjectId"/>); it follows the document when another one is selected, and
/// the list when a project is saved or deleted. Disabled while the window shows no document.
/// </summary>
public static class ProjectPicker
{
    private sealed record Item(string? Id, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>The field of the project of <paramref name="document"/>, for the top of its window.</summary>
    public static Control Create(IProjectDocument document)
    {
        var combo = new ComboBox
        {
            FontSize = 13,
            MinWidth = 320,
            MaxWidth = 480,
            VerticalAlignment = VerticalAlignment.Center
        };
        var hint = Ui.Label(string.Empty, Ui.Muted, 11);
        hint.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(combo, "The project this document is attached to (chosen by its name; kept with the document when it is saved)");
        var items = new List<Item>();
        var updating = false;

        void Select()
        {
            updating = true;
            try
            {
                var id = string.IsNullOrWhiteSpace(document.ProjectId) ? null : document.ProjectId;
                var item = items.FirstOrDefault(i => i.Id == id);
                if (item is null)
                {
                    // The project of the document is not in the list (deleted since): shown as such, and kept.
                    item = new Item(id, "🎯 (unknown project)");
                    items.Add(item);
                    combo.ItemsSource = items.ToList();
                }
                combo.SelectedItem = item;
                combo.IsEnabled = document.HasProjectDocument;
                hint.Text = !document.HasProjectDocument ? "select a document to attach it to a project"
                    : id is null ? "this document is attached to no project" : "this document is attached to this project (kept when it is saved)";
            }
            finally
            {
                updating = false;
            }
        }

        void Fill()
        {
            updating = true;
            items.Clear();
            items.Add(new Item(null, "🎯 No project"));
            items.AddRange(ProjectCatalog.Projects.Select(p => new Item(p.Id, "🎯 " + p.Name)));
            combo.ItemsSource = items.ToList();
            updating = false;
            Select();
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (updating || combo.SelectedItem is not Item item || !document.HasProjectDocument || item.Id == document.ProjectId)
                return;
            document.ProjectId = item.Id;
            Select();
        };

        void OnCatalog() => Dispatcher.UIThread.Post(Fill);
        void OnDocument(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(IProjectDocument.ProjectId) or nameof(IProjectDocument.HasProjectDocument))
                Dispatcher.UIThread.Post(Select);
        }
        combo.AttachedToVisualTree += (_, _) =>
        {
            ProjectCatalog.Changed -= OnCatalog;
            ProjectCatalog.Changed += OnCatalog;
            document.PropertyChanged -= OnDocument;
            document.PropertyChanged += OnDocument;
            Fill();
        };
        combo.DetachedFromVisualTree += (_, _) =>
        {
            ProjectCatalog.Changed -= OnCatalog;
            document.PropertyChanged -= OnDocument;
        };

        Fill();
        // A list that is still empty (the chat just started, or a project was made elsewhere) is read again.
        if (ProjectCatalog.Projects.Count == 0)
            _ = ProjectCatalog.RefreshAsync();
        var caption = Ui.Label("PROJECT", Ui.Muted, 11, bold: true);
        caption.VerticalAlignment = VerticalAlignment.Center;
        var bar = Ui.Row(10, caption, combo, hint);
        bar.Margin = new Thickness(12, 8, 12, 0);
        return bar;
    }
}
