using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>An annotation of the picture: its rectangle (shares of the original picture) and its comment.</summary>
public partial class ImageAnnotationViewModel : ObservableObject
{
    public ImageAnnotationViewModel(ImageAnnotationItem item, int number)
    {
        Item = item;
        _number = number;
        _comment = item.Comment;
    }

    public ImageAnnotationItem Item { get; }

    /// <summary>Number shown on the rectangle and in the list.</summary>
    [ObservableProperty]
    private int _number;

    [ObservableProperty]
    private string _comment;

    partial void OnCommentChanged(string value) => Item.Comment = value;

    public Rect Rect => new(Item.X, Item.Y, Item.Width, Item.Height);
}

/// <summary>
/// Analysis of one picture (double-click in <c>/pictures</c>): the picture shown with its brightness, contrast, filters
/// (monochrome, binarization, edges, invert), orientation and zoom; rectangles that annotate parts of it, each with a
/// comment; its metadata; the comments (the previous ones shown, never removed, a new one added at each save); the
/// description written by the vision model of Ollama (<c>visionmodel</c> in config.yml), where it could have been taken included;
/// the place it was taken. Save stores all of it, with every action of the analysis and the link to the picture, in the
/// <c>imageanalysis</c> index of the Kaonomi server (<c>PUT /imageanalysis</c>); the analysis is read again at the next
/// opening of the picture.
/// </summary>
public partial class ImageAnalysisViewModel : ViewModelBase, INoteContextProvider
{
    /// <inheritdoc />
    public string NoteWindowName => "Image analysis";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        imagePath = ImagePath,
        title = ShownTitle,
        analysisId = _entry.Id ?? ImageAnalysisEntry.IdFor(ImagePath),
        brightness = Brightness,
        contrast = Contrast,
        filters = ActiveFilters().Select(f => f.ToString()).ToList(),
        rotation = Rotation,
        annotations = Annotations.Select(a => new { a.Number, a.Comment, a.Item.ImagePartPath }).ToList(),
        latitude = Latitude,
        longitude = Longitude,
        locationName = LocationName,
        aiAnalysis = AiAnalysis
    };

    /// <summary>Vision model used by Analyse (the installed one whose name starts with it).</summary>
    public string VisionModel { get; }

    /// <summary>Widest picture processed (larger ones are reduced for the view; the file is not changed).</summary>
    private const int MaxWorkingSize = 2400;

    /// <summary>Longest side of the picture sent to the vision model.</summary>
    private const int MaxAiSize = 1600;

    private const string AiPrompt = """
        Analyse cette image et décris-la de la façon la plus détaillée possible, en français :
        1. Ce qu'elle montre : le sujet, les personnes (attitude, vêtements, sans les identifier), les objets, les animaux, le texte lisible, les couleurs, la lumière, le moment de la journée et la saison probables.
        2. Le type d'image (photo, capture d'écran, dessin, document…), le cadrage, la qualité, les retouches éventuelles.
        3. Où cette image aurait pu être prise : le pays, la région ou la ville, le type de lieu (intérieur, extérieur, urbain, rural, montagne, bord de mer…). Donne les indices qui le suggèrent (architecture, végétation, panneaux, langue des textes, plaques, style des véhicules, relief, climat) et un niveau de confiance.
        4. Tout détail inhabituel ou remarquable.
        Utilise des titres et des listes en Markdown.
        """;

    private readonly KaonomiAssistantService? _server;
    private readonly OllamaModelService? _ollama;
    private ImagePixels? _original;
    private CancellationTokenSource? _render;
    /// <summary>Number of the last change of the view: a redraw waits a moment and is skipped when another change came.</summary>
    private int _renderVersion;
    private ImageAnalysisEntry _entry;

    /// <summary>Design-time constructor.</summary>
    public ImageAnalysisViewModel() : this(string.Empty, null, null, ChatSettings.DefaultVisionModel)
    {
    }

    /// <summary>Analysis of the picture at <paramref name="path"/>, saved with <paramref name="server"/>, described by <paramref name="ollama"/>.</summary>
    public ImageAnalysisViewModel(string path, KaonomiAssistantService? server, OllamaModelService? ollama, string visionModel)
    {
        ImagePath = path;
        VisionModel = visionModel;
        _server = server;
        _ollama = ollama;
        _entry = new ImageAnalysisEntry { ImagePath = path, FileName = Path.GetFileName(path) };
        _imageTitle = Path.GetFileName(path);
        if (path.Length > 0)
            _ = LoadAsync();
    }

    public string ImagePath { get; }
    public string FileName => Path.GetFileName(ImagePath);

    /// <summary>The title of the picture, typed by the user (by default its file name); saved with the analysis.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _imageTitle = string.Empty;

    /// <summary>The title, else the file name.</summary>
    private string ShownTitle => string.IsNullOrWhiteSpace(ImageTitle) ? FileName : ImageTitle.Trim();

    /// <summary>The title of the window: "Image-" and the title of the picture.</summary>
    public string Title => $"Image-{ShownTitle}";

    /// <summary>
    /// What a right click on the picture annotates: the picture (its analysis id, else its path), or the part of it
    /// under the pointer (<paramref name="part"/>: its number, comment and place in the excerpt).
    /// </summary>
    public AnnotationTarget AnnotationTargetFor(ImageAnnotationViewModel? part = null) => new(
        AnnotationTargetTypes.Image,
        _entry.Id ?? ImagePath,
        ShownTitle,
        part is null ? null : $"Part #{part.Number}{(string.IsNullOrWhiteSpace(part.Comment) ? string.Empty : ": " + part.Comment.Trim())} (x {part.Item.X:0.###}, y {part.Item.Y:0.###}, {part.Item.Width:0.###} × {part.Item.Height:0.###})");

    partial void OnImageTitleChanged(string value) => Log("title", value.Trim(), merge: true);

    public ObservableCollection<MetadataGroup> Metadata { get; } = [];
    public ObservableCollection<ImageAnnotationViewModel> Annotations { get; } = [];
    public ObservableCollection<ImageCommentItem> PreviousComments { get; } = [];

    /// <summary>The picture as shown (processed, rotated); its size gives the shares of the annotations.</summary>
    [ObservableProperty]
    private Bitmap? _shown;

    [ObservableProperty]
    private int _brightness;

    [ObservableProperty]
    private int _contrast;

    [ObservableProperty]
    private bool _monochrome;

    [ObservableProperty]
    private bool _binarize;

    [ObservableProperty]
    private int _threshold = 128;

    [ObservableProperty]
    private bool _edges;

    [ObservableProperty]
    private bool _invert;

    [ObservableProperty]
    private int _rotation;

    [ObservableProperty]
    private bool _flipHorizontal;

    [ObservableProperty]
    private bool _flipVertical;

    /// <summary>Zoom of the view (1: the picture at its working size); set to fit by the view when it opens.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    private double _zoom = 1;

    /// <summary>True while rectangles are drawn on the picture (the mouse draws instead of scrolling).</summary>
    [ObservableProperty]
    private bool _isAnnotating;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteAnnotationCommand))]
    private ImageAnnotationViewModel? _selectedAnnotation;

    /// <summary>The comment written now (added to the previous ones at the next save).</summary>
    [ObservableProperty]
    private string _newComment = string.Empty;

    [ObservableProperty]
    private string _aiAnalysis = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyseCommand))]
    private bool _isAnalysing;

    [ObservableProperty]
    private string _latitude = string.Empty;

    [ObservableProperty]
    private string _longitude = string.Empty;

    [ObservableProperty]
    private string _locationName = string.Empty;

    [ObservableProperty]
    private string _status = "Loading the picture…";

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private bool _isSaving;

    /// <summary>Size of the picture in pixels (original file), and when the analysis was saved last.</summary>
    [ObservableProperty]
    private string _summary = string.Empty;

    public string ZoomText => $"{Zoom * 100:0} %";

    public bool CanUseServer => _server is not null;

    public ImageAdjustments Adjustments => new(Brightness, Contrast, ActiveFilters(), Threshold, Rotation, FlipHorizontal, FlipVertical);

    private List<ImageFilter> ActiveFilters()
    {
        var filters = new List<ImageFilter>();
        if (Monochrome)
            filters.Add(ImageFilter.Monochrome);
        if (Binarize)
            filters.Add(ImageFilter.Binarize);
        if (Edges)
            filters.Add(ImageFilter.Edges);
        if (Invert)
            filters.Add(ImageFilter.Invert);
        return filters;
    }

    // Every change of the view is redrawn (a moment after the last one) and logged as an action of the analysis.
    partial void OnBrightnessChanged(int value) => Changed("brightness", value.ToString(CultureInfo.InvariantCulture));
    partial void OnContrastChanged(int value) => Changed("contrast", value.ToString(CultureInfo.InvariantCulture));
    partial void OnMonochromeChanged(bool value) => Changed("filter", $"monochrome {(value ? "on" : "off")}");
    partial void OnBinarizeChanged(bool value) => Changed("filter", $"binarization {(value ? "on" : "off")}");
    partial void OnThresholdChanged(int value) => Changed("threshold", value.ToString(CultureInfo.InvariantCulture));
    partial void OnEdgesChanged(bool value) => Changed("filter", $"edges {(value ? "on" : "off")}");
    partial void OnInvertChanged(bool value) => Changed("filter", $"invert {(value ? "on" : "off")}");
    partial void OnRotationChanged(int value) => Changed("rotate", value.ToString(CultureInfo.InvariantCulture));
    partial void OnFlipHorizontalChanged(bool value) => Changed("flip", $"horizontal {(value ? "on" : "off")}");
    partial void OnFlipVerticalChanged(bool value) => Changed("flip", $"vertical {(value ? "on" : "off")}");
    partial void OnZoomChanged(double value) => Log("zoom", ZoomText, merge: true);

    private bool _loading;

    private void Changed(string action, string value)
    {
        if (_loading)
            return;
        Log(action, value, merge: action is "brightness" or "contrast" or "threshold");
        OnPropertyChanged(nameof(Adjustments));
        ScheduleRender();
    }

    /// <summary>Redraws the picture a moment after the last change (a slider moved quickly redraws once).</summary>
    private async void ScheduleRender()
    {
        var version = ++_renderVersion;
        await Task.Delay(120);
        if (version != _renderVersion)
            return;
        try
        {
            await RenderAsync();
        }
        catch (Exception ex)
        {
            // Out of memory on a large picture, a filter that fails: said, not fatal.
            SetStatus($"The picture could not be drawn: {ex.Message}", true);
            ActivityLog.Error("Image", "The picture could not be drawn", ex);
        }
    }

    /// <summary>Adds an action to the log; a slider moved again within a few seconds updates its last action.</summary>
    private void Log(string action, string? value, bool merge = false)
    {
        if (_loading)
            return;
        var now = DateTimeOffset.Now;
        if (merge && _entry.Actions.LastOrDefault() is { } last && last.Action == action && now - last.Date < TimeSpan.FromSeconds(4))
        {
            last.Value = value;
            last.Date = now;
            return;
        }
        _entry.Actions.Add(new ImageAnalysisActionItem { Date = now, Action = action, Value = value });
    }

    /// <summary>Reads the picture, its metadata and its saved analysis.</summary>
    private async Task LoadAsync()
    {
        try
        {
            var bitmap = await Task.Run(() => LoadBitmap(ImagePath));
            if (bitmap is null)
            {
                SetStatus($"The picture cannot be read: {ImagePath}", true);
                return;
            }
            _original = await Task.Run(() => ImagePixels.From(bitmap));
            AnalyseCommand.NotifyCanExecuteChanged();
            await RenderAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            SetStatus($"The picture cannot be read: {ex.Message}", true);
            return;
        }

        foreach (var group in await PictureMetadata.ReadAsync(ImagePath, CancellationToken.None))
            Metadata.Add(group);
        Summary = $"{_entry.Width} × {_entry.Height} px · {FileName}";
        FillLocationFromMetadata(overwrite: false);

        if (_server is null)
        {
            SetStatus("No Kaonomi server: the analysis cannot be saved", false);
            return;
        }
        try
        {
            if (await _server.GetImageAnalysisOfImageAsync(ImagePath) is { } saved)
            {
                ApplySaved(saved);
                SetStatus($"Analysis saved on {saved.Date?.ToLocalTime():yyyy-MM-dd HH:mm} loaded: {saved.Comments.Count} comment(s), {saved.Annotations.Count} annotation(s)", false);
            }
            else
                SetStatus("New analysis: adjust the picture, annotate it, comment it, Analyse it with the AI, then Save", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("The saved analysis could not be read (Kaonomi server): " + ex.Message, true);
        }
    }

    /// <summary>The picture decoded, at most <see cref="MaxWorkingSize"/> pixels wide; its full size noted.</summary>
    private Bitmap? LoadBitmap(string path)
    {
        Bitmap? bitmap = null;
        try
        {
            using var stream = File.OpenRead(path);
            bitmap = new Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or InvalidOperationException or IOException)
        {
            // HEIC, TIFF, PSD…: through the picture library (converted with sips on macOS).
            bitmap = PictureLibrary.DecodeAsync(path, MaxWorkingSize, CancellationToken.None).GetAwaiter().GetResult();
        }
        if (bitmap is null)
            return null;
        _entry.Width = bitmap.PixelSize.Width;
        _entry.Height = bitmap.PixelSize.Height;
        if (bitmap.PixelSize.Width <= MaxWorkingSize)
            return bitmap;
        var reduced = bitmap.CreateScaledBitmap(new PixelSize(MaxWorkingSize,
            (int)Math.Round(MaxWorkingSize * (double)bitmap.PixelSize.Height / bitmap.PixelSize.Width)), BitmapInterpolationMode.HighQuality);
        bitmap.Dispose();
        return reduced;
    }

    private void ApplySaved(ImageAnalysisEntry saved)
    {
        _loading = true;
        try
        {
            _entry = saved;
            _entry.ImagePath = ImagePath;
            Brightness = saved.Brightness;
            Contrast = saved.Contrast;
            Monochrome = saved.Filters.Contains("monochrome");
            Binarize = saved.Filters.Contains("binarization");
            Edges = saved.Filters.Contains("edges");
            Invert = saved.Filters.Contains("invert");
            Threshold = saved.BinarizationThreshold;
            Rotation = saved.Rotation;
            FlipHorizontal = saved.FlipHorizontal;
            FlipVertical = saved.FlipVertical;
            Annotations.Clear();
            foreach (var annotation in saved.Annotations)
                Annotations.Add(new ImageAnnotationViewModel(annotation, Annotations.Count + 1));
            PreviousComments.Clear();
            foreach (var comment in saved.Comments)
                PreviousComments.Add(comment);
            AiAnalysis = saved.AiAnalysis ?? string.Empty;
            ImageTitle = string.IsNullOrWhiteSpace(saved.Title) ? FileName : saved.Title;
            Latitude = saved.Latitude?.ToString("0.######", CultureInfo.InvariantCulture) ?? Latitude;
            Longitude = saved.Longitude?.ToString("0.######", CultureInfo.InvariantCulture) ?? Longitude;
            LocationName = saved.LocationName ?? LocationName;
        }
        finally
        {
            _loading = false;
        }
        OnPropertyChanged(nameof(Adjustments));
        _ = RenderAsync();
    }

    /// <summary>Draws the picture with the current adjustments (in the background; the last request wins).</summary>
    private async Task RenderAsync()
    {
        if (_original is not { } original)
            return;
        _render?.Cancel();
        var render = _render = new CancellationTokenSource();
        var adjustments = Adjustments;
        var processed = await Task.Run(() => original.Apply(adjustments));
        if (render.IsCancellationRequested)
            return;
        var old = Shown;
        Shown = processed.ToBitmap();
        // The bitmap shown before may still be converted to a texture by the view: it is freed at the next render, the
        // one before it now.
        _retired?.Dispose();
        _retired = old;
    }

    /// <summary>The bitmap shown before the current one, freed at the next render.</summary>
    private Avalonia.Media.Imaging.Bitmap? _retired;

    // Toolbar.

    [RelayCommand]
    private void RotateLeft() => Rotation = (Rotation + 270) % 360;

    [RelayCommand]
    private void RotateRight() => Rotation = (Rotation + 90) % 360;

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Min(8, Math.Round(Zoom * 1.25, 3));

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Max(0.05, Math.Round(Zoom / 1.25, 3));

    /// <summary>Puts the picture back as it is (no brightness, contrast, filter or turn).</summary>
    [RelayCommand]
    private void ResetView()
    {
        _loading = true;
        Brightness = Contrast = 0;
        Monochrome = Binarize = Edges = Invert = FlipHorizontal = FlipVertical = false;
        Threshold = 128;
        Rotation = 0;
        _loading = false;
        Log("reset", "view");
        OnPropertyChanged(nameof(Adjustments));
        _ = RenderAsync();
    }

    // Annotations.

    /// <summary>Adds a rectangle drawn on the shown picture (<paramref name="shown"/> in shares of the shown picture).</summary>
    public ImageAnnotationViewModel AddAnnotation(Rect shown)
    {
        var rect = ImagePixels.FromView(shown, Adjustments);
        var item = new ImageAnnotationItem
        {
            X = Math.Round(rect.X, 5), Y = Math.Round(rect.Y, 5), Width = Math.Round(rect.Width, 5), Height = Math.Round(rect.Height, 5),
            Date = DateTimeOffset.Now
        };
        var annotation = new ImageAnnotationViewModel(item, Annotations.Count + 1);
        Annotations.Add(annotation);
        SelectedAnnotation = annotation;
        Log("annotate", $"#{annotation.Number} at {item.X:0.###},{item.Y:0.###} ({item.Width:0.###} × {item.Height:0.###})");
        _ = SavePartAsync(annotation);
        return annotation;
    }

    /// <summary>Folder of the parts of pictures cut out by the annotation rectangles: Pictures/kaonomi/imageparts.</summary>
    public static string ImagePartsDirectory => Path.Combine(ChatSettings.SystemPicturesDirectory, "kaonomi", "imageparts");

    /// <summary>
    /// The part of the picture in the rectangle of <paramref name="annotation"/> (its own pixels: no filter, not turned)
    /// saved as a PNG in <see cref="ImagePartsDirectory"/>, then as an analysis of category "subimage" on the server.
    /// </summary>
    private async Task SavePartAsync(ImageAnnotationViewModel annotation)
    {
        if (_original is not { } original)
            return;
        var item = annotation.Item;
        try
        {
            var part = original.Crop(new Rect(item.X, item.Y, item.Width, item.Height));
            Directory.CreateDirectory(ImagePartsDirectory);
            var path = Path.Combine(ImagePartsDirectory,
                $"{Path.GetFileNameWithoutExtension(ImagePath)}-part{annotation.Number}-{DateTime.Now:yyyyMMdd-HHmmss}-{item.Id[..6]}.png");
            await Task.Run(() =>
            {
                using var bitmap = part.ToBitmap();
                bitmap.Save(path);
            });
            item.ImagePartPath = path;
            Log("subimage", path);
            ActivityLog.Info("ImageAnalysis", $"Part #{annotation.Number} of {FileName} saved: {path}");
            await SavePartRecordAsync(annotation);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            SetStatus($"The part #{annotation.Number} was not saved: {ex.Message}", true);
        }
    }

    /// <summary>
    /// Saves the analysis of the part of <paramref name="annotation"/> (category "subimage"): its metadata hold the
    /// picture it comes from (path, link, id of its analysis), the rectangle (shares and pixels of that picture), the
    /// comment of the annotation, and every metadata of that picture; saved again with the picture, to keep the comment.
    /// </summary>
    private async Task SavePartRecordAsync(ImageAnnotationViewModel annotation)
    {
        var item = annotation.Item;
        if (_server is null || item.ImagePartPath is not { } partPath || !File.Exists(partPath))
            return;
        var parentId = _entry.Id ?? ImageAnalysisEntry.IdFor(ImagePath);
        var pixels = $"{Math.Round(item.X * _entry.Width)}, {Math.Round(item.Y * _entry.Height)}, " +
                     $"{Math.Round(item.Width * _entry.Width)} × {Math.Round(item.Height * _entry.Height)}";
        ImageMetadataItem Origin(string key, string value) => new() { Group = "Origin", Key = key, Value = value };
        var metadata = new List<ImageMetadataItem>
        {
            Origin("Image path", ImagePath),
            Origin("Image URL", new Uri(ImagePath).AbsoluteUri),
            Origin("Analysis id (imageanalysis)", parentId),
            Origin("Rectangle (shares x, y, width, height)", string.Create(CultureInfo.InvariantCulture, $"{item.X:0.#####}, {item.Y:0.#####}, {item.Width:0.#####}, {item.Height:0.#####}")),
            Origin("Rectangle (pixels x, y, width × height)", pixels),
            Origin("Annotation", $"#{annotation.Number}"),
            Origin("Annotation comment", item.Comment)
        };
        metadata.AddRange(Metadata.SelectMany(g => g.Entries.Select(e => new ImageMetadataItem { Group = "Original · " + g.Name, Key = e.Key, Value = e.Value })));
        var size = new FileInfo(partPath).Length;
        var record = new ImageAnalysisEntry
        {
            // The server needs the date of the record (null is refused with 400).
            Date = DateTimeOffset.Now,
            ImagePath = partPath,
            FileName = Path.GetFileName(partPath),
            Title = $"{ShownTitle} · part #{annotation.Number}",
            Category = "subimage",
            ParentId = parentId,
            ParentImagePath = ImagePath,
            Comment = item.Comment,
            AppName = "KAONOMI.Chat",
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            Width = (int)Math.Round(item.Width * _entry.Width),
            Height = (int)Math.Round(item.Height * _entry.Height),
            Metadata = metadata,
            Actions = [new ImageAnalysisActionItem { Date = item.Date, Action = "subimage", Value = $"#{annotation.Number} of {ImagePath} ({size} bytes)" }]
        };
        try
        {
            var result = await _server.SaveImageAnalysisAsync(record);
            if (result.ReturnedId is null or "-1")
                SetStatus($"The part #{annotation.Number} was saved in {ImagePartsDirectory} but not on the server: {result.Message}", true);
            else
                SetStatus($"✓ Part #{annotation.Number} saved as a subimage: {Path.GetFileName(partPath)} (imageanalysis {result.ReturnedId[..8]}…)", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus($"The part #{annotation.Number} was saved in {ImagePartsDirectory} but not on the server: {ex.Message}", true);
        }
    }

    private bool HasSelectedAnnotation() => SelectedAnnotation is not null;

    /// <summary>Removes the selected rectangle (the comments of the picture are never removed).</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedAnnotation))]
    private void DeleteAnnotation()
    {
        if (SelectedAnnotation is not { } annotation)
            return;
        Annotations.Remove(annotation);
        Log("annotation removed", $"#{annotation.Number} {annotation.Comment}");
        for (var i = 0; i < Annotations.Count; i++)
            Annotations[i].Number = i + 1;
        SelectedAnnotation = null;
    }

    // AI analysis.

    private bool CanAnalyse() => !IsAnalysing && _ollama is not null && _original is not null;

    /// <summary>
    /// Sends the picture (as shown: adjusted and turned, at most <see cref="MaxAiSize"/> pixels) to the vision model,
    /// which describes it in detail and says where it could have been taken; the answer goes to the AI analysis.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAnalyse))]
    private async Task AnalyseAsync()
    {
        if (_ollama is null || Shown is null)
            return;
        IsAnalysing = true;
        SetStatus($"The AI ({VisionModel}) is looking at the picture… (a minute or more)", false);
        try
        {
            var models = await _ollama.ListModelsAsync();
            var model = models.FirstOrDefault(m => m.StartsWith(VisionModel, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"The model {VisionModel} is not installed in Ollama (ollama pull {VisionModel}).");
            var png = EncodeForAi(Shown);
            var started = DateTimeOffset.Now;
            var answer = await _ollama.DescribeImageAsync(model, null, AiPrompt, png);
            AiAnalysis = LatexText.Convert(answer).Trim();
            _entry.AiModel = model;
            _entry.AiDate = DateTimeOffset.Now;
            Log("aiAnalysis", $"{model}, {(DateTimeOffset.Now - started).TotalSeconds:0} s");
            SetStatus($"AI analysis written by {model} in {(DateTimeOffset.Now - started).TotalSeconds:0} s: read it, change it if needed, then Save", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException)
        {
            SetStatus("AI analysis failed: " + ex.Message, true);
        }
        finally
        {
            IsAnalysing = false;
        }
    }

    /// <summary>The shown picture as PNG, its longest side at most <see cref="MaxAiSize"/> pixels.</summary>
    private static byte[] EncodeForAi(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var scale = Math.Min(1.0, (double)MaxAiSize / Math.Max(size.Width, size.Height));
        using var stream = new MemoryStream();
        if (scale < 1)
        {
            using var reduced = bitmap.CreateScaledBitmap(new PixelSize((int)(size.Width * scale), (int)(size.Height * scale)), BitmapInterpolationMode.HighQuality);
            reduced.Save(stream);
        }
        else
            bitmap.Save(stream);
        return stream.ToArray();
    }

    // Location.

    /// <summary>Fills the latitude and longitude from the GPS metadata of the picture.</summary>
    [RelayCommand]
    private void LocationFromMetadata()
    {
        if (FillLocationFromMetadata(overwrite: true))
        {
            Log("geolocate", $"{Latitude}, {Longitude} (GPS metadata)");
            SetStatus("Position read from the GPS metadata of the picture", false);
        }
        else
            SetStatus("The picture has no GPS position in its metadata", true);
    }

    private bool FillLocationFromMetadata(bool overwrite)
    {
        if (!overwrite && (Latitude.Length > 0 || Longitude.Length > 0))
            return false;
        var gps = Metadata.FirstOrDefault(g => g.Name.Equals("GPS", StringComparison.OrdinalIgnoreCase));
        if (gps is null)
            return false;
        double? Read(string key, string refKey, string negative)
        {
            var entry = gps.Entries.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (entry is null || !double.TryParse(Regex.Match(entry.Value, @"-?\d+(\.\d+)?").Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return null;
            var direction = gps.Entries.FirstOrDefault(e => e.Key.Equals(refKey, StringComparison.OrdinalIgnoreCase))?.Value;
            return direction is not null && direction.Trim().StartsWith(negative, StringComparison.OrdinalIgnoreCase) ? -Math.Abs(value) : value;
        }
        if (Read("Latitude", "LatitudeRef", "S") is not { } latitude || Read("Longitude", "LongitudeRef", "W") is not { } longitude)
            return false;
        Latitude = latitude.ToString("0.######", CultureInfo.InvariantCulture);
        Longitude = longitude.ToString("0.######", CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>Opens the place on OpenStreetMap in the browser of the chat (a window of KAONOMI).</summary>
    [RelayCommand]
    private void ShowOnMap()
    {
        if (ParseCoordinate(Latitude) is { } latitude && ParseCoordinate(Longitude) is { } longitude)
            PictureLibrary.OpenWeb(string.Create(CultureInfo.InvariantCulture,
                $"https://www.openstreetmap.org/?mlat={latitude}&mlon={longitude}#map=14/{latitude}/{longitude}"));
        else if (LocationName.Trim().Length > 0)
            PictureLibrary.OpenWeb("https://www.openstreetmap.org/search?query=" + Uri.EscapeDataString(LocationName.Trim()));
        else
            SetStatus("Give a latitude and a longitude, or the name of the place", true);
    }

    private static double? ParseCoordinate(string text) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    // Save.

    private bool CanSave() => !IsSaving && _server is not null;

    /// <summary>
    /// Saves the analysis: the link to the picture, its metadata, the settings of the view, the annotations, the
    /// comments (the new one added to the previous ones), the AI analysis, the place and every action.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (_server is null)
            return;
        var latitude = ParseCoordinate(Latitude);
        var longitude = ParseCoordinate(Longitude);
        if ((Latitude.Trim().Length > 0 && latitude is not (>= -90 and <= 90)) || (Longitude.Trim().Length > 0 && longitude is not (>= -180 and <= 180)))
        {
            SetStatus("Latitude: -90 to 90, longitude: -180 to 180 (decimal degrees)", true);
            return;
        }

        IsSaving = true;
        var entry = _entry;
        entry.ImagePath = ImagePath;
        entry.FileName = FileName;
        entry.Title = ShownTitle;
        entry.AppName = "KAONOMI.Chat";
        entry.MachineName = Environment.MachineName;
        entry.UserName = Environment.UserName;
        entry.Metadata = Metadata.SelectMany(g => g.Entries.Select(e => new ImageMetadataItem { Group = g.Name, Key = e.Key, Value = e.Value })).ToList();
        entry.Brightness = Brightness;
        entry.Contrast = Contrast;
        entry.Filters = ActiveFilters().Select(f => f switch
        {
            ImageFilter.Binarize => "binarization",
            _ => f.ToString().ToLowerInvariant()
        }).ToList();
        entry.BinarizationThreshold = Threshold;
        entry.Rotation = Rotation;
        entry.FlipHorizontal = FlipHorizontal;
        entry.FlipVertical = FlipVertical;
        entry.Zoom = Math.Round(Zoom, 3);
        entry.Annotations = Annotations.Select(a => a.Item).ToList();
        var comments = PreviousComments.ToList();
        if (NewComment.Trim().Length > 0)
        {
            comments.Add(new ImageCommentItem { Text = NewComment.Trim(), Date = DateTimeOffset.Now, UserName = Environment.UserName });
            Log("comment", NewComment.Trim().Length > 80 ? NewComment.Trim()[..80] + "…" : NewComment.Trim());
        }
        entry.Comments = comments;
        entry.AiAnalysis = AiAnalysis.Trim().Length > 0 ? AiAnalysis.Trim() : null;
        entry.Latitude = latitude;
        entry.Longitude = longitude;
        entry.LocationName = LocationName.Trim().Length > 0 ? LocationName.Trim() : null;
        if (latitude is not null || entry.LocationName is not null)
            Log("geolocate", $"{Latitude} {Longitude} {LocationName}".Trim(), merge: true);
        Log("save", null);
        try
        {
            var result = await _server.SaveImageAnalysisAsync(entry);
            if (result.ReturnedId is null or "-1")
            {
                SetStatus("Not saved: " + result.Message, true);
                return;
            }
            entry.Id = result.ReturnedId;
            if (NewComment.Trim().Length > 0)
            {
                PreviousComments.Add(comments[^1]);
                NewComment = string.Empty;
            }
            // The subimages are saved again, with the comments of their annotations as they are now.
            foreach (var annotation in Annotations.Where(a => a.Item.ImagePartPath is not null).ToList())
                await SavePartRecordAsync(annotation);
            SetStatus($"✓ Analysis saved (imageanalysis {result.ReturnedId[..8]}…) · {entry.Actions.Count} action(s), {entry.Annotations.Count} annotation(s) and their subimages, {entry.Comments.Count} comment(s)", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("Not saved: " + ex.Message, true);
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Notes an export of the picture (its file) in the actions of the analysis, and in the status.</summary>
    public void Exported(string path, PixelSize size)
    {
        Log("export", path);
        SetStatus($"✓ Exported as PNG ({size.Width} × {size.Height}, with the filters and the annotations): {path}", false);
    }

    /// <summary>Shows an error of the export.</summary>
    public void ExportFailed(string message) => SetStatus("Not exported: " + message, true);

    /// <summary>Opens the picture with the viewer of the system.</summary>
    [RelayCommand]
    private void OpenInSystem() => PictureLibrary.OpenWithSystem(ImagePath);

    private void SetStatus(string text, bool error)
    {
        Status = text;
        IsError = error;
    }
}
