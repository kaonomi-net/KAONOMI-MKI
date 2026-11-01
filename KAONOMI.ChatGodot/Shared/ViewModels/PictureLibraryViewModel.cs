using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>A picture of the grid: its file, its thumbnail (loaded in the background) and its labels.</summary>
public partial class PictureItemViewModel : ViewModelBase, IDisposable
{
    /// <summary>Width the thumbnails are decoded to (they are shown smaller or at this size).</summary>
    public const int ThumbnailWidth = 320;

    public PictureItemViewModel(PictureFile file) => File = file;

    /// <summary>The picture file.</summary>
    public PictureFile File { get; }

    /// <summary>File name with its extension.</summary>
    public string Name => File.Name;

    /// <summary>Format, in upper case (JPG, HEIC…).</summary>
    public string Format => Path.GetExtension(File.Path).TrimStart('.').ToUpperInvariant();

    /// <summary>Size of the file.</summary>
    public string SizeLabel => PictureLibrary.FormatSize(File.Size);

    /// <summary>Last change of the file, short.</summary>
    public string DateLabel => File.Modified.ToString("g");

    /// <summary>False for a format without thumbnail on this system (shown with its format only).</summary>
    public bool CanDecode => PictureLibrary.CanDecode(File.Path);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    /// <summary>True once the thumbnail is shown.</summary>
    public bool HasThumbnail => Thumbnail is not null;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Decodes the thumbnail in the background (nothing when the format cannot be decoded).</summary>
    public async Task LoadThumbnailAsync(CancellationToken cancellationToken)
    {
        if (Thumbnail is not null || !CanDecode)
            return;
        IsLoading = true;
        try
        {
            var bitmap = await PictureLibrary.DecodeAsync(File.Path, ThumbnailWidth, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                bitmap?.Dispose();
            else
                Thumbnail = bitmap;
        }
        catch (OperationCanceledException)
        {
            // Left the folder before the thumbnail was ready.
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var thumbnail = Thumbnail;
        Thumbnail = null;
        thumbnail?.Dispose();
    }
}

/// <summary>
/// Picture library of <c>/pictures</c> (<c>/picture</c>; <c>/npictures</c> in its own window): browses the folders of
/// <c>pictures.directory</c> (config.yml, the Pictures folder of the system by default), shows their pictures as
/// thumbnails (by pages, filtered by name and sorted), previews the selected one with its details, and opens, reveals,
/// renames or moves it to the trash.
/// </summary>
public partial class PictureLibraryViewModel : ViewModelBase, INoteContextProvider, IDisposable
{
    /// <summary>Pictures added to the grid at a time ("Show more" adds the next ones).</summary>
    private const int PageSize = 150;

    /// <summary>Width the selected picture is decoded to for the preview.</summary>
    private const int PreviewWidth = 1800;

    /// <summary>Pictures of the folder, filtered and sorted (the grid shows the first ones).</summary>
    private List<PictureFile> _matching = [];

    /// <summary>Every picture of the folder.</summary>
    private IReadOnlyList<PictureFile> _all = [];

    /// <summary>Cancels the thumbnails of the folder when another one is opened.</summary>
    private CancellationTokenSource _folderLoad = new();

    /// <summary>Cancels the preview being decoded when another picture is selected.</summary>
    private CancellationTokenSource? _previewLoad;

    /// <summary>Design-time constructor (the Pictures folder of the system).</summary>
    public PictureLibraryViewModel() : this(Models.ChatSettings.SystemPicturesDirectory)
    {
    }

    /// <summary>
    /// Makes the analysis of a picture (its window opens on a double-click); null: a double-click opens the picture
    /// with the viewer of the system.
    /// </summary>
    public Func<string, ImageAnalysisViewModel>? CreateAnalysis { get; set; }

    /// <summary>Opens the library at <paramref name="rootDirectory"/>.</summary>
    public PictureLibraryViewModel(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        _currentDirectory = RootDirectory;
        Load();
    }

    /// <summary>Root of the library (<c>pictures.directory</c> in config.yml).</summary>
    public string RootDirectory { get; }

    /// <summary>Folder shown.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpCommand))]
    private string _currentDirectory;

    /// <summary>The folders from the root to the current one (clickable path).</summary>
    public ObservableCollection<PictureFolder> Breadcrumbs { get; } = [];

    /// <summary>Sub-folders of the current folder.</summary>
    public ObservableCollection<PictureFolder> Folders { get; } = [];

    /// <summary>Pictures shown in the grid (the first pages of the matching ones).</summary>
    public ObservableCollection<PictureItemViewModel> Pictures { get; } = [];

    /// <summary>True when the folder has sub-folders.</summary>
    public bool HasFolders => Folders.Count > 0;

    /// <summary>True when more matching pictures than the ones shown remain.</summary>
    public bool HasMore => Pictures.Count < _matching.Count;

    /// <summary>Label of the "Show more" button.</summary>
    public string MoreLabel => $"Show more ({_matching.Count - Pictures.Count} left)";

    /// <summary>True when the folder shows nothing (no sub-folder, no matching picture).</summary>
    public bool IsEmpty => Folders.Count == 0 && _matching.Count == 0;

    /// <summary>Sort orders of the drop-down.</summary>
    public IReadOnlyList<string> SortModes { get; } = ["Name", "Newest first", "Oldest first", "Largest first"];

    [ObservableProperty]
    private string _sortMode = "Newest first";

    /// <summary>Text the file names must contain.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Displayed size of the thumbnails (zoom slider), in pixels.</summary>
    [ObservableProperty]
    private double _thumbnailSize = 150;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(OpenInSystemCommand), nameof(RevealCommand), nameof(DeleteCommand), nameof(RenameCommand),
        nameof(PreviousCommand), nameof(NextCommand), nameof(RotateLeftCommand), nameof(RotateRightCommand))]
    private PictureItemViewModel? _selectedPicture;

