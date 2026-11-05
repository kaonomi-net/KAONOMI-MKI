using System.Linq;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;
using EveStyle = KAONOMI.ChatGodot.Eve.Eve;
using FaceAvatar = KAONOMI.ChatGodot.FaceAvatar;
using JarvisAvatar = KAONOMI.ChatGodot.JarvisAvatar;

namespace KAONOMI.ChatGodot.Tools.Dashboards;

/// <summary>
/// /preset (/presets): the editor of the presets of the avatar stored in the search engine. Header: a search, ↻, NEW,
/// DELETE, ▶ APPLY (to the running chat, and config.yml for the next start), 💾 SAVE. Left: the presets (name,
/// animation and voice, description), a click opens one. Right: the name, the description, the French voice and the 3D
/// animation (drop-downs) with the animation itself, alive, beside them (the 3D face, or the JARVIS display), the
/// effects of the voice (sliders), the system prompt (the personality). Under it the default preset of config.yml
/// (edited in /config). Each preset of the list shows the picture of its face. Status bar: "● not saved" and the status
/// (red on an error).
/// </summary>
[ToolView(typeof(PresetWindowViewModel), "Presets", "🎭", 1120, 740)]
public sealed class PresetView : IToolView
{
    private static readonly Color Accent = new("#D4A017");

