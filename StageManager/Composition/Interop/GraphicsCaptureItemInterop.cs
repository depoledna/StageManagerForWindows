using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using WinRT;

namespace StageManager.Composition.Interop
{
	/// <summary>
	/// ABI factory bridge for <see cref="GraphicsCaptureItem"/>: the WinRT
	/// projection has no public constructor, so we must reach the activation
	/// factory's <c>IGraphicsCaptureItemInterop</c> ABI to build an item from
	/// an HWND or HMONITOR.
	/// </summary>
	[ComImport]
	[Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IGraphicsCaptureItemInterop
	{
		IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
		IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
	}

	internal static class GraphicsCaptureItemFactory
	{
		// IID for Windows.Graphics.Capture.IGraphicsCaptureItem.
		// Hardcoded to avoid relying on typeof(GraphicsCaptureItem).GUID
		// (which on some CsWinRT versions returns the helper-type GUID).
		private static Guid s_captureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

		public static GraphicsCaptureItem? CreateForWindow(IntPtr hwnd)
		{
			if (hwnd == IntPtr.Zero) return null;

			try
			{
				// CsWinRT 2.x: ActivationFactory.Get returns IObjectReference
				// whose ThisPtr is the IUnknown of the factory. Marshal it
				// into an RCW so we can QI for our [ComImport] ABI interface.
				var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
				var rcw = Marshal.GetObjectForIUnknown(factory.ThisPtr);
				try
				{
					var interop = (IGraphicsCaptureItemInterop)rcw;
					var iid = s_captureItemIid;
					var ptr = interop.CreateForWindow(hwnd, ref iid);
					if (ptr == IntPtr.Zero) return null;

					try
					{
						return MarshalInspectable<GraphicsCaptureItem>.FromAbi(ptr);
					}
					finally
					{
						Marshal.Release(ptr);
					}
				}
				finally
				{
					Marshal.ReleaseComObject(rcw);
				}
			}
			catch (Exception ex)
			{
				Log.Info("WGCITEM", $"CreateForWindow failed for 0x{hwnd:X}: {ex.Message}");
				return null;
			}
		}
	}
}
