using System;
using Avalonia.Controls;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>Anonymous note popup; closes itself 2 seconds after the note was saved.</summary>
public partial class NoteDialog : Window
{
    /// <summary>Initializes the view; Cancel closes without sending.</summary>
    public NoteDialog()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => Close();
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is NoteDialogViewModel viewModel)
            viewModel.CloseRequested += Close;
    }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        NoteBox.Focus();
    }
}
