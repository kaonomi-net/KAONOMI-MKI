using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using KAONOMI.ChatGodot.Tools.Work;

namespace KAONOMI.ChatGodot.Tools.Writing;

/// <summary>
/// /journal: the entry edited as it is shown, with the editor and the ribbon of /work (<see cref="TapeEditor"/>,
/// <see cref="TapeRibbon"/>). Home: font, size, B I U S, color, clear formatting, alignment, paragraph style; Insert:
/// pictures (linked where they are, not copied), a picture generated with Fooocus, crop, link, code inserted or generated
/// by the LLM; View: zoom; the right click: correct the spelling, rewrite, evaluate, translate, cut, copy, paste. The entry
/// stays Markdown: the blocks left unchanged are written back exactly as they were read. Shortcuts: ⌘/Ctrl S saves,
/// B I U, K (link), E (inline code, or a block of code with nothing selected), 1 2 3 (headings, again: Normal). The ribbon
/// is the one of /work: Home starts with the Entry group (Previous entries, New entry, Save, Delete, big tools) and ends
/// with the Feeling group (the emoticons of the emotion of the entry); Security holds the password encrypting the entry
/// (AES-256, from 12 characters) with 👁 and Unlock (Enter). The mode of the entry (new, saved, its date) is the title
/// of the window; the status (not saved, error) is under the page. Pictures dropped from the Finder on the window in
/// front are inserted where they fall (or at the caret).
/// </summary>
[ToolView(typeof(JournalWindowViewModel), "Journal", "📓", 1250, 820)]
public sealed class JournalView : IToolView
{
    /// <summary>The font of the inline code of the tape (<c>`code`</c>).</summary>
    private const string CodeFont = "Cascadia Mono, Menlo, Consolas, monospace";

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (JournalWindowViewModel)viewModel;
        vm.ConfirmAsync = WritingUi.Confirm;

        var root = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var overlay = new Overlay();
        var editor = new TapeEditor
        {
            KeepSource = true,
            Placeholder = "Write your entry here… select text to format it (the ribbon above, or ⌘/Ctrl B, I, U)."
        };
        var transient = Eve.Eve.Text_("", Eve.Eve.Green, 11);
        var ribbon = new TapeRibbon(editor, overlay, shell, root, transient, viewModel as ITapeAssistant, "Journal", "the journal entry", "journal",
            _ => JournalWindowViewModel.JournalPicturesDirectory);

        // The entry, both ways (Markdown; the view model encrypts and decrypts, the editor only sees the clear text).
        editor.TextCommitted += text => vm.Text = text;
        Bind.When(editor, vm, () => editor.Text = vm.Text, nameof(vm.Text));
        editor.LoadImage = async path => AvaloniaBridge.ToTexture(await vm.LoadImageAsync(path));
        editor.OpenImage = path => OpenImage(vm, shell, path);

        // The entry (its mode and date) is the title of the window, as the document is in /work: nothing above the ribbon.
        void UpdateTitle()
        {
            if (!GodotObject.IsInstanceValid(root) || shell.WindowOf(root) is not { } window)
                return;
            var caption = vm.ModeLabel.Trim();
            window.Title = caption.Length == 0 ? "Journal" : $"Journal · {caption}";
        }
        Bind.When(root, vm, UpdateTitle, nameof(vm.ModeLabel));
        root.Ready += () => Callable.From(UpdateTitle).CallDeferred();

        // The Entry group of the ribbon: previous entries, new entry, save, delete (big tools, as the Document group of /work).
        var history = TapeRibbon.BigTool("🗂", "Previous", "Show / hide the previous entries (search, a click opens one)", () => vm.ShowHistory = !vm.ShowHistory);
        Bind.When(history, vm, () => TapeRibbon.SetOn(history, vm.ShowHistory), nameof(vm.ShowHistory));
        var newEntry = TapeRibbon.BigTool("📓", "New entry", "Start a new entry", () => Bind.Run(vm.NewEntryCommand));
        var save = TapeRibbon.BigTool("💾", "Save", "Save the entry (Ctrl+S / ⌘S)", () => Bind.Run(vm.SaveCommand));
        var delete = TapeRibbon.BigTool("🗑", "Delete", "Delete this entry", () => Bind.Run(vm.DeleteCommand));
        var entryGroup = TapeRibbon.Group("Entry", false, history, newEntry, save, delete);

