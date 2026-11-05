using Godot;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Games;

/// <summary>
/// /game:marie: a first-person game in a maze of 25 × 25 cells, hunted by MarieKilleuse. Header: NEW GAME. Middle:
/// the maze (<see cref="MarieWorld"/>) with the screen over it (<see cref="MarieHud"/>). A click in the maze plays,
/// Esc pauses.
/// </summary>
[ToolView(typeof(MarieWindowViewModel), "MarieKilleuse · the maze", "🔪", 1200, 820)]
public sealed class MarieView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (MarieWindowViewModel)viewModel;
        var world = new MarieWorld(vm);
        var hud = new MarieHud(vm, world);
        hud.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var stage = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, ClipContents = true };
        world.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        stage.AddChild(world);
        stage.AddChild(hud);

        var header = Ui.Toolbar(Ui.Row(10,
            Eve.Eve.Caption("MarieKilleuse", Eve.Eve.TextBright, 14),
            Ui.Fill(Ui.Label(vm, nameof(vm.Status), Eve.Eve.Muted, 12)),
            Ui.Action("✚ NEW GAME", () =>
            {
                world.Release();
                vm.NewGameCommand.Execute(null);
            }, primary: true, tooltip: "A new maze, MarieKilleuse asleep in the middle")));

        var page = Ui.Column(0, header, Ui.FillHeight(stage));
        page.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        return page;
    }

}
