using System;
using System.Linq;
using Godot;
using KAONOMI.Chat.Services;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The output and the microphone of the chat (<c>/audiosettings</c>): a popup lists the devices of the system, the
/// choice is kept in <see cref="Preferences"/> and applied to the voice (<see cref="AudioDevices"/>), to the voice input
/// and to the sounds of Godot.
/// </summary>
public static class AudioSettings
{
    private const string SystemDefault = "Default of the system";

    /// <summary>The devices chosen last, applied at startup.</summary>
    public static void Restore()
    {
        AudioDevices.Input = Preferences.AudioInput;
        AudioDevices.Output = Preferences.AudioOutput;
        ApplyToGodot(Preferences.AudioOutput);
    }

    /// <summary>Opens the popup (OK applies and keeps the choice, Cancel leaves everything as it was).</summary>
    public static void Open(Node parent)
    {
        string[] outputs, inputs;
        try
        {
            outputs = [.. AudioDevices.Outputs()];
            inputs = [.. AudioDevices.Inputs()];
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or BadImageFormatException or PortAudioSharp.PortAudioException)
        {
            _ = Dialogs.Message("Audio settings", "The audio devices cannot be listed (PortAudio did not load): " + ex.Message);
            return;
        }

        var dialog = new ConfirmationDialog { Title = "AUDIO SETTINGS", OkButtonText = "OK", MinSize = new Vector2I(520, 0) };
        var output = Choices(outputs, AudioDevices.Output);
        var input = Choices(inputs, AudioDevices.Input);
        var column = Ui.Column(8,
            Ui.Field("Output (the voice and the sounds of the chat)", output),
            Ui.Field("Microphone (the voice input)", input));
        column.CustomMinimumSize = new Vector2(480, 0);
        dialog.AddChild(column);
        dialog.Confirmed += () =>
        {
            var chosenOutput = Chosen(output);
            var chosenInput = Chosen(input);
            AudioDevices.Output = Preferences.AudioOutput = chosenOutput;
            AudioDevices.Input = Preferences.AudioInput = chosenInput;
            ApplyToGodot(chosenOutput);
            ActivityLog.Info("Audio", $"Output: {chosenOutput ?? SystemDefault}; microphone: {chosenInput ?? SystemDefault}");
            dialog.QueueFree();
        };
        dialog.Canceled += dialog.QueueFree;
        parent.GetTree().Root.AddChild(dialog);
        dialog.ResetSize();
        dialog.PopupCentered();
        output.GrabFocus();
    }

    /// <summary>"Default of the system" then the devices; the current one selected (a device missing now stays listed).</summary>
    private static OptionButton Choices(string[] devices, string? current)
    {
        var box = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        box.AddItem(SystemDefault);
        box.SetItemMetadata(0, "");
        foreach (var device in devices)
            Add(box, device, device);
        if (current is not null && !devices.Contains(current))
            Add(box, current + " (not connected)", current);
        for (var i = 0; i < box.ItemCount; i++)
            if (box.GetItemMetadata(i).AsString() == (current ?? ""))
                box.Selected = i;
        return box;
    }

    private static void Add(OptionButton box, string text, string device)
    {
        box.AddItem(text);
        box.SetItemMetadata(box.ItemCount - 1, device);
    }

    /// <summary>The device selected (null: the default of the system).</summary>
    private static string? Chosen(OptionButton box) =>
        box.Selected < 0 || box.GetItemMetadata(box.Selected).AsString() is not { Length: > 0 } device ? null : device;

    /// <summary>
    /// The sounds played by Godot (the intro, the ticks) go to the same output when Godot sees a device of that name
    /// (its names can differ from those of PortAudio); otherwise to the default one.
    /// </summary>
    private static void ApplyToGodot(string? output)
    {
        var name = output is not null && AudioServer.GetOutputDeviceList().Contains(output) ? output : "Default";
        if (AudioServer.OutputDevice != name)
            AudioServer.OutputDevice = name;
    }
}
