using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Data;

/// <summary>
/// /preset (/presets): the editor of the presets of the avatar stored in the search engine. Header: a search, ↻, NEW,
/// DELETE, ▶ APPLY (to the running chat, and config.yml for the next start), 💾 SAVE. Left: the presets (name,
/// animation and voice, description), a click opens one. Right: the name, the description, the French voice and the
/// animation (drop-downs), the effects of the voice (sliders), the system prompt (the personality). Under it the
/// default preset of config.yml (edited in /config). Status bar: "● not saved" and the status (red on an error).
/// This chat has no 3D: the animation chosen is named, not shown alive.
/// </summary>
[ToolView(typeof(PresetWindowViewModel), "Presets", "🎭", 1120, 740)]
public sealed class PresetView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (PresetWindowViewModel)viewModel;

        // Header.
        var search = DataUi.Line(vm, nameof(vm.Search), "Search the presets (name, description, voice, animation, personality)");
        search.Width = 320;
        var header = DataUi.Toolbar(DataUi.Bar([DataUi.Caption("Presets", Ui.TextBright, 14), search,
                DataUi.Command("↻", vm.RefreshCommand, "Read the presets of the search engine again")], null,
            DataUi.Command("✚ NEW", vm.NewCommand, "A new preset, started from the voice, effects and animation in use in the chat"),
            DataUi.Command("🗑 DELETE", vm.DeleteCommand, "Mark the preset chosen deleted (it stays in the search engine, out of the lists)"),
            DataUi.Command("▶ APPLY", vm.ApplyCommand, "Give the voice, effects, animation and personality of the fields to the running chat (and to config.yml for the next start); the system prompt of the preset becomes the system prompt of the chat"),
            DataUi.Command("💾 SAVE", vm.SaveCommand, "Write the preset to the search engine (a new version of a stored preset of the same name)", primary: true)));

        // The list.
        var list = DataUi.ScrollList(vm.Presets, entry => Row(vm, entry), "No preset in the search engine (↻ to read them again, ✚ NEW to write one)", spacing: 4);
        list.Margin = new Thickness(8, 8, 4, 8);

        // The fields.
        var title = DataUi.Bound(vm, nameof(vm.Title), DataUi.Gold, 16);
        var name = DataUi.Line(vm, nameof(vm.Name), "Name (upper case: MARI, JARVIS…)");
        var description = DataUi.Line(vm, nameof(vm.Description), "A few words on the voice and the animation");
        var voice = DataUi.Tip(DataUi.Options(vm.InstalledVoices, vm, nameof(vm.Voice), minWidth: 160), "The French Piper voice (the voices installed in tts.voicesDirectory; /remote downloads more)");
        voice.HorizontalAlignment = HorizontalAlignment.Stretch;
        var animation = DataUi.Tip(DataUi.Options(vm.Animations, vm, nameof(vm.Animation), a => a.ToString().ToUpperInvariant(), 160), "The animation of the band of the chat");
        animation.HorizontalAlignment = HorizontalAlignment.Stretch;
        var animationNote = DataUi.Bound(vm, nameof(vm.Animation), Ui.Muted, 11, wrap: true,
            format: v => v is SceneAnimation.None ? "NONE · no animation, the band shows the clock" : $"{v?.ToString()?.ToUpperInvariant()} · the animation of the band of the chat (not shown here: this chat has no 3D)");

        var effects = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*,50"), RowSpacing = 6, ColumnSpacing = 10 };
        Effect(effects, vm, "Pitch", nameof(vm.Pitch), 0.5, 2, "The pitch of the voice: 0.5 (low) to 2 (high)");
        Effect(effects, vm, "Speed", nameof(vm.Speed), 0.25, 4, "The speed of the voice: 0.25 (slow) to 4 (fast)");
        Effect(effects, vm, "Robot", nameof(vm.Robot), 0, 1, "The robotic effect, 0 to 1");
        Effect(effects, vm, "Vibrato", nameof(vm.Vibrato), 0, 1, "The vibrato, 0 to 1");
        Effect(effects, vm, "Assistant", nameof(vm.Assistant), 0, 1, "The AI assistant sheen (doubling, small-room reverb, presence), 0 to 1");
        Effect(effects, vm, "Horror", nameof(vm.Horror), 0, 1, "The horror-film treatment, 0 to 1");
        var persona = DataUi.Lines(vm, nameof(vm.Persona), 170,
            "The system prompt of the preset (its personality, its instructions): sent while this preset is in use, however it was chosen (the picker, APPLY, CHANGE:NAME in an answer), with the system prompt of the chat added after it; empty for none");
        var defaults = Ui.Label(vm.DefaultInfo ?? string.Empty, Ui.Muted, 11, wrap: true);

        var form = Ui.Column(10,
            title,
            DataUi.Cells(2, 10, 10,
                DataUi.Field("Name", name), DataUi.Field("Description", description),
                DataUi.Field("Voice", voice), DataUi.Field("Animation", animation)),
            animationNote,
            DataUi.Field("Voice effects", effects),
            DataUi.Field("System prompt of the preset", persona),
            defaults);
        form.Margin = new Thickness(12, 8);

        var split = DataUi.SplitH(list, Ui.Scroll(form), 340, 260);

        // Status.
        var status = DataUi.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsBusy));
        if (status.Child is DockPanel row)
        {
            var dirty = DataUi.Text("● not saved", DataUi.Amber, 12).Visible(vm, nameof(vm.IsDirty));
            dirty.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(dirty, Dock.Left);
            row.Children.Insert(row.Children.Count - 1, dirty);
        }

        var page = Ui.Dock((header, Dock.Top), (status, Dock.Bottom), (split, Dock.Top));
        // Out of the desktop for good (closed, not only brought to the front): the view model lets go of the chat.
        page.DetachedFromVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(page) is null)
                vm.Detach();
        }, Avalonia.Threading.DispatcherPriority.Background);
        return page;
    }

    /// <summary>A row of the effects: its name, a slider bound to <paramref name="name"/>, its value.</summary>
    private static void Effect(Grid grid, PresetWindowViewModel vm, string label, string name, double min, double max, string tooltip)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var caption = DataUi.Caption(label);
        var slider = new Slider { Minimum = min, Maximum = max, SmallChange = 0.01, LargeChange = 0.1, VerticalAlignment = VerticalAlignment.Center };
        slider.Bind(RangeBaseValue, vm, name, BindingMode.TwoWay);
        ToolTip.SetTip(slider, tooltip);
        var value = DataUi.Bound(vm, name, Ui.Text, 12, format: v => v is double d ? d.ToString("0.00", CultureInfo.InvariantCulture) : string.Empty);
        value.FontFamily = Ui.Mono;
        value.TextAlignment = TextAlignment.Right;
        Grid.SetRow(caption, row);
        Grid.SetRow(slider, row);
        Grid.SetColumn(slider, 1);
        Grid.SetRow(value, row);
        Grid.SetColumn(value, 2);
        grid.Children.Add(caption);
        grid.Children.Add(slider);
        grid.Children.Add(value);
    }

    private static AvaloniaProperty RangeBaseValue => Avalonia.Controls.Primitives.RangeBase.ValueProperty;

    /// <summary>A preset of the list: a glyph of its animation, its name, its animation and voice, its description; the one chosen outlined in gold.</summary>
    private static Control Row(PresetWindowViewModel vm, PresetEntry entry)
    {
        var animation = Enum.TryParse<SceneAnimation>(entry.Animation, ignoreCase: true, out var parsed) ? parsed : SceneAnimation.Mari;
        var glyphText = animation switch
        {
            SceneAnimation.Jarvis => "◎",
            SceneAnimation.None => "·",
            _ => string.IsNullOrEmpty(entry.Animation) ? "·" : entry.Animation[..1].ToUpperInvariant()
        };
        var glyph = DataUi.Text(glyphText, animation == SceneAnimation.Jarvis ? Ui.Accent : Ui.Muted, 26);
        glyph.Width = 44;
        glyph.TextAlignment = TextAlignment.Center;
        var texts = Ui.Column(2,
            DataUi.Text(entry.Name ?? string.Empty, Ui.TextBright, 13, bold: true),
            DataUi.Caption($"{entry.Animation} · {entry.Voice}", Ui.Muted, 10),
            DataUi.Text(entry.Description ?? string.Empty, Ui.Text, 11));
        var content = Ui.Dock((glyph, Dock.Left), (texts, Dock.Left));
        glyph.Margin = new Thickness(0, 0, 10, 0);
        var row = DataUi.ClickRow(content, () => vm.Selected = entry, null, vm, nameof(vm.Selected),
            () => vm.Selected is { } selected && (ReferenceEquals(selected, entry) || selected.Id is not null && selected.Id == entry.Id), DataUi.Gold);
        row.MinHeight = 60;
        return DataUi.Tip(row, string.IsNullOrEmpty(entry.Persona) ? entry.Description : entry.Persona);
    }
}
