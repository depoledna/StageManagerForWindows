using System.Runtime.InteropServices;

namespace StageProbe;

internal static class Native
{
    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int dwProcessId);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForSystem();

    public const int SW_MINIMIZE = 6;
    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    /// <summary>
    /// Every visible top-level window of a process. Process.MainWindowHandle
    /// only ever yields one, and the multi-window probe needs both of its
    /// windows addressable to minimize just one of them.
    /// </summary>
    public static List<IntPtr> TopLevelWindows(int processId)
    {
        var found = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)processId && IsWindowVisible(hwnd))
                found.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Bring a window to the foreground from a background process. The
    /// synthetic ALT tap satisfies the foreground-lock heuristic; falls back to
    /// SwitchToThisWindow (the Alt-Tab path) when SetForegroundWindow is refused.
    /// </summary>
    public static bool Activate(IntPtr hwnd, int attempts = 4)
    {
        for (int i = 0; i < attempts; i++)
        {
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            SetForegroundWindow(hwnd);
            Thread.Sleep(250);
            if (GetForegroundWindow() == hwnd) return true;
            SwitchToThisWindow(hwnd, true);
            Thread.Sleep(250);
            if (GetForegroundWindow() == hwnd) return true;
        }
        return false;
    }
}
