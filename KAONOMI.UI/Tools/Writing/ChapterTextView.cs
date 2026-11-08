using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;
using KAONOMI.UI.Tools.Work;

namespace KAONOMI.UI.Tools.Writing;

/// <summary>
/// The window where a chapter of /write is written (a double-click on the chapter, or ✍ WRITE): a window of the desktop like
/// the others, one per chapter, with the editor and the ribbon of /work (as the journal and the wiki): Home (Chapter: Save, AI-Check, Footnote, Done;
/// font, paragraph, styles), Insert (pictures, copied into Pictures/kaonomi/write; links; code), View (zoom);
/// the right click corrects, rewrites, evaluates or translates with the model of the chat. Over the ribbon: the position and
/// title of the chapter, the 🎤 of the dictation, the count of its words; under the page its footnotes. The text is kept in
/// the chapter (Markdown) after each change; ⌘/Ctrl S saves the writing, ⌘/Ctrl K makes a link.
/// </summary>
[ToolView(typeof(ChapterTextWindowViewModel), "Chapter", "✍", 1000, 720)]
public sealed class ChapterTextView : IToolView
{
    /// <summary>The title of the window of <paramref name="chapter"/>.</summary>
    public static string TitleOf(WriterChapter chapter) => $"✍ {chapter.Position}. {chapter.Title}";

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (ChapterTextWindowViewModel)viewModel;
        var chapter = vm.Chapter;

