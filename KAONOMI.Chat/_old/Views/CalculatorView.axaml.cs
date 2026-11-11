using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>Calculator (<c>/calculator</c>, <c>/ncalculator</c>): keys on screen and the keyboard.</summary>
public partial class CalculatorView : UserControl
{
    /// <summary>Initializes the view; the keyboard is read before the focused control handles it.</summary>
    public CalculatorView()
    {
        InitializeComponent();
        AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Graph.RangeChanged += () => GraphRange.Text = Graph.RangeText;
        _tapeRender.Tick += (_, _) =>
        {
            _tapeRender.Stop();
            RenderTape();
        };
        // In Edit mode, pictures dragged from /pictures (or the Finder) are inserted as image lines under the pointer.
        PictureDragDrop.Attach(TapeBox, (text, caret, _) =>
        {
            TapeBox.Text = text;
            if (ViewModel is { } viewModel)
                viewModel.Tape = text;
            TapeBox.Focus();
            Avalonia.Threading.Dispatcher.UIThread.Post(() => TapeBox.CaretIndex = System.Math.Clamp(caret, 0, TapeBox.Text?.Length ?? 0));
        });
        // A new calculation on the tape: scrolled to the end so that it shows.
        TapeBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && !TapeBox.IsFocused)
                TapeBox.CaretIndex = TapeBox.Text?.Length ?? 0;
        };
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_watched is not null)
            _watched.PropertyChanged -= OnViewModelChanged;
        _watched = ViewModel;
        if (_watched is not null)
            _watched.PropertyChanged += OnViewModelChanged;
        RenderTape();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => TapeBox.CaretIndex = TapeBox.Text?.Length ?? 0);
    }

    /// <summary>
    /// True while a text box (the tape, the functions of the graph) or the graph has the focus: the keys then go there,
    /// not to the calculator.
    /// </summary>
    private bool IsEditingTape => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox or GraphView;

    /// <summary>Brush of a curve color (legend of the graph).</summary>
    public static readonly Avalonia.Data.Converters.IValueConverter ColorBrush =
        new Avalonia.Data.Converters.FuncValueConverter<Avalonia.Media.Color, Avalonia.Media.IBrush>(c => new Avalonia.Media.SolidColorBrush(c));

    private void OnGraphZoomIn(object? sender, RoutedEventArgs e) => Graph.Zoom(0.8);
    private void OnGraphZoomOut(object? sender, RoutedEventArgs e) => Graph.Zoom(1.25);
    private void OnGraphZoomXIn(object? sender, RoutedEventArgs e) => Graph.Zoom(0.8, y: false);
    private void OnGraphZoomXOut(object? sender, RoutedEventArgs e) => Graph.Zoom(1.25, y: false);
    private void OnGraphZoomYIn(object? sender, RoutedEventArgs e) => Graph.Zoom(0.8, x: false);
    private void OnGraphZoomYOut(object? sender, RoutedEventArgs e) => Graph.Zoom(1.25, x: false);
    private void OnGraphLeft(object? sender, RoutedEventArgs e) => Graph.Pan(-0.15, 0);
    private void OnGraphRight(object? sender, RoutedEventArgs e) => Graph.Pan(0.15, 0);
    private void OnGraphUp(object? sender, RoutedEventArgs e) => Graph.Pan(0, 0.15);
    private void OnGraphDown(object? sender, RoutedEventArgs e) => Graph.Pan(0, -0.15);
    private void OnGraphReset(object? sender, RoutedEventArgs e) => Graph.ResetView();

    /// <summary>📷: saves a picture of the graph as PNG (calculator.graphDirectory of config.yml) and adds it to the tape.</summary>
    private void OnGraphCamera(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel)
            return;
        try
        {
            var directory = viewModel.GraphPicturesDirectory;
            System.IO.Directory.CreateDirectory(directory);
            var path = System.IO.Path.Combine(directory, $"graph-{System.DateTime.Now:yyyyMMdd-HHmmss}.png");
            using (var picture = Graph.Snapshot(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1))
                picture.Save(path);
            viewModel.AddGraphToTape(path, Graph.RangeText);
            CameraStatus.Text = "✓ Added to the tape";
            Avalonia.Threading.DispatcherTimer.RunOnce(() => CameraStatus.Text = string.Empty, System.TimeSpan.FromSeconds(3));
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException)
        {
            CameraStatus.Text = $"Not saved: {ex.Message}";
        }
    }

    /// <summary>Exports the tape, its pictures embedded, to a Word document chosen in the save dialog.</summary>
    private async void OnExportWord(object? sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } viewModel || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var file = await storage.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Export the tape to Word",
            SuggestedFileName = $"kaonomi-tape-{System.DateTime.Now:yyyyMMdd-HHmmss}.docx",
            DefaultExtension = "docx",
            ShowOverwritePrompt = true,
            FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("Word document") { Patterns = ["*.docx"] }]
        });
        if (file is null)
            return;
        try
        {
            int pictures;
            await using (var stream = await file.OpenWriteAsync())
                pictures = await KAONOMI.Chat.Services.TapeWordExport.WriteAsync(stream, viewModel.Tape, LoadPngAsync);
            TapeStatus.Text = $"✓ Exported to {file.Name}" + (pictures > 0 ? $" ({pictures} picture(s))" : string.Empty);
        }
        catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException)
        {
            TapeStatus.Text = $"Not exported: {ex.Message}";
        }
        Avalonia.Threading.DispatcherTimer.RunOnce(() => TapeStatus.Text = string.Empty, System.TimeSpan.FromSeconds(5));
    }

    /// <summary>A picture of the tape as PNG for Word (any format the picture library reads, at most 1600 pixels wide).</summary>
    private static async System.Threading.Tasks.Task<KAONOMI.Chat.Services.TapeWordExport.Picture?> LoadPngAsync(string path)
    {
        using var bitmap = await KAONOMI.Chat.Services.PictureLibrary.DecodeAsync(path, 1600, System.Threading.CancellationToken.None);
        if (bitmap is null)
            return null;
        using var png = new System.IO.MemoryStream();
        bitmap.Save(png);
        return new(png.ToArray(), bitmap.PixelSize.Width, bitmap.PixelSize.Height);
    }

    /// <summary>The rendering of the tape is rebuilt a moment after it changes.</summary>
    private readonly Avalonia.Threading.DispatcherTimer _tapeRender = new() { Interval = System.TimeSpan.FromMilliseconds(200) };

    /// <summary>Renders the tape (its lines and pictures) and scrolls to its end, the latest calculation.</summary>
    private void RenderTape()
    {
        if (ViewModel is not { } viewModel)
            return;
        TapeRendered.Content = viewModel.Tape.Length == 0
            ? new TextBlock { Text = "The calculations done with = are written here, and kept", Opacity = 0.5, FontSize = 13 }
            : JournalMarkdown.Render(viewModel.Tape, new JournalRenderOptions
            {
                LoadImage = viewModel.LoadTapeImageAsync,
                OpenLink = path => KAONOMI.Chat.Services.PictureLibrary.OpenWithSystem(JournalMarkdown.LocalPath(path)),
                ImageResized = viewModel.SetTapeImageWidth
            });
        Avalonia.Threading.Dispatcher.UIThread.Post(() => TapeScroller.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Background);
    }

    private CalculatorViewModel? _watched;

    /// <summary>The tape is rendered again when it changes, or when its editing ends.</summary>
    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CalculatorViewModel.Tape) or nameof(CalculatorViewModel.IsTapeEditing) && ViewModel is { IsTapeEditing: false })
        {
            _tapeRender.Stop();
            _tapeRender.Start();
        }
    }

    private CalculatorViewModel? ViewModel => DataContext as CalculatorViewModel;

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Focus();
        // Taking the focus scrolls the calculator into view; it starts at its top left corner instead.
        Avalonia.Threading.Dispatcher.UIThread.Post(() => Scroller.Offset = default, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// A click anywhere in the calculator (its keys included) gives the keyboard back to it; a click in the tape edits
    /// the tape.
    /// </summary>
    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Avalonia.Visual source && (source is TextBox || source.FindAncestorOfType<TextBox>() is not null
                                                   || source == Graph || Graph.IsVisualAncestorOf(source)))
            return;
        Focus();
    }

    /// <summary>Typed characters: digits, separators, operators, parentheses, letters of the functions (x and : also multiply and divide).</summary>
    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (ViewModel is not { } viewModel || string.IsNullOrEmpty(e.Text) || IsEditingTape)
            return;

        string? key = e.Text switch
        {
            "*" or "x" or "X" => "×",
            "/" or ":" => "÷",
            "-" => "−",
            "," => ".",
            "=" => "=",
            var text when text.Length == 1 && (char.IsDigit(text[0]) || "+.()%^!π√".Contains(text[0])) => text,
            // Letters type the functions and constants (sin, log, pi, e, Ans, mod…); x is the multiplication.
            var text when text.Length == 1 && char.IsLetter(text[0]) => text,
            _ => null
        };
        if (key is null)
            return;
        e.Handled = true;
        viewModel.PressCommand.Execute(key);
    }

    /// <summary>Enter = result, Backspace = last character, Escape / Delete = clear, Ctrl+C / ⌘C = copy the result.</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } viewModel || IsEditingTape)
            return;

        if (e.Key == Key.C && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)))
        {
            e.Handled = true;
            CopyResult();
            return;
        }

        string? key = e.Key switch
        {
            Key.Enter => "=",
            Key.Back => "⌫",
            Key.Escape or Key.Delete => "C",
            _ => null
        };
        if (key is null)
            return;
        e.Handled = true;
        viewModel.PressCommand.Execute(key);
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e) => CopyResult();

    /// <summary>Copies the result shown to the clipboard.</summary>
    private async void CopyResult()
    {
        if (ViewModel is { IsError: false } viewModel && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(viewModel.Result);
    }
}