        // The Password group (the Security tab): the password encrypting the entry, 👁, its state, Unlock.
        var password = new LineEdit
        {
            Secret = true,
            SecretCharacter = "●",
            PlaceholderText = "Password (12+ characters) to encrypt",
            CustomMinimumSize = new Vector2(240, 0),
            TooltipText = "With a password of at least 12 characters the entry is encrypted with AES-256 (key derived with PBKDF2-SHA256) before it is sent; the password is never saved: without it the entry cannot be read"
        };
        Bind.Edit(password, vm, nameof(vm.Password));
        password.TextSubmitted += _ => Bind.Run(vm.UnlockCommand);
        var forget = Eve.Eve.Text_("Don't forget your password", Eve.Eve.Amber, 10);
        forget.HorizontalAlignment = HorizontalAlignment.Center;
        var reveal = TapeRibbon.SmallTool("👁", "Show / hide the password", () => { }, toggle: true);
        reveal.Toggled += on => password.Secret = !on;
        var info = Ui.Label(vm, nameof(vm.PasswordInfo), Eve.Eve.Text, 12);
        info.CustomMinimumSize = new Vector2(200, 0);
        Bind.When(info, vm, () => info.AddThemeColorOverride("font_color", vm.IsPasswordValid ? Eve.Eve.Text : Eve.Eve.Amber), nameof(vm.IsPasswordValid), nameof(vm.PasswordInfo));
        var unlock = Bind.Visible(Ui.Command("🔓 UNLOCK", vm.UnlockCommand, primary: true, tooltip: "Decrypt this entry with the password"), vm, nameof(vm.IsLocked));
        var passwordGroup = TapeRibbon.Group("Password", true, Ui.Column(1, password, forget), reveal, info, unlock);

        // The Feeling group: the emoticons of the emotions of the entry, the one chosen outlined, its name beside them.
        var feelings = Ui.Row(2);
        foreach (var feeling in vm.Feelings)
            feelings.AddChild(FeelingButton(vm, feeling));
        var feelingLabel = Ui.Label(vm, nameof(vm.FeelingLabel), Eve.Eve.Text with { A = 0.75f }, 12);
        feelingLabel.CustomMinimumSize = new Vector2(110, 0);
        var feelingGroup = TapeRibbon.Group("Feeling", true, feelings, feelingLabel);

        // History (hidden by default).
        var search = Ui.Line(vm, nameof(vm.HistorySearch), "Search in the entries…");
        search.TextSubmitted += _ => Bind.Run(vm.SearchHistoryCommand);
        var historyList = new VBoxContainer();
        historyList.AddThemeConstantOverride("separation", 2);
        var noEntry = Ui.Placeholder("No entry");
        Bind.Items(historyList, vm.History, item => HistoryRow(vm, item), noEntry);
        var historyPanel = Ui.Column(8,
            Ui.Row(6, search, Ui.Command("⟳", vm.SearchHistoryCommand, tooltip: "Search (Enter); empty: the last entries")),
            Ui.FillHeight(Ui.Scroll(historyList)), noEntry);
        historyPanel.CustomMinimumSize = new Vector2(300, 0);
        var historyHolder = Bind.Visible(Ui.Margin(historyPanel, 10, 10, 0, 10), vm, nameof(vm.ShowHistory));

        // The page, as in /work: the ribbon fixed above (the short messages at the right of its tabs), the entry scrolling below.
        var picturesTooltip = $"Insert pictures at the caret (picked in {vm.PicturesDirectory}; linked where they are, not copied); resize them with the grip of their corner";
        var ribbonPanel = ribbon.Build(transient,
            ("Home", [entryGroup, ribbon.FontGroup(), ribbon.ParagraphGroup(), ribbon.StylesGroup(), feelingGroup]),
            ("Insert", [ribbon.PicturesGroup(picturesTooltip, () => _ = InsertPicturesAsync(vm, editor),
                $"Generate an image with Fooocus from a prompt, then insert it at the caret (saved in {JournalWindowViewModel.JournalPicturesDirectory})"),
                ribbon.LinksGroup(), ribbon.CodeGroup()]),
            ("Security", [passwordGroup]),
            ("View", [ribbon.ZoomGroup()]));
        var scroll = Ui.Scroll(editor);
        scroll.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var page = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        page.AddThemeStyleboxOverride("panel", Eve.Eve.Box(WorkPalette.Page, Eve.Eve.Edge with { A = 0.45f }, 0));
        page.AddChild(Ui.Column(0, ribbonPanel, scroll));