    public Control Build(object viewModel, Shell shell)
    {
        var vm = (PresetWindowViewModel)viewModel;

        // Header.
        var search = Ui.Line(vm, nameof(vm.Search), "Search the presets (name, description, voice, animation, personality)");
        search.CustomMinimumSize = new Vector2(260, 0);
        var header = Ui.Toolbar(Ui.Row(8,
            EveStyle.Caption("Presets", EveStyle.TextBright, 14),
            search,
            Ui.Command("↻", vm.RefreshCommand, tooltip: "Read the presets of the search engine again"),
            Ui.Spring(),
            Ui.Command("✚ NEW", vm.NewCommand, tooltip: "A new preset, started from the voice, effects and animation in use in the chat"),
            Ui.Command("🗑 DELETE", vm.DeleteCommand, tooltip: "Mark the preset chosen deleted (it stays in the search engine, out of the lists)"),
            Ui.Command("▶ APPLY", vm.ApplyCommand, tooltip: "Give the voice, effects, animation and personality of the fields to the running chat (and to config.yml for the next start); the system prompt of the preset becomes the system prompt of the chat"),
            Ui.Command("💾 SAVE", vm.SaveCommand, primary: true, tooltip: "Write the preset to the search engine (a new version of a stored preset of the same name)")));

        // The list.
        var list = new VBoxContainer();
        list.AddThemeConstantOverride("separation", 4);
        var empty = Ui.Placeholder("No preset in the search engine (↻ to read them again, ✚ NEW to write one)");
        Bind.Items(list, vm.Presets, entry => Row(vm, entry), empty);
        var listPanel = Ui.Column(6, Ui.FillHeight(Ui.Scroll(list)), empty);
        listPanel.CustomMinimumSize = new Vector2(320, 0);

        // The fields.
        var title = Ui.Label(vm, nameof(vm.Title), Accent, 16);
        var name = Ui.Line(vm, nameof(vm.Name), "Name (upper case: MARI, JARVIS…)");
        var description = Ui.Line(vm, nameof(vm.Description), "A few words on the voice and the animation");
        var voice = Bind.Options(new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FitToLongestItem = false, ClipText = true, TooltipText = "The French Piper voice (the voices installed in tts.voicesDirectory; /remote downloads more)" }, vm.InstalledVoices, vm, nameof(vm.Voice));
        var animation = Bind.Options(new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, TooltipText = "The 3D animation of the band of the chat" }, vm.Animations, vm, nameof(vm.Animation), a => a.ToString().ToUpperInvariant());
        // The animation chosen, alive: the 3D face (or the JARVIS display), in a small stage beside the fields.
        var stage = new PanelContainer { CustomMinimumSize = new Vector2(300, 210), ClipContents = true, TooltipText = "The 3D animation of the preset, as the band of the chat shows it" };
        stage.AddThemeStyleboxOverride("panel", EveStyle.Box(new Color(0.015f, 0.022f, 0.03f), EveStyle.Edge with { A = 0.5f }, 10));
        var holder = new Control { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill, ClipContents = true };
        stage.AddChild(holder);
        var stageCaption = EveStyle.Caption(string.Empty, EveStyle.Muted, 10);
        stageCaption.HorizontalAlignment = HorizontalAlignment.Center;
        SceneAnimation? shownAnimation = null;
        void ShowAnimation()
        {
            if (!GodotObject.IsInstanceValid(holder) || shownAnimation == vm.Animation)
                return;
            shownAnimation = vm.Animation;
            foreach (var child in holder.GetChildren())
                child.QueueFree();
            Control view;
            if (vm.Animation == SceneAnimation.Jarvis)
                view = new JarvisAvatar();
            else if (vm.Animation == SceneAnimation.None)
            {
                var none = EveStyle.Text_("NONE · no animation, the band shows the clock", EveStyle.Muted, 12, wrap: true);
                none.HorizontalAlignment = HorizontalAlignment.Center;
                none.VerticalAlignment = VerticalAlignment.Center;
                view = none;
            }
            else
            {
                try
                {
                    view = new FaceAvatar(vm.Animation);
                }
                catch (System.Exception ex) when (ex is System.ArgumentException or System.InvalidOperationException or System.Collections.Generic.KeyNotFoundException)
                {
                    view = new FaceAvatar(SceneAnimation.Kaonomi);
                }
            }
            view.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            holder.AddChild(view);
            stageCaption.Text = vm.Animation.ToString().ToUpperInvariant() + " · 3D animation";
        }
        Bind.When(stage, vm, ShowAnimation, nameof(vm.Animation));
        var stageColumn = Ui.Column(4, stage, stageCaption);
        stageColumn.SizeFlagsHorizontal = Control.SizeFlags.ShrinkCenter;
        var effects = new GridContainer { Columns = 3 };
        effects.AddThemeConstantOverride("h_separation", 10);
        effects.AddThemeConstantOverride("v_separation", 6);
        Effect(effects, vm, "Pitch", nameof(vm.Pitch), 0.5, 2, "The pitch of the voice: 0.5 (low) to 2 (high)");
        Effect(effects, vm, "Speed", nameof(vm.Speed), 0.25, 4, "The speed of the voice: 0.25 (slow) to 4 (fast)");
        Effect(effects, vm, "Robot", nameof(vm.Robot), 0, 1, "The robotic effect, 0 to 1");
        Effect(effects, vm, "Vibrato", nameof(vm.Vibrato), 0, 1, "The vibrato, 0 to 1");
        Effect(effects, vm, "Assistant", nameof(vm.Assistant), 0, 1, "The AI assistant sheen (doubling, small-room reverb, presence), 0 to 1");
        Effect(effects, vm, "Horror", nameof(vm.Horror), 0, 1, "The horror-film treatment, 0 to 1");
        var persona = Ui.Lines(vm, nameof(vm.Persona), 170,
            "The system prompt of the preset (its personality, its instructions): sent while this preset is in use, however it was chosen (the picker, APPLY, CHANGE:NAME in an answer), with the system prompt of the chat added after it; empty for none");
        var defaults = EveStyle.Text_(vm.DefaultInfo, EveStyle.Muted, 11, wrap: true);
        // The fields on the left, the animation alive on the right.
        var fields = Ui.Column(10,
            Ui.Row(10, Ui.Field("Name", name), Ui.Field("Description", description)),
            Ui.Row(10, Ui.Field("Voice", voice), Ui.Field("3D animation", animation)),
            Ui.Field("Voice effects", effects));
        fields.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        var form = Ui.Column(10,
            title,
            Ui.Row(14, fields, stageColumn),
            Ui.Field("System prompt of the preset", persona),
            defaults);
        var formPanel = Ui.Scroll(Ui.Margin(form, 12, 8, 12, 8));
        formPanel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var split = new HSplitContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        split.AddChild(Ui.Margin(listPanel, 8, 8, 4, 8));
        split.AddChild(formPanel);

        // Status.
        var status = Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError), nameof(vm.IsBusy));
        if (status.GetChild(0) is HBoxContainer statusRow)
        {
            var dirty = Bind.Visible(EveStyle.Text_("● not saved", EveStyle.Amber, 12), vm, nameof(vm.IsDirty));
            statusRow.AddChild(dirty);
            statusRow.MoveChild(dirty, statusRow.GetChildCount() - 2);
        }

        var page = Ui.Column(0, header, split, status);
        page.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        Bind.OnGone(page, vm.Detach);
        return page;
    }

    /// <summary>A row of the effects: its name, a slider bound to <paramref name="name"/>, its value.</summary>
    private static void Effect(GridContainer grid, PresetWindowViewModel vm, string label, string name, double min, double max, string tooltip)
    {
        var caption = EveStyle.Caption(label);
        caption.CustomMinimumSize = new Vector2(80, 0);
        caption.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        var slider = Bind.Range(new HSlider { MinValue = min, MaxValue = max, Step = 0.01, TooltipText = tooltip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter }, vm, name);
        var value = Ui.Label(vm, name, EveStyle.Text, 12, format: v => v is double d ? d.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
        value.CustomMinimumSize = new Vector2(44, 0);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.AddThemeFontOverride("font", EveStyle.Mono);
        grid.AddChild(caption);
        grid.AddChild(slider);
        grid.AddChild(value);
    }

    /// <summary>A preset of the list: its name, its animation and voice, its description; the one chosen outlined in gold.</summary>
    private static Control Row(PresetWindowViewModel vm, PresetEntry entry)
    {
        var button = new Button { FocusMode = Control.FocusModeEnum.None, MouseDefaultCursorShape = Control.CursorShape.PointingHand, TooltipText = entry.Persona.Length == 0 ? entry.Description : entry.Persona };
        var name = EveStyle.Text_(entry.Name, EveStyle.TextBright, 13);
        name.AddThemeFontOverride("font", EveStyle.Display);
        var kind = EveStyle.Caption($"{entry.Animation} · {entry.Voice}", EveStyle.Muted, 10);
        var description = EveStyle.Text_(entry.Description, EveStyle.Text with { A = 0.8f }, 11, wrap: true);
        description.ClipText = true;
        description.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        description.AutowrapMode = TextServer.AutowrapMode.Off;
        var texts = Ui.Column(2, name, kind, description);
        texts.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        // The picture of the face of the animation (a glyph for JARVIS and NONE).
        var animation = System.Enum.TryParse<SceneAnimation>(entry.Animation, ignoreCase: true, out var parsed) ? parsed : SceneAnimation.Mari;
        Control picture;
        if (FaceAvatar.PictureOf(animation) is { } face)
            picture = new TextureRect { Texture = face, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, CustomMinimumSize = new Vector2(52, 52) };
        else
        {
            var glyph = EveStyle.Text_(animation == SceneAnimation.Jarvis ? "◎" : "·", animation == SceneAnimation.Jarvis ? EveStyle.Cyan : EveStyle.Muted, 30);
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            glyph.CustomMinimumSize = new Vector2(52, 52);
            picture = glyph;
        }
        picture.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        var content = Ui.Row(10, picture, texts);
        content.MouseFilter = Control.MouseFilterEnum.Ignore;
        foreach (var child in content.FindChildren("*", "Control", recursive: true, owned: false).OfType<Control>())
            child.MouseFilter = Control.MouseFilterEnum.Ignore;
        content.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect, Control.LayoutPresetMode.Minsize, 8);
        button.AddChild(content);
        button.CustomMinimumSize = new Vector2(0, 68);
        void Look()
        {
            var chosen = vm.Selected is { } selected && (ReferenceEquals(selected, entry) || selected.Id is not null && selected.Id == entry.Id);
            button.AddThemeStyleboxOverride("normal", EveStyle.Box(chosen ? Accent with { A = 0.14f } : EveStyle.Field, chosen ? Accent : EveStyle.Edge with { A = 0.4f }, 8));
            button.AddThemeStyleboxOverride("hover", EveStyle.Box(chosen ? Accent with { A = 0.2f } : EveStyle.Field.Lightened(0.06f), chosen ? Accent : EveStyle.Edge, 8));
            button.AddThemeStyleboxOverride("pressed", EveStyle.Box(Accent with { A = 0.25f }, Accent, 8));
        }
        Bind.When(button, vm, Look, nameof(vm.Selected));
        button.Pressed += () => vm.Selected = entry;
        return button;
    }
}
