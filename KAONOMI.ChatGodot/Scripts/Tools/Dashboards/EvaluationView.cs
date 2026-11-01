using System;
using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;

namespace KAONOMI.ChatGodot.Tools.Dashboards;

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

        var thumb = EveStyle.Text_(vm.IsNegative ? "👎" : "👍", EveStyle.Cyan, 22);
        var question = EveStyle.Text_(vm.Question, EveStyle.TextBright, 16, wrap: true);
        question.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var answer = EveStyle.Text_(vm.AnswerPreview, EveStyle.Muted, 12, wrap: true);
        answer.MaxLinesVisible = 5;
        answer.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var answerBox = DashUi.Card(answer, padding: 8);

        var explanation = Ui.Lines(vm, nameof(vm.Explanation), 140, "Explain your evaluation (optional)");
        explanation.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        Bind.Enabled(explanation, vm, nameof(vm.IsSent), invert: true);
        explanation.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.Enter or Key.KpEnter } key && (key.CtrlPressed || key.MetaPressed))
            {
                explanation.AcceptEvent();
                Bind.Run(vm.SendCommand);
            }
        };

        var status = Ui.Label(vm, nameof(vm.Status), EveStyle.Text, 12, wrap: true);
        status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        Bind.When(status, vm, () => status.AddThemeColorOverride("font_color", vm.IsError ? EveStyle.Red : EveStyle.Text), nameof(vm.IsError));
        var cancel = Ui.Action("CANCEL", () => shell.Close(vm));
        cancel.CustomMinimumSize = new Vector2(90, 0);
        Bind.Enabled(cancel, vm.SendCommand, "IsRunning", invert: true);
        var send = Ui.Command("SEND", vm.SendCommand, primary: true, tooltip: "Send the evaluation (Ctrl / ⌘ + Enter)");
        send.CustomMinimumSize = new Vector2(90, 0);

        var page = Ui.Margin(Ui.Column(12,
            Ui.Row(10, thumb, question),
            answerBox,
            explanation,
            Ui.Row(8, DashUi.Dot(EveStyle.Red, vm, nameof(vm.IsError)), DashUi.Busy(vm.SendCommand, 40), status, cancel, send)), 16, 14, 16, 14);

        // Closed by the view model once the reply has been shown.
        Action close = () => Bind.OnMain(() => shell.Close(vm));
        vm.CloseRequested += close;
        Bind.OnGone(page, () => vm.CloseRequested -= close);

        DashUi.TitleWhenOpen(page, shell, vm.Title);
        page.Ready += () => explanation.CallDeferred(Control.MethodName.GrabFocus);
        return page;
    }
}