        // The locked notice replaces the page until the entry is unlocked (the editor never sees the ciphertext).
        var locked = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        locked.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.3f), Eve.Eve.Amber with { A = 0.4f }, 12));
        var lockIcon = Eve.Eve.Text_("🔒", null, 56);
        var lockTitle = Eve.Eve.Caption("This entry is encrypted (AES-256)", Eve.Eve.TextBright, 15);
        var lockHint = Eve.Eve.Text_("Type its password above and press Unlock", Eve.Eve.Muted, 13);
        var lockColumn = Ui.Column(8, lockIcon, lockTitle, lockHint);
        foreach (var label in new[] { lockIcon, lockTitle, lockHint })
        {
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            label.CustomMinimumSize = new Vector2(120, 0);
        }
        lockColumn.Alignment = BoxContainer.AlignmentMode.Center;
        locked.AddChild(lockColumn);
        Bind.Visible(locked, vm, nameof(vm.IsLocked));
        Bind.Visible(page, vm, nameof(vm.IsLocked), invert: true);
        var pageArea = Ui.Margin(Ui.Column(0, page, locked), 10, 10, 10, 10);
        pageArea.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var body = Ui.Row(0, historyHolder, pageArea);
        body.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        // Status: error dot, not saved, the text (the short messages of the ribbon are at the right of its tabs).
        var status = Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError));
        if (status.GetChild(0) is HBoxContainer statusRow)
        {
            var dirty = Bind.Visible(Eve.Eve.Text_("● not saved", Eve.Eve.Amber, 12), vm, nameof(vm.IsDirty));
            statusRow.AddChild(dirty);
            statusRow.MoveChild(dirty, statusRow.GetChildCount() - 2);
        }

        var content = Ui.Column(0, body, status);
        content.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        root.AddChild(content);
        root.AddChild(overlay);

        // Shortcuts of the journal (the editor has B, I, U, Ctrl / ⌘ + Alt + 0…3 itself).
        WritingUi.Shortcuts(root, key =>
        {
            if (WritingUi.IsCommand(key, Key.S))
                return WritingUi.Run(vm.SaveCommand);
            if (vm.IsLocked || !key.IsCommandOrControlPressed() || key.ShiftPressed || key.AltPressed || !editor.HasFocus())
                return false;
            switch (key.Keycode)
            {
                case Key.K:
                    ribbon.ShowLinkFlyout();
                    return true;
                case Key.E:
                    InlineCode(editor, ribbon);
                    return true;
                case Key.Key1 or Key.Key2 or Key.Key3:
                    var kind = key.Keycode switch
                    {
                        Key.Key1 => TapeParagraphKind.Heading1,
                        Key.Key2 => TapeParagraphKind.Heading2,
                        _ => TapeParagraphKind.Heading3
                    };
                    editor.SetParagraphKind(editor.CurrentParagraphKind == kind ? TapeParagraphKind.Normal : kind);
                    ribbon.UpdateToolbar();
                    return true;
                default:
                    return false;
            }
        });

        // Pictures dropped from the Finder on the window of the journal (in front) are inserted where they fall.
        var window = shell.Root.GetTree().Root;
        void OnFilesDropped(string[] files)
        {
            if (!GodotObject.IsInstanceValid(root) || shell.WindowOf(root) is not { IsFront: true } || vm.IsLocked)
                return;
            var pictures = files.Where(PictureLibrary.IsPicture).ToList();
            if (pictures.Count == 0)
                return;
            var mouse = editor.GetLocalMousePosition();
            if (new Rect2(Vector2.Zero, editor.Size).HasPoint(mouse))
                editor.InsertImagesAt(pictures, mouse);
            else
                editor.InsertImages(pictures);
            vm.Status = $"{pictures.Count} picture(s) inserted; drag the corner of a picture to resize it";
        }
        window.FilesDropped += OnFilesDropped;
        Bind.OnGone(root, () => window.FilesDropped -= OnFilesDropped);

        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(editor) && editor.IsVisibleInTree())
                editor.GrabFocus();
        }).CallDeferred();
        return root;
    }

    /// <summary>⌘/Ctrl E: the selection (in one paragraph) in inline code, or out of it; nothing selected: the window of a block of code.</summary>
    private static void InlineCode(TapeEditor editor, TapeRibbon ribbon)
    {
        if (!editor.HasSelectedContent || editor.SelectedText.Contains('\n'))
        {
            ribbon.InsertCode();
            return;
        }
        var isCode = editor.CurrentStyle.Font == CodeFont;
        editor.ApplyStyle(s => isCode ? s with { Font = null } : s with { Font = CodeFont, Size = null, Color = null });
        ribbon.UpdateToolbar();
    }

    /// <summary>🖼: pictures picked in the Pictures folder, inserted at the caret with their path (not copied).</summary>
    private static async Task InsertPicturesAsync(JournalWindowViewModel vm, TapeEditor editor)
    {
        if (vm.IsLocked)
            return;
        var files = await Dialogs.OpenFiles("Insert pictures",
            ["*.jpg,*.jpeg,*.png,*.gif,*.webp,*.bmp,*.heic,*.heif,*.tif,*.tiff ; Pictures", "* ; All files"], vm.PicturesDirectory);
        if (files.Length == 0 || !GodotObject.IsInstanceValid(editor))
            return;
        editor.InsertImages(files);
        vm.Status = $"{files.Length} picture(s) inserted; drag the corner of a picture to resize it";
    }

    /// <summary>A double-click on a picture: the image analysis window of /pictures (or the viewer of the system).</summary>
    private static void OpenImage(JournalWindowViewModel vm, Shell shell, string path)
    {
        if (vm.CreateAnalysis is not { } create || !System.IO.File.Exists(path) || !Shell.HasView(typeof(ImageAnalysisViewModel)))
        {
            PictureLibrary.OpenWithSystem(path);
            return;
        }
        shell.Open(create(path));
    }

    /// <summary>A round emoticon of an emotion, outlined in cyan when it is the one of the entry (again: cleared).</summary>
    private static Button FeelingButton(JournalWindowViewModel vm, JournalFeeling feeling)
    {
        var button = new Button { Text = feeling.Emoji, TooltipText = feeling.Name, CustomMinimumSize = new Vector2(36, 36), FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand };
        button.AddThemeFontSizeOverride("font_size", 19);
        StyleBoxFlat Round(Color back, Color edge)
        {
            var box = Eve.Eve.Box(back, edge, 0, 2);
            box.SetCornerRadiusAll(18);
            box.AntiAliasing = true;
            return box;
        }
        Bind.When(button, feeling, () =>
        {
            var edge = feeling.IsSelected ? Eve.Eve.Cyan : new Color(0, 0, 0, 0);
            button.AddThemeStyleboxOverride("normal", Round(new Color(0, 0, 0, 0), edge));
            button.AddThemeStyleboxOverride("hover", Round(Eve.Eve.Cyan with { A = 0.12f }, feeling.IsSelected ? Eve.Eve.Cyan : Eve.Eve.Edge));
            button.AddThemeStyleboxOverride("pressed", Round(Eve.Eve.Cyan with { A = 0.25f }, Eve.Eve.Cyan));
            button.Modulate = new Color(1, 1, 1, feeling.IsSelected ? 1f : 0.55f);
        }, nameof(feeling.IsSelected));
        button.MouseEntered += () => button.Modulate = Colors.White;
        button.MouseExited += () => button.Modulate = new Color(1, 1, 1, feeling.IsSelected ? 1f : 0.55f);
        Bind.Command(button, vm.ChooseFeelingCommand, () => feeling);
        return button;
    }

    /// <summary>A previous entry: its date, emotion and first words; a click opens it.</summary>
    private static Control HistoryRow(JournalWindowViewModel vm, JournalHistoryItem item)
    {
        var date = Eve.Eve.Text_(item.DateLabel, Eve.Eve.TextBright, 11);
        var preview = WritingUi.Clamp(Eve.Eve.Text_(item.Preview, Eve.Eve.Text with { A = 0.75f }, 12), 3);
        var content = Ui.Column(2, Ui.Row(6, date, Eve.Eve.Text_(item.Feeling, Eve.Eve.Text, 11)), preview);
        var button = WritingUi.ResultRow(vm, nameof(vm.EntryId), () => vm.EntryId is not null && vm.EntryId == item.Entry.Id,
            () => Bind.Run(vm.OpenEntryCommand, item), content, 52);
        button.TooltipText = item.Preview;
        return button;
    }
}
