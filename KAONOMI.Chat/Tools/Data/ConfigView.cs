using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Threading;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// /config: the editor of config.yml, and its YAML colours (keys, punctuation, comments, booleans, numbers, URLs,
/// texts) in the coloured view (YAML COLOURS, read-only). Header: the file, the Presets drop-down (voice and
/// animation: written into config.yml, saved and applied at once; the description of each preset as tooltip), SAVE
/// (also Ctrl / ⌘ + S) and RELOAD. Status bar: "● modified" and the status (red on an error).
/// </summary>
[ToolView(typeof(ConfigWindowViewModel), "Edit config.yml", "⚙", 900, 760)]
public sealed class ConfigView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (ConfigWindowViewModel)viewModel;

        var path = DataUi.Tip(DataUi.Text(vm.FilePath ?? string.Empty, Ui.Muted, 11), vm.FilePath);

        // The presets, read again when the store changes (a preset saved or deleted in /preset or /remote).
        var presets = new ComboBox
        {
            MinWidth = 150,
            PlaceholderText = "Presets",
            ItemTemplate = new FuncDataTemplate<ChatPreset>((p, _) =>
            {
                var text = new TextBlock { Text = p?.Name ?? string.Empty };
                if (p is not null && !string.IsNullOrWhiteSpace(p.Description))
                    ToolTip.SetTip(text, p.Description);
                return text;
            })
        };
        ToolTip.SetTip(presets, "Voice and animation preset: written into config.yml, saved and applied at once");
        var updating = false;
        void Select()
        {
            updating = true;
            presets.SelectedItem = vm.SelectedPreset is { } selected ? vm.Presets.FirstOrDefault(p => ReferenceEquals(p, selected) || p.Name == selected.Name) : null;
            updating = false;
        }
        void Fill()
        {
            updating = true;
            presets.ItemsSource = vm.Presets.ToList();
            updating = false;
            Select();
        }
        presets.SelectionChanged += (_, _) =>
        {
            if (!updating && presets.SelectedItem is ChatPreset preset)
                vm.SelectedPreset = preset;
        };
        Fill();
        Ui.When(presets, vm, Select, nameof(vm.SelectedPreset));
        Action changed = () => Ui.OnMain(Fill);
        DataUi.Subscribe(presets, () => PresetStore.Changed += changed, () => PresetStore.Changed -= changed);

        // The text, and its colours.
        var editor = CodeSyntax.Editor();
        editor.Bind(TextBox.TextProperty, vm, nameof(vm.Text), Avalonia.Data.BindingMode.TwoWay);
        DataUi.Keys(editor, e =>
        {
            if (e.Key != Key.S || !DataUi.IsCommand(e))
                return false;
            Ui.Run(vm.SaveCommand);
            return true;
        });
        DataUi.FocusWhenShown(editor);
        var coloured = CodeSyntax.View();
        coloured.IsVisible = false;
        var colours = new CheckBox { Content = "YAML colours" };
        ToolTip.SetTip(colours, "The text in the YAML colours (read-only); untick to edit it");
        colours.IsCheckedChanged += (_, _) =>
        {
            var on = colours.IsChecked == true;
            if (on)
                coloured.ShowYaml(vm.Text ?? string.Empty);
            coloured.IsVisible = on;
            editor.IsVisible = !on;
            if (!on)
                Dispatcher.UIThread.Post(() => editor.Focus(), DispatcherPriority.Background);
        };
        Ui.When(coloured, vm, () =>
        {
            if (coloured.IsVisible)
                coloured.ShowYaml(vm.Text ?? string.Empty);
        }, nameof(vm.Text));

        var header = DataUi.Toolbar(DataUi.Bar([DataUi.Caption("config.yml", Ui.TextBright, 14)], path,
            colours,
            presets,
            DataUi.Command("SAVE", vm.SaveCommand, "Saves config.yml (the previous version is kept as config-YYYYMMDD.config) · Ctrl / ⌘ + S", primary: true),
            DataUi.Command("RELOAD", vm.ReloadCommand, "Reads config.yml again (the changes not saved are dropped)")));

        var modified = DataUi.Text("● modified", DataUi.Amber, 12).Visible(vm, nameof(vm.IsModified));
        var status = DataUi.ErrorColor(DataUi.Bound(vm, nameof(vm.Status), Ui.Muted, 12), vm, nameof(vm.IsError));
        var footer = DataUi.Card(DataUi.Bar([modified], status), padding: 4);

        var body = new Panel { Margin = new Thickness(8, 6) };
        body.Children.Add(editor);
        body.Children.Add(coloured);
        return Ui.Dock((header, Dock.Top), (footer, Dock.Bottom), (body, Dock.Top));
    }
}
