using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using StageManager.Native.PInvoke;

namespace StageManager.Controls
{
	/// <summary>
	/// Base class for transparent topmost overlay windows. Sets WPF
	/// transparent-toolwindow defaults in the constructor and applies the
	/// extended-style mask in <see cref="OnSourceInitialized"/>.
	/// Subclasses override <see cref="GetExStyleFlags"/> to extend the base mask.
	/// </summary>
	public class LayeredOverlayWindowBase : Window
	{
		public LayeredOverlayWindowBase()
		{
			WindowStyle = WindowStyle.None;
			AllowsTransparency = true;
			Background = Brushes.Transparent;
			Topmost = true;
			ShowInTaskbar = false;
			ShowActivated = false;
			Focusable = false;
			ResizeMode = ResizeMode.NoResize;
		}

		/// <summary>
		/// Extended-window-style flags OR'd onto the existing exstyle in
		/// <see cref="OnSourceInitialized"/>. Default: invisible to Alt-Tab
		/// (TOOLWINDOW) + click-through (TRANSPARENT).
		/// </summary>
		protected virtual Win32.WS_EX GetExStyleFlags() =>
			Win32.WS_EX.WS_EX_TOOLWINDOW | Win32.WS_EX.WS_EX_TRANSPARENT;

		protected override void OnSourceInitialized(EventArgs e)
		{
			base.OnSourceInitialized(e);
			var hwnd = new WindowInteropHelper(this).Handle;
			var ex = Win32.GetWindowExStyleLongPtr(hwnd);
			Win32.SetWindowStyleExLongPtr(hwnd, ex | GetExStyleFlags());
		}
	}
}
