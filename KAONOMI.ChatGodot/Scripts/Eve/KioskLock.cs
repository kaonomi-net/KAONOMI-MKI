using System;
using System.Runtime.InteropServices;
using Godot;
using KAONOMI.Chat.Services;

namespace KAONOMI.ChatGodot.Eve;

/// <summary>
/// The chat in full screen locked (the voice mode): the window over 100 % of the screen, and no other application can
/// be brought in front. On macOS it is not the full screen of macOS (that one keeps a black band under the camera of the
/// screens with a notch, and is a desktop of its own that Ctrl+arrows leaves): the window loses its borders and covers
/// the whole frame of its screen, above the windows of the other applications and on every desktop; the options of
/// presentation of the application hide the menu bar and the dock and turn the switcher of applications off (Cmd+Tab),
/// and Cmd+H with it. The chat itself ignores Ctrl+Tab and the close of its window while it is locked. Leaving the
/// voice mode (a double-click on the animation or Esc) brings the usual desktop back. Cmd+Option+Esc (force quit)
/// stays, as the way out of last resort.
/// </summary>
public static class KioskLock
{
    // NSApplicationPresentationOptions.
    private const ulong HideDock = 1 << 1, HideMenuBar = 1 << 3, DisableProcessSwitching = 1 << 5, DisableHideApplication = 1 << 8;
    // NSWindowCollectionBehavior: on every desktop, not moved by Mission Control.
    private const ulong CanJoinAllSpaces = 1 << 0, Stationary = 1 << 4;
    // NSFloatingWindowLevel: above the windows of the other applications, under the window of force quit.
    private const long FloatingLevel = 3;

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPointer(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern ulong SendULong(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendULong(IntPtr receiver, IntPtr selector, ulong value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern long SendLong(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendLong(IntPtr receiver, IntPtr selector, long value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern Rect SendRectRegisters(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend_stret")]
    private static extern void SendRectStret(out Rect rect, IntPtr receiver, IntPtr selector);

    /// <summary>An NSRect returned by a selector: in registers on Apple Silicon, through objc_msgSend_stret on Intel.</summary>
    private static Rect SendRect(IntPtr receiver, IntPtr selector)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            SendRectStret(out var rect, receiver, selector);
            return rect;
        }
        return SendRectRegisters(receiver, selector);
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendRectBool(IntPtr receiver, IntPtr selector, Rect rect, bool value);

    /// <summary>An NSRect (four doubles).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public double X, Y, Width, Height;
    }

    /// <summary>True while the chat in full screen is locked.</summary>
    public static bool IsOn { get; private set; }

    // The window before the lock, given back by Release.
    private static DisplayServer.WindowMode _mode;
    private static Vector2I _position, _size;
    private static bool _borderless;
    private static ulong _options, _behavior;
    private static long _level;
    private static bool _native;

    /// <summary>Puts the window of the chat over the whole screen and locks it.</summary>
    public static void Engage()
    {
        if (IsOn)
            return;
        IsOn = true;
        _mode = DisplayServer.WindowGetMode();
        _position = DisplayServer.WindowGetPosition();
        _size = DisplayServer.WindowGetSize();
        _borderless = DisplayServer.WindowGetFlag(DisplayServer.WindowFlags.Borderless);
        var window = Window();
        if (window == IntPtr.Zero)
        {
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.ExclusiveFullscreen);
            return;
        }
        if (_mode is DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen or DisplayServer.WindowMode.Maximized)
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
        if (_mode is DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen)
        {
            // Out of the full screen of macOS first: its animation ends by giving back the menu bar and the frame of
            // before, so the window covers the screen only once it has ended.
            _settled = 0;
            _asked = Time.GetTicksMsec();
            ((SceneTree)Engine.GetMainLoop()).ProcessFrame += AwaitWindowed;
        }
        else
            Cover(window);
    }

    /// <summary>The time (ms) the window has been out of the full screen of macOS (0: not yet).</summary>
    private static ulong _settled;

    /// <summary>The time (ms) the end of the full screen of macOS was last asked.</summary>
    private static ulong _asked;

    // NSWindowStyleMaskFullScreen.
    private const ulong FullScreenStyle = 1 << 14;

    /// <summary>Each frame until the full screen of macOS has ended, then a moment more: the window covers the screen.</summary>
    private static void AwaitWindowed()
    {
        var window = Window();
        var leaving = DisplayServer.WindowGetMode() is DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen
            || (window != IntPtr.Zero && (SendULong(window, sel_registerName("styleMask")) & FullScreenStyle) != 0);
        if (IsOn && (leaving || _settled == 0 || Time.GetTicksMsec() - _settled < 400))
        {
            if (leaving)
            {
                _settled = 0;
                // Asked while macOS was still putting it in full screen (the start of the chat), the end is ignored: asked again.
                if (Time.GetTicksMsec() - _asked > 1500)
                {
                    _asked = Time.GetTicksMsec();
                    DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                }
            }
            else if (_settled == 0)
                _settled = Time.GetTicksMsec();
            return;
        }
        ((SceneTree)Engine.GetMainLoop()).ProcessFrame -= AwaitWindowed;
        if (IsOn && window != IntPtr.Zero)
            Cover(window);
    }

    /// <summary>The window without borders over the frame of its screen, the menu bar, the dock and the switcher off.</summary>
    private static void Cover(IntPtr window)
    {
        try
        {
            DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, true);
            var app = App();
            _options = SendULong(app, sel_registerName("presentationOptions"));
            // The dock hidden, not on auto-hide: macOS only turns the switcher off with the dock hidden.
            SendULong(app, sel_registerName("setPresentationOptions:"), HideDock | HideMenuBar | DisableProcessSwitching | DisableHideApplication);
            _behavior = SendULong(window, sel_registerName("collectionBehavior"));
            SendULong(window, sel_registerName("setCollectionBehavior:"), _behavior | CanJoinAllSpaces | Stationary);
            _level = SendLong(window, sel_registerName("level"));
            SendLong(window, sel_registerName("setLevel:"), FloatingLevel);
            var screen = SendPointer(window, sel_registerName("screen"));
            if (screen != IntPtr.Zero)
                SendRectBool(window, sel_registerName("setFrame:display:"), SendRect(screen, sel_registerName("frame")), true);
            _native = true;
            var frame = SendRect(window, sel_registerName("frame"));
            ActivityLog.Info("Kiosk", $"Full screen locked ({frame.Width}x{frame.Height}): Cmd+Tab is off until the voice mode is left");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            ActivityLog.Error("Kiosk", "The window cannot be locked over the screen", ex);
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.ExclusiveFullscreen);
        }
    }

    private static IntPtr Window() =>
        OperatingSystem.IsMacOS() ? (IntPtr)DisplayServer.WindowGetNativeHandle(DisplayServer.HandleType.WindowHandle) : IntPtr.Zero;

    /// <summary>Unlocks the chat: the switcher of applications, the menu bar and the dock are back, the window as it was.</summary>
    public static void Release()
    {
        if (!IsOn)
            return;
        IsOn = false;
        // At the end of the frame: the change of the mode of the window, asked from the signal of the password, frees
        // objects while it is being emitted (the process ends).
        Callable.From(Restore).CallDeferred();
    }

    /// <summary>The switcher of applications, the menu bar and the dock back, the window as it was before the lock.</summary>
    private static void Restore()
    {
        if (!_native)
        {
            DisplayServer.WindowSetMode(_mode is DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen
                ? DisplayServer.WindowMode.Windowed : _mode);
            return;
        }
        _native = false;
        try
        {
            var window = Window();
            SendULong(App(), sel_registerName("setPresentationOptions:"), _options);
            if (window != IntPtr.Zero)
            {
                SendLong(window, sel_registerName("setLevel:"), _level);
                SendULong(window, sel_registerName("setCollectionBehavior:"), _behavior);
            }
            ActivityLog.Info("Kiosk", "Full screen unlocked");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            ActivityLog.Error("Kiosk", "The switcher of applications cannot be turned back on", ex);
        }
        DisplayServer.WindowSetFlag(DisplayServer.WindowFlags.Borderless, _borderless);
        if (_mode == DisplayServer.WindowMode.Maximized)
            DisplayServer.WindowSetMode(DisplayServer.WindowMode.Maximized);
        else
        {
            // The full screen of the K before the voice mode comes back as the window it was before it.
            DisplayServer.WindowSetSize(_size);
            DisplayServer.WindowSetPosition(_position);
            if (_mode != DisplayServer.WindowMode.Windowed)
                DisplayServer.WindowSetMode(_mode);
        }
    }

    private static IntPtr App() => SendPointer(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
}
