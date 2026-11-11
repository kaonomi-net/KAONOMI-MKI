using System.ComponentModel;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// Implemented by the view model of every window that edits a document: the document it shows can be attached to a
/// project. The header of the window then shows a selector of the projects, by name (the id is never shown); what is
/// chosen there is written in <see cref="ProjectId"/>, kept with the document (<c>projectId</c>) when it is saved.
/// <para>
/// The view model raises <see cref="INotifyPropertyChanged.PropertyChanged"/> for <see cref="ProjectId"/> and
/// <see cref="HasProjectDocument"/> when another document is shown, so the selector follows.
/// </para>
/// </summary>
public interface IProjectDocument : INotifyPropertyChanged
{
    /// <summary>The id of the project of the document shown; null: none.</summary>
    string? ProjectId { get; set; }

    /// <summary>False while the window shows no document (nothing selected): the selector is then disabled.</summary>
    bool HasProjectDocument { get; }
}
