using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using KAONOMI.Chat.Services;

namespace KAONOMI.Chat.Views;

/// <summary>
/// Makes the chat a Windows application desktop toolbar (AppBar) docked to the right edge of its screen: Windows
/// removes that strip from the working area of every application, so maximized windows stop at the left of the chat
/// as if the screen ended there. The windows of the other applications that still overlap the strip (moved or resized
/// over it, or full screen, which ignores the working area) are put back to its left. Windows only; no-op elsewhere.
/// </summary>
internal static class AppBar
{
    private static Window? _window;
    private static IntPtr _hwnd;
    private static uint _callbackMessage;
    private static RECT _strip;
    private static IntPtr _monitor;
    private static WinEventProc? _eventProc;             // kept alive: called by Windows
    private static Win32Properties.CustomWndProcHookCallback? _wndProc;

    /// <summary>
    /// Thread of the hook on the windows of the other applications. Windows calls the hook on the thread that installed
    /// it, through its message loop, for every window shown or moved on the desktop (and every move of a caret, a
    /// cursor…): on the UI thread, that flood would freeze the chat and all its windows.
    /// </summary>
    private static Thread? _hookThread;
    private static uint _hookThreadId;

    /// <summary>Strip and screen read by the hook thread (null when not docked); replaced whole, never changed.</summary>
    private static volatile Docked? _docked;

    private sealed record Docked(RECT Strip, IntPtr Monitor);

    /// <summary>
    /// Registers <paramref name="window"/> as an AppBar on the right edge of its current screen and docks it there.
    /// Called again after <see cref="Undock"/> (the chat shown again from the systray).
    /// </summary>
    public static void Dock(Window window)
    {
        if (!OperatingSystem.IsWindows() || _hwnd != IntPtr.Zero
            || window.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero)
            return;

        if (!ReferenceEquals(_window, window))
        {
            // Once per window: window procedure hook and release of the strip when the application ends.
            _window = window;
            _callbackMessage = RegisterWindowMessage("Kaonomi.Chat.AppBar");
            _wndProc = OnWndProc;
            Win32Properties.AddWndProcHookCallback(window, _wndProc);
            _eventProc = OnWinEvent;
            window.Closed += (_, _) => Undock();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Undock();
        }

        _hwnd = hwnd;
        var data = NewData();
        data.uCallbackMessage = _callbackMessage;
        SHAppBarMessage(ABM_NEW, ref data);

        Place();
        StartHookThread();
    }

