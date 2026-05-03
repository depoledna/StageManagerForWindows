using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using StageManager.Helpers;
using StageManager.Model;

namespace StageManager.Animations
{
	/// <summary>
	/// Manages the drag ghost for WPF sidebar drag (Flow 2: sidebar → active screen).
	/// Borrows the overlay from SceneTransitionAnimator to render a placeholder
	/// that follows the cursor during drag.
	/// </summary>
	internal class SidebarDragGhost
	{
		private readonly SceneTransitionAnimator _animator;
		private Border _ghost;
		private bool _isActive;

		public bool IsActive => _isActive;

		public SidebarDragGhost(SceneTransitionAnimator animator)
		{
			_animator = animator;
		}

		public void Show(Rect overlayBounds, Rect ghostRect, SceneModel scene)
		{
			if (_animator.IsAnimating) return;
			_isActive = true;

			try
			{
				var overlay = _animator.GetOrCreateOverlay(overlayBounds);

				var icon = scene?.Windows.FirstOrDefault()?.Icon;
				_ghost = PlaceholderFactory.Create(icon);
				var ghostCanvas = ghostRect.ToCanvas(overlay);
				Canvas.SetLeft(_ghost, ghostCanvas.X);
				Canvas.SetTop(_ghost, ghostCanvas.Y);
				_ghost.Width = ghostCanvas.Width;
				_ghost.Height = ghostCanvas.Height;

				overlay.Canvas.Children.Add(_ghost);
				overlay.Show();
				Log.Info("DRAG", $"Ghost shown at ({ghostRect.X:F0},{ghostRect.Y:F0} {ghostRect.Width:F0}x{ghostRect.Height:F0}) overlay=({overlayBounds.X:F0},{overlayBounds.Y:F0} {overlayBounds.Width:F0}x{overlayBounds.Height:F0})");
			}
			catch (Exception ex)
			{
				Log.Info("DRAG", $"ShowDragGhost failed: {ex.Message}");
				Hide();
			}
		}

		public void UpdatePositionAndSize(double screenX, double screenY, double width, double height)
		{
			if (_ghost == null) return;
			var overlay = _animator.Overlay;
			if (overlay == null) return;
			var canvasPoint = new Point(screenX, screenY).ToCanvas(overlay);
			Canvas.SetLeft(_ghost, canvasPoint.X);
			Canvas.SetTop(_ghost, canvasPoint.Y);
			_ghost.Width = Math.Max(1, width);
			_ghost.Height = Math.Max(1, height);
		}

		public void SetVisible(bool visible)
		{
			if (_ghost == null) return;
			_ghost.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
		}

		public void Hide()
		{
			if (_ghost != null)
			{
				_animator.Overlay?.Canvas.Children.Remove(_ghost);
				_ghost = null;
			}
			if (_animator.Overlay != null && _animator.Overlay.Canvas.Children.Count == 0)
				_animator.Overlay.Hide();
			_isActive = false;
			Log.Info("DRAG", "Ghost hidden");
		}
	}
}