        var tape = new TapePage(shell, vm, "GhostWriter", "the chapter", "write", _ => GhostWriterWindowViewModel.PicturesDirectory,
            "Write the chapter here… select text to format it (the ribbon above, or ⌘/Ctrl B, I, U) or put a footnote on it (¹); right click: correct, rewrite, evaluate, translate.",
            linksOpenOnClick: true);
        var editor = tape.Editor;
        var ribbon = tape.Ribbon;
        // The text of the chapter, both ways (Markdown).
        tape.Bind(chapter, nameof(chapter.Text));
        editor.LoadImage = vm.LoadImageAsync;
        editor.OpenImage = PictureLibrary.OpenWithSystem;
        // A click on the words of a footnote opens it; any other link opens as in /work (⌥/Alt + click edits a link).
        editor.OpenLink = link =>
        {
            if (link.StartsWith(WriterChapter.NoteScheme, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(link[WriterChapter.NoteScheme.Length..], out var number) && vm.Footnote(number) is { } note)
                EditFootnote(vm, editor, ribbon, note);
            else
                ribbon.OpenLink(link);
        };

        // ── The head: the chapter, the 🎤, its words ──
        var title = Ui.Label(string.Empty, Ui.TextBright, 16, bold: true);
        var words = Ui.Label(string.Empty, Ui.Muted, 11);
        // The 🎤: the words dictated are typed at the caret of the chapter as they are said, a space after them.
        var mic = GhostWriterView.Mic(vm.Writing, Dictation(editor));
        var right = Ui.Row(8, mic, words);
        var head = Ui.Dock((right, Dock.Right), (title, Dock.Left));
        head.Margin = new Thickness(0, 0, 0, 8);

        Control? root = null;
        var save = TapeRibbon.BigTool("💾", "Save", "Save the writing (every chapter) in the search engine · ⌘/Ctrl S", () => Ui.Run(vm.Writing.SaveToServerCommand));
        var done = TapeRibbon.BigTool("✓", "Done", "Close this window (the text is kept in the chapter)", () => WritingUi.CloseWindowOf(shell, root!));
        var check = TapeRibbon.BigTool("🧪", "AI-Check", "Does the text match the description, the perception goal and the ambiance of the chapter? The model of the chat says it, then offers to rewrite it",
            () => _ = AiCheckAsync(vm, shell, editor, ribbon));
        var footnote = TapeRibbon.BigTool("¹", "Footnote", "A footnote on the selected words: its text and links (a click on the words opens it again)",
            () => AddFootnote(vm, editor, ribbon));
        var chapterGroup = TapeRibbon.Group("Chapter", false, save, check, footnote, done);
        var pageArea = tape.Build(
            ("Home", [chapterGroup, ribbon.FontGroup(), ribbon.ParagraphGroup(), ribbon.StylesGroup(last: true)]),
            ("Insert", [ribbon.PicturesGroup($"Insert pictures at the caret (copied into {GhostWriterWindowViewModel.PicturesDirectory}); resize them with the grip of their corner",
                () => _ = InsertPicturesAsync(vm, editor),
                $"Generate an image with Fooocus from a prompt, then insert it at the caret (saved in {GhostWriterWindowViewModel.PicturesDirectory})"),
                ribbon.LinksGroup(), ribbon.CodeGroup()]),
            ("View", [ribbon.ZoomGroup()]));

        // The footnotes under the page (hidden while there is none): a click on one edits it.
        var notes = GhostWriterView.FootnotesOf(chapter, () => { }, ribbon.OpenLink, note => EditFootnote(vm, editor, ribbon, note));
        var notesScroll = new ScrollViewer { Content = WritingUi.Margin(notes, 12, 0, 12, 8), MaxHeight = 140, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        notesScroll.Bind(Visual.IsVisibleProperty, notes, nameof(Visual.IsVisible));
        var page = WritingUi.Page([], pageArea, notesScroll);

        root = WritingUi.Margin(WritingUi.Page([head], page), 10);

        void Refresh()
        {
            title.Text = $"{chapter.Position}. {chapter.Title}";
            words.Text = $"{GhostWriterWindowViewModel.Words(chapter.Text)} word(s)";
            WritingUi.SetTitle(root, TitleOf(chapter));
        }
        Ui.When(root, chapter, Refresh, nameof(chapter.Title), nameof(chapter.Position), nameof(chapter.Text));
        WritingUi.WhenInWindow(root, Refresh);

        // Shortcuts: ⌘/Ctrl S saves the writing, ⌘/Ctrl K makes a link.
        WritingUi.Shortcuts(root, key =>
        {
            if (WritingUi.IsCommand(key, Key.S))
                return WritingUi.Run(vm.Writing.SaveToServerCommand);
            if (WritingUi.IsCommand(key, Key.K) && editor.IsFocused)
            {
                ribbon.ShowLinkFlyout();
                return true;
            }
            return false;
        });

        // Pictures dropped from the file manager: copied into Pictures/kaonomi/write, then inserted where they fall.
        DragDrop.SetAllowDrop(root, true);
        root.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var copies = vm.CopyPictures(WritingFiles.Dropped(e).Where(PictureLibrary.IsPicture));
            if (copies.Count == 0)
                return;
            tape.InsertDropped(copies, e);
            vm.Writing.SetStatus($"{copies.Count} picture(s) copied into {GhostWriterWindowViewModel.PicturesDirectory} and inserted", false);
            e.Handled = true;
        });
        WritingUi.FocusWhenShown(editor);
        return root;
    }

    /// <summary>
    /// The dictation into the page of a chapter: the words said so far typed at the caret, a space after them, taken back
    /// (undone) when the next ones come, then the final text.
    /// </summary>
    private static Action<string, bool> Dictation(TapeEditor editor)
    {
        var shown = false;
        return (text, final) => Ui.OnMain(() =>
        {
            if (shown)
                editor.Undo();
            shown = false;
            if (text.Length == 0)
                return;
            editor.InsertPlainText(text + " ");
            shown = !final;
        });
    }

    // ── Footnotes ──

    /// <summary>¹ Footnote: a footnote on the selected words (one paragraph); saved, the words link to it (note:&lt;number&gt;).</summary>
    private static void AddFootnote(ChapterTextWindowViewModel vm, TapeEditor editor, TapeRibbon ribbon)
    {
        var words = editor.SelectedText.Trim();
        if (words.Length == 0 || words.Contains('\n'))
        {
            ribbon.ShowStatus("Select the words the footnote is about first (in one paragraph)", WritingUi.Amber);
            return;
        }
        var note = vm.NewFootnote(words);
        _ = FootnoteDialog(note, $"¹ New footnote {note.Number}", () =>
        {
            vm.AddFootnote(note);
            editor.SetLink(WriterChapter.NoteLink(note.Number));
            ribbon.UpdateToolbar();
            ribbon.ShowStatus($"Footnote {note.Number} added on « {words} » (a click on the words opens it)");
        }, null);
    }

    /// <summary>Opens footnote <paramref name="note"/>: SAVE keeps its changes, DELETE removes it and the link of its words.</summary>
    private static void EditFootnote(ChapterTextWindowViewModel vm, TapeEditor editor, TapeRibbon ribbon, WriterFootnote note) =>
        _ = FootnoteDialog(note, $"¹ Footnote {note.Number}", () => vm.Writing.MarkDirty(), () =>
        {
            editor.ReplaceAll(vm.RemoveFootnote(note, editor.Text));
            ribbon.ShowStatus($"Footnote {note.Number} removed (its words stay, without the link)");
        });

    /// <summary>
    /// The dialog of a footnote: the words it is on, its text, its links (a title and an address each, ✚ ADD LINK, ✕); SAVE
    /// writes them in <paramref name="note"/> then calls <paramref name="saved"/>; DELETE (when <paramref name="deleted"/> is
    /// given) calls it; CANCEL changes nothing.
    /// </summary>
    private static async Task FootnoteDialog(WriterFootnote note, string title, Action saved, Action? deleted)
    {
        var dialog = new Window
        {
            Title = title.ToUpperInvariant(),
            Width = 660,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var anchor = Ui.Label($"On « {note.Anchor} »", WritingUi.Amber, 12, wrap: true);
        var text = new TextBox
        {
            Text = note.Text,
            PlaceholderText = "The text of the footnote…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 150,
            MaxHeight = 320,
            VerticalContentAlignment = VerticalAlignment.Top
        };

        // The links, edited on copies: CANCEL leaves the footnote as it was.
        var rows = new StackPanel { Spacing = 4 };
        var links = note.Links.Select(l => new WriterFootnoteLink { Title = l.Title, Url = l.Url }).ToList();
        void AddRow(WriterFootnoteLink link)
        {
            var name = new TextBox { Text = link.Title, PlaceholderText = "Title", Width = 200, Margin = new Thickness(0, 0, 6, 0) };
            var address = new TextBox { Text = link.Url, PlaceholderText = "https://…" };
            name.TextChanged += (_, _) => link.Title = name.Text ?? string.Empty;
            address.TextChanged += (_, _) => link.Url = address.Text ?? string.Empty;
            DockPanel? row = null;
            var remove = Ui.Button("✕", () =>
            {
                links.Remove(link);
                rows.Children.Remove(row!);
            }, "Remove this link");
            remove.Margin = new Thickness(6, 0, 0, 0);
            row = Ui.Dock((name, Dock.Left), (remove, Dock.Right), (address, Dock.Left));
            rows.Children.Add(row);
        }
        foreach (var link in links)
            AddRow(link);
        var addLink = Ui.Button("✚ ADD LINK", () =>
        {
            var link = new WriterFootnoteLink();
            links.Add(link);
            AddRow(link);
        }, "A link of the footnote: a title and an address (a web page, a file…)");
        addLink.HorizontalAlignment = HorizontalAlignment.Left;

        var result = "cancel";
        var save = Ui.Button("SAVE", () => { result = "save"; dialog.Close(); }, primary: true);
        var cancel = Ui.Button("CANCEL", dialog.Close);
        cancel.IsCancel = true;
        var buttons = Ui.Row(8, save, cancel);
        var footer = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        if (deleted is not null)
            footer.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Left, Child = Ui.Button("DELETE", () => { result = "delete"; dialog.Close(); }, "Remove this footnote and the link of its words") });
        footer.Margin = new Thickness(0, 6, 0, 0);

        dialog.Content = new Border
        {
            Padding = new Thickness(18),
            Child = Ui.Column(8, anchor, WritingUi.Field("Note", text), Ui.Caption("Links", Ui.Accent, 11), rows, addLink, footer)
        };
        dialog.Opened += (_, _) => text.Focus();
        if (Dialogs.Owner is { IsVisible: true } owner)
            await dialog.ShowDialog(owner);
        else
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            await closed.Task;
        }

        switch (result)
        {
            case "save":
                note.Text = (text.Text ?? string.Empty).Trim();
                note.Links = links.Where(l => l.Url.Trim().Length > 0)
                    .Select(l => new WriterFootnoteLink { Title = l.Title.Trim(), Url = WritingUi.NormalizeLink(l.Url) ?? l.Url.Trim() })
                    .ToList();
                saved();
                break;
            case "delete":
                deleted?.Invoke();
                break;
        }
    }

    // ── AI ──

    /// <summary>
    /// 🧪 AI-Check: the model of the chat compares the text with the description, the perception goal and the ambiance of the
    /// chapter (the waiting dialog, Cancel); its analysis opens in a window that offers ✍ REWRITE IT.
    /// </summary>
    private static async Task AiCheckAsync(ChapterTextWindowViewModel vm, Shell shell, TapeEditor editor, TapeRibbon ribbon)
    {
        var text = vm.Chapter.Text.Trim();
        if (text.Length == 0)
        {
            ribbon.ShowStatus("Write the chapter first: the AI-Check reads its text", WritingUi.Amber);
            return;
        }
        if (!vm.CanEvaluate)
        {
            ribbon.ShowStatus("No LLM available for the AI-Check", Ui.Red);
            return;
        }
        var model = vm.CurrentModelName ?? "LLM";
        var (analysis, error) = await AssistWindows.AskAsync("AI-Check", $"{model} compares the chapter with its description, perception goal and ambiance…",
            vm.CheckAsync, "AI-Check", "GhostWriter");
        if (analysis is null && error is null)
            return;
        AssistWindows.Evaluation(shell, text, model, analysis, error, "🧪 AI-Check",
            $"Does the chapter match its description, perception goal and ambiance? · {model}",
            "✍ REWRITE IT", () => _ = RewriteAsync(vm, shell, editor, analysis!));
    }

    /// <summary>
    /// ✍ REWRITE IT: the chapter rewritten to match its intent and the goals and audience of the writing (General); the result
    /// opens in a window where it can be changed, then replace the chapter or go below it.
    /// </summary>
    private static async Task RewriteAsync(ChapterTextWindowViewModel vm, Shell shell, TapeEditor editor, string analysis)
    {
        var text = vm.Chapter.Text;
        var model = vm.CurrentModelName ?? "LLM";
        var (rewritten, error) = await AssistWindows.AskAsync("Rewriting",
            $"{model} rewrites the chapter for its description, perception goal and ambiance, the goals and the audience of the writing…",
            token => vm.RewriteToFitAsync(analysis, token), "AI rewriting", "GhostWriter");
        if (rewritten is null && error is null)
            return;
        AssistWindows.Translation(shell, text, "", model, rewritten, error,
            result =>
            {
                editor.ReplaceAll(result);
                return true;
            },
            result =>
            {
                editor.ReplaceAll(editor.Text.TrimEnd() + "\n\n" + result);
                return true;
            },
            $"✍ Rewritten · {vm.Chapter.Position}. {vm.Chapter.Title}", "Rewriting", "REPLACE THE CHAPTER", "ADD BELOW");
    }

    /// <summary>🖼: pictures picked, copied into Pictures/kaonomi/write, inserted at the caret.</summary>
    private static async Task InsertPicturesAsync(ChapterTextWindowViewModel vm, TapeEditor editor)
    {
        var files = await Dialogs.OpenFiles("Insert pictures", WritingUi.PictureFilters, ChatSettings.SystemPicturesDirectory);
        if (files.Length == 0)
            return;
        var copies = vm.CopyPictures(files);
        if (copies.Count == 0)
            return;
        editor.InsertImages(copies);
        vm.Writing.SetStatus($"{copies.Count} picture(s) copied into {GhostWriterWindowViewModel.PicturesDirectory} and inserted", false);
    }
}
