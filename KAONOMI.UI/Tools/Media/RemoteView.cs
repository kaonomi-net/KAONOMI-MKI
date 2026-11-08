using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using KAONOMI.Chat.Models;
using KAONOMI.Chat.ViewModels;
using KAONOMI.UI.Desk;

namespace KAONOMI.UI.Tools.Media;

/// <summary>
/// /remote: remote control of the chatbot. What it says (Say, Ctrl+Enter: shown in the chat, read aloud, the avatar
/// following the voice), or the text as a script (▶ Run script: paragraph by paragraph, a line CHANGE:NAME switches to
/// the preset NAME), Stop (Esc); the scripts of ~/kaonomi/script (Open…, Save — Ctrl+S, asks for a name the first time —,
/// Save as…). The system prompt of the preset (its personality). Voice and animation: start from a preset (Delete for a
/// saved one), the voice (Try), pitch, speed, robot, vibrato, assistant, horror, the animation, all applied live to the
/// chat; Save preset (name, description). Voices: the installed Piper voices (Try, Delete) and the Piper catalog
/// (filter, Reload, Download with its progress, then Try).
/// </summary>
[ToolView(typeof(RemoteWindowViewModel), "Remote", "🎛", 1400, 860)]
public sealed class RemoteView : IToolView
{
    public Control Build(object viewModel, Shell shell)
    {
        var vm = (RemoteWindowViewModel)viewModel;
        var (sayPanel, sayBox) = SayPanel(vm);
        var left = Kit.SplitV(sayPanel, PersonaPanel(vm), "3*", "2*", 220, 160);

        // Three columns, the bars between them movable: the text takes the free width, the settings and the voices
        // their width of the Avalonia view (360 and 400).
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,5,360,5,400") };
        columns.ColumnDefinitions[0].MinWidth = 260;
        columns.ColumnDefinitions[2].MinWidth = 280;
        columns.ColumnDefinitions[4].MinWidth = 300;
        var middle = SettingsPanel(vm);
        var right = VoicesPanel(vm);
        Grid.SetColumn(middle, 2);
        Grid.SetColumn(right, 4);
        var split1 = new GridSplitter { Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
        var split2 = new GridSplitter { Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(split1, 1);
        Grid.SetColumn(split2, 3);
        columns.Children.AddRange([left, split1, middle, split2, right]);

        var page = Kit.Rows("*,Auto", Kit.Margin(columns, 4, 4, 4, 6), Kit.StatusBar(vm, nameof(vm.Status), nameof(vm.IsError)));
        Kit.FocusWhenShown(sayBox);
        Kit.Keys(page, e =>
        {
            if (e.Key == Key.Enter && Kit.CommandHeld(e.KeyModifiers))
                Ui.Run(vm.SayCommand);
            else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
                Ui.Run(vm.StopCommand);
            else if (Kit.IsCommand(e, Key.S))
                SaveScript(vm);
            else
                return false;
            return true;
        });
        return page;
    }

    /// <summary>A panel with a title: <paramref name="top"/> under it, <paramref name="fill"/> taking the rest, <paramref name="bottom"/> at the bottom.</summary>
    private static Border Panel(string title, Control? beside, Control[] top, Control? fill, params Control[] bottom)
    {
        var header = beside is null
            ? (Control)Ui.Label(title.ToUpperInvariant(), Ui.TextBright, 13, bold: true)
            : Kit.FillRow(8, [Ui.Label(title.ToUpperInvariant(), Ui.TextBright, 13, bold: true)], beside);
        var dock = new DockPanel { LastChildFill = true };
        void Add(Control control, Dock side)
        {
            control.Margin = side == Dock.Bottom ? new Thickness(0, 8, 0, 0) : new Thickness(0, 0, 0, 8);
            DockPanel.SetDock(control, side);
            dock.Children.Add(control);
        }
        Add(header, Dock.Top);
        foreach (var control in top)
            Add(control, Dock.Top);
        foreach (var control in Enumerable.Reverse(bottom))
            Add(control, Dock.Bottom);
        dock.Children.Add(fill ?? new Panel());
        return Ui.Box(dock, 14);
    }

    // ── What the chatbot says ──

    private static (Control Panel, TextBox Box) SayPanel(RemoteWindowViewModel vm)
    {
        var script = Kit.Text(vm, nameof(vm.ScriptName), v => $"· {v}", Ui.Muted, 11);
        Ui.When(script, vm, () => ToolTip.SetTip(script, string.IsNullOrEmpty(vm.ScriptPath) ? null : vm.ScriptPath), nameof(vm.ScriptPath));

        var box = Kit.Lines(vm, nameof(vm.Text), 120,
            "Text the chatbot says as its answer: shown in the chat, read aloud with the voice below, the animation following it (Ctrl+Enter). "
            + "As a script (Run): said paragraph by paragraph (paragraphs separated by a blank line); a line CHANGE:MARI switches to the preset MARI");
        box.VerticalContentAlignment = VerticalAlignment.Top;

        var actions = Kit.Flow(8,
            Ui.Command("SAY", vm.SayCommand, tooltip: "The chatbot says the whole text at once (Ctrl+Enter)", primary: true),
            Ui.Command("►  RUN SCRIPT", vm.RunScriptCommand, tooltip: "Says the text paragraph by paragraph; a line CHANGE:NAME switches to the preset NAME (an unknown preset keeps the current one)"),
            Kit.Check("5 s DELAY", vm, nameof(vm.DelayStart), $"Run script waits {RemoteWindowViewModel.StartDelaySeconds} seconds before the presentation starts (a countdown in the status; Stop ends it)"),
            Ui.Command("STOP", vm.StopCommand, tooltip: "Stops the voice and the script (Esc)"),
            Ui.Button("OPEN…", () => OpenScript(vm), "Opens a script (.txt) of ~/kaonomi/script"),
            Ui.Button("SAVE", () => SaveScript(vm), "Saves the script (Ctrl+S; asks for a name the first time)"),
            Ui.Button("SAVE AS…", () => SaveScriptAs(vm), "Saves the script under another name in ~/kaonomi/script"));
        foreach (var button in actions.Children.OfType<Button>())
            button.MinWidth = 80;

        return (Panel("The chatbot says", script, [], box, actions), box);
    }

    private static Control PersonaPanel(RemoteWindowViewModel vm)
    {
        var persona = Kit.Lines(vm, nameof(vm.Persona), 80, "E.g. Tu t'appelles Mari. Tu es gentille et tu t'intéresses à l'utilisateur.");
        persona.VerticalContentAlignment = VerticalAlignment.Top;
        return Panel("System prompt of the preset", null,
            [Ui.Label("The personality of the preset, added to the system prompt of the chat for the next prompts.", Ui.Muted, 11, wrap: true)],
            persona);
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
        var presets = new ComboBox
        {
            ItemsSource = vm.Presets,
            PlaceholderText = "Presets…",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<ChatPreset>((p, _) =>
            {
                var label = new TextBlock { Text = p?.Name ?? string.Empty };
                if (!string.IsNullOrEmpty(p?.Description))
                    ToolTip.SetTip(label, p.Description);
                return label;
            })
        };
        presets.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty, vm, nameof(vm.SelectedPreset), Avalonia.Data.BindingMode.TwoWay);
        Ui.When(presets, vm, () => ToolTip.SetTip(presets, vm.SelectedPreset?.Description ?? "Presets"), nameof(vm.SelectedPreset));
        var presetDescription = Kit.Text(vm, nameof(vm.SelectedPreset), v => (v as ChatPreset)?.Description ?? string.Empty, Ui.Muted, 11, wrap: true);
        var deletePreset = Ui.Command("DELETE", vm.DeletePresetCommand, tooltip: "Deletes the saved preset (the built-in presets cannot be deleted)");

        // Voice.
        var voice = Kit.Options(vm.InstalledVoices, vm, nameof(vm.Voice));
        var tryVoice = Ui.Command("TRY", vm.TestVoiceCommand, tooltip: "Says a sentence with this voice and these effects");

        // Effects.
        var effects = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*,44"), ColumnSpacing = 8, RowSpacing = 8 };
        var row = 0;
        void Effect(string label, string name, double min, double max, string? tooltip = null)
        {
            effects.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var caption = Ui.Caption(label);
            var slider = Kit.Slider(vm, name, min, max, 0.01, tooltip);
            var value = Kit.Text(vm, name, v => v is double d ? d.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : string.Empty, Ui.Text, 12);
            value.FontFamily = Ui.Mono;
            value.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetRow(caption, row);
            Grid.SetRow(slider, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(value, 2);
            effects.Children.AddRange([caption, slider, value]);
            row++;
        }
        Effect("Pitch", nameof(vm.Pitch), 0.5, 2, "1 = normal, lower = deeper, higher = higher");
        Effect("Speed", nameof(vm.Speed), 0.5, 2);
        Effect("Robot", nameof(vm.Robot), 0, 1, "0 = natural voice, 1 = plainly robotic");
        Effect("Vibrato", nameof(vm.Vibrato), 0, 1);
        Effect("Assistant", nameof(vm.Assistant), 0, 1, "The sheen of a film's AI: doubling, small-room reverb, presence");
        Effect("Horror", nameof(vm.Horror), 0, 1, "Horror-film treatment: demonic shadow, breath, stutters, dark hall");

        var animation = Kit.Options(vm.Animations, vm, nameof(vm.Animation), a => a.ToString()!.ToUpperInvariant());

        var save = Ui.Command("SAVE PRESET", vm.SavePresetCommand, primary: true,
            tooltip: "Saved in presets.json (offered by /config and /remote) and written into config.yml (kept at the next start); a saved preset of the same name is replaced");
        save.HorizontalAlignment = HorizontalAlignment.Stretch;
        save.HorizontalContentAlignment = HorizontalAlignment.Center;

        var column = Ui.Column(10,
            Kit.Field("Start from a preset", Kit.FillRow(6, [], presets, deletePreset)),
            presetDescription,
            Kit.Field("Voice", Kit.FillRow(6, [], voice, tryVoice)),
            effects,
            Kit.Field("Animation", animation),
            new Separator(),
            Ui.Label("SAVE AS A PRESET", Ui.TextBright, 13, bold: true),
            Ui.Edit(vm, nameof(vm.PresetName), "Name of the preset (e.g. ROBOTJEROME)"),
            Ui.Edit(vm, nameof(vm.PresetDescription), "Description (written from the settings when empty)"),
            save);
        return Panel("Voice and animation", null, [], Ui.Scroll(Kit.Margin(column, 0, 0, 8, 0)));
    }

    // ── Voices ──

    private static Control VoicesPanel(RemoteWindowViewModel vm)
    {
        var folder = Ui.Label(vm.VoicesDirectory, Ui.Muted, 11);
        ToolTip.SetTip(folder, vm.VoicesDirectory);

        // Installed voices.
        var installed = Kit.Items(vm.InstalledVoices, key => InstalledRow(vm, key));
        var installedBox = new Border
        {
            Child = new ScrollViewer { Content = installed, Height = 140 },
            Background = Ui.Field,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4)
        };

        // The catalog.
        var reload = Ui.Command("RELOAD", vm.LoadCatalogCommand, tooltip: "Downloads the catalog of the Piper voices again (rhasspy/piper-voices)");
        var catalogHeader = Kit.FillRow(8, [], Ui.Caption("Download (Piper catalog)"), reload);
        var filter = Ui.Edit(vm, nameof(vm.CatalogFilter), "Filter: fr_, english, de_DE high…");
        var loading = new ProgressBar { IsIndeterminate = true, Height = 2, MinHeight = 2 }.Visible(vm, nameof(vm.IsCatalogLoading));
        var catalog = Kit.Items(vm.CatalogVoices, item => CatalogRow(vm, item));
        var none = Kit.Placeholder("No voice matches the filter");
        void NoneVisible() => none.IsVisible = vm.CatalogVoices.Count == 0 && !vm.IsCatalogLoading;
        Kit.OnCollection(none, vm.CatalogVoices, NoneVisible);
        Ui.When(none, vm, NoneVisible, nameof(vm.IsCatalogLoading));
        var catalogBox = new Border
        {
            Child = new Grid { Children = { Ui.Scroll(catalog), none } },
            Background = Ui.Field,
            BorderBrush = Ui.Edge,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4)
        };

        return Panel("Voices", null,
            [folder, Ui.Caption("Installed"), installedBox, catalogHeader, Ui.Column(0, filter, loading)],
            catalogBox);
    }

