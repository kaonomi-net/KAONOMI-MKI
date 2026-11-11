using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>An image of <c>/fooocus</c>: its file in Pictures/kaonomi/fooocus, its thumbnail and what was asked for it.</summary>
public partial class FooocusImageViewModel : ObservableObject
{
    public FooocusImageViewModel(string path, FooocusImageInfo? info)
    {
        Path = path;
        Info = info;
        _ = LoadThumbnailAsync();
    }

    /// <summary>The image file.</summary>
    public string Path { get; }

    /// <summary>Prompt, seed, model… (null for an image without its name.json).</summary>
    public FooocusImageInfo? Info { get; }

    /// <summary>The prompt, shown under the thumbnail.</summary>
    public string Caption => Info?.Prompt ?? System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>Seed, size, performance, model and time, shown under the preview.</summary>
    public string Details => Info is null
        ? System.IO.Path.GetFileName(Path)
        : $"seed {Info.Seed} · {Info.AspectRatio?.Replace('*', '×')} · {Info.Performance} · {Info.Model} · {Info.Seconds:0.#} s · {Info.Date.ToLocalTime():yyyy-MM-dd HH:mm}";

    [ObservableProperty]
    private Bitmap? _thumbnail;

    private async Task LoadThumbnailAsync() =>
        Thumbnail = await PictureLibrary.DecodeAsync(Path, 320, CancellationToken.None);
}

/// <summary>A style of Fooocus, ticked or not.</summary>
public partial class FooocusStyleViewModel(string name, bool selected) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    private bool _isSelected = selected;
}

