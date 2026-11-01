using System;
using System.Linq;
using Godot;
using KAONOMI.Chat.Services;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// A window of the desktop moved into a window of its own of the system (⧉ in its header): the content leaves the
/// deck for a native window of Godot, which the user puts where he likes; closing that window brings the content
/// back into the desktop. The window opens on the other screen when there is one (the chat is often in full screen:
/// a window of the same screen would be hidden behind it, or taken into its full-screen space); with one screen only
/// and the chat in full screen, the chat leaves the full screen first. The main window of the chat embeds its
/// sub-windows (the pop-ups); the embedding is turned off while a system window is open (Godot cannot turn it back on
/// while one is shown) and comes back once it is closed.
/// </summary>
public static class NativeWindows
{
    /// <summary>
    /// Moves the content of <paramref name="window"/> into a window of the system titled <paramref name="title"/>
    /// (<paramref name="size"/> in points of the interface, scaled to the screen), and closes it on the desktop; when
    /// the system window is closed, <paramref name="reopen"/> gets the content back (the caller opens it again on the
    /// desktop). Returns what was done, for the status of the chat.
    /// </summary>
    public static string Detach(Shell shell, DeckWindow window, string title, Vector2I size, Action<Control> reopen)
    {
        var content = window.Content;
        var root = shell.Root.GetTree().Root;
        var mainId = root.GetWindowId();
        // Out of its frame (the deck frees the frame with the window), with no parent for a moment.
        content.GetParent()?.RemoveChild(content);
        shell.Deck.Close(window);

        // The screen: the other one when there is one, else the screen of the chat, which then leaves the full screen.
        var mainScreen = DisplayServer.WindowGetCurrentScreen(mainId);
        var screens = DisplayServer.GetScreenCount();
        var target = Enumerable.Range(0, screens).FirstOrDefault(s => s != mainScreen, mainScreen);
        var mainMode = DisplayServer.WindowGetMode(mainId);
        var note = target != mainScreen ? "on the other screen" : "on the screen of the chat";
        if (target == mainScreen && mainMode is DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen)
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Maximized, mainId);
            note = "on the screen of the chat, which left the full screen (one screen only)";
        }
        var scale = (float)Math.Max(1, DisplayServer.ScreenGetScale(target));
        var pixels = (Vector2I)((Vector2)size * scale);
        var origin = DisplayServer.ScreenGetPosition(target);
        var area = DisplayServer.ScreenGetSize(target);
        var position = origin + (area - pixels) / 2;

        var native = new Window
        {
            Title = title,
            Mode = Window.ModeEnum.Windowed,
            InitialPosition = Window.WindowInitialPosition.Absolute,
            Size = pixels,
            MinSize = (Vector2I)(new Vector2(360, 300) * scale),
            Position = position,
            ContentScaleFactor = scale,
            Transparent = false,
            Unresizable = false,
            Borderless = false,
            Transient = false,
            Exclusive = false
        };
        var back = new ColorRect { Color = Eve.Space, MouseFilter = Control.MouseFilterEnum.Ignore };
        back.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        native.AddChild(back);
        content.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        native.AddChild(content);

        // A real window of the system: the main window must not embed it. Godot refuses to embed again while the
        // window is shown: the pop-ups of the chat are windows of the system too until it is closed.
        var embedded = root.GuiEmbedSubwindows;
        root.GuiEmbedSubwindows = false;
        root.AddChild(native);
        var isNative = !native.IsEmbedded();
        if (!isNative)
            root.GuiEmbedSubwindows = embedded;
        var where = isNative ? $"the system, {note}" : "the chat (the system window could not be made)";
        ActivityLog.Info("Desk", $"{title}: moved to a window of {where}: screen {target} of {screens} (the chat on {mainScreen}, {mainMode}), "
                                 + $"position {native.Position}, size {native.Size}, scale {scale}");

        native.CloseRequested += () =>
        {
            // Back to the desktop: the content leaves the system window before it is freed, the embedding comes back.
            if (GodotObject.IsInstanceValid(content) && content.GetParent() == native)
                native.RemoveChild(content);
            native.QueueFree();
            Callable.From(() =>
            {
                if (GodotObject.IsInstanceValid(root))
                    root.GuiEmbedSubwindows = embedded;
            }).CallDeferred();
            if (GodotObject.IsInstanceValid(content))
                reopen(content);
        };
        return $"Chat in a window of {where}";
    }
}
