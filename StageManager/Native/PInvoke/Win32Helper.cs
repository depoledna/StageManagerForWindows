using System;
using System.Runtime.InteropServices;

namespace StageManager.Native.PInvoke
{
    public static class Win32Helper
    {

        public static void QuitApplication(IntPtr hwnd)
        {
            Win32.SendNotifyMessage(hwnd, Win32.WM_SYSCOMMAND, Win32.SC_CLOSE, IntPtr.Zero);
        }

        public static bool IsCloaked(IntPtr hwnd)
        {
            bool isCloaked;
            var attr = Win32.DwmGetWindowAttribute(hwnd, (int)Win32.DwmWindowAttribute.DWMWA_CLOAKED, out isCloaked, Marshal.SizeOf(typeof(bool)));
            return isCloaked;
        }

        public static bool IsAppWindow(IntPtr hwnd)
        {
            // A minimized window keeps WS_VISIBLE — only ShowWindow(SW_HIDE) clears it — so an
            // "|| IsIconic" here never admitted an ordinary minimized window that the visibility
            // test had not already admitted. What it did admit is windows an app deliberately
            // hid: closing to the notification area leaves the window minimized AND hidden, and
            // that combination arrived as a scene with a blank tray tile for an app the user had
            // quit. WindowsManager.Start also un-minimizes whatever passes this test, so it
            // brought the hidden window back on top of tiling it.
            return Win32.IsWindowVisible(hwnd) &&
                   !Win32.GetWindowExStyleLongPtr(hwnd).HasFlag(Win32.WS_EX.WS_EX_NOACTIVATE) &&
                   !Win32.GetWindowStyleLongPtr(hwnd).HasFlag(Win32.WS.WS_CHILD);
        }

        public static bool IsAltTabWindow(IntPtr hWnd)
        {
            var exStyle = Win32.GetWindowExStyleLongPtr(hWnd);
            if (exStyle.HasFlag(Win32.WS_EX.WS_EX_TOOLWINDOW) ||
                Win32.GetWindow(hWnd, Win32.GW.GW_OWNER) != IntPtr.Zero)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Ensures WS_EX_LAYERED is set on the window, then applies the given alpha.
        /// </summary>
        public static void SetAlpha(IntPtr hWnd, byte alpha)
        {
            var exStyle = Win32.GetWindowExStyleLongPtr(hWnd);
            if (!exStyle.HasFlag(Win32.WS_EX.WS_EX_LAYERED))
                Win32.SetWindowStyleExLongPtr(hWnd, exStyle | Win32.WS_EX.WS_EX_LAYERED);
            Win32.SetLayeredWindowAttributes(hWnd, 0, alpha, Win32.LWA_ALPHA);
        }

        /// <summary>
        /// Removes WS_EX_LAYERED again. Must be called wherever a window is handed back to
        /// the user, because <see cref="SetAlpha"/> only ever adds the style and it outlives
        /// this process: Chromium renders through DirectComposition and a window forced
        /// layered from outside falls back to a redirection surface it never paints into, so
        /// a Chrome window left with the style set comes up blank — and stays blank after
        /// Stage Manager exits.
        /// </summary>
        public static void ClearLayered(IntPtr hWnd)
        {
            var exStyle = Win32.GetWindowExStyleLongPtr(hWnd);
            if (!exStyle.HasFlag(Win32.WS_EX.WS_EX_LAYERED))
                return;

            Win32.SetWindowStyleExLongPtr(hWnd, exStyle & ~Win32.WS_EX.WS_EX_LAYERED);

            // The style change only reaches the frame on the next SetWindowPos with
            // FrameChanged; without it the window keeps rendering through the stale path.
            Win32.SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
                Win32.SetWindowPosFlags.FrameChanged |
                Win32.SetWindowPosFlags.IgnoreMove |
                Win32.SetWindowPosFlags.IgnoreResize |
                Win32.SetWindowPosFlags.IgnoreZOrder |
                Win32.SetWindowPosFlags.DoNotActivate);
        }

        public static void ForceForegroundWindow(IntPtr hWnd)
        {
            FocusStealer.Steal(hWnd);
        }
    }
}
