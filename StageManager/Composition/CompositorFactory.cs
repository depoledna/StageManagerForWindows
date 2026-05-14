using System;
using System.Runtime.InteropServices;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace StageManager.Composition
{
	/// <summary>
	/// Single shared <see cref="Compositor"/> for the application and helper to
	/// build a <see cref="DesktopWindowTarget"/> for a given HWND via
	/// <c>ICompositorDesktopInterop</c>.
	/// </summary>
	internal static class CompositorFactory
	{
		// Returns the IInspectable as a raw IntPtr instead of letting the runtime
		// marshal it. CsWinRT 2.x projected types (DesktopWindowTarget) cannot be
		// produced by the built-in COM marshaler — it hands back a generic
		// __ComObject and the cast to the projection type fails. We must lift
		// the ABI pointer through MarshalInspectable<T>.FromAbi instead.
		[ComImport]
		[Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
		[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		internal interface ICompositorDesktopInterop
		{
			void CreateDesktopWindowTarget(
				IntPtr hwndTarget,
				[MarshalAs(UnmanagedType.Bool)] bool isTopmost,
				out IntPtr target);
		}

		private static Compositor? _instance;

		/// <summary>
		/// Returns the process-wide <see cref="Compositor"/>, creating it on
		/// first use. Ensures a dispatcher queue exists on the current thread
		/// before the compositor is constructed.
		/// </summary>
		public static Compositor GetOrCreate()
		{
			DispatcherQueueHelper.EnsureOnCurrentThread();
			return _instance ??= new Compositor();
		}

		/// <summary>
		/// Creates a <see cref="DesktopWindowTarget"/> bound to the given HWND.
		/// The caller owns the returned target and must keep it alive (it
		/// disconnects its visual tree when GC'd).
		/// </summary>
		public static DesktopWindowTarget CreateTargetForHwnd(IntPtr hwnd, bool isTopmost)
		{
			var compositor = GetOrCreate();
			var interop = compositor.As<ICompositorDesktopInterop>();
			interop.CreateDesktopWindowTarget(hwnd, isTopmost, out var abi);
			try
			{
				return MarshalInspectable<DesktopWindowTarget>.FromAbi(abi);
			}
			finally
			{
				MarshalInspectable<DesktopWindowTarget>.DisposeAbi(abi);
			}
		}
	}
}
