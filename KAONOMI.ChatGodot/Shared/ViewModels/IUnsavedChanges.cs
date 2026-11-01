namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// A view model whose window can hold changes not saved yet (a page, a diagram, a writing…): the desktop asks before
/// closing it, before closing every window (<c>/closeall</c>) and before the chat quits while <see cref="IsDirty"/>.
/// </summary>
public interface IUnsavedChanges
{
    /// <summary>True when the window has changes not saved.</summary>
    bool IsDirty { get; }
}