    /// <summary>An installed voice: its name, Try, Delete.</summary>
    private static Control InstalledRow(RemoteWindowViewModel vm, string key)
    {
        var name = Ui.Label(key, Ui.Text, 12);
        Ui.When(name, vm, () => name.Foreground = vm.Voice == key ? Ui.Accent : Ui.Text, nameof(vm.Voice));
        var row = Kit.FillRow(4, [], name,
            Small(Kit.Command("TRY", vm.TestVoiceCommand, () => key)),
            Small(Kit.Command("DELETE", vm.DeleteVoiceCommand, () => key)));
        return Kit.Margin(row, 6, 2, 4, 2);
    }

    /// <summary>A voice of the catalog: its key, details, the download progress, Download or Try.</summary>
    private static Control CatalogRow(RemoteWindowViewModel vm, CatalogVoiceViewModel item)
    {
        var key = Ui.Label(item.Voice.Key, Ui.TextBright, 12);
        var details = Ui.Label(item.Details, Ui.Muted, 11);
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, MinHeight = 4 };
        progress.Bind(Avalonia.Controls.Primitives.RangeBase.ValueProperty, item, nameof(item.Progress));
        progress.Visible(item, nameof(item.IsDownloading));
        var text = Ui.Column(2, key, details, progress);

        var download = Small(Kit.Command("DOWNLOAD", vm.DownloadVoiceCommand, () => item, "Downloads the voice into the voices folder, then tries it")).Visible(item, nameof(item.CanDownload));
        var test = Small(Kit.Command("TRY", vm.TestVoiceCommand, () => item.Voice.Key)).Visible(item, nameof(item.IsInstalled));
        var installedBadge = Kit.Badge("installed", Ui.Green, item, nameof(item.IsInstalled));
        return Kit.Margin(Kit.FillRow(6, [], text, installedBadge, download, test), 6, 4, 4, 4);
    }

    private static Button Small(Button button)
    {
        button.FontSize = 11;
        button.Padding = new Thickness(6, 2);
        button.VerticalAlignment = VerticalAlignment.Center;
        return button;
    }
}
