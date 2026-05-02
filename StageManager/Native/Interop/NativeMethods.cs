using System;
using System.Runtime.InteropServices;
using System.Windows;
using StageManager.Native.PInvoke;

namespace StageManager.Native.Interop
{
    public static class NativeMethods
    {
        [DllImport("dwmapi.dll", SetLastError = true)]
        public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);

        [DllImport("dwmapi.dll")]
        public static extern int DwmUnregisterThumbnail(IntPtr thumb);

        [DllImport("dwmapi.dll", PreserveSig = false)]
        public static extern void DwmQueryThumbnailSourceSize(IntPtr hThumbnail, out Size size);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmUpdateThumbnailProperties(IntPtr hThumbnail, ref DWM_THUMBNAIL_PROPERTIES props);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        public const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public Win32.Rect rcMonitor;
            public Win32.Rect rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        public enum MonitorOptions : uint
        {
            MONITOR_DEFAULTTONULL = 0,
            MONITOR_DEFAULTTOPRIMARY = 1,
            MONITOR_DEFAULTTONEAREST = 2
        }
    }
}
