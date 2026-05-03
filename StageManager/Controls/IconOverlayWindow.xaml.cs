using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using StageManager.Native.PInvoke;

namespace StageManager.Controls
{
	public partial class IconOverlayWindow : LayeredOverlayWindowBase
	{
		private IntPtr _hwnd;
		private bool _clickThrough = true;

		public IconOverlayWindow()
		{
			InitializeComponent();
			CompositionTarget.Rendering += OnRenderTick;
			Closed += (_, _) => CompositionTarget.Rendering -= OnRenderTick;
		}

		public Canvas Canvas => IconCanvas;

		protected override Win32.WS_EX GetExStyleFlags() =>
			base.GetExStyleFlags() | Win32.WS_EX.WS_EX_NOACTIVATE;

		protected override void OnSourceInitialized(EventArgs e)
		{
			base.OnSourceInitialized(e);
			_hwnd = new WindowInteropHelper(this).Handle;
		}

		// Per-frame: pass clicks through unless cursor is over an icon, then enable WPF input
		// so the icon's MouseEnter/MouseLeave/MouseLeftButtonUp can fire.
		private void OnRenderTick(object? sender, EventArgs e)
		{
			if (_hwnd == IntPtr.Zero || !IsVisible || IconCanvas.Children.Count == 0)
			{
				SetClickThrough(true);
				return;
			}

			if (!Win32.GetCursorPos(out var pt))
				return;

			Point local;
			try { local = PointFromScreen(new Point(pt.X, pt.Y)); }
			catch { return; }

			var hit = IconCanvas.InputHitTest(local) as DependencyObject;
			var overIcon = false;
			while (hit != null)
			{
				if (hit is Image) { overIcon = true; break; }
				hit = VisualTreeHelper.GetParent(hit);
			}

			SetClickThrough(!overIcon);
		}

		private void SetClickThrough(bool value)
		{
			if (_clickThrough == value || _hwnd == IntPtr.Zero) return;
			_clickThrough = value;

			var exStyle = Win32.GetWindowExStyleLongPtr(_hwnd);
			exStyle = value
				? exStyle | Win32.WS_EX.WS_EX_TRANSPARENT
				: exStyle & ~Win32.WS_EX.WS_EX_TRANSPARENT;
			Win32.SetWindowStyleExLongPtr(_hwnd, exStyle);
		}
	}
}
