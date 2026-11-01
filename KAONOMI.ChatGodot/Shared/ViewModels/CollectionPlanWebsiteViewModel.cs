using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using KAONOMI.Chat.Models;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// Editable website of a collection plan, with its URLs; every change (of its name, of its URLs, a URL added or
/// removed) calls <c>changed</c> so that the plan becomes dirty.
/// </summary>
public partial class CollectionPlanWebsiteViewModel : ViewModelBase
{
    private readonly Action _changed;

    public CollectionPlanWebsiteViewModel(WebsiteEntry entry, Action changed)
    {
        _changed = changed;
        _name = entry.Name ?? string.Empty;
        foreach (var url in entry.UrlProcesses ?? [])
            UrlProcesses.Add(new CollectionPlanUrlViewModel(url, changed));
        UrlProcesses.CollectionChanged += (_, _) => _changed();
    }

    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>The URLs of the website.</summary>
    public ObservableCollection<CollectionPlanUrlViewModel> UrlProcesses { get; } = [];

    /// <summary>Adds a blank URL (mode <c>fixed</c>).</summary>
    public void AddUrl() => UrlProcesses.Add(new CollectionPlanUrlViewModel(new UrlProcessEntry { Mode = UrlProcessEntry.Modes[0] }, _changed));

    public void RemoveUrl(CollectionPlanUrlViewModel url) => UrlProcesses.Remove(url);

    /// <inheritdoc />
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(Name))
            _changed();
    }

    internal WebsiteEntry ToEntry() => new()
    {
        Name = Name.Trim(),
        UrlProcesses = UrlProcesses.Select(u => u.ToEntry()).ToList()
    };
}

/// <summary>Editable URL of a website: the address, the mode of the scraper and the keywords.</summary>
public partial class CollectionPlanUrlViewModel : ViewModelBase
{
    private readonly Action _changed;

    public CollectionPlanUrlViewModel(UrlProcessEntry entry, Action changed)
    {
        _changed = changed;
        _url = entry.Url ?? string.Empty;
        _mode = UrlProcessEntry.Modes.Contains(entry.Mode) ? entry.Mode! : UrlProcessEntry.Modes[0];
        _keywords = string.Join(", ", entry.Keywords ?? []);
    }

    /// <summary>The modes offered by the drop-down.</summary>
    public static string[] Modes => UrlProcessEntry.Modes;

    [ObservableProperty]
    private string _url = string.Empty;

    /// <summary>fixed, explore or random.</summary>
    [ObservableProperty]
    private string _mode = UrlProcessEntry.Modes[0];

    /// <summary>The keywords, separated by commas.</summary>
    [ObservableProperty]
    private string _keywords = string.Empty;

    /// <inheritdoc />
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Url) or nameof(Mode) or nameof(Keywords))
            _changed();
    }

    internal UrlProcessEntry ToEntry() => new()
    {
        Url = Url.Trim(),
        Mode = Mode,
        Keywords = Keywords.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList()
    };
}
