using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// <c>/folders</c> (or <c>/folder</c>): the folders of windows made with 📁+, searched by name, description and
/// security level; a double-click on a folder (or OPEN) opens every window kept in it.
/// </summary>
public partial class FoldersWindowViewModel : ViewModelBase
{
    private List<WindowFolder> _all = [];

    public FoldersWindowViewModel() => Refresh();

    /// <summary>The folders matching <see cref="Term"/>, by name.</summary>
    public ObservableCollection<WindowFolder> Folders { get; } = [];

    /// <summary>Words searched (all of them, in any order) in the name, the description and the level of a folder.</summary>
    [ObservableProperty]
    private string _term = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private WindowFolder? _selectedFolder;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>Raised to open the windows of a folder (the view opens one window per item).</summary>
    public event Action<WindowFolder, IReadOnlyList<FolderItem>>? OpenRequested;

    partial void OnTermChanged(string value) => Filter();

    /// <summary>Reads the folders again from the disk.</summary>
    [RelayCommand]
    private void Refresh()
    {
        _all = FolderStore.List();
        Filter();
    }

    private bool CanOpen() => SelectedFolder is not null;

    /// <summary>Opens every window kept in the selected folder.</summary>
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open()
    {
        if (SelectedFolder is not { } folder)
            return;
        var items = FolderStore.Items(folder);
        Status = items.Count == 0
            ? $"{folder.Name} is empty."
            : $"{folder.Name}: {items.Count} window{(items.Count == 1 ? "" : "s")} opened.";
        if (items.Count > 0)
            OpenRequested?.Invoke(folder, items);
    }

    private void Filter()
    {
        var words = Term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var selected = SelectedFolder;
        Folders.Clear();
        foreach (var folder in _all.Where(f => words.All(w =>
                     f.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                     || f.Description.Contains(w, StringComparison.CurrentCultureIgnoreCase)
                     || f.SecurityClassification.Contains(w, StringComparison.CurrentCultureIgnoreCase))))
            Folders.Add(folder);
        SelectedFolder = Folders.FirstOrDefault(f => f.Id == selected?.Id) ?? Folders.FirstOrDefault();
        Status = _all.Count == 0
            ? "No folder yet: 📁+ in the header of a window adds it to a new folder."
            : $"{Folders.Count} of {_all.Count} folder{(_all.Count == 1 ? "" : "s")}";
    }
}

/// <summary>A window kept in a folder, opened again: its picture and its data, read only.</summary>
public sealed class FolderItemViewModel(WindowFolder folder, FolderItem item) : ViewModelBase
{
    public WindowFolder Folder { get; } = folder;

    public FolderItem Item { get; } = item;
}
