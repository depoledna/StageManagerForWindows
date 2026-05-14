using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using StageManager.Native.PInvoke;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace StageManager.Composition
{
	/// <summary>
	/// <see cref="HwndHost"/> that creates a child HWND and attaches a
	/// <see cref="DesktopWindowTarget"/> to it so a Composition visual tree
	/// can be rendered inside a WPF panel. Consumers assign their root visual
	/// to <see cref="Root"/>; the setter wires it onto the underlying
	/// <c>DesktopWindowTarget.Root</c>.
	/// </summary>
	internal sealed class CompositionHost : HwndHost
	{
		private DesktopWindowTarget? _target;
		private Visual? _pendingRoot;
		private bool _destroyed;

		/// <summary>
		/// Shared application compositor. Same instance for every host.
		/// </summary>
		public Compositor Compositor => CompositorFactory.GetOrCreate();

		/// <summary>
		/// Root visual mounted on the underlying <see cref="DesktopWindowTarget"/>.
		/// May be set before <c>BuildWindowCore</c> runs; in that case the
		/// assignment is deferred until the target is created.
		/// </summary>
		public Visual? Root
		{
			get => _target?.Root ?? _pendingRoot;
			set
			{
				if (_destroyed) return;
				if (_target is not null)
					_target.Root = value;
				else
					_pendingRoot = value;
			}
		}

		protected override HandleRef BuildWindowCore(HandleRef hwndParent)
		{
			// STATIC class without SS_NOTIFY returns HTTRANSPARENT, so OS mouse
			// routing falls through to the WPF parent. WS_EX_NOREDIRECTIONBITMAP
			// keeps DWM from allocating a redirection bitmap that conflicts with
			// the AllowsTransparency=True parent.
			var hwnd = Native.CreateWindowExW(
				dwExStyle: (int)Win32.WS_EX.WS_EX_NOREDIRECTIONBITMAP,
				lpClassName: "STATIC",
				lpWindowName: string.Empty,
				dwStyle: (int)(Win32.WS.WS_CHILD | Win32.WS.WS_VISIBLE),
				X: 0,
				Y: 0,
				nWidth: 0,
				nHeight: 0,
				hWndParent: hwndParent.Handle,
				hMenu: IntPtr.Zero,
				hInstance: IntPtr.Zero,
				lpParam: IntPtr.Zero);

			if (hwnd == IntPtr.Zero)
			{
				var err = Marshal.GetLastWin32Error();
				Log.Fatal("COMPHOST", $"CreateWindowExW failed err={err}");
				throw new InvalidOperationException($"CreateWindowExW failed (Win32 {err})");
			}

			_target = CompositorFactory.CreateTargetForHwnd(hwnd, isTopmost: false);

			if (_pendingRoot is not null)
			{
				_target.Root = _pendingRoot;
				_pendingRoot = null;
			}

			Log.Info("COMPHOST", "Created composition child", hwnd);
			return new HandleRef(this, hwnd);
		}

		protected override void DestroyWindowCore(HandleRef hwnd)
		{
			_destroyed = true;
			_pendingRoot = null;

			try
			{
				_target?.Dispose();
			}
			catch (Exception ex)
			{
				Log.Info("COMPHOST", $"Target dispose threw: {ex.Message}");
			}
			finally
			{
				_target = null;
			}

			if (hwnd.Handle != IntPtr.Zero)
				Native.DestroyWindow(hwnd.Handle);
		}

		// Local P/Invokes — only used here by HwndHost child-window construction.
		private static class Native
		{
			[DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
			public static extern IntPtr CreateWindowExW(
				int dwExStyle,
				string lpClassName,
				string lpWindowName,
				int dwStyle,
				int X,
				int Y,
				int nWidth,
				int nHeight,
				IntPtr hWndParent,
				IntPtr hMenu,
				IntPtr hInstance,
				IntPtr lpParam);

			[DllImport("user32.dll", SetLastError = true)]
			[return: MarshalAs(UnmanagedType.Bool)]
			public static extern bool DestroyWindow(IntPtr hWnd);
		}
	}
}