    /// <summary>Gives the strip back to the other applications (chat closed or hidden in the systray).</summary>
    public static void Undock()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        _docked = null;
        StopHookThread();
        var data = NewData();
        SHAppBarMessage(ABM_REMOVE, ref data);
        _hwnd = IntPtr.Zero;
    }

    /// <summary>Installs the hook on the windows shown or moved, on its own thread with its own message loop.</summary>
    private static void StartHookThread()
    {
        if (_hookThread is not null)
            return;
        using var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() =>
        {
            _hookThreadId = GetCurrentThreadId();
            PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE); // creates the message queue of the thread
            // Two narrow hooks: the range SHOW..LOCATIONCHANGE would also bring focus, selection and state changes.
            var shown = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero, _eventProc!, 0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            var moved = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _eventProc!, 0, 0,
                WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            ready.Set();
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
            if (shown != IntPtr.Zero)
                UnhookWinEvent(shown);
            if (moved != IntPtr.Zero)
                UnhookWinEvent(moved);
        })
        {
            IsBackground = true,
            Name = "Kaonomi AppBar hook"
        };
        _hookThread.Start();
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    /// <summary>Ends the message loop of the hook thread, which removes its hooks.</summary>
    private static void StopHookThread()
    {
        if (_hookThread is null)
            return;
        PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _hookThread = null;
        _hookThreadId = 0;
    }

    /// <summary>Reserves the strip (as wide as the window) on the right of the screen and moves the window into it.</summary>
    private static void Place()
    {
        if (_window is not { } window || window.Screens.ScreenFromWindow(window) is not { } screen)
            return;

        _monitor = MonitorFromWindow(_hwnd, MONITOR_DEFAULTTONEAREST);
        var monitor = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(_monitor, ref monitor);

        var scaling = screen.Scaling;
        var frame = window.FrameSize ?? window.ClientSize;
        var extraWidth = Math.Max(0, frame.Width - window.ClientSize.Width);
        var extraHeight = Math.Max(0, frame.Height - window.ClientSize.Height);
        var widthPx = (int)Math.Ceiling((window.Width + extraWidth) * scaling);

        // Ask for the right edge of the whole screen; Windows adjusts it to the other AppBars, then reserves it.
        var data = NewData();
        data.uEdge = ABE_RIGHT;
        data.rc = new RECT { left = monitor.rcMonitor.right - widthPx, top = monitor.rcMonitor.top, right = monitor.rcMonitor.right, bottom = monitor.rcMonitor.bottom };
        SHAppBarMessage(ABM_QUERYPOS, ref data);
        data.rc.left = data.rc.right - widthPx;
        SHAppBarMessage(ABM_SETPOS, ref data);
        _strip = data.rc;

        // The window fills the strip vertically between the taskbar and the top of the working area.
        GetMonitorInfo(_monitor, ref monitor);
        var top = monitor.rcWork.top;
        var bottom = monitor.rcWork.bottom;
        WindowGuard.Unpin();
        window.Height = Math.Max(window.MinHeight, (bottom - top) / scaling - extraHeight);
        window.Position = new PixelPoint(_strip.left, top);
        WindowGuard.Pin(window);
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        _docked = new Docked(_strip, _monitor);
    }

    private static IntPtr OnWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_hwnd == IntPtr.Zero)
            return IntPtr.Zero; // not docked (hidden in the systray)
        if (msg == WM_ACTIVATE || msg == WM_WINDOWPOSCHANGED)
        {
            var data = NewData();
            SHAppBarMessage(msg == WM_ACTIVATE ? ABM_ACTIVATE : ABM_WINDOWPOSCHANGED, ref data);
        }
        else if (msg == _callbackMessage && _callbackMessage != 0)
        {
            switch ((int)wParam)
            {
                case ABN_POSCHANGED: // taskbar or another AppBar moved: reserve the strip again
                    // Outside the window procedure: Place() moves and resizes the Avalonia window, and an exception
                    // crossing the native boundary would end the process without a trace.
                    Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            if (_hwnd != IntPtr.Zero)
                                Place();
                        }
                        catch (Exception ex)
                        {
                            ActivityLog.Error("AppBar", "Strip not placed again after ABN_POSCHANGED", ex);
                        }
                    });
                    break;
                case ABN_FULLSCREENAPP: // a full screen application: stay above it
                    SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    break;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// A window of another application was shown, moved or resized: it is put back to the left of the strip. Called on
    /// the hook thread: touches no Avalonia object, and an exception must not cross the native boundary.
    /// </summary>
    private static void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != OBJID_WINDOW || idChild != 0 || hwnd == IntPtr.Zero || _docked is not { } docked)
            return;
        try
        {
            KeepClearOfStrip(hwnd, docked.Strip, docked.Monitor);
        }
        catch (Exception ex)
        {
            ActivityLog.Error("AppBar", "Window not kept clear of the chat strip", ex);
        }
    }

    private static void KeepClearOfStrip(IntPtr hwnd, RECT strip, IntPtr dockedMonitor)
    {
        if (GetAncestor(hwnd, GA_ROOT) != hwnd || !IsWindowVisible(hwnd) || IsIconic(hwnd) || IsZoomed(hwnd))
            return; // maximized windows already follow the working area
        if (MonitorFromWindow(hwnd, MONITOR_DEFAULTTONULL) != dockedMonitor)
            return;

        var style = GetWindowLong(hwnd, GWL_STYLE);
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if ((exStyle & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) != 0)
            return; // menus, tooltips, notifications
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return;

        var className = new char[64];
        var length = GetClassName(hwnd, className, className.Length);
        var name = new string(className, 0, Math.Max(0, length));
        if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return;

        GetWindowRect(hwnd, out var outer);
        var visual = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT bounds, Marshal.SizeOf<RECT>()) == 0 ? bounds : outer;
        if (visual.right <= strip.left || visual.left >= strip.right || visual.bottom <= strip.top || visual.top >= strip.bottom)
            return;

        var monitor = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(dockedMonitor, ref monitor);
        var fullScreen = visual.left <= monitor.rcMonitor.left && visual.top <= monitor.rcMonitor.top
                         && visual.right >= monitor.rcMonitor.right && visual.bottom >= monitor.rcMonitor.bottom;
        if (!fullScreen && (style & WS_CAPTION) != WS_CAPTION)
            return; // borderless popups (splash screens, overlays) that are not full screen

        // Invisible resize borders around the visible frame (Windows 10/11).
        var borderLeft = visual.left - outer.left;
        var borderRight = outer.right - visual.right;
        var freeLeft = monitor.rcMonitor.left;
        var freeRight = strip.left;
        int left, width;
        if (fullScreen)
        {
            left = freeLeft;
            width = freeRight - freeLeft;
            SetWindowPos(hwnd, IntPtr.Zero, left, monitor.rcMonitor.top, width, monitor.rcMonitor.bottom - monitor.rcMonitor.top,
                SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
            return;
        }

        // Same size moved to the left when it fits, otherwise narrowed to the free width.
        width = Math.Min(visual.right - visual.left, freeRight - Math.Max(freeLeft, monitor.rcWork.left));
        left = Math.Min(visual.left, freeRight - width);
        SetWindowPos(hwnd, IntPtr.Zero, left - borderLeft, outer.top, width + borderLeft + borderRight, outer.bottom - outer.top,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }

    private static APPBARDATA NewData() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };

    private const int ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_ACTIVATE = 6, ABM_WINDOWPOSCHANGED = 9;
    private const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;
    private const int ABE_RIGHT = 2;
    private const uint WM_ACTIVATE = 0x0006, WM_WINDOWPOSCHANGED = 0x0047, WM_QUIT = 0x0012;
    private const uint PM_NOREMOVE = 0x0000;
    private const uint EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    private const int OBJID_WINDOW = 0;
    private const uint GA_ROOT = 2;
    private const uint MONITOR_DEFAULTTONULL = 0, MONITOR_DEFAULTTONEAREST = 2;
    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_ASYNCWINDOWPOS = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public int uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("shell32.dll")] private static extern UIntPtr SHAppBarMessage(int message, ref APPBARDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, char[] name, int max);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG message, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG message, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