    /// <summary>True when a picture is selected.</summary>
    public bool HasSelection => SelectedPicture is not null;

    /// <summary>The selected picture, decoded for the preview.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    private Bitmap? _preview;

    /// <summary>True when the preview is shown.</summary>
    public bool HasPreview => Preview is not null;

    [ObservableProperty]
    private bool _isPreviewLoading;

    /// <summary>Width × height of the selected picture (from the decoded preview).</summary>
    [ObservableProperty]
    private string _dimensions = string.Empty;

    /// <summary>New name typed for the selected picture (without its folder).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand))]
    private string _newName = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    /// <summary>Asks the user to confirm (message, confirm label); set by the view.</summary>
    public Func<string, string, Task<bool>> ConfirmAsync { get; set; } = (message, label) => Confirmations.Ask(message, label);

    partial void OnSortModeChanged(string value) => ApplyFilter();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedPictureChanged(PictureItemViewModel? value)
    {
        NewName = value?.Name ?? string.Empty;
        _ = LoadPreviewAsync(value);
        _ = LoadMetadataAsync(value);
    }

    /// <summary>Every metadata of the selected picture (shown when the mouse is over the preview).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMetadata))]
    private IReadOnlyList<MetadataGroup> _metadata = [];

    /// <summary>True once the metadata of the selected picture are read.</summary>
    public bool HasMetadata => Metadata.Count > 0;

    /// <summary>Cancels the metadata being read when another picture is selected.</summary>
    private CancellationTokenSource? _metadataLoad;

    /// <summary>Reads every metadata of the selected picture in the background.</summary>
    private async Task LoadMetadataAsync(PictureItemViewModel? item)
    {
        _metadataLoad?.Cancel();
        Metadata = [];
        if (item is null)
            return;
        var cancellation = new CancellationTokenSource();
        _metadataLoad = cancellation;
        try
        {
            var groups = await PictureMetadata.ReadAsync(item.File.Path, cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(SelectedPicture, item))
                Metadata = groups;
        }
        catch (OperationCanceledException)
        {
            // Another picture was selected.
        }
    }

    /// <summary>Opens a sub-folder or a folder of the path.</summary>
    [RelayCommand]
    private void OpenFolder(PictureFolder? folder)
    {
        if (folder is null || !Directory.Exists(folder.Path))
            return;
        CurrentDirectory = folder.Path;
        SearchText = string.Empty;
        Load();
    }

    private bool CanGoUp() => !string.Equals(Path.GetFullPath(CurrentDirectory), RootDirectory, StringComparison.Ordinal);

    /// <summary>Goes to the parent folder (not above the root of the library).</summary>
    [RelayCommand(CanExecute = nameof(CanGoUp))]
    private void Up()
    {
        if (Directory.GetParent(CurrentDirectory) is { } parent)
            OpenFolder(new PictureFolder(parent.Name, parent.FullName));
    }

