using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StageManager.Animations
{
	/// <summary>
	/// Renders semi-transparent debug overlays for the sidebar and buffer drag zones.
	/// All public methods are [Conditional("DEBUG")] — zero cost in Release builds.
	/// </summary>
	internal class DebugZoneOverlay
	{
		private readonly SceneTransitionAnimator _animator;
		private List<Border>? _zones;

		public DebugZoneOverlay(SceneTransitionAnimator animator)
		{
			_animator = animator;
		}

		[Conditional("DEBUG")]
		public void Show(Rect sidebarZone, Rect bufferZone, Rect overlayBounds)
		{
			Hide();
			var overlay = _animator.GetOrCreateOverlay(overlayBounds);

			var sidebar = new Border
			{
				BorderThickness = new Thickness(2),
				BorderBrush = new SolidColorBrush(Color.FromArgb(180, 100, 180, 255)),
				Background = new SolidColorBrush(Color.FromArgb(20, 100, 180, 255)),
				IsHitTestVisible = false,
			};
			Canvas.SetLeft(sidebar, sidebarZone.X + 4);
			Canvas.SetTop(sidebar, sidebarZone.Y + 4);
			sidebar.Width = Math.Max(1, sidebarZone.Width - 8);
			sidebar.Height = Math.Max(1, sidebarZone.Height - 8);

			var buffer = new Border
			{
				BorderThickness = new Thickness(2),
				BorderBrush = new SolidColorBrush(Color.FromArgb(180, 255, 200, 50)),
				Background = new SolidColorBrush(Color.FromArgb(20, 255, 200, 50)),
				IsHitTestVisible = false,
			};
			Canvas.SetLeft(buffer, bufferZone.X + 4);
			Canvas.SetTop(buffer, bufferZone.Y + 4);
			buffer.Width = Math.Max(1, bufferZone.Width - 8);
			buffer.Height = Math.Max(1, bufferZone.Height - 8);

			_zones = new List<Border> { sidebar, buffer };
			overlay.Canvas.Children.Add(sidebar);
			overlay.Canvas.Children.Add(buffer);
			overlay.Show();
		}

		[Conditional("DEBUG")]
		public void Hide()
		{
			if (_zones != null && _animator.Overlay != null)
			{
				foreach (var z in _zones)
					_animator.Overlay.Canvas.Children.Remove(z);
				_zones = null;
				if (_animator.Overlay.Canvas.Children.Count == 0)
					_animator.Overlay.Hide();
			}
		}
	}
}
