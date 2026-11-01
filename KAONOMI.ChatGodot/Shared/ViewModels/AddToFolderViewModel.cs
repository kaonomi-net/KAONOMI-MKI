using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// "Add to a folder" popup (📁+ in the header of a window): the window, with its data and a picture of it, goes into
/// a folder chosen among the existing ones, or into a new folder given a name, a description and a security level.
/// </summary>
public partial class AddToFolderViewModel : ViewModelBase
{
    private readonly string _context;
    private readonly byte[]? _picture;

    /// <summary>Prepares the adding of the window <paramref name="windowName"/>, its JSON <paramref name="context"/> and its PNG <paramref name="picture"/>.</summary>
    public AddToFolderViewModel(string windowName, string context, byte[]? picture)
    {
        WindowName = windowName;
        _context = context;
        _picture = picture;
        foreach (var folder in FolderStore.List())
            Folders.Add(folder);
        SelectedFolder = Folders.FirstOrDefault();
        // No folder yet: the popup opens on the making of one.
        IsNewFolder = Folders.Count == 0;
    }

    /// <summary>Design-time constructor.</summary>
    public AddToFolderViewModel() : this("Kaonomi Chat", "{ \"window\": \"Kaonomi Chat\" }", null)
    {
    }

    /// <summary>Window added to the folder.</summary>
    public string WindowName { get; }

    /// <summary>Whether a picture of the window goes with its data.</summary>
    public bool HasPicture => _picture is { Length: > 0 };

    /// <summary>The existing folders, by name.</summary>
    public ObservableCollection<WindowFolder> Folders { get; } = [];

    /// <summary>The security levels a new folder can have.</summary>
    public string[] SecurityLevels { get; } = FolderStore.SecurityLevels.ToArray();

    /// <summary>True: a new folder is made (name, description, level); false: an existing folder is chosen.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private bool _isNewFolder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private WindowFolder? _selectedFolder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private string _newName = string.Empty;

    [ObservableProperty]
    private string _newDescription = string.Empty;

    [ObservableProperty]
    private string _newSecurity = "internal";

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isError;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    private bool _isDone;

    /// <summary>Raised when the popup must close (once the window is in its folder).</summary>
    public event Action? CloseRequested;

    private bool CanAdd() => !IsDone && (IsNewFolder ? !string.IsNullOrWhiteSpace(NewName) : SelectedFolder is not null);

    /// <summary>Makes the new folder when asked, then writes the window into the folder and closes.</summary>
    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        IsError = false;
        try
        {
            var folder = SelectedFolder;
            if (IsNewFolder)
            {
                var name = NewName.Trim();
                if (Folders.Any(f => string.Equals(f.Name, name, StringComparison.CurrentCultureIgnoreCase)))
                {
                    IsError = true;
                    Status = $"A folder named \"{name}\" exists already: choose it in Existing folder.";
                    return;
                }
                folder = FolderStore.Create(name, NewDescription, NewSecurity);
                Folders.Add(folder);
            }
            if (folder is null)
                return;
            FolderStore.AddWindow(folder, WindowName, _context, _picture);
            ActivityLog.Info("Folders", $"{WindowName} added to the folder {folder.Name}");
            Status = $"Added to {folder.Name} ({folder.SecurityClassification}).";
            IsDone = true;
            CloseRequested?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            IsError = true;
            Status = $"Not saved: {ex.Message}";
        }
    }
}
