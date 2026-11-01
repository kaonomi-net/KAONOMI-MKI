using System;
using Godot;
using KAONOMI.ChatGodot.Eve;

namespace KAONOMI.ChatGodot.Tools.Media;

/// <summary>
/// The keyboard shortcuts of a tool window: an invisible node added to the content of the window that sees the keys
/// before the controls (as the tunnelling key handlers and key bindings of the Avalonia views), only while its window
/// is the one in front. <see cref="Handler"/> returns true when it used the key.
/// </summary>
public partial class MediaKeys : Node
{
    public MediaKeys()
    {
    }

    public MediaKeys(Func<InputEventKey, bool> handler) => Handler = handler;

    /// <summary>Called for every key pressed while the window is in front; true when the key was used.</summary>
    public Func<InputEventKey, bool>? Handler { get; set; }

    /// <summary>Adds the shortcuts <paramref name="handler"/> to <paramref name="root"/>.</summary>
    public static T Attach<T>(T root, Func<InputEventKey, bool> handler) where T : Control
    {
        root.AddChild(new MediaKeys(handler));
        return root;
    }

    /// <summary>True when a text field has the focus (Space, Enter… belong to it).</summary>
    public bool IsTyping() => GetViewport()?.GuiGetFocusOwner() is LineEdit or TextEdit or SpinBox;

    public override void _Input(InputEvent e)
    {
        if (e is not InputEventKey { Pressed: true } key || Handler is null || GetParent() is not Control { } root || !root.IsVisibleInTree())
            return;
        // The active window of the deck only (every window receives every key), and not while the chat is typed in.
        if (Shell.Current is { } shell && !shell.HasKeyboard(root))
            return;
        if (Handler(key))
            GetViewport().SetInputAsHandled();
    }

    /// <summary>True for Ctrl (or Cmd on the Mac) + <paramref name="code"/>.</summary>
    public static bool Command(InputEventKey key, Key code) =>
        key.Keycode == code && (key.CtrlPressed || key.MetaPressed) && !key.AltPressed;
}