/// <summary>
/// Image generation with Fooocus (<c>/fooocus</c>, <c>/nfooocus</c> in its own window): a prompt, its negative prompt,
/// the styles, the performance, the size, the number of images, the seed and the model, sent to the Kaonomi server
/// (<c>POST /image/generate</c>, which hands it to the Fooocus API). The images are saved in the Pictures folder of the
/// system, kaonomi/fooocus, each with a name.json of what was asked; the gallery shows them, newest first.
/// </summary>
public partial class FooocusWindowViewModel : ViewModelBase, INoteContextProvider
{
    /// <inheritdoc />
    public string NoteWindowName => "Fooocus";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        prompt = Prompt,
        negativePrompt = NegativePrompt,
        styles = Styles.Where(s => s.IsSelected).Select(s => s.Name).ToList(),
        performance = Performance,
        aspectRatio = AspectRatio,
        seed = Seed,
        model = Model,
        selectedImage = SelectedImage?.Path,
        selectedPrompt = SelectedImage?.Info?.Prompt
    };

    /// <summary>Images shown in the gallery (the newest ones of the folder).</summary>
    private const int GalleryLimit = 200;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly KaonomiAssistantService _assistant;
    private readonly string _folder;
    private CancellationTokenSource? _generation;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _elapsed = new();

    /// <summary>Design-time constructor using the default server URL from settings.</summary>
    public FooocusWindowViewModel() : this(new KaonomiAssistantService(ChatSettings.Load().ServerUrl))
    {
    }

    /// <summary>Uses the given server client; images go to <paramref name="folder"/> (Pictures/kaonomi/fooocus by default).</summary>
    public FooocusWindowViewModel(KaonomiAssistantService assistant, string? folder = null)
    {
        _assistant = assistant;
        _folder = folder ?? ChatSettings.FooocusDirectory;
        _clock.Tick += (_, _) => Status = $"Generating with Fooocus… {_elapsed.Elapsed:m\\:ss}";
        LoadGallery();
        _ = LoadOptionsAsync();
    }

    /// <summary>Folder of the images.</summary>
    public string Folder => _folder;

    public ObservableCollection<FooocusImageViewModel> Images { get; } = [];
    public ObservableCollection<FooocusStyleViewModel> Styles { get; } = [];
    public ObservableCollection<string> Performances { get; } = ["Speed", "Quality", "Extreme Speed", "Lightning", "Hyper-SD"];
    public ObservableCollection<string> AspectRatios { get; } = ["1152*896", "1024*1024", "896*1152", "1344*768", "768*1344"];
    public ObservableCollection<string> Models { get; } = [];

    /// <summary>Opens a file or a folder with the system; replaced by the tests.</summary>
    public Action<string> OpenWithSystem { get; set; } = PictureLibrary.OpenWithSystem;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    private string _prompt = string.Empty;

    [ObservableProperty]
    private string _negativePrompt = string.Empty;

    [ObservableProperty]
    private string _styleFilter = string.Empty;

    [ObservableProperty]
    private string _performance = "Speed";

    [ObservableProperty]
    private string _aspectRatio = "1152*896";

    [ObservableProperty]
    private decimal _imageNumber = 1;

    /// <summary>Seed of the first image; -1 (or empty): random.</summary>
    [ObservableProperty]
    private string _seed = "-1";

    [ObservableProperty]
    private string? _model;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReuseCommand))]
    private FooocusImageViewModel? _selectedImage;

    [ObservableProperty]
    private Bitmap? _preview;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isGenerating;

    [ObservableProperty]
    private string _status = "Describe the image, then Generate (Ctrl+Enter)";

    [ObservableProperty]
    private bool _isError;

    /// <summary>The styles whose name contains the filter (all of them when it is empty), the ticked ones first.</summary>
    public IEnumerable<FooocusStyleViewModel> VisibleStyles => Styles
        .Where(s => StyleFilter.Length == 0 || s.Name.Contains(StyleFilter, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(s => s.IsSelected);

    /// <summary>The ticked styles, shown above the list.</summary>
    public string SelectedStylesText => Styles.Where(s => s.IsSelected).Select(s => s.Name) is var names && names.Any()
        ? string.Join(" · ", names)
        : "No style";

    partial void OnStyleFilterChanged(string value) => OnPropertyChanged(nameof(VisibleStyles));

    partial void OnSelectedImageChanged(FooocusImageViewModel? value) => _ = LoadPreviewAsync(value);

    private async Task LoadPreviewAsync(FooocusImageViewModel? image)
    {
        Preview = image is null ? null : await PictureLibrary.DecodeAsync(image.Path, 1600, CancellationToken.None);
    }

    /// <summary>The styles, performances, sizes and models of Fooocus, through the server; the defaults stay when it does not answer.</summary>
    private async Task LoadOptionsAsync()
    {
        try
        {
            var options = await _assistant.GetImageOptionsAsync();
            Replace(Performances, options.Performances);
            Replace(AspectRatios, options.AspectRatios.Select(r => r.Replace('×', '*')));
            Replace(Models, options.Models);
            var defaults = new HashSet<string>(options.DefaultStyles);
            Styles.Clear();
            foreach (var style in options.Styles)
                AddStyle(new FooocusStyleViewModel(style, defaults.Contains(style)));
            Performance = options.DefaultPerformance ?? Performance;
            AspectRatio = options.DefaultAspectRatio ?? AspectRatio;
            Model = options.DefaultModel;
            OnPropertyChanged(nameof(VisibleStyles));
            OnPropertyChanged(nameof(SelectedStylesText));
            SetStatus($"Fooocus ready · {options.Styles.Count} styles · model {options.DefaultModel}", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            SetStatus("Fooocus not reachable: start the Fooocus API (KAONOMI.Server/Fooocus/fooocus_api.py) and the Kaonomi server. "
                      + ex.Message, true);
        }
    }

    private void AddStyle(FooocusStyleViewModel style)
    {
        style.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FooocusStyleViewModel.IsSelected))
                OnPropertyChanged(nameof(SelectedStylesText));
        };
        Styles.Add(style);
    }

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> values)
    {
        var list = values.ToList();
        if (list.Count == 0)
            return;
        target.Clear();
        foreach (var value in list)
            target.Add(value);
    }

    private bool CanGenerate() => !IsGenerating && Prompt.Trim().Length > 0;

    /// <summary>Sends the prompt to Fooocus and saves the images in the folder (Ctrl+Enter).</summary>
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        var seed = long.TryParse(Seed.Trim(), out var parsed) && parsed >= 0 ? parsed : -1;
        var styles = Styles.Where(s => s.IsSelected).Select(s => s.Name).ToList();
        var request = new ImageGenerationRequest
        {
            Prompt = Prompt.Trim(),
            NegativePrompt = string.IsNullOrWhiteSpace(NegativePrompt) ? null : NegativePrompt.Trim(),
            Styles = Styles.Count > 0 ? styles : null,
            Performance = Performance,
            AspectRatio = AspectRatio,
            ImageNumber = (int)Math.Clamp(ImageNumber, 1, 8),
            Seed = seed,
            BaseModel = Model
        };

        _generation = new CancellationTokenSource();
        IsGenerating = true;
        IsError = false;
        _elapsed.Restart();
        _clock.Start();
        Status = "Generating with Fooocus… 0:00";
        try
        {
            var result = await _assistant.GenerateImagesAsync(request, _generation.Token);
            if (result.Error is { } error)
            {
                SetStatus(error, true);
                return;
            }
            // The pictures decoded from base64 and written on the thread pool (several megabytes each).
            var saved = await Task.Run(() => Save(result, request));
            foreach (var path in saved.AsEnumerable().Reverse())
                Images.Insert(0, new FooocusImageViewModel(path, ReadInfo(path)));
            SelectedImage = Images.FirstOrDefault();
            Seed = "-1";
            SetStatus($"{saved.Count} image(s) in {result.Seconds:0.#} s · seed {result.Seed} · saved in {_folder}", false);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Stopped waiting (Fooocus finishes the images in its own outputs folder)", false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or FormatException or JsonException)
        {
            SetStatus("Generation failed: " + ex.Message, true);
        }
        finally
        {
            _clock.Stop();
            _generation.Dispose();
            _generation = null;
            IsGenerating = false;
        }
    }

    private bool CanCancel() => IsGenerating;

    /// <summary>Stops waiting for the images.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _generation?.Cancel();

    /// <summary>
    /// Writes the images of <paramref name="result"/> in the folder (date, seed and the start of the prompt in their
    /// names), each with a name.json of what was asked; returns their paths.
    /// </summary>
    private List<string> Save(ImageGenerationResult result, ImageGenerationRequest request)
    {
        Directory.CreateDirectory(_folder);
        var paths = new List<string>();
        var now = DateTimeOffset.Now;
        for (var i = 0; i < result.Images.Count; i++)
        {
            var image = result.Images[i];
            if (image.Base64 is not { Length: > 0 } data)
                continue;
            var extension = System.IO.Path.GetExtension(image.Path) is { Length: > 0 } ext ? ext : ".png";
            var seed = result.Seed + i;
            var name = $"{now:yyyy-MM-dd_HH-mm-ss}_{seed}_{Slug(request.Prompt)}";
            var path = System.IO.Path.Combine(_folder, name + extension);
            File.WriteAllBytes(path, Convert.FromBase64String(data));
            var info = new FooocusImageInfo
            {
                Prompt = request.Prompt,
                NegativePrompt = request.NegativePrompt,
                Styles = request.Styles ?? [],
                Performance = request.Performance,
                AspectRatio = request.AspectRatio,
                Seed = seed,
                Model = result.Model,
                Seconds = result.Seconds,
                Date = now
            };
            File.WriteAllText(System.IO.Path.Combine(_folder, name + ".json"), JsonSerializer.Serialize(info, JsonOptions), new UTF8Encoding(false));
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>The first words of the prompt for a file name: letters and digits, joined with dashes.</summary>
    private static string Slug(string prompt)
    {
        var text = new StringBuilder();
        foreach (var c in prompt.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c) && c < 128)
                text.Append(char.ToLowerInvariant(c));
            else if (text.Length > 0 && text[^1] != '-' && (char.IsWhiteSpace(c) || c is '-' or '_' or ',' or '.'))
                text.Append('-');
            if (text.Length >= 40)
                break;
        }
        return text.ToString().Trim('-') is { Length: > 0 } slug ? slug : "image";
    }

    private static FooocusImageInfo? ReadInfo(string path)
    {
        var json = System.IO.Path.ChangeExtension(path, ".json");
        try
        {
            return File.Exists(json) ? JsonSerializer.Deserialize<FooocusImageInfo>(File.ReadAllText(json)) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The images of the folder, newest first.</summary>
    private void LoadGallery()
    {
        if (!Directory.Exists(_folder))
            return;
        foreach (var file in new DirectoryInfo(_folder).EnumerateFiles()
                     .Where(f => f.Extension.ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp")
                     .OrderByDescending(f => f.LastWriteTimeUtc).Take(GalleryLimit))
            Images.Add(new FooocusImageViewModel(file.FullName, ReadInfo(file.FullName)));
        SelectedImage = Images.FirstOrDefault();
    }

    private bool HasSelection() => SelectedImage is not null;

    /// <summary>Makes the analysis of an image, for the image analysis window of /pictures (set by the chat).</summary>
    public Func<string, ImageAnalysisViewModel>? CreateAnalysis { get; set; }

    /// <summary>Opens the selected image with the viewer of the system (double-click on the preview).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenImage()
    {
        if (SelectedImage is { } image)
            OpenWithSystem(image.Path);
    }

    /// <summary>Puts the prompt, styles, size, performance, seed and model of the selected image back in the form.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Reuse()
    {
        if (SelectedImage?.Info is not { } info)
            return;
        Prompt = info.Prompt;
        NegativePrompt = info.NegativePrompt ?? string.Empty;
        foreach (var style in Styles)
            style.IsSelected = info.Styles.Contains(style.Name);
        OnPropertyChanged(nameof(VisibleStyles));
        if (info.Performance is { } performance)
            Performance = performance;
        if (info.AspectRatio is { } ratio)
            AspectRatio = ratio;
        if (info.Model is { } model && Models.Contains(model))
            Model = model;
        Seed = info.Seed.ToString();
        SetStatus($"Settings of the image put back (seed {info.Seed}): Generate makes it again, change the seed to -1 for a new one", false);
    }

    /// <summary>Opens the folder of the images.</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(_folder);
        OpenWithSystem(_folder);
    }

    private void SetStatus(string text, bool error)
    {
        Status = text;
        IsError = error;
    }
}
