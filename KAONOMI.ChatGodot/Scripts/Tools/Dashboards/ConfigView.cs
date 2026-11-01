using System;
using System.Linq;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;

namespace KAONOMI.ChatGodot.Tools.Dashboards;

/// <summary>
/// /config: the editor of config.yml in the YAML colours (keys, punctuation, comments, booleans, numbers, URLs,
/// texts). Header: the file, the Presets drop-down (voice and animation: written into config.yml, saved and applied at
/// once; the description of each preset as tooltip), SAVE (also Ctrl / ⌘ + S) and RELOAD. Status bar: "● modified" and
/// the status (red on an error).
/// </summary>
[ToolView(typeof(ConfigWindowViewModel), "Edit config.yml", "⚙", 900, 760)]
public sealed class ConfigView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (ConfigWindowViewModel)viewModel;

        var path = EveStyle.Text_(vm.FilePath, EveStyle.Muted, 11);
        path.ClipText = true;
        path.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        path.TooltipText = vm.FilePath;
        path.MouseFilter = Control.MouseFilterEnum.Stop;
        path.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var presets = new OptionButton { CustomMinimumSize = new Vector2(150, 0), TooltipText = "Voice and animation preset: written into config.yml, saved and applied at once" };
        // The presets read again when the store changes (a preset saved or deleted in /preset or /remote).
        void Tooltips()
        {
            var all = vm.Presets;
            for (var i = 0; i < all.Count && i < presets.ItemCount; i++)
                presets.SetItemTooltip(i, all[i].Description);
        }
        Bind.Options(presets, Bind.Live(() => vm.Presets), vm, nameof(vm.SelectedPreset), p => p.Name, refresh: fill =>
        {
            Action changed = () => Bind.OnMain(() =>
            {
                if (!GodotObject.IsInstanceValid(presets))
                    return;
                fill();
                Tooltips();
            });
            PresetStore.Changed += changed;
            Bind.OnGone(presets, () => PresetStore.Changed -= changed);
        });
        Tooltips();
        void Placeholder()
        {
            if (presets.Selected < 0)
                presets.Text = "Presets";
        }
        Bind.When(presets, vm, Placeholder, nameof(vm.SelectedPreset));

        var header = Ui.Toolbar(Ui.Row(10,
            EveStyle.Caption("config.yml", EveStyle.TextBright, 14),
            path,
            presets,
            Ui.Command("SAVE", vm.SaveCommand, primary: true, tooltip: "Saves config.yml (the previous version is kept as config-YYYYMMDD.config) · Ctrl / ⌘ + S"),
            Ui.Command("RELOAD", vm.ReloadCommand, tooltip: "Reads config.yml again (the changes not saved are dropped)")));

        var editor = CodeEditors.Editor();
        editor.SyntaxHighlighter = new YamlSyntaxHighlighter();
        editor.IndentAutomatic = true;
        Bind.Edit(editor, vm, nameof(vm.Text));
        editor.GuiInput += e =>
        {
            if (e is InputEventKey { Pressed: true, Keycode: Key.S } key && (key.CtrlPressed || key.MetaPressed))
            {
                Bind.Run(vm.SaveCommand);
                editor.AcceptEvent();
            }
        };

        var modified = Bind.Visible(EveStyle.Text_("● modified", Color.FromHtml("#E6B450"), 12), vm, nameof(vm.IsModified));
        var status = Ui.Label(vm, nameof(vm.Status), EveStyle.Muted, 12);
        status.ClipText = true;
        status.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        status.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        Bind.When(status, vm, () => status.AddThemeColorOverride("font_color", vm.IsError ? EveStyle.Red : EveStyle.Muted), nameof(vm.IsError));
        var footer = DashUi.Card(Ui.Row(8, modified, status), padding: 4);

        var page = Ui.Column(6, header, Ui.FillHeight(Ui.Margin(editor, 8, 0, 8, 0)), footer);
        page.Ready += () => editor.CallDeferred(Control.MethodName.GrabFocus);
        return page;
    }
}
