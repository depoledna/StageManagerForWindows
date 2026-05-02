using System.Runtime.InteropServices;
using StageManager.Native.PInvoke;

// ReSharper disable FieldCanBeMadeReadOnly.Global
// ReSharper disable  InconsistentNaming
// ReSharper disable MemberCanBePrivate.Global

namespace StageManager.Native.Interop
{
    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public Win32.Rect rcDestination;
        public Win32.Rect rcSource;
        public byte opacity;

        [MarshalAs(UnmanagedType.Bool, SizeConst = 4)]
        public bool fVisible;

        public bool fSourceClientAreaOnly;
    }
}