    /// <summary>Reads the current folder again.</summary>
    [RelayCommand]
    private void Refresh() => Load();

    /// <summary>Adds the next page of pictures to the grid.</summary>
    [RelayCommand]
    private void ShowMore() => AddPage();

    /// <summary>Reads the folder: sub-folders, pictures, path; the thumbnails of the first page start loading.</summary>
    private void Load()
    {
        _folderLoad.Cancel();
        _folderLoad = new CancellationTokenSource();
        ClearPictures();
        Folders.Clear();
        Breadcrumbs.Clear();
        IsError = false;

        // Path from the root (the root itself first).
        var crumbs = new List<PictureFolder>();
        for (var dir = new DirectoryInfo(CurrentDirectory); dir is not null; dir = dir.Parent)
        {
            crumbs.Insert(0, new PictureFolder(dir.FullName == RootDirectory ? Path.GetFileName(RootDirectory) is { Length: > 0 } root ? root : RootDirectory : dir.Name, dir.FullName));
            if (dir.FullName == RootDirectory)
                break;
        }
        foreach (var crumb in crumbs)
            Breadcrumbs.Add(crumb);

        try
        {
            var (folders, pictures) = PictureLibrary.List(CurrentDirectory);
            foreach (var folder in folders)
                Folders.Add(folder);
            _all = pictures;
            ApplyFilter();
            ActivityLog.Info("Pictures", $"Folder {CurrentDirectory}: {folders.Count} folder(s), {pictures.Count} picture(s)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _all = [];
            ApplyFilter();
            IsError = true;
            Status = $"Cannot read {CurrentDirectory}: {ex.Message}";
            ActivityLog.Error("Pictures", $"Folder {CurrentDirectory} not read", ex);
        }
        OnPropertyChanged(nameof(HasFolders));
    }

    /// <summary>Filters the pictures by name, sorts them and shows the first page.</summary>
    private void ApplyFilter()
    {
        var term = SearchText.Trim();
        IEnumerable<PictureFile> matching = term.Length == 0
            ? _all
            : _all.Where(p => p.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase));
        matching = SortMode switch
        {
            "Name" => matching.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
            "Oldest first" => matching.OrderBy(p => p.Modified),
            "Largest first" => matching.OrderByDescending(p => p.Size),
            _ => matching.OrderByDescending(p => p.Modified)
        };
        _matching = matching.ToList();

        _folderLoad.Cancel();
        _folderLoad = new CancellationTokenSource();
        ClearPictures();
        AddPage();
        if (!IsError)
            Status = term.Length == 0
                ? $"{Folders.Count} folder(s) · {_all.Count} picture(s) · {PictureLibrary.FormatSize(_all.Sum(p => p.Size))}"
                : $"{_matching.Count} of {_all.Count} picture(s) match \"{term}\"";
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Adds the next <see cref="PageSize"/> matching pictures and starts loading their thumbnails.</summary>
    private void AddPage()
    {
        var token = _folderLoad.Token;
        foreach (var file in _matching.Skip(Pictures.Count).Take(PageSize))
        {
            var item = new PictureItemViewModel(file);
            Pictures.Add(item);
            _ = item.LoadThumbnailAsync(token);
        }
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(MoreLabel));
    }

    /// <summary>Empties the grid and releases the thumbnails.</summary>
    private void ClearPictures()
    {
        SelectedPicture = null;
        foreach (var item in Pictures)
            item.Dispose();
        Pictures.Clear();
    }

    /// <summary>Decodes the selected picture for the preview (the previous one is released).</summary>
    private async Task LoadPreviewAsync(PictureItemViewModel? item)
    {
        _previewLoad?.Cancel();
        var previous = Preview;
        Preview = null;
        previous?.Dispose();
        Dimensions = string.Empty;
        if (item is null || !item.CanDecode)
            return;

        var cancellation = new CancellationTokenSource();
        _previewLoad = cancellation;
        IsPreviewLoading = true;
        try
        {
            var bitmap = await PictureLibrary.DecodeAsync(item.File.Path, PreviewWidth, cancellation.Token);
            if (cancellation.IsCancellationRequested || !ReferenceEquals(SelectedPicture, item))
            {
                bitmap?.Dispose();
                return;
            }
            Preview = bitmap;
            Dimensions = bitmap is null ? "Cannot be decoded" : $"{bitmap.PixelSize.Width} × {bitmap.PixelSize.Height} (preview)";
        }
        catch (OperationCanceledException)
        {
            // Another picture was selected.
        }
        finally
        {
            if (ReferenceEquals(_previewLoad, cancellation))
                IsPreviewLoading = false;
        }
    }

