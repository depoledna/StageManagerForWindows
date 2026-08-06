using System;
using System.Runtime.InteropServices;

namespace StageManager.Native.PInvoke
{
    public delegate void WinEventDelegate(IntPtr hWinEventHook, Win32.EVENT_CONSTANTS eventType, IntPtr hwnd, Win32.OBJID idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
    public delegate bool EnumDelegate(IntPtr hWnd, int lParam);

    public static partial class Win32
    {
        public class Message
        {
            public int message { get; set; }
        }

        [DllImport("user32.dll")]
        public static extern uint GetDoubleClickTime();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SendNotifyMessage(IntPtr hWnd, uint Msg, UIntPtr wParam, IntPtr lParam);

        public static readonly int WH_MOUSE_LL = 14;
        public static readonly uint WM_SYSCOMMAND = 0x0112;

        public static readonly uint WM_LBUTTONDOWN = 0x0201;
        public static readonly uint WM_LBUTTONUP = 0x0202;

        // Ends a modal loop. DefWindowProc breaks the SC_MOVE title-bar drag loop on
        // receipt, which is how the stage→tray drag takes the window off the OS and
        // drives it itself.
        public static readonly uint WM_CANCELMODE = 0x001F;

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public static readonly UIntPtr SC_CLOSE = (UIntPtr)0xF060;

        public delegate IntPtr HookProc(int code, UIntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWindowsHookEx(int hookType, [MarshalAs(UnmanagedType.FunctionPtr)] HookProc lpfn, IntPtr hMod, int dwThreadId);

        [DllImport("user32.dll")]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx([Optional] IntPtr hhk, int nCode, UIntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern short GetKeyState(System.Windows.Forms.Keys nVirtKey);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        public static readonly uint LVM_FIRST = 0x1000;
        public static readonly uint LVM_GETSELECTEDCOUNT = LVM_FIRST + 50; // 0x1032

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);
    }
}
