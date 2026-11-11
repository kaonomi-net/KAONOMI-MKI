using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Godot;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The field of the project of a document, at the top of its window (a bar under the header: PROJECT and a list):
/// the projects of the server by name (their id is never shown), "No project" first. What is chosen is written in the document shown by the window
/// (<see cref="IProjectDocument.ProjectId"/>); it follows the document when another one is selected, and the list
/// when a project is saved or deleted. Disabled while the window shows no document.
/// </summary>
public static class ProjectPicker
{
    /// <summary>Adds the field of the project of <paramref name="document"/> at the top of <paramref name="frame"/>, under its header.</summary>
    public static void Attach(EveFrame frame, IProjectDocument document)
    {
        var option = new OptionButton
        {
            FitToLongestItem = false,
            ClipText = true,
            CustomMinimumSize = new Vector2(320, 26),
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            TooltipText = "The project this document is attached to (chosen by its name; kept with the document when it is saved)"
        };
        option.AddThemeFontSizeOverride("font_size", 13);

        var hint = Eve.Text_(string.Empty, Eve.Muted, 11);
        hint.ClipText = true;
        hint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        // The ids of the items, in their order (the first: no project).
        var ids = new List<string?>();

        void Select()
        {
            var index = ids.IndexOf(string.IsNullOrWhiteSpace(document.ProjectId) ? null : document.ProjectId);
            if (index < 0)
            {
                // The project of the document is not in the list (deleted since): shown as such, and kept.
                ids.Add(document.ProjectId);
                option.AddItem("🎯 (unknown project)");
                index = ids.Count - 1;
            }
            option.Select(index);
            option.Disabled = !document.HasProjectDocument;
            hint.Text = !document.HasProjectDocument ? "select a document to attach it to a project"
                : index > 0 ? "this document is attached to this project (kept when it is saved)" : "this document is attached to no project";
        }

        void Fill()
        {
            option.Clear();
            ids.Clear();
            ids.Add(null);
            option.AddItem("🎯 No project");
            foreach (var project in ProjectCatalog.Projects)
            {
                ids.Add(project.Id);
                option.AddItem("🎯 " + project.Name);
            }
            Select();
        }

        option.ItemSelected += index =>
        {
            if (index >= 0 && index < ids.Count && document.HasProjectDocument && ids[(int)index] != document.ProjectId)
                document.ProjectId = ids[(int)index];
            Select();
        };

        void OnCatalog() => Bind.OnMain(() =>
        {
            if (GodotObject.IsInstanceValid(option))
                Fill();
        });
        void OnDocument(object? sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(IProjectDocument.ProjectId) or nameof(IProjectDocument.HasProjectDocument))
                Bind.OnMain(() =>
                {
                    if (GodotObject.IsInstanceValid(option))
                        Select();
                });
        }
        ProjectCatalog.Changed += OnCatalog;
        document.PropertyChanged += OnDocument;
        Bind.OnGone(option, () =>
        {
            ProjectCatalog.Changed -= OnCatalog;
            document.PropertyChanged -= OnDocument;
        });

        Fill();
        var caption = Eve.Caption("Project");
        caption.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        hint.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        row.AddChild(caption);
        row.AddChild(option);
        row.AddChild(hint);
        var bar = new MarginContainer();
        bar.AddThemeConstantOverride("margin_left", 12);
        bar.AddThemeConstantOverride("margin_right", 12);
        bar.AddThemeConstantOverride("margin_top", 8);
        bar.AddThemeConstantOverride("margin_bottom", 0);
        bar.AddChild(row);
        frame.AddTopBar(bar);
        // A list that is still empty (the chat just started, or a project was made elsewhere) is read again.
        if (ProjectCatalog.Projects.Count == 0)
            _ = ProjectCatalog.RefreshAsync();
    }
}
