using System;
using System.Threading.Tasks;

namespace KAONOMI.Chat.ViewModels;

/// <summary>
/// The question asked before a change that cannot be undone (a delete, a change spread to other entries): set once by
/// the application (a dialog), and the default <c>ConfirmAsync</c> of every view model, so that one created without a
/// window of its own (the ontology behind <c>/relation</c>, for one) still asks. Until it is set, the answer is no.
/// </summary>
public static class Confirmations
{
    /// <summary>Asks the user <c>(message, label of the confirming button)</c>; true when the user confirms.</summary>
    public static Func<string, string, Task<bool>> Ask { get; set; } = (_, _) => Task.FromResult(false);
}
