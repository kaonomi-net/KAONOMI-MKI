using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The theme chosen in this Godot chat, kept in <c>preferences.json</c> of its data folder (the Avalonia chat keeps its
/// own theme in config.yml: the two apps do not change each other's look).
/// </summary>
public static class Preferences
{
    private sealed record Data(string? Theme, string? Wallpaper = null, bool SetupDone = false, string[]? RecentWindows = null, string? AudioInput = null, string? AudioOutput = null);

    private static string FilePath => ProjectSettings.GlobalizePath("user://preferences.json");

    /// <summary>The name of the theme chosen last (null: the default, EVE).</summary>
    public static string? Theme
    {
        get => Load().Theme;
        set => Save(Load() with { Theme = value });
    }

    /// <summary>The picture behind the windows of the desktop (a copy in the data folder; null: none).</summary>
    public static string? Wallpaper
    {
        get => Load().Wallpaper;
        set => Save(Load() with { Wallpaper = value });
    }

    /// <summary>True once DONE was pressed on the screen of the dependencies (shown at the first start until then).</summary>
    public static bool SetupDone
    {
        get => Load().SetupDone;
        set => Save(Load() with { SetupDone = value });
    }

    /// <summary>The microphone chosen with /audiosettings (null: the default one of the system).</summary>
    public static string? AudioInput
    {
        get => Load().AudioInput;
        set => Save(Load() with { AudioInput = value });
    }

    /// <summary>The output chosen with /audiosettings (null: the default one of the system).</summary>
    public static string? AudioOutput
    {
        get => Load().AudioOutput;
        set => Save(Load() with { AudioOutput = value });
    }

    /// <summary>The commands of the tool windows opened last (/intel, /mindmap…), the newest first (10 at most).</summary>
    public static IReadOnlyList<string> RecentWindows => Load().RecentWindows ?? [];

    /// <summary>The tool window of <paramref name="command"/> was opened: it goes first in <see cref="RecentWindows"/>.</summary>
    public static void AddRecentWindow(string command)
    {
        var data = Load();
        var recent = new List<string> { command };
        recent.AddRange((data.RecentWindows ?? []).Where(c => c != command).Take(9));
        Save(data with { RecentWindows = [.. recent] });
    }

    /// <summary>The data folder of the chat (where the copy of the wallpaper is kept).</summary>
    public static string Folder => Path.GetDirectoryName(FilePath)!;

    private static Data Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data(null) : new Data(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Data(null);
        }
    }

    private static void Save(Data data)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(data));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning("The preferences were not saved: " + ex.Message);
        }
    }
}

/// <summary>
/// The screen of the themes (🎨 at the bottom of the bar of commands): one card per theme with a small preview of its
/// colours (its page, a panel with a title, a text and a button, its accent), its name and description, and Apply (the
/// interface is built again in that theme at once; the windows open come back as they were).
/// </summary>
public static class ThemeScreen
{
    /// <summary>The key of its window.</summary>
    public const string Key = "themes";

    /// <summary>Builds the screen; <paramref name="apply"/> applies the theme chosen.</summary>
    public static Control Build(Action<Palette> apply)
    {
        var column = Ui.Column(12,
            Eve.Caption("Choose the look of the chat", Eve.TextBright, 13),
            Eve.Text_("The theme is kept for the next start. The windows open stay open.", Eve.Muted, 12, wrap: true));
        var cards = new HFlowContainer();
        cards.AddThemeConstantOverride("h_separation", 14);
        cards.AddThemeConstantOverride("v_separation", 14);
        foreach (var palette in Eve.Palettes)
            cards.AddChild(Card(palette, apply));
        column.AddChild(cards);
        return Ui.Scroll(Ui.Margin(column, 6));
    }

    private static Control Card(Palette palette, Action<Palette> apply)
    {
        var inUse = palette == Eve.Current;
        var card = new PanelContainer { CustomMinimumSize = new Vector2(300, 0) };
        card.AddThemeStyleboxOverride("panel", Eve.Box(Eve.Field, inUse ? Eve.Cyan : Eve.Edge, 10, inUse ? 2 : 1));
        var name = Eve.Caption(palette.Name, inUse ? Eve.Cyan : Eve.TextBright, 14);
        var description = Eve.Text_(palette.Description, Eve.Muted, 12, wrap: true);
        description.CustomMinimumSize = new Vector2(280, 0);
        var button = inUse ? Eve.Button("IN USE") : Eve.Button("APPLY", primary: true);
        button.Disabled = inUse;
        button.Pressed += () => apply(palette);
        card.AddChild(Ui.Column(8, Preview(palette), name, description, button));
        return card;
    }

    /// <summary>A small picture of the theme drawn with its own colours.</summary>
    private static Control Preview(Palette palette)
    {
        var page = new PanelContainer { CustomMinimumSize = new Vector2(280, 130), MouseFilter = Control.MouseFilterEnum.Ignore };
        page.AddThemeStyleboxOverride("panel", Eve.Box(palette.Space, palette.Edge, 12));
        var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        var box = Eve.Box(palette.PanelSolid, palette.Edge, 10);
        box.BorderWidthTop = 3;
        box.BorderColor = palette.Accent;
        panel.AddThemeStyleboxOverride("panel", box);
        Label Label(string text, Color color, int size)
        {
            var label = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
            label.AddThemeColorOverride("font_color", color);
            label.AddThemeFontSizeOverride("font_size", size);
            label.AddThemeFontOverride("font", new SystemFont { FontNames = palette.DisplayFonts, FontWeight = palette.DisplayWeight });
            return label;
        }
        var chip = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin };
        chip.AddThemeStyleboxOverride("panel", Eve.Box(palette.Accent with { A = 0.85f }, palette.Accent, 4));
        chip.AddChild(Label("SEND", palette.IsLight ? Colors.White : palette.TextBright, 10));
        var swatches = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        foreach (var color in new[] { palette.Accent, palette.Warm, palette.Green, palette.Red, palette.Muted })
            swatches.AddChild(new ColorRect { Color = color, CustomMinimumSize = new Vector2(22, 8), MouseFilter = Control.MouseFilterEnum.Ignore });
        panel.AddChild(Ui.Column(4, Label("KAONOMI", palette.TextBright, 13), Label("The chat, the tools, the 3D desk", palette.Text, 10), chip, swatches));
        page.AddChild(panel);
        return page;
    }
}
