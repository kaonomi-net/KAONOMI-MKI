using Avalonia.Controls;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Knowledge;

/// <summary>/infra: the infrastructure diagrams, the view of the BPMN diagrams (its view model is one of theirs), as in the Godot chat.</summary>
[ToolView(typeof(InfraWindowViewModel), "Infrastructure", "🏗", 1240, 780)]
public sealed class InfraView : IToolView
{
    public Control Build(object viewModel, Shell shell) => new BpmnView().Build(viewModel, shell);
}
