using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// The evaluation of an answer (👍 / 👎 of the chat): the thumb and the question, the beginning of the answer, the
/// explanation (optional; disabled once sent), the status (busy, red dot), CANCEL (closes without sending) and SEND
/// (also Ctrl / ⌘ + Enter). The window closes itself when the view model asks it (2 s after the server's reply); the
/// chat then reads <see cref="EvaluationDialogViewModel.Succeeded"/>. The window takes the title of the view model.
/// </summary>
[ToolView(typeof(EvaluationDialogViewModel), "Evaluation", "⭐", 560, 470, PerObject = true)]
public sealed class EvaluationView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (EvaluationDialogViewModel)viewModel;

        var thumb = DataUi.Text(vm.IsNegative ? "👎" : "👍", Ui.Accent, 22);
        var question = Ui.Label(vm.Question ?? string.Empty, Ui.TextBright, 16, wrap: true);
        var answer = new TextBlock
        {
            Text = vm.AnswerPreview ?? string.Empty,
            Foreground = Ui.Muted,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 5,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var answerBox = DataUi.Card(answer, padding: 8);

        var explanation = DataUi.FocusWhenShown(DataUi.Lines(vm, nameof(vm.Explanation), 140, "Explain your evaluation (optional)"));
        DataUi.Enabled(explanation, vm, nameof(vm.IsSent), invert: true);
        DataUi.CtrlEnter(explanation, () => Ui.Run(vm.SendCommand));

        var status = DataUi.ErrorColor(DataUi.Bound(vm, nameof(vm.Status), Ui.Text, 12, wrap: true), vm, nameof(vm.IsError), Ui.Text);
        var cancel = DataUi.Action("CANCEL", () => shell.Close(vm));
        cancel.MinWidth = 90;
        DataUi.Enabled(cancel, vm.SendCommand, "IsRunning", invert: true);
        var send = DataUi.Command("SEND", vm.SendCommand, "Send the evaluation (Ctrl / ⌘ + Enter)", primary: true);
        send.MinWidth = 90;

        var top = Ui.Column(12, DataUi.Bar([thumb], question), answerBox);
        top.Margin = new Thickness(0, 0, 0, 12);
        var bottom = DataUi.Bar([DataUi.Dot(Ui.Red, vm, nameof(vm.IsError)), DataUi.Busy(vm.SendCommand, 40)], status, cancel, send);
        bottom.Margin = new Thickness(0, 12, 0, 0);
        var page = Ui.Dock((top, Dock.Top), (bottom, Dock.Bottom), (explanation, Dock.Top));
        page.Margin = new Thickness(16, 14);

        // Closed by the view model once the reply has been shown.
        Action close = () => Ui.OnMain(() => shell.Close(vm));
        DataUi.Subscribe(page, () => vm.CloseRequested += close, () => vm.CloseRequested -= close);

        DataUi.TitleWhenOpen(page, vm.Title);
        return page;
    }
}
