using System.Threading.Tasks;
using Avalonia.Controls;

namespace KAONOMI.Chat.Views;

/// <summary>Modal yes/no dialog returning true when the user confirms.</summary>
public partial class ConfirmDialog : Window
{
    /// <summary>Required by the XAML loader; use <see cref="AskAsync"/>.</summary>
    public ConfirmDialog()
    {
        InitializeComponent();
        ConfirmButton.Click += (_, _) => Close(true);
        CancelButton.Click += (_, _) => Close(false);
    }

    /// <summary>Shows <paramref name="message"/> over <paramref name="owner"/>; false when cancelled or closed.</summary>
    public static async Task<bool> AskAsync(Window owner, string message, string confirmLabel = "Delete")
    {
        var dialog = new ConfirmDialog();
        dialog.MessageText.Text = message;
        dialog.ConfirmButton.Content = confirmLabel;
        return await dialog.ShowDialog<bool?>(owner) == true;
    }
}
