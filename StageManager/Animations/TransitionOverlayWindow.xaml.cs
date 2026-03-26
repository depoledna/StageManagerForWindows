using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using StageManager.Native.PInvoke;

namespace StageManager.Animations
{
	public partial class TransitionOverlayWindow : Window
	{
		public TransitionOverlayWindow()
		{
			InitializeComponent();
		}

		public Canvas Canvas => AnimationCanvas;

		protected override void OnSourceInitialized(EventArgs e)
		{
			base.OnSourceInitialized(e);

			// Make this window invisible to Alt-Tab and click-through
			var hwnd = new WindowInteropHelper(this).Handle;
			var exStyle = Win32.GetWindowExStyleLongPtr(hwnd);
			Win32.SetWindowStyleExLongPtr(hwnd, exStyle | Win32.WS_EX.WS_EX_TOOLWINDOW | Win32.WS_EX.WS_EX_TRANSPARENT);
		}
	}
}
