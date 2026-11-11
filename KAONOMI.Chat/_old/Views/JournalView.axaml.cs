using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KAONOMI.Chat.Services;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>
/// Journal (<c>/diary</c>, <c>/journal</c>): a Markdown editor with a formatting toolbar and its shortcuts, and the live
/// preview on the right (images resized there with their corner grip).
/// </summary>
public partial class JournalView : UserControl
{
    private JournalWindowViewModel? _viewModel;

    /// <summary>The preview is rendered again a moment after the typing pauses.</summary>
    private readonly DispatcherTimer _render = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>Initializes the editor, the shortcuts and the preview.</summary>
    public JournalView()
    {
        InitializeComponent();
        _render.Tick += (_, _) =>
        {
            _render.Stop();
            RenderPreview();
        };
        Editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        // Pictures dragged from /pictures (or the Finder) are inserted as image lines under the pointer.
        PictureDragDrop.Attach(Editor, (text, caret, count) =>
        {
            Replace(text, caret, caret);
            if (_viewModel is { } viewModel)
                viewModel.Status = $"{count} picture(s) inserted; drag the corner of an image in the preview to resize it";
        });
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.S && IsCommandKey(e) && _viewModel?.SaveCommand is { } save && save.CanExecute(null))
            {
                save.Execute(null);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        HistoryList.DoubleTapped += (_, _) => OpenSelectedEntry();
        HistoryList.SelectionChanged += (_, _) => OpenSelectedEntry();
        // 👁 shows the password while it is pressed in.
        RevealPassword.IsCheckedChanged += (_, _) => PasswordBox.PasswordChar = RevealPassword.IsChecked == true ? '\0' : '●';
        PasswordBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _viewModel?.UnlockCommand is { } unlock && unlock.CanExecute(null))
            {
                unlock.Execute(null);
                e.Handled = true;
            }
        };
        HistorySearchBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _viewModel?.SearchHistoryCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = DataContext as JournalWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            _viewModel.ConfirmAsync = (message, label) => ConfirmDialog.AskAsync((Window)TopLevel.GetTopLevel(this)!, message, label);
        }
        RenderPreview();
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Editor.Focus();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JournalWindowViewModel.Text))
        {
            _render.Stop();
            _render.Start();
        }
    }

    private void OpenSelectedEntry()
    {
        if (HistoryList.SelectedItem is JournalHistoryItem item)
            _viewModel?.OpenEntryCommand.Execute(item);
    }

    /// <summary>Renders the Markdown into the preview, keeping its scroll position.</summary>
    private void RenderPreview()
    {
        if (_viewModel is not { } viewModel)
            return;
        var offset = PreviewScroller.Offset;
        PreviewHost.Content = JournalMarkdown.Render(viewModel.Text, new JournalRenderOptions
        {
            LoadImage = viewModel.LoadImageAsync,
            OpenLink = OpenLink,
            ImageResized = (index, width) =>
            {
                viewModel.SetImageWidth(index, width);
                viewModel.Status = $"Image resized to {width} px";
            }
        });
        Dispatcher.UIThread.Post(() => PreviewScroller.Offset = offset, DispatcherPriority.Background);
    }

    /// <summary>Opens a link: a web address in the browser, a local file with its application.</summary>
    private void OpenLink(string url)
    {
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
                _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri);
            else
                PictureLibrary.OpenWithSystem(JournalMarkdown.LocalPath(url));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (_viewModel is { } viewModel)
            {
                viewModel.IsError = true;
                viewModel.Status = $"Cannot open {url}: {ex.Message}";
            }
        }
    }

    // Toolbar.

    private void OnH1Click(object? sender, RoutedEventArgs e) => Heading(1);
    private void OnH2Click(object? sender, RoutedEventArgs e) => Heading(2);
    private void OnBoldClick(object? sender, RoutedEventArgs e) => Wrap("**", "**", "bold text");
    private void OnItalicClick(object? sender, RoutedEventArgs e) => Wrap("*", "*", "italic text");
    private void OnUnderlineClick(object? sender, RoutedEventArgs e) => Wrap("<u>", "</u>", "underlined text");
    private void OnCodeClick(object? sender, RoutedEventArgs e) => Code();
    private void OnLinkClick(object? sender, RoutedEventArgs e) => Link();
    private void OnListClick(object? sender, RoutedEventArgs e) => LinePrefix("- ");
    private void OnQuoteClick(object? sender, RoutedEventArgs e) => LinePrefix("> ");
    private async void OnImageClick(object? sender, RoutedEventArgs e) => await InsertImagesAsync();

    /// <summary>Ctrl (Windows / Linux) or ⌘ (macOS).</summary>
    private static bool IsCommandKey(KeyEventArgs e) =>
        e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsCommandKey(e))
            return;
        Action? action = e.Key switch
        {
            Key.B => () => Wrap("**", "**", "bold text"),
            Key.I => () => Wrap("*", "*", "italic text"),
            Key.U => () => Wrap("<u>", "</u>", "underlined text"),
            Key.K => Link,
            Key.E => Code,
            Key.D1 => () => Heading(1),
            Key.D2 => () => Heading(2),
            _ => null
        };
        if (action is null)
            return;
        action();
        e.Handled = true;
    }

    private (int Start, int End, string Text) Selection()
    {
        var text = Editor.Text ?? string.Empty;
        var start = Math.Clamp(Math.Min(Editor.SelectionStart, Editor.SelectionEnd), 0, text.Length);
        var end = Math.Clamp(Math.Max(Editor.SelectionStart, Editor.SelectionEnd), 0, text.Length);
        return (start, end, text);
    }

    /// <summary>Puts <paramref name="before"/> and <paramref name="after"/> around the selection (or a placeholder, selected).</summary>
    private void Wrap(string before, string after, string placeholder)
    {
        var (start, end, text) = Selection();
        var inner = end > start ? text[start..end] : placeholder;
        Replace(text[..start] + before + inner + after + text[end..], start + before.Length, start + before.Length + inner.Length);
    }

    /// <summary>Inline code for a selection on one line, a code block for several lines (or an empty line).</summary>
    private void Code()
    {
        var (start, end, text) = Selection();
        var selected = text[start..end];
        var lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        var lineIsEmpty = start == end && text[lineStart..(text.IndexOf('\n', start) is var i and >= 0 ? i : text.Length)].Trim().Length == 0;
        if (selected.Contains('\n') || lineIsEmpty)
        {
            var inner = selected.Length > 0 ? selected : "code";
            var prefix = start > 0 && text[start - 1] != '\n' ? "\n" : string.Empty;
            var block = $"{prefix}```\n{inner}\n```\n";
            Replace(text[..start] + block + text[end..], start + prefix.Length + 4, start + prefix.Length + 4 + inner.Length);
        }
        else
        {
            Wrap("`", "`", "code");
        }
    }

    /// <summary>[selection](https://) with the address selected, ready to be typed.</summary>
    private void Link()
    {
        var (start, end, text) = Selection();
        var label = end > start ? text[start..end] : "link text";
        const string address = "https://";
        var markdown = $"[{label}]({address})";
        var addressStart = start + label.Length + 3;
        Replace(text[..start] + markdown + text[end..], addressStart, addressStart + address.Length);
    }

    /// <summary>Sets the heading level of the current line (the same level again removes it).</summary>
    private void Heading(int level)
    {
        var (start, _, text) = Selection();
        var lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var lineEnd = text.IndexOf('\n', start) is var i and >= 0 ? i : text.Length;
        var line = text[lineStart..lineEnd];
        var hashes = line.TakeWhile(c => c == '#').Count();
        var content = line[hashes..].TrimStart();
        var marker = new string('#', level) + " ";
        var newLine = hashes == level ? content : marker + (content.Length > 0 ? content : "Heading");
        var contentStart = lineStart + (newLine.Length - (content.Length > 0 ? content.Length : "Heading".Length));
        Replace(text[..lineStart] + newLine + text[lineEnd..], contentStart, lineStart + newLine.Length);
    }

    /// <summary>Adds <paramref name="prefix"/> to the selected lines (or the current one).</summary>
    private void LinePrefix(string prefix)
    {
        var (start, end, text) = Selection();
        var lineStart = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        var lineEnd = text.IndexOf('\n', Math.Max(start, end - 1)) is var i and >= 0 ? i : text.Length;
        var lines = text[lineStart..lineEnd].Split('\n').Select(l => l.StartsWith(prefix, StringComparison.Ordinal) ? l : prefix + l);
        var block = string.Join('\n', lines);
        Replace(text[..lineStart] + block + text[lineEnd..], lineStart + block.Length, lineStart + block.Length);
    }

    /// <summary>Picks pictures (in the Pictures folder) and inserts them as image lines with their local path.</summary>
    private async Task InsertImagesAsync()
    {
        if (_viewModel is not { } viewModel || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Insert pictures",
            AllowMultiple = true,
            SuggestedStartLocation = await storage.TryGetFolderFromPathAsync(viewModel.PicturesDirectory),
            FileTypeFilter =
            [
                new FilePickerFileType("Pictures") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.gif", "*.webp", "*.bmp", "*.heic", "*.heif", "*.tif", "*.tiff"] },
                new FilePickerFileType("All files") { Patterns = ["*"] }
            ]
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0)
            return;

        var (start, end, text) = Selection();
        var before = start > 0 && text[start - 1] != '\n' ? "\n" : string.Empty;
        var lines = string.Join("\n", paths.Select(p => JournalMarkdown.ImageMarkdown(p, JournalMarkdown.DefaultImageWidth)));
        var insert = before + lines + "\n";
        Replace(text[..start] + insert + text[end..], start + insert.Length, start + insert.Length);
        viewModel.Status = $"{paths.Count} picture(s) inserted; drag the corner of an image in the preview to resize it";
    }

    /// <summary>Puts the new text in the editor (and the view model) and selects a range.</summary>
    private void Replace(string text, int selectionStart, int selectionEnd)
    {
        Editor.Text = text;
        if (_viewModel is { } viewModel)
            viewModel.Text = text;
        Editor.Focus();
        Dispatcher.UIThread.Post(() =>
        {
            Editor.SelectionStart = Math.Clamp(selectionStart, 0, text.Length);
            Editor.SelectionEnd = Math.Clamp(selectionEnd, 0, text.Length);
        });
    }
}
