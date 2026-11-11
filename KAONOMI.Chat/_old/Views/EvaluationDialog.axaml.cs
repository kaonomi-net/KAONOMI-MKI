using System;
using Avalonia.Controls;
using KAONOMI.Chat.ViewModels;

namespace KAONOMI.Chat.Views;

/// <summary>Like / dislike popup: explanation text box and Send button; closes itself after the server's reply.</summary>
public partial class EvaluationDialog : Window
{
    /// <summary>Initializes the view; Cancel closes without sending.</summary>
    public EvaluationDialog()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => Close();
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is EvaluationDialogViewModel viewModel)
            viewModel.CloseRequested += Close;
    }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        ExplanationBox.Focus();
    }
}
