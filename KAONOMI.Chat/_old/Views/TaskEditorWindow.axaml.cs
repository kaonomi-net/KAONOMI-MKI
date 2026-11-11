using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>Popup editing a task of the kanban (<c>/project</c>), opened by a double-click on its card.</summary>
public partial class TaskEditorWindow : Window
{
    /// <summary>Required by the XAML loader; use <see cref="EditAsync"/>.</summary>
    public TaskEditorWindow()
    {
        InitializeComponent();
    }

    /// <summary>Shows <paramref name="editor"/> over <paramref name="owner"/>; true when the task was saved or deleted.</summary>
    public static async Task<bool> EditAsync(Window owner, TaskEditorViewModel editor)
    {
        var window = new TaskEditorWindow { DataContext = editor };
        editor.CloseRequested += changed => window.Close(changed);
        editor.ConfirmAsync = (message, confirmLabel) => ConfirmDialog.AskAsync(window, message, confirmLabel);
        return await window.ShowDialog<bool?>(owner) == true;
    }

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        NameBox.Focus();
    }
}
