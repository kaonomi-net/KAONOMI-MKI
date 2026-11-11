using System;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KAONOMI.Chat.ViewModels;

/// <summary>One regular expression of a symbolic pattern, checked as it is typed.</summary>
public partial class RegexPatternItemViewModel : ViewModelBase
{
    /// <summary>Creates a row for <paramref name="pattern"/>.</summary>
    public RegexPatternItemViewModel(string pattern = "")
    {
        _pattern = pattern;
        Validate();
    }

    [ObservableProperty]
    private string _pattern;

    /// <summary>Why the pattern is not a valid .NET regular expression, or null when it is (or empty).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInvalid))]
    private string? _error;

    /// <summary>True when the pattern does not compile.</summary>
    public bool IsInvalid => Error is not null;

    partial void OnPatternChanged(string value) => Validate();

    private void Validate()
    {
        if (string.IsNullOrWhiteSpace(Pattern))
        {
            Error = null;
            return;
        }

        try
        {
            _ = new Regex(Pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            Error = null;
        }
        catch (ArgumentException ex)
        {
            Error = $"Invalid regular expression: {ex.Message}";
        }
    }
}