    private bool HasSelected() => SelectedPicture is not null;

    /// <summary>Selects the previous picture of the grid.</summary>
    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void Previous() => Step(-1);

    /// <summary>Selects the next picture of the grid.</summary>
    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void Next() => Step(1);

    private void Step(int direction)
    {
        if (SelectedPicture is not { } current || Pictures.Count == 0)
            return;
        var index = (Pictures.IndexOf(current) + direction + Pictures.Count) % Pictures.Count;
        SelectedPicture = Pictures[index];
    }

    /// <summary>True while a rotation is being saved (the rotate buttons wait).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RotateLeftCommand), nameof(RotateRightCommand))]
    private bool _isRotating;

    private bool CanRotate() => SelectedPicture is { } picture && !IsRotating && PictureLibrary.CanRotate(picture.File.Path);

    /// <summary>Rotates the selected picture a quarter turn to the left and saves it.</summary>
    [RelayCommand(CanExecute = nameof(CanRotate))]
    private Task RotateLeftAsync() => RotateAsync(270);

    /// <summary>Rotates the selected picture a quarter turn to the right and saves it.</summary>
    [RelayCommand(CanExecute = nameof(CanRotate))]
    private Task RotateRightAsync() => RotateAsync(90);

    /// <summary>
    /// Rotates the selected picture by <paramref name="degrees"/> clockwise, saves it in place, then shows its new
    /// thumbnail and preview (it stays selected, at the same place in the grid).
    /// </summary>
    private async Task RotateAsync(int degrees)
    {
        if (SelectedPicture is not { } picture)
            return;
        IsRotating = true;
        IsError = false;
        Status = $"Rotating {picture.Name}…";
        try
        {
            await PictureLibrary.RotateAsync(picture.File.Path, degrees);
            var info = new FileInfo(picture.File.Path);
            var rotated = new PictureItemViewModel(new PictureFile(info.FullName, info.Length, info.LastWriteTime));

            // The picture keeps its place in the grid (the sort is not applied again until the folder is reloaded).
            var index = Pictures.IndexOf(picture);
            var matchIndex = _matching.FindIndex(p => p.Path == picture.File.Path);
            if (matchIndex >= 0)
                _matching[matchIndex] = rotated.File;
            _all = _all.Select(p => p.Path == picture.File.Path ? rotated.File : p).ToList();
            if (index >= 0)
                Pictures[index] = rotated;
            picture.Dispose();
            SelectedPicture = rotated;
            _ = rotated.LoadThumbnailAsync(_folderLoad.Token);

            Status = $"{picture.Name} rotated {(degrees == 90 ? "right" : degrees == 270 ? "left" : $"{degrees}°")} and saved";
            ActivityLog.Info("Pictures", $"Rotated {degrees}° and saved: {picture.File.Path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            IsError = true;
            Status = $"Not rotated: {ex.Message}";
            ActivityLog.Error("Pictures", $"Rotation of {picture.File.Path} failed", ex);
        }
        finally
        {
            IsRotating = false;
        }
    }

    /// <summary>Opens the selected picture with the default application of the system.</summary>
    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void OpenInSystem() => Run("open", () => PictureLibrary.OpenWithSystem(SelectedPicture!.File.Path));

    /// <summary>Shows the selected picture in the Finder / Explorer.</summary>
    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void Reveal() => Run("reveal", () => PictureLibrary.Reveal(SelectedPicture!.File.Path));

    /// <summary>Opens the current folder in the Finder / Explorer.</summary>
    [RelayCommand]
    private void OpenFolderInSystem() => Run("open the folder", () => PictureLibrary.OpenWithSystem(CurrentDirectory));

    private bool CanRename() => SelectedPicture is { } picture && !string.IsNullOrWhiteSpace(NewName) && NewName.Trim() != picture.Name;

    /// <summary>Renames the selected picture (same folder; the name must be free and valid).</summary>
    [RelayCommand(CanExecute = nameof(CanRename))]
    private void Rename()
    {
        var picture = SelectedPicture!;
        var name = NewName.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
        {
            IsError = true;
            Status = $"\"{name}\" is not a valid file name";
            return;
        }
        if (Path.GetExtension(name).Length == 0)
            name += Path.GetExtension(picture.Name);
        var target = Path.Combine(Path.GetDirectoryName(picture.File.Path)!, name);
        if (File.Exists(target))
        {
            IsError = true;
            Status = $"A file named \"{name}\" already exists here";
            return;
        }

        try
        {
            File.Move(picture.File.Path, target);
            ActivityLog.Info("Pictures", $"Renamed {picture.File.Path} -> {name}");
            Load();
            SelectedPicture = Pictures.FirstOrDefault(p => p.File.Path == target);
            Status = $"Renamed to {name}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IsError = true;
            Status = $"Not renamed: {ex.Message}";
        }
    }

    /// <summary>
    /// Moves <paramref name="files"/> (pictures of the library, dragged onto a folder) into <paramref name="folder"/>:
    /// a name already taken there gets -2, -3…; a picture already in that folder, or outside the library, stays.
    /// </summary>
    public void MoveToFolder(IReadOnlyList<string> files, PictureFolder folder)
    {
        var target = Path.GetFullPath(folder.Path);
        if (!Directory.Exists(target) || !InLibrary(target))
            return;
        var index = SelectedPicture is { } selected ? Pictures.IndexOf(selected) : -1;
        var moved = new List<string>();
        foreach (var file in files.Select(Path.GetFullPath).Distinct())
        {
            if (!File.Exists(file) || !InLibrary(file) || !PictureLibrary.IsPicture(file)
                || string.Equals(Path.GetDirectoryName(file), target, StringComparison.OrdinalIgnoreCase))
                continue;
            var name = Path.GetFileNameWithoutExtension(file);
            var extension = Path.GetExtension(file);
            var destination = Path.Combine(target, name + extension);
            for (var i = 2; File.Exists(destination); i++)
                destination = Path.Combine(target, $"{name}-{i}{extension}");
            try
            {
                File.Move(file, destination);
                moved.Add(Path.GetFileName(destination));
                ActivityLog.Info("Pictures", $"Moved {file} -> {destination}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                IsError = true;
                Status = $"{Path.GetFileName(file)} not moved: {ex.Message}";
                ActivityLog.Error("Pictures", $"{file} not moved to {target}", ex);
            }
        }
        if (moved.Count == 0)
            return;
        Load();
        if (index >= 0 && Pictures.Count > 0)
            SelectedPicture = Pictures[Math.Min(index, Pictures.Count - 1)];
        IsError = false;
        Status = moved.Count == 1 ? $"\"{moved[0]}\" moved to {folder.Name}" : $"{moved.Count} pictures moved to {folder.Name}";
    }

    /// <summary>True when <paramref name="path"/> is the root of the library or in it.</summary>
    private bool InLibrary(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var root = RootDirectory.TrimEnd(Path.DirectorySeparatorChar);
        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Moves the selected picture to the trash of the system, after confirmation.</summary>
    [RelayCommand(CanExecute = nameof(HasSelected))]
    private async Task DeleteAsync()
    {
        var picture = SelectedPicture!;
        if (!await ConfirmAsync($"Move \"{picture.Name}\" to the trash?\n\nIt can be restored from the trash.", "Move to trash"))
            return;
        try
        {
            if (!PictureLibrary.MoveToTrash(picture.File.Path))
            {
                IsError = true;
                Status = "No trash found on this system: the picture was kept";
                return;
            }
            var index = Pictures.IndexOf(picture);
            ActivityLog.Info("Pictures", $"Moved to the trash: {picture.File.Path}");
            Load();
            if (Pictures.Count > 0)
                SelectedPicture = Pictures[Math.Min(index, Pictures.Count - 1)];
            Status = $"\"{picture.Name}\" moved to the trash";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IsError = true;
            Status = $"Not moved to the trash: {ex.Message}";
        }
    }

    private void Run(string action, Action run)
    {
        try
        {
            run();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            IsError = true;
            Status = $"Cannot {action}: {ex.Message}";
        }
    }

    /// <inheritdoc />
    public string NoteWindowName => "Pictures";

    /// <inheritdoc />
    public object? GetNoteContext() => new
    {
        folder = CurrentDirectory,
        pictures = _all.Count,
        search = SearchText,
        selected = SelectedPicture?.Name
    };

    /// <inheritdoc />
    public void Dispose()
    {
        _folderLoad.Cancel();
        _previewLoad?.Cancel();
        _metadataLoad?.Cancel();
        ClearPictures();
        Preview?.Dispose();
        Preview = null;
    }
}
