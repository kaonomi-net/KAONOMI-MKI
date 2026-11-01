using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using Godot;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Media;

/// <summary>
/// /remote: remote control of the chatbot. What it says (Say, Ctrl+Enter: shown in the chat, read aloud, the avatar
/// following the voice), or the text as a script (▶ Run script: paragraph by paragraph, a line CHANGE:NAME switches to
/// the preset NAME), Stop (Esc); the scripts of ~/kaonomi/script (Open…, Save — Ctrl+S, asks for a name the first time —,
/// Save as…). The system prompt of the preset (its personality). Voice and animation: start from a preset (Delete for a
/// saved one), the voice (Try), pitch, speed, robot, vibrato, assistant, horror, the 3D animation, all applied live to
/// the chat; Save preset (name, description). Voices: the installed Piper voices (Try, Delete) and the Piper catalog
/// (filter, Reload, Download with its progress, then Try).
/// </summary>
[ToolView(typeof(RemoteWindowViewModel), "Remote", "🎛", 1400, 860)]
public sealed class RemoteView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (RemoteWindowViewModel)viewModel;
        var (sayPanel, sayBox) = SayPanel(vm);
        var left = Ui.SplitV(sayPanel, PersonaPanel(vm), 420);
        left.CustomMinimumSize = new Vector2(260, 0);
        var middle = SettingsPanel(vm);
        middle.CustomMinimumSize = new Vector2(280, 0);
        var right = VoicesPanel(vm);
        right.CustomMinimumSize = new Vector2(300, 0);

        // Three columns, the bars between them movable: the text takes the free width, the settings and the voices get
        // their width of the Avalonia view (360 and 400) when the window is large enough.
        var columns = new HSplitContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        var rest = new HSplitContainer { SizeFlagsHorizontal = Control.SizeFlags.Fill, SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        left.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        middle.SizeFlagsHorizontal = Control.SizeFlags.Fill;
        right.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        rest.AddChild(middle);
        rest.AddChild(right);
        columns.AddChild(left);
        columns.AddChild(rest);
        var placed = false;
        columns.Resized += () =>
        {
            if (placed || columns.Size.X < 100)
                return;
            placed = true;
            var room = columns.Size.X - left.CustomMinimumSize.X - 2 * columns.GetThemeConstant("separation");
            var extra = Math.Clamp(room - rest.GetCombinedMinimumSize().X, 0, 772 + 12 - rest.GetCombinedMinimumSize().X);
            columns.SplitOffsets = [-(int)extra];
            rest.SplitOffsets = [(int)Math.Min(80, extra * 80 / (772 + 12 - 580 + 0.001f))];
        };
        var body = columns;
        var page = Ui.Column(6, Ui.Margin(body, 4, 4, 4, 0), Ui.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError)));
        Ui.FillHeight(page.GetChild<Control>(0));
        page.Ready += () => sayBox.CallDeferred(Control.MethodName.GrabFocus);
        return MediaKeys.Attach(page, key =>
        {
            if (key.Keycode is Key.Enter or Key.KpEnter && (key.CtrlPressed || key.MetaPressed))
                Bind.Run(vm.SayCommand);
            else if (key.Keycode == Key.Escape && !key.CtrlPressed && !key.MetaPressed && !key.ShiftPressed)
                Bind.Run(vm.StopCommand);
            else if (MediaKeys.Command(key, Key.S))
                SaveScript(vm);
            else
                return false;
            return true;
        });
    }

    /// <summary>A panel with a title.</summary>
    private static PanelContainer Panel(string title, Control? beside, params Control[] content)
    {
        var panel = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        panel.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Sunken(0.2f), Eve.Eve.Edge with { A = 0.35f }, 14));
        var header = Ui.Row(8, Eve.Eve.Caption(title, Eve.Eve.TextBright, 13));
        if (beside is not null)
            header.AddChild(beside);
        var column = Ui.Column(8, header);
        foreach (var control in content)
            column.AddChild(control);
        panel.AddChild(column);
        return panel;
    }

    /// <summary>Buttons in rows that wrap when the column is narrow.</summary>
    private static HFlowContainer Flow(params Control[] children)
    {
        var flow = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        flow.AddThemeConstantOverride("h_separation", 8);
        flow.AddThemeConstantOverride("v_separation", 6);
        foreach (var child in children)
            flow.AddChild(child);
        return flow;
    }

    // ── What the chatbot says ──

    private static (Control Panel, TextEdit Box) SayPanel(RemoteWindowViewModel vm)
    {
        var script = Ui.Label(vm, nameof(vm.ScriptName), Eve.Eve.Muted, 11, format: v => $"· {v}");
        script.ClipText = true;
        script.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        script.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        script.MouseFilter = Control.MouseFilterEnum.Pass;
        Bind.When(script, vm, () => script.TooltipText = vm.ScriptPath ?? string.Empty, nameof(vm.ScriptPath));

        var box = Ui.Lines(vm, nameof(vm.Text), 120,
            "Text the chatbot says as its answer: shown in the chat, read aloud with the voice below, the 3D animation following it (Ctrl+Enter). "
            + "As a script (Run): said paragraph by paragraph (paragraphs separated by a blank line); a line CHANGE:MARI switches to the preset MARI");
        box.SizeFlagsVertical = Control.SizeFlags.ExpandFill;

        var actions = Flow(
            Ui.Command("SAY", vm.SayCommand, primary: true, tooltip: "The chatbot says the whole text at once (Ctrl+Enter)"),
            Ui.Command("►  RUN SCRIPT", vm.RunScriptCommand, tooltip: "Says the text paragraph by paragraph; a line CHANGE:NAME switches to the preset NAME (an unknown preset keeps the current one)"),
            Ui.Check("5 s DELAY", vm, nameof(vm.DelayStart), $"Run script waits {RemoteWindowViewModel.StartDelaySeconds} seconds before the presentation starts (a countdown in the status; Stop ends it)"),
            Ui.Command("STOP", vm.StopCommand, tooltip: "Stops the voice and the script (Esc)"),
            Ui.Action("OPEN…", () => OpenScript(vm), tooltip: "Opens a script (.txt) of ~/kaonomi/script"),
            Ui.Action("SAVE", () => SaveScript(vm), tooltip: "Saves the script (Ctrl+S; asks for a name the first time)"),
            Ui.Action("SAVE AS…", () => SaveScriptAs(vm), tooltip: "Saves the script under another name in ~/kaonomi/script"));
        foreach (var button in actions.GetChildren().OfType<Button>())
            button.CustomMinimumSize = new Vector2(Math.Max(button.CustomMinimumSize.X, 80), 0);

        var panel = Panel("The chatbot says", script, box, actions);
        panel.CustomMinimumSize = new Vector2(0, 220);
        return (panel, box);
    }

    private static Control PersonaPanel(RemoteWindowViewModel vm)
    {
        var persona = Ui.Lines(vm, nameof(vm.Persona), 80, "E.g. Tu t'appelles Mari. Tu es gentille et tu t'intéresses à l'utilisateur.");
        persona.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        var panel = Panel("System prompt of the preset", null,
            Eve.Eve.Text_("The personality of the preset, added to the system prompt of the chat for the next prompts.", Eve.Eve.Muted, 11, wrap: true),
            persona);
        panel.CustomMinimumSize = new Vector2(0, 160);
        return panel;
    }

    // ── Scripts ──

    private static string ScriptFolder(RemoteWindowViewModel vm)
    {
        try
        {
            Directory.CreateDirectory(vm.ScriptDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The picker opens elsewhere.
        }
        return vm.ScriptDirectory;
    }

    private static async void OpenScript(RemoteWindowViewModel vm)
    {
        if (await Dialogs.OpenFile("Open a script", ["*.txt ; Script (text)", "* ; All files"], ScriptFolder(vm)) is { } path)
            vm.OpenScript(path);
    }

    private static void SaveScript(RemoteWindowViewModel vm)
    {
        if (vm.ScriptPath is { } path)
            vm.SaveScript(path);
        else
            SaveScriptAs(vm);
    }

    private static async void SaveScriptAs(RemoteWindowViewModel vm)
    {
        var name = vm.ScriptPath is { } current ? Path.GetFileName(current) : $"script-{DateTime.Now:yyyyMMdd-HHmm}.txt";
        if (await Dialogs.SaveFile("Save the script", name, ["*.txt ; Script (text)"], ScriptFolder(vm)) is { } path)
            vm.SaveScript(Path.HasExtension(path) ? path : path + ".txt");
    }

    // ── Preset, voice, effects, animation ──

    private static Control SettingsPanel(RemoteWindowViewModel vm)
    {
        // Presets: the name in the list, its description as tooltip and under the list.
        var presets = Bind.Options(new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FitToLongestItem = false, ClipText = true }, vm.Presets, vm, nameof(vm.SelectedPreset), p => p.Name);
        void Tooltips()
        {
            if (!GodotObject.IsInstanceValid(presets))
                return;
            var popup = presets.GetPopup();
            for (var i = 0; i < vm.Presets.Count && i < popup.ItemCount; i++)
                popup.SetItemTooltip(i, vm.Presets[i].Description);
        }
        NotifyCollectionChangedEventHandler changed = (_, _) => Bind.OnMain(Tooltips);
        vm.Presets.CollectionChanged += changed;
        Bind.OnGone(presets, () => vm.Presets.CollectionChanged -= changed);
        Tooltips();
        var presetDescription = Ui.Label(vm, nameof(vm.SelectedPreset), Eve.Eve.Muted, 11, wrap: true, format: v => (v as ChatPreset)?.Description ?? string.Empty);
        Bind.When(presets, vm, () =>
        {
            presets.TooltipText = vm.SelectedPreset?.Description ?? "Presets";
            if (vm.SelectedPreset is null)
                presets.Text = "Presets…";
        }, nameof(vm.SelectedPreset));
        var deletePreset = Ui.Command("DELETE", vm.DeletePresetCommand, tooltip: "Deletes the saved preset (the built-in presets cannot be deleted)");

        // Voice.
        var voice = Bind.Options(new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, FitToLongestItem = false, ClipText = true }, vm.InstalledVoices, vm, nameof(vm.Voice));
        var tryVoice = Ui.Command("TRY", vm.TestVoiceCommand, tooltip: "Says a sentence with this voice and these effects");

        // Effects.
        var effects = Ui.Grid(3, 8, 8);
        Effect(effects, vm, "Pitch", nameof(vm.Pitch), 0.5, 2, "1 = normal, lower = deeper, higher = higher");
        Effect(effects, vm, "Speed", nameof(vm.Speed), 0.5, 2);
        Effect(effects, vm, "Robot", nameof(vm.Robot), 0, 1, "0 = natural voice, 1 = plainly robotic");
        Effect(effects, vm, "Vibrato", nameof(vm.Vibrato), 0, 1);
        Effect(effects, vm, "Assistant", nameof(vm.Assistant), 0, 1, "The sheen of a film's AI: doubling, small-room reverb, presence");
        Effect(effects, vm, "Horror", nameof(vm.Horror), 0, 1, "Horror-film treatment: demonic shadow, breath, stutters, dark hall");

        var animation = Bind.Options(new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill }, vm.Animations, vm, nameof(vm.Animation), a => a.ToString().ToUpperInvariant());

        var save = Ui.Command("SAVE PRESET", vm.SavePresetCommand, primary: true,
            tooltip: "Saved in presets.json (offered by /config and /remote) and written into config.yml (kept at the next start); a saved preset of the same name is replaced");
        save.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var column = Ui.Column(10,
            Ui.Field("Start from a preset", Ui.Row(6, presets, deletePreset)),
            presetDescription,
            Ui.Field("Voice", Ui.Row(6, voice, tryVoice)),
            effects,
            Ui.Field("3D animation", animation),
            Eve.Eve.Separator(),
            Eve.Eve.Caption("Save as a preset", Eve.Eve.TextBright, 13),
            Ui.Line(vm, nameof(vm.PresetName), "Name of the preset (e.g. ROBOTJEROME)"),
            Ui.Line(vm, nameof(vm.PresetDescription), "Description (written from the settings when empty)"),
            save);
        var scroll = Ui.Scroll(Ui.Margin(column, 0, 0, 8, 0));
        return Panel("Voice and animation", null, scroll);
    }

    /// <summary>A row of the effects: its name, a slider bound to <paramref name="name"/>, its value.</summary>
    private static void Effect(GridContainer grid, RemoteWindowViewModel vm, string label, string name, double min, double max, string tooltip = "")
    {
        var caption = Eve.Eve.Caption(label);
        caption.CustomMinimumSize = new Vector2(80, 0);
        caption.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        var slider = Bind.Range(new HSlider { MinValue = min, MaxValue = max, Step = 0.01, TooltipText = tooltip, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, SizeFlagsVertical = Control.SizeFlags.ShrinkCenter }, vm, name);
        var value = Ui.Label(vm, name, Eve.Eve.Text, 12, format: v => v is double d ? d.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
        value.CustomMinimumSize = new Vector2(44, 0);
        value.HorizontalAlignment = HorizontalAlignment.Right;
        value.AddThemeFontOverride("font", Eve.Eve.Mono);
        grid.AddChild(caption);
        grid.AddChild(slider);
        grid.AddChild(value);
    }

    // ── Voices ──

    private static Control VoicesPanel(RemoteWindowViewModel vm)
    {
        var folder = Eve.Eve.Text_(vm.VoicesDirectory, Eve.Eve.Muted, 11);
        folder.ClipText = true;
        folder.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        folder.TooltipText = vm.VoicesDirectory;
        folder.MouseFilter = Control.MouseFilterEnum.Pass;

        // Installed voices.
        var installed = new VBoxContainer();
        installed.AddThemeConstantOverride("separation", 2);
        Bind.Items(installed, vm.InstalledVoices, key => InstalledRow(vm, key));
        var installedScroll = Ui.Scroll(installed);
        installedScroll.SizeFlagsVertical = Control.SizeFlags.Fill;
        installedScroll.CustomMinimumSize = new Vector2(0, 140);
        var installedBox = new PanelContainer();
        installedBox.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Field, Eve.Eve.Edge with { A = 0.35f }, 4));
        installedBox.AddChild(installedScroll);

        // The catalog.
        var reload = Ui.Command("RELOAD", vm.LoadCatalogCommand, tooltip: "Downloads the catalog of the Piper voices again (rhasspy/piper-voices)");
        var catalogHeader = Ui.Row(8, Ui.Fill(Eve.Eve.Caption("Download (Piper catalog)")), reload);
        var filter = Ui.Line(vm, nameof(vm.CatalogFilter), "Filter: fr_, english, de_DE high…");
        var loading = Bind.Visible(new ProgressBar { Indeterminate = true, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 2) }, vm, nameof(vm.IsCatalogLoading));
        var catalog = new VBoxContainer();
        catalog.AddThemeConstantOverride("separation", 2);
        var none = Ui.Placeholder("No voice matches the filter");
        Bind.Items(catalog, vm.CatalogVoices, item => CatalogRow(vm, item), none);
        Bind.When(none, vm, () => none.Visible = vm.CatalogVoices.Count == 0 && !vm.IsCatalogLoading, nameof(vm.IsCatalogLoading));
        var catalogScroll = Ui.Scroll(catalog);
        var catalogBox = new PanelContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        catalogBox.AddThemeStyleboxOverride("panel", Eve.Eve.Box(Eve.Eve.Field, Eve.Eve.Edge with { A = 0.35f }, 4));
        catalogBox.AddChild(Ui.Column(0, catalogScroll, none));

        return Panel("Voices", null,
            folder,
            Eve.Eve.Caption("Installed"),
            installedBox,
            Ui.Margin(catalogHeader, 0, 8, 0, 0),
            Ui.Column(0, filter, loading),
            catalogBox);
    }

    /// <summary>An installed voice: its name, Try, Delete.</summary>
    private static Control InstalledRow(RemoteWindowViewModel vm, string key)
    {
        var name = Eve.Eve.Text_(key, Eve.Eve.Text, 12);
        name.ClipText = true;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        name.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        name.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        var row = Ui.Row(4, name,
            Small(Ui.Command("TRY", vm.TestVoiceCommand, parameter: () => key)),
            Small(Ui.Command("DELETE", vm.DeleteVoiceCommand, parameter: () => key)));
        Bind.When(name, vm, () => name.AddThemeColorOverride("font_color", vm.Voice == key ? Eve.Eve.Cyan : Eve.Eve.Text), nameof(vm.Voice));
        return Ui.Margin(row, 6, 2, 4, 2);
    }

    /// <summary>A voice of the catalog: its key, details, the download progress, Download or Try.</summary>
    private static Control CatalogRow(RemoteWindowViewModel vm, CatalogVoiceViewModel item)
    {
        var key = Eve.Eve.Text_(item.Voice.Key, Eve.Eve.TextBright, 12);
        key.ClipText = true;
        key.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var details = Eve.Eve.Text_(item.Details, Eve.Eve.Muted, 11);
        details.ClipText = true;
        details.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        var progress = new ProgressBar { MinValue = 0, MaxValue = 100, ShowPercentage = false, CustomMinimumSize = new Vector2(0, 4) };
        Bind.Range(progress, item, nameof(item.Progress));
        Bind.Visible(progress, item, nameof(item.IsDownloading));
        var text = Ui.Fill(Ui.Column(2, key, details, progress));

        var download = Small(Ui.Command("DOWNLOAD", vm.DownloadVoiceCommand, tooltip: "Downloads the voice into the voices folder, then tries it", parameter: () => item));
        Bind.Visible(download, item, nameof(item.CanDownload));
        var test = Small(Ui.Command("TRY", vm.TestVoiceCommand, parameter: () => item.Voice.Key));
        Bind.Visible(test, item, nameof(item.IsInstalled));
        var installedBadge = Ui.Badge("installed", Eve.Eve.Green, item, nameof(item.IsInstalled));
        var row = Ui.Row(6, text, installedBadge, download, test);
        return Ui.Margin(row, 6, 4, 4, 4);
    }

    private static Button Small(Button button)
    {
        button.AddThemeFontSizeOverride("font_size", 11);
        button.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        return button;
    }
}
